# Progress

## Current Status

The repository has completed the Python/TimescaleDB import foundation and has a
clear database schema, Docker setup, migration tests, raw FastF1 documentation,
single-session importer, and bulk session importer. The .NET Query API now has
a Timescale-backed query store, bounded replay endpoints, lap telemetry,
lap comparison, telemetry-event search, validation/error envelopes, OpenAPI,
and Bruno requests. The Avalonia desktop app has not been scaffolded yet.

The current local database has been verified with full-context Monza race
imports for 2024 and 2025. A high-concurrency all-2025 import was attempted with
`--workers 6`, but that overloaded the local database/container path. Future
season imports should start at `--workers 2` or `--workers 3`.

## Phase Tracker

| Phase | Status | Notes |
|---|---|---|
| Planning scaffold | Done | Added planning docs, decisions, backlog, progress tracking, and repo instructions. |
| Phase 1 - Database and Import | Mostly done | TimescaleDB Docker setup, migrations, analytical views, schema docs/DBML, integration tests, download script, storage estimator, optimized importer, bulk importer, full-context imports, and Monza 2024/2025 verification are in place. Still missing Aspire AppHost wiring. |
| Phase 2 - Query API | Mostly done | .NET 10 solution, Aspire AppHost, ServiceDefaults, contracts, Timescale-backed data store, bounded replay/context endpoints, lap telemetry, lap comparison, telemetry-event search, OpenAPI, Bruno collection, MCP placeholder, and HTTP integration-test runner are in place. Needs deeper performance/observability validation against larger imports. |
| Phase 3 - Desktop Replay | Not started | No Avalonia project exists yet. Needs session selector, replay workspace, controls, charts, data-derived track map, context overlays, and playback verification. |
| Phase 4 - Lap Comparison | Not started | Needs lap-time-aligned comparison endpoint integration and UI. |
| Phase 5 - MCP Query Server | Not started | Needs read-only MCP tools backed by Query API calls. |
| Phase 6 - AI Assistant Panel | Not started | Optional first UI iteration after MCP server works externally. |

## Next Recommended Step

Validate the Timescale-backed Query API against the imported Monza 2024/2025
databases through Bruno and Aspire, then start the Avalonia desktop replay
surface against `/replay/metadata`, `/replay/chunk`, and `/replay/context`.

## Achieved Through Phase 3 Checkpoint

### Phase 1 - Database and Import

- TimescaleDB is available through Docker Compose.
- Schema migrations create relational metadata/context tables and Timescale
  hypertables for telemetry, position, and weather samples.
- Analytical views exist for lap summaries, stint summaries, weather summaries,
  track-status periods, race-control search, and telemetry event candidates.
- Schema documentation and DBML visual ER model exist.
- Database integration tests apply the real migrations against TimescaleDB.
- `scripts/download_session.py` downloads and validates FastF1 race sessions.
- `scripts/import_session.py` imports full-context race sessions by default.
- `scripts/import_sessions.py` imports multiple full-context sessions in
  parallel for season backfills.
- The importer uses raw FastF1 car and position streams, not composed telemetry.
- Monza 2024 and 2025 race imports have been verified with weather, circuit,
  status, and race-control context rows.

### Phase 2 - Query API

- .NET 10 solution scaffold exists.
- Aspire AppHost and ServiceDefaults are in place.
- Query API exposes database-backed sessions, drivers, laps, lap telemetry,
  lap comparison, replay metadata, replay chunk, replay context, and telemetry
  event search endpoints.
- Shared contracts and query-store abstractions are ready for API, desktop, and MCP reuse.
- HTTP integration-test runner covers the implemented endpoint surface.
- Bruno collection exists for manual API checks.

### Phase 3 - Desktop Replay

- Not implemented yet.
- Database tables needed for replay are populated: sessions, drivers, laps,
  telemetry samples, position samples, weather samples, status events,
  race-control messages, and circuit markers.
- The desktop replay can be built once Phase 2 exposes bounded replay metadata,
  chunk, and context endpoints.
