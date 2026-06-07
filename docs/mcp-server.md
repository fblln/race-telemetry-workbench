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

Use `get_lap_telemetry`, `compare_laps`, `get_replay_chunk`,
`get_replay_context`, and `search_telemetry_events` only when the client needs
raw samples or a narrower time window.

Current limitation: braking-zone corner labels depend on exact timestamp
alignment between car telemetry and position samples. If a lap has no aligned
position sample at the braking-zone start, the braking window still returns but
`nearestCorner` is null. A future nearest-position join would improve this.
