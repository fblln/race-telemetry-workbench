using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Maui.DevFlow.Agent;
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

        // Prefetch cache: eagerly loads everything about a session so view
        // switches are instant (§8.9). Singleton so the cache outlives views.
        builder.Services.AddSingleton<ISessionPrefetchService, SessionPrefetchService>();

        // View models
        builder.Services.AddSingleton<AppState>();
        builder.Services.AddSingleton<CommandPaletteViewModel>();
        builder.Services.AddTransient<LauncherViewModel>();
        builder.Services.AddTransient<SessionConsoleViewModel>();
        builder.Services.AddTransient<OverviewViewModel>();
        builder.Services.AddTransient<FieldViewViewModel>();
        builder.Services.AddTransient<TrackIncidentsViewModel>();
        builder.Services.AddTransient<ReplayWorkspaceViewModel>();
        builder.Services.AddTransient<LapComparisonViewModel>();
        builder.Services.AddTransient<StrategyViewModel>();

        // Pages
        builder.Services.AddTransient<LauncherPage>();
        builder.Services.AddTransient<SessionConsolePage>();

        // Views hosted inside the console content area (§8.11)
        builder.Services.AddTransient<OverviewView>();
        builder.Services.AddTransient<FieldView>();
        builder.Services.AddTransient<TrackIncidentsView>();
        builder.Services.AddTransient<ReplayWorkspaceView>();
        builder.Services.AddTransient<LapComparisonView>();
        builder.Services.AddTransient<StrategyView>();

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
