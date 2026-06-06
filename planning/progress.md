# Progress

## Current Status

The repository has the planning scaffold plus the first Python data-download slice. No database schema, .NET services, desktop app, or database importer has been implemented yet.

## Phase Tracker

| Phase | Status | Notes |
|---|---|---|
| Planning scaffold | Done | Added this tracking folder and ignored the local spec document. |
| Phase 1 - Database and Import | Started | Added race-default FastF1 download/validation script, requirements, documentation, cache tests, and storage estimator. Needs Aspire AppHost, TimescaleDB, schema, and database import script. |
| Phase 2 - Query API | Not started | Needs REST endpoints, validation, OpenAPI, and observability. |
| Phase 3 - Desktop Replay | Not started | Needs Avalonia session selector, replay workspace, controls, charts, data-derived track map, and FastF1 circuit marker overlays. |
| Phase 4 - Lap Comparison | Not started | Needs distance-aligned comparison endpoint integration and UI. |
| Phase 5 - MCP Query Server | Not started | Needs read-only MCP tools backed by Query API calls. |
| Phase 6 - AI Assistant Panel | Not started | Optional first UI iteration after MCP server works externally. |

## Next Recommended Step

Create the database schema and wire the importer write path, keeping race sessions (`R`) as the default import scope.
