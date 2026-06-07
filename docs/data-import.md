# Data Import

`scripts/import_session.py` imports one FastF1 session from the local FastF1
cache into TimescaleDB.

The importer defaults to race sessions:

```bash
.venv/bin/python scripts/import_session.py --year 2024 --event Monza
```

## Local Database

Start TimescaleDB:

```bash
PATH="$HOME/.docker/bin:$PATH" docker compose up -d timescaledb
```

The default database URL is:

```text
postgresql://race_telemetry:race_telemetry@localhost:5432/race_telemetry
```

Override it with either `RACE_TELEMETRY_DATABASE_URL` or `--database-url`.

## Fast Smoke Test

Use a single cached driver/lap while developing:

```bash
.venv/bin/python scripts/import_session.py \
  --year 2024 \
  --event Monza \
  --drivers LEC \
  --limit-laps 1 \
  --mode replace
```

On the warm Monza cache, this path should log `Using cached data` for the
FastF1 feeds and import in seconds.

## Bulk Session Import

Use `scripts/import_sessions.py` when importing several sessions from a season.
It runs multiple full-context `import_session.py` processes concurrently:

```bash
.venv/bin/python scripts/import_sessions.py \
  --year 2024 \
  --events "Bahrain,Saudi Arabia,Australia,Japan,China,Miami,Emilia Romagna,Monaco,Canada,Spain" \
  --sessions R \
  --mode replace \
  --workers 2
```

For mixed years or one-off batches, pass explicit specs:

```bash
.venv/bin/python scripts/import_sessions.py \
  --spec 2024:Monza:R \
  --spec 2025:Monza:R \
  --mode replace \
  --workers 2
```

Bulk imports always keep full context enabled. There is intentionally no
`--skip-context` option in the bulk importer, so benchmark and backfill runs do
not accidentally omit weather, circuit markers, status events, or race-control
messages.

## Write Modes

| Mode | Behavior |
|---|---|
| `fail` | Default. Stops if the session already exists. |
| `replace` | Deletes the existing session first, relying on cascade deletes. |
| `upsert` | Updates session, driver, and lap rows; clears and reinserts sample/context child rows for the session. |

## Configurable Scope

| Option | Purpose |
|---|---|
| `--session` | FastF1 session code. Defaults to `R`. |
| `--drivers` | Comma-separated driver-code subset, such as `LEC,HAM`. |
| `--limit-laps` | Imports only the first N laps per selected driver. |
| `--batch-size` | Number of sample rows to buffer before a database write. Defaults to `100000`. |
| `--sample-write-method` | `copy` streams sample batches with PostgreSQL COPY; `insert` uses `executemany` with conflict handling. Defaults to `copy`. |
| `--telemetry-workers` | Worker threads for per-driver raw sample extraction. Defaults to `1`, which is currently fastest on the warm-cache Monza benchmark. |
| `--parallel-sample-copy` | Copy `telemetry_samples` and `position_samples` concurrently using separate database connections. Default. |
| `--no-parallel-sample-copy` | Use the older single-connection sample COPY path. |
| `--skip-telemetry` | Skip raw car telemetry rows. |
| `--skip-position` | Skip raw position rows. |
| `--skip-context` | Single-session developer shortcut only. Skip circuit, weather, status, and race-control rows. Do not use for benchmark or season import runs. |

## Imported Data

The first importer slice writes:

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

Telemetry rows preserve the raw car channels from FastF1 `session.car_data`
grouped by driver. Position rows preserve the raw position channels from
FastF1 `session.pos_data`, with lap numbers assigned from the selected lap
windows so replay and track-outline generation can stay data-native. The
`source_sample_index` column reflects the sample order within the raw
driver-level FastF1 stream.

## Performance Notes

Full race imports are bottlenecked by two phases:

- FastF1/Pandas raw sample extraction and shaping per driver.
- High-volume writes into `telemetry_samples` and `position_samples`.

The importer assigns samples to laps with a linear session-time pass and uses
PostgreSQL COPY for sample writes by default. The fastest measured path uses
one extraction worker and then copies `telemetry_samples` and
`position_samples` concurrently through separate database connections. Progress
logs include per-driver extraction timing, cumulative sample counts, database
write time, and duplicate sample rows skipped before COPY.

Use `--sample-write-method insert` only when debugging conflict behavior. It is
expected to be much slower for full sessions.

Observed on a 10-core Apple Silicon Mac with warm FastF1 cache, the raw
Monza 2024 race importer completed a full session in about 13 seconds with
parallel sample COPY after switching from lap-level extraction to driver-level
extraction over `session.car_data` and `session.pos_data`.

Measured worker-count sanity check with `--skip-context` on the same warm cache:

- `--telemetry-workers 1`: about 16 seconds
- `--telemetry-workers 4`: about 17 seconds
- `--telemetry-workers 8`: about 18 seconds

That result suggests the importer is no longer bottlenecked by per-lap FastF1
calls. The remaining work is mostly raw sample shaping plus COPY writes, so
oversubscribing extraction workers is not useful. Increasing the sample batch
size from `20000` to `100000` shaved about one more second off the warm-cache
Monza 2024 import, and parallel telemetry/position COPY shaved roughly another
two seconds. The resulting Timescale hypertables used about 524 MB for
`telemetry_samples`, 168 MB for `position_samples`, and 192 kB for
`weather_samples` before compression tuning.

For many sessions, parallelize across sessions instead of parallelizing the
small context tables inside one session. The context tables are tiny compared
with telemetry and position samples, while each session import already copies
the two large sample tables concurrently. Start bulk imports with `--workers 2`;
raise it only after watching database CPU, disk I/O, and connection count.
