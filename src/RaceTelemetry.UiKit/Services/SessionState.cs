using RaceTelemetry.Contracts;

namespace RaceTelemetry.Desktop.Services;

/// <summary>
/// One source of truth for the launcher funnel + open session, replacing the old
/// ConsoleShellViewModel.Launcher state machine. Blazor's render diffing removes the need for
/// ObservableCollections, batching, and the _isRestoring*/_driverLoadVersion guards: components
/// read plain state and re-render on <see cref="Changed"/>.
/// </summary>
public sealed class SessionState
{
    private readonly IQueryApiClient _api;

    // ponytail: cancel the in-flight driver load on selection change — replaces _driverLoadVersion.
    private CancellationTokenSource? _driverLoad;

    public SessionState(IQueryApiClient api) => _api = api;

    public event Action? Changed;

    // --- Funnel selection ------------------------------------------------
    public int? Year { get; private set; }
    public string? SessionType { get; private set; } = "Race";
    public string? CircuitName { get; private set; }
    public SessionSummary? Session { get; private set; }

    public IReadOnlyList<SessionSummary> Sessions { get; private set; } = Array.Empty<SessionSummary>();
    public IReadOnlyList<DriverSummary> Drivers { get; private set; } = Array.Empty<DriverSummary>();
    public HashSet<string> SelectedDrivers { get; } = new();

    public bool IsSessionOpen => Session is not null;
    public bool IsLoadingDrivers { get; private set; }

    // --- Shell -----------------------------------------------------------
    public int ActiveView { get; private set; } = 0; // 0 = Home/launcher

    public void SetView(int i)
    {
        if (i == ActiveView) return;
        ActiveView = i;
        Changed?.Invoke();
    }

    /// <summary>Load the full session catalogue once; year + type filters are applied client-side
    /// (so every season's count is known without reloading when the user switches year).</summary>
    public async Task LoadSessionsAsync(CancellationToken ct = default)
    {
        Sessions = await _api.GetSessionsAsync(null, ct: ct);
        Changed?.Invoke();
    }

    public IEnumerable<string> Circuits =>
        FilteredSessions.Select(s => s.CircuitName ?? s.EventName).Distinct();

    public IEnumerable<SessionSummary> FilteredSessions =>
        Sessions.Where(s =>
            (Year is null || s.Year == Year) &&
            (SessionType is null || MatchesType(s.SessionType, SessionType)));

    // Query API uses short codes (R / Q / SQ / FP1…); the funnel chips use friendly labels.
    static bool MatchesType(string code, string label) => label switch
    {
        "Race" => code is "R" or "S",            // race or sprint
        "Qualifying" => code is "Q" or "SQ",
        "Practice" => code.StartsWith("FP", StringComparison.OrdinalIgnoreCase) || code is "P",
        _ => string.Equals(code, label, StringComparison.OrdinalIgnoreCase),
    };

    // All seasons are already loaded; switching year is just a client-side filter change.
    public void SelectYear(int year) { Year = year; Changed?.Invoke(); }
    public void SelectSessionType(string type) { SessionType = type; Changed?.Invoke(); }

    public async Task SelectCircuitAsync(string circuit)
    {
        CircuitName = circuit;
        // Pick the matching session row for this circuit + current type filter.
        var match = FilteredSessions.FirstOrDefault(s => (s.CircuitName ?? s.EventName) == circuit);
        await OpenSessionAsync(match);
    }

    /// <summary>Set the open session and (re)load its drivers, cancelling any in-flight load.</summary>
    public async Task OpenSessionAsync(SessionSummary? session)
    {
        Session = session;
        SelectedDrivers.Clear();
        // ponytail: keep the prior driver grid rendered until the new list arrives — avoids the
        // empty-then-full blink when switching circuits (lineups are near-identical across a season).

        _driverLoad?.Cancel();
        if (session is null) { Drivers = Array.Empty<DriverSummary>(); Changed?.Invoke(); return; }

        _driverLoad = new CancellationTokenSource();
        var ct = _driverLoad.Token;
        IsLoadingDrivers = true;
        Changed?.Invoke();

        try
        {
            var drivers = await _api.GetDriversAsync(session.SessionId, ct);
            if (ct.IsCancellationRequested) return; // a newer selection won
            Drivers = drivers;
            foreach (var d in drivers) SelectedDrivers.Add(d.DriverCode); // default: all selected
        }
        catch (OperationCanceledException) { return; }
        finally
        {
            if (!ct.IsCancellationRequested) { IsLoadingDrivers = false; Changed?.Invoke(); }
        }
    }

    public void ToggleDriver(string code)
    {
        if (!SelectedDrivers.Remove(code)) SelectedDrivers.Add(code);
        Changed?.Invoke();
    }

    public void SelectAllDrivers(bool all)
    {
        SelectedDrivers.Clear();
        if (all) foreach (var d in Drivers) SelectedDrivers.Add(d.DriverCode);
        Changed?.Invoke();
    }

    /// <summary>Context sent to the AG-UI agent — built from current state (replaces AppState).</summary>
    public TelemetryWorkspaceContext WorkspaceContext() =>
        new(
            SessionKey: Session?.SessionId,
            SelectedDrivers: SelectedDrivers.Count > 0 ? SelectedDrivers.ToList().AsReadOnly() : null,
            SelectedLap: null,
            SelectedCorner: null,
            WindowStart: null,
            WindowEnd: null,
            ActiveView: "reports-ai");
}
