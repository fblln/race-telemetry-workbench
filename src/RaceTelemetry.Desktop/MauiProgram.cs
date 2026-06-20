#if DEBUG
using Microsoft.Maui.DevFlow.Agent;
#endif
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RaceTelemetry.Desktop.Services;

namespace RaceTelemetry.Desktop;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

#if MACCATALYST
        // Strip the native rounded UITextField chrome — every Entry in the app sits
        // inside its own Carbon Signal bordered container (§2/§2a).
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("CarbonNoBorder", (handler, _) =>
        {
            handler.PlatformView.BorderStyle = UIKit.UITextBorderStyle.None;
            handler.PlatformView.BackgroundColor = UIKit.UIColor.Clear;
        });

        // Make the BlazorWebView's WKWebView inspectable so DevFlow/Safari can attach (debug only).
        Microsoft.AspNetCore.Components.WebView.Maui.BlazorWebViewHandler.BlazorWebViewMapper.AppendToMapping(
            "Inspectable", (handler, _) =>
            {
                if (OperatingSystem.IsMacCatalystVersionAtLeast(16, 4))
                    handler.PlatformView.Inspectable = true;
            });
#endif

        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                // Carbon Signal type stack. TTFs live in Resources/Fonts (see README there).
                fonts.AddFont("Inter-Regular.ttf", "Inter");
                fonts.AddFont("Inter-Medium.ttf", "InterMedium");
                fonts.AddFont("Inter-SemiBold.ttf", "InterSemiBold");
                fonts.AddFont("JetBrainsMono-Regular.ttf", "JetBrainsMono");
                fonts.AddFont("JetBrainsMono-Medium.ttf", "JetBrainsMonoMedium");
            });

        // Blazor Hybrid: the whole UI is a BlazorWebView hosting Razor components.
        builder.Services.AddMauiBlazorWebView();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif

        // Query API base address. AppHost exposes the Query API on http://localhost:5120 (§11).
        var apiBase = Environment.GetEnvironmentVariable("RACE_TELEMETRY_QUERY_API_BASEURL")
                      ?? "http://localhost:5120";

        builder.Services.AddHttpClient<IQueryApiClient, QueryApiClient>(client =>
        {
            client.BaseAddress = new Uri(apiBase);
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // Agent API base address (AG-UI over SSE).
        var agentApiBase = Environment.GetEnvironmentVariable("RACE_TELEMETRY_AGENT_API_BASEURL")
                           ?? "http://localhost:5124";

        builder.Services.AddHttpClient("agent-api", client =>
        {
            client.BaseAddress = new Uri(agentApiBase);
            client.Timeout = TimeSpan.FromMinutes(5);
        });

        // Prefetch + launcher caches
        builder.Services.AddSingleton<ISessionPrefetchService, SessionPrefetchService>();
        builder.Services.AddSingleton<ILauncherSessionCache, LauncherSessionCache>();

        // Single source of truth for the open session + selected drivers + active view.
        builder.Services.AddSingleton<SessionState>();

        // AG-UI agent client (stateless HTTP client, thread ID managed separately)
        builder.Services.AddSingleton<ChatThreadIdentity>();
        builder.Services.AddSingleton<ITelemetryAgentClient, TelemetryAgentClient>();

        // Host page for the BlazorWebView.
        builder.Services.AddTransient<MainPage>();

#if DEBUG
        builder.AddMauiDevFlowAgent(options =>
        {
            options.Enabled = true;
            options.Port = 9223;
            options.EnableFileLogging = true;
            options.CaptureILogger = true;
        });
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
