# AGENTS.md

## Project
Race Telemetry Workbench — local F1 telemetry app. Imports public race data into
TimescaleDB, exposes it via a .NET Query API and MCP server, replays it in a
high-performance .NET MAUI desktop app.

**Authoritative references:**
- `f1_telemetry_architecture_spec_focused.md` — architecture spec
- `planning.md`
- `docs/design-system/DESIGN_SYSTEM.md` — Carbon Signal UI rules and component patterns
- `docs/design-system/design-tokens.json` — design-token source of truth
- `docs/design-system/styleguide.html` — live component and layout reference

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
| Query API | `src/RaceTelemetry.QueryApi/` |
| Shared contracts | `src/RaceTelemetry.Contracts/` |
| Query data abstraction + PostgreSQL store | `src/RaceTelemetry.Data/` |
| MCP server | `src/RaceTelemetry.McpServer/` |
| Agent class library | `src/RaceTelemetry.Agent/` |
| Agent API (AG-UI / SSE) | `src/RaceTelemetry.AgentApi/` |
| .NET MAUI desktop app | `src/RaceTelemetry.Desktop/` |
| 2025 bad-lap telemetry EDA | `notebooks/2025_telemetry_bad_lap_eda.ipynb`, `notebooks/telemetry_bad_lap_support.py` |
| 2025 database-surface EDA | `notebooks/race_database_surface_eda.ipynb`, `notebooks/database_surface_quality_support.py` |
| Unit + integration tests | `tests/`, `tests/RaceTelemetry.IntegrationTests/`, `tests/RaceTelemetry.McpServer.Tests/`, `tests/RaceTelemetry.AgentApi.Tests/` |
| Bruno collections | `bruno/race-telemetry-query-api/`, `bruno/race-telemetry-agent-api/` |
| Docs | `docs/`, `db/README.md`, `db/schema.md` |

The Query API, MCP server, Agent API, and desktop app all share the read-only
analytical surface through `RaceTelemetry.Data` and `RaceTelemetry.Contracts`.
Keep API, MCP, agent, and contract changes in parity unless a divergence is
intentionally documented.

Verified local imports include full-context Monza race data for 2024 and 2025.
Season backfills should start conservatively at `--workers 2` or `--workers 3`;
`--workers 6` overloaded the local database/container path.

Data-quality EDA artifacts currently live under:

| EDA | Notebook | Summary | Backlog | Artifacts |
|---|---|---|---|---|
| 2025 bad-lap telemetry quality | `notebooks/2025_telemetry_bad_lap_eda.ipynb` | `docs/data-quality/2025-telemetry-bad-lap-eda-summary.md` | `docs/data-quality/2025-telemetry-bad-lap-eda-backlog.md` | `artifacts/2025-telemetry-bad-lap-eda/` |
| 2025 race database-surface quality | `notebooks/race_database_surface_eda.ipynb` | `docs/data-quality/race-database-surface-eda-summary.md` | `docs/data-quality/2025-race-database-surface-eda-backlog.md` | `artifacts/race-database-surface-eda/` |

Future EDA work should use **2025 race sessions only** (`year = 2025`,
`session_type = 'R'`) unless another scope is explicitly requested and
documented as a comparison baseline. The current local DB also contains partial
2024 and 2026 race imports; do not mix those into 2025 season conclusions.

---

## Next Work (priority order)

1. Add focused real-database tests for Query API analytical endpoints
2. Build the high-performance .NET MAUI desktop replay surface against the Query API
3. Add deeper performance validation against larger imported datasets
4. Improve position-aware corner matching for telemetry windows

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
.venv/bin/python -m pip install -r notebooks/requirements.txt

# Tests
.venv/bin/python -m unittest discover -s tests                  # unit
.venv/bin/python -m unittest tests.test_database_migrations     # integration (needs DB)

# .NET
dotnet restore RaceTelemetryWorkbench.slnx
dotnet build RaceTelemetryWorkbench.slnx
dotnet test RaceTelemetryWorkbench.slnx
dotnet run --project tests/RaceTelemetry.IntegrationTests/RaceTelemetry.IntegrationTests.csproj
dotnet run --project tests/RaceTelemetry.McpServer.Tests/RaceTelemetry.McpServer.Tests.csproj
dotnet run --project tests/RaceTelemetry.AgentApi.Tests/RaceTelemetry.AgentApi.Tests.csproj
aspire start

# Agent API secrets (one-time setup)
dotnet user-secrets set "Parameters:openai-api-key" "sk-..." --project src/RaceTelemetry.AppHost
dotnet user-secrets set "Parameters:openai-model" "gpt-4o" --project src/RaceTelemetry.AppHost

# Agent API smoke test (Aspire must be running)
curl http://127.0.0.1:5124/health/ready
curl -s -N -X POST http://127.0.0.1:5124/ag-ui \
  -H "Content-Type: application/json" -H "Accept: text/event-stream" \
  -d '{"threadId":"00000000-0000-7000-8000-000000000001","messages":[{"id":"1","role":"user","content":"List available sessions"}]}'

# Database
docker compose up -d timescaledb

# Download
.venv/bin/python scripts/download_session.py --year 2024 --event Monza
.venv/bin/python scripts/download_session.py --year 2024 --event Monza --drivers LEC --limit-laps 3

# Import
.venv/bin/python scripts/import_session.py --year 2024 --event Monza --drivers LEC --limit-laps 1 --mode replace
.venv/bin/python scripts/import_sessions.py --year 2025 --workers 2 --mode upsert

# Storage estimate
.venv/bin/python scripts/estimate_storage.py
```

Set `RACE_TELEMETRY_DATABASE_URL` to target a non-default database.

See `docs/development.md` for the full day-to-day command guide.

Use Aspire for the distributed app loop; do not run the AppHost with
`dotnet run`. In automated runs prefer `aspire start --non-interactive`,
`aspire wait query-api`, `aspire ps`, `aspire describe`, and `aspire stop`.
Aspire 13.4 writes diagnostics under `~/.aspire`, so sandboxed agent runs may
need approval for `aspire` commands.

Aspire debugging note: stable Query API ports must be owned by Aspire/DCP, not
by Kestrel directly. Prefer `WithHttpEndpoint(port: 5120, env:
"ASPNETCORE_HTTP_PORTS")` in the AppHost. Do not hard-code
`ASPNETCORE_URLS=http://127.0.0.1:5120` for the Aspire-managed Query API.
If `query-api` is `Finished`, run `aspire logs query-api --non-interactive`
before changing endpoint code.

If .NET builds fail with file-lock errors while Aspire is running, stop Aspire
before rebuilding:

```bash
aspire stop
dotnet build RaceTelemetryWorkbench.slnx
```

DB integration tests create a temp schema, apply real migrations, insert Monza
fixture data, verify hypertables and views, then drop the schema.

---

## Application Testing

Use the .NET MAUI DevFlow agent for desktop UI smoke tests and visual
verification. Debug builds of `src/RaceTelemetry.Desktop` register
`Microsoft.Maui.DevFlow.Agent` on port `9223`; the project-local config lives at
`src/RaceTelemetry.Desktop/.mauidevflow`.

When testing the app end to end, start the distributed backend with Aspire
first, run the Mac Catalyst debug app, then inspect the live UI from another
shell:

```bash
aspire start --non-interactive
aspire wait query-api
dotnet build src/RaceTelemetry.Desktop -t:Run -f net10.0-maccatalyst

maui devflow ui tree
maui devflow ui screenshot --output screenshot.png --overwrite
maui devflow mcp
```

Prefer DevFlow screenshots and UI-tree inspection for launcher/console/replay
layout checks instead of relying only on code review. Use `maui devflow mcp`
when an agent needs interactive inspection of the running MAUI app.

---

## Codebase Notes

- `RaceTelemetry.QueryApi` is a Minimal API. Endpoint registration and route
  handlers live in `RaceTelemetryApi.cs`; validation helpers live in
  `RaceTelemetryApi.Validation.cs`.
- `RaceTelemetry.McpServer` exposes equivalent read-only tools. Validation
  helpers live in `RaceTelemetryMcpTools.Validation.cs`.
- `RaceTelemetry.Data` owns the query-store abstraction and PostgreSQL SQL.
  Keep hot-path analytical and replay queries bounded by explicit time ranges,
  row limits, and requested channels.
- `RaceTelemetry.Agent` owns OpenAI client construction (`GetChatClient().AsIChatClient()`),
  MCP tool discovery (`HttpClientTransport` + `McpClient.CreateAsync`), and the
  `AgentInstructions` system prompt. Keep provider-specific code isolated here.
- `RaceTelemetry.AgentApi` hosts the AG-UI SSE endpoint (`POST /ag-ui`), the
  in-memory session registry, and the agentic streaming loop. The OpenAI API key
  is injected by Aspire via user secrets and never leaves this process. Sessions
  are in-memory, keyed by `threadId`, and lost on restart by design.
- The desktop sends only the current UI selection (session key, selected drivers,
  lap, active view) as `TelemetryWorkspaceContext` in the AG-UI `state` field.
  The agent uses this as natural-language context to form MCP tool call
  arguments — it does not trust this state as authoritative telemetry data.
- AG-UI protocol is implemented manually via SSE (no official .NET AG-UI hosting
  package exists as of this writing). Streaming type is `ChatResponseUpdate`
  (not `StreamingChatCompletionUpdate`).
- `RaceTelemetry.Contracts` is the shared DTO boundary for API, MCP, agent, and
  desktop. Prefer additive DTO changes and open-ended known-value handling for
  client compatibility.
- Query API errors should stay aligned with RFC-style problem responses.
- Replay, analytical, and comparison paths should preserve backend `null`
  values rather than inventing client-friendly defaults.
- The desktop app folder is only a placeholder. Do not start MAUI work until
  the Query API data path has focused real-database analytical endpoint tests.
- Data-quality notebooks use `skrub` and repo-local writable cache directories.
  Keep generated cache folders ignored, but preserve intentional notebook
  outputs such as markdown summaries, SVG figures, CSV/Parquet tables,
  metadata JSON, and `skrub` HTML reports when they are part of the requested
  deliverable.

---

## Design System And Assets

- The desktop UI follows **Carbon Signal**. Treat `docs/design-system/DESIGN_SYSTEM.md`
  as the authority for launcher flow, console shell, overview, field, replay,
  strategy, incidents, typography, spacing, and component behavior.
- Treat `docs/design-system/design-tokens.json` as the token source of truth.
  Keep MAUI theme values aligned with it; do not invent parallel token names or
  silently drift colors, spacing, radii, or typography away from the design
  system.
- Use `docs/design-system/styleguide.html` to verify concrete component shapes:
  session chips, driver multi-select chips, panel headers, command bar, HUD
  strip, tables, badges, and timeline/context patterns.
- **Amber has one meaning:** primary action, selection, focus, and replay
  cursor. Do not use amber as a telemetry data-series color.
- Default driver identity in the launcher, field, and position trace is the
  project-owned **categorical palette** (`DriverPalette`), not team livery.
  The design-system default is team-free categorical rails and chips.
- Team-livery presentation is an explicit opt-in mode only. If added, keep it
  clearly separate from the default categorical mode and do not let it replace
  the project-owned palette across the product.
- Use only project-owned or generated assets. Do **not** pull in external team
  logos, car renders, track art, or other third-party visuals unless the task
  explicitly adds a licensed local asset set and documents that decision.
- National flags are allowed as factual identifiers for circuits/countries in
  the launcher. Team liveries and branding are not the default launcher visual
  language.
- Track outlines must be derived from imported `position_samples`. Do not use
  static track SVGs, screenshots, or bundled circuit artwork.
- When a requested overview or panel field is not present in the current Query
  API / import surface, render it explicitly as unavailable (`--`,
  `not imported`, etc.) rather than guessing or fabricating values.

---

## Working Style

- Stay aligned with the spec and `planning.md`; update planning state when it changes.
- Prefer small, verifiable slices.
- Preserve race-default behavior; keep non-race support opt-in.
- Favor real DB/integration coverage for Query API behavior over mocks when
  touching analytical endpoints, replay endpoints, query SQL, or migrations.
- Keep Aspire endpoint ownership clear: stable public ports belong in AppHost,
  while project resources listen on Aspire-injected internal ports.
- Keep imports and tests locally reproducible; use Monza 2024/2025 and limited
  driver/lap slices for fast checks before broad season runs.
- When FastF1 source behavior matters, trust the raw source tables listed above
  and document any discovered quirks in `planning.md`.
- Do not commit: `.venv/`, `.idea/`, `.DS_Store`, `__pycache__/`, `*.pyc`, `data/fastf1-cache/`, `data/download-manifests/`.
