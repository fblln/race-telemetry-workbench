using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RaceTelemetry.Desktop.Services;

namespace RaceTelemetry.Desktop.ViewModels;

/// <summary>
/// The session console shell (§8.11): owns the breadcrumb, the left view rail,
/// the HUD metrics, and the active view. View switching is keyboard-driven (§8.12).
/// </summary>
public sealed partial class SessionConsoleViewModel : ObservableObject
{
    private readonly IQueryApiClient _api;
    private readonly AppState _state;

    public SessionConsoleViewModel(IQueryApiClient api, AppState state)
    {
        _api = api;
        _state = state;

        Views = new ObservableCollection<ConsoleView>
        {
            new("Overview",      "1"),
            new("Replay",        "2"),
            new("Strategy",      "3"),
            new("Lap analysis",  "4"),
            new("Field",         "5"),
            new("Incidents",     "6"),
            new("Head to head",  "7"),
            new("Telemetry",     "8"),
        };
        ActiveView = Views[4]; // default to the Field view — the engineer's situational screen
    }

    public ObservableCollection<ConsoleView> Views { get; }

    [ObservableProperty]
    private ConsoleView _activeView;

    [ObservableProperty]
    private string _breadcrumb = "—";

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    public ObservableCollection<HudMetric> Hud { get; } = new();

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_state.SessionId is null) return;

        Breadcrumb = $"{_state.Year} / {_state.EventName} / RACE / {_state.SessionId}";

        Hud.Clear();
        try
        {
            var meta = await _api.GetReplayMetadataAsync(_state.SessionId);
            var drivers = await _api.GetDriversAsync(_state.SessionId);
            Hud.Add(new HudMetric("drivers", drivers.Count.ToString()));
            // Remaining HUD cells (pit stops, SC/VSC, conditions) bind to
            // /standings and /incidents once those endpoints land (§6.11, §6.13).
        }
        catch
        {
            // Surface load failures inside the views; the shell stays usable.
        }
    }

    [RelayCommand]
    private void SelectView(ConsoleView view) => ActiveView = view;

    [RelayCommand]
    private async Task BackToLauncherAsync() => await Shell.Current.GoToAsync("//launcher");
}

public sealed record ConsoleView(string Title, string Hotkey);

public sealed record HudMetric(string Label, string Value);
