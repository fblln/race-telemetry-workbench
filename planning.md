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

The desktop folder now contains a .NET MAUI Mac desktop shell using the Carbon
Signal design tokens, launcher funnel, console rail, and first replay/analysis
views. The replay workspace has an initial chunk-backed timebase, seek slider,
current-value readouts, track markers, and waveform rendering against the Query
API. It still needs deeper playback buffering, placeholder analysis modules, and
larger-dataset responsiveness validation.

The current local database has been verified with full-context Monza race
imports for 2024 and 2025. A high-concurrency all-2025 import with
`--workers 6` overloaded the local database/container path. Future season
imports should start at `--workers 2` or `--workers 3`.

A 2025 race-only telemetry data-quality EDA now exists in
`notebooks/2025_telemetry_bad_lap_eda.ipynb`, with supporting code in
`notebooks/telemetry_bad_lap_support.py`, generated artifacts under
`artifacts/2025-telemetry-bad-lap-eda/`, and a written summary in
`docs/data-quality/2025-telemetry-bad-lap-eda-summary.md`. A follow-up backlog
for turning this into a mature data-quality program lives in
`docs/data-quality/2025-telemetry-bad-lap-eda-backlog.md`. The local all-race
2025 import contains 24 race sessions and 26,689 laps; the EDA flags 5,497 laps
(20.6%) with at least one telemetry/timing/position/context quality signal. Raw
FastF1 `Distance` is not currently imported, so distance reset and non-monotonic
distance checks remain explicitly unavailable rather than inferred. The bad-lap
EDA now includes category-overlap, decision-waterfall, primary-category
race-decomposition, selected-race drilldown, `reason_count`/`reason_set`,
quality-lens, safety-flag, severity-score, recommendation, driver/race matrix,
Parquet, threshold, metadata, threshold-sensitivity, and borderline-lap outputs.
The threshold pass uses near-baseline perturbations across sample counts,
coverage, telemetry gaps, path tolerance, and speed-shape limits; the current
borderline review table contains 179 laps whose classification, recommendation,
or safety labels change under at least one scenario. The remaining near-term EDA
follow-ups are to persist and join per-lap Apexline geometry diagnostics, add
deeper shape baseline robustness checks, and decide whether distance should be
imported.
The speed-shape EDA now compares shape outliers against clean green-flag
same-race speed-profile quantile bands, writes representative shape examples,
cluster exemplars, and sampled cluster-stability results. Equal-time bins remain
an explicit limitation until raw/derived distance is imported.

A broader imported race-session database surface EDA now exists in
`notebooks/race_database_surface_eda.ipynb`, with supporting code in
`notebooks/database_surface_quality_support.py`, generated artifacts under
`artifacts/race-database-surface-eda/`, and a written summary in
`docs/data-quality/race-database-surface-eda-summary.md`. It audits all imported
race sessions across session metadata, drivers, laps, raw telemetry, raw
position, aligned 10 Hz replay data, ingestion diagnostics, weather, status
timelines, race-control messages, and circuit annotations. The current local DB
has 32 imported race sessions: 1 from 2024, 24 from 2025, and 7 from 2026. The
2025 season is the only complete season-level slice; 2024 and 2026 should be
treated as partial imports. Future database-surface EDA iterations should narrow
to 2025 race sessions only, following
`docs/data-quality/2025-race-database-surface-eda-backlog.md`, unless another
scope is explicitly requested and documented as a comparison baseline.

The database-surface EDA has been reframed to the 2025 race-only scope in the
same notebook/support module, with guarded outputs under
`artifacts/2025-race-database-surface-eda/` and
`docs/data-quality/2025-race-database-surface-eda-summary.md`. The guardrail
requires exactly 24 2025 race sessions before season conclusions are generated.
The scoped pass finds no raw telemetry, raw position, aligned replay, weather,
status, driver metadata, or circuit marker session-level coverage flags, while
ingestion diagnostics, missing lap times, and race-control timing/sparsity remain
the main follow-up surfaces.

The database-surface EDA now includes an aligned replay quality deep dive:
stable `quality_flags` family decoding, race/driver/lap/30-second-window
aggregates, complete consecutive degraded-segment extraction, pit/status/race
control context overlap, representative replay-quality strips, a driver/race
heatmap, and a desktop replay quality watchlist. The 2025 pass finds 458,158
non-OK aligned rows out of 24,477,559 aligned rows (1.87%). Severe 30-second
windows are rare (133 windows at 10%+ non-OK rows), and the current desktop
guidance is to expose aligned quality primarily as a diagnostics overlay, with
warnings reserved for sustained or repeated degraded windows.

The database-surface EDA also derives 2025 race session duration and surface
coverage windows from imported samples and status events. `session_end_utc` is
missing for all 24 sessions, but `session_status_events` provides a complete
derived duration for every race. Weather and session-status surfaces cover the
aligned active replay window, while race-control and track-status should be
treated as event timelines rather than continuous context. The raw-position
coverage window is currently approximated from UTC timestamps because
`position_samples` lacks `session_time_ms`; add that column during a future
schema/importer pass before using raw-position coverage as a user-facing warning.

The database-surface EDA now includes a deterministic context-surface pass:
race-control message taxonomy, duplicate/near-duplicate message groups,
representative examples by taxonomy bucket, track-status intervals,
status/race-control overlap, weather cadence and value-jump checks, rainfall
transitions, 5-minute context-density bins, and a replay/context correlation
summary. Race-control messages fall into 8 deterministic buckets; the current
pass identifies 308 repeated-message groups, mostly sector flag and blue-flag
families that should be deduplicated or grouped in desktop timelines. Weather
cadence is stable at roughly one sample per minute; real rainfall transitions
are concentrated in Australian, Belgian, British, Miami, Canadian, and Singapore
2025 races. Context-event bins have a higher degraded-window rate than
no-context bins, but current evidence is correlation only and does not justify
blaming race events for aligned replay quality degradation.

The database-surface EDA now converts the 2025 race-only metrics into
product-readiness labels for catalog, raw streams, aligned replay, context, and
circuit context, plus a primary desktop/API recommendation table. All 24 races
currently need UI labeling rather than reimport or inspection, with a separate
schema/importer follow-up for missing `session_end_utc` and raw-position
coverage windows inferred from UTC offsets. Circuit marker QA compares marker
coordinates against imported position bounds and finds no implausible marker
coordinates in the 2025 race set. The same pass adds race-control TF-IDF/KMeans
text clusters and per-race weather trend panels for rainfall or large-shift
races. The implementation backlog for backend and desktop changes is tracked in
`docs/data-quality/eda-driven-product-change-backlog.md`.

The standalone Apexline geometry-validation work is ready to be extracted into
its own sibling repository at `/Users/fabio/Workspace/apexline`. The migration
handoff backlog lives in
`docs/data-quality/apexline-repo-migration-backlog.md`.

## Next Recommended Work

1. Add focused real-database tests for Query API analytical endpoints.
2. Implement the EDA-driven backend/desktop changes in
   `docs/data-quality/eda-driven-product-change-backlog.md`, starting with
   readiness and replay-quality contracts.
3. Build the high-performance .NET MAUI desktop replay surface against the
   stable Query API ports.
4. Add deeper performance validation against larger imported datasets.
5. Improve position-aware corner matching for telemetry windows.
6. Persist per-lap geometry diagnostics from the standalone Apexline workflow if
   bad-lap quality flags become part of the Query API or desktop analysis
   surface.
7. Add raw stream ingestion severity tables that separate normal FastF1 cadence
   from importer/source problems.
8. Consider adding `session_time_ms` to `position_samples` so raw position
   coverage diagnostics do not have to infer session-relative windows from UTC.

Keep the next work focused on the Query API data path before starting the MAUI
desktop UI surface.

## Phase Tracker

| Phase | Status | Notes |
|---|---|---|
| Planning scaffold | Done | Planning, decisions, backlog, progress tracking, and repo instructions are now consolidated here and in `AGENTS.md`. |
| Phase 1 - Database and Import | Mostly done | TimescaleDB Docker setup, migrations, analytical views, schema docs/DBML, integration tests, optimized importer, bulk importer, full-context imports, and Monza 2024/2025 verification are in place. TimescaleDB still runs through Docker Compose rather than as an Aspire-managed resource. |
| Phase 2 - Query API | Implemented, needs coverage/perf hardening | .NET 10 solution, Aspire AppHost, ServiceDefaults, contracts, Timescale-backed data store, RFC-style problems, replay/context, lap telemetry, comparison, race/lap stories, analytical primitives, telemetry-event search, OpenAPI, Bruno, and HTTP integration runner are in place. Needs focused real-database analytical tests and larger-dataset performance validation. |
| Phase 3 - Desktop Replay | In progress | .NET MAUI shell, Carbon Signal styling, launcher circuit/session/driver funnel, console rail/HUD, field/incidents/strategy/position-trace views, and a first chunk-backed replay workspace are in place. Still needs deeper buffering/context overlays, richer analysis screens, and end-to-end AppHost UI verification. |
| Phase 4 - Lap Comparison | Backend in progress, UI not started | The product now distinguishes time-domain comparison from distance-domain comparison. Query API and MCP time-bucket comparison remain in place for synchronized overlays, and the additive distance-domain path now owns where-performance-was-gained analysis. UI still needs explicit Time Overlay and Distance Delta modes. |
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

- [x] Create the .NET MAUI desktop project.
- [x] Add CommunityToolkit.Mvvm.
- [x] Add a high-performance drawing stack for track map, waveform, and timeline
  rendering.
- [ ] Add virtualized native table/list controls for lap, driver, event, and
  race-control rows.
- [ ] Build Version 1 as an opinionated fixed layout, not a configurable
  analytics toolkit.
- [x] Build the Session Browser with race-default filtering, search,
  selected-session details, and context availability flags.
- [x] Tune Carbon Signal and the MAUI shell for the 15-inch MacBook Pro Retina
  density target: 1440x900 logical points at 2x.
- [ ] Build the Replay Workspace with fixed first-pass docked panels.
- [ ] Structure replay panels as independent components so later saved layouts
  or resizing do not require a rewrite.
- [ ] Implement replay controls: play, pause, restart, timeline seek, speed
  selector, driver selector, and channel selector.
- [x] Implement a linked session-relative timebase shared by all replay panels.
- [ ] Implement cursor seek from timeline, waveform, event rows, lap rows, and
  timestamped track-map selections.
- [ ] Implement optional reference cursor for analysis views.
- [x] Load replay metadata when opening a session.
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
- [ ] Decide whether telemetry bad-lap flags should be persisted in the
  database or exposed only as offline notebook diagnostics.
- [ ] Add raw/derived distance import support if distance reset checks become a
  first-class telemetry quality gate.
- [ ] Decide whether session-level surface quality summaries should be persisted
  or exposed only as offline notebook diagnostics.

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

### 2026-06-15 - Timescale Compression Experiment Deferred

An experiment enabled Timescale compression on `telemetry_samples`,
`position_samples`, and `aligned_telemetry_10hz` using segment-by keys
(`session_id,driver_code` for raw tables; `session_key,driver_number` for
aligned telemetry) and `sample_time_utc` ordering.

Measured local storage improved substantially:

| Table | Before | After | Reduction |
|---|---:|---:|---:|
| `telemetry_samples` | 6,111 MB | 167 MB | ~36.5x |
| `position_samples` | 5,795 MB | 111 MB | ~52.2x |
| Raw combined | ~12 GB | ~279 MB | ~42.7x |
| `aligned_telemetry_10hz` | 15 GB | 1,505 MB | ~10.3x |

Hot-cache, replay-shaped bounded query timings did not improve. Most small
window reads were slower due to decompression CPU overhead:

| Query | Before | After |
|---|---:|---:|
| aligned 5s all drivers | 3.57 ms | 7.19 ms |
| aligned 60s single driver | 2.24 ms | 3.03 ms |
| aligned lap 25 all drivers | 39.45 ms | 67.01 ms |
| raw car 60s single driver | 1.17 ms | 1.13 ms |
| raw position 60s single driver | 0.72 ms | 1.07 ms |
| raw car 5s all drivers | 1.36 ms | 3.07 ms |
| raw position 5s all drivers | 1.30 ms | 2.82 ms |

Decision: do not adopt compression as a migration yet. Revisit after measuring
cold-cache reads, larger data volumes, index changes, and chunk/columnstore
settings shaped specifically around replay queries.
