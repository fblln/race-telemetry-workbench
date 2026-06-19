# API And MCP

This page is the single reference for the local Query API, MCP server,
validation/error behavior, manual checks, and observability. The rendered
OpenAPI reference remains at `api/openapi.md`.

## Runtime Shape

```text
Bruno / Desktop / MCP
        |
        v
RaceTelemetry.QueryApi
        |
        v
RaceTelemetry.Data
        |
        v
TimescaleDB / PostgreSQL
```

The Query API is the local REST boundary over imported telemetry. It is the data
path used by the future .NET MAUI desktop app and by the MCP server; clients
should not read FastF1 files or TimescaleDB directly.

Projects:

| Project | Role |
|---|---|
| `src/RaceTelemetry.QueryApi` | ASP.NET Core Minimal API. |
| `src/RaceTelemetry.McpServer` | Streamable HTTP MCP server. |
| `src/RaceTelemetry.Contracts` | Shared DTOs for REST, MCP, and future desktop app. |
| `src/RaceTelemetry.Data` | Query-store abstraction and PostgreSQL implementation. |
| `src/RaceTelemetry.ServiceDefaults` | Aspire defaults, health checks, OpenTelemetry, service discovery, HTTP resilience. |
| `src/RaceTelemetry.AppHost` | Aspire AppHost. |

Stable Aspire endpoints:

| Resource | URL |
|---|---|
| Query API | `http://127.0.0.1:5120` |
| MCP server | `http://127.0.0.1:5122/mcp` |

Aspire owns the stable external ports. Project processes listen on
Aspire-injected internal ports.

## Startup And Store Selection

`Program.cs` delegates Query API construction to
`RaceTelemetryApi.CreateApp(args)`.

Startup order:

1. Create a `WebApplicationBuilder`.
2. Add shared Aspire service defaults.
3. Register Problem Details, OpenAPI, and endpoint discovery.
4. Register `IF1TelemetryQueryStore`.
5. Build the app.
6. Map health/aliveness endpoints.
7. Map OpenAPI in development.
8. Map telemetry REST endpoints under `/api`.

`IF1TelemetryQueryStore` is the API/MCP data abstraction.

| Type | Used when | Purpose |
|---|---|---|
| `PostgresTelemetryQueryStore` | `RACE_TELEMETRY_DATABASE_URL` or `ConnectionStrings:RaceTelemetry` is configured. | Real TimescaleDB/PostgreSQL queries. |
| `InMemoryTelemetryQueryStore` | No database URL is configured. | Lightweight smoke-test fallback. |

Default database URL:

```text
postgresql://race_telemetry:race_telemetry@localhost:5432/race_telemetry
```

Set `RACE_TELEMETRY_DATABASE_URL` to override it.

## REST Surface

All product endpoints are under `/api`.

| Endpoint | Purpose |
|---|---|
| `GET /api/` | API metadata and capability list. |
| `GET /api/sessions` | List imported sessions, optionally filtered by `year`, `event`, and `sessionType`. |
| `GET /api/sessions/{sessionId}/drivers` | List drivers in a session. |
| `GET /api/sessions/{sessionId}/drivers/{driverCode}/laps` | List non-deleted laps for one driver. |
| `GET /api/sessions/{sessionId}/drivers/{driverCode}/laps/{lapNumber}/telemetry` | Raw bounded telemetry samples for one lap. |
| `GET /api/sessions/{sessionId}/drivers/{driverCode}/laps/{lapNumber}/quality` | Objective distance-alignment quality metrics for one lap. |
| `GET /api/sessions/{sessionId}/drivers/{driverCode}/laps/{lapNumber}/story` | Compact lap timing, tyre, aggregate telemetry, and insight facts. |
| `GET /api/sessions/{sessionId}/drivers/{driverCode}/laps/{lapNumber}/braking-zones` | Contiguous braking windows with corner labels when position/circuit data aligns. |
| `GET /api/sessions/{sessionId}/compare/laps` | Time-domain, lap-time-bucketed comparison between two driver/lap pairs. |
| `GET /api/sessions/{sessionId}/compare/laps/by-distance` | Distance-domain comparison between two driver/lap pairs at common lap-distance points. |
| `GET /api/sessions/{sessionId}/compare/laps/story` | Total delta, sector deltas, coarse segment comparison, and insight facts. |
| `GET /api/sessions/{sessionId}/story` | Race-level weather, stints, pits, track status, race-control context, and insight facts. |
| `POST /api/sessions/{sessionId}/telemetry/aggregate` | Grouped telemetry metrics without raw samples. |
| `POST /api/sessions/{sessionId}/telemetry/windows` | Contiguous telemetry event intervals. |
| `POST /api/sessions/{sessionId}/stints/analyze` | Tyre/stint degradation, lap-time slope, best/worst lap, and strategy summaries. |
| `POST /api/sessions/{sessionId}/pit-stops/analyze` | Pit-in/out markers with nearby baselines and estimated pit-lap loss. |
| `POST /api/sessions/{sessionId}/weather/trend` | Weather deltas and rainfall summary. |
| `POST /api/sessions/{sessionId}/race-control/timeline` | Filtered race-control timeline plus counts. |
| `GET /api/sessions/{sessionId}/circuit/context` | Imported circuit rotation, corners, marshal lights, and marshal sectors. |
| `GET /api/sessions/{sessionId}/replay/metadata` | Replay bounds, driver list, track markers, context availability, weather summary. |
| `GET /api/sessions/{sessionId}/replay/chunk` | Bounded replay samples for a session-relative time window. |
| `GET /api/sessions/{sessionId}/replay/context` | Weather, track status, and race-control context in a time window. |
| `POST /api/sessions/{sessionId}/telemetry/events/search` | Bounded search over telemetry event candidates. |
| `POST /api/sessions/{sessionId}/telemetry-events/search` | Compatibility alias for telemetry event search. |

Health endpoints:

| Endpoint | Purpose |
|---|---|
| `GET /health` | Readiness. |
| `GET /alive` | Liveness. |

## MCP Tools

The MCP server exposes read-only Formula 1 telemetry tools over Streamable HTTP
using the official .NET `ModelContextProtocol` SDK. Tools reuse
`RaceTelemetry.Contracts` and `IF1TelemetryQueryStore`.

| Tool | Purpose |
|---|---|
| `list_sessions` | List imported sessions. Defaults to race sessions (`R`). |
| `get_session_drivers` | List drivers in one session. |
| `get_driver_laps` | List non-deleted laps for a driver. |
| `get_replay_metadata` | Get replay bounds, drivers, channels, track markers, overlays, and weather summary. |
| `get_lap_telemetry` | Get raw bounded lap telemetry samples. |
| `get_lap_quality` | Get objective lap-level distance-alignment quality metrics. |
| `get_lap_story` | Get analyst-ready lap timing, sector, tyre, aggregate telemetry, and insight facts. |
| `get_lap_braking_zones` | Detect contiguous braking windows and nearest corner labels where data supports it. |
| `compare_laps` | Compare two laps in the time domain by lap-relative time buckets. |
| `compare_laps_by_distance` | Compare two laps in the distance domain at common lap-distance points. |
| `compare_laps_story` | Compare two laps with total/sector deltas, coarse segment differences, and insight facts. |
| `get_race_story` | Get weather, tyre stints, pits, track-status periods, race-control highlights, and race insight facts. |
| `aggregate_telemetry` | Grouped telemetry metrics such as DRS active time, brake time, average speed, max speed, and sample counts. |
| `detect_telemetry_windows` | Contiguous event windows such as DRS active, hard braking, throttle lifts, and high-speed periods. |
| `analyze_driver_stints` | Tyre/stint degradation, lap-time slope, best/worst lap, and compound strategy summaries. |
| `analyze_pit_stops` | Pit-in/out markers, nearby non-pit lap baselines, and estimated pit-lap loss. |
| `get_weather_trend` | Weather deltas and rainfall summary for a session or selected time window. |
| `get_race_control_timeline` | Filtered race-control timeline with category, flag, and status counts. |
| `get_circuit_context` | Imported circuit rotation, corner markers, marshal lights, and marshal sectors. |
| `get_replay_chunk` | Get bounded replay samples for a session-relative window. |
| `get_replay_context` | Get weather, track-status, and race-control context for a window. |
| `search_telemetry_events` | Search bounded telemetry event candidates. |

All tools are marked read-only, idempotent, and not open-world. They do not
write to the database or expose arbitrary SQL.

## Validation And Problem Details

Validation happens in the endpoint/tool layer before calling the store.
Validated inputs include session IDs, driver codes, session types, lap numbers,
channel allow-lists, sample/downsampling limits, replay/context time windows,
telemetry event types, metrics, grouping keys, and race-control filters.

Validation and business errors use `application/problem+json` with an RFC
9457-style document. Standard fields (`type`, `title`, `status`, `detail`,
`instance`) stay compatible with problem-details clients. The API also adds a
stable machine-readable `code` and optional `errors` object.

Example:

```json
{
  "type": "https://fblln.github.io/race-telemetry-workbench/problems#invalid-driver",
  "title": "Invalid request",
  "status": 400,
  "detail": "Driver codes must contain 2 to 4 letters.",
  "code": "InvalidDriver",
  "errors": {
    "driverCode": "XYZ"
  }
}
```

Clients should branch on `status` for HTTP behavior and `code` for
application-specific handling. The `detail` text is for display and diagnostics,
not program logic.

Known `400` validation codes include:

| Code | Meaning |
|---|---|
| `InvalidBrakeThreshold` | `brakeThresholdPct` is outside the supported percentage range. |
| `InvalidChannels` | One or more requested telemetry or replay channels are not supported. |
| `InvalidDistanceRange` | Distance range values are negative or ordered incorrectly. |
| `InvalidDriver` | A driver code is missing or has an invalid shape. |
| `InvalidEventType` | A telemetry event type is missing or unsupported. |
| `InvalidGroupBy` | A telemetry aggregate grouping key is unsupported. |
| `InvalidLapNumber` | Lap numbers must be positive integers. |
| `InvalidLapRange` | Lap range values must be positive and ordered. |
| `InvalidLimit` | A request limit is outside the supported range. |
| `InvalidMaxSamples` | `maxSamples` is outside the supported range. |
| `InvalidMetrics` | One or more metric names are unsupported. |
| `InvalidMinimumDuration` | `minimumDurationMs` is outside the supported range. |
| `InvalidMinimumLaps` | `minimumLaps` is outside the supported range. |
| `InvalidNearbyLapWindow` | `nearbyLapWindow` is outside the supported range. |
| `InvalidRaceControlLimit` | `raceControlLimit` is outside the supported range. |
| `InvalidRacingNumber` | One or more race-control racing numbers are outside the supported range. |
| `InvalidSampleEvery` | `sampleEvery` is outside the supported downsampling range. |
| `InvalidSearch` | Search text exceeds the endpoint limit. |
| `InvalidSegmentCount` | `segmentCount` is outside the supported range. |
| `InvalidSessionId` | The session id does not match the imported session-id shape. |
| `InvalidSessionType` | The requested FastF1 session type is unsupported by the endpoint. |
| `InvalidTimeBucket` | `timeBucketMs` is outside the supported range. |
| `InvalidTimeRange` | A required session-relative time window is missing or invalid. |
| `InvalidTimeStep` | `timeStepMs` is outside the supported lap-comparison range. |
| `InvalidYear` | The requested season year is outside the supported range. |

Known `404` lookup codes include `DriverNotFound`, `LapComparisonNotFound`,
`LapNotFound`, `ReplayChunkNotFound`, and `SessionNotFound`.

Unhandled exceptions should be treated as bugs.

## SQL Query Design

`PostgresTelemetryQueryStore` uses raw SQL through `NpgsqlDataSource` and
Task-based async database I/O.

Design rules:

- REST endpoints remain bounded; no endpoint exposes arbitrary SQL.
- Summary endpoints aggregate in SQL instead of application memory.
- Replay and context endpoints require explicit time windows and use the replay-oriented time-domain projection when available.
- Lap telemetry exposes raw source samples; replay exposes time-aligned derived samples.
- Lap telemetry supports `sampleEvery` and `maxSamples`.
- Time comparison aligns samples by lap-relative time buckets.
- Distance comparison reads from `lap_telemetry_by_distance` so it answers where performance was gained or lost.
- Hot-path preflight checks are collapsed into the main SQL query when possible.
- Independent metadata/context/comparison reads can run concurrently because
  each command gets its own pooled connection from the shared `NpgsqlDataSource`.
- Natural-language analysis should prefer story, aggregate, window, and stint
  endpoints before raw telemetry endpoints.
- Query API and MCP analytical capabilities should stay in parity.

The Query API registers a startup warmup hosted service for PostgreSQL. It opens
two connections from the shared `NpgsqlDataSource` and closes them, returning
physical connections to the pool. Warmup failures are logged as warnings and do
not stop the app.

Key database surfaces:

| API area | Database tables/views |
|---|---|
| Sessions/drivers/laps | `sessions`, `session_drivers`, `laps` |
| Lap telemetry | `telemetry_samples` |
| Lap story | `lap_summaries` |
| Lap braking zones | `telemetry_samples`, `position_samples`, `circuit_markers` |
| Lap comparison (time domain) | `laps`, `telemetry_samples` |
| Lap comparison (distance domain) | `laps`, `lap_telemetry_by_distance`, `lap_telemetry_quality` |
| Lap quality | `lap_telemetry_quality`, `lap_telemetry_by_distance`, `session_drivers` |
| Race story | `sessions`, `session_drivers`, `laps`, `driver_stint_summaries`, `session_weather_summary`, `track_status_periods`, `race_control_event_index` |
| Analytical primitives | `telemetry_samples`, `laps`, `track_status_periods`, `weather_samples`, `race_control_event_index`, `circuit_metadata`, `circuit_markers` |
| Replay | `aligned_telemetry_10hz`, `telemetry_samples`, `position_samples`, `weather_samples`, `track_status_events`, `race_control_messages`, `circuit_metadata`, `circuit_markers` |
| Event search | `telemetry_event_candidates` |

## Run And Test

Build MCP server:

```bash
dotnet build src/RaceTelemetry.McpServer/RaceTelemetry.McpServer.csproj
```

Run MCP directly:

```bash
RACE_TELEMETRY_DATABASE_URL=postgresql://race_telemetry:race_telemetry@localhost:5432/race_telemetry \
dotnet run --project src/RaceTelemetry.McpServer/RaceTelemetry.McpServer.csproj --urls http://127.0.0.1:5122
```

Start the full app with Aspire:

```bash
aspire start
```

Useful checks:

```bash
curl http://127.0.0.1:5120/api/
curl http://127.0.0.1:5120/api/sessions
curl http://127.0.0.1:5122/
curl http://127.0.0.1:5122/health
MCP_TEST_HTTP_ENDPOINT=http://127.0.0.1:5122/mcp \
dotnet run --project tests/RaceTelemetry.McpServer.Tests/RaceTelemetry.McpServer.Tests.csproj
```

The MCP protocol smoke test lists tools and calls representative tools.

## Client Configuration

Streamable HTTP MCP client entry:

```json
{
  "mcpServers": {
    "race-telemetry": {
      "url": "http://127.0.0.1:5122/mcp"
    }
  }
}
```

Codex CLI:

```bash
codex mcp add race-telemetry-aspire --url http://127.0.0.1:5122/mcp
codex mcp list
```

Claude Code:

```bash
claude mcp add --transport http race-telemetry http://127.0.0.1:5122/mcp
claude mcp list
```

Claude Desktop may not support direct HTTP MCP servers in every release. If a
Desktop build rejects a `url`-based MCP config, use Claude Code or an
HTTP-to-stdio bridge.

## Bruno Collection

The Bruno collection is:

```text
bruno/race-telemetry-query-api
```

Run from the collection root:

```bash
bru run -r --env Local
```

The `Local` environment points to `http://127.0.0.1:5120`.

For imported 2025 Monza data, the replay window starts around `3470000ms`.
When switching sessions, run `Replay Metadata` first and set `fromMs` near
`replayStartMs`.

## Analysis Workflow

For natural-language race analysis, start with compact story tools:

1. `list_sessions`
2. `get_race_story`
3. `get_session_drivers`
4. `get_driver_laps`
5. `get_lap_story`
6. `get_lap_braking_zones`
7. `compare_laps_story`

For complex analytical questions, use aggregate, window, stint, pit, weather,
race-control, and circuit-context tools before raw telemetry.

Use `get_lap_telemetry`, `compare_laps`, `get_replay_chunk`,
`get_replay_context`, and `search_telemetry_events` only when the client needs
raw samples, exact bucket data, or a narrower time window.

Current limitation: braking-zone corner labels depend on exact timestamp
alignment between car telemetry and position samples. If a lap has no aligned
position sample at the braking-zone start, the braking window still returns but
`nearestCorner` is null.

## Observability

`RaceTelemetry.ServiceDefaults` configures OpenTelemetry logging, ASP.NET Core
request metrics/traces, HTTP client metrics/traces, runtime metrics, and OTLP
export to Aspire Dashboard when an OTLP endpoint is configured.

PostgreSQL command spans are enabled through `Npgsql.OpenTelemetry` and
`.AddNpgsql()` in `RaceTelemetry.ServiceDefaults`.

Expected MCP trace shape for a PostgreSQL tool:

```text
mcp.tool.<tool_name>
  query_store.<method_name>
    postgresql -> localhost
```

Useful checks:

```bash
aspire describe
aspire logs query-api --non-interactive
curl http://127.0.0.1:5120/api/
```

In Aspire Dashboard, use Structured Logs for request logs/correlation IDs,
Traces for HTTP/tool/database spans, and Metrics for runtime and ASP.NET Core
metrics.

## Performance Notes

The lap telemetry endpoint is latency-sensitive because desktop replay and lap
comparison views can call it during exploration.

Current optimizations:

- PostgreSQL connections are warmed at Query API startup.
- Lap telemetry validates lap existence and fetches samples in one SQL
  roundtrip.
- Independent response parts can be fetched concurrently when the response
  genuinely needs multiple independent result sets.

When investigating latency in Aspire traces:

- a `CONNECT ...` span inside the first request usually means the connection
  pool was cold;
- multiple sibling PostgreSQL spans usually mean multiple SQL commands;
- prefer one bounded SQL command when the data shape allows it;
- parallelize database calls only for independent queries.

## Common Failure Modes

| Symptom | Cause | Fix |
|---|---|---|
| `query-api` is `Finished` in `aspire describe` | App crashed at startup. | Run `aspire logs query-api --non-interactive`. |
| `Failed to bind ... 5120: address already in use` | Kestrel and Aspire/DCP both tried to own the stable external port, or an old AppHost is still running. | Use `WithHttpEndpoint(port: 5120, env: "ASPNETCORE_HTTP_PORTS")`; do not hard-code `ASPNETCORE_URLS=...5120`; stop old AppHosts. |
| Dashboard has HTTP/tool traces but no PostgreSQL spans | Npgsql instrumentation is not registered or the in-memory store is active. | Add `.AddNpgsql()` and verify the real PostgreSQL store is configured. |
| Bruno returns empty replay chunks | `fromMs` is outside the session replay range. | Run replay metadata and use a value near `replayStartMs`. |
