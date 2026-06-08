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
| .NET solution | `RaceTelemetryWorkbench.slnx` |
| Aspire AppHost | `src/RaceTelemetry.AppHost/` |
| Query API scaffold | `src/RaceTelemetry.QueryApi/` |
| Shared contracts | `src/RaceTelemetry.Contracts/` |
| Query data abstraction | `src/RaceTelemetry.Data/` |
| MCP server | `src/RaceTelemetry.McpServer/` |
| Avalonia app slot | `src/RaceTelemetry.Desktop/` |
| Unit + integration tests | `tests/` |
| Docs | `docs/`, `db/README.md`, `db/schema.md` |

**Not yet implemented:** Avalonia desktop app · full MCP analytical prompt surface.

---

## Next Work (priority order)

1. Validate Query API and MCP analytical primitives against imported datasets
2. Add pit-stop loss, weather delta, race-control, and corner/sector summaries
3. Build the Avalonia desktop replay surface against the Query API
4. Add deeper performance validation against larger imported datasets

Keep the next work focused on the Query API data path before starting the
Avalonia UI surface.

---

## Session Default

`--session R` (race) is the default everywhere. Non-race sessions (`FP1`, `FP2`,
`FP3`, `Q`, `SQ`, `S`) are **explicit opt-ins only**.

---

## FastF1 Data Sources

| Purpose | Source |
|---|---|
| Driver abbreviations | `session.get_driver(ref)["Abbreviation"]` — do NOT assume `session.drivers` contains three-letter codes |
| Telemetry samples | `session.car_data` raw driver streams |
| Position samples | `session.pos_data` raw driver streams |
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

# .NET
dotnet restore RaceTelemetryWorkbench.slnx
dotnet build RaceTelemetryWorkbench.slnx
dotnet run --project tests/RaceTelemetry.IntegrationTests/RaceTelemetry.IntegrationTests.csproj
aspire start

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

See `docs/development.md` for the full day-to-day command guide.

Aspire debugging note: stable Query API ports must be owned by Aspire/DCP, not
by Kestrel directly. Prefer `WithHttpEndpoint(port: 5120, env:
"ASPNETCORE_HTTP_PORTS")` in the AppHost. Do not hard-code
`ASPNETCORE_URLS=http://127.0.0.1:5120` for the Aspire-managed Query API.
If `query-api` is `Finished`, run `aspire logs query-api --non-interactive`
before changing endpoint code.

DB integration tests create a temp schema, apply real migrations, insert Monza
fixture data, verify hypertables and views, then drop the schema.

---

## Working Style

- Stay aligned with the spec and planning files; update planning docs when state changes.
- Prefer small, verifiable slices.
- Preserve race-default behavior; keep non-race support opt-in.
- Do not commit: `.venv/`, `.idea/`, `.DS_Store`, `__pycache__/`, `*.pyc`, `data/fastf1-cache/`, `data/download-manifests/`.
