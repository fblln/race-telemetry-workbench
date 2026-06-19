# Race Telemetry Workbench: Time-, Distance-, and Track-Aligned Telemetry

## Objective

Extend Race Telemetry Workbench so that telemetry is represented explicitly in different analytical domains:

1. **Raw source telemetry** for fidelity and debugging.
2. **Time-aligned telemetry** for replay and cross-driver event analysis.
3. **Distance-aligned telemetry** for lap comparison and performance analysis.
4. **Track-aligned telemetry** for canonical circuit position and corner-based analysis.

The main architectural rule is:

> There is no single universally correct aligned telemetry representation. Alignment must match the question being asked.

The existing `aligned_telemetry_10hz` model remains useful, but it must be treated as a replay-oriented projection rather than raw sensor truth.

---

## Problem Statement

Race Telemetry Workbench currently stores raw telemetry and position samples and generates a 10 Hz aligned telemetry table.

This works well for:

- synchronized replay;
- session-time charts;
- multi-driver event windows;
- UI playback;
- fixed-rate querying.

However, time alignment is not sufficient for lap-performance comparisons.

At the same elapsed time, two drivers are usually at different points on the circuit. Questions such as the following require distance alignment instead:

- Where did one driver gain time?
- Who was faster at a specific corner?
- Who braked later at the same location?
- What was the speed difference at 3,000 metres into the lap?
- How did the cumulative lap-time delta evolve?

Race-event questions require session-time alignment instead:

- Who braked first?
- Which driver reacted to another?
- Where were the cars at a specific session timestamp?
- What happened around a race-control event?

The implementation must preserve these distinct semantics.

---

## Core Design Principles

### 1. Preserve raw data

Raw source samples must remain available and must not be replaced by derived projections.

Raw tables are the source of truth for:

- original timestamps;
- missing data;
- source frequency;
- source gaps;
- validation;
- rebuilding derived tables.

### 2. Treat aligned tables as derived read models

`aligned_telemetry_10hz` is a synthetic read model.

A row may combine:

- an observed car-data sample;
- an interpolated position sample;
- values originating from different timestamps.

It must not be represented as if all values were observed atomically.

### 3. Separate temporal and spatial analysis

Use:

- **session time** for replay and causal ordering;
- **lap elapsed time** for within-lap timing;
- **distance** for comparing laps at the same circuit location;
- **canonical track progress** for circuit-normalized analysis.

### 4. Never derive official lap time from telemetry boundaries

Official lap duration must come from the timing data.

Do not calculate official lap duration as:

```text
last telemetry timestamp - first telemetry timestamp
```

Telemetry coverage may differ because of:

- missing samples;
- source frequencies;
- interpolation;
- boundary slicing;
- merged data streams.

---

## Existing Data Model

The current architecture includes approximately:

```text
telemetry_samples
position_samples
aligned_telemetry_10hz
laps
sessions
drivers
stints
tyres
weather
race_control_messages
```

The implementation must retain these existing concepts.

---

## Target Architecture

```text
Raw layer
├── telemetry_samples
├── position_samples
└── laps

Derived time domain
└── aligned_telemetry_10hz
    ├── replay
    ├── cross-driver synchronization
    └── race-event analysis

Derived distance domain
└── lap_telemetry_by_distance
    ├── lap comparison
    ├── braking analysis
    ├── corner analysis
    └── cumulative delta analysis

Derived track domain
└── track_progress_samples
    ├── canonical circuit position
    ├── normalized circuit maps
    └── corner and sector segmentation
```

---

# Implementation Requirements

## 1. Canonical Time Fields

The system must expose explicit time semantics.

### Required concepts

```text
session_time
lap_elapsed_time
official_lap_time
sample_source_time
```

### Definitions

#### `session_time`

Time relative to the start of the session.

Use for:

- replay;
- cross-driver alignment;
- race-event windows;
- ordering events between drivers.

#### `lap_elapsed_time`

Time relative to the official start boundary of the lap.

Use for:

- within-lap analysis;
- distance-to-time interpolation;
- cumulative lap delta.

#### `official_lap_time`

Lap duration reported by timing data.

This is authoritative.

#### `sample_source_time`

Original timestamp associated with the source sample.

Use for:

- provenance;
- interpolation-age calculation;
- debugging;
- data-quality validation.

---

## 2. Enhance `aligned_telemetry_10hz`

Keep the table, but make its derived nature explicit.

### Add provenance fields

Recommended fields:

```text
car_data_source_time
position_source_time
car_data_age_ms
position_age_ms
car_data_interpolated
position_interpolated
```

Optional additional fields:

```text
car_data_previous_source_time
car_data_next_source_time
position_previous_source_time
position_next_source_time
alignment_method
```

### Semantics

For every aligned row:

- `car_data_source_time` identifies the nearest or selected source time used for car data.
- `position_source_time` identifies the nearest or selected source time used for position.
- `car_data_age_ms` is the absolute difference between aligned timestamp and car-data source timestamp.
- `position_age_ms` is the absolute difference between aligned timestamp and position source timestamp.
- interpolation flags indicate whether the value was synthesized.

### Constraints

- The aligned timestamp must be monotonic.
- Source timestamps must not be later than or earlier than the interpolation bounds in an invalid way.
- Interpolation outside an accepted maximum gap must be rejected or marked invalid.
- The alignment algorithm must not silently interpolate across large missing-data gaps.

### Configuration

Add configurable thresholds:

```text
MaxCarDataInterpolationGapMs
MaxPositionInterpolationGapMs
MaxSourceAgeMs
```

Rows exceeding thresholds should either:

1. contain null values for the affected channels; or
2. be marked with a quality status.

Recommended quality field:

```text
quality_status
```

Possible values:

```text
Observed
Interpolated
Stale
Missing
Invalid
```

---

## 3. Add `lap_telemetry_by_distance`

Create a distance-aligned projection for lap comparison.

### Purpose

Provide telemetry values at common circuit-distance points for each lap.

### Suggested primary key

```text
session_id
driver_number
lap_number
distance_m
```

### Suggested columns

```text
session_id
session_key
driver_number
driver_code
lap_number
distance_m
normalized_track_progress
lap_elapsed_time_ms
session_time
speed_kph
throttle
brake
gear
rpm
drs
x
y
z
source_sample_before_time
source_sample_after_time
interpolated
quality_status
created_at
```

### Resolution

Use configurable distance resolution.

Recommended default:

```text
5 metres
```

Allow alternatives:

```text
1 metre
10 metres
25 metres
```

Configuration example:

```text
DistanceAlignmentStepMeters = 5
```

### Generation algorithm

For every valid lap:

1. Load raw car telemetry samples belonging to the lap.
2. Sort samples by source timestamp.
3. Calculate `lap_elapsed_time`.
4. Calculate cumulative distance from speed and elapsed time, or use an existing FastF1-derived distance if already imported.
5. Ensure cumulative distance is monotonic.
6. Generate a regular distance grid.
7. Interpolate telemetry values onto the distance grid.
8. Interpolate lap elapsed time as a function of distance.
9. Store the result in `lap_telemetry_by_distance`.
10. Attach quality and provenance information.

### Distance calculation

When calculating distance from speed:

```text
distance_increment_m =
    speed_kph / 3.6 * elapsed_seconds
```

Use trapezoidal integration where possible:

```text
distance_increment_m =
    ((previous_speed_kph + current_speed_kph) / 2) / 3.6
    * elapsed_seconds
```

### Important constraint

Integrated lap distance is an analytical coordinate, not a surveyed physical circuit coordinate.

Name fields accordingly:

```text
integrated_lap_distance_m
```

Do not imply that this is authoritative geographic track distance.

---

## 4. Cumulative Lap Delta

Implement cumulative lap delta using elapsed time at common distance points.

### Preferred calculation

For drivers A and B:

```text
delta_ms(distance) =
    elapsed_time_ms_A(distance)
    - elapsed_time_ms_B(distance)
```

This is preferable to reintegrating inverse speed during every query.

### Expected invariant

At the end of two complete valid laps:

```text
delta_at_finish
≈ official_lap_time_A - official_lap_time_B
```

The difference must remain within a configurable tolerance.

Example:

```text
LapDeltaValidationToleranceMs = 100
```

A mismatch beyond tolerance must produce a validation warning.

### Sign convention

Document and apply one convention consistently.

Recommended:

```text
positive delta = driver A is slower than driver B
negative delta = driver A is faster than driver B
```

---

## 5. Track-Normalized Projection

Add a future-compatible track-domain projection.

### Required distinction

Maintain both:

```text
integrated_lap_distance_m
normalized_track_progress
```

### `normalized_track_progress`

Range:

```text
0.0 to 1.0
```

Initial implementation may calculate:

```text
normalized_track_progress =
    integrated_lap_distance_m / final_integrated_lap_distance_m
```

This permits comparisons when two laps have slightly different integrated lengths.

### Future canonical centreline support

Design the schema so that track progress can later be projected onto a canonical circuit centreline.

Potential future fields:

```text
canonical_track_distance_m
canonical_track_progress
lateral_offset_m
nearest_track_segment_id
corner_number
marshal_sector
mini_sector
```

Do not block the current implementation on geographic centreline calibration.

---

## 6. API and MCP Tool Separation

Avoid a generic telemetry endpoint that changes semantics silently.

Create or maintain explicit operations.

### Replay telemetry

```text
GetReplayTelemetry
```

Purpose:

- return session-time-aligned telemetry;
- use `aligned_telemetry_10hz`;
- support one or more drivers;
- support time windows and replay rates.

Suggested parameters:

```text
session
drivers
start_session_time
end_session_time
frequency_hz
```

### Raw telemetry

```text
GetRawTelemetry
```

Purpose:

- return source samples;
- preserve original timestamps;
- allow debugging and high-fidelity inspection.

Suggested parameters:

```text
session
driver
lap
start_time
end_time
channels
```

### Distance-based lap comparison

```text
CompareLapsByDistance
```

Purpose:

- compare two or more laps at common distance points;
- return speed, throttle, brake, gear and cumulative delta.

Suggested parameters:

```text
session
reference_driver
reference_lap
comparison_driver
comparison_lap
distance_step_m
start_distance_m
end_distance_m
```

Suggested response:

```json
{
  "referenceLap": {},
  "comparisonLap": {},
  "distanceStepMeters": 5,
  "deltaSignConvention": "positive means reference lap is slower",
  "samples": [
    {
      "distanceMeters": 0,
      "referenceElapsedMs": 0,
      "comparisonElapsedMs": 0,
      "deltaMs": 0,
      "referenceSpeedKph": 0,
      "comparisonSpeedKph": 0
    }
  ]
}
```

### Session event window

```text
GetSessionEventWindow
```

Purpose:

- inspect multiple drivers around a common session timestamp;
- support incident and reaction analysis.

Suggested parameters:

```text
session
drivers
event_session_time
window_before_ms
window_after_ms
```

---

## 7. Data-Quality Metrics

Calculate and persist per-lap quality metrics.

### Required metrics

```text
official_lap_duration_ms
telemetry_covered_duration_ms
first_sample_offset_ms
last_sample_offset_ms
maximum_car_data_gap_ms
maximum_position_gap_ms
final_integrated_distance_m
interpolated_car_data_percentage
interpolated_position_percentage
stale_sample_percentage
distance_delta_validation_ms
```

### Optional table

```text
lap_telemetry_quality
```

Suggested key:

```text
session_id
driver_number
lap_number
```

Suggested columns:

```text
session_id
driver_number
lap_number
official_lap_duration_ms
telemetry_covered_duration_ms
first_sample_offset_ms
last_sample_offset_ms
maximum_car_data_gap_ms
maximum_position_gap_ms
final_integrated_distance_m
interpolated_car_data_percentage
interpolated_position_percentage
stale_sample_percentage
quality_status
quality_messages
created_at
```

---

## 8. Validation Rules

Implement automated validation during ingestion or projection generation.

### Timestamp validation

```text
source timestamps are monotonic
aligned timestamps are monotonic
lap elapsed time is non-decreasing
```

### Distance validation

```text
integrated distance is non-decreasing
normalized track progress is between 0 and 1
final distance is greater than zero
```

### Lap boundary validation

```text
first sample offset is within configured tolerance
last sample offset is within configured tolerance
telemetry coverage does not define official lap duration
```

### Delta validation

For two complete laps:

```text
abs(
    delta_at_finish
    - (official_lap_time_A - official_lap_time_B)
) <= configured tolerance
```

### Gap validation

Do not interpolate across gaps larger than configured thresholds.

### Suggested statuses

```text
Valid
ValidWithWarnings
Incomplete
Invalid
```

---

## 9. Database and TimescaleDB Considerations

### Raw hypertables

Keep raw telemetry tables optimized for time-based access.

Recommended partition dimension:

```text
sample_time_utc
```

Recommended segment keys:

```text
session_id
driver_code
```

### Distance-aligned table

`lap_telemetry_by_distance` is not primarily time-series data.

A normal PostgreSQL table may be more appropriate than a hypertable unless query volume proves otherwise.

Recommended index:

```sql
CREATE UNIQUE INDEX
ON lap_telemetry_by_distance (
    session_id,
    driver_number,
    lap_number,
    distance_m
);
```

Additional index:

```sql
CREATE INDEX
ON lap_telemetry_by_distance (
    session_id,
    driver_number,
    lap_number
);
```

### Compression

Distance-aligned data is highly compressible because:

- rows are ordered by distance;
- values change smoothly;
- session and driver identifiers repeat;
- adjacent telemetry values are correlated.

If Timescale compression is used, consider:

```text
segment by:
    session_id
    driver_number
    lap_number

order by:
    distance_m
```

Only use a hypertable if needed for Timescale compression or retention features.

---

## 10. Migration Strategy

### Phase 1: Semantics and provenance

1. Document time-field semantics.
2. Add interpolation flags and source timestamps to `aligned_telemetry_10hz`.
3. Add quality thresholds.
4. Add ingestion validation metrics.

### Phase 2: Distance-aligned projection

1. Add `lap_telemetry_by_distance`.
2. Implement cumulative distance calculation.
3. Implement interpolation onto a configurable distance grid.
4. Implement lap elapsed time by distance.
5. Add cumulative lap delta query.

### Phase 3: APIs and MCP tools

1. Add `CompareLapsByDistance`.
2. Add `GetSessionEventWindow`.
3. Clarify `GetReplayTelemetry`.
4. Expose quality metadata.

### Phase 4: Canonical track projection

1. Add normalized track progress.
2. Add centreline projection support.
3. Add corner and sector segmentation.
4. Add track-relative coordinates.

---

## 11. Acceptance Criteria

### Time-aligned telemetry

- Existing replay functionality continues to work.
- Aligned rows expose interpolation and provenance metadata.
- Interpolation does not cross configured maximum gaps.
- Multi-driver replay remains synchronized by session time.

### Distance-aligned telemetry

- A valid lap produces rows at fixed distance intervals.
- Distance values are monotonic.
- Lap elapsed time values are monotonic.
- Final cumulative delta approximately equals official lap-time difference.
- Querying two laps returns values at identical distance points.

### Data quality

- Every imported valid lap has a quality record.
- Missing or stale samples produce warnings.
- Invalid lap boundaries do not silently create apparently valid projections.
- Quality metrics are queryable through the API.

### API semantics

- Replay endpoints use session time.
- Lap comparison endpoints use distance.
- Raw endpoints preserve source samples.
- Endpoint names and documentation make alignment semantics explicit.

---

## 12. Tests

### Unit tests

Add tests for:

- speed integration into cumulative distance;
- trapezoidal distance calculation;
- interpolation at exact sample points;
- interpolation between sample points;
- rejection of interpolation across excessive gaps;
- normalized progress calculation;
- lap delta sign convention;
- official-lap-time validation;
- monotonicity checks.

### Integration tests

Add tests that:

1. import one known session;
2. create time-aligned telemetry;
3. create distance-aligned telemetry;
4. compare two known laps;
5. validate final delta against official lap-time difference;
6. query event windows across two drivers;
7. verify quality warnings for incomplete laps.

### Edge cases

Test:

- incomplete laps;
- pit-in laps;
- pit-out laps;
- safety-car laps;
- red-flag interruptions;
- missing position samples;
- duplicated timestamps;
- non-monotonic timestamps;
- zero or invalid speed;
- telemetry gaps;
- laps with different integrated total distances.

---

## 13. Non-Goals

The first implementation does not need to:

- reconstruct exact FIA timing internals;
- derive geographic latitude and longitude;
- guarantee centimetre-level track coordinates;
- replace raw FastF1 source data;
- infer official lap time from telemetry;
- calibrate FastF1 X/Y coordinates to OpenStreetMap automatically;
- support real-time ingestion.

---

## 14. Codex Implementation Instructions

Before changing code:

1. Inspect the current schema and entity names.
2. Locate ingestion code for car telemetry and position data.
3. Locate generation logic for `aligned_telemetry_10hz`.
4. Locate existing lap comparison API and MCP tools.
5. Reuse existing naming, dependency injection, migrations and test conventions.
6. Do not introduce a second framework or unnecessary infrastructure.
7. Prefer additive changes and backward compatibility.

Implementation order:

1. Add schema migrations.
2. Add domain models.
3. Add provenance to time alignment.
4. Add quality calculations.
5. Add distance-alignment service.
6. Add repository queries.
7. Add API endpoints.
8. Add MCP tools.
9. Add tests.
10. Update README and architecture documentation.

When implementation choices are ambiguous:

- preserve raw data;
- make derived semantics explicit;
- prefer deterministic rebuildable projections;
- expose quality rather than hiding interpolation;
- do not silently change existing replay behavior.

---

## Final Architectural Rule

> Time alignment answers when something happened. Distance alignment answers where performance was gained or lost. Raw telemetry explains what was actually observed.

Race Telemetry Workbench should support all three explicitly.
