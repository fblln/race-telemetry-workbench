# Backlog

## Repository Setup

- [ ] Create the repository structure from the spec:
  - `src/F1Telemetry.AppHost`
  - `src/F1Telemetry.ServiceDefaults`
  - `src/F1Telemetry.Contracts`
  - `src/F1Telemetry.QueryApi`
  - `src/F1Telemetry.McpServer`
  - `src/F1Telemetry.Desktop`
  - `db/migrations`
  - `scripts`
  - `docs`
  - `tests`
- [ ] Create the .NET solution and project references.
- [ ] Add shared DTO and JSON serialization contracts.
- [ ] Add local development configuration placeholders.

## Phase 1 - Database and Import

- [ ] Start TimescaleDB through Aspire.
- [ ] Add `db/migrations/001_initial_schema.sql`.
- [ ] Add `db/migrations/002_timescale_hypertables.sql`.
- [ ] Create tables:
  - `sessions`
  - `session_drivers`
  - `laps`
  - `telemetry_samples`
  - `position_samples`
- [ ] Add required indexes for telemetry, position, and replay queries.
- [ ] Add `scripts/import_session.py`.
- [ ] Add `scripts/requirements.txt`.
- [ ] Implement FastF1 session resolution.
- [ ] Import all available drivers by default.
- [ ] Import lap metadata.
- [ ] Import telemetry channels:
  - `speed_kmh`
  - `throttle_pct`
  - `brake_pct`
  - `gear`
  - `rpm`
  - `drs`
  - `distance_m`
- [ ] Import position channels:
  - `x`
  - `y`
  - `z`
- [ ] Implement stable `session_id` and `lap_id` generation.
- [ ] Implement `fail`, `upsert`, and `replace` modes.
- [ ] Preserve missing telemetry values as `NULL`.
- [ ] Convert boolean brake values to `0` or `100`.
- [ ] Print the required import summary.
- [ ] Verify import with row-count SQL checks.

## Phase 2 - Query API

- [ ] Create ASP.NET Core Minimal API project.
- [ ] Wire Npgsql and EF Core where useful.
- [ ] Add raw SQL query layer for analytical and time-series paths.
- [ ] Add local OpenAPI.
- [ ] Implement standard error response shape.
- [ ] Implement validation for:
  - session IDs
  - driver codes
  - lap numbers
  - allowed channels
  - row limits
  - bounded time ranges
- [ ] Implement `GET /api/sessions`.
- [ ] Implement `GET /api/sessions/{sessionId}/drivers`.
- [ ] Implement `GET /api/sessions/{sessionId}/drivers/{driverCode}/laps`.
- [ ] Implement `GET /api/sessions/{sessionId}/drivers/{driverCode}/laps/{lapNumber}/telemetry`.
- [ ] Implement `GET /api/sessions/{sessionId}/compare/laps`.
- [ ] Implement `GET /api/sessions/{sessionId}/replay/metadata`.
- [ ] Implement `GET /api/sessions/{sessionId}/replay/chunk`.
- [ ] Implement `POST /api/sessions/{sessionId}/telemetry-events/search`.
- [ ] Emit structured logs, traces, and metrics visible in Aspire Dashboard.
- [ ] Add focused Query API tests.

## Phase 3 - Desktop Replay

- [ ] Create Avalonia UI 11 desktop project.
- [ ] Add CommunityToolkit.Mvvm.
- [ ] Add LiveCharts2.
- [ ] Add ScottPlot.
- [ ] Build Session Selector screen.
- [ ] Build Replay Workspace screen.
- [ ] Add replay controls:
  - play
  - pause
  - restart
  - timeline seek
  - speed selector
  - driver selector
  - channel selector
- [ ] Load replay metadata when opening a session.
- [ ] Load first replay chunk before playback.
- [ ] Keep at least one future chunk buffered.
- [ ] Cancel in-flight chunk requests on seek.
- [ ] Downsample chart data when needed.
- [ ] Preserve backend `null` values.
- [ ] Verify playback at `1x` and switching to `5x`.

## Phase 4 - Lap Comparison

- [ ] Build Lap Comparison screen.
- [ ] Add inputs for Driver A, Lap A, Driver B, Lap B, and channels.
- [ ] Call `/api/sessions/{sessionId}/compare/laps`.
- [ ] Display distance-based overlay charts.
- [ ] Display lap-time delta.
- [ ] Display sector deltas.
- [ ] Use delta convention `driverA - driverB`.

## Phase 5 - MCP Query Server

- [ ] Create .NET MCP server project.
- [ ] Add HTTP transport for Aspire execution.
- [ ] Consider optional stdio transport for coding-agent integration.
- [ ] Ensure tools call the Query API only.
- [ ] Ensure no direct TimescaleDB access.
- [ ] Ensure tools are read-only.
- [ ] Implement `list_sessions`.
- [ ] Implement `list_drivers`.
- [ ] Implement `get_driver_laps`.
- [ ] Implement `compare_laps`.
- [ ] Implement `get_replay_metadata`.
- [ ] Implement `find_telemetry_events`.
- [ ] Return compact, bounded, model-friendly JSON.
- [ ] Emit one trace span per tool call.
- [ ] Add focused MCP server tests.

## Phase 6 - Optional AI Assistant Panel

- [ ] Add AI Assistant Panel to the Avalonia app.
- [ ] Display MCP-derived answers beside session data.
- [ ] Support opening replay from relevant answers.
- [ ] Support opening lap comparison from relevant answers.

## Cross-Cutting Requirements

- [ ] Validate all channel names against the allow-list.
- [ ] Prevent arbitrary SQL exposure.
- [ ] Bound large responses with downsampling or pagination.
- [ ] Keep MCP tools read-only.
- [ ] Make validation errors explicit and actionable.
- [ ] Track performance targets for core operations.
- [ ] Add Aspire observability for .NET services.

