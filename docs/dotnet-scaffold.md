# .NET Application Scaffold

The .NET solution is the long-lived application surface for the workbench. It is
scaffolded with .NET 10 and Aspire so the Query API, future desktop app, and MCP
server can share contracts and query services without each layer learning about
FastF1 or raw SQL independently.

## Projects

| Project | Role |
|---|---|
| `src/RaceTelemetry.AppHost` | Aspire AppHost. Orchestrates local services during development. |
| `src/RaceTelemetry.ServiceDefaults` | Aspire service defaults: health checks, service discovery, resilience, and OpenTelemetry. |
| `src/RaceTelemetry.QueryApi` | Minimal REST API for sessions, drivers, laps, and replay metadata. |
| `src/RaceTelemetry.Contracts` | DTO contracts shared by API, desktop, tests, and MCP. |
| `src/RaceTelemetry.Data` | Query-store abstractions and current in-memory scaffold implementation. |
| `src/RaceTelemetry.McpServer` | Placeholder MCP host. It will expose tools backed by the same query contracts. |
| `src/RaceTelemetry.Desktop` | Reserved for the Avalonia desktop replay app. |
| `tests/RaceTelemetry.IntegrationTests` | Console-based HTTP integration checks for the Query API. |

## Query API First Slice

Current scaffold endpoints:

```text
GET /api/
GET /api/sessions
GET /api/sessions/{sessionId}/drivers
GET /api/sessions/{sessionId}/drivers/{driverCode}/laps
GET /api/sessions/{sessionId}/replay/metadata
```

The API currently uses `InMemoryTelemetryQueryStore` so the endpoint shape can be
tested before the Timescale-backed query store is added. The next implementation
step is to add an Npgsql-backed `IF1TelemetryQueryStore` implementation that
reads from the existing schema and analytical views.

## Aspire Shape

`RaceTelemetry.AppHost` currently models the Query API:

```csharp
builder.AddProject<Projects.RaceTelemetry_QueryApi>("query-api")
    .WithExternalHttpEndpoints();
```

The TimescaleDB container should be moved from standalone Docker Compose into
the AppHost once the .NET Query API owns its database connection string.

## Testing

The integration test runner starts the Query API on a dynamic local HTTP port,
calls the first endpoints, and verifies both success and `404` behavior:

```bash
dotnet run --project tests/RaceTelemetry.IntegrationTests/RaceTelemetry.IntegrationTests.csproj
```

See [Development Guide](development.md) for the normal restore, build, test,
Aspire, database, download, and import command loops.

## Next Steps

1. Add an Npgsql/Timescale implementation of `IF1TelemetryQueryStore`.
2. Add real database integration tests against Docker/Aspire TimescaleDB.
3. Add replay chunk endpoints for telemetry, position, weather, and race-control context.
4. Create the Avalonia app from official templates and consume the Query API over HTTP.
5. Replace the MCP placeholder with typed tools that call the Query API/query services.
