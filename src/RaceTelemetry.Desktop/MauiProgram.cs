using CommunityToolkit.Mvvm.ComponentModel;
#if DEBUG
using Microsoft.Maui.DevFlow.Agent;
#endif
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RaceTelemetry.Desktop.Services;
using RaceTelemetry.Desktop.ViewModels;
using RaceTelemetry.Desktop.Views;

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

        // Shared app state
        builder.Services.AddSingleton<AppState>();

        // Shell + command palette (singletons — persistent chrome)
        builder.Services.AddSingleton<ConsoleShellViewModel>();
        builder.Services.AddSingleton<CommandPaletteViewModel>();

        // AG-UI agent client (stateless HTTP client, thread ID managed separately)
        builder.Services.AddSingleton<ChatThreadIdentity>();
        builder.Services.AddSingleton<ITelemetryAgentClient, TelemetryAgentClient>();

        // Pages and views (transient — re-created on each navigation)
        builder.Services.AddTransient<ConsoleShellPage>();
        builder.Services.AddTransient<LauncherView>();
        builder.Services.AddTransient<PlaceholderView>();
        builder.Services.AddTransient<ReportsAiViewModel>();
        builder.Services.AddTransient<ReportsAiView>();

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

/// <summary>
/// Lightweight shared application state: the open session and the selected drivers.
/// Views observe this so the console keeps one source of truth across the view rail (§8.11).
/// </summary>
public sealed partial class AppState : ObservableObject
{
    [ObservableProperty]
    private string? _sessionId;

    [ObservableProperty]
    private string? _eventName;

    [ObservableProperty]
    private int _year;

    public List<string> SelectedDrivers { get; } = new();
}
