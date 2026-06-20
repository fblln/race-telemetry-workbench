using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RaceTelemetry.Agent;
using RaceTelemetry.Agent.Options;
using RaceTelemetry.AgentApi.AgUi;
using RaceTelemetry.AgentApi.Sessions;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddRaceTelemetryAgent(builder.Configuration);

builder.Services.AddSingleton<AgentSessionRegistry>();
builder.Services.AddSingleton<GroundedFrameVerifier>();
builder.Services.AddHostedService<SessionCleanupService>();
builder.Services.AddScoped<AgentRunner>();

builder.Services.AddHttpClient("mcp", client =>
{
    client.Timeout = TimeSpan.FromMinutes(2);
});

builder.Services.AddHealthChecks()
    .AddCheck<McpReadinessCheck>("mcp-ready", tags: ["ready"])
    .AddCheck<OpenAiConfigCheck>("openai-config", tags: ["ready"]);

var app = builder.Build();

app.MapDefaultEndpoints();

var mcpRegistry = app.Services.GetRequiredService<McpToolRegistry>();
try
{
    await mcpRegistry.InitializeAsync();
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "MCP tool discovery failed at startup — readiness check will report degraded");
}

app.MapAgUiEndpoints();

app.MapGet("/", () => new
{
    name = "Race Telemetry Agent API",
    version = "1.0.0",
    protocol = "ag-ui",
    endpoint = "/ag-ui",
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("ready"),
});

await app.RunAsync();

internal sealed class McpReadinessCheck : IHealthCheck
{
    private readonly McpToolRegistry _registry;
    public McpReadinessCheck(McpToolRegistry registry) => _registry = registry;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(_registry.IsReady
            ? HealthCheckResult.Healthy("MCP connected")
            : HealthCheckResult.Degraded("MCP tools not yet discovered"));
}

internal sealed class OpenAiConfigCheck : IHealthCheck
{
    private readonly IConfiguration _config;
    public OpenAiConfigCheck(IConfiguration config) => _config = config;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var key = _config[$"{OpenAiOptions.SectionName}:ApiKey"];
        return Task.FromResult(!string.IsNullOrWhiteSpace(key)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("OpenAI API key not configured"));
    }
}
