using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Npgsql;
using RaceTelemetry.Data;
using RaceTelemetry.McpServer;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddMemoryCache(options => options.SizeLimit = 2_000);
builder.Services.AddRaceTelemetryQueryStore(builder.Configuration);
builder.Services.AddScoped<RaceTelemetryMcpTools>();
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "race-telemetry-mcp-server",
            Version = "0.1.0"
        };
        options.ServerInstructions = """
            Read-only Formula 1 race telemetry tools backed by Race Telemetry Workbench.
            Race sessions are the default; practice, qualifying, sprint qualifying, and sprint sessions are explicit opt-ins.
            Keep replay, telemetry, and event queries bounded by the exposed time-window and sample-limit arguments.
            """;
    })
    .WithHttpTransport(options =>
    {
        options.Stateless = true;
    })
    .WithTools<RaceTelemetryMcpTools>();

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));
app.MapGet("/alive", () => Results.Ok(new { status = "Alive" }));
app.MapGet("/", () => new
{
    name = "Race Telemetry MCP Server",
    version = "0.1.0",
    transport = "streamable-http",
    endpoint = "/mcp"
});
app.MapMcp("/mcp");

await app.RunAsync();

internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRaceTelemetryQueryStore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var databaseUrl = configuration.GetConnectionString("RaceTelemetry")
            ?? configuration["RACE_TELEMETRY_DATABASE_URL"]
            ?? Environment.GetEnvironmentVariable("RACE_TELEMETRY_DATABASE_URL");

        if (string.IsNullOrWhiteSpace(databaseUrl))
        {
            services.AddSingleton<IF1TelemetryQueryStore, InMemoryTelemetryQueryStore>();
            return services;
        }

        services.AddSingleton(_ =>
        {
            var connectionString = PostgresConnectionString.Normalize(databaseUrl);
            return new NpgsqlDataSourceBuilder(connectionString).Build();
        });
        services.AddSingleton<IF1TelemetryQueryStore, PostgresTelemetryQueryStore>();
        return services;
    }
}
