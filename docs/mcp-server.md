# MCP Server

The MCP server exposes read-only Formula 1 telemetry tools over Streamable HTTP
using the official .NET `ModelContextProtocol` SDK.

Project:

```text
src/RaceTelemetry.McpServer
```

It uses the same contracts and `IF1TelemetryQueryStore` data path as the Query
API. When `RACE_TELEMETRY_DATABASE_URL` or `ConnectionStrings:RaceTelemetry` is
configured, tools query TimescaleDB/PostgreSQL through `PostgresTelemetryQueryStore`.
Without a database URL, the server falls back to `InMemoryTelemetryQueryStore`
for lightweight smoke checks.

## Tools

| Tool | Purpose |
|---|---|
| `list_sessions` | List imported sessions. Defaults to race sessions (`R`). |
| `get_session_drivers` | List drivers in one session. |
| `get_driver_laps` | List non-deleted laps for a driver. |
| `get_replay_metadata` | Get replay bounds, drivers, channels, track markers, overlays, and weather summary. |
| `get_lap_telemetry` | Get bounded lap telemetry samples. |
| `get_lap_story` | Get analyst-ready lap timing, sector, tyre, aggregate telemetry, and insight facts. |
| `get_lap_braking_zones` | Detect contiguous braking windows and nearest corner labels where data supports it. |
| `compare_laps` | Compare two laps by lap-relative time buckets. |
| `compare_laps_story` | Compare two laps with total/sector deltas, coarse segment differences, and insight facts. |
| `get_race_story` | Get weather, tyre stints, pit markers, track-status periods, race-control highlights, and race insight facts. |
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

## Local Run

Build:

```bash
dotnet build src/RaceTelemetry.McpServer/RaceTelemetry.McpServer.csproj
```

Run as a Streamable HTTP MCP server:

```bash
RACE_TELEMETRY_DATABASE_URL=postgresql://race_telemetry:race_telemetry@localhost:5432/race_telemetry \
dotnet run --project src/RaceTelemetry.McpServer/RaceTelemetry.McpServer.csproj --urls http://127.0.0.1:5122
```

HTTP mode exposes:

| Endpoint | Purpose |
|---|---|
| `/` | Lightweight server metadata. |
| `/mcp` | Streamable HTTP MCP endpoint. |
| `/health` | Manual/Aspire health probe. |
| `/alive` | Manual/Aspire liveness probe. |

## Aspire

The Aspire AppHost includes the MCP server as `mcp-server` and exposes it on:

```text
http://127.0.0.1:5122/mcp
```

Start the full local set:

```bash
aspire run
```

Useful checks:

```bash
curl http://127.0.0.1:5122/
curl http://127.0.0.1:5122/health
MCP_TEST_HTTP_ENDPOINT=http://127.0.0.1:5122/mcp \
dotnet run --project tests/RaceTelemetry.McpServer.Tests/RaceTelemetry.McpServer.Tests.csproj
```

## Client Config

Example Streamable HTTP MCP client server entry:

```json
{
  "mcpServers": {
    "race-telemetry": {
      "url": "http://127.0.0.1:5122/mcp"
    }
  }
}
```

For Codex CLI, register the Aspire HTTP endpoint:

```bash
codex mcp add race-telemetry-aspire --url http://127.0.0.1:5122/mcp
codex mcp list
```

That entry requires Aspire to be running.

For Claude Code, register the same HTTP endpoint:

```bash
claude mcp add --transport http race-telemetry http://127.0.0.1:5122/mcp
claude mcp list
```

Claude Desktop may not support direct HTTP MCP servers in every release. If
your Desktop build rejects a `url`-based MCP config, use Claude Code for this
server or put an HTTP-to-stdio MCP bridge in front of it. This project itself
intentionally exposes MCP only over HTTP.

## Testing

The protocol smoke test connects to a running HTTP MCP endpoint using the MCP
client SDK, lists tools, and calls representative tools.

```bash
MCP_TEST_HTTP_ENDPOINT=http://127.0.0.1:5122/mcp \
dotnet run --project tests/RaceTelemetry.McpServer.Tests/RaceTelemetry.McpServer.Tests.csproj
```

The protocol test currently asserts all expected tools are listed and calls
`list_sessions`, `get_replay_metadata`, and `get_race_story`.

## Analysis Workflow

For natural-language race analysis, start with compact story tools:

1. `list_sessions`
2. `get_race_story`
3. `get_session_drivers`
4. `get_driver_laps`
5. `get_lap_story`
6. `get_lap_braking_zones`
7. `compare_laps_story`

For complex analytical questions, use story, aggregate, window, stint, pit,
weather, race-control, and circuit-context tools before raw telemetry:

1. `aggregate_telemetry` for grouped metrics such as DRS time or brake time.
2. `detect_telemetry_windows` for intervals such as DRS activations or braking
   windows.
3. `analyze_driver_stints` for tyre degradation and stint strategy.
4. `analyze_pit_stops` for pit-loss questions.
5. `get_weather_trend` and `get_race_control_timeline` for race context.
6. `get_circuit_context` to map telemetry windows to imported track markers.

Use `get_lap_telemetry`, `compare_laps`, `get_replay_chunk`,
`get_replay_context`, and `search_telemetry_events` only when the client needs
raw samples, exact bucket data, or a narrower time window.

MCP and Query API analysis should remain in parity. New MCP analytical tools
should be backed by a shared contract and Query API route unless a decision
record explicitly explains why the tool is MCP-only.

Current limitation: braking-zone corner labels depend on exact timestamp
alignment between car telemetry and position samples. If a lap has no aligned
position sample at the braking-zone start, the braking window still returns but
`nearestCorner` is null. A future nearest-position join would improve this.

## Tracing

The MCP server uses `RaceTelemetry.ServiceDefaults`, so it exports logs,
metrics, and traces to Aspire when `OTEL_EXPORTER_OTLP_ENDPOINT` or
`ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL` is present.

Expected trace shape for a tool that reads PostgreSQL:

```text
mcp.tool.<tool_name>
  query_store.<method_name>
    postgresql -> localhost
```

The tool spans come from the `RaceTelemetry.McpServer` activity source. The
query-store spans come from `RaceTelemetry.Data`. PostgreSQL command spans come
from `Npgsql.OpenTelemetry` via `.AddNpgsql()` in ServiceDefaults.

If tool spans appear but PostgreSQL child spans do not, verify that the server
is using the real PostgreSQL store. Without `RACE_TELEMETRY_DATABASE_URL` or
`ConnectionStrings:RaceTelemetry`, the MCP server falls back to
`InMemoryTelemetryQueryStore`, which produces no database spans.
