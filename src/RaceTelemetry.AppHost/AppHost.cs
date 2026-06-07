var builder = DistributedApplication.CreateBuilder(args);

var databaseUrl = Environment.GetEnvironmentVariable("RACE_TELEMETRY_DATABASE_URL")
    ?? "postgresql://race_telemetry:race_telemetry@localhost:5432/race_telemetry";

builder.AddProject<Projects.RaceTelemetry_QueryApi>("query-api")
    .WithEnvironment("RACE_TELEMETRY_DATABASE_URL", databaseUrl)
    .WithHttpEndpoint(port: 5120, env: "ASPNETCORE_HTTP_PORTS")
    .WithExternalHttpEndpoints();

builder.AddProject<Projects.RaceTelemetry_McpServer>("mcp-server")
    .WithEnvironment("RACE_TELEMETRY_DATABASE_URL", databaseUrl)
    .WithHttpEndpoint(port: 5122, env: "ASPNETCORE_HTTP_PORTS")
    .WithExternalHttpEndpoints();

builder.Build().Run();
