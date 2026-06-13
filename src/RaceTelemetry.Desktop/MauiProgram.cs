using CommunityToolkit.Mvvm.ComponentModel;
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

        // View models
        builder.Services.AddSingleton<AppState>();
        builder.Services.AddTransient<LauncherViewModel>();
        builder.Services.AddTransient<SessionConsoleViewModel>();
        builder.Services.AddTransient<FieldViewViewModel>();
        builder.Services.AddTransient<TrackIncidentsViewModel>();
        builder.Services.AddTransient<ReplayWorkspaceViewModel>();
        builder.Services.AddTransient<LapComparisonViewModel>();

        // Pages
        builder.Services.AddTransient<LauncherPage>();
        builder.Services.AddTransient<SessionConsolePage>();

#if DEBUG
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
