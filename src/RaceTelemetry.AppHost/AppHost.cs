var builder = DistributedApplication.CreateBuilder(args);

var databaseUrl = Environment.GetEnvironmentVariable("RACE_TELEMETRY_DATABASE_URL")
    ?? "postgresql://race_telemetry:race_telemetry@localhost:5432/race_telemetry";

var openAiApiKey = builder.AddParameter("openai-api-key", secret: true);
var openAiModel = builder.AddParameter("openai-model");

builder.AddProject<Projects.RaceTelemetry_QueryApi>("query-api", launchProfileName: null)
    .WithEnvironment("RACE_TELEMETRY_DATABASE_URL", databaseUrl)
    .WithHttpEndpoint(port: 5120, env: "ASPNETCORE_HTTP_PORTS")
    .WithExternalHttpEndpoints();

var mcpServer = builder.AddProject<Projects.RaceTelemetry_McpServer>("mcp-server", launchProfileName: null)
    .WithEnvironment("RACE_TELEMETRY_DATABASE_URL", databaseUrl)
    .WithHttpEndpoint(port: 5122, env: "ASPNETCORE_HTTP_PORTS")
    .WithExternalHttpEndpoints();

builder.AddProject<Projects.RaceTelemetry_AgentApi>("agent-api", launchProfileName: null)
    .WithHttpEndpoint(port: 5124, env: "ASPNETCORE_HTTP_PORTS")
    .WithReference(mcpServer)
    .WaitFor(mcpServer)
    .WithEnvironment("OpenAI__ApiKey", openAiApiKey)
    .WithEnvironment("OpenAI__Model", openAiModel)
    .WithEnvironment("TelemetryAgent__McpEndpoint", "http://localhost:5122/mcp");

builder.Build().Run();
