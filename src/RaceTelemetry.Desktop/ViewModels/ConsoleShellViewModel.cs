using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RaceTelemetry.Contracts;
using RaceTelemetry.Desktop.Services;

namespace RaceTelemetry.Desktop.ViewModels;

public enum ShellState { PreSession, SessionOpen }

/// <summary>
/// Unified shell view model: owns the persistent chrome (breadcrumb, HUD, rail) and
/// hosts the launcher funnel as the PreSession content. Replaces LauncherViewModel +
/// SessionConsoleViewModel per the Option A unified-shell spec.
/// </summary>
public sealed partial class ConsoleShellViewModel : ObservableObject
{
    private readonly ISessionPrefetchService _prefetch;
    private readonly ILauncherSessionCache _launcherCache;
    internal readonly AppState _appState;

    public ConsoleShellViewModel(ISessionPrefetchService prefetch, ILauncherSessionCache launcherCache, AppState appState)
    {
        _prefetch = prefetch;
        _launcherCache = launcherCache;
        _appState = appState;

        Func<bool> isLockedFn = () => IsPreSession;
        Views = new ObservableCollection<ConsoleView>
        {
            new("Home",          "0", 0, () => false),
            new("Overview",      "1", 1, isLockedFn),
            new("Replay",        "2", 2, isLockedFn),
            new("Strategy",      "3", 3, isLockedFn),
            new("Field",         "4", 4, isLockedFn),
            new("Incidents",     "5", 5, isLockedFn),
            new("Head-to-Head",  "6", 6, isLockedFn),
            new("Lap Detail",    "7", 7, isLockedFn),
            new("Telemetry",     "8", 8, isLockedFn),
            new("Reports & AI",  "9", 9, isLockedFn),
        };
        _activeView = Views[0];
        Views[0].IsActive = true;

        SeedPlaceholderHud();
    }

    // ── Shell state ──────────────────────────────────────────────────────────

    private ShellState _shellState = ShellState.PreSession;
    public ShellState State
    {
        get => _shellState;
        private set
        {
            if (_shellState == value) return;
            _shellState = value;
            OnPropertyChanged();
            RaiseShellChanged();
        }
    }

    public bool IsPreSession => State == ShellState.PreSession;
    public bool IsSessionOpen => State == ShellState.SessionOpen;

    // ── Rail & active view ───────────────────────────────────────────────────

    public ObservableCollection<ConsoleView> Views { get; }

    [ObservableProperty]
    private ConsoleView _activeView;

    partial void OnActiveViewChanged(ConsoleView? oldValue, ConsoleView newValue)
    {
        if (oldValue is not null) oldValue.IsActive = false;
        newValue.IsActive = true;
    }

    // ── HUD ─────────────────────────────────────────────────────────────────

    public ObservableCollection<HudMetric> Hud { get; } = new();

    private void SeedPlaceholderHud()
    {
        Hud.Clear();
        Hud.Add(new HudMetric("drivers", "--", isPlaceholder: true));
        Hud.Add(new HudMetric("pit stops", "--", isPlaceholder: true));
        Hud.Add(new HudMetric("SC / VSC", "--", isPlaceholder: true));
        Hud.Add(new HudMetric("duration", "--", isPlaceholder: true));
    }

    private async Task LoadSessionHudAsync(string sessionId)
    {
        try
        {
            var snap = await _prefetch.GetAsync(sessionId);
            Hud.Clear();
            Hud.Add(new HudMetric("drivers", snap.Drivers.Count.ToString()));
            if (snap.Standings is not null)
            {
                var stops = snap.Standings.Items.Sum(r => r.PitCount);
                Hud.Add(new HudMetric("pit stops", stops.ToString()));
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
            // Shell stays usable even if HUD fails to load
        }
    }

    // ── Computed shell properties ────────────────────────────────────────────

    public string Breadcrumb => BuildBreadcrumb();
    public string SearchPlaceholder => IsPreSession
        ? "Search circuits, years, countries, or drivers…"
        : "Search / query";
    public string SelectionHint
    {
        get
        {
            var year = SelectedYear?.Year.ToString() ?? "—";
            var circuit = SelectedCircuit?.CircuitName ?? "—";
            var session = SelectedSession is not null ? CountryFlags.SessionTypeName(SelectedSession.SessionType) : "—";
            var count = SelectedDriverCount;
            return $"{year} · {circuit} · {session} · {count} {(count == 1 ? "driver" : "drivers")}";
        }
    }

    private string BuildBreadcrumb()
    {
        var year = SelectedYear?.Year.ToString() ?? "2025";
        if (IsSessionOpen)
        {
            var sc = ShortCode(SelectedCircuit?.Country);
            var sess = SessionCode(SelectedSession);
            return $"{year} / {sc} / {sess} / {_appState.SessionId}";
        }
        var circuitCode = SelectedCircuit is not null ? ShortCode(SelectedCircuit.Country) : "—";
        var sessionCode = SelectedSession is not null ? SessionCode(SelectedSession) : "—";
        return $"{year} / {circuitCode} / {sessionCode}";
    }

    private static string ShortCode(string? country) => country?.Trim().ToUpperInvariant() switch
    {
        "AUSTRALIA" => "AUS",
        "AUSTRIA" => "AUT",
        "AZERBAIJAN" => "AZE",
        "BAHRAIN" => "BHR",
        "BELGIUM" => "BEL",
        "BRAZIL" => "BRA",
        "CANADA" => "CAN",
        "CHINA" => "CHN",
        "HUNGARY" => "HUN",
        "ITALY" => "ITA",
        "JAPAN" => "JPN",
        "MEXICO" => "MEX",
        "MONACO" => "MCO",
        "NETHERLANDS" => "NED",
        "QATAR" => "QAT",
        "SAUDI ARABIA" => "KSA",
        "SINGAPORE" => "SGP",
        "SPAIN" => "ESP",
        "UNITED ARAB EMIRATES" => "UAE",
        "UNITED KINGDOM" => "GBR",
        "USA" or "UNITED STATES" => "USA",
        { } c => c[..Math.Min(3, c.Length)],
        null => "—",
    };

    private static string SessionCode(SessionSummary? session) => session?.SessionType?.Trim().ToUpperInvariant() switch
    {
        "R" => "RACE",
        "Q" => "QUALI",
        "S" or "SQ" or "SS" => "SPRINT",
        "FP1" => "FP1",
        "FP2" => "FP2",
        "FP3" => "FP3",
        _ => session?.SessionType?.ToUpperInvariant() ?? "—",
    };

    // ── Commands ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private void GoHome()
    {
        State = ShellState.PreSession;
        ActiveView = Views[0];
        SeedPlaceholderHud();
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenAsync()
    {
        var session = SelectedSession;
        if (session is null) return;

        _appState.SessionId = session.SessionId;
        _appState.EventName = session.EventName;
        _appState.Year = session.Year;
        _appState.SelectedDrivers.Clear();
        _appState.SelectedDrivers.AddRange(Drivers.Where(d => d.IsSelected).Select(d => d.DriverCode));

        _prefetch.Prime(session.SessionId);
        await LoadSessionHudAsync(session.SessionId);

        State = ShellState.SessionOpen;
        ActiveView = Views[9]; // Reports & AI is the priority view
    }

    /// <summary>Called by CommandPalette after the palette sets AppState — opens the session without re-loading the launcher funnel.</summary>
    public async Task OpenFromPaletteAsync()
    {
        var sessionId = _appState.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        _prefetch.Prime(sessionId);
        await LoadSessionHudAsync(sessionId);
        State = ShellState.SessionOpen;
        ActiveView = Views[9];
    }

    [RelayCommand]
    private void SelectIndex(string key)
    {
        if (!int.TryParse(key, out var i) || i < 0 || i >= Views.Count) return;
        if (i == 0) { GoHome(); return; }
        if (IsPreSession) return;
        ActiveView = Views[i];
    }

    private void RaiseShellChanged()
    {
        OnPropertyChanged(nameof(IsPreSession));
        OnPropertyChanged(nameof(IsSessionOpen));
        OnPropertyChanged(nameof(Breadcrumb));
        OnPropertyChanged(nameof(SearchPlaceholder));
        OnPropertyChanged(nameof(SelectionHint));
        foreach (var view in Views) view.RefreshLocked();
        OpenCommand.NotifyCanExecuteChanged();
    }
}
