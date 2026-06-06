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
  - `circuit_metadata`
  - `circuit_markers`
  - `weather_samples`
  - `track_status_events`
  - `session_status_events`
  - `race_control_messages`
- [ ] Add required indexes for telemetry, position, and replay queries.
- [ ] Make TimescaleDB the primary storage target for multi-year, multi-session analytics.
- [ ] Use relational PostgreSQL tables for session, driver, lap, circuit, status, and race-control metadata.
- [ ] Use Timescale hypertables for telemetry, position, and weather samples.
- [ ] Add analytical views/materialized-view candidates:
  - `lap_summaries`
  - `driver_stint_summaries`
  - `session_weather_summary`
  - `track_status_periods`
  - `race_control_event_index`
  - `telemetry_event_candidates`
- [ ] Add `scripts/import_session.py`.
- [ ] Add `scripts/requirements.txt`.
- [x] Add `scripts/download_session.py` as the database-free FastF1 fetch and validation slice.
- [x] Document the data download workflow.
- [x] Document raw FastF1 session, lap, car telemetry, position, and cache shapes.
- [x] Add a raw-cache storage estimator for full-year planning.
- [x] Set race sessions (`R`) as the default download/import scope.
- [ ] Implement FastF1 session resolution.
- [ ] Keep non-race session downloads/imports opt-in via explicit `--session`.
- [ ] Import all available drivers by default.
- [ ] Import lap metadata.
- [ ] Import telemetry channels:
  - `session_time_ms`
  - `lap_time_ms`
  - `speed_kmh`
  - `throttle_pct`
  - `brake_pct`
  - `gear`
  - `rpm`
  - `drs`
  - `distance_m`
  - `relative_distance`
  - `driver_ahead`
  - `distance_to_driver_ahead_m`
  - `track_status`
  - `sample_source`
- [ ] Import position channels:
  - `x`
  - `y`
  - `z`
  - `track_status`
  - `sample_source`
- [ ] Import FastF1 circuit metadata:
  - `rotation_degrees`
  - corner markers
  - marshal light markers
  - marshal sector markers
- [ ] Import FastF1 weather samples:
  - `air_temp_c`
  - `track_temp_c`
  - `humidity_pct`
  - `pressure_mbar`
  - `rainfall`
  - `wind_direction_deg`
  - `wind_speed_mps`
- [ ] Import FastF1 track and race-control events:
  - track status events
  - session status events
  - race-control messages
  - safety car and virtual safety car periods
  - yellow/red/green flag periods
- [ ] Use FastF1 `lap.get_telemetry()` as the composed telemetry source for database telemetry rows.
- [ ] Use FastF1 `lap.get_pos_data()` as the raw position source for database position rows.
- [ ] Use FastF1 `session.get_circuit_info()` as the circuit annotation source when available.
- [ ] Use FastF1 `session.weather_data` as the weather source when available.
- [ ] Use FastF1 `session.track_status`, `session.session_status`, and `session.race_control_messages` as event timeline sources when available.
- [ ] Derive the desktop track outline from imported `position_samples`, not external track assets.
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
- [ ] Implement `GET /api/sessions/{sessionId}/replay/context`.
- [ ] Implement `POST /api/sessions/{sessionId}/telemetry-events/search`.
- [ ] Add replay context tests for weather, track-status, and race-control timeline windows.
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
- [ ] Load replay context windows for weather, flags, safety car, VSC, red flags, DRS messages, and race-control messages.
- [ ] Keep at least one future chunk buffered.
- [ ] Cancel in-flight chunk requests on seek.
- [ ] Downsample chart data when needed.
- [ ] Preserve backend `null` values.
- [ ] Show current weather at the replay timestamp.
- [ ] Shade timeline/charts for yellow, safety car, VSC, and red-flag periods.
- [ ] Show race-control messages as inspectable timeline markers.
- [ ] Mark rainfall periods when weather data reports rain.
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
- [ ] Implement `get_replay_context`.
- [ ] Implement `find_telemetry_events`.
- [ ] Support weather and race-control questions through Query API-backed tools.
- [ ] Prefer bounded summary/context endpoints over raw sample responses for MCP analytics.
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
