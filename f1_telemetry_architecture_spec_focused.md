# F1 Telemetry Visualizer — Focused Implementation Specification

**Owner:** Fabio  
**Status:** Implementation specification  
**Scope:** Local Formula 1 telemetry import, storage, replay, visual analysis, and natural-language querying.

---

## 1. Product Goal

Build a local desktop application that imports public Formula 1 telemetry for one selected race session by default, stores it in TimescaleDB, and lets the user replay, inspect, compare, and query that race through a focused UI and an AI-ready MCP interface.

The implementation must prioritize a small set of capabilities that demonstrate the framework clearly:

1. Import one real race session for all available drivers.
2. Replay the race in the desktop app at multiple speeds.
3. Compare two laps by lap-relative time using core telemetry channels.
4. Ask natural-language questions through a read-only MCP server.
5. Observe local .NET services through Aspire Dashboard.

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
[Query API] <---- REST ----> [Avalonia Desktop App]
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
| Avalonia Desktop App | Native .NET desktop app | Optional | No | Session selection, replay, lap comparison, and optional AI panel |
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

Acceptance criteria:

- Aligns samples by lap-relative time, not derived distance.
- Uses interpolation or bucket aggregation when exact time matches are unavailable.
- Includes sector deltas in the summary.
- Returns `400` for invalid lap numbers or invalid `timeStepMs`.
- Returns `404` for missing session, driver, or lap.

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

---

## 7. Replay Requirements

Replay is client-driven. The backend returns chunks over REST; the Avalonia app controls playback locally.

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

---

## 8. Avalonia Desktop App

### 8.1 Technology

```text
Avalonia UI 11
CommunityToolkit.Mvvm
LiveCharts2 for animated time-series charts
ScottPlot for static lap comparison charts
System.Text.Json source-generated serializers where useful
```

### 8.2 Screens

The desktop app must provide only these initial screens:

1. Session Selector.
2. Replay Workspace.
3. Lap Comparison.
4. AI Assistant Panel.

### 8.3 Session Selector

Fields:

- Season.
- Event.
- Session type.
- Imported timestamp.
- Number of drivers.
- Number of laps.

Actions:

- Refresh sessions.
- Open selected session.

### 8.4 Replay Workspace

Must show:

- Track map with selected driver positions and circuit markers when available.
- Time-series chart for selected channels.
- Timeline overlays for track status, race-control messages, and rainfall when available.
- Current weather readout with air temperature, track temperature, rain state, and wind.
- Replay controls.
- Driver list.
- Current lap and speed for selected drivers.

### 8.5 Lap Comparison

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

### 8.6 AI Assistant Panel

The AI Assistant Panel is optional in the first UI iteration. The MCP server must work first from an external MCP-compatible client.

When implemented, the panel should support questions such as:

```text
Compare Leclerc lap 12 and Hamilton lap 14.
Where did LEC lose time against HAM?
Find LEC braking events above 80 percent in the first 10 laps.
Can I replay the Monza race and which drivers are available?
```

The panel should show the answer and, when applicable, offer an action to open the corresponding replay or lap comparison screen.

---

## 9. MCP Query Server

### 9.1 Technology

```text
.NET 9
ModelContextProtocol C# SDK
HTTP transport for local Aspire execution
Optional stdio transport for coding-agent integration
Aspire service defaults
```

### 9.2 Responsibilities

The MCP server must:

- Expose read-only tools.
- Validate inputs before calling the Query API.
- Call the Query API for all data access.
- Return compact, bounded, model-friendly JSON.
- Prefer summary/context endpoints for analytical questions over raw sample retrieval.
- Emit one trace span per tool call.

The MCP server must not:

- Connect directly to TimescaleDB.
- Execute arbitrary SQL.
- Write data.
- Return unbounded telemetry samples.

MCP-backed analytics must be answered from Query API endpoints that use
TimescaleDB SQL, indexes, and analytical views/materialized views. The MCP
server is an adapter over those capabilities, not an analytical database client.

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

#### `find_telemetry_events`

Input:

```json
{
  "sessionId": "2024-monza-r",
  "drivers": ["LEC"],
  "lapRange": { "from": 1, "to": 10 },
  "conditions": [
    { "channel": "brake_pct", "operator": ">=", "value": 80 }
  ],
  "maxResults": 50
}
```

Output:

```json
{
  "items": [
    {
      "driverCode": "LEC",
      "lapNumber": 12,
      "distanceM": 612.4,
      "sampleTimeUtc": "2024-09-01T13:14:41.200Z",
      "values": { "brakePct": 91.0 }
    }
  ]
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
find_telemetry_events(sessionId="...", drivers=["LEC"], lapRange={from:1,to:10}, conditions=[{channel:"brake_pct", operator:">=", value:80}], maxResults=100)
```

Expected answer:

```text
Found N events. The strongest braking event was on lap X at distance Y m with brake_pct Z.
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
- Query API endpoint called.
- Query API duration.
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

The Avalonia app may be launched separately or registered in Aspire if practical.

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

- Endpoints for sessions, drivers, laps, lap telemetry, lap comparison, replay metadata, replay chunks, and telemetry-event search.
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

- Session selector.
- Replay workspace.
- Driver selector.
- Replay controls.
- Time-series chart updates.
- Track-map position updates.
- Replay speed support from `0.25x` to `20x`.

Acceptance test:

```text
Open imported Monza race, select two drivers, play at 1x, switch to 5x, pause, seek, resume.
```

### Phase 4 — Lap Comparison

Deliverables:

- Lap comparison screen.
- Lap-time-aligned channel overlays.
- Lap-time delta.
- Sector deltas included in the same screen.

Acceptance test:

```text
Compare two laps from two drivers and display speed, throttle, brake, lap delta, and sector deltas.
```

### Phase 5 — MCP Query Server

Deliverables:

- MCP server started by Aspire.
- Tools defined in this document.
- Tools call the Query API only.
- Tool calls visible in Aspire Dashboard.

Acceptance test:

```text
From an MCP-compatible client, ask: "Compare LEC lap 12 with HAM lap 14 at Monza 2024 race." The server resolves the session and returns comparison data.
```

### Phase 6 — Optional AI Assistant Panel

Deliverables:

- AI panel in Avalonia.
- Display of MCP-derived answers beside the session data.
- Deep link from comparison answers to the lap comparison screen.

Acceptance test:

```text
Ask for a lap comparison from the assistant and open the corresponding lap comparison screen from the answer.
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

If targets are missed, optimize in this order:

1. Indexes.
2. Query shape.
3. Downsampling.
4. Materialized summaries.
5. Persisted replay chunks.

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
- One Avalonia desktop application.
- One MCP Query Server.
- Aspire Dashboard for local observability.

The workflow is:

1. Import one real session into TimescaleDB.
2. Query stored data through the Query API.
3. Replay and compare the session in the desktop app.
4. Use MCP tools for natural-language questions over the same Query API capabilities.
