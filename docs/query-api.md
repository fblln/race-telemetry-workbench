# Query API

The Query API is the local REST boundary over the imported Formula 1 telemetry
database. It is the data path used by the future Avalonia desktop app and MCP
server; neither client should read FastF1 files or TimescaleDB directly.

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

The application is an ASP.NET Core Minimal API in:

```text
src/RaceTelemetry.QueryApi
```

It references:

| Project | Role |
|---|---|
| `RaceTelemetry.Contracts` | DTOs returned by REST endpoints. |
| `RaceTelemetry.Data` | Query-store abstraction and implementations. |
| `RaceTelemetry.ServiceDefaults` | Aspire defaults, health checks, OpenTelemetry, service discovery, HTTP resilience. |

The Aspire AppHost is:

```text
src/RaceTelemetry.AppHost/AppHost.cs
```

The intended local Query API URL is:

```text
http://127.0.0.1:5120
```

Aspire owns this stable external port. The Query API project process listens on
an Aspire-injected internal port via `ASPNETCORE_HTTP_PORTS`.

## Startup Flow

`Program.cs` delegates all application construction to
`RaceTelemetryApi.CreateApp(args)`.

Startup order:

1. Create a `WebApplicationBuilder`.
2. Add shared Aspire service defaults.
3. Register Problem Details, OpenAPI, and endpoint discovery.
4. Register an `IF1TelemetryQueryStore`.
5. Build the app.
6. Map health/aliveness endpoints.
7. Map OpenAPI in development.
8. Map telemetry REST endpoints under `/api`.

## Data Store Selection

`IF1TelemetryQueryStore` is the API-facing data abstraction.

Implementations:

| Type | Used when | Purpose |
|---|---|---|
| `PostgresTelemetryQueryStore` | `RACE_TELEMETRY_DATABASE_URL` or `ConnectionStrings:RaceTelemetry` is configured. | Real TimescaleDB/PostgreSQL queries. |
| `InMemoryTelemetryQueryStore` | No database URL is configured. | Lightweight fallback for basic smoke tests. |

Aspire configures the real database URL by default:

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
| `GET /api/sessions/{sessionId}/drivers/{driverCode}/laps/{lapNumber}/telemetry` | Bounded telemetry samples for one lap. |
| `GET /api/sessions/{sessionId}/drivers/{driverCode}/laps/{lapNumber}/story` | Compact lap timing, tyre, aggregate telemetry, and insight facts. |
| `GET /api/sessions/{sessionId}/drivers/{driverCode}/laps/{lapNumber}/braking-zones` | Contiguous braking windows with corner labels when position/circuit data aligns. |
| `GET /api/sessions/{sessionId}/compare/laps` | Lap-time-bucketed comparison between two driver/lap pairs. |
| `GET /api/sessions/{sessionId}/compare/laps/story` | Total delta, sector deltas, coarse segment comparison, and insight facts. |
| `GET /api/sessions/{sessionId}/story` | Race-level weather, tyre stints, pit markers, track status, race-control context, and insight facts. |
| `GET /api/sessions/{sessionId}/replay/metadata` | Replay bounds, driver list, track markers, context availability, weather summary. |
| `GET /api/sessions/{sessionId}/replay/chunk` | Bounded replay samples for a session-relative time window. |
| `GET /api/sessions/{sessionId}/replay/context` | Weather, track status, and race-control context in a time window. |
| `POST /api/sessions/{sessionId}/telemetry-events/search` | Bounded search over telemetry event candidates. |

Health endpoints come from ServiceDefaults:

| Endpoint | Purpose |
|---|---|
| `GET /health` | Readiness. |
| `GET /alive` | Liveness. |

## Validation And Error Shape

Validation happens in the endpoint layer before calling the store.

Validated inputs include:

- session IDs
- driver codes
- session types
- lap numbers
- channel allow-lists
- sample/downsampling limits
- replay/context time windows
- telemetry event types

Validation and business errors use a stable envelope:

```json
{
  "error": {
    "code": "InvalidDriver",
    "message": "Driver code XYZ does not exist in session 2025-italian-grand-prix-r.",
    "details": {
      "sessionId": "2025-italian-grand-prix-r",
      "driverCode": "XYZ"
    }
  }
}
```

Unhandled exceptions still flow through ASP.NET Core diagnostics. Those should be
treated as bugs.

## SQL Query Design

`PostgresTelemetryQueryStore` uses raw SQL through `NpgsqlDataSource`.
Npgsql is used through its async ADO.NET API; it is not a reactive-streams
driver like R2DBC. The application gets non-blocking request-thread behavior
from `Task`-based async database I/O, async readers, and pooled connections.

Important design choices:

- REST endpoints remain bounded. No endpoint exposes arbitrary SQL.
- Summary endpoints aggregate in SQL instead of in application memory.
- Replay and context endpoints require explicit time windows.
- Lap telemetry supports `sampleEvery` and `maxSamples`.
- Lap comparison aligns samples by lap-relative time buckets.
- Hot-path preflight checks are collapsed into the main SQL query when the API
  still needs to distinguish "missing parent" from "empty result".
- Independent metadata/context/comparison reads run concurrently because each
  command gets its own pooled connection from the shared `NpgsqlDataSource`.
- Replay chunks currently return joined telemetry/position samples when source
  timestamps match. The frontend should be allowed to evolve toward separate
  telemetry and position streams for interpolation.

The Query API also registers a startup warmup hosted service for PostgreSQL.
It opens two connections from the shared `NpgsqlDataSource` and immediately
closes them, returning those physical connections to the pool. This pays most
connection setup cost during application startup instead of on the first API
request. Warmup failures are logged as warnings and do not stop the app.

Key database surfaces:

| API path | Database tables/views |
|---|---|
| Sessions | `sessions`, `session_drivers`, `laps` |
| Drivers | `session_drivers`, `laps` |
| Laps | `laps` |
| Lap telemetry | `telemetry_samples` |
| Lap story | `lap_summaries` |
| Lap braking zones | `telemetry_samples`, `position_samples`, `circuit_markers` |
| Lap comparison | `laps`, `telemetry_samples` |
| Lap comparison story | `laps`, `telemetry_samples` |
| Race story | `sessions`, `session_drivers`, `laps`, `driver_stint_summaries`, `session_weather_summary`, `track_status_periods`, `race_control_event_index` |
| Replay metadata | `telemetry_samples`, `position_samples`, `circuit_metadata`, `circuit_markers`, `session_weather_summary`, context tables |
| Replay chunk | `telemetry_samples`, `position_samples` |
| Replay context | `weather_samples`, `track_status_events`, `race_control_messages` |
| Event search | `telemetry_event_candidates` |

## Bruno Collection

The Bruno collection is:

```text
bruno/race-telemetry-query-api
```

Run from the collection root:

```bash
bru run -r --env Local
```

The `Local` environment points to:

```text
http://127.0.0.1:5120
```

For imported 2025 Monza data, the replay window starts around `3470000ms`.
When switching sessions, run `Replay Metadata` first and set `fromMs` near
`replayStartMs`.

## Observability

`RaceTelemetry.ServiceDefaults` configures:

- OpenTelemetry logging with formatted messages and scopes
- ASP.NET Core request metrics/traces
- HTTP client metrics/traces
- runtime metrics
- OTLP export to Aspire Dashboard when an OTLP endpoint is configured

The Query API should show request spans in Aspire Dashboard. PostgreSQL command
spans require Npgsql-specific OpenTelemetry instrumentation. The Npgsql
documentation states that tracing is activated by referencing
`Npgsql.OpenTelemetry` and adding `.AddNpgsql()` to the tracer provider. Npgsql
also exposes `NpgsqlDataSourceBuilder.ConfigureTracing(...)` for span naming,
filtering, and enrichment.

Useful checks:

```bash
aspire describe
aspire logs query-api --non-interactive
curl http://127.0.0.1:5120/api/
```

In Aspire Dashboard:

- use Structured Logs to inspect request logs and correlation IDs;
- use Traces to inspect HTTP request spans and child database spans;
- use Metrics to inspect runtime and ASP.NET Core metrics.

## Performance Notes

The lap telemetry endpoint is latency-sensitive because the desktop replay and
lap comparison views can call it during manual exploration.

Current optimizations:

- PostgreSQL connections are warmed at Query API startup when the real
  PostgreSQL store is active. This moves first connection-open latency out of
  the first user request.
- `GET /api/sessions/{sessionId}/drivers/{driverCode}/laps/{lapNumber}/telemetry`
  validates lap existence and fetches telemetry samples in one SQL roundtrip.
  Earlier versions performed a separate existence query before the sample query,
  which showed up as two PostgreSQL spans under one HTTP request.

When investigating latency in Aspire traces:

- a `CONNECT ...` span inside the first request usually means the connection
  pool was cold;
- multiple sibling PostgreSQL spans usually mean the endpoint is issuing
  multiple SQL commands sequentially;
- prefer one bounded SQL command when the data shape allows it;
- use parallel database calls only when the queries are independent and the
  response genuinely needs both result sets.

## Common Failure Modes

| Symptom | Cause | Fix |
|---|---|---|
| `query-api` is `Finished` in `aspire describe` | App crashed at startup. | Run `aspire logs query-api --non-interactive`. |
| `Failed to bind ... 5120: address already in use` | Kestrel and Aspire/DCP both tried to own the stable external port, or an old AppHost is still running. | Use `WithHttpEndpoint(port: 5120, env: "ASPNETCORE_HTTP_PORTS")`; do not hard-code `ASPNETCORE_URLS=...5120` under Aspire; stop old AppHosts. |
| Dashboard has HTTP traces but no PostgreSQL spans | Npgsql instrumentation is not registered. | Add `Npgsql.OpenTelemetry` and `.AddNpgsql()` to tracing. |
| Bruno returns empty replay chunks | `fromMs` is outside the session replay range. | Run replay metadata and use a value near `replayStartMs`. |
