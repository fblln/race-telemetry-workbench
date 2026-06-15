# F1 Telemetry Visualizer — Focused Implementation Specification

**Owner:** Fabio  
**Status:** Implementation specification  
**Scope:** Local Formula 1 telemetry import, storage, replay, visual analysis, and natural-language querying.

---

## 1. Product Goal

Build a high-performance local desktop application that imports public Formula
1 telemetry for one selected race session by default, stores it in TimescaleDB,
and lets the user replay, inspect, compare, and query that race through a
focused UI and an AI-ready MCP interface.

The implementation must prioritize a small set of capabilities that demonstrate the framework clearly:

1. Import one real race session for all available drivers.
2. Replay the race in the desktop app at multiple speeds.
3. Compare two laps by lap-relative time using core telemetry channels.
4. Ask natural-language questions through a read-only MCP server.
5. Observe local .NET services through Aspire Dashboard.

The desktop application is the main product surface. The Query API, MCP server,
import scripts, and database exist to support fast local desktop replay and
analysis.

The desktop experience should feel like a focused motorsport analysis
workbench: a synchronized track map, waveform view, lap table, current-value
readouts, and event timeline should all describe the same replay timestamp.
The project must use original requirements, terminology, diagrams, and generated
visual assets.

---

## 2. Target Architecture

```text
[Import Script]
      |
      | bulk load / upsert
      v
[TimescaleDB]
      ^
      |
[Query API] <---- REST ----> [.NET MAUI Desktop App]
      ^                         ^
      |                         |
[MCP Query Server]              |
      ^                         |
      |                         |
[MCP-compatible AI Client] -----+
```

### 2.1 Runtime Components

| Component | Runtime | Started by Aspire | Docker | Purpose |
|---|---:|---:|---:|---|
| TimescaleDB | Postgres + TimescaleDB | Yes | Yes | Local time-series database |
| Query API | ASP.NET Core Minimal API | Yes | No | REST backend for sessions, replay chunks, lap comparison, and event search |
| MCP Query Server | .NET MCP server | Yes | No | Read-only natural-language adapter over the Query API |
| .NET MAUI Desktop App | Native .NET desktop app | Optional | No | Primary product surface for session selection, replay, lap comparison, and optional AI panel |
| Import Script | Python CLI script | No | No | Loads one selected real race into TimescaleDB by default |
| Aspire Dashboard | Aspire built-in UI | Yes | No | Local logs, traces, and metrics |

The initial runtime requires one Docker container: TimescaleDB.

### 2.2 Database Requirements

TimescaleDB is the primary database target.

Use PostgreSQL relational tables for bounded metadata and event data:

- `sessions`
- `session_drivers`
- `laps`
- `circuit_metadata`
- `circuit_markers`
- `track_status_events`
- `session_status_events`
- `race_control_messages`

Use Timescale hypertables for high-volume or time-windowed sample data:

- `telemetry_samples`
- `position_samples`
- `weather_samples`

Keep the application API and MCP server database-agnostic at their boundary:
they must read through Query API contracts and raw SQL query services, not
through direct FastF1 access.

---

## 3. Import Script

### 3.1 Purpose

The import script loads one real Formula 1 race session into TimescaleDB by default. After import, the rest of the system reads only from the database.

The import script uses FastF1 as the source for historical timing, lap, telemetry, position, weather, circuit, and race-control data.

Race data is the default import scope. Practice, qualifying, sprint qualifying, and sprint sessions are explicit opt-ins through non-race `--session` values.

### 3.2 Location

```text
scripts/import_session.py
```

### 3.3 Required Import Scope

The script must import:

- One championship year.
- One event or circuit.
- One session type, defaulting to `R`.
- Non-race session types `FP1`, `FP2`, `FP3`, `Q`, `SQ`, and `S` are opt-in only.
- All available drivers by default.
- Lap metadata for every available driver.
- Raw car telemetry samples from FastF1 `lap.get_car_data()`: speed, throttle, brake, gear, RPM, DRS, session-relative time, lap-relative time, and source.
- Position samples: x, y, z, track status, and source.
- Circuit metadata from FastF1 `session.get_circuit_info()`: map rotation, corners, marshal lights, and marshal sectors when available.
- Weather samples from FastF1 `session.weather_data`: air temperature, track temperature, humidity, pressure, rainfall, wind direction, and wind speed.
- Race-control and track-status events: safety car, virtual safety car, yellow/red/green flag periods, DRS status messages, and other race-control messages where available.

### 3.4 Command Examples

```bash
python scripts/import_session.py \
  --year 2024 \
  --event "Monza" \
  --if-exists replace
```

```bash
python scripts/import_session.py --year 2024 --event "Silverstone" --if-exists upsert
python scripts/import_session.py --year 2024 --event "Silverstone" --session "Q" --if-exists upsert
python scripts/import_session.py --year 2023 --event "Spa" --session "R" --drivers VER,HAM,LEC
python scripts/import_session.py --year 2024 --event "Monaco" --session "R" --dry-run
```

### 3.5 CLI Options

| Option | Required | Default | Example | Description |
|---|---:|---|---|---|
| `--year` | Yes | | `2024` | Championship year |
| `--event` | Yes | | `Monza` | Event, circuit, or Grand Prix name |
| `--session` | No | `R` | `R` | Session identifier. Non-race sessions are explicit opt-ins. |
| `--database-url` | No | env var | connection string | Overrides environment config |
| `--drivers` | No | all | `VER,HAM,LEC` | Optional driver-code subset |
| `--if-exists` | No | `upsert` | `fail`, `upsert`, `replace` | Import conflict behavior |
| `--dry-run` | No | false | | Fetch and validate without writing |
| `--limit-laps` | No | none | `10` | Developer shortcut for smaller imports |
| `--log-level` | No | `INFO` | `DEBUG` | Logging level |

### 3.6 Import Behavior

The script must:

1. Resolve the requested event and session, defaulting to race (`R`).
2. Fetch all available drivers unless `--drivers` is specified.
3. Normalize source data into the database schema.
   - Use FastF1 `session.car_data` for raw car telemetry rows.
   - Use FastF1 `session.pos_data` for raw position rows used by track-map replay.
   - Use FastF1 `session.get_circuit_info()` for circuit annotations. Import should continue if circuit metadata is unavailable, but the summary must report that it was skipped.
   - Use FastF1 `session.weather_data` for session weather samples. Import should continue if weather is unavailable, but the summary must report that it was skipped.
   - Use FastF1 `session.track_status`, `session.session_status`, and `session.race_control_messages` for race-control timelines. Import should continue if race-control messages are unavailable, but the summary must report that they were skipped.
4. Generate stable IDs:
   - `session_id`: `{year}-{event-slug}-{session-lower}`
   - `lap_id`: `{session_id}-{driver_code_lower}-{lap_number}`
5. Store missing telemetry values as `NULL`.
6. Convert boolean brake values to `0` or `100`.
7. Bulk insert raw telemetry and position samples.
8. Be idempotent when `--if-exists upsert` is used.
9. Delete and reload the selected session when `--if-exists replace` is used.
10. Exit with a non-zero status on validation or database errors.

### 3.7 Import Summary

A successful import must print:

```text
Import completed successfully.
Session: 2024 Monza R
Drivers: 20
Laps: 1062
Telemetry samples: 3,481,220
Position samples: 3,481,220
Database: f1telemetry@localhost:5432
Mode: replace
Elapsed: 00:02:41
```

---

## 4. Database Schema

### 4.1 `sessions`

```sql
CREATE TABLE IF NOT EXISTS sessions (
    session_id TEXT PRIMARY KEY,
    year INT NOT NULL,
    event_name TEXT NOT NULL,
    circuit_name TEXT NULL,
    country TEXT NULL,
    session_type TEXT NOT NULL,
    session_start_utc TIMESTAMPTZ NULL,
    session_end_utc TIMESTAMPTZ NULL,
    source TEXT NOT NULL,
    imported_at_utc TIMESTAMPTZ NOT NULL DEFAULT now(),
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb
);
```

### 4.2 `session_drivers`

```sql
CREATE TABLE IF NOT EXISTS session_drivers (
    session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
    driver_code TEXT NOT NULL,
    driver_number INT NULL,
    full_name TEXT NULL,
    team_name TEXT NULL,
    PRIMARY KEY (session_id, driver_code)
);
```

### 4.3 `laps`

```sql
CREATE TABLE IF NOT EXISTS laps (
    lap_id TEXT PRIMARY KEY,
    session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
    driver_code TEXT NOT NULL,
    lap_number INT NOT NULL,
    lap_start_utc TIMESTAMPTZ NULL,
    lap_end_utc TIMESTAMPTZ NULL,
    lap_time_ms INT NULL,
    sector_1_ms INT NULL,
    sector_2_ms INT NULL,
    sector_3_ms INT NULL,
    compound TEXT NULL,
    tyre_life INT NULL,
    is_pit_out_lap BOOLEAN NOT NULL DEFAULT false,
    is_pit_in_lap BOOLEAN NOT NULL DEFAULT false,
    is_deleted BOOLEAN NOT NULL DEFAULT false,
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    UNIQUE (session_id, driver_code, lap_number),
    FOREIGN KEY (session_id, driver_code)
        REFERENCES session_drivers(session_id, driver_code)
        ON DELETE CASCADE
);
```

### 4.4 `telemetry_samples`

```sql
CREATE TABLE IF NOT EXISTS telemetry_samples (
    sample_time_utc TIMESTAMPTZ NOT NULL,
    session_id TEXT NOT NULL,
    driver_code TEXT NOT NULL,
    lap_number INT NULL,
    session_time_ms BIGINT NULL,
    lap_time_ms BIGINT NULL,
    speed_kmh DOUBLE PRECISION NULL,
    throttle_pct DOUBLE PRECISION NULL,
    brake_pct DOUBLE PRECISION NULL,
    gear INT NULL,
    rpm DOUBLE PRECISION NULL,
    drs INT NULL,
    sample_source TEXT NULL,
    source_sample_index BIGINT NULL,
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    PRIMARY KEY (sample_time_utc, session_id, driver_code),
    FOREIGN KEY (session_id, driver_code)
        REFERENCES session_drivers(session_id, driver_code)
        ON DELETE CASCADE
);

SELECT create_hypertable('telemetry_samples', 'sample_time_utc', if_not_exists => TRUE);

CREATE INDEX IF NOT EXISTS ix_telemetry_session_driver_lap_time
ON telemetry_samples (session_id, driver_code, lap_number, lap_time_ms);

CREATE INDEX IF NOT EXISTS ix_telemetry_session_time
ON telemetry_samples (session_id, sample_time_utc);
```

### 4.5 `position_samples`

```sql
CREATE TABLE IF NOT EXISTS position_samples (
    sample_time_utc TIMESTAMPTZ NOT NULL,
    session_id TEXT NOT NULL,
    driver_code TEXT NOT NULL,
    lap_number INT NULL,
    x DOUBLE PRECISION NULL,
    y DOUBLE PRECISION NULL,
    z DOUBLE PRECISION NULL,
    track_status TEXT NULL,
    sample_source TEXT NULL,
    source_sample_index BIGINT NULL,
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    PRIMARY KEY (sample_time_utc, session_id, driver_code),
    FOREIGN KEY (session_id, driver_code)
        REFERENCES session_drivers(session_id, driver_code)
        ON DELETE CASCADE
);

SELECT create_hypertable('position_samples', 'sample_time_utc', if_not_exists => TRUE);

CREATE INDEX IF NOT EXISTS ix_position_session_driver_lap
ON position_samples (session_id, driver_code, lap_number);
```

### 4.6 `circuit_metadata`

```sql
CREATE TABLE IF NOT EXISTS circuit_metadata (
    session_id TEXT PRIMARY KEY REFERENCES sessions(session_id) ON DELETE CASCADE,
    rotation_degrees DOUBLE PRECISION NULL,
    source TEXT NOT NULL,
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb
);
```

### 4.7 `circuit_markers`

```sql
CREATE TABLE IF NOT EXISTS circuit_markers (
    session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
    marker_type TEXT NOT NULL,
    marker_number INT NULL,
    marker_letter TEXT NULL,
    x DOUBLE PRECISION NOT NULL,
    y DOUBLE PRECISION NOT NULL,
    angle_degrees DOUBLE PRECISION NULL,
    distance_m DOUBLE PRECISION NULL,
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    PRIMARY KEY (session_id, marker_type, marker_number, marker_letter, x, y)
);

CREATE INDEX IF NOT EXISTS ix_circuit_markers_session_type
ON circuit_markers (session_id, marker_type);
```

`marker_type` must be one of:

- `corner`
- `marshal_light`
- `marshal_sector`

Circuit markers come from FastF1 `session.get_circuit_info()`. The desktop track outline must be derived from imported `position_samples`; circuit markers are annotations over that data-native outline.

### 4.8 `weather_samples`

```sql
CREATE TABLE IF NOT EXISTS weather_samples (
    session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
    sample_time_utc TIMESTAMPTZ NOT NULL,
    session_time_ms BIGINT NOT NULL,
    air_temp_c DOUBLE PRECISION NULL,
    track_temp_c DOUBLE PRECISION NULL,
    humidity_pct DOUBLE PRECISION NULL,
    pressure_mbar DOUBLE PRECISION NULL,
    rainfall BOOLEAN NULL,
    wind_direction_deg INT NULL,
    wind_speed_mps DOUBLE PRECISION NULL,
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    PRIMARY KEY (sample_time_utc, session_id)
);

CREATE INDEX IF NOT EXISTS ix_weather_samples_session_time
ON weather_samples (session_id, session_time_ms);

SELECT create_hypertable('weather_samples', 'sample_time_utc', if_not_exists => TRUE);
```

FastF1 weather samples are low-frequency session samples, often around one row per minute. They are useful for context, overlays, search, and race narrative, but they are not high-frequency telemetry.

### 4.9 `track_status_events`

```sql
CREATE TABLE IF NOT EXISTS track_status_events (
    session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
    event_time_ms BIGINT NOT NULL,
    status_code TEXT NOT NULL,
    message TEXT NULL,
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    PRIMARY KEY (session_id, event_time_ms, status_code)
);
```

Known FastF1 track status codes:

- `1`: track clear
- `2`: yellow flag
- `4`: safety car
- `5`: red flag
- `6`: virtual safety car deployed
- `7`: virtual safety car ending

### 4.10 `session_status_events`

```sql
CREATE TABLE IF NOT EXISTS session_status_events (
    session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
    event_time_ms BIGINT NOT NULL,
    status TEXT NOT NULL,
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    PRIMARY KEY (session_id, event_time_ms, status)
);
```

### 4.11 `race_control_messages`

```sql
CREATE TABLE IF NOT EXISTS race_control_messages (
    session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
    message_time_utc TIMESTAMPTZ NULL,
    session_time_ms BIGINT NULL,
    category TEXT NULL,
    message TEXT NOT NULL,
    status TEXT NULL,
    flag TEXT NULL,
    scope TEXT NULL,
    sector TEXT NULL,
    racing_number INT NULL,
    lap_number INT NULL,
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb
);

CREATE INDEX IF NOT EXISTS ix_race_control_messages_session_time
ON race_control_messages (session_id, session_time_ms);

CREATE INDEX IF NOT EXISTS ix_race_control_messages_session_lap
ON race_control_messages (session_id, lap_number);
```

Race-control messages are more verbose than `track_status_events` and may include DRS, pit-exit, investigation, flag, sector, lap, or driver-specific messages.

### 4.12 Analytical Views And Summaries

The database must expose bounded analytical views/materialized views for Query
API and MCP use. MCP questions must not be answered by returning unbounded raw
samples.

Initial planned views:

- `lap_summaries`
  - one row per session, driver, lap;
  - lap time, sector times, tyre compound/life, max/average speed, throttle and
    brake summary values, deleted/accurate flags.
- `driver_stint_summaries`
  - grouped by session, driver, stint, compound;
  - lap range, stint length, lap-time trend, tyre-life range.
- `session_weather_summary`
  - min/max/average air temperature, track temperature, humidity, pressure,
    wind speed, and rainfall observed flag.
- `track_status_periods`
  - normalized periods with start/end session time for clear, yellow, safety
    car, VSC, red flag, and related states.
- `race_control_event_index`
  - searchable race-control messages with normalized category, flag, lap,
    sector, and driver/racing-number context.
- `telemetry_event_candidates`
  - bounded helper view for common MCP/event searches such as hard braking,
    high speed, DRS usage, and throttle lift events.

Views may be materialized when repeated analytics require lower latency.

### 4.13 Supported Channels

| Database column | API field | Unit | Notes |
|---|---|---:|---|
| `speed_kmh` | `speedKmh` | km/h | Vehicle speed |
| `throttle_pct` | `throttlePct` | % | 0-100 |
| `brake_pct` | `brakePct` | % | 0-100 |
| `gear` | `gear` | integer | 1-8, neutral may be 0 |
| `rpm` | `rpm` | rpm | Engine speed |
| `drs` | `drs` | integer | Preserve source DRS state |
| `session_time_ms` | `sessionTimeMs` | ms | Milliseconds from FastF1 session start |
| `lap_time_ms` | `lapTimeMs` | ms | Milliseconds from lap start |
| `sample_source` | `sampleSource` | text | FastF1 sample source, for example `car` or `pos` |
| `track_status` | `trackStatus` | text | Position/status value such as `OnTrack` from position data |
| `x` | `x` | source units | Track-map coordinate |
| `y` | `y` | source units | Track-map coordinate |
| `z` | `z` | source units | Track-map coordinate |

All API and MCP inputs must validate channel names against this allow-list.

---

## 5. Query API

### 5.1 Technology

```text
.NET 9
ASP.NET Core Minimal APIs
Npgsql
EF Core where useful
Raw SQL for analytical and time-series queries
OpenAPI in local development
Aspire service defaults
```

### 5.2 Responsibilities

The Query API must:

- Read from TimescaleDB.
- Expose REST endpoints only.
- Return deterministic DTOs consumed by the desktop app and MCP server.
- Keep MCP and REST capabilities in sync: every MCP analytical tool must be
  backed by a Query API route and shared contract unless a decision record
  explicitly documents why it is MCP-only.
- Validate session IDs, driver codes, lap numbers, channel names, row limits, and bounded time ranges.
- Emit structured logs, traces, and metrics to Aspire Dashboard.

### 5.3 Error Shape

All validation and business errors must use this shape:

```json
{
  "error": {
    "code": "InvalidDriver",
    "message": "Driver code XYZ does not exist in session 2024-monza-r.",
    "details": {
      "sessionId": "2024-monza-r",
      "driverCode": "XYZ"
    }
  }
}
```

---

## 6. Query API Endpoints

### 6.1 `GET /api/sessions`

Returns imported sessions.

Query parameters:

| Name | Required | Example | Description |
|---|---:|---|---|
| `year` | No | `2024` | Filter by season |
| `event` | No | `Monza` | Case-insensitive event search |
| `sessionType` | No | `R` | Filter by session type |

Example:

```http
GET /api/sessions?year=2024&event=Monza&sessionType=R
```

Response:

```json
{
  "items": [
    {
      "sessionId": "2024-monza-r",
      "year": 2024,
      "eventName": "Italian Grand Prix",
      "circuitName": "Monza",
      "country": "Italy",
      "sessionType": "R",
      "sessionStartUtc": "2024-09-01T13:00:00Z",
      "driverCount": 20,
      "lapCount": 1062
    }
  ]
}
```

### 6.2 `GET /api/sessions/{sessionId}/drivers`

Returns drivers for one session.

```http
GET /api/sessions/2024-monza-r/drivers
```

```json
{
  "sessionId": "2024-monza-r",
  "items": [
    {
      "driverCode": "LEC",
      "driverNumber": 16,
      "fullName": "Charles Leclerc",
      "teamName": "Ferrari"
    }
  ]
}
```

Acceptance criteria:

- Returns `404` if `sessionId` does not exist.
- Returns drivers ordered by `driverCode`.

### 6.3 `GET /api/sessions/{sessionId}/drivers/{driverCode}/laps`

Returns laps for one driver.

```http
GET /api/sessions/2024-monza-r/drivers/LEC/laps
```

```json
{
  "sessionId": "2024-monza-r",
  "driverCode": "LEC",
  "items": [
    {
      "lapNumber": 12,
      "lapTimeMs": 82540,
      "sector1Ms": 27123,
      "sector2Ms": 29612,
      "sector3Ms": 25805,
      "compound": "MEDIUM",
      "tyreLife": 8,
      "isPitOutLap": false,
      "isPitInLap": false,
      "isDeleted": false
    }
  ]
}
```

Acceptance criteria:

- Returns `404` if the session or driver does not exist.
- Excludes deleted laps by default.
- Orders laps by `lapNumber`.

### 6.4 `GET /api/sessions/{sessionId}/drivers/{driverCode}/laps/{lapNumber}/telemetry`

Returns telemetry for one lap.

Query parameters:

| Name | Required | Default | Description |
|---|---:|---|---|
| `channels` | No | core telemetry channels | Comma-separated channel allow-list |
| `sampleEvery` | No | `1` | Return every Nth sample |
| `maxSamples` | No | `5000` | Response cap |

Example:

```http
GET /api/sessions/2024-monza-r/drivers/LEC/laps/12/telemetry?channels=speed_kmh,throttle_pct,brake_pct,rpm,gear&sampleEvery=2&maxSamples=5000
```

Response:

```json
{
  "sessionId": "2024-monza-r",
  "driverCode": "LEC",
  "lapNumber": 12,
  "channels": ["speed_kmh", "throttle_pct", "brake_pct", "rpm", "gear"],
  "items": [
    {
      "t": "2024-09-01T13:14:03.120Z",
      "distanceM": 0.0,
      "speedKmh": 282.4,
      "throttlePct": 100.0,
      "brakePct": 0.0,
      "rpm": 11342,
      "gear": 8
    }
  ]
}
```

Validation:

- `maxSamples`: `1` to `50000`.
- `sampleEvery`: `1` to `100`.
- Unknown channels return `400`.
- Nonexistent session, driver, or lap returns `404`.

### 6.5 `GET /api/sessions/{sessionId}/compare/laps`

Compares two laps by lap-relative time. This is the main analytical endpoint.

Query parameters:

| Name | Required | Example |
|---|---:|---|
| `driverA` | Yes | `LEC` |
| `lapA` | Yes | `12` |
| `driverB` | Yes | `HAM` |
| `lapB` | Yes | `14` |
| `channels` | No | `speed_kmh,throttle_pct,brake_pct` |
| `timeStepMs` | No | `100` |
| `sessionIdB` | No | `2025-monza-r` |

`sessionIdB` enables cross-session lap comparison, for example comparing a
driver's lap at the same circuit across two seasons. When omitted, `driverB`
and `lapB` are read from `sessionId` (the existing single-session behavior).
When present, `driverB` and `lapB` are read from `sessionIdB` instead.

Example:

```http
GET /api/sessions/2024-monza-r/compare/laps?driverA=LEC&lapA=12&driverB=HAM&lapB=14&channels=speed_kmh,throttle_pct,brake_pct&timeStepMs=100
```

Response:

```json
{
  "sessionId": "2024-monza-r",
  "driverA": "LEC",
  "lapA": 12,
  "driverB": "HAM",
  "lapB": 14,
  "timeStepMs": 100,
  "items": [
    {
      "lapTimeMs": 0,
      "a": { "speedKmh": 282.4, "throttlePct": 100.0, "brakePct": 0.0 },
      "b": { "speedKmh": 279.1, "throttlePct": 100.0, "brakePct": 0.0 },
      "delta": { "speedKmh": 3.3, "throttlePct": 0.0, "brakePct": 0.0 }
    }
  ],
  "summary": {
    "lapTimeDeltaMs": -214,
    "sectorDeltasMs": [-157, 192, -249],
    "maxSpeedDeltaKmh": 8.7,
    "avgSpeedDeltaKmh": 1.9
  }
}
```

Delta convention: `driverA - driverB`. Negative lap or sector deltas mean driver A was faster.
When `sessionIdB` is set, the response includes `sessionIdA` and `sessionIdB`
fields alongside `driverA`/`driverB` so cross-session results are
unambiguous.

Acceptance criteria:

- Aligns samples by lap-relative time, not derived distance.
- Uses interpolation or bucket aggregation when exact time matches are unavailable.
- Includes sector deltas in the summary.
- Returns `400` for invalid lap numbers or invalid `timeStepMs`.
- Returns `404` for missing session, driver, or lap, including a missing `sessionIdB`.
- When `sessionIdB` is set, both sessions must reference the same circuit; otherwise return `400` with code `IncompatibleCircuits`. Cross-season comparisons of the same circuit are the primary use case.

### 6.6 `GET /api/sessions/{sessionId}/replay/metadata`

Returns replay initialization data.

```http
GET /api/sessions/2024-monza-r/replay/metadata
```

```json
{
  "sessionId": "2024-monza-r",
  "startTimeUtc": "2024-09-01T13:00:00Z",
  "endTimeUtc": "2024-09-01T14:27:45Z",
  "durationMs": 5265000,
  "drivers": ["VER", "NOR", "LEC", "HAM"],
  "availableChannels": ["speed_kmh", "throttle_pct", "brake_pct", "gear", "rpm", "drs", "session_time_ms", "lap_time_ms", "track_status", "sample_source", "x", "y", "z"],
  "trackMap": {
    "rotationDegrees": 95.0,
    "outlineSource": "position_samples",
    "markers": [
      { "type": "corner", "number": 1, "letter": null, "x": -569.58, "y": 8153.72, "angleDegrees": 153.79, "distanceM": null },
      { "type": "marshal_light", "number": 1, "letter": null, "x": -1393.0, "y": -874.0, "angleDegrees": -166.01, "distanceM": null },
      { "type": "marshal_sector", "number": 1, "letter": null, "x": -1414.55, "y": -1183.94, "angleDegrees": 176.27, "distanceM": null }
    ]
  },
  "eventOverlays": {
    "trackStatus": true,
    "raceControlMessages": true,
    "weather": true
  },
  "weatherSummary": {
    "airTempMinC": 32.2,
    "airTempMaxC": 34.1,
    "trackTempMinC": 43.5,
    "trackTempMaxC": 54.6,
    "rainfallObserved": false
  },
  "recommendedChunkDurationMs": 30000,
  "supportedReplaySpeeds": [0.25, 0.5, 1, 2, 5, 10, 20],
  "defaultReplaySpeed": 1
}
```

Acceptance criteria:

- Uses actual minimum and maximum sample times.
- Returns only drivers that have telemetry samples.
- Returns circuit rotation and marker annotations when imported.
- Returns availability flags for weather, track-status, and race-control overlays.
- Returns compact weather summary statistics when weather samples exist.
- Does not include telemetry samples.

### 6.7 `GET /api/sessions/{sessionId}/replay/chunk`

Returns replay samples for a bounded session-relative time range.

Query parameters:

| Name | Required | Default | Description |
|---|---:|---|---|
| `fromMs` | Yes | | Milliseconds from session start |
| `durationMs` | Yes | | Chunk duration |
| `drivers` | No | all | Comma-separated driver subset |
| `channels` | No | all replay channels | Comma-separated channel allow-list |
| `sampleEvery` | No | `1` | Return every Nth sample |

Example:

```http
GET /api/sessions/2024-monza-r/replay/chunk?fromMs=60000&durationMs=30000&drivers=LEC,HAM&sampleEvery=2
```

Response:

```json
{
  "sessionId": "2024-monza-r",
  "fromMs": 60000,
  "durationMs": 30000,
  "nextFromMs": 90000,
  "items": [
    {
      "driverCode": "LEC",
      "samples": [
        {
          "offsetMs": 60012,
          "lapNumber": 2,
          "distanceM": 4312.4,
          "speedKmh": 298.2,
          "throttlePct": 100.0,
          "brakePct": 0.0,
          "gear": 8,
          "rpm": 11680,
          "drs": 0,
          "x": 1234.5,
          "y": -341.2,
          "z": 0.0
        }
      ]
    }
  ]
}
```

Validation:

- `fromMs` must be `>= 0`.
- `durationMs`: `1000` to `120000`.
- `sampleEvery`: `1` to `100`.
- Drivers must exist in the session.
- Unknown channels return `400`.

### 6.8 `GET /api/sessions/{sessionId}/replay/context`

Returns non-car replay context for a bounded session-relative time range. This powers weather readouts, track-status shading, safety-car/VSC markers, flag markers, and race-control message annotations.

Query parameters:

| Name | Required | Default | Description |
|---|---:|---|---|
| `fromMs` | Yes | | Milliseconds from session start |
| `durationMs` | Yes | | Context window duration |
| `includeWeather` | No | `true` | Include weather samples in range |
| `includeTrackStatus` | No | `true` | Include track-status events in range |
| `includeRaceControl` | No | `true` | Include race-control messages in range |

Example:

```http
GET /api/sessions/2024-monza-r/replay/context?fromMs=60000&durationMs=300000
```

Response:

```json
{
  "sessionId": "2024-monza-r",
  "fromMs": 60000,
  "durationMs": 300000,
  "weatherSamples": [
    {
      "sampleTimeMs": 86139,
      "airTempC": 33.2,
      "trackTempC": 52.1,
      "humidityPct": 37.0,
      "pressureMbar": 993.9,
      "rainfall": false,
      "windDirectionDeg": 207,
      "windSpeedMps": 1.0
    }
  ],
  "trackStatusEvents": [
    { "eventTimeMs": 792826, "statusCode": "1", "message": "AllClear" }
  ],
  "raceControlMessages": [
    {
      "sessionTimeMs": 3424000,
      "lapNumber": 1,
      "category": "Drs",
      "message": "DRS DISABLED",
      "status": "DISABLED",
      "flag": null,
      "scope": null,
      "sector": null,
      "racingNumber": null
    }
  ]
}
```

Acceptance criteria:

- Returns weather samples ordered by `sampleTimeMs`.
- Returns track-status events ordered by `eventTimeMs`.
- Returns race-control messages ordered by time, preserving original message text.
- Includes the last track-status event before `fromMs` when needed to determine the active status at the start of the window.
- Bounds `durationMs` to prevent unbounded timeline dumps.

### 6.9 `POST /api/sessions/{sessionId}/telemetry-events/search`

Finds simple telemetry events using a safe filter object. This powers AI questions such as hard-braking searches.

Request:

```json
{
  "drivers": ["LEC", "HAM"],
  "lapRange": { "from": 1, "to": 20 },
  "conditions": [
    { "channel": "brake_pct", "operator": ">=", "value": 80 },
    { "channel": "speed_kmh", "operator": ">=", "value": 280 }
  ],
  "maxResults": 100
}
```

Response:

```json
{
  "sessionId": "2024-monza-r",
  "items": [
    {
      "driverCode": "LEC",
      "lapNumber": 12,
      "sampleTimeUtc": "2024-09-01T13:14:41.200Z",
      "distanceM": 612.4,
      "values": {
        "brakePct": 91.0,
        "speedKmh": 283.4
      }
    }
  ]
}
```

Validation:

- `conditions` must not be empty.
- Channels must be allow-listed.
- Operators must be one of: `>`, `>=`, `<`, `<=`, `=`, `between`.
- `maxResults`: `1` to `1000`.
- Unbounded all-session searches require `maxResults <= 100`.

### 6.10 Analytical Primitive Endpoints

Natural-language clients must not answer broad analytical questions by fetching
all lap or race telemetry samples. The Query API must expose generic,
bounded analytical primitives that aggregate or compress telemetry in SQL and
return model-friendly tables, windows, rankings, and summaries.

These endpoints are not arbitrary SQL. They accept constrained filter,
grouping, metric, and condition vocabularies.

#### 6.10.1 `POST /api/sessions/{sessionId}/telemetry/aggregate`

Aggregates telemetry into compact rows.

Typical questions:

- DRS active time by driver/lap/stint.
- Average speed by stint or compound.
- Brake time by lap.
- Throttle lift count by race phase.

Request:

```json
{
  "drivers": ["LEC", "HAM"],
  "groupBy": ["driver", "stint", "compound"],
  "metrics": ["sample_count", "avg_speed_kmh", "max_speed_kmh", "drs_active_time_ms", "brake_time_ms"],
  "filters": {
    "lapRange": { "from": 1, "to": 53 },
    "compound": ["MEDIUM", "HARD"],
    "excludePitLaps": true,
    "trackStatus": ["track_clear"]
  },
  "timeBucketMs": null,
  "limit": 500
}
```

Response:

```json
{
  "sessionId": "2024-monza-r",
  "groupBy": ["driver", "stint", "compound"],
  "items": [
    {
      "driverCode": "LEC",
      "stintNumber": 1,
      "compound": "MEDIUM",
      "sampleCount": 14520,
      "avgSpeedKmh": 238.4,
      "maxSpeedKmh": 342.0,
      "drsActiveTimeMs": 184200,
      "brakeTimeMs": 41200
    }
  ]
}
```

Validation:

- `groupBy` values must be allow-listed: `driver`, `lap`, `stint`,
  `compound`, `sector`, `time_bucket`, `track_status`.
- `metrics` values must be allow-listed. Initial metrics:
  `sample_count`, `avg_speed_kmh`, `max_speed_kmh`, `avg_throttle_pct`,
  `avg_brake_pct`, `brake_time_ms`, `drs_active_time_ms`,
  `throttle_lift_count`, `high_speed_time_ms`.
- `timeBucketMs` is required when grouping by `time_bucket`.
- `limit`: `1` to `5000`.
- Responses must contain aggregate rows only, never raw telemetry samples.

#### 6.10.2 `POST /api/sessions/{sessionId}/telemetry/windows`

Detects contiguous telemetry windows and returns intervals instead of samples.

Typical questions:

- When was DRS active?
- Where were heavy braking zones?
- Where did a driver lift at high speed?

Request:

```json
{
  "drivers": ["LEC"],
  "eventType": "drs_active",
  "lapRange": { "from": 1, "to": 53 },
  "minimumDurationMs": 250,
  "includeNearestCorner": true,
  "limit": 1000
}
```

Response:

```json
{
  "sessionId": "2024-monza-r",
  "eventType": "drs_active",
  "items": [
    {
      "driverCode": "LEC",
      "lapNumber": 12,
      "startSessionTimeMs": 812340,
      "endSessionTimeMs": 818920,
      "startLapTimeMs": 12400,
      "endLapTimeMs": 18980,
      "durationMs": 6580,
      "nearestCorner": null,
      "summary": {
        "entrySpeedKmh": 286.2,
        "maxSpeedKmh": 331.4
      }
    }
  ]
}
```

Validation:

- `eventType` values must be allow-listed. Initial values:
  `drs_active`, `hard_braking`, `throttle_lift`, `high_speed`.
- `minimumDurationMs`: `0` to `10000`.
- `limit`: `1` to `5000`.
- Responses must return intervals/windows only, never per-sample rows.

#### 6.10.3 `POST /api/sessions/{sessionId}/stints/analyze`

Analyzes tyre stints from lap metadata and lap summaries, not raw telemetry.

Typical questions:

- Compare tyre degradation by driver and compound.
- Identify best/worst stints.
- Explain strategy shape across pit stops.

Request:

```json
{
  "drivers": ["LEC", "HAM"],
  "compound": ["MEDIUM", "HARD"],
  "excludePitLaps": true,
  "minimumLaps": 3,
  "metrics": ["lap_time_slope_ms_per_lap", "best_lap_time_ms", "average_lap_time_ms"]
}
```

Response:

```json
{
  "sessionId": "2024-monza-r",
  "items": [
    {
      "driverCode": "LEC",
      "stintNumber": 1,
      "compound": "MEDIUM",
      "firstLapNumber": 1,
      "lastLapNumber": 24,
      "laps": 24,
      "minTyreLife": 1,
      "maxTyreLife": 24,
      "averageLapTimeMs": 85620,
      "bestLapTimeMs": 83440,
      "lapTimeSlopeMsPerLap": 82.4
    }
  ]
}
```

Acceptance criteria:

- Uses lap/stint tables and `driver_stint_summaries` where possible.
- Supports excluding pit-in, pit-out, deleted, and inaccurate laps.
- Returns compact per-stint rows and optional rankings.
- Does not read or return raw telemetry unless a future metric explicitly
  requires telemetry aggregation, in which case it must still return aggregates.

#### 6.10.4 `POST /api/sessions/{sessionId}/strategy/summarize`

Produces a bounded strategy narrative for selected drivers by composing
existing stint, pit-stop, track-status, and race-control data. This endpoint
must not introduce new raw-data access; it aggregates results already
available from `driver_stint_summaries`, pit-stop analytics, and
`track_status_periods`/`race_control_event_index`.

Typical questions:

- Why did a driver pit on a given lap?
- Did a driver undercut or overcut a rival?
- How costly was a pit stop relative to the field?

Request:

```json
{
  "drivers": ["LEC", "HAM"],
  "compareToFieldAverage": true
}
```

Response:

```json
{
  "sessionId": "2024-monza-r",
  "items": [
    {
      "driverCode": "LEC",
      "stops": [
        {
          "lapNumber": 24,
          "fromCompound": "MEDIUM",
          "toCompound": "HARD",
          "pitLaneTimeMs": 23800,
          "fieldAveragePitLaneTimeMs": 24100,
          "trackStatusAtStop": "track_clear",
          "strategyLabel": "undercut",
          "rivalDriverCode": "HAM",
          "positionGainAfterStop": 1
        }
      ],
      "narrativeFacts": [
        "LEC pitted on lap 24 onto HARD, two laps before HAM.",
        "LEC's pit stop was 0.3s faster than the field average.",
        "The undercut gained LEC one position over HAM by lap 27."
      ]
    }
  ]
}
```

Validation:

- `drivers`: `1` to `10` driver codes that must exist in the session.
- `strategyLabel` values must be allow-listed: `undercut`, `overcut`,
  `reactive`, `scheduled`, `unknown`.
- Returns compact per-stop rows and short, deterministic narrative strings,
  never raw telemetry or position samples.
- Returns `404` for missing session or unknown driver codes.

#### 6.10.5 `POST /api/sessions/{sessionId}/debrief`

Generates a bounded, structured race debrief by composing the race/lap story,
weather trend, track-status/race-control timeline, and strategy-summary
endpoints into a single document-shaped response. The Query API returns
structured JSON; rendering to markdown or PDF is a client-side concern (MCP
client, desktop export, or a script under `scripts/`).

Request:

```json
{
  "drivers": ["LEC", "HAM", "VER"],
  "sections": ["overview", "incidents", "strategy", "weather"]
}
```

Response:

```json
{
  "sessionId": "2024-monza-r",
  "overview": {
    "winner": "VER",
    "headline": "VER wins from pole, LEC undercuts HAM for P2.",
    "lapCount": 53
  },
  "incidents": [
    { "lapNumber": 18, "type": "yellow_flag", "message": "YELLOW FLAG SECTOR 2" }
  ],
  "strategy": {
    "items": []
  },
  "weather": {
    "summary": "Dry, track temperature rising from 43C to 55C."
  }
}
```

Validation:

- `sections` values must be allow-listed: `overview`, `incidents`, `strategy`,
  `weather`.
- `drivers`: optional; when omitted, `strategy` covers the top-10 classified
  drivers only, to keep the response bounded.
- Returns `404` for missing session.
- Response size must stay bounded regardless of session length; no raw
  telemetry or position samples.

#### 6.10.6 `POST /api/sessions/{sessionId}/corners/compare`

Compares driver behavior at a specific corner using `circuit_markers` and the
`nearestCorner` attribution already returned by `telemetry/windows`
(§6.10.2). This is the corner-level extension of lap comparison: instead of a
full lap-time-aligned overlay, it returns a compact per-corner braking/exit
comparison.

Request:

```json
{
  "cornerNumber": 1,
  "drivers": ["LEC", "HAM"],
  "lapRange": { "from": 1, "to": 10 },
  "metrics": ["brake_point_distance_m", "entry_speed_kmh", "min_corner_speed_kmh", "exit_speed_kmh"]
}
```

Response:

```json
{
  "sessionId": "2024-monza-r",
  "cornerNumber": 1,
  "cornerLabel": "Turn 1, Variante del Rettifilo",
  "items": [
    {
      "driverCode": "LEC",
      "lapNumber": 5,
      "brakePointDistanceM": 612.4,
      "entrySpeedKmh": 342.0,
      "minCornerSpeedKmh": 87.0,
      "exitSpeedKmh": 198.2
    }
  ],
  "summary": {
    "averageBrakePointDeltaM": 4.8,
    "fastestMinCornerSpeedDriver": "LEC"
  }
}
```

Validation:

- `cornerNumber` must reference an imported `circuit_markers` row of type
  `corner` for the session; otherwise return `404`.
- `metrics` values must be allow-listed: `brake_point_distance_m`,
  `entry_speed_kmh`, `min_corner_speed_kmh`, `exit_speed_kmh`.
- Requires `telemetry/windows` corner attribution (`includeNearestCorner`) to
  be available for the session; sessions without circuit metadata return
  `409` with code `CornerDataUnavailable`.
- `lapRange` is required and bounded to `100` laps total across all drivers.
- Returns per-lap rows plus a compact cross-driver summary, never raw
  telemetry samples.

### 6.11 `GET /api/sessions/{sessionId}/standings`

Returns a compact, sortable field snapshot for the all-driver Field View / timing
tower. One row per driver, ordered by classified position. Computed from `laps`,
`lap_summaries`, and `driver_stint_summaries` — never raw telemetry.

Query parameters:

| Name | Required | Default | Description |
|---|---:|---|---|
| `atLap` | No | last | Standings as of this lap number |
| `sortBy` | No | `position` | One of `position`, `last_lap_ms`, `best_lap_ms`, `gap_ms`, `pit_count` |

Example:

```http
GET /api/sessions/2024-monza-r/standings?atLap=40&sortBy=position
```

Response:

```json
{
  "sessionId": "2024-monza-r",
  "atLap": 40,
  "items": [
    {
      "position": 1,
      "driverCode": "VER",
      "fullName": "Max Verstappen",
      "teamName": "Red Bull",
      "gapToLeaderMs": 0,
      "intervalMs": 0,
      "lastLapMs": 81450,
      "bestLapMs": 81450,
      "isSessionBestLap": true,
      "isPersonalBestLap": true,
      "compound": "HARD",
      "tyreLife": 29,
      "pitCount": 1,
      "status": "running",
      "recentLapMs": [81980, 81620, 81700, 81510, 81450]
    }
  ]
}
```

Acceptance criteria:

- Returns every driver in the session, including retired drivers with `status` of `out`.
- `gapToLeaderMs` and `intervalMs` are derived from cumulative lap times up to `atLap`.
- `isSessionBestLap` / `isPersonalBestLap` flag the fastest classified laps for the requested scope.
- `recentLapMs` is bounded to the last 5 laps for the pace sparkline.
- Returns `404` for missing session; `400` for invalid `sortBy` or out-of-range `atLap`.

### 6.12 `GET /api/sessions/{sessionId}/positions`

Returns lap-by-lap classified position per driver for the Position-Trace
("race trace") view.

Query parameters:

| Name | Required | Default | Description |
|---|---:|---|---|
| `drivers` | No | all | Comma-separated driver subset |
| `fromLap` | No | `1` | First lap (inclusive) |
| `toLap` | No | last | Last lap (inclusive) |

Response:

```json
{
  "sessionId": "2024-monza-r",
  "fromLap": 1,
  "toLap": 53,
  "items": [
    { "driverCode": "VER", "positions": [4, 3, 3, 2, 1, 1, 1] }
  ]
}
```

Acceptance criteria:

- `positions[i]` aligns to lap `fromLap + i`; missing classification is `null`.
- Returns aggregate rows only; one short array per driver, never telemetry samples.

### 6.13 `GET /api/sessions/{sessionId}/incidents`

Returns a unified, location-aware incident list for the Track Incidents view,
joining `track_status_events`, `race_control_messages`, and the
`telemetry_event_candidates` hard-braking helper view, with corner attribution
from `circuit_markers`.

Query parameters:

| Name | Required | Default | Description |
|---|---:|---|---|
| `types` | No | all | Subset of `safety_car`, `vsc`, `yellow`, `red`, `clear`, `drs`, `hard_braking`, `off_track`, `spin` |
| `minBrakingG` | No | `4.0` | Threshold for `hard_braking` items |
| `maxResults` | No | `200` | `1`–`1000` |

Response:

```json
{
  "sessionId": "2024-monza-r",
  "items": [
    {
      "type": "safety_car",
      "lapNumber": 8,
      "sessionTimeMs": 842800,
      "message": "Safety car deployed — debris",
      "nearestCorner": { "number": 1, "label": "Turn 1, Variante del Rettifilo" },
      "x": -1234.5,
      "y": 8153.7,
      "driverCode": null,
      "severity": "high",
      "metrics": null
    },
    {
      "type": "hard_braking",
      "lapNumber": 29,
      "sessionTimeMs": 2515300,
      "message": "Peak braking 5.1 g",
      "nearestCorner": { "number": 11, "label": "Parabolica" },
      "x": 8120.4,
      "y": -341.2,
      "driverCode": "VER",
      "severity": "info",
      "metrics": { "peakBrakingG": 5.1, "entrySpeedKmh": 342.0, "minSpeedKmh": 198.0 }
    }
  ],
  "summary": { "incidentCount": 6, "hardestBrakingG": 5.1, "lapsUnderSafetyCar": 3 }
}
```

Acceptance criteria:

- Each item carries `x`/`y` from the nearest `position_samples`/`circuit_markers` point so the desktop can place it on the data-derived outline.
- Hard-braking items derive from bounded aggregate/window queries, not raw sample dumps.
- `summary` reports incident count, hardest braking load, and laps lost under SC/VSC.
- Returns `400` for unknown `types` values; `maxResults` is clamped.

### 6.14 `POST /api/sessions/{sessionId}/tires/degradation`

Forecasts per-stint tyre degradation and recommends a pit window. Trained/fit
over `lap_summaries` and `driver_stint_summaries` (lap time, compound, tyre age,
fuel-corrected pace); returns measured points plus a bounded forward projection
with a confidence band. This is the predictive extension of `stints/analyze`
(§6.10.3) and `strategy/summarize` (§6.10.4).

Request:

```json
{
  "drivers": ["LEC"],
  "stint": 1,
  "horizonLaps": 12,
  "fuelCorrect": true
}
```

Response:

```json
{
  "sessionId": "2024-monza-r",
  "items": [
    {
      "driverCode": "LEC",
      "stintNumber": 1,
      "compound": "MEDIUM",
      "measured": [
        { "lapNumber": 12, "lapTimeMs": 83440, "tyreLife": 12 }
      ],
      "forecast": [
        { "lapNumber": 25, "predictedLapTimeMs": 84120, "lowerMs": 84020, "upperMs": 84260 }
      ],
      "degradationMsPerLap": 82.4,
      "predictedCliffLap": 29,
      "recommendedPitWindow": { "fromLap": 24, "toLap": 27 },
      "undercutThreat": "high",
      "modelVersion": "deg_v3",
      "confidence": 0.86
    }
  ]
}
```

Validation:

- `horizonLaps`: `1`–`30`.
- Responses must clearly separate `measured` from `forecast`; the desktop renders forecast as dashed inside the band.
- Returns aggregate/forecast rows only, never raw telemetry.
- Returns `404` for missing session/driver/stint.

### 6.15 `GET /api/sessions/{sessionId}/weather-correlation`

Returns weather, track-status, and race-control series aligned on the
session-relative timebase, plus detected correlations (e.g. SC deployed N laps
after rainfall crossed a threshold) for the Incident × Weather timeline.

Query parameters:

| Name | Required | Default | Description |
|---|---:|---|---|
| `rainThreshold` | No | `0.0` | Rainfall flag/intensity threshold for correlation detection |

Response:

```json
{
  "sessionId": "2024-monza-r",
  "lanes": {
    "trackStatus": [ { "fromLap": 8, "toLap": 11, "status": "safety_car" } ],
    "raceControl": [ { "lapNumber": 12, "category": "Drs", "message": "DRS ENABLED" } ],
    "rainfall": [ { "fromLap": 42, "toLap": 49 } ],
    "trackTempC": [ { "lapNumber": 40, "value": 52.1 } ]
  },
  "correlations": [
    {
      "summary": "Rainfall crossed threshold at L42; safety car deployed L44.",
      "lagLaps": 2,
      "trigger": { "type": "rainfall", "lapNumber": 42 },
      "effect": { "type": "safety_car", "lapNumber": 44 }
    }
  ]
}
```

Acceptance criteria:

- Weather is presented stepped/nearest, never interpolated as high-frequency data.
- Correlation detection is bounded and rule-based (lag windows over imported series), not free-form inference.

### 6.16 `GET /api/compare/sessions`

Cross-session lap comparison: the same circuit across two seasons (or two
sessions) for one driver, or two drivers across two sessions. Reuses the
lap-relative alignment and delta convention of `/compare/laps` (§6.5) but pins
circuit identity instead of a single `sessionId`. Powers the Session-Diff and
Ghost-Car overlays.

Query parameters:

| Name | Required | Example |
|---|---:|---|
| `sessionA` | Yes | `2024-monza-r` |
| `driverA` | Yes | `LEC` |
| `lapA` | Yes | `best` or a lap number |
| `sessionB` | Yes | `2025-monza-r` |
| `driverB` | Yes | `LEC` |
| `lapB` | Yes | `best` |
| `channels` | No | `speed_kmh,throttle_pct,brake_pct` |
| `timeStepMs` | No | `100` |

Acceptance criteria:

- Both sessions must share the same `circuit_name`; otherwise return `400` with a `CircuitMismatch` error code.
- Response shape matches `/compare/laps` (§6.5) plus `sessionA`/`sessionB` identifiers.
- Supports `lap=best` resolution from `lap_summaries`.
- Ghost-car mode additionally returns aligned `position_samples` for both laps so the track map can animate two cars on one outline (§8.14, §8.15).

---

## 7. Replay Requirements

Replay is client-driven. The backend returns chunks over REST; the .NET MAUI
desktop app controls playback locally.

### 7.1 Replay Model

```text
sampleOffsetMs = sample_time_utc - session_start_utc
replayClockDeltaMs = realElapsedMs * replaySpeed
```

Supported speeds:

```text
0.25x, 0.5x, 1x, 2x, 5x, 10x, 20x
```

### 7.2 Replay UI Controls

The replay screen must provide:

- Play.
- Pause.
- Restart.
- Timeline seek.
- Speed selector.
- Driver selector.
- Channel selector.
- Current replay time.
- Current lap per selected driver.
- Track-map position updates.
- Time-series chart updates.
- Current weather readout at the replay timestamp.
- Track-status and race-control timeline overlays.

### 7.3 Track Map

The track map must be generated from imported data, not external track assets.

The desktop app must:

1. Build the track outline from `position_samples` returned by the backend or from a backend-provided outline derived from those samples.
2. Normalize source `x`/`y` coordinates to fit the viewport while preserving aspect ratio.
3. Apply `circuit_metadata.rotation_degrees` when available.
4. Overlay `circuit_markers` for corners, marshal lights, and marshal sectors when available.
5. Animate selected driver markers using replay chunk position samples.

Circuit annotations are optional at runtime: replay must still work when FastF1 circuit metadata is missing.

### 7.4 Context Timeline

The replay workspace should treat weather, flags, safety-car periods, virtual-safety-car periods, red flags, DRS status messages, and race-control messages as contextual overlays.

The desktop app must:

1. Load replay context through `/api/sessions/{sessionId}/replay/context` for the visible/buffered time window.
2. Show the nearest weather sample at the current replay timestamp.
3. Shade chart backgrounds and the timeline for yellow, safety car, virtual safety car, and red flag periods.
4. Display race-control messages as timeline markers with concise tooltips/details.
5. Mark rainfall periods when `weather_samples.rainfall` is true.
6. Keep contextual overlays synchronized with seek and replay speed changes.

Weather is low-frequency context, not car telemetry. The UI should not interpolate it as if it were high-frequency driver data; it should show nearest or stepped values.

### 7.5 Chunk Loading

The desktop app must:

1. Call `/api/sessions/{sessionId}/replay/metadata` when a session opens.
2. Load the first replay chunk before playback starts.
3. Keep at least one future chunk buffered during playback.
4. Cancel in-flight chunk requests when the user seeks.
5. Load the chunk that contains the new seek position.
6. Load or refresh the matching replay context window.
7. Downsample chart data when the visible range has too many points.
8. Preserve `null` values from the backend.

### 7.6 Replay Acceptance Criteria

Given an imported session:

- The user can select at least two drivers and press Play.
- Charts and track positions update at `1x`.
- The track outline is visible before playback starts when position samples exist.
- Corner markers are visible when circuit metadata exists.
- Weather readout updates as replay time advances.
- Safety-car, VSC, yellow, and red-flag periods are visible as timeline/chart overlays when event data exists.
- Race-control messages appear as inspectable timeline markers when message data exists.
- The user can switch from `1x` to `5x` while replay is running.
- Pause and resume keep the same replay position.
- Seeking to the middle of the session reloads the correct chunk.
- Replay remains usable when one driver has missing samples.

### 7.7 Linked Timebase And Cursor

Replay displays must share a common session-relative timebase unless the user
explicitly opens a comparison view with lap-relative alignment.

The desktop app must:

1. Keep track map, waveform chart, numeric readouts, summary selections, event
   markers, and context overlays synchronized to the same replay timestamp.
2. Support a cursor timestamp that can be moved by playback, timeline seek,
   chart click/drag, event selection, or track-map selection.
3. Show the cursor timestamp as both session-relative time and, when possible,
   driver/lap-relative time for the selected driver.
4. Support a reference cursor in analysis views so the UI can show current
   values and deltas between the cursor and reference timestamp.
5. Use `session_time_ms` for replay synchronization and `lap_time_ms` only for
   lap-comparison alignment.
6. Avoid inventing samples when data is missing; displays should leave gaps or
   show unavailable values rather than interpolating across backend `null`
   values.

### 7.8 Display Interaction Requirements

All replay displays should support a small shared interaction vocabulary:

- Hover or focus shows the nearest timestamp and values for selected drivers.
- Clicking a chart point, track position, lap row, or event row seeks the common
  cursor when the target has a timestamp.
- Zooming a time-series display changes only the visible time window, not the
  underlying replay clock.
- Reset zoom returns to the active replay window.
- Channel visibility, driver visibility, and color assignment are local UI
  state and must not mutate imported telemetry.
- Export and copy actions, when added, must export derived project views or
  tabular data from the local database.

---

## 8. .NET MAUI Desktop App

### 8.1 Technology

```text
.NET MAUI for desktop-first UI
CommunityToolkit.Mvvm
SkiaSharp or MAUI Graphics for high-performance track, waveform, and timeline rendering
Virtualized native list/table controls for lap, driver, and event rows
System.Text.Json source-generated serializers where useful
```

The MAUI app should target desktop ergonomics first. Mobile layouts are out of
scope unless a later decision explicitly adds them.

Chart-like replay surfaces should prefer custom retained data models plus
batched drawing over per-sample UI elements. Track maps, waveforms, context
strips, and dense telemetry charts must render from downsampled or windowed
data sized to the viewport.

### 8.2 Product Slices

The desktop app should be built in opinionated slices. The first version should
make a few workflows excellent instead of exposing a configurable analytics
toolkit too early.

#### Version 1 - Replay And First Analysis

Version 1 must provide:

1. Session Browser.
2. Replay Workspace.
3. Lap Comparison.

Version 1 must be opinionated:

- One fixed workspace layout.
- Race sessions shown by default.
- Two selected drivers in replay by default.
- Core channels selected by default: `speed_kmh`, `throttle_pct`, `brake_pct`,
  `gear`, and `rpm`.
- DRS and weather shown as context rather than primary charts by default.
- Track-status, race-control, and rainfall context visible when imported.
- Lap comparison limited to two laps in one session.
- Pit analysis limited to summary cards and timeline markers.
- No saved layouts, plugin displays, arbitrary channel formulas, video sync, or
  live telemetry ingestion.
- No full-session raw telemetry rendering in the UI. Visible charts must use
  replay chunks, lap comparison samples, or bounded aggregate responses.

Version 1 should make these questions fast to answer:

- What happened during this race window?
- Where are the selected drivers on track now?
- What lap is each selected driver on?
- How do speed, throttle, brake, gear, and RPM behave around the cursor?
- Which laps were fastest, pit-in, pit-out, deleted, or unusual?
- How does one driver's lap compare to another lap?
- How did pit stops and safety-car/VSC periods affect the selected drivers?

#### Later Analysis Modules

Later versions may add:

- Saved and rearrangeable workspaces.
- Driver profile view with stint, tyre, lap-time, and consistency summaries.
- Pit analysis view with stop duration, pit-lane loss, undercut/overcut
  context, and before/after stint pace.
- Multi-driver lap ranking and mini-sector style comparison.
- Cross-session lap comparison.
- Histogram, load-map, and scatter displays backed by bounded aggregate
  endpoints.
- Event search builder for telemetry windows such as hard braking, throttle
  lifts, DRS usage, and high-speed periods.
- Local notes and bookmarks.
- Export of project-owned charts and tables.

### 8.3 Screens

The desktop app is organized as a keyboard-first **session console** (§8.11)
whose left view rail switches between analysis views. The required screens and
views are:

1. Home / Launcher (§8.11) — circuit → session → driver selection, recent sessions, and the global command palette.
2. Session Browser (§8.4) — searchable imported-session list with context-availability flags.
3. Replay Workspace (§8.5).
4. Lap Comparison (§8.6).
5. Field View — all drivers / timing tower (§8.13).
6. Track Incidents and Hard-Braking (§8.14).
7. Strategy view — tire-strategy gantt (§8.15).
8. Lap Analysis — position trace (§8.15).
9. Cross-Session Comparison — session diff / ghost car (§8.15).
10. Optional AI Assistant Panel (§8.10).

Version 1 must ship at least screens 1–4; the remaining views build on the same
panel components and Query API contracts and are sequenced in §13. Every view
must be built as an independent panel component sharing the linked timebase
(§7.7) and selection state so views can be added, saved, hidden, or rearranged
without reshaping the whole UI.

### 8.4 Session Browser

Fields:

- Season.
- Event.
- Session type.
- Imported timestamp.
- Number of drivers.
- Number of laps.
- Available context flags: position, circuit markers, weather, track status,
  race-control messages.

Actions:

- Refresh sessions.
- Open selected session.
- Search by season, event, circuit, and session type.
- Filter to race sessions by default.
- Show selected-session details in a property panel without requiring the user
  to open the replay workspace.

### 8.5 Replay Workspace

Must show:

- Track map with selected driver positions and circuit markers when available.
- Time-series chart for selected channels.
- Timeline overlays for track status, race-control messages, and rainfall when available.
- Current weather readout with air temperature, track temperature, rain state, and wind.
- Replay controls.
- Driver list.
- Current lap and speed for selected drivers.

The first implementation can use a fixed docked layout, but it must be
structured as independent panels so the user can later save, hide, resize, or
rearrange analysis displays.

Required panels:

- **Track Map:** data-derived circuit outline, selected driver markers, current
  cursor position, sector/corner/marshal annotations when available, and
  zoom-to-fit/reset controls.
- **Waveform:** stacked or overlaid time-series for selected channels, visible
  units, channel colors, current cursor line, optional reference cursor,
  per-channel current values, track-status shading, lap boundaries, and sector
  markers when available. Rendering must be viewport-aware and avoid one visual
  element per telemetry sample.
- **Current Values:** compact readouts for selected driver speed, throttle,
  brake, gear, RPM, DRS, lap number, lap-relative time, and session-relative
  time.
- **Lap Summary:** lap table for selected drivers with lap time, sectors, tyre
  compound/life, pit flags, and min/max values for selected channels when
  available from analytical endpoints.
- **Event Timeline:** searchable/filterable list for track status, race-control
  messages, pit events, and telemetry-event candidates. Selecting an event must
  seek the replay cursor when the event has a timestamp.
- **Context Strip:** a compact timeline strip that shows laps, flags, safety
  car/VSC/red-flag periods, rainfall periods, and race-control markers.
- **Pit Summary:** compact cards for selected drivers showing pit lap, compound
  change, pit-in/pit-out flags, and available pit-stop analysis metrics.

Optional later panels:

- **Histogram:** distribution of one selected channel over the visible window,
  backed by aggregate queries rather than client-side full-session scans.
- **Load Map:** two-channel bucket view such as speed versus gear or speed
  versus throttle, backed by bounded aggregate queries.
- **Scatter Plot:** relation between two selected channels for a bounded lap or
  visible replay window.
- **Driver Profile:** stint pace, tyre life, consistency, best/worst lap, and
  pit strategy summary for one selected driver.
- **Pit Analysis:** detailed stop table, pit-lane loss estimates, before/after
  pace comparison, and undercut/overcut context when enough data exists.
- **Notes:** local free-text session notes.

### 8.6 Lap Comparison

Inputs:

- Driver A.
- Lap A.
- Driver B.
- Lap B.
- Channels.

Default channels:

```text
speed_kmh, throttle_pct, brake_pct, gear, rpm
```

Expected behavior:

- Calls `/api/sessions/{sessionId}/compare/laps`.
- Displays lap-time-aligned overlay charts.
- Displays lap-time delta and sector deltas.
- Uses the delta convention `driverA - driverB`.
- Shows a cursor and optional reference cursor over the aligned lap timeline.
- Shows per-channel values and deltas at the cursor.
- Shows lap metadata for both selected laps: lap time, sectors, tyre compound,
  tyre life, pit/deleted/inaccurate flags when available.
- Supports comparing two drivers, two laps from one driver, or two imported
  sessions via `compare/laps`'s `sessionIdB` parameter (§6.5), for example the
  same circuit across two seasons. The first implementation may limit the UI
  entry point to one session and add a second-session picker as a later
  module, but the backend contract already supports it.
- Keeps distance-based alignment out of scope until the data path can derive a
  reliable distance or position-aware alignment model.

### 8.7 Driver And Pit Analysis Requirements

Version 1 should expose driver and pit analysis as compact, opinionated
summaries inside the Replay Workspace, not as separate full screens.

Driver summary cards should show:

- Current position in the replay window when position data is available.
- Current lap, tyre compound, tyre life, and lap-relative time.
- Best lap, latest completed lap, and selected-window average lap time when
  available.
- Current speed, throttle, brake, gear, RPM, and DRS state.

Pit summary cards should show:

- Pit-in and pit-out laps.
- Compound change and tyre-life reset where imported lap metadata supports it.
- Stop duration or pit-lane time loss when returned by the pit analysis
  endpoint.
- Nearby safety-car, VSC, yellow, red-flag, and race-control context.

Later dedicated analysis screens may expand these summaries with stint pace,
pit-loss ranking, undercut/overcut context, and driver-to-driver strategy
comparison.

### 8.8 Display Styling And Assets

The UI must implement the project-owned **Carbon Signal** design system,
documented in `docs/design-system/`:

- `docs/design-system/DESIGN_SYSTEM.md` — the authoritative description of the system.
- `docs/design-system/design-tokens.json` / `design-tokens.css` — the single source of truth for all colors, typography, spacing, radius, elevation, and motion values.
- `docs/design-system/styleguide.html` — an interactive reference rendering every token, component, and view.

Carbon Signal is an original "warm carbon" dark analysis theme: warm graphite
surfaces (never pure black), a single punchy **signal-amber** accent reserved
for primary action, selection, focus, and the replay cursor, and an original,
colorblind-safe telemetry-channel palette that never borrows real team liveries.

The MAUI app must consume these tokens rather than hardcoding values: generate a
`Theme.Carbon.xaml` `ResourceDictionary` from `design-tokens.json` so the app,
charts, and any documentation surfaces never drift. SkiaSharp / MAUI Graphics
rendering for the track map, waveform, gantt, position trace, and timelines must
read channel, flag, grid, and heatmap tokens from this theme.

Required styling rules (from the design system):

1. Amber is never used for a telemetry trace — it is chrome only, so the cursor never competes with data.
2. Channel hue is reinforced with a dash pattern (gear `4 3`, rpm `6 3`, drs `2 3`) so traces survive grayscale and color-vision deficiency.
3. All numeric telemetry uses the monospaced, tabular figure font so columns do not shift during replay.
4. Delta sign is encoded with green/red **and** a leading `+`/`−` glyph; flags carry an LED dot **and** a text label — no meaning rests on color alone.
5. Flags expose both a marker color and a low-alpha period shade for chart-background and timeline overlays.
6. The replay render loop must not use CSS/animation transitions; motion tokens apply to chrome only and collapse to zero under reduced-motion.
7. Driver identity uses the original categorical palette by default; a real-team-livery mode, if added, must be an explicit opt-in.

Project documentation may include original generated mockups or diagrams that
communicate equivalent concepts (synchronized replay workspace; track map with
corner and sector annotations; waveform with cursor/reference delta; event
timeline with severity/status filters; lap comparison with sector and lap
deltas; field-view timing tower; incident map). The data-derived track outline
must come from imported `position_samples` (the styleguide uses a real Monza
lap), never an external track asset. Generated mockups are project-owned
illustrative assets.

### 8.9 Desktop Performance Requirements

High performance is a core product requirement for the desktop app.

The MAUI app must:

1. Keep replay interaction responsive while playback is running.
2. Avoid blocking the UI thread on HTTP calls, JSON parsing, database-facing
   requests, downsampling, or derived metric calculations.
3. Use cancellation tokens for seek, session switch, driver switch, and channel
   switch operations.
4. Virtualize large row sets such as laps, events, race-control messages, and
   telemetry-event candidates.
5. Keep chart, track-map, and timeline rendering bounded by visible pixels and
   visible time range, not by total imported sample count.
6. Reuse drawing resources and avoid per-frame allocations in replay render
   loops where practical.
7. Cache replay metadata, selected-session static context, and recently used
   chunks in memory with explicit size limits.
8. Preserve backend downsampling and `null` semantics instead of expanding data
   client-side.
9. Degrade gracefully on large sessions by reducing chart detail before
   dropping replay controls or cursor interaction.
10. **Eagerly prefetch a session on open.** When a session is selected or opened,
    the app must warm an in-memory snapshot of all session-scoped, bounded data
    in parallel — drivers, replay metadata, standings, incidents, positions, and
    per-driver lap summaries — so that switching between views (Field, Strategy,
    Lap analysis, Incidents, …) reads from memory and is effectively instant.
    Prefetch must: start warming on session selection (before open); share one
    in-flight request per session so priming and opening never double-fetch; use
    bounded concurrency for per-driver calls; never let a view switch cancel a
    warm another view is about to await; degrade gracefully so a failed sub-fetch
    leaves the rest of the snapshot usable; and bound the number of cached
    sessions. High-volume replay chunks remain streamed/windowed on demand (they
    are not part of the eager snapshot).

### 8.10 AI Assistant Panel

The AI Assistant Panel is optional in the first UI iteration. The MCP server must work first from an external MCP-compatible client.

When implemented, the panel should support questions such as:

```text
Compare Leclerc lap 12 and Hamilton lap 14.
Where did LEC lose time against HAM?
Find LEC braking events above 80 percent in the first 10 laps.
Can I replay the Monza race and which drivers are available?
```

The panel should show the answer and, when applicable, offer an action to open the corresponding replay or lap comparison screen.

### 8.11 Application Shell, Navigation, And Command Model

The desktop app is a keyboard-first **session console** for race engineers, not a
casual dashboard. It must not use a generic horizontal tab strip. The shell has
three persistent regions that stay in place across every view so the engineer
never loses session context or keyboard focus.

1. **Command bar** (top): a monospace session breadcrumb (`{year} / {event-code} / {session} / {session-id}`) with a load indicator, an **always-on search/query input**, and actions that each display their shortcut. The search input doubles as the telemetry-event query entry (e.g. `brake > 80 in S2`).
2. **View rail** (left): a vertical list of the views in §8.3, each with an icon, label, and number-key shortcut (`1`–`9`). The active view takes the amber left-border and muted fill. The rail replaces the tab strip.
3. **Instrument HUD** (per session): a compact strip of monospaced label-over-value metric cells divided by hairlines (pit stops, average stops, most-used compound, laps, SC/VSC count, driver count, conditions). Sized like a status line, not hero cards.

The app must also provide:

- **Home / Launcher**: a circuit → session → driver funnel. Circuit cards may show a national flag (a factual identifier; team liveries are not permitted). Selecting a circuit, then a session, then drivers, then a primary action (e.g. Open replay) commits the choice.
- **Command palette**: a global launcher on `⌘K` / `Ctrl+K` (and `/` to focus search) that fuzzy-matches imported sessions, drivers, and quick actions, with grouped results and an `Enter`-to-open affordance.
- **Driver multi-select**: a grid of toggleable driver chips (categorical team-free rail, checkbox affordance, code, name, position) with select-all / clear and a live count. Selection is local UI state and must never mutate imported data.

### 8.12 Keyboard And Shortcut Model

Fast keyboard operation is a first-class requirement. The app must expose a
discoverable, always-available shortcut model and a cheat-sheet on `?`. The
initial required bindings:

| Action | Binding |
|---|---|
| Command palette | `⌘K` / `Ctrl+K` |
| Focus search / filter | `/` |
| Switch view | `1`–`9` |
| Play / pause replay | `Space` |
| Step frame back / forward | `←` / `→` |
| Set / clear reference cursor | `R` |
| Add / remove selected driver | `D` |
| Toggle channel visibility | `C` |
| Jump to next / previous incident | `N` / `Shift+N` |
| Export · Save view | `E` · `S` |
| Show shortcut cheat-sheet | `?` |

Shortcuts must work from any view, must not conflict with text entry in the
search field, and must be listed in one place (`?`) rather than hidden in menus.

### 8.13 Field View — All Drivers (Timing Tower)

The Field View is the engineer's default situational-awareness screen, backed by
`GET /api/sessions/{sessionId}/standings` (§6.11). It must:

1. Show every driver in the session at once in a dense, virtualized timing tower.
2. Provide columns: position, driver (categorical team rail + code + name), gap to leader, interval, last lap, best lap, tyre compound + age, pit count, a five-lap pace sparkline, and a running/out status dot.
3. Color the last/best lap as session-best and personal-best per the design-system delta/highlight tokens.
4. Be sortable on any column and filterable by driver/team via `/`.
5. Let the user pin a row to comparison and add a driver to replay with `D`.
6. Offer at least Tower, Grid, and Gaps presentations of the same data.
7. Update with the replay cursor when a lap boundary is crossed; never block playback.

### 8.14 Track Incidents And Hard-Braking View

A spatial situational-awareness view backed by
`GET /api/sessions/{sessionId}/incidents` (§6.13). It must:

1. Render incidents and hard-braking hotspots on the data-derived track outline (§7.3) using each item's `x`/`y` and corner attribution.
2. Use a colored glyph per incident type (safety car, VSC, yellow, red, spin, off-track) and amber heat dots sized by braking load.
3. Show a synced incident list (timestamp, glyph, message, lap/location); selecting a list row highlights the map marker and seeks the replay cursor when the item has a timestamp, and vice-versa.
4. Provide filter toggles per incident type and a hard-braking threshold control.
5. Show a compact summary (hardest braking g, incident count, laps lost under SC/VSC).

### 8.15 Strategy, Position-Trace, And Cross-Session Views

These views reuse existing analytical contracts and the linked timebase:

- **Strategy view (tire gantt):** one row per driver, stints as compound-colored bars on a shared lap axis with stint length labelled and pit boundaries at segment edges. Backed by `strategy/summarize` (§6.10.4) and lap/stint summaries. Selecting a stint may open the degradation/pit-window predictor (§6.14) and the Strategy / pit-loss narrative.
- **Lap Analysis (position trace):** a race-trace chart of classified position over laps, one line per driver in the categorical palette, backed by `GET /api/sessions/{sessionId}/positions` (§6.12). Crossings read as overtakes and pit cycles; the legend toggles visibility.
- **Cross-Session Comparison (session diff / ghost car):** the same circuit across two seasons (or two sessions), backed by `GET /api/compare/sessions` (§6.16). The lap-relative overlay recolors the pair to a neutral past-year tone against the amber current year so it never reads as a live driver duel. In **ghost-car** mode the track map animates two cars on one outline using both laps' position samples (§7.3, §7.7).
- **Strategy / pit-loss narrative** and **Tire degradation & pit-window predictor:** surfaced as cards in the Strategy view, backed by `strategy/summarize` (§6.10.4) and `tires/degradation` (§6.14); measured data is solid, forecasts are dashed inside a confidence band.
- **Incident × weather correlation** and **Exportable session report:** the correlation timeline is backed by `weather-correlation` (§6.15); the session report is a client-side templating feature composing existing Query API responses into a shareable PDF/HTML, with the on-screen race debrief and the exported report sharing one renderer at two levels of detail.

---

## 9. MCP Query Server

### 9.1 Technology

```text
.NET 10
ModelContextProtocol C# SDK
HTTP transport for local Aspire execution
Streamable HTTP transport for coding-agent integration
Aspire service defaults
```

### 9.2 Responsibilities

The MCP server must:

- Expose read-only tools.
- Validate inputs before calling shared Query API/data contracts.
- Stay in capability parity with Query API analytical routes.
- Return compact, bounded, model-friendly JSON.
- Prefer summary, aggregate, window, and context endpoints for analytical
  questions over raw sample retrieval.
- Emit one trace span per tool call.

The MCP server must not:

- Connect directly to TimescaleDB.
- Execute arbitrary SQL.
- Write data.
- Return unbounded telemetry samples.

MCP-backed analytics must be answered from Query API endpoints that use
TimescaleDB SQL, indexes, and analytical views/materialized views. The MCP
server is an adapter over the same capabilities and contracts, not an
independent analytical database client.

### 9.2.1 Analytical Tool Design

The MCP server must not add a new special-purpose tool for every natural
language question. Instead it should expose a small set of generic, safe
analytical primitives that map directly to Query API routes:

| MCP tool | Query API route | Purpose |
|---|---|---|
| `aggregate_telemetry` | `POST /api/sessions/{sessionId}/telemetry/aggregate` | Return grouped telemetry metrics such as DRS active time, brake time, average speed, max speed, and sample counts. |
| `detect_telemetry_windows` | `POST /api/sessions/{sessionId}/telemetry/windows` | Return contiguous event intervals such as DRS activation, hard braking, throttle lifts, and high-speed periods. |
| `analyze_driver_stints` | `POST /api/sessions/{sessionId}/stints/analyze` | Return tyre/stint degradation, best/worst lap, average lap time, tyre-life range, and strategy facts. |
| `summarize_strategy` | `POST /api/sessions/{sessionId}/strategy/summarize` | Return pit-stop timing, undercut/overcut labels, pit-lane loss vs. field average, and short narrative facts. |
| `generate_race_debrief` | `POST /api/sessions/{sessionId}/debrief` | Return a bounded, section-based race summary (overview, incidents, strategy, weather) for export or chat display. |
| `compare_corners` | `POST /api/sessions/{sessionId}/corners/compare` | Return per-corner braking/exit comparison across drivers using circuit-marker attribution. |
| `get_standings` | `GET /api/sessions/{sessionId}/standings` | Return the classified field at a lap: position, gap, interval, last/best lap, tyre, pits, status. |
| `list_incidents` | `GET /api/sessions/{sessionId}/incidents` | Return location-aware incidents and hard-braking hotspots with corner attribution and a summary. |
| `predict_pit_window` | `POST /api/sessions/{sessionId}/tires/degradation` | Return per-stint degradation forecast, recommended pit window, predicted cliff lap, and undercut threat. |
| `correlate_incidents_weather` | `GET /api/sessions/{sessionId}/weather-correlation` | Return weather/track-status/race-control lanes plus detected lag correlations (e.g. rain → safety car). |
| `compare_sessions` | `GET /api/compare/sessions` | Return a cross-session lap comparison (same circuit, two seasons/sessions) reusing the lap-comparison contract. |

These tools are intentionally broader than one-off question handlers but still
bounded enough to avoid arbitrary SQL and raw telemetry dumps. Every tool remains
read-only and in parity with its Query API route (§5.2, §9.2).

Recommended MCP tool order for complex questions:

1. Use story/context tools to scope the session, drivers, laps, weather, pit
   stops, and race-control state.
2. Use aggregate/window/stint tools to compute compact facts.
3. Use raw lap telemetry or replay chunks only for a short drill-down window
   after the model has identified the relevant lap, event, or time range.

### 9.3 Tools

#### `list_sessions`

Input:

```json
{ "year": 2024, "event": "Monza", "sessionType": "R" }
```

Output:

```json
{
  "items": [
    {
      "sessionId": "2024-monza-r",
      "year": 2024,
      "eventName": "Italian Grand Prix",
      "sessionType": "R"
    }
  ]
}
```

#### `list_drivers`

Input:

```json
{ "sessionId": "2024-monza-r" }
```

Output:

```json
{
  "sessionId": "2024-monza-r",
  "items": [
    { "driverCode": "LEC", "fullName": "Charles Leclerc", "teamName": "Ferrari" }
  ]
}
```

#### `get_driver_laps`

Input:

```json
{ "sessionId": "2024-monza-r", "driverCode": "LEC" }
```

Output:

```json
{
  "sessionId": "2024-monza-r",
  "driverCode": "LEC",
  "items": [
    { "lapNumber": 12, "lapTimeMs": 82540, "sector1Ms": 27123, "sector2Ms": 29612, "sector3Ms": 25805 }
  ]
}
```

#### `compare_laps`

Input:

```json
{
  "sessionId": "2024-monza-r",
  "driverA": "LEC",
  "lapA": 12,
  "driverB": "HAM",
  "lapB": 14,
  "channels": ["speed_kmh", "throttle_pct", "brake_pct"]
}
```

Output:

```json
{
  "summary": {
    "lapTimeDeltaMs": -214,
    "sectorDeltasMs": [-157, 192, -249],
    "maxSpeedDeltaKmh": 8.7,
    "interpretationHints": [
      "Driver A was faster overall by 214 ms.",
      "Driver A lost time in sector 2 but gained more in sectors 1 and 3."
    ]
  }
}
```

`sessionIdB` may be set to compare laps across two sessions at the same
circuit, for example a driver's qualifying lap across two seasons:

```json
{
  "sessionId": "2024-monza-r",
  "sessionIdB": "2025-monza-r",
  "driverA": "LEC",
  "lapA": 12,
  "driverB": "LEC",
  "lapB": 9,
  "channels": ["speed_kmh", "throttle_pct", "brake_pct"]
}
```

#### `summarize_strategy`

Input:

```json
{
  "sessionId": "2024-monza-r",
  "drivers": ["LEC", "HAM"],
  "compareToFieldAverage": true
}
```

Output:

```json
{
  "items": [
    {
      "driverCode": "LEC",
      "stops": [
        {
          "lapNumber": 24,
          "fromCompound": "MEDIUM",
          "toCompound": "HARD",
          "strategyLabel": "undercut",
          "rivalDriverCode": "HAM",
          "positionGainAfterStop": 1
        }
      ],
      "narrativeFacts": [
        "LEC pitted on lap 24 onto HARD, two laps before HAM.",
        "The undercut gained LEC one position over HAM by lap 27."
      ]
    }
  ]
}
```

#### `generate_race_debrief`

Input:

```json
{
  "sessionId": "2024-monza-r",
  "sections": ["overview", "incidents", "strategy", "weather"]
}
```

Output:

```json
{
  "overview": {
    "winner": "VER",
    "headline": "VER wins from pole, LEC undercuts HAM for P2.",
    "lapCount": 53
  },
  "incidents": [
    { "lapNumber": 18, "type": "yellow_flag", "message": "YELLOW FLAG SECTOR 2" }
  ],
  "strategy": { "items": [] },
  "weather": { "summary": "Dry, track temperature rising from 43C to 55C." }
}
```

#### `compare_corners`

Input:

```json
{
  "sessionId": "2024-monza-r",
  "cornerNumber": 1,
  "drivers": ["LEC", "HAM"],
  "lapRange": { "from": 1, "to": 10 },
  "metrics": ["brake_point_distance_m", "min_corner_speed_kmh"]
}
```

Output:

```json
{
  "cornerLabel": "Turn 1, Variante del Rettifilo",
  "items": [
    {
      "driverCode": "LEC",
      "lapNumber": 5,
      "brakePointDistanceM": 612.4,
      "minCornerSpeedKmh": 87.0
    }
  ],
  "summary": {
    "averageBrakePointDeltaM": 4.8,
    "fastestMinCornerSpeedDriver": "LEC"
  }
}
```

#### `get_replay_metadata`

Input:

```json
{ "sessionId": "2024-monza-r" }
```

Output:

```json
{
  "sessionId": "2024-monza-r",
  "durationMs": 5265000,
  "drivers": ["VER", "NOR", "LEC", "HAM"],
  "availableChannels": ["speed_kmh", "throttle_pct", "brake_pct", "gear", "rpm", "drs", "session_time_ms", "lap_time_ms", "track_status", "sample_source", "x", "y", "z"],
  "supportedReplaySpeeds": [0.25, 0.5, 1, 2, 5, 10, 20]
}
```

#### `detect_telemetry_windows`

Input:

```json
{
  "sessionId": "2024-monza-r",
  "drivers": ["LEC"],
  "eventType": "hard_braking",
  "lapRange": { "from": 1, "to": 10 },
  "minimumDurationMs": 250,
  "includeNearestCorner": true,
  "limit": 50
}
```

Output:

```json
{
  "items": [
    {
      "driverCode": "LEC",
      "lapNumber": 12,
      "startLapTimeMs": 8600,
      "endLapTimeMs": 13100,
      "durationMs": 4500,
      "nearestCorner": "Turn 1/2, Variante del Rettifilo",
      "summary": {
        "entrySpeedKmh": 342.0,
        "minimumSpeedKmh": 87.0,
        "maxBrakePct": 100.0
      }
    }
  ]
}
```

#### `get_standings`

Input:

```json
{ "sessionId": "2024-monza-r", "atLap": 40, "sortBy": "position" }
```

Output:

```json
{
  "atLap": 40,
  "items": [
    { "position": 1, "driverCode": "VER", "gapToLeaderMs": 0, "lastLapMs": 81450, "compound": "HARD", "tyreLife": 29, "pitCount": 1, "status": "running" }
  ]
}
```

#### `list_incidents`

Input:

```json
{ "sessionId": "2024-monza-r", "types": ["safety_car", "red", "hard_braking"], "minBrakingG": 4.0, "maxResults": 100 }
```

Output:

```json
{
  "items": [
    { "type": "safety_car", "lapNumber": 8, "nearestCorner": "Turn 1, Variante del Rettifilo", "message": "Safety car deployed — debris" }
  ],
  "summary": { "incidentCount": 6, "hardestBrakingG": 5.1, "lapsUnderSafetyCar": 3 }
}
```

#### `predict_pit_window`

Input:

```json
{ "sessionId": "2024-monza-r", "drivers": ["LEC"], "stint": 1, "horizonLaps": 12 }
```

Output:

```json
{
  "items": [
    {
      "driverCode": "LEC",
      "compound": "MEDIUM",
      "degradationMsPerLap": 82.4,
      "predictedCliffLap": 29,
      "recommendedPitWindow": { "fromLap": 24, "toLap": 27 },
      "undercutThreat": "high",
      "confidence": 0.86
    }
  ]
}
```

#### `correlate_incidents_weather`

Input:

```json
{ "sessionId": "2024-monza-r", "rainThreshold": 0.2 }
```

Output:

```json
{
  "correlations": [
    { "summary": "Rainfall crossed threshold at L42; safety car deployed L44.", "lagLaps": 2 }
  ]
}
```

#### `compare_sessions`

Input:

```json
{
  "sessionA": "2024-monza-r", "driverA": "LEC", "lapA": "best",
  "sessionB": "2025-monza-r", "driverB": "LEC", "lapB": "best",
  "channels": ["speed_kmh", "throttle_pct", "brake_pct"]
}
```

Output:

```json
{
  "sessionA": "2024-monza-r",
  "sessionB": "2025-monza-r",
  "summary": { "lapTimeDeltaMs": -920, "maxSpeedDeltaKmh": 8.4 }
}
```

### 9.4 Natural-Language Examples

#### Compare two laps

User:

```text
Compare LEC lap 12 with HAM lap 14 at Monza 2024 race.
```

Expected tool sequence:

```text
list_sessions(year=2024, event="Monza", sessionType="R")
compare_laps(sessionId="2024-monza-r", driverA="LEC", lapA=12, driverB="HAM", lapB=14)
```

Expected answer:

```text
LEC was faster/slower by X ms overall. Sector deltas were S1, S2, S3. The largest difference was in sector N.
```

#### Find hard braking events

User:

```text
Find all LEC braking events above 80 percent in the first 10 laps.
```

Expected tool sequence:

```text
detect_telemetry_windows(sessionId="...", drivers=["LEC"], eventType="hard_braking", lapRange={from:1,to:10}, minimumDurationMs=250, limit=100)
```

Expected answer:

```text
Found N braking windows. The longest was on lap X from A-B seconds and peaked at Z percent brake.
```

#### Get replay availability

User:

```text
Can I replay the Monza race and which drivers are available?
```

Expected tool sequence:

```text
list_sessions(year=2024, event="Monza", sessionType="R")
get_replay_metadata(sessionId="2024-monza-r")
```

Expected answer:

```text
Yes. The session duration is X minutes. Available drivers are A, B, C. Replay supports 0.25x to 20x in the desktop app.
```

#### Explain a pit stop

User:

```text
Why did LEC pit on lap 24, and did it work?
```

Expected tool sequence:

```text
list_sessions(year=2024, event="Monza", sessionType="R")
summarize_strategy(sessionId="2024-monza-r", drivers=["LEC", "HAM"], compareToFieldAverage=true)
```

Expected answer:

```text
LEC pitted on lap 24 for HARD tyres, two laps before HAM. The stop was slightly
faster than the field average and the undercut gained LEC one position over HAM
by lap 27.
```

#### Generate a race debrief

User:

```text
Give me a quick debrief of the Monza 2024 race.
```

Expected tool sequence:

```text
list_sessions(year=2024, event="Monza", sessionType="R")
generate_race_debrief(sessionId="2024-monza-r", sections=["overview","incidents","strategy","weather"])
```

Expected answer:

```text
VER won from pole. There was a yellow flag in sector 2 on lap 18. LEC undercut
HAM for P2. Conditions were dry, with track temperature rising from 43C to 55C.
```

#### Compare a corner across drivers

User:

```text
Where does LEC brake later than HAM into Turn 1?
```

Expected tool sequence:

```text
list_sessions(year=2024, event="Monza", sessionType="R")
compare_corners(sessionId="2024-monza-r", cornerNumber=1, drivers=["LEC","HAM"], lapRange={from:1,to:10}, metrics=["brake_point_distance_m","min_corner_speed_kmh"])
```

Expected answer:

```text
On average LEC brakes about 5 meters later into Turn 1 than HAM and carries a
slightly higher minimum corner speed.
```

---

## 10. Observability

Use Aspire Dashboard as the local observability UI.

### 10.1 Signals

Each .NET service must emit:

- Structured logs.
- Distributed traces.
- Basic metrics.

### 10.2 Required Trace Attributes

Query API spans:

- Endpoint name.
- Session ID when present.
- Driver code when present.
- Lap number when present.
- Database query duration.
- Returned row count.

MCP server spans:

- Tool name.
- Input validation result.
- Query API route or shared query-store method called.
- Query API/query-store duration.
- Output item count.

### 10.3 Required Logs

Import script:

- Import start.
- Resolved session.
- Driver count.
- Lap count.
- Batch insert progress.
- Import completion.
- Validation failures.

Query API:

- Request start and completion.
- Validation failures.
- Slow queries over 1 second.
- Replay chunk requests.

MCP server:

- Tool call start and completion.
- Rejected invalid tool input.
- Query API failures.

---

## 11. Aspire AppHost

The Aspire AppHost must orchestrate:

- TimescaleDB container.
- Query API.
- MCP Query Server.

The .NET MAUI desktop app may be launched separately. Aspire should remain
focused on local services unless a later workflow needs desktop launch
integration.

Example:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder
    .AddPostgres("postgres", port: 5432)
    .WithDataVolume()
    .AddDatabase("f1telemetry");

var queryApi = builder
    .AddProject<Projects.F1Telemetry_QueryApi>("query-api")
    .WithReference(postgres)
    .WaitFor(postgres);

builder
    .AddProject<Projects.F1Telemetry_McpServer>("mcp-server")
    .WithReference(queryApi)
    .WaitFor(queryApi);

builder.Build().Run();
```

If the built-in Aspire Postgres resource cannot install the TimescaleDB extension, use a TimescaleDB Docker image through a custom container resource.

Required configuration:

```text
ConnectionStrings__f1telemetry
QueryApi__BaseUrl
ASPNETCORE_ENVIRONMENT=Development
```

---

## 12. Repository Structure

```text
f1-telemetry-visualizer/
  src/
    F1Telemetry.AppHost/
    F1Telemetry.ServiceDefaults/
    F1Telemetry.Contracts/
      Dtos/
      Json/
    F1Telemetry.QueryApi/
      Data/
      Dtos/
      Endpoints/
      Queries/
      Validation/
    F1Telemetry.McpServer/
      Clients/
      Dtos/
      Tools/
      Validation/
    F1Telemetry.Desktop/
      Charts/
      Services/
      ViewModels/
      Views/
  db/
    migrations/
      001_initial_schema.sql
      002_timescale_hypertables.sql
  scripts/
    import_session.py
    requirements.txt
  docs/
    architecture.md
  tests/
    F1Telemetry.QueryApi.Tests/
    F1Telemetry.McpServer.Tests/
```

---

## 13. Implementation Phases

### Phase 1 — Database and Import

Deliverables:

- TimescaleDB started through Aspire.
- Initial schema and hypertables.
- `scripts/import_session.py`.
- Successful import of one session, all drivers, and all laps.

Acceptance test:

```bash
python scripts/import_session.py --year 2024 --event "Monza" --session "R" --if-exists replace
```

Verification:

```sql
SELECT count(*) FROM sessions;
SELECT count(*) FROM session_drivers WHERE session_id = '2024-monza-r';
SELECT count(*) FROM laps WHERE session_id = '2024-monza-r';
SELECT count(*) FROM telemetry_samples WHERE session_id = '2024-monza-r';
```

### Phase 2 — Query API

Deliverables:

- Endpoints for sessions, drivers, laps, lap telemetry, lap comparison, replay metadata, replay chunks, telemetry-event search, and story/context summaries.
- Analytical primitive endpoints for telemetry aggregation, telemetry windows, and driver stint analysis.
- OpenAPI in local development.
- Validation implemented.
- Logs, traces, and metrics visible in Aspire Dashboard.

Acceptance test:

```bash
curl http://localhost:{port}/api/sessions
curl http://localhost:{port}/api/sessions/2024-monza-r/replay/metadata
```

### Phase 3 — Desktop Replay

Deliverables:

- .NET MAUI desktop app scaffold.
- Session browser with race-default filtering, search, context availability
  flags, and selected-session details.
- Replay workspace with fixed first-pass docked panels.
- Driver selector.
- Replay controls.
- Time-series chart updates.
- Track-map position updates.
- Linked replay timebase shared by track map, waveform, current values, lap
  summary, event timeline, and context strip.
- Cursor-driven seek from timeline, waveform, event rows, lap rows, and track
  map positions where timestamps are available.
- Current-value readouts for selected driver speed, throttle, brake, gear, RPM,
  DRS, lap number, lap-relative time, and session-relative time.
- Event timeline with track-status, race-control, pit, and telemetry-event
  candidate rows.
- Lap summary table for selected drivers.
- Replay speed support from `0.25x` to `20x`.
- Viewport-aware rendering and virtualized lists for high-volume replay data.

Acceptance test:

```text
Open imported Monza race, select two drivers, play at 1x, switch to 5x,
pause, seek from waveform and event timeline, resume, and verify map,
waveform, readouts, lap summary, and context overlays stay synchronized.
```

### Phase 4 — Lap Comparison

Deliverables:

- Lap comparison screen.
- Lap-time-aligned channel overlays.
- Lap-time delta.
- Sector deltas included in the same screen.
- Cursor and optional reference cursor over the aligned lap timeline.
- Per-channel current values and deltas at the cursor.
- Lap metadata for both compared laps.

Acceptance test:

```text
Compare two laps from two drivers and display speed, throttle, brake, lap
delta, sector deltas, cursor values, and channel deltas.
```

### Phase 5 — MCP Query Server

Deliverables:

- MCP server started by Aspire.
- Tools defined in this document.
- Tools stay in parity with Query API analytical routes and shared contracts.
- Aggregate/window/stint tools return compact facts instead of raw telemetry samples.
- Tool calls visible in Aspire Dashboard.

Acceptance test:

```text
From an MCP-compatible client, ask: "Compare LEC lap 12 with HAM lap 14 at Monza 2024 race." The server resolves the session and returns comparison data.
```

### Phase 6 — Optional AI Assistant Panel

Deliverables:

- AI panel in the .NET MAUI desktop app.
- Display of MCP-derived answers beside the session data.
- Deep link from comparison answers to the lap comparison screen.

Acceptance test:

```text
Ask for a lap comparison from the assistant and open the corresponding lap comparison screen from the answer.
```

### Phase 7 — Application Shell, Field View, And Incidents

Deliverables:

- Carbon Signal design system applied via a generated `Theme.Carbon.xaml` consumed by the app and chart renderers (§8.8).
- Session console shell: command bar, left view rail, instrument HUD (§8.11).
- Home / Launcher and command palette (`⌘K` / `/`) over imported sessions, drivers, and quick actions (§8.11).
- Full keyboard model with `?` cheat-sheet (§8.12).
- Field View / timing tower backed by `GET /api/sessions/{sessionId}/standings` (§6.11, §8.13).
- Track Incidents and Hard-Braking view backed by `GET /api/sessions/{sessionId}/incidents` (§6.13, §8.14).
- Strategy tire-gantt and Lap-Analysis position trace backed by `strategy/summarize` (§6.10.4) and `GET /api/sessions/{sessionId}/positions` (§6.12, §8.15).

Acceptance test:

```text
Open a session from the launcher, switch views with number keys, sort the
field-view timing tower, click an incident on the track map and confirm the
incident list and replay cursor sync, and confirm every surface uses the
Carbon Signal tokens.
```

### Phase 8 — Predictive And Cross-Session Analytics

Deliverables:

- Tire degradation & pit-window predictor endpoint and MCP tool (§6.14, `predict_pit_window`), surfaced as Strategy-view cards with measured/forecast separation.
- Cross-session comparison endpoint and MCP tool (§6.16, `compare_sessions`), driving the session-diff and ghost-car overlay (§8.15).
- Incident × weather correlation endpoint and MCP tool (§6.15, `correlate_incidents_weather`) and timeline view.
- Strategy / pit-loss narrative and natural-language race story over the MCP analytical tools.
- Exportable session report (PDF/HTML) sharing one renderer with the on-screen race debrief.

Acceptance test:

```text
Forecast LEC's stint-1 pit window, overlay LEC's best 2024 vs 2025 Monza lap as
a ghost car, surface a rain→safety-car correlation, and export a one-page
session report — all from bounded Query API / MCP responses.
```

---

## 14. Validation and Safety Rules

The system must enforce these rules:

1. Session IDs must exist before querying child resources.
2. Driver codes must exist in the selected session.
3. Lap numbers must exist for the selected driver.
4. Channel names must come from the allow-list in this document.
5. Replay chunks must be bounded by duration and row count.
6. MCP tools must be read-only.
7. No endpoint or tool may expose arbitrary SQL.
8. Large responses must support downsampling or pagination.
9. Missing telemetry values must be represented as `null`.
10. Errors must be explicit and actionable.

---

## 15. Performance Targets

Initial local targets:

| Operation | Target |
|---|---:|
| List sessions | `< 100 ms` |
| List drivers | `< 100 ms` |
| List laps for driver | `< 200 ms` |
| Fetch one lap telemetry | `< 500 ms` |
| Compare two laps | `< 1000 ms` |
| Replay metadata | `< 200 ms` |
| Replay chunk, 30 seconds, 2 drivers | `< 1000 ms` |
| MCP tool call excluding model time | `< 1500 ms` |
| Desktop replay UI at 1x, 2 selected drivers | Sustained responsive playback |
| Desktop cursor seek within buffered range | `< 100 ms` visible response |
| Desktop replay seek requiring chunk load | `< 1500 ms` visible response |
| Desktop chart redraw after channel toggle | `< 250 ms` for visible window |
| Desktop event/lap table scroll | Virtualized, no full-list layout stalls |

If targets are missed, optimize in this order:

1. Indexes.
2. Query shape.
3. Downsampling.
4. Materialized summaries.
5. Persisted replay chunks.
6. Client-side viewport-aware rendering and cache tuning.

---

## 16. Configuration

### 16.1 Query API

```json
{
  "Telemetry": {
    "DefaultMaxSamples": 5000,
    "AbsoluteMaxSamples": 50000,
    "DefaultReplayChunkDurationMs": 30000,
    "MaxReplayChunkDurationMs": 120000,
    "AllowedChannels": [
      "speed_kmh",
      "throttle_pct",
      "brake_pct",
      "gear",
      "rpm",
      "drs",
      "x",
      "y",
      "z"
    ]
  }
}
```

### 16.2 Import Script

```text
F1TELEMETRY_DATABASE_URL=Host=localhost;Port=5432;Database=f1telemetry;Username=postgres;Password=postgres
FASTF1_CACHE_DIR=.cache/fastf1
```

---

## 17. Final Architecture Summary

The implementation consists of:

- One TimescaleDB container.
- One offline import script.
- One Query API.
- One .NET MAUI desktop application.
- One MCP Query Server.
- Aspire Dashboard for local observability.

The desktop application is a keyboard-first **session console** (§8.11) styled
with the project-owned **Carbon Signal** design system (§8.8, `docs/design-system/`).
Beyond replay and lap comparison it provides a Home/Launcher with command
palette, an all-driver Field View / timing tower (§8.13), a Track Incidents and
hard-braking map (§8.14), tire-strategy gantt and position-trace views, and
cross-session / ghost-car comparison with predictive tire and pit-window
analytics (§8.15) — every surface backed by a bounded Query API route (§6) and a
read-only MCP tool in parity (§9).

The workflow is:

1. Import one or more real sessions into TimescaleDB.
2. Query stored data through the Query API.
3. Explore the field, replay, compare, and analyze the session in the desktop console.
4. Use MCP tools for natural-language questions and narrative generation over the same Query API capabilities.

---

## 18. Future Enhancements (Backlog)

> **Status update.** The five items below have been **promoted into required
> scope** and are now specified in the body of this document; they are retained
> here only as a reading index. Their authoritative definitions are:
>
> - 18.1 Tire degradation & pit-window predictor → §6.14, `predict_pit_window` (§9.3), §8.15, Phase 8.
> - 18.2 Cross-session "ghost car" overlay → §6.16, `compare_sessions` (§9.3), §8.15, Phase 8.
> - 18.3 Natural-language race debrief narratives → §6.10.5 / `generate_race_debrief`, race-story surface (§8.15), Phase 8.
> - 18.4 Incident and weather correlation timeline → §6.15, `correlate_incidents_weather` (§9.3), §8.15, Phase 8.
> - 18.5 Exportable session report → §8.15 (client renderer over existing aggregates), Phase 8.
>
> Genuinely future ideas (saved layouts, cross-circuit meta-analysis, live
> ingestion, video sync) remain out of scope. The original descriptions follow.

These items extend the focused implementation above. They build directly on
existing schema, endpoints, and the MCP layer.

### 18.1 Tire Degradation And Pit-Window Predictor

A model trained on `telemetry_samples` and `laps` (lap time, compound, tyre
age, stint position) that forecasts per-stint degradation curves and surfaces
a recommended pit window. Builds on the existing `stints/analyze` and
`strategy/summarize` endpoints (6.10.3, 6.10.4) by adding a forward-looking
projection rather than only historical summaries. Surfaced in the desktop
Pit Analysis view and as a new MCP tool (e.g. `predict_pit_window`).

### 18.2 Cross-Session "Ghost Car" Overlay

Extends lap comparison (6.5, 8.6) beyond a single session: overlay a driver's
lap against another lap from a different session, year, or driver on the
track map as an animated ghost car synchronized to the same lap-relative
timebase used in replay (7.7). Requires the compare-laps endpoint to accept
laps from two different `sessionId` values and the replay track map to render
multiple position traces concurrently.

### 18.3 Natural-Language Race Debrief Narratives

Extends `generate_race_debrief` (9.3) to chain multiple analytical primitives
(stint analysis, corner comparison, telemetry windows) into a single narrative
summary describing key moments of a session — e.g. pace loss tied to tyre
wear, and its effect on race position. This is a composition layer over
existing MCP tools rather than a new data source.

### 18.4 Incident And Weather Correlation Timeline

A unified timeline view joining `race_control_messages`, `track_status_events`,
and `weather_samples` on the replay timebase, so events such as a Virtual
Safety Car can be displayed alongside the weather conditions that preceded
them. Extends the existing Context Timeline (7.4) with a weather overlay and
a corresponding MCP tool for querying correlated events.

### 18.5 Exportable Session Report

A one-click export (PDF/HTML) of a session's key findings — lap comparison,
stint/strategy summary, and incident timeline — composed from existing Query
API responses (6.5, 6.10.3, 6.10.4, 9.3). Primarily a desktop-app rendering
and templating feature; no new backend data is required.
