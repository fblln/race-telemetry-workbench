# Race Telemetry Workbench

Race Telemetry Workbench is a local Formula 1 telemetry analysis project. It
imports public FastF1 race data into TimescaleDB, exposes the data through a
.NET Query API and HTTP MCP server, and is evolving toward a desktop replay and
natural-language race analysis workbench.

The project is built around one goal: turn raw race telemetry into useful
engineering and race-strategy insight without requiring every user to write SQL
or manually inspect thousands of samples.

## What It Does Today

### Import Full Race Context

The Python importer can load race sessions from FastF1 into PostgreSQL /
TimescaleDB:

- sessions, drivers, and laps
- raw car telemetry: speed, throttle, brake, gear, RPM, DRS
- raw position samples for replay and track maps
- tyre compound, tyre life, stint, pit-in, and pit-out lap metadata
- weather samples
- circuit metadata and corner/marshal markers
- track status, session status, and race-control messages

Race sessions (`R`) are the default. Practice, qualifying, sprint qualifying,
and sprint sessions are explicit opt-ins.

### Store Data For Replay And Analysis

The database schema combines ordinary PostgreSQL tables with TimescaleDB
hypertables. It also includes analytical views for common questions:

- lap summaries
- stint summaries
- weather summaries
- track-status periods
- race-control search
- telemetry event candidates

This keeps high-volume samples queryable while giving API and MCP clients compact
summary surfaces.

### Query API

The .NET Query API exposes bounded REST endpoints for:

- listing sessions, drivers, and laps
- fetching lap telemetry with sampling and row limits
- comparing two laps by lap-relative time buckets
- replay metadata, replay chunks, and replay context
- searching telemetry event candidates
- race story, lap story, braking zones, and lap comparison story responses

The API uses shared contracts from `RaceTelemetry.Contracts` and a
Timescale-backed query store in `RaceTelemetry.Data`.

### MCP Server

The MCP server exposes read-only Streamable HTTP tools for Codex, Claude, and
other MCP-compatible clients. It is designed for natural-language questions such
as:

- "What happened in the Monza race?"
- "Compare Leclerc and Hamilton on lap 53."
- "Where were the main braking zones?"
- "What context should I know before replaying this stint?"

Current MCP tools return bounded JSON and reuse the same query-store contracts
as the Query API.

### Bruno Collection

The Bruno collection under `bruno/race-telemetry-query-api` is ready for manual
testing against the local Query API. It includes requests for the core API,
replay endpoints, event search, and story-oriented analytical responses.

## Current Architecture

```text
FastF1
  |
  v
Python import scripts
  |
  v
TimescaleDB / PostgreSQL
  |
  v
.NET Query API  <---- Bruno / MAUI desktop app
  |
  v
HTTP MCP Server <---- Codex / Claude / MCP clients
```

The desktop app project slot exists, but the .NET MAUI UI is not implemented yet.

## Quick Start

Start TimescaleDB:

```bash
docker compose up -d timescaledb
```

Install Python dependencies:

```bash
python3 -m venv .venv
.venv/bin/python -m pip install -r scripts/requirements.txt
```

Download and import a small Monza slice:

```bash
.venv/bin/python scripts/download_session.py --year 2024 --event Monza --drivers LEC --limit-laps 3
.venv/bin/python scripts/import_session.py --year 2024 --event Monza --drivers LEC --limit-laps 1 --mode replace
```

Build .NET projects:

```bash
dotnet restore RaceTelemetryWorkbench.slnx
dotnet build RaceTelemetryWorkbench.slnx
```

Run with Aspire:

```bash
aspire start
```

Stable local ports:

| Resource | URL |
|---|---|
| Query API | `http://127.0.0.1:5120` |
| MCP server | `http://127.0.0.1:5122/mcp` |
| Aspire Dashboard | `https://127.0.0.1:18888` |

Open the Bruno collection:

```text
bruno/race-telemetry-query-api
```

## Example API Calls

```bash
curl http://127.0.0.1:5120/api/sessions
curl http://127.0.0.1:5120/api/sessions/2025-italian-grand-prix-r/replay/metadata
curl http://127.0.0.1:5120/api/sessions/2025-italian-grand-prix-r/story
curl http://127.0.0.1:5120/api/sessions/2025-italian-grand-prix-r/drivers/LEC/laps/53/story
curl http://127.0.0.1:5120/api/sessions/2025-italian-grand-prix-r/drivers/LEC/laps/53/braking-zones
```

Register the MCP server with Codex CLI:

```bash
codex mcp add race-telemetry-aspire --url http://127.0.0.1:5122/mcp
codex mcp list
```

## Natural-Language Analysis Primitives

The Query API and MCP server include compact analytical primitives so complex
questions do not require the MCP client to download all telemetry samples for a
driver or race.

- `aggregate_telemetry`
  - grouped metrics such as DRS active time, brake time, average speed, max
    speed, sample count, and throttle-lift count
- `detect_telemetry_windows`
  - compact intervals for DRS activation, hard braking, throttle lifts, and
    high-speed periods
- `analyze_driver_stints`
  - tyre degradation, stint lap-time slope, best/worst lap, tyre-life range,
    and compound strategy summaries
- `analyze_pit_stops`
  - pit-in/out markers, nearby non-pit baselines, and estimated pit-lap loss
- `get_weather_trend`
  - weather deltas and rainfall summary for a session or selected time window
- `get_race_control_timeline`
  - searchable race-control timeline with category, flag, and status counts
- `get_circuit_context`
  - imported circuit rotation, corner markers, marshal lights, and marshal
    sectors

The Query API and MCP server stay in sync: every analytical MCP tool is backed
by a shared contract and Query API route.

## What Is Coming Next

The next evolution moves toward:

- focused real-database tests for analytical Query API endpoints
- high-performance .NET MAUI desktop replay workspace
- data-derived track map and driver replay
- timeline overlays for weather, flags, safety car, VSC, and race control
- lap comparison UI
- optional AI assistant panel beside the race data

## Repository Map

| Path | Purpose |
|---|---|
| `scripts/` | FastF1 download, import, bulk import, and storage estimate scripts |
| `db/migrations/` | PostgreSQL / TimescaleDB schema, hypertables, indexes, and views |
| `src/RaceTelemetry.QueryApi/` | ASP.NET Core Query API |
| `src/RaceTelemetry.McpServer/` | HTTP MCP server |
| `src/RaceTelemetry.Data/` | Query-store abstraction and PostgreSQL implementation |
| `src/RaceTelemetry.Contracts/` | Shared API/MCP/Desktop DTOs |
| `src/RaceTelemetry.AppHost/` | Aspire AppHost |
| `bruno/race-telemetry-query-api/` | Bruno collection for manual API testing |
| `docs/` | Development, data, API/MCP, and OpenAPI docs |
| `planning.md` | Backlog, decisions, and progress tracking |

## License

GNU GPLv3. See `LICENSE`.
