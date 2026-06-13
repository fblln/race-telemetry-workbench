# Data

This page is the single reference for FastF1 source data, local downloads,
TimescaleDB imports, and storage planning.

Race sessions (`R`) are the default project scope. Practice, qualifying, sprint
qualifying, and sprint sessions are explicit opt-ins through `--session`.

## Local Database

Start TimescaleDB:

```bash
docker compose up -d timescaledb
```

Default database URL:

```text
postgresql://race_telemetry:race_telemetry@localhost:5432/race_telemetry
```

Override it with either `RACE_TELEMETRY_DATABASE_URL` or `--database-url`.

## Download And Validate

`scripts/download_session.py` downloads Formula 1 race data through FastF1,
validates that the source data needed by the importer is present, warms the
FastF1 cache, and writes a JSON manifest with row counts and data-quality
warnings. It does not write to TimescaleDB.

Install dependencies:

```bash
python3 -m venv .venv
.venv/bin/python -m pip install -r scripts/requirements.txt
```

Basic usage:

```bash
.venv/bin/python scripts/download_session.py --year 2024 --event Monza
.venv/bin/python scripts/download_session.py --year 2024 --event Monza --drivers LEC --limit-laps 3
.venv/bin/python scripts/download_session.py --year 2024 --event Silverstone --session Q --log-level DEBUG
```

Options:

| Option | Default | Description |
|---|---|---|
| `--year` | required | Championship year, for example `2024`. |
| `--event` | required | Event, circuit, or Grand Prix name accepted by FastF1. |
| `--session` | `R` | Session identifier: `FP1`, `FP2`, `FP3`, `Q`, `SQ`, `S`, or `R`. |
| `--drivers` | all drivers | Comma-separated driver-code subset such as `VER,HAM,LEC`. |
| `--limit-laps` | no limit | Validate only the first N laps per selected driver. |
| `--cache-dir` | `data/fastf1-cache` | FastF1 cache directory. |
| `--manifest-dir` | `data/download-manifests` | Output directory for JSON manifests. |
| `--log-level` | `INFO` | Logging verbosity. |

Manifests are written to:

```text
data/download-manifests/{session_id}.json
```

Filtered or limited development runs write subset manifests so smoke tests do
not overwrite the canonical full-session manifest.

Verified download sessions:

| Session | Drivers | Laps | Telemetry samples | Position samples | Missing telemetry laps | Missing position laps |
|---|---:|---:|---:|---:|---:|---:|
| 2024 Monza Race | 20 | 1,008 | 324,546 | 333,505 | 0 | 0 |
| 2025 Monza Race | 20 | 974 | 305,177 | 312,768 | 0 | 0 |

FastF1 emitted a lap-accuracy warning for driver number `27` during the 2025
race load. The downloaded lap, telemetry, and position data still validated
with no missing sample laps.

## Import To TimescaleDB

`scripts/import_session.py` imports one FastF1 session from the local FastF1
cache into TimescaleDB:

```bash
.venv/bin/python scripts/import_session.py --year 2024 --event Monza
```

Fast smoke test:

```bash
.venv/bin/python scripts/import_session.py \
  --year 2024 \
  --event Monza \
  --drivers LEC \
  --limit-laps 1 \
  --mode replace
```

Use `scripts/import_sessions.py` for several sessions. Bulk imports always keep
full context enabled so benchmark and backfill runs do not accidentally omit
weather, circuit markers, status events, or race-control messages.

```bash
.venv/bin/python scripts/import_sessions.py \
  --spec 2024:Monza:R \
  --spec 2025:Monza:R \
  --mode replace \
  --workers 2
```

Start bulk imports conservatively with `--workers 2`; raise it only after
watching database CPU, disk I/O, and connection count. A local `--workers 6`
season attempt overloaded the database/container path.

Write modes:

| Mode | Behavior |
|---|---|
| `fail` | Default. Stops if the session already exists. |
| `replace` | Deletes the existing session first, relying on cascade deletes. |
| `upsert` | Updates session, driver, and lap rows; clears and reinserts sample/context child rows for the session. |

Important import options:

| Option | Purpose |
|---|---|
| `--session` | FastF1 session code. Defaults to `R`. |
| `--drivers` | Comma-separated driver-code subset, such as `LEC,HAM`. |
| `--limit-laps` | Imports only the first N laps per selected driver. |
| `--batch-size` | Number of sample rows to buffer before a database write. Defaults to `100000`. |
| `--sample-write-method` | `copy` streams sample batches with PostgreSQL COPY; `insert` uses `executemany`. Defaults to `copy`. |
| `--telemetry-workers` | Worker threads for per-driver raw sample extraction. Defaults to `1`, currently fastest on warm-cache Monza. |
| `--parallel-sample-copy` | Copy `telemetry_samples` and `position_samples` concurrently using separate database connections. Default. |
| `--no-parallel-sample-copy` | Use the older single-connection sample COPY path. |
| `--skip-telemetry` | Skip raw car telemetry rows. |
| `--skip-position` | Skip raw position rows. |
| `--skip-context` | Single-session developer shortcut only. Do not use for benchmark or season imports. |

Imported tables:

- `sessions`
- `session_drivers`
- `laps`
- `telemetry_samples`
- `position_samples`
- `circuit_metadata`
- `circuit_markers`
- `weather_samples`
- `track_status_events`
- `session_status_events`
- `race_control_messages`

Telemetry rows preserve raw car channels from FastF1 `session.car_data` grouped
by driver. Position rows preserve raw position channels from FastF1
`session.pos_data`, with lap numbers assigned from selected lap windows so
replay and track-outline generation stay data-native.

## FastF1 Source Mapping

### Session Loading

```python
import fastf1

fastf1.Cache.enable_cache("data/fastf1-cache")

session = fastf1.get_session(2024, "Monza", "R")
session.load(laps=True, telemetry=True, weather=False, messages=False)
```

FastF1's `session.drivers` contains driver numbers as strings, for example
`["16", "81", "4", "55", "44"]`. Use
`session.get_driver(ref)["Abbreviation"]` for three-letter codes.

### Laps

Useful `session.laps` columns include `Time`, `Driver`, `DriverNumber`,
`LapTime`, `LapNumber`, `Stint`, `PitOutTime`, `PitInTime`, sector times,
`Compound`, `TyreLife`, `FreshTyre`, `Team`, `LapStartTime`, `LapStartDate`,
`TrackStatus`, `Position`, `Deleted`, `DeletedReason`, `FastF1Generated`, and
`IsAccurate`.

Mapping notes:

- `Driver` is already the three-letter code.
- `DriverNumber` is the racing number string.
- `LapTime` and sector columns are Pandas timedeltas.
- Missing values can be `NaT`, `NaN`, or `None`.
- `IsAccurate` is FastF1's lap-quality flag.

### Car Telemetry

The importer uses raw driver streams from `session.car_data`. Common fields
match per-lap `lap.get_car_data()` output:

| FastF1 column | Database field |
|---|---|
| `Date` | `sample_time_utc` |
| `SessionTime` | `session_time_ms` |
| `Time` | `lap_time_ms` when assigned to a lap window |
| `Speed` | `speed_kmh` |
| `Throttle` | `throttle_pct` |
| `Brake` | `brake_pct`, converted from boolean to `0` or `100` |
| `nGear` | `gear` |
| `RPM` | `rpm` |
| `DRS` | `drs` |
| `Source` | `sample_source` |

FastF1 car samples do not include distance by default. The canonical importer
stores raw car data and raw position data separately rather than adding derived
distance or driver-ahead enrichment.

### Position Data

The importer uses raw driver streams from `session.pos_data`. Common fields
match per-lap `lap.get_pos_data()` output:

| FastF1 column | Database field |
|---|---|
| `Date` | `sample_time_utc` |
| `SessionTime` | `session_time_ms` |
| `X` | `x` |
| `Y` | `y` |
| `Z` | `z` |
| `Status` | `track_status` |
| `Source` | `sample_source` |

The application should not depend on external track image or vector assets for
replay correctness. The track outline should be derived from imported
`position_samples`.

### Circuit Info

FastF1 circuit annotations come from:

```python
circuit_info = session.get_circuit_info()
```

For Monza 2024, the object contains `corners`, `marshal_lights`,
`marshal_sectors`, and `rotation`. Marker tables contain `X`, `Y`, `Number`,
`Letter`, `Angle`, and `Distance`.

| FastF1 field | Database field |
|---|---|
| `rotation` | `circuit_metadata.rotation_degrees` |
| `corners` rows | `circuit_markers` with `marker_type = "corner"` |
| `marshal_lights` rows | `circuit_markers` with `marker_type = "marshal_light"` |
| `marshal_sectors` rows | `circuit_markers` with `marker_type = "marshal_sector"` |
| `X` / `Y` | `circuit_markers.x` / `circuit_markers.y` |
| `Number` | `circuit_markers.marker_number` |
| `Letter` | `circuit_markers.marker_letter` |
| `Angle` | `circuit_markers.angle_degrees` |
| `Distance` | `circuit_markers.distance_m` |

### Weather

Weather data comes from `session.weather_data` after `session.load(weather=True)`.
It is usually about one sample per minute.

| FastF1 column | Database field |
|---|---|
| `Time` | `weather_samples.session_time_ms`, plus calculated `sample_time_utc` |
| `AirTemp` | `air_temp_c` |
| `TrackTemp` | `track_temp_c` |
| `Humidity` | `humidity_pct` |
| `Pressure` | `pressure_mbar` |
| `Rainfall` | `rainfall` |
| `WindDirection` | `wind_direction_deg` |
| `WindSpeed` | `wind_speed_mps` |

For Monza 2024 Race, cached weather has 133 rows from `00:00:26.141` to
`02:12:26.670` session time, with roughly one-minute spacing.

### Track Status And Race Control

FastF1 status and race-control timelines come from:

```python
session.load(messages=True)
track_status = session.track_status
session_status = session.session_status
messages = session.race_control_messages
```

Known `track_status` codes include:

| Code | Meaning |
|---|---|
| `1` | Track clear |
| `2` | Yellow flag |
| `4` | Safety car |
| `5` | Red flag |
| `6` | Virtual safety car deployed |
| `7` | Virtual safety car ending |

`race_control_messages` is the right source for timeline annotations such as
DRS status, yellow flags, safety car, virtual safety car, pit-exit status,
investigations, sector-specific messages, and driver-specific messages.

## Cache Behavior

FastF1 uses a persistent HTTP cache at:

```text
data/fastf1-cache/fastf1_http_cache.sqlite
```

The first run downloads data from FastF1/F1 timing endpoints. Later runs reuse
cached responses and are much faster. A warm cache may still attempt to
revalidate schedule data; if the network is blocked, FastF1 can fall back to
cached responses for data that has already been fetched.

Useful checks:

```bash
.venv/bin/python scripts/download_session.py --year 2024 --event Monza
.venv/bin/python scripts/download_session.py --year 2024 --event Monza --drivers LEC --limit-laps 3
du -sh data/fastf1-cache
```

## Storage Estimates

Storage has two layers:

- FastF1 raw cache: HTTP responses stored by FastF1 in
  `data/fastf1-cache/fastf1_http_cache.sqlite`.
- Database storage: normalized TimescaleDB rows plus indexes.

Observed after downloading Monza 2024 and 2025 races:

| Downloaded sessions | Raw cache size | Manifest size |
|---:|---:|---:|
| 2 race sessions | 169 MB | 16 KB |

That gives a rough raw-cache average of about `84.5 MB` per race session.

Run the estimator:

```bash
.venv/bin/python scripts/estimate_storage.py
```

Current Monza-only projection:

| Scope | Assumption | Estimated raw FastF1 cache |
|---|---|---:|
| Default race-only season | 24 race sessions | about 2.0 GB |
| Opt-in full weekend season | 24 events x 5 sessions | about 9.9 GB |

TimescaleDB storage is larger than the raw cache because normalized rows and
indexes are stored for query speed. Local planning reserve:

| Scope | Suggested local reserve |
|---|---:|
| One race session imported to DB | 250 MB to 750 MB |
| Race-only season imported to DB | 8 GB to 18 GB |
| Full-weekend season imported to DB | 25 GB to 60 GB |

The measured warm-cache Monza 2024 import completed in about 13 seconds after
switching to driver-level extraction over `session.car_data` and
`session.pos_data`, large COPY batches, and parallel telemetry/position COPY.
