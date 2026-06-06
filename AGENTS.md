# AGENTS.md

Guidance for coding agents working in this repository.

## Project

Race Telemetry Workbench is a local Formula 1 telemetry application. The product
goal is to import public race telemetry, store it in TimescaleDB, expose it
through a .NET Query API and MCP server, and replay/analyze it in an Avalonia
desktop app.

The authoritative product/architecture reference is:

- `f1_telemetry_architecture_spec_focused.md`

Planning files live in:

- `planning/progress.md`
- `planning/backlog.md`
- `planning/decisions.md`

## License

The repository is licensed under GNU GPLv3. Keep `LICENSE` as standard GPLv3
text unless the user explicitly requests a license change.

## Current State

Implemented:

- `scripts/download_session.py`
  - FastF1 race download/validation slice.
  - Defaults to race session `R`.
  - Supports explicit non-race sessions with `--session`.
  - Warms `data/fastf1-cache`.
  - Writes manifests to `data/download-manifests`.
  - Avoids overwriting canonical full-session manifests for driver/lap subsets.
- `scripts/estimate_storage.py`
  - Estimates raw FastF1 cache storage from canonical manifests.
- `db/migrations`
  - Initial PostgreSQL/TimescaleDB schema.
  - Hypertable and index setup.
  - Analytical summary views for Query API and MCP use.
- Docs:
  - `docs/data-download.md`
  - `docs/fastf1-raw-data.md`
  - `docs/storage-estimates.md`
  - `db/README.md`
- Unit tests:
  - `tests/test_download_session.py`
  - `tests/test_database_migrations.py`

Not implemented yet:

- TimescaleDB/Aspire setup.
- `scripts/import_session.py`.
- .NET solution/projects.
- Query API.
- Desktop app.
- MCP server.

## Default Data Scope

Race data is the default scope. Commands and import/download behavior should
default to `--session R`.

Non-race sessions (`FP1`, `FP2`, `FP3`, `Q`, `SQ`, `S`) are explicit opt-ins.

## FastF1 Data Model Notes

Use these FastF1 sources as planned in the spec:

- `session.drivers` may contain racing numbers, not three-letter codes.
  Normalize via `session.get_driver(driver_ref)["Abbreviation"]`.
- `lap.get_telemetry()` is the composed telemetry source for
  `telemetry_samples`.
  It includes car channels plus fields like `Distance`, `RelativeDistance`,
  `DriverAhead`, `DistanceToDriverAhead`, `Status`, `Source`, and `X/Y/Z`.
- `lap.get_pos_data()` is the raw position source for `position_samples`.
- `session.get_circuit_info()` provides map rotation, corners, marshal lights,
  and marshal sectors. Use these as annotations over a data-derived track
  outline.
- `session.weather_data` provides low-frequency weather samples, roughly once
  per minute in the observed Monza 2024 race.
- `session.track_status`, `session.session_status`, and
  `session.race_control_messages` provide safety car, VSC, flags, DRS, and
  other race-control timeline context.

The track outline should be derived from imported `position_samples`, not from
external static track assets.

## Generated And Local Files

Do not commit:

- `.venv/`
- `.idea/`
- `.DS_Store`
- `__pycache__/`
- `*.pyc`
- `data/fastf1-cache/`
- `data/download-manifests/`

These are already ignored.

## Python Commands

Install dependencies into a local virtual environment:

```bash
python3 -m venv .venv
.venv/bin/python -m pip install -r scripts/requirements.txt
```

Run unit tests:

```bash
.venv/bin/python -m unittest discover -s tests
```

Download default race data:

```bash
.venv/bin/python scripts/download_session.py --year 2024 --event Monza
```

Smoke-test a cached subset without overwriting the full manifest:

```bash
.venv/bin/python scripts/download_session.py --year 2024 --event Monza --drivers LEC --limit-laps 3
```

Estimate raw FastF1 storage:

```bash
.venv/bin/python scripts/estimate_storage.py
```

Network access is needed the first time FastF1 downloads a session, weather,
race-control messages, or circuit info. Warm-cache runs may still attempt
schedule revalidation but can fall back to cached responses.

## Recommended Next Work

The best next implementation slice is validating the database foundation in a
real TimescaleDB runtime:

1. Start TimescaleDB locally, preferably through the planned Aspire host once
   the .NET solution exists.
2. Apply `db/migrations/001_initial_schema.sql`,
   `db/migrations/002_timescale_hypertables.sql`, and
   `db/migrations/003_analytical_views.sql` in order.
3. Verify that hypertables, indexes, and analytical views are created.
4. Build `scripts/import_session.py` to write one race into the database.

This should happen before Query API, desktop replay, or MCP work because those
components all depend on imported database data.

## Working Style

- Keep changes aligned with the spec and planning files.
- Update planning docs when implementation state changes.
- Prefer small, verifiable slices.
- Preserve race-default behavior.
- Keep non-race support opt-in.
- Do not commit generated caches, IDE files, or local environment files.
