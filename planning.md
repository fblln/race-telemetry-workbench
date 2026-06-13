# Planning

This file is the single planning surface for implementation status, next work,
backlog, and durable project decisions. The architecture source of truth remains
`f1_telemetry_architecture_spec_focused.md`.

## Current Status

The Python/TimescaleDB import foundation is implemented: Docker setup,
migrations, hypertables, analytical views, schema docs/DBML, migration tests,
download validation, single-session import, and bulk season import are in
place. The importer uses raw FastF1 `session.car_data` and `session.pos_data`
streams, not composed telemetry.

The .NET Query API is implemented with a Timescale-backed query store, bounded
replay endpoints, lap telemetry, lap comparison, story-oriented analytical
endpoints, telemetry-event search, RFC-style problem responses, OpenAPI, Bruno
requests, and Aspire/OpenTelemetry wiring. The MCP server exposes the same
read-only analytical surface over Streamable HTTP.

The desktop folder is still a placeholder; no .NET MAUI project or replay UI
exists yet.

The current local database has been verified with full-context Monza race
imports for 2024 and 2025. A high-concurrency all-2025 import with
`--workers 6` overloaded the local database/container path. Future season
imports should start at `--workers 2` or `--workers 3`.

## Next Recommended Work

1. Add focused real-database tests for Query API analytical endpoints.
2. Build the high-performance .NET MAUI desktop replay surface against the
   stable Query API ports.
3. Add deeper performance validation against larger imported datasets.
4. Improve position-aware corner matching for telemetry windows.

Keep the next work focused on the Query API data path before starting the MAUI
desktop UI surface.

## Phase Tracker

| Phase | Status | Notes |
|---|---|---|
| Planning scaffold | Done | Planning, decisions, backlog, progress tracking, and repo instructions are now consolidated here and in `AGENTS.md`. |
| Phase 1 - Database and Import | Mostly done | TimescaleDB Docker setup, migrations, analytical views, schema docs/DBML, integration tests, optimized importer, bulk importer, full-context imports, and Monza 2024/2025 verification are in place. TimescaleDB still runs through Docker Compose rather than as an Aspire-managed resource. |
| Phase 2 - Query API | Implemented, needs coverage/perf hardening | .NET 10 solution, Aspire AppHost, ServiceDefaults, contracts, Timescale-backed data store, RFC-style problems, replay/context, lap telemetry, comparison, race/lap stories, analytical primitives, telemetry-event search, OpenAPI, Bruno, and HTTP integration runner are in place. Needs focused real-database analytical tests and larger-dataset performance validation. |
| Phase 3 - Desktop Replay | Not started | `src/RaceTelemetry.Desktop` is a placeholder. Version 1 is scoped as a high-performance .NET MAUI desktop workbench with session browser, replay workspace, track map, waveform, current values, lap summary, event timeline, context strip, driver summary, pit summary, linked timebase/cursor behavior, context overlays, viewport-aware rendering, virtualized lists, and playback verification. |
| Phase 4 - Lap Comparison | Backend done, UI not started | Query API and MCP lap comparison endpoints exist. Version 1 comparison is limited to two laps in one session with lap-time-aligned overlays, sector/lap deltas, cursor/reference-cursor values, channel deltas, lap metadata, and UI verification. |
| Phase 5 - MCP Query Server | Implemented, needs deeper external validation | Read-only Streamable HTTP MCP server exposes sessions, drivers, laps, replay metadata, lap telemetry, stories, braking zones, comparisons, race story, analytical primitives, replay chunk/context, and telemetry-event search. HTTP protocol smoke-test runner is in place. |
| Phase 6 - AI Assistant Panel | Not started | Optional first UI iteration after MCP works externally. |

## Backlog

### Phase 1 - Database And Import

- [ ] Move TimescaleDB from standalone Docker Compose into Aspire-managed local resources.
- [ ] Derive the desktop track outline from imported `position_samples`, not external track assets.
- [ ] Tune season-import concurrency after completing a full 2025 season import at `--workers 2` or `--workers 3`.
- [ ] Ensure `circuit_markers` corner attribution is consistently available for
  imported sessions so `telemetry/windows`'s `nearestCorner` and the new
  `corners/compare` endpoint (§6.10.6) can rely on it.

Completed highlights:

- [x] Docker Compose TimescaleDB setup.
- [x] Migrations for relational metadata/context tables and Timescale
  hypertables.
- [x] Analytical views: `lap_summaries`, `driver_stint_summaries`,
  `session_weather_summary`, `track_status_periods`,
  `race_control_event_index`, and `telemetry_event_candidates`.
- [x] Download validation script and raw FastF1 cache workflow.
- [x] Single-session importer with `fail`, `replace`, and `upsert` modes.
- [x] Bulk full-context importer.
- [x] Raw car telemetry, position, circuit markers, weather, track status,
  session status, and race-control import.
- [x] Stable `session_id` and `lap_id` generation.
- [x] Real TimescaleDB migration integration tests.

### Phase 2 - Query API

- [ ] Add focused Query API analytical endpoint tests against the real database.
- [ ] Add `sessionIdB` to `compare/laps` for cross-session lap comparison
  (§6.5), restricted to sessions at the same circuit.
- [ ] Add `POST /api/sessions/{sessionId}/strategy/summarize` (§6.10.4):
  pit-stop timing, undercut/overcut labels, pit-lane loss vs. field average,
  and short narrative facts, composed from existing stint/pit/track-status
  data.
- [ ] Add `POST /api/sessions/{sessionId}/debrief` (§6.10.5): bounded,
  section-based race summary (overview, incidents, strategy, weather)
  composed from existing story/strategy/weather endpoints.
- [ ] Add `POST /api/sessions/{sessionId}/corners/compare` (§6.10.6):
  per-corner braking/exit comparison across drivers using `circuit_markers`
  and `telemetry/windows` corner attribution.

Completed highlights:

- [x] ASP.NET Core Minimal API, .NET 10 solution, Aspire AppHost, and
  ServiceDefaults.
- [x] Shared contracts and `IF1TelemetryQueryStore`.
- [x] PostgreSQL-backed sessions, drivers, laps, lap telemetry, replay metadata,
  replay chunk, and replay context.
- [x] Race/lap story, braking-zone, lap comparison, and lap comparison story
  endpoints.
- [x] Analytical primitives for telemetry aggregate/windows, stints, pit stops,
  weather trend, race-control timeline, and circuit context.
- [x] Telemetry-event search.
- [x] RFC-style problem responses, validation, OpenAPI, Bruno collection, and
  HTTP integration runner.
- [x] PostgreSQL tracing, startup connection warmup, and first-pass query
  round-trip optimizations.

### Phase 3 - Desktop Replay

- [ ] Create the .NET MAUI desktop project.
- [ ] Add CommunityToolkit.Mvvm.
- [ ] Add a high-performance drawing stack for track map, waveform, and timeline
  rendering.
- [ ] Add virtualized native table/list controls for lap, driver, event, and
  race-control rows.
- [ ] Build Version 1 as an opinionated fixed layout, not a configurable
  analytics toolkit.
- [ ] Build the Session Browser with race-default filtering, search,
  selected-session details, and context availability flags.
- [ ] Build the Replay Workspace with fixed first-pass docked panels.
- [ ] Structure replay panels as independent components so later saved layouts
  or resizing do not require a rewrite.
- [ ] Implement replay controls: play, pause, restart, timeline seek, speed
  selector, driver selector, and channel selector.
- [ ] Implement a linked session-relative timebase shared by all replay panels.
- [ ] Implement cursor seek from timeline, waveform, event rows, lap rows, and
  timestamped track-map selections.
- [ ] Implement optional reference cursor for analysis views.
- [ ] Load replay metadata when opening a session.
- [ ] Load the first replay chunk before playback and keep at least one future
  chunk buffered.
- [ ] Load context windows for weather, flags, safety car, VSC, red flags, DRS
  messages, and race-control messages.
- [ ] Cancel in-flight chunk requests on seek/session/driver/channel changes.
- [ ] Preserve backend `null` values.
- [ ] Keep chart, track-map, and timeline rendering bounded by visible pixels
  and visible time range.
- [ ] Avoid one UI element per telemetry sample.
- [ ] Keep replay HTTP calls, JSON parsing, downsampling, and derived metric
  calculations off the UI thread.
- [ ] Cache replay metadata, static session context, and recently used chunks
  with explicit size limits.
- [ ] Build track map, waveform, current values, lap summary, event timeline,
  context strip, compact pit summary, and compact driver summary panels.
- [ ] Show current weather at the replay timestamp.
- [ ] Shade timeline/charts for yellow, safety car, VSC, and red-flag periods.
- [ ] Show race-control messages as inspectable timeline markers.
- [ ] Mark rainfall periods when weather reports rain.
- [ ] Keep UI styling and documentation assets project-owned and original.
- [ ] Verify playback at `1x` and `5x`.
- [ ] Verify seeking keeps map, waveform, current values, lap summary, and
  context overlays synchronized.
- [ ] Verify replay remains responsive on larger imported datasets.

### Phase 4 - Lap Comparison

- [ ] Build the Lap Comparison screen.
- [ ] Add inputs for Driver A, Lap A, Driver B, Lap B, and channels.
- [ ] Call `/api/sessions/{sessionId}/compare/laps`.
- [ ] Display lap-time-aligned overlay charts, lap-time delta, sector deltas,
  per-channel cursor values/deltas, and lap metadata.
- [ ] Use delta convention `driverA - driverB`.
- [ ] Keep distance-based alignment out of scope until a reliable distance or
  position-aware alignment model exists.

### Later Desktop Analysis Modules

- [ ] Saved and rearrangeable workspaces.
- [ ] Dedicated driver profile view.
- [ ] Dedicated pit analysis view.
- [ ] Multi-driver lap ranking and mini-sector style comparison.
- [ ] Cross-session lap comparison UI (second-session picker over the
  `sessionIdB` backend contract, §6.5).
- [ ] Histogram, load-map, and scatter displays backed by bounded aggregate
  endpoints.
- [ ] Event search builder for telemetry windows.
- [ ] Local notes and bookmarks.
- [ ] Export of project-owned charts and tables.

### Phase 5 - MCP Query Server

Completed:

- [x] .NET MCP server project with HTTP transport for Aspire and coding-agent
  integration.
- [x] Tools call the shared query-store abstraction and remain read-only.
- [x] Tools for sessions, drivers, laps, replay metadata/context/chunk, lap
  telemetry/story/braking zones, lap comparison/story, race story, analytical
  primitives, circuit context, and telemetry-event search.
- [x] Story, aggregate, window, and stint tools are preferred before raw
  telemetry for natural-language analysis.
- [x] Bounded structured JSON responses and one trace span per tool call.
- [x] Focused MCP server tests.

Backlog:

- [ ] Add `summarize_strategy`, `generate_race_debrief`, and `compare_corners`
  MCP tools as thin adapters over the new Query API endpoints (§9.2.1, §9.3),
  once those endpoints exist.
- [ ] Add cross-session support to the `compare_laps` MCP tool via
  `sessionIdB`, matching the Query API contract.

### Phase 6 - Optional AI Assistant Panel

- [ ] Add AI Assistant Panel to the MAUI desktop app.
- [ ] Display MCP-derived answers beside session data.
- [ ] Support opening replay or lap comparison from relevant answers.

### Cross-Cutting

- [x] Validate all channel names against allow-lists.
- [x] Prevent arbitrary SQL exposure.
- [x] Bound large responses with downsampling or pagination.
- [x] Keep MCP tools read-only.
- [x] Make validation errors explicit and actionable.
- [x] Add Aspire observability for .NET services.
- [ ] Track performance targets for core operations.
- [ ] Track desktop UI performance targets for replay, seek, chart redraw, and
  virtualized scrolling.

## Decisions

### 2026-06-06 - Track Architecture Spec In Git

The architecture spec is tracked in git so product, data, database, API, replay,
MCP, and licensing decisions can be reviewed directly on GitHub.

### 2026-06-06 - Initial Work Order

Follow the implementation phases from the spec: database/import, Query API,
desktop replay, lap comparison, MCP query server, and optional AI assistant
panel.

### 2026-06-06 - GNU GPLv3 License

The project license is GNU GPLv3. It permits use, copying, modification,
distribution, and commercial use under the license terms, while requiring
distributed derivative works to preserve the same freedoms.

### 2026-06-06 - TimescaleDB Primary Storage

TimescaleDB is the primary storage target because the expected scope includes
multi-year, multi-session replay queries, lap comparison, weather/context
overlays, and in-database analytics for MCP-backed questions.

Use ordinary PostgreSQL tables for bounded relational/event metadata, and
Timescale hypertables for high-volume or time-windowed sample data such as
telemetry, position, and weather samples.

DuckDB, ClickHouse, QuestDB, and plain PostgreSQL are not the primary
implementation path. They may be reconsidered later for export/offline analysis
or scale-specific secondary analytics.

### 2026-06-07 - Aspire Stable Port Model

The Query API should be exposed through a stable Aspire/DCP external HTTP port
for manual testing and Bruno. The project process itself must not bind the same
external port.

Use this AppHost pattern:

```csharp
builder.AddProject<Projects.RaceTelemetry_QueryApi>("query-api")
    .WithEnvironment("RACE_TELEMETRY_DATABASE_URL", databaseUrl)
    .WithHttpEndpoint(port: 5120, env: "ASPNETCORE_HTTP_PORTS")
    .WithExternalHttpEndpoints();
```

Avoid hard-coding `ASPNETCORE_URLS` to `http://127.0.0.1:5120` under Aspire,
setting identical `targetPort` and `port` on a proxied project resource, or
starting a second AppHost/Rider-launched Query API on the stable port.

If Aspire Dashboard is running but `query-api` is `Finished`, first inspect
`aspire logs query-api --non-interactive`.

### 2026-06-07 - Natural-Language Analysis Shape

MCP and Query API should expose compact, structured analytical tools in addition
to raw sample retrieval. Natural-language clients should start with race/lap
story tools, braking zones, and comparison stories that return bounded facts,
summaries, and deterministic insight labels.

Raw telemetry, replay chunks, and bucketed comparisons remain available for
drill-down, charting, and validation. They should not be the first tool a
language model needs to call for broad race questions.

### 2026-06-08 - Query API And MCP Analytical Parity

MCP analytical capabilities should stay in sync with the Query API:

- first add or expose a shared contract and Query API route;
- then expose an MCP tool as a thin, read-only adapter over that same bounded
  capability;
- only allow MCP-only analytical tools when a decision record explains why the
  capability is not useful over REST.

For complex natural-language questions, prefer generic analytical primitives
such as `aggregate_telemetry`, `detect_telemetry_windows`, and
`analyze_driver_stints` instead of fetching raw telemetry or adding one tool per
question.

### 2026-06-13 - Original Desktop UI Requirements And Assets

Desktop requirements, documentation, mockups, and assets should use original
language and original generated visuals. Any UI documentation images added later
should be original generated mockups or project diagrams.

### 2026-06-13 - Opinionated Desktop Version 1

The first desktop version should be a fixed, opinionated workbench rather than a
general-purpose configurable analytics environment. Version 1 should ship a
session browser, replay workspace, lap comparison, fixed workspace layout, core
channels selected by default, data-derived track map, waveform, current values,
lap summary, event timeline, context strip, driver summary, and pit summary.

Saved layouts, dedicated driver/pit screens, cross-session comparison,
histogram/load-map/scatter panels, event builders, notes, bookmarks, and export
are later modules.

### 2026-06-13 - Desktop Client Uses .NET MAUI

The desktop application is the main product surface and should be implemented
with .NET MAUI. Desktop ergonomics are first; mobile layouts are out of scope
unless a later decision explicitly adds them.

High performance is a core MAUI requirement: viewport-aware drawing, no UI
element per telemetry sample, responsive replay/seek/redraw/scrolling, off-UI
thread HTTP/JSON/downsampling work, virtualized lists, and explicitly bounded
caches.

### 2026-06-13 - New Analytical Endpoints: Strategy, Debrief, Corner Comparison, Cross-Session

Four new analytical capabilities are added to the spec (§6.10.4-6.10.6, §6.5)
as compositions over existing data and endpoints, not new raw-data access
paths:

- `strategy/summarize` (pit timing, undercut/overcut, narrative facts), built
  from existing stint, pit-stop, track-status, and race-control data.
- `debrief` (bounded, section-based race summary), composed from existing
  race/lap story, weather trend, and the new strategy-summary endpoint.
- `corners/compare`, extending the existing `telemetry/windows`
  `nearestCorner` attribution into a per-corner driver comparison. This
  depends on the Phase 1 backlog item to ensure `circuit_markers` corner
  attribution is consistently available.
- `compare/laps` gains an optional `sessionIdB` for cross-session comparison
  at the same circuit (for example year-over-year), keeping the existing
  single-session behavior as the default.

Each gets a matching MCP tool (`summarize_strategy`, `generate_race_debrief`,
`compare_corners`) following the existing parity rule
(2026-06-08 decision): Query API route first, MCP adapter second.
