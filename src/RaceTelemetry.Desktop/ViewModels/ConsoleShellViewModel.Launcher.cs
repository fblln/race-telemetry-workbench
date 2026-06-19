using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RaceTelemetry.Contracts;
using RaceTelemetry.Desktop.Controls;
using RaceTelemetry.Desktop.Services;

namespace RaceTelemetry.Desktop.ViewModels;

/// <summary>
/// Launcher funnel logic (year → circuit → session → drivers) merged into the unified
/// shell view model. All field names and logic are preserved from LauncherViewModel.
/// </summary>
public sealed partial class ConsoleShellViewModel
{
    private IReadOnlyList<SessionSummary> _allSessions = Array.Empty<SessionSummary>();
    private IReadOnlyList<SessionSummary> _filteredSessions = Array.Empty<SessionSummary>();
    private CancellationTokenSource? _searchDebounceCts;
    private bool _isRebuildingChoices;
    private bool _isRebuildingYearChoices;
    private bool _isReplacingSessions;
    private bool _isRestoringYearSelection;
    private bool _isRestoringCircuitSelection;
    private bool _isRestoringSessionSelection;
    private int _driverLoadVersion;
    private YearChoice? _lastSelectedYear;
    private CircuitChoice? _lastSelectedCircuit;
    private SessionSummary? _lastSelectedSession;
    private string? _renderedDriversSessionId;
    private string? _lastFilterSignature;
    private int _driverPrefetchVersion;
    private readonly Dictionary<int, YearChoice> _yearChoiceCache = new();
    private readonly Dictionary<string, CircuitChoice> _circuitChoiceCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DriverChoice>> _driverChoiceCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, int>> _driverPositionCache = new(StringComparer.OrdinalIgnoreCase);

    public BatchedObservableCollection<CircuitChoice> Circuits { get; } = new();
    public BatchedObservableCollection<YearChoice> Years { get; } = new();
    public BatchedObservableCollection<SessionSummary> Sessions { get; } = new();
    public BatchedObservableCollection<DriverChoice> Drivers { get; } = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isLoadingDrivers;

    [ObservableProperty]
    private bool _isRefreshingDrivers;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private YearChoice? _selectedYear;

    [ObservableProperty]
    private CircuitChoice? _selectedCircuit;

    [ObservableProperty]
    private SessionSummary? _selectedSession;

    [ObservableProperty]
    private int _selectedDriverCount;

    [ObservableProperty]
    private string _driverHeader = "Select a circuit and session";

    public bool CanOpen => SelectedSession is not null && SelectedDriverCount > 0 && !IsRefreshingDrivers;

    public string YearCountLabel
    {
        get
        {
            var years = Years.Count;
            return years == 1 ? "1 year" : $"{years} years";
        }
    }

    public string SeasonsLabel
    {
        get
        {
            var seasons = _allSessions.Select(s => s.Year).Distinct().Count();
            return seasons == 1 ? "1 season imported" : $"{seasons} seasons imported";
        }
    }

    public string ResultsSummaryLabel
    {
        get
        {
            var circuits = Circuits.Count == 1 ? "1 circuit" : $"{Circuits.Count} circuits";
            return $"{circuits} · {SeasonsLabel}";
        }
    }

    [RelayCommand]
    private Task LoadAsync() => FetchSessionsAsync(refresh: false);

    [RelayCommand]
    private Task RetryAsync() => FetchSessionsAsync(refresh: true);

    private async Task FetchSessionsAsync(bool refresh)
    {
        if (IsLoading) return;
        Error = null;
        var sessionsTask = _prefetch.GetSessionsAsync(refresh);
        var showLoader = !sessionsTask.IsCompletedSuccessfully;
        if (showLoader) IsLoading = true;

        try
        {
            _allSessions = await sessionsTask;
            OnPropertyChanged(nameof(SeasonsLabel));
            OnPropertyChanged(nameof(ResultsSummaryLabel));
            _filteredSessions = FilterSessions(_allSessions);
            _lastFilterSignature = FilterSignature(_filteredSessions);
            BuildYearChoices(_filteredSessions);
            BuildCircuitChoices(FilterBySelectedYear(_filteredSessions));

            if (Circuits.Count == 0)
                Error = "No imported sessions found. Import a session, then retry.";

            _ = PrefetchAllDriversAsync(_allSessions);
        }
        catch (Exception ex)
        {
            Error = $"Could not reach the Query API on {ApiHint}. Start the backend, then retry.\n{ex.Message}";
        }
        finally
        {
            if (showLoader) IsLoading = false;
        }
    }

    private async Task PrefetchAllDriversAsync(IReadOnlyList<SessionSummary> sessions)
    {
        var version = ++_driverPrefetchVersion;
        using var gate = new SemaphoreSlim(6);

        async Task Warm(SessionSummary session)
        {
            await gate.WaitAsync();
            try { await _launcherCache.Get(session.SessionId).Drivers; }
            catch { }
            finally { gate.Release(); }
        }

        await Task.WhenAll(sessions.Select(Warm));

        if (version != _driverPrefetchVersion) return;
        if (!string.IsNullOrWhiteSpace(SearchQuery))
            await MainThread.InvokeOnMainThreadAsync(RefreshFilter);
    }

    private void RefreshFilter()
    {
        var filtered = FilterSessions(_allSessions);
        var signature = FilterSignature(filtered);
        if (signature == _lastFilterSignature) return;
        _lastFilterSignature = signature;
        _filteredSessions = filtered;

        var preferredSessionId = SelectedSession?.SessionId;
        BuildYearChoices(filtered, preferredSessionId);
        BuildCircuitChoices(FilterBySelectedYear(filtered), preferredSessionId);
    }

    private static string ApiHint =>
        Environment.GetEnvironmentVariable("RACE_TELEMETRY_QUERY_API_BASEURL") ?? "http://localhost:5120";

    [RelayCommand]
    private void SelectAllDrivers()
    {
        foreach (var driver in Drivers) driver.IsSelected = true;
        UpdateSelectedDriverCount();
    }

    [RelayCommand]
    private void ClearDrivers()
    {
        foreach (var driver in Drivers) driver.IsSelected = false;
        UpdateSelectedDriverCount();
    }

    [RelayCommand]
    private void ToggleDriver(DriverChoice? driver)
    {
        if (driver is null) return;
        driver.IsSelected = !driver.IsSelected;
        UpdateSelectedDriverCount();
    }

    [RelayCommand]
    private void SelectYear(YearChoice? year)
    {
        if (year is not null) SelectedYear = year;
    }

    [RelayCommand]
    private void SelectCircuit(CircuitChoice? circuit)
    {
        if (circuit is not null) SelectedCircuit = circuit;
    }

    partial void OnSearchQueryChanged(string value)
    {
        _searchDebounceCts?.Cancel();
        var cts = _searchDebounceCts = new CancellationTokenSource();
        _ = ApplySearchAfterDelayAsync(cts.Token);
    }

    partial void OnSelectedYearChanged(YearChoice? value)
    {
        if (_isRestoringYearSelection) return;

        if (_isRebuildingYearChoices)
        {
            UpdateYearSelection(value);
            return;
        }

        if (value is null && _lastSelectedYear is not null && Years.Contains(_lastSelectedYear))
        {
            _isRestoringYearSelection = true;
            try { SelectedYear = _lastSelectedYear; }
            finally { _isRestoringYearSelection = false; }
            return;
        }

        _lastSelectedYear = value;
        UpdateYearSelection(value);
        BuildCircuitChoices(FilterBySelectedYear(_filteredSessions));
        OnPropertyChanged(nameof(Breadcrumb));
        OnPropertyChanged(nameof(SelectionHint));
    }

    partial void OnSelectedCircuitChanged(CircuitChoice? value)
    {
        if (_isRestoringCircuitSelection) return;

        if (!_isRebuildingChoices && value is null && _lastSelectedCircuit is not null && Circuits.Contains(_lastSelectedCircuit))
        {
            _isRestoringCircuitSelection = true;
            try { SelectedCircuit = _lastSelectedCircuit; }
            finally { _isRestoringCircuitSelection = false; }
            return;
        }

        _lastSelectedCircuit = value;
        UpdateCircuitSelection(value);

        if (_isRebuildingChoices) return;

        ApplySelectedCircuit(value, preserveDrivers: false, preferredSessionId: null);
        OnPropertyChanged(nameof(Breadcrumb));
        OnPropertyChanged(nameof(SelectionHint));
    }

    private void ApplySelectedCircuit(CircuitChoice? value, bool preserveDrivers, string? preferredSessionId)
    {
        if (!preserveDrivers)
        {
            _driverLoadVersion++;
            IsRefreshingDrivers = false;
        }

        var orderedSessions = value?.Sessions.OrderByDescending(s => s.Year).ThenBy(s => s.SessionType).ToArray()
            ?? Array.Empty<SessionSummary>();

        _isReplacingSessions = true;
        try { Sessions.SyncWith(orderedSessions); }
        finally { _isReplacingSessions = false; }

        if (value is null)
        {
            Drivers.Clear();
            _renderedDriversSessionId = null;
            SelectedSession = null;
            _lastSelectedSession = null;
            SelectedDriverCount = 0;
            DriverHeader = "Select a circuit and session";
            return;
        }

        var nextSession = preferredSessionId is null
            ? Sessions.FirstOrDefault(s => string.Equals(s.SessionType, "R", StringComparison.OrdinalIgnoreCase))
              ?? Sessions.FirstOrDefault()
            : Sessions.FirstOrDefault(s => string.Equals(s.SessionId, preferredSessionId, StringComparison.OrdinalIgnoreCase))
              ?? Sessions.FirstOrDefault(s => string.Equals(s.SessionType, "R", StringComparison.OrdinalIgnoreCase))
              ?? Sessions.FirstOrDefault();

        PrimeCircuit(value, nextSession);

        if (nextSession is null)
        {
            Drivers.Clear();
            _renderedDriversSessionId = null;
            SelectedSession = null;
            _lastSelectedSession = null;
            SelectedDriverCount = 0;
            DriverHeader = "Select a session";
            return;
        }

        if (!preserveDrivers) TryShowCachedDrivers(nextSession);

        if (!preserveDrivers || !string.Equals(SelectedSession?.SessionId, nextSession.SessionId, StringComparison.OrdinalIgnoreCase))
            SelectedSession = nextSession;
    }

    async partial void OnSelectedSessionChanged(SessionSummary? value)
    {
        if (_isRestoringSessionSelection || _isReplacingSessions) return;

        if (value is not null && !IsAvailableSession(value))
        {
            var fallback = _lastSelectedSession is not null && IsAvailableSession(_lastSelectedSession)
                ? _lastSelectedSession : Sessions.FirstOrDefault();
            _isRestoringSessionSelection = true;
            try { SelectedSession = fallback; }
            finally { _isRestoringSessionSelection = false; }
            return;
        }

        if (value is null && _lastSelectedSession is not null && Sessions.Contains(_lastSelectedSession))
        {
            _isRestoringSessionSelection = true;
            try { SelectedSession = _lastSelectedSession; }
            finally { _isRestoringSessionSelection = false; }
            return;
        }

        _lastSelectedSession = value;
        OnPropertyChanged(nameof(Breadcrumb));
        OnPropertyChanged(nameof(SelectionHint));

        if (value is not null)
            await LoadDriversAsync(value);
        else
        {
            Drivers.Clear();
            SelectedDriverCount = 0;
            _renderedDriversSessionId = null;
            DriverHeader = "Select a session";
        }
    }

    partial void OnSelectedDriverCountChanged(int value)
    {
        OnPropertyChanged(nameof(CanOpen));
        OnPropertyChanged(nameof(SelectionHint));
        OpenCommand.NotifyCanExecuteChanged();
        if (IsPreSession)
        {
            var cell = Hud.FirstOrDefault(h => h.Label == "drivers");
            if (cell is not null)
                cell.Value = value > 0 ? value.ToString() : "--";
        }
    }

    partial void OnIsLoadingDriversChanged(bool value) => OnPropertyChanged(nameof(CanOpen));
    partial void OnIsRefreshingDriversChanged(bool value)
    {
        OnPropertyChanged(nameof(CanOpen));
        OpenCommand.NotifyCanExecuteChanged();
    }

    private void BuildYearChoices(IReadOnlyList<SessionSummary> sessions, string? preferredSessionId = null)
    {
        YearChoice? preferredYear = null;
        var years = sessions.GroupBy(s => s.Year).OrderByDescending(g => g.Key)
            .Select(g => GetYearChoice(g.Key, g.Count())).ToList();

        var preferredValue = preferredSessionId is null
            ? _lastSelectedYear?.Year
            : sessions.FirstOrDefault(s => string.Equals(s.SessionId, preferredSessionId, StringComparison.OrdinalIgnoreCase))?.Year;

        if (preferredValue is not null)
            preferredYear = years.FirstOrDefault(y => y.Year == preferredValue.Value);
        preferredYear ??= years.FirstOrDefault();

        _isRebuildingYearChoices = true;
        try
        {
            Years.SyncWith(years);
            OnPropertyChanged(nameof(YearCountLabel));
            if (!ReferenceEquals(SelectedYear, preferredYear)) SelectedYear = preferredYear;
            UpdateYearSelection(SelectedYear);
        }
        finally { _isRebuildingYearChoices = false; }

        _lastSelectedYear = SelectedYear;
    }

    private YearChoice GetYearChoice(int year, int sessionCount)
    {
        if (!_yearChoiceCache.TryGetValue(year, out var choice))
        {
            choice = new YearChoice(year, sessionCount);
            _yearChoiceCache[year] = choice;
        }
        else { choice.SessionCount = sessionCount; }
        return choice;
    }

    private void BuildCircuitChoices(IReadOnlyList<SessionSummary> sessions, string? preferredSessionId = null)
    {
        CircuitChoice? preferredCircuit = null;
        var circuits = new List<CircuitChoice>();
        _isRebuildingChoices = true;
        try
        {
            foreach (var group in sessions.GroupBy(s => CircuitKey(s))
                         .OrderBy(g => g.Min(s => s.EventName)).ThenByDescending(g => g.Max(s => s.Year)))
            {
                var first = group.OrderByDescending(s => s.Year).First();
                var groupSessions = group.OrderByDescending(s => s.Year).ThenBy(s => s.SessionType).ToArray();
                var circuit = GetCircuitChoice(first, groupSessions);
                circuits.Add(circuit);

                if (preferredSessionId is not null && groupSessions.Any(s => string.Equals(s.SessionId, preferredSessionId, StringComparison.OrdinalIgnoreCase)))
                    preferredCircuit = circuit;
            }

            if (preferredCircuit is null && _lastSelectedCircuit is not null)
            {
                var previousKey = CircuitIdentity(_lastSelectedCircuit);
                preferredCircuit = circuits.FirstOrDefault(c => string.Equals(CircuitIdentity(c), previousKey, StringComparison.OrdinalIgnoreCase));
            }

            Circuits.SyncWith(circuits);
            OnPropertyChanged(nameof(ResultsSummaryLabel));
            var nextCircuit = preferredCircuit ?? Circuits.FirstOrDefault();
            if (!ReferenceEquals(SelectedCircuit, nextCircuit)) SelectedCircuit = nextCircuit;
            UpdateCircuitSelection(SelectedCircuit);
        }
        finally { _isRebuildingChoices = false; }

        var preserveDrivers = preferredSessionId is not null && preferredCircuit is not null;
        ApplySelectedCircuit(SelectedCircuit, preserveDrivers, preserveDrivers ? preferredSessionId : null);
    }

    private CircuitChoice GetCircuitChoice(SessionSummary first, IReadOnlyList<SessionSummary> sessions)
    {
        var cacheKey = CircuitChoiceCacheKey(sessions);
        if (_circuitChoiceCache.TryGetValue(cacheKey, out var choice)) return choice;

        choice = new CircuitChoice(
            first.CircuitName ?? first.EventName,
            first.Country,
            CountryFlag(first.Country),
            sessions.Count,
            sessions.Max(s => s.Year),
            sessions);
        _circuitChoiceCache[cacheKey] = choice;
        return choice;
    }

    private async Task LoadDriversAsync(SessionSummary session)
    {
        var version = ++_driverLoadVersion;
        var data = GetLauncherDataAsync(session.SessionId);
        var driversTask = data.Drivers;
        var standingsTask = data.Standings;

        try
        {
            IReadOnlyList<DriverSummary> drivers;
            if (driversTask.IsCompletedSuccessfully)
            {
                drivers = driversTask.Result;
            }
            else
            {
                var showLoader = Drivers.Count == 0;
                IsRefreshingDrivers = true;
                if (showLoader) { DriverHeader = "Loading drivers…"; IsLoadingDrivers = true; }

                try { drivers = await driversTask; }
                finally { IsRefreshingDrivers = false; if (showLoader) IsLoadingDrivers = false; }
                if (!IsCurrentDriverLoad(version, session)) return;
            }

            IsRefreshingDrivers = false;
            if (!IsCurrentDriverLoad(version, session)) return;
            var positions = standingsTask.IsCompletedSuccessfully ? ToPositions(session.SessionId, standingsTask.Result) : null;
            ShowDriverChoices(session.SessionId, BuildOrUpdateDriverChoices(session.SessionId, drivers, positions));

            if (positions is null || positions.Count == 0)
            {
                var standings = await standingsTask;
                if (!IsCurrentDriverLoad(version, session)) return;
                ApplyPositions(session.SessionId, ToPositions(session.SessionId, standings));
            }
        }
        catch (Exception ex)
        {
            IsRefreshingDrivers = false;
            if (version == _driverLoadVersion)
                DriverHeader = $"Drivers unavailable: {ex.Message}";
        }
    }

    private bool TryShowCachedDrivers(SessionSummary session)
    {
        if (!_driverChoiceCache.TryGetValue(session.SessionId, out var choices)) return false;
        if (_driverPositionCache.TryGetValue(session.SessionId, out var positions))
            ApplyPositionsToChoices(choices, positions);
        else
            ClearPositions(choices);
        foreach (var choice in choices) choice.SessionId = session.SessionId;
        SortDriverChoices(choices);
        ShowDriverChoices(session.SessionId, choices);
        return true;
    }

    private void ShowDriverChoices(string sessionId, IReadOnlyList<DriverChoice> choices)
    {
        _renderedDriversSessionId = sessionId;
        Drivers.SyncWith(choices);
        IsRefreshingDrivers = false;
        IsLoadingDrivers = false;
        DriverHeader = Drivers.Count == 0 ? "No drivers for this session" : "Choose drivers for replay";
        UpdateSelectedDriverCount();
    }

    private IReadOnlyList<DriverChoice> BuildOrUpdateDriverChoices(
        string sessionId, IReadOnlyList<DriverSummary> drivers, IReadOnlyDictionary<string, int>? positions)
    {
        if (_driverChoiceCache.TryGetValue(sessionId, out var cached) && SameDriverSet(cached, drivers))
        {
            RememberPositions(sessionId, positions);
            foreach (var choice in cached) choice.SessionId = sessionId;
            if (positions is null || positions.Count == 0) ClearPositions(cached);
            else ApplyPositionsToChoices(cached, positions);
            SortDriverChoices(cached);
            return cached;
        }

        if (SameDriverSet(Drivers, drivers))
        {
            var visible = Drivers.ToList();
            RememberPositions(sessionId, positions);
            foreach (var choice in visible) choice.SessionId = sessionId;
            if (positions is null || positions.Count == 0) ClearPositions(visible);
            else ApplyPositionsToChoices(visible, positions);
            SortDriverChoices(visible);
            CacheDriverChoices(sessionId, visible);
            return visible;
        }

        var ordered = positions is null
            ? drivers.OrderBy(d => d.DriverCode)
            : drivers.OrderBy(d => positions.GetValueOrDefault(d.DriverCode, 999)).ThenBy(d => d.DriverCode);

        RememberPositions(sessionId, positions);
        var newChoices = new List<DriverChoice>(drivers.Count);
        foreach (var driver in ordered)
        {
            var choice = new DriverChoice(
                sessionId, driver.DriverCode, driver.FullName, driver.TeamName,
                DriverPalette.HexFor(driver.DriverCode),
                positions?.TryGetValue(driver.DriverCode, out var pos) is true ? pos : null,
                isSelected: true);
            choice.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DriverChoice.IsSelected))
                    UpdateSelectedDriverCount();
            };
            newChoices.Add(choice);
        }
        CacheDriverChoices(sessionId, newChoices);
        return newChoices;
    }

    private bool IsCurrentDriverLoad(int version, SessionSummary session)
        => version == _driverLoadVersion
           && string.Equals(SelectedSession?.SessionId, session.SessionId, StringComparison.OrdinalIgnoreCase);

    private bool IsAvailableSession(SessionSummary session)
        => Sessions.Any(s => string.Equals(s.SessionId, session.SessionId, StringComparison.OrdinalIgnoreCase));

    private void ApplyPositions(string sessionId, IReadOnlyDictionary<string, int> positions)
    {
        if (positions.Count == 0) return;
        RememberPositions(sessionId, positions);
        if (!string.Equals(_renderedDriversSessionId, sessionId, StringComparison.OrdinalIgnoreCase)) return;
        if (!_driverChoiceCache.TryGetValue(sessionId, out var choices)) return;
        ApplyPositionsToChoices(choices, positions);
        SortDriverChoices(choices);
        ShowDriverChoices(sessionId, choices);
    }

    private static bool SameDriverSet(IReadOnlyList<DriverChoice> choices, IReadOnlyList<DriverSummary> drivers)
    {
        if (choices.Count != drivers.Count) return false;
        var codes = choices.Select(c => c.DriverCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return drivers.All(d => codes.Contains(d.DriverCode));
    }

    private static bool SameDriverSet(IEnumerable<DriverChoice> choices, IReadOnlyList<DriverSummary> drivers)
        => SameDriverSet(choices.ToArray(), drivers);

    private void RememberPositions(string sessionId, IReadOnlyDictionary<string, int>? positions)
    {
        if (positions is null || positions.Count == 0) return;
        _driverPositionCache[sessionId] = new Dictionary<string, int>(positions, StringComparer.OrdinalIgnoreCase);
    }

    private void CacheDriverChoices(string sessionId, List<DriverChoice> choices)
    {
        foreach (var key in _driverChoiceCache
                     .Where(pair => !string.Equals(pair.Key, sessionId, StringComparison.OrdinalIgnoreCase)
                                    && ReferenceEquals(pair.Value, choices))
                     .Select(pair => pair.Key).ToArray())
            _driverChoiceCache.Remove(key);
        _driverChoiceCache[sessionId] = choices;
    }

    private static void ClearPositions(IEnumerable<DriverChoice> choices)
    {
        foreach (var choice in choices) choice.Position = null;
    }

    private static void ApplyPositionsToChoices(List<DriverChoice> choices, IReadOnlyDictionary<string, int>? positions)
    {
        if (positions is null || positions.Count == 0) return;
        foreach (var choice in choices)
            if (positions.TryGetValue(choice.DriverCode, out var position)) choice.Position = position;
    }

    private static void SortDriverChoices(List<DriverChoice> choices)
        => choices.Sort(static (left, right) =>
        {
            var position = (left.Position ?? 999).CompareTo(right.Position ?? 999);
            return position != 0 ? position : string.CompareOrdinal(left.DriverCode, right.DriverCode);
        });

    private static Dictionary<string, int> ToPositions(string sessionId, StandingsResponse? standings)
        => standings is not null && string.Equals(standings.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)
            ? standings.Items.ToDictionary(r => r.DriverCode, r => r.Position, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private void UpdateSelectedDriverCount()
    {
        SelectedDriverCount = Drivers.Count(d => d.IsSelected);
        OnPropertyChanged(nameof(CanOpen));
    }

    private void UpdateYearSelection(YearChoice? selected)
    {
        foreach (var year in Years) year.IsSelected = ReferenceEquals(year, selected);
    }

    private void UpdateCircuitSelection(CircuitChoice? selected)
    {
        foreach (var circuit in Circuits) circuit.IsSelected = ReferenceEquals(circuit, selected);
    }

    private LauncherSessionData GetLauncherDataAsync(string sessionId) => _launcherCache.Get(sessionId);

    private void PrimeCircuit(CircuitChoice? circuit, SessionSummary? prioritySession)
    {
        if (circuit is null) return;
        if (prioritySession is not null)
        {
            var priorityData = GetLauncherDataAsync(prioritySession.SessionId);
            _ = priorityData.Drivers;
            _ = priorityData.Standings;
        }
        foreach (var session in circuit.Sessions)
        {
            if (prioritySession is not null && string.Equals(session.SessionId, prioritySession.SessionId, StringComparison.OrdinalIgnoreCase))
                continue;
            _ = GetLauncherDataAsync(session.SessionId).Drivers;
        }
    }

    private IReadOnlyList<SessionSummary> FilterSessions(IReadOnlyList<SessionSummary> sessions)
    {
        var query = SearchQuery.Trim();
        if (string.IsNullOrWhiteSpace(query)) return sessions;
        return sessions.Where(s =>
                Contains(s.EventName, query) || Contains(s.CircuitName, query) || Contains(s.Country, query)
                || Contains(s.SessionType, query) || Contains(s.SessionId, query)
                || s.Year.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(query, StringComparison.OrdinalIgnoreCase)
                || MatchesDriver(s, query))
            .ToArray();
    }

    private IReadOnlyList<SessionSummary> FilterBySelectedYear(IReadOnlyList<SessionSummary> sessions)
        => SelectedYear is null ? Array.Empty<SessionSummary>() : sessions.Where(s => s.Year == SelectedYear.Year).ToArray();

    private bool MatchesDriver(SessionSummary session, string query)
    {
        var driversTask = _launcherCache.Get(session.SessionId).Drivers;
        if (!driversTask.IsCompletedSuccessfully) return false;
        foreach (var driver in driversTask.Result)
            if (Contains(driver.DriverCode, query) || Contains(driver.FullName, query)) return true;
        return false;
    }

    private static bool Contains(string? value, string query)
        => value?.Contains(query, StringComparison.OrdinalIgnoreCase) is true;

    private async Task ApplySearchAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(220, cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
                await MainThread.InvokeOnMainThreadAsync(RefreshFilter);
        }
        catch (OperationCanceledException) { }
    }

    private static string FilterSignature(IReadOnlyList<SessionSummary> sessions)
        => string.Join("|", sessions.Select(s => s.SessionId));

    private static string CircuitKey(SessionSummary session)
        => $"{session.CircuitName ?? session.EventName}|{session.Country}".ToUpperInvariant();

    private static string CircuitIdentity(CircuitChoice circuit)
        => $"{circuit.CircuitName}|{circuit.Country}".ToUpperInvariant();

    private static string CircuitChoiceCacheKey(IReadOnlyList<SessionSummary> sessions)
        => string.Join("|", sessions.Select(s => s.SessionId));

    private static string CountryFlag(string? country) => CountryFlags.For(country);
}

// ── Support types re-homed from LauncherViewModel ────────────────────────────

public sealed partial class YearChoice : ObservableObject
{
    public YearChoice(int year, int sessionCount)
    {
        Year = year;
        _sessionCount = sessionCount;
    }

    public int Year { get; }

    [ObservableProperty]
    private int _sessionCount;

    [ObservableProperty]
    private bool _isSelected;

    public string SessionCountLabel => SessionCount == 1 ? "1 session" : $"{SessionCount} sessions";

    partial void OnSessionCountChanged(int value) => OnPropertyChanged(nameof(SessionCountLabel));
}

public sealed partial class CircuitChoice : ObservableObject
{
    public CircuitChoice(string circuitName, string? country, string flag, int sessionCount, int latestYear, IReadOnlyList<SessionSummary> sessions)
    {
        CircuitName = circuitName;
        Country = country;
        Flag = flag;
        SessionCount = sessionCount;
        LatestYear = latestYear;
        Sessions = sessions;
    }

    public string CircuitName { get; }
    public string? Country { get; }
    public string Flag { get; }
    public int SessionCount { get; }
    public int LatestYear { get; }
    public IReadOnlyList<SessionSummary> Sessions { get; }

    [ObservableProperty]
    private bool _isSelected;

    public string SessionCountLabel => SessionCount == 1 ? "1 session" : $"{SessionCount} sessions";
    public string YearsLabel => string.Join(" · ", Sessions.Select(s => s.Year).Distinct().OrderByDescending(y => y));
}

public sealed partial class DriverChoice : ObservableObject
{
    public DriverChoice(string sessionId, string driverCode, string? fullName, string? teamName, string railColor, int? position, bool isSelected)
    {
        _sessionId = sessionId;
        DriverCode = driverCode;
        FullName = fullName;
        TeamName = teamName;
        RailColor = railColor;
        Position = position;
        _isSelected = isSelected;
    }

    [ObservableProperty]
    private string _sessionId;

    public string DriverCode { get; }
    public string? FullName { get; }
    public string? TeamName { get; }
    public string RailColor { get; }

    [ObservableProperty]
    private int? _position;

    public string PositionText => Position is null ? "--" : $"P{Position}";
    public string ChipBackground => IsSelected ? "#43320F" : "#241F1B";
    public string ChipStroke => IsSelected ? "#7D5E12" : "#3A3128";
    public string SelectionGlyph => IsSelected ? "✓" : string.Empty;
    public string SelectionFill => IsSelected ? "#FFA60D" : "#14110E";
    public string SelectionStroke => IsSelected ? "#FFA60D" : "#524537";
    public string SelectionTextColor => IsSelected ? "#14110E" : "#8C857A";

    [ObservableProperty]
    private bool _isSelected;

    partial void OnPositionChanged(int? value) => OnPropertyChanged(nameof(PositionText));

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ChipBackground));
        OnPropertyChanged(nameof(ChipStroke));
        OnPropertyChanged(nameof(SelectionGlyph));
        OnPropertyChanged(nameof(SelectionFill));
        OnPropertyChanged(nameof(SelectionStroke));
        OnPropertyChanged(nameof(SelectionTextColor));
    }
}

public sealed class BatchedObservableCollection<T> : System.Collections.ObjectModel.ObservableCollection<T>
{
    private bool _suppressNotifications;

    public void SyncWith(IReadOnlyList<T> items)
    {
        if (!HasSameItems(items)) { ReplaceWith(items); return; }
        for (var i = 0; i < items.Count; i++)
        {
            var current = Items.IndexOf(items[i]);
            if (current >= 0 && current != i) Move(current, i);
        }
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
    }

    public void ReplaceWith(IEnumerable<T> items)
    {
        _suppressNotifications = true;
        try { Items.Clear(); foreach (var item in items) Items.Add(item); }
        finally { _suppressNotifications = false; }
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotifications) base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_suppressNotifications) base.OnPropertyChanged(e);
    }

    private bool HasSameItems(IReadOnlyList<T> items)
    {
        if (Items.Count != items.Count) return false;
        var comparer = EqualityComparer<T>.Default;
        var matched = new bool[items.Count];
        foreach (var existing in Items)
        {
            var found = false;
            for (var i = 0; i < items.Count; i++)
            {
                if (matched[i] || !comparer.Equals(existing, items[i])) continue;
                matched[i] = true; found = true; break;
            }
            if (!found) return false;
        }
        return true;
    }
}
