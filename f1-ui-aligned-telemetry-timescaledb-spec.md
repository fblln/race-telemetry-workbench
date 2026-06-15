# UI-Aligned F1 Telemetry Materialization Specification

## 1. Goal

Build an ingestion-time telemetry materialization pipeline that combines F1 car telemetry and GPS/location data into one timestamp-aligned stream optimized for desktop UI replay and visualization.

The frontend must be able to query a single ordered stream per session, driver, lap, or time window without doing any of the following:

- joining car telemetry with GPS/location data;
- aligning timestamps;
- interpolating between different source streams;
- resampling raw data;
- compensating for source timestamp mismatch.

The UI may still interpolate between adjacent already-aligned samples for visual smoothing at 60 FPS.

## 2. Product Context

The application is a desktop F1 telemetry/replay UI backed by TimescaleDB.

The user wants to support playback speeds:

- 0.5x
- 1x
- 5x

The target display refresh rate is 60 Hz.

The best tradeoff is:

```text
Raw data frequency          = whatever the source actually provides
Materialized UI frequency   = 10 Hz
UI render frequency         = 60 FPS
```

At 10 Hz materialization:

```text
0.5x replay ->  5 telemetry samples/sec
1x replay   -> 10 telemetry samples/sec
5x replay   -> 50 telemetry samples/sec
```

This is a good match for a 60 Hz screen. At 5x, the UI gets close to one fresh telemetry sample per frame. At 1x and 0.5x, the UI can visually interpolate between aligned samples.

## 3. Important Design Decision

Use two storage layers:

```text
raw telemetry hypertables       = source of truth
aligned telemetry hypertable    = product/UI materialization
```

Do not choose between raw and aligned data.

Use both.

Raw data is kept for correctness, debugging, future analysis, and reprocessing.

Aligned data is used by the UI because it is fast, simple, and snappy.

## 4. Non-Goals

This pipeline must not reproduce FastF1 `get_telemetry()` fully.

This pipeline must not compute advanced channels unless explicitly enabled later.

The first implementation must not compute:

- driver ahead;
- distance to driver ahead;
- integrated distance;
- relative distance;
- lap delta;
- tyre analysis;
- pit stop loss;
- racing line reconstruction.

This materialization is for UI playback and visualization, not high-precision race engineering analysis.

## 5. Source Data Assumptions

The data comes from F1 telemetry sources such as SECU-derived public feeds exposed through OpenF1/FastF1-style APIs.

Even if the source has a known nominal frequency, the implementation must not hardcode that the timestamps are perfectly aligned.

The implementation must validate the actual source timestamps for each:

```text
session
driver
stream
```

Reason:

- car telemetry and GPS/location may have different timestamps;
- nominal frequency does not guarantee identical sample times;
- public/processed feeds may be irregular;
- missing samples and gaps can happen;
- the UI needs one deterministic stream.

## 6. Recommended Architecture

```text
OpenF1 / FastF1 / source importer
        |
        v
raw_car_telemetry hypertable
raw_location_telemetry hypertable
        |
        v
alignment materializer job
        |
        v
aligned_telemetry_10hz hypertable
        |
        v
desktop app / replay UI
```

Runtime UI path:

```text
desktop app -> query aligned_telemetry_10hz -> render
```

The app must not align raw car and location streams at runtime.

## 7. Alignment Strategy

Use a deterministic fixed-frequency output grid.

Default:

```yaml
telemetry_alignment:
  output_frequency_hz: 10
  output_interval_ms: 100
  max_interpolation_gap_ms: 1000
  max_source_age_ms: 750
  keep_raw_source_timestamps: true
  write_quality_flags: true
  alignment_version: 1
```

The materialized stream must have one row every 100 ms.

The output frequency may be configurable, but the default production table is 10 Hz.

Possible future extension:

```text
aligned_telemetry_20hz
```

Only add 20 Hz later if 10 Hz is visibly insufficient for specific screens.

Do not start with 60 Hz storage. A 60 Hz materialized stream generates many fake/interpolated rows and increases storage/IO without much benefit on a 60 Hz display.

## 8. Input Streams

### 8.1 Raw Car Telemetry

Expected columns:

```text
time
session_key
driver_number
speed
rpm
n_gear
throttle
brake
drs
```

Optional columns:

```text
meeting_key
driver_code
source
ingested_at
```

Field meaning:

```text
time            UTC timestamp of the source sample
session_key     session identifier
driver_number   driver identifier
speed           speed in km/h
rpm             engine RPM
n_gear          current gear
throttle        throttle percentage
brake           brake state/percentage depending on source
drs             DRS status code
```

### 8.2 Raw Location/GPS Telemetry

Expected columns:

```text
time
session_key
driver_number
x
y
z
```

Optional columns:

```text
meeting_key
driver_code
status
source
ingested_at
```

Field meaning:

```text
time            UTC timestamp of the source sample
session_key     session identifier
driver_number   driver identifier
x               track coordinate X
y               track coordinate Y
z               track coordinate Z
status          optional source status
```

## 9. Output Stream

The output is a single aligned stream.

Recommended logical schema:

```text
time
session_key
driver_number
driver_code
lap_number

sample_index
session_time_ms
lap_time_ms

speed
rpm
n_gear
throttle
brake
drs

x
y
z
location_status

source_car_time
source_location_time
car_sample_age_ms
location_sample_age_ms

is_interpolated_car
is_interpolated_location
quality_flags
alignment_version
created_at
```

One row represents one UI sample for one driver at one aligned timestamp.

The stream must be ordered by:

```text
session_key
driver_number
time
```

For lap replay, it must support ordering by:

```text
session_key
driver_number
lap_number
lap_time_ms
```

## 10. TimescaleDB Storage Model

### 10.1 Raw Car Telemetry Table

```sql
CREATE TABLE IF NOT EXISTS raw_car_telemetry (
    time            TIMESTAMPTZ NOT NULL,
    session_key     INTEGER NOT NULL,
    driver_number   INTEGER NOT NULL,

    speed           DOUBLE PRECISION,
    rpm             DOUBLE PRECISION,
    n_gear          INTEGER,
    throttle        DOUBLE PRECISION,
    brake           DOUBLE PRECISION,
    drs             INTEGER,

    source          TEXT,
    ingested_at     TIMESTAMPTZ NOT NULL DEFAULT now(),

    PRIMARY KEY (time, session_key, driver_number)
);

SELECT create_hypertable(
    'raw_car_telemetry',
    'time',
    if_not_exists => TRUE,
    chunk_time_interval => INTERVAL '1 day'
);
```

### 10.2 Raw Location Telemetry Table

```sql
CREATE TABLE IF NOT EXISTS raw_location_telemetry (
    time            TIMESTAMPTZ NOT NULL,
    session_key     INTEGER NOT NULL,
    driver_number   INTEGER NOT NULL,

    x               DOUBLE PRECISION,
    y               DOUBLE PRECISION,
    z               DOUBLE PRECISION,
    status          TEXT,

    source          TEXT,
    ingested_at     TIMESTAMPTZ NOT NULL DEFAULT now(),

    PRIMARY KEY (time, session_key, driver_number)
);

SELECT create_hypertable(
    'raw_location_telemetry',
    'time',
    if_not_exists => TRUE,
    chunk_time_interval => INTERVAL '1 day'
);
```

### 10.3 Aligned UI Telemetry Table

```sql
CREATE TABLE IF NOT EXISTS aligned_telemetry_10hz (
    time                    TIMESTAMPTZ NOT NULL,
    session_key             INTEGER NOT NULL,
    driver_number           INTEGER NOT NULL,
    driver_code             TEXT,
    lap_number              INTEGER,

    sample_index            INTEGER NOT NULL,
    session_time_ms         BIGINT,
    lap_time_ms             BIGINT,

    speed                   DOUBLE PRECISION,
    rpm                     DOUBLE PRECISION,
    n_gear                  INTEGER,
    throttle                DOUBLE PRECISION,
    brake                   DOUBLE PRECISION,
    drs                     INTEGER,

    x                       DOUBLE PRECISION,
    y                       DOUBLE PRECISION,
    z                       DOUBLE PRECISION,
    location_status         TEXT,

    source_car_time         TIMESTAMPTZ,
    source_location_time    TIMESTAMPTZ,

    car_sample_age_ms       INTEGER,
    location_sample_age_ms  INTEGER,

    is_interpolated_car      BOOLEAN NOT NULL DEFAULT TRUE,
    is_interpolated_location BOOLEAN NOT NULL DEFAULT TRUE,

    quality_flags           TEXT[] NOT NULL DEFAULT ARRAY['OK'],
    alignment_version       INTEGER NOT NULL DEFAULT 1,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT now(),

    PRIMARY KEY (time, session_key, driver_number)
);

SELECT create_hypertable(
    'aligned_telemetry_10hz',
    'time',
    if_not_exists => TRUE,
    chunk_time_interval => INTERVAL '1 day'
);
```

### 10.4 Recommended Indexes

Hot path for driver replay by time:

```sql
CREATE INDEX IF NOT EXISTS idx_aligned_telemetry_driver_time
ON aligned_telemetry_10hz (
    session_key,
    driver_number,
    time
);
```

Hot path for lap replay:

```sql
CREATE INDEX IF NOT EXISTS idx_aligned_telemetry_driver_lap
ON aligned_telemetry_10hz (
    session_key,
    driver_number,
    lap_number,
    lap_time_ms
);
```

Useful for multi-driver lap comparison:

```sql
CREATE INDEX IF NOT EXISTS idx_aligned_telemetry_session_lap
ON aligned_telemetry_10hz (
    session_key,
    lap_number,
    driver_number,
    lap_time_ms
);
```

## 11. Diagnostics Table

Create a diagnostics table for source frequency and data quality validation.

```sql
CREATE TABLE IF NOT EXISTS telemetry_ingestion_diagnostics (
    id                      BIGSERIAL PRIMARY KEY,
    session_key             INTEGER NOT NULL,
    driver_number           INTEGER NOT NULL,
    stream_name             TEXT NOT NULL,

    sample_count            INTEGER NOT NULL,
    start_time              TIMESTAMPTZ,
    end_time                TIMESTAMPTZ,

    min_delta_ms            DOUBLE PRECISION,
    median_delta_ms         DOUBLE PRECISION,
    p90_delta_ms            DOUBLE PRECISION,
    p99_delta_ms            DOUBLE PRECISION,
    max_delta_ms            DOUBLE PRECISION,

    estimated_frequency_hz  DOUBLE PRECISION,
    duplicate_count         INTEGER NOT NULL DEFAULT 0,
    out_of_order_count      INTEGER NOT NULL DEFAULT 0,

    warning_flags           TEXT[] NOT NULL DEFAULT ARRAY[]::TEXT[],
    created_at              TIMESTAMPTZ NOT NULL DEFAULT now()
);
```

For every imported session/driver/stream, write one diagnostic row.

## 12. Native Frequency Validation

The implementation must calculate observed frequency separately for:

```text
raw_car_telemetry
raw_location_telemetry
```

For each session and driver:

```python
delta_ms = time.diff().dt.total_seconds() * 1000
median_delta_ms = delta_ms.median()
estimated_frequency_hz = 1000 / median_delta_ms
```

Diagnostics must include:

```text
sample_count
min_delta_ms
median_delta_ms
p90_delta_ms
p99_delta_ms
max_delta_ms
estimated_frequency_hz
duplicate_count
out_of_order_count
```

The implementation must log warnings if:

```text
sample_count = 0
duplicate_count > 0
out_of_order_count > 0
max_delta_ms > max_interpolation_gap_ms
estimated frequency is unexpectedly low
```

Do not fail ingestion only because the observed frequency is different from the nominal one. Store diagnostics and continue when possible.

## 13. Grid Creation

For each session/driver/materialization unit, create a fixed timestamp grid.

Materialization unit may be:

```text
whole session
driver stint
driver lap
driver time window
```

Recommended initial implementation:

```text
materialize per session + driver
```

Compute safe interval:

```text
start = max(car_start, location_start)
end   = min(car_end, location_end)
```

Then generate timestamps every:

```text
1000 / output_frequency_hz milliseconds
```

For 10 Hz:

```text
100 ms
```

The generated output timestamps must be unique and ascending.

## 14. Interpolation Rules

### 14.1 Continuous Channels

Interpolate linearly:

```text
speed
rpm
throttle
x
y
z
```

Linear interpolation is acceptable because this stream is optimized for UI replay.

### 14.2 Discrete Channels

Use as-of backward fill:

```text
n_gear
brake
drs
location_status
```

For each output timestamp, use the latest known value at or before that timestamp.

Do not linearly interpolate:

```text
gear
brake
drs
status codes
```

### 14.3 Source Timestamp Tracking

For every output row, keep:

```text
source_car_time
source_location_time
car_sample_age_ms
location_sample_age_ms
```

`source_car_time` is the previous/raw car telemetry sample timestamp used as an anchor.

`source_location_time` is the previous/raw location sample timestamp used as an anchor.

Sample age:

```text
car_sample_age_ms = output_time - source_car_time
location_sample_age_ms = output_time - source_location_time
```

These fields help debug suspicious UI behavior.

## 15. Gap Handling

Configuration:

```yaml
max_interpolation_gap_ms: 1000
max_source_age_ms: 750
```

If the source samples around an output timestamp are too far apart, do not silently pretend the result is normal.

Add quality flags:

```text
CAR_GAP_TOO_LARGE
LOCATION_GAP_TOO_LARGE
```

If previous source sample age exceeds `max_source_age_ms`, add:

```text
CAR_SAMPLE_TOO_OLD
LOCATION_SAMPLE_TOO_OLD
```

The first version may still write the row, but it must make degraded quality visible.

## 16. Quality Flags

Use a text array or equivalent enum/bitmask.

Recommended flags:

```text
OK
MISSING_CAR_DATA
MISSING_LOCATION_DATA
CAR_GAP_TOO_LARGE
LOCATION_GAP_TOO_LARGE
CAR_SAMPLE_TOO_OLD
LOCATION_SAMPLE_TOO_OLD
EDGE_INTERPOLATION
DUPLICATE_SOURCE_TIMESTAMP
OUT_OF_ORDER_SOURCE_DATA
```

Rules:

- If no issue is detected, use `["OK"]`.
- If any issue is detected, do not include `OK`.
- The frontend may choose to hide, dim, or warn on degraded samples.
- The backend API must return quality flags.

## 17. Duplicate Timestamp Handling

If multiple source records exist with the same timestamp for the same session/driver/stream:

1. sort by timestamp ascending;
2. if ingestion order exists, keep the last ingested row;
3. otherwise keep the last row after sorting;
4. increment duplicate diagnostic count;
5. continue materialization.

The aligned output must have unique timestamps per:

```text
session_key
driver_number
time
```

## 18. Out-of-Order Data Handling

Before alignment:

1. detect out-of-order source rows;
2. increment diagnostic count;
3. sort source data by timestamp ascending;
4. continue materialization.

Out-of-order input must not produce out-of-order output.

## 19. Lap Association

If lap data is available, assign each aligned sample to a lap.

Recommended table assumption:

```text
laps
```

Required lap fields:

```text
session_key
driver_number
lap_number
lap_start_time
lap_end_time
```

Association rule:

```text
lap_start_time <= sample.time < lap_end_time
```

If `lap_end_time` is not available, use next lap start:

```text
lap_start_time <= sample.time < next_lap_start_time
```

Calculate:

```text
lap_time_ms = sample.time - lap_start_time
```

Rows outside known lap windows may be stored with:

```text
lap_number = null
lap_time_ms = null
```

## 20. API Requirements

The API must return aligned telemetry without additional joins.

### 20.1 Get Telemetry for One Driver/Lap

```http
GET /sessions/{sessionKey}/drivers/{driverNumber}/laps/{lapNumber}/telemetry
```

Response shape:

```json
{
  "sessionKey": 9159,
  "driverNumber": 55,
  "lapNumber": 12,
  "frequencyHz": 10,
  "samples": [
    {
      "sampleIndex": 0,
      "dateUtc": "2023-09-15T13:08:19.900Z",
      "sessionTimeMs": 1234567,
      "lapTimeMs": 0,
      "speed": 312.4,
      "rpm": 11120,
      "nGear": 8,
      "throttle": 98.3,
      "brake": 0,
      "drs": 12,
      "x": 123.4,
      "y": 456.7,
      "z": 0.0,
      "qualityFlags": ["OK"]
    }
  ]
}
```

SQL:

```sql
SELECT
    sample_index,
    time AS date_utc,
    session_time_ms,
    lap_time_ms,
    speed,
    rpm,
    n_gear,
    throttle,
    brake,
    drs,
    x,
    y,
    z,
    location_status,
    quality_flags
FROM aligned_telemetry_10hz
WHERE session_key = $1
  AND driver_number = $2
  AND lap_number = $3
ORDER BY lap_time_ms;
```

### 20.2 Get Telemetry for a Time Window

```http
GET /sessions/{sessionKey}/drivers/{driverNumber}/telemetry?from=...&to=...
```

SQL:

```sql
SELECT
    sample_index,
    time AS date_utc,
    session_time_ms,
    lap_number,
    lap_time_ms,
    speed,
    rpm,
    n_gear,
    throttle,
    brake,
    drs,
    x,
    y,
    z,
    location_status,
    quality_flags
FROM aligned_telemetry_10hz
WHERE session_key = $1
  AND driver_number = $2
  AND time >= $3
  AND time < $4
ORDER BY time;
```

### 20.3 Get Multi-Driver Lap Telemetry

Useful for lap comparison or ghost/replay mode.

```http
GET /sessions/{sessionKey}/laps/{lapNumber}/telemetry?drivers=1,16,44,55
```

SQL:

```sql
SELECT
    driver_number,
    sample_index,
    time AS date_utc,
    lap_time_ms,
    speed,
    rpm,
    n_gear,
    throttle,
    brake,
    drs,
    x,
    y,
    z,
    quality_flags
FROM aligned_telemetry_10hz
WHERE session_key = $1
  AND lap_number = $2
  AND driver_number = ANY($3)
ORDER BY driver_number, lap_time_ms;
```

## 21. UI Playback Requirements

The frontend must use the aligned telemetry stream as product data.

The frontend must not:

- query raw car telemetry and raw location telemetry separately for replay;
- perform source stream joins;
- align GPS and telemetry timestamps;
- calculate missing location from raw source timestamps.

The frontend may:

- render at 60 FPS;
- maintain a playback clock;
- use current replay time to find the previous and next aligned telemetry sample;
- interpolate visual position between adjacent aligned samples;
- interpolate chart cursor position;
- show nearest sample values in telemetry panels.

The frontend interpolation is only for visual smoothing between already-aligned samples.

This is allowed and expected.

## 22. Why 10 Hz Is the Default

10 Hz means one materialized sample every 100 ms.

For a 90-second lap:

```text
10 Hz -> 900 samples
20 Hz -> 1,800 samples
60 Hz -> 5,400 samples
```

For a race with 20 drivers and 70 laps:

```text
10 Hz -> ~1.26M samples
20 Hz -> ~2.52M samples
60 Hz -> ~7.56M samples
```

10 Hz is the recommended starting point because:

- it is good enough for snappy 0.5x/1x/5x playback;
- it keeps database size reasonable;
- it avoids generating too many fake/interpolated rows;
- it maps well to 5x replay on a 60 Hz screen;
- the UI can still render smoothly at 60 FPS.

## 23. Implementation Module

Create a module/package:

```text
telemetry_alignment/
```

Suggested structure:

```text
telemetry_alignment/
  __init__.py
  aligner.py
  models.py
  diagnostics.py
  storage.py
  sql/
    001_create_raw_car_telemetry.sql
    002_create_raw_location_telemetry.sql
    003_create_aligned_telemetry_10hz.sql
    004_create_diagnostics.sql
  tests/
    test_alignment.py
    test_gap_handling.py
    test_frequency_detection.py
    test_discrete_channels.py
    test_duplicate_timestamps.py
    test_out_of_order_data.py
```

## 24. Core Python Function

Implement this function first:

```python
def align_telemetry_for_ui(
    car_df,
    location_df,
    *,
    output_frequency_hz: int = 10,
    max_interpolation_gap_ms: int = 1000,
    max_source_age_ms: int = 750,
):
    """
    Align car telemetry and location telemetry onto a fixed-frequency UI grid.

    Input:
        car_df:
            DataFrame containing time, speed, rpm, n_gear, throttle, brake, drs.

        location_df:
            DataFrame containing time, x, y, z, and optionally status.

    Output:
        DataFrame with one row per aligned UI timestamp.
    """
```

Input requirements:

```text
car_df must contain time, speed, rpm, n_gear, throttle, brake, drs
location_df must contain time, x, y, z
time columns must be timezone-aware UTC timestamps or convertible to UTC
```

Output requirements:

```text
one row per aligned timestamp
timestamps sorted ascending
timestamps unique
continuous fields linearly interpolated
discrete fields as-of backward filled
quality flags populated
source timestamps retained
```

## 25. Suggested Algorithm

1. Normalize timestamps to UTC.
2. Sort car data by timestamp.
3. Sort location data by timestamp.
4. Detect duplicate source timestamps.
5. Resolve duplicate source timestamps.
6. Detect out-of-order input.
7. Compute diagnostics for each source stream.
8. Compute alignment interval:

```text
start = max(car_start, location_start)
end = min(car_end, location_end)
```

9. Generate output grid at 10 Hz.
10. Interpolate continuous car channels with vectorized operations.
11. Interpolate continuous location channels with vectorized operations.
12. Fill discrete car channels using as-of backward fill.
13. Fill discrete location/status channels using as-of backward fill.
14. Track previous source timestamps.
15. Calculate source sample ages.
16. Detect large source gaps.
17. Apply quality flags.
18. Assign lap metadata if lap data is provided.
19. Return dataframe.
20. Batch insert into TimescaleDB.

## 26. Performance Requirements

The implementation must support bulk ingestion of many sessions.

Requirements:

```text
No per-row Python loops over samples
Use vectorized NumPy/Pandas operations
Use merge_asof or equivalent for discrete channels
Use numpy.interp or equivalent for continuous channels
Batch insert output rows
Avoid calling FastF1 get_telemetry()
```

The implementation must not call:

```python
add_driver_ahead()
add_distance()
add_relative_distance()
```

unless a future configuration explicitly enables those derived channels.

## 27. Bulk Ingestion Strategy

For each session:

1. Load/import raw car telemetry into `raw_car_telemetry`.
2. Load/import raw location telemetry into `raw_location_telemetry`.
3. For each driver:
   1. load car data from DB or in-memory import;
   2. load location data from DB or in-memory import;
   3. run `align_telemetry_for_ui`;
   4. assign lap metadata if available;
   5. write diagnostics;
   6. batch insert into `aligned_telemetry_10hz`.

The materializer should be idempotent.

Before inserting aligned data for a given session/driver/alignment version, either:

```sql
DELETE FROM aligned_telemetry_10hz
WHERE session_key = $1
  AND driver_number = $2
  AND alignment_version = $3;
```

or use an upsert strategy.

Recommended initial strategy:

```text
delete + reinsert per session/driver/alignment_version
```

Simple and predictable.

## 28. TimescaleDB Query-Time Alignment

TimescaleDB can perform query-time bucketing/gap filling, but this must not be the hot path for the UI.

Query-time alignment may be used for:

- experiments;
- diagnostics;
- one-off analysis;
- validating materialization behavior.

The production UI path must use `aligned_telemetry_10hz`.

Reason:

```text
align once during ingestion
read many times during UI
```

## 29. Compression and Retention

Raw and aligned data may use different retention/compression policies.

Suggested policy:

```text
raw telemetry:
  keep indefinitely during development
  compress after N days if needed

aligned telemetry:
  keep as long as the session is available in the product
  can always be regenerated from raw data
```

Do not delete raw data unless regeneration is no longer required.

## 30. Acceptance Criteria

### AC1: Output Is Aligned

Given car and location data with different source timestamps, the output contains one row per generated UI timestamp.

There must be no separate car/location rows.

### AC2: Output Frequency Is Stable

For `output_frequency_hz = 10`, output timestamps must be spaced by 100 ms, except possible final boundary behavior.

### AC3: Continuous Values Are Interpolated

Continuous values must be linearly interpolated between surrounding source samples.

### AC4: Discrete Values Are Not Interpolated

Gear, brake, DRS, and status values must use latest known source value at or before the output timestamp.

### AC5: FastF1 Convenience Pipeline Is Not Used

The implementation must not call FastF1 `get_telemetry()` internally.

The implementation must not compute driver-ahead or distance channels.

### AC6: Diagnostics Are Produced

For every input stream, diagnostics must include:

```text
sample count
median delta
estimated frequency
max gap
duplicate count
out-of-order count
```

### AC7: Gaps Are Visible

If source data has a gap larger than `max_interpolation_gap_ms`, affected rows must have quality flags.

### AC8: UI Can Query Without Joining

The API/storage result must allow the frontend to render telemetry and car position using a single ordered result set.

### AC9: Raw Data Is Preserved

Raw car telemetry and raw location telemetry must be stored separately from aligned telemetry.

### AC10: Materialization Is Idempotent

Running the materializer twice for the same session/driver/alignment version must not create duplicate aligned rows.

## 31. Unit Tests

### Test 1: Basic Alignment

Input:

```text
car timestamps:      00.000, 00.300, 00.600
location timestamps: 00.100, 00.400, 00.700
output frequency: 10 Hz
```

Expected output timestamps:

```text
00.100, 00.200, 00.300, 00.400, 00.500, 00.600
```

### Test 2: Discrete Gear Fill

Input gear:

```text
00.000 -> 3
00.500 -> 4
```

Expected at 10 Hz:

```text
00.100 -> 3
00.200 -> 3
00.300 -> 3
00.400 -> 3
00.500 -> 4
```

### Test 3: Continuous Speed Interpolation

Input speed:

```text
00.000 -> 100
00.500 -> 150
```

Expected:

```text
00.250 -> 125
```

### Test 4: Location Interpolation

Input location:

```text
00.000 -> x=0
00.500 -> x=100
```

Expected:

```text
00.250 -> x=50
```

### Test 5: Large Gap Flagging

Input:

```text
max_interpolation_gap_ms = 1000
location gap = 2500 ms
```

Expected:

```text
quality_flags contains LOCATION_GAP_TOO_LARGE
```

### Test 6: Duplicate Timestamp

Input:

```text
two car rows with same timestamp
```

Expected:

```text
only one source row used
diagnostic duplicate_count > 0
```

### Test 7: Out-of-Order Source Data

Input:

```text
source rows not sorted by timestamp
```

Expected:

```text
output sorted by timestamp
diagnostic out_of_order_count > 0
```

### Test 8: UI Query Shape

Given aligned samples for one lap, API query by session/driver/lap returns:

```text
ordered samples
one row per timestamp
car telemetry and location in same row
quality flags included
```

## 32. Codex Implementation Notes

Prioritize correctness and clarity over clever optimization in the first implementation.

The first version should be easy to inspect.

Use plain Pandas DataFrames as the alignment function boundary.

Keep FastF1/OpenF1-specific code outside the core aligner when possible.

The aligner should not care where the data came from, as long as the input DataFrames have the required columns.

The aligned output is a materialized UI product, not the authoritative raw telemetry truth.

Raw input streams must remain available so the aligned stream can be regenerated if the strategy changes.

## 33. Final Design Summary

Use this model:

```text
TimescaleDB stores the truth.
The aligned hypertable serves the product.
The frontend just renders.
```

Default implementation:

```text
raw_car_telemetry hypertable
raw_location_telemetry hypertable
aligned_telemetry_10hz hypertable
telemetry_ingestion_diagnostics table
10 Hz materialization
60 FPS frontend rendering
visual interpolation only between already-aligned samples
```

Do not align in the desktop app.

Do not rely on dynamic SQL gap filling for every replay request.

Do not use FastF1 `get_telemetry()` for bulk UI materialization.

Align once during ingestion.

Read many times during UI.
