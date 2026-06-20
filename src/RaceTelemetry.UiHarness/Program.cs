using RaceTelemetry.Desktop.Services;
using RaceTelemetry.UiHarness;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// Same backend the MAUI app talks to (Query API 5120, Agent API 5124 — run via AppHost).
var apiBase = Environment.GetEnvironmentVariable("RACE_TELEMETRY_QUERY_API_BASEURL") ?? "http://localhost:5120";
builder.Services.AddHttpClient<IQueryApiClient, QueryApiClient>(c =>
{
    c.BaseAddress = new Uri(apiBase);
    c.Timeout = TimeSpan.FromSeconds(30);
});

var agentBase = Environment.GetEnvironmentVariable("RACE_TELEMETRY_AGENT_API_BASEURL") ?? "http://localhost:5124";
builder.Services.AddHttpClient("agent-api", c =>
{
    c.BaseAddress = new Uri(agentBase);
    c.Timeout = TimeSpan.FromMinutes(5);
});

builder.Services.AddScoped<ISessionPrefetchService, SessionPrefetchService>();
builder.Services.AddScoped<ILauncherSessionCache, LauncherSessionCache>();
builder.Services.AddScoped<SessionState>();
builder.Services.AddScoped<IChatThreadIdentity, InMemoryChatThreadIdentity>();
builder.Services.AddSingleton<ITelemetryAgentClient, TelemetryAgentClient>();

var app = builder.Build();

app.UseStaticFiles(); // serves RCL _content/* assets with real content (MapStaticAssets served them empty)
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
