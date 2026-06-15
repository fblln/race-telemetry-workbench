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
    private readonly ISessionPrefetchService _prefetch;
    private readonly AppState _state;

    public SessionConsoleViewModel(ISessionPrefetchService prefetch, AppState state)
    {
        _prefetch = prefetch;
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
        ActiveView.IsActive = true;
    }

    public ObservableCollection<ConsoleView> Views { get; }

    [ObservableProperty]
    private ConsoleView _activeView;

    partial void OnActiveViewChanged(ConsoleView? oldValue, ConsoleView? newValue)
    {
        if (oldValue is not null) oldValue.IsActive = false;
        if (newValue is not null) newValue.IsActive = true;
    }

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
            // Already warm if the launcher primed it on selection/open.
            var snap = await _prefetch.GetAsync(_state.SessionId);

            Hud.Add(new HudMetric("drivers", snap.Drivers.Count.ToString()));

            if (snap.Standings is not null)
            {
                var stops = snap.Standings.Items.Sum(r => r.PitCount);
                Hud.Add(new HudMetric("pit stops", stops.ToString()));
                var classified = snap.Standings.Items.Count(r => r.LastLapMs is not null);
                if (classified > 0)
                    Hud.Add(new HudMetric("avg stops", (stops / (double)classified).ToString("0.0")));
            }

            if (snap.Incidents is not null)
            {
                var sc = snap.Incidents.Items.Count(i => i.Type is "safety_car");
                var vsc = snap.Incidents.Items.Count(i => i.Type is "vsc");
                Hud.Add(new HudMetric("SC / VSC", $"{sc} / {vsc}"));
            }

            if (snap.ReplayMetadata is { DurationMs: > 0 } meta)
                Hud.Add(new HudMetric("duration", TimeSpan.FromMilliseconds(meta.DurationMs).ToString(@"hh\:mm\:ss")));
        }
        catch
        {
            // Surface load failures inside the views; the shell stays usable.
        }
    }

    [RelayCommand]
    private void SelectView(ConsoleView view) => ActiveView = view;

    /// <summary>Switch view by 1-based index — bound to number-key accelerators (§8.12).</summary>
    [RelayCommand]
    private void SelectIndex(string oneBasedIndex)
    {
        if (int.TryParse(oneBasedIndex, out var i) && i >= 1 && i <= Views.Count)
            ActiveView = Views[i - 1];
    }

    [RelayCommand]
    private async Task BackToLauncherAsync() => await Shell.Current.GoToAsync("//launcher");
}

public sealed partial class ConsoleView : ObservableObject
{
    public ConsoleView(string title, string hotkey)
    {
        Title = title;
        Hotkey = hotkey;
    }

    public string Title { get; }
    public string Hotkey { get; }

    /// <summary>True for the active rail item — drives the amber rail + muted fill (§2a).</summary>
    [ObservableProperty]
    private bool _isActive;
}

public sealed record HudMetric(string Label, string Value);
