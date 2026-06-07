# AGENTS.md

## Project
Race Telemetry Workbench — local F1 telemetry app. Imports public race data into
TimescaleDB, exposes it via a .NET Query API and MCP server, replays it in an
Avalonia desktop app.

**Authoritative references:**
- `f1_telemetry_architecture_spec_focused.md` — architecture spec
- `planning/progress.md`, `planning/backlog.md`, `planning/decisions.md`

**License:** GNU GPLv3. Do not modify `LICENSE`.

---

## Implemented

| Component | Path |
|---|---|
| Race download/validation | `scripts/download_session.py` |
| Storage estimator | `scripts/estimate_storage.py` |
| TimescaleDB importer | `scripts/import_session.py` |
| DB migrations + hypertables + views | `db/migrations/` |
| Local TimescaleDB container | `docker-compose.yml` |
| Unit + integration tests | `tests/` |
| Docs | `docs/`, `db/README.md`, `db/schema.md` |

**Not yet implemented:** TimescaleDB/Aspire setup · .NET solution · Query API · Desktop app · MCP server.

---

## Next Work (priority order)

1. Import full Monza 2024 race into TimescaleDB; verify row counts against manifest
2. Fix any importer edge cases found in step 1
3. Create .NET solution, Aspire host, Query API skeleton

Do not start the Query API, desktop, or MCP work before steps 1–2.

---

## Session Default

`--session R` (race) is the default everywhere. Non-race sessions (`FP1`, `FP2`,
`FP3`, `Q`, `SQ`, `S`) are **explicit opt-ins only**.

---

## FastF1 Data Sources

| Purpose | Source |
|---|---|
| Driver abbreviations | `session.get_driver(ref)["Abbreviation"]` — do NOT assume `session.drivers` contains three-letter codes |
| Telemetry samples | `lap.get_telemetry()` — includes car channels + Distance, RelativeDistance, DriverAhead, DistanceToDriverAhead, Status, Source, X/Y/Z |
| Position samples | `lap.get_pos_data()` |
| Track outline | Derive from imported `position_samples` — do NOT use static track assets |
| Circuit annotations | `session.get_circuit_info()` — rotation, corners, marshal lights/sectors |
| Weather | `session.weather_data` (~1 sample/min) |
| Race control timeline | `session.track_status`, `session.session_status`, `session.race_control_messages` |

---

## Commands

```bash
# Setup
python3 -m venv .venv
.venv/bin/python -m pip install -r scripts/requirements.txt

# Tests
.venv/bin/python -m unittest discover -s tests                  # unit
.venv/bin/python -m unittest tests.test_database_migrations     # integration (needs DB)

# Database
docker compose up -d timescaledb

# Download
.venv/bin/python scripts/download_session.py --year 2024 --event Monza
.venv/bin/python scripts/download_session.py --year 2024 --event Monza --drivers LEC --limit-laps 3

# Import
.venv/bin/python scripts/import_session.py --year 2024 --event Monza --drivers LEC --limit-laps 1 --mode replace

# Storage estimate
.venv/bin/python scripts/estimate_storage.py
```

Set `RACE_TELEMETRY_DATABASE_URL` to target a non-default database.

DB integration tests create a temp schema, apply real migrations, insert Monza
fixture data, verify hypertables and views, then drop the schema.

---

## Working Style

- Stay aligned with the spec and planning files; update planning docs when state changes.
- Prefer small, verifiable slices.
- Preserve race-default behavior; keep non-race support opt-in.
- Do not commit: `.venv/`, `.idea/`, `.DS_Store`, `__pycache__/`, `*.pyc`, `data/fastf1-cache/`, `data/download-manifests/`.