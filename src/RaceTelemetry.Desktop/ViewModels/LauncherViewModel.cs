using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RaceTelemetry.Contracts;
using RaceTelemetry.Desktop.Controls;
using RaceTelemetry.Desktop.Services;

namespace RaceTelemetry.Desktop.ViewModels;

/// <summary>
/// Home / Launcher (§8.11): circuit -> session -> drivers. Selecting a session
/// primes its prefetch; opening commits selected drivers to shared app state.
/// </summary>
public sealed partial class LauncherViewModel : ObservableObject
{
    private readonly ISessionPrefetchService _prefetch;
    private readonly ILauncherSessionCache _launcherCache;
    private readonly AppState _state;
    private IReadOnlyList<SessionSummary> _allSessions = Array.Empty<SessionSummary>();
    private CancellationTokenSource? _searchDebounceCts;
    private bool _isRebuildingChoices;
    private bool _isReplacingSessions;
    private bool _isRestoringCircuitSelection;
    private bool _isRestoringSessionSelection;
    private int _driverLoadVersion;
    private CircuitChoice? _lastSelectedCircuit;
    private SessionSummary? _lastSelectedSession;
    private string? _renderedDriversSessionId;
    private string? _lastFilterSignature;
    private int _driverPrefetchVersion;

    public LauncherViewModel(ISessionPrefetchService prefetch, ILauncherSessionCache launcherCache, AppState state)
    {
        _prefetch = prefetch;
        _launcherCache = launcherCache;
        _state = state;
    }

    public BatchedObservableCollection<CircuitChoice> Circuits { get; } = new();

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
    private CircuitChoice? _selectedCircuit;

    [ObservableProperty]
    private SessionSummary? _selectedSession;

    [ObservableProperty]
    private int _selectedDriverCount;

    [ObservableProperty]
    private string _driverHeader = "Select a circuit and session";

    public bool CanOpen => SelectedSession is not null && SelectedDriverCount > 0 && !IsRefreshingDrivers;

    /// <summary>"N seasons imported" for the panel subtitle (§2a home header).</summary>
    public string SeasonsLabel
    {
        get
        {
            var seasons = _allSessions.Select(s => s.Year).Distinct().Count();
            return seasons == 1 ? "1 season imported" : $"{seasons} seasons imported";
        }
    }

    /// <summary>Panel subtitle: result count for the current search, plus seasons covered.</summary>
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

    /// <summary>Retry after a failed load (e.g. the Query API was not running yet).</summary>
    [RelayCommand]
    private Task RetryAsync() => FetchSessionsAsync(refresh: true);

    private async Task FetchSessionsAsync(bool refresh)
    {
        if (IsLoading) return;
        Error = null;
        var sessionsTask = _prefetch.GetSessionsAsync(refresh);
        var showLoader = !sessionsTask.IsCompletedSuccessfully;
        if (showLoader)
            IsLoading = true;

        try
        {
            _allSessions = await sessionsTask;
            OnPropertyChanged(nameof(SeasonsLabel));
            OnPropertyChanged(nameof(ResultsSummaryLabel));
            var filtered = FilterSessions(_allSessions);
            _lastFilterSignature = FilterSignature(filtered);
            BuildCircuitChoices(filtered);

            if (Circuits.Count == 0)
                Error = "No imported sessions found. Import a session, then retry.";

            // Warm driver rosters for every session in the background so search-by-driver
            // (name, surname, or code) becomes effective as results trickle in.
            _ = PrefetchAllDriversAsync(_allSessions);
        }
        catch (Exception ex)
        {
            Error = $"Could not reach the Query API on {ApiHint}. Start the backend, then retry.\n{ex.Message}";
        }
        finally
        {
            if (showLoader)
                IsLoading = false;
        }
    }

    /// <summary>Best-effort background warm-up so driver search can match sessions not yet opened.</summary>
    private async Task PrefetchAllDriversAsync(IReadOnlyList<SessionSummary> sessions)
    {
        var version = ++_driverPrefetchVersion;
        using var gate = new SemaphoreSlim(6);

        async Task Warm(SessionSummary session)
        {
            await gate.WaitAsync();
            try
            {
                await _launcherCache.Get(session.SessionId).Drivers;
            }
            catch
            {
                // best-effort; an unreachable session just won't match driver search
            }
            finally
            {
                gate.Release();
            }
        }

        await Task.WhenAll(sessions.Select(Warm));

        if (version != _driverPrefetchVersion) return;
        if (!string.IsNullOrWhiteSpace(SearchQuery))
            await MainThread.InvokeOnMainThreadAsync(RefreshFilter);
    }

    /// <summary>Re-applies the current search query and rebuilds the circuit list if results changed.</summary>
    private void RefreshFilter()
    {
        var filtered = FilterSessions(_allSessions);
        var signature = FilterSignature(filtered);
        if (signature == _lastFilterSignature)
            return;
        _lastFilterSignature = signature;

        var preferredSessionId = SelectedSession?.SessionId;
        BuildCircuitChoices(filtered, preferredSessionId);
    }

    private static string ApiHint =>
        Environment.GetEnvironmentVariable("RACE_TELEMETRY_QUERY_API_BASEURL") ?? "http://localhost:5120";

    /// <summary>Open a session: set context, warm the snapshot, navigate to the console.</summary>
    [RelayCommand]
    private async Task OpenAsync()
    {
        var session = SelectedSession;
        if (session is null) return;

        _state.SessionId = session.SessionId;
        _state.EventName = session.EventName;
        _state.Year = session.Year;
        _state.SelectedDrivers.Clear();
        _state.SelectedDrivers.AddRange(Drivers.Where(d => d.IsSelected).Select(d => d.DriverCode));

        _prefetch.Prime(session.SessionId);
        await Shell.Current.GoToAsync("console");
    }

    [RelayCommand]
    private void SelectAllDrivers()
    {
        foreach (var driver in Drivers)
            driver.IsSelected = true;
        UpdateSelectedDriverCount();
    }

    [RelayCommand]
    private void ClearDrivers()
    {
        foreach (var driver in Drivers)
            driver.IsSelected = false;
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
    private void SelectCircuit(CircuitChoice? circuit)
    {
        if (circuit is not null)
            SelectedCircuit = circuit;
    }

    partial void OnSearchQueryChanged(string value)
    {
        _searchDebounceCts?.Cancel();
        var cts = _searchDebounceCts = new CancellationTokenSource();
        _ = ApplySearchAfterDelayAsync(cts.Token);
    }

    partial void OnSelectedCircuitChanged(CircuitChoice? value)
    {
        if (_isRestoringCircuitSelection)
            return;

        if (!_isRebuildingChoices && value is null && _lastSelectedCircuit is not null && Circuits.Contains(_lastSelectedCircuit))
        {
            _isRestoringCircuitSelection = true;
            try { SelectedCircuit = _lastSelectedCircuit; }
            finally { _isRestoringCircuitSelection = false; }
            return;
        }

        _lastSelectedCircuit = value;
        UpdateCircuitSelection(value);

        if (_isRebuildingChoices)
            return;

        ApplySelectedCircuit(value, preserveDrivers: false, preferredSessionId: null);
    }

    private void ApplySelectedCircuit(CircuitChoice? value, bool preserveDrivers, string? preferredSessionId)
    {
        var orderedSessions = value?.Sessions.OrderByDescending(s => s.Year).ThenBy(s => s.SessionType).ToArray()
            ?? Array.Empty<SessionSummary>();

        _isReplacingSessions = true;
        try
        {
            Sessions.ReplaceWith(orderedSessions);
        }
        finally
        {
            _isReplacingSessions = false;
        }

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

        // Warm only the driver rosters here; standings can wait until a session is
        // actually shown so a circuit switch does not fan out unnecessary work.
        PrimeCircuit(value);

        var nextSession = preferredSessionId is null
            ? Sessions.FirstOrDefault(s => string.Equals(s.SessionType, "R", StringComparison.OrdinalIgnoreCase))
              ?? Sessions.FirstOrDefault()
            : Sessions.FirstOrDefault(s => string.Equals(s.SessionId, preferredSessionId, StringComparison.OrdinalIgnoreCase))
              ?? Sessions.FirstOrDefault(s => string.Equals(s.SessionType, "R", StringComparison.OrdinalIgnoreCase))
                          ?? Sessions.FirstOrDefault();

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

        if (!preserveDrivers || !string.Equals(SelectedSession?.SessionId, nextSession.SessionId, StringComparison.OrdinalIgnoreCase))
            SelectedSession = nextSession;
    }

    async partial void OnSelectedSessionChanged(SessionSummary? value)
    {
        if (_isRestoringSessionSelection || _isReplacingSessions)
            return;

        if (value is null && _lastSelectedSession is not null && Sessions.Contains(_lastSelectedSession))
        {
            _isRestoringSessionSelection = true;
            try { SelectedSession = _lastSelectedSession; }
            finally { _isRestoringSessionSelection = false; }
            return;
        }

        _lastSelectedSession = value;

        // Warm the snapshot the moment a row is highlighted, so opening is instant.
        if (value is not null)
        {
            await LoadDriversAsync(value);
        }
        else
        {
            Drivers.Clear();
            SelectedDriverCount = 0;
            _renderedDriversSessionId = null;
            DriverHeader = "Select a session";
        }
    }

    partial void OnSelectedDriverCountChanged(int value) => OnPropertyChanged(nameof(CanOpen));

    partial void OnIsLoadingDriversChanged(bool value) => OnPropertyChanged(nameof(CanOpen));

    partial void OnIsRefreshingDriversChanged(bool value) => OnPropertyChanged(nameof(CanOpen));

    private void BuildCircuitChoices(IReadOnlyList<SessionSummary> sessions, string? preferredSessionId = null)
    {
        CircuitChoice? preferredCircuit = null;
        var circuits = new List<CircuitChoice>();
        _isRebuildingChoices = true;
        try
        {
            SelectedCircuit = null;

            foreach (var group in sessions
                         .GroupBy(s => CircuitKey(s))
                         .OrderBy(g => g.Min(s => s.EventName))
                         .ThenByDescending(g => g.Max(s => s.Year)))
            {
                var first = group.OrderByDescending(s => s.Year).First();
                var groupSessions = group
                    .OrderByDescending(s => s.Year)
                    .ThenBy(s => s.SessionType)
                    .ToArray();

                var circuit = new CircuitChoice(
                    first.CircuitName ?? first.EventName,
                    first.Country,
                    CountryFlag(first.Country),
                    groupSessions.Length,
                    groupSessions.Max(s => s.Year),
                    groupSessions);
                circuits.Add(circuit);

                if (preferredSessionId is not null && groupSessions.Any(s => string.Equals(s.SessionId, preferredSessionId, StringComparison.OrdinalIgnoreCase)))
                    preferredCircuit = circuit;
            }

            Circuits.ReplaceWith(circuits);
            OnPropertyChanged(nameof(ResultsSummaryLabel));
            SelectedCircuit = preferredCircuit ?? Circuits.FirstOrDefault();
            UpdateCircuitSelection(SelectedCircuit);
        }
        finally
        {
            _isRebuildingChoices = false;
        }

        var preserveDrivers = preferredSessionId is not null && preferredCircuit is not null;
        ApplySelectedCircuit(SelectedCircuit, preserveDrivers, preserveDrivers ? preferredSessionId : null);
    }

    private async Task LoadDriversAsync(SessionSummary session)
    {
        var version = ++_driverLoadVersion;
        var data = GetLauncherDataAsync(session.SessionId);
        var warmStandings = data.TryGetStandingsTask();

        try
        {
            // 1) Show chips as soon as the (fast) drivers call returns — never block
            //    the grid on the heavier standings query.
            IReadOnlyList<DriverSummary> drivers;
            if (data.Drivers.IsCompletedSuccessfully)
            {
                drivers = data.Drivers.Result; // warm cache → render synchronously, no spinner
            }
            else
            {
                var showLoader = Drivers.Count == 0;
                IsRefreshingDrivers = true;
                if (showLoader)
                {
                    DriverHeader = "Loading drivers…";
                    IsLoadingDrivers = true;
                }

                try { drivers = await data.Drivers; }
                finally
                {
                    IsRefreshingDrivers = false;
                    if (showLoader)
                        IsLoadingDrivers = false;
                }
                if (version != _driverLoadVersion) return;
            }

            // Use standings ordering immediately if it's already there, else fall back
            // to driver-code order and enrich below.
            IsRefreshingDrivers = false;
            var positions = warmStandings?.IsCompletedSuccessfully is true ? ToPositions(warmStandings.Result) : null;
            RenderChips(drivers, positions);
            _renderedDriversSessionId = session.SessionId;
            DriverHeader = Drivers.Count == 0 ? "No drivers for this session" : "Choose drivers for replay";

            // 2) Enrich positions/order once standings land (when they weren't ready).
            if (positions is null)
            {
                var standings = await data.Standings;
                if (version != _driverLoadVersion) return;
                ApplyPositions(ToPositions(standings));
            }
        }
        catch (Exception ex)
        {
            IsRefreshingDrivers = false;
            if (version == _driverLoadVersion)
                DriverHeader = $"Drivers unavailable: {ex.Message}";
        }
    }

    /// <summary>Build the driver chips ordered by finishing position when known, else by code.</summary>
    private void RenderChips(IReadOnlyList<DriverSummary> drivers, IReadOnlyDictionary<string, int>? positions)
    {
        SelectedDriverCount = 0;

        var ordered = positions is null
            ? drivers.OrderBy(d => d.DriverCode)
            : drivers.OrderBy(d => positions.GetValueOrDefault(d.DriverCode, 999)).ThenBy(d => d.DriverCode);

        var choices = new List<DriverChoice>();
        foreach (var driver in ordered)
        {
            var choice = new DriverChoice(
                driver.DriverCode,
                driver.FullName,
                driver.TeamName,
                DriverPalette.HexFor(driver.DriverCode),
                positions?.TryGetValue(driver.DriverCode, out var pos) is true ? pos : null,
                isSelected: true); // default to the whole field selected (§2a — Open replay ready)
            choice.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DriverChoice.IsSelected))
                    UpdateSelectedDriverCount();
            };
            choices.Add(choice);
        }

        Drivers.ReplaceWith(choices);
        UpdateSelectedDriverCount();
    }

    /// <summary>Fill in finishing positions and re-sort in place without rebuilding the chips.</summary>
    private void ApplyPositions(IReadOnlyDictionary<string, int> positions)
    {
        if (positions.Count == 0) return;

        foreach (var d in Drivers)
            if (positions.TryGetValue(d.DriverCode, out var pos))
                d.Position = pos;

        var sorted = Drivers.OrderBy(d => d.Position ?? 999).ThenBy(d => d.DriverCode).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var current = Drivers.IndexOf(sorted[i]);
            if (current != i)
                Drivers.Move(current, i);
        }
    }

    private static Dictionary<string, int> ToPositions(StandingsResponse? standings)
        => standings?.Items.ToDictionary(r => r.DriverCode, r => r.Position, StringComparer.OrdinalIgnoreCase)
           ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private void UpdateSelectedDriverCount()
    {
        SelectedDriverCount = Drivers.Count(d => d.IsSelected);
        OnPropertyChanged(nameof(CanOpen));
    }

    private void UpdateCircuitSelection(CircuitChoice? selected)
    {
        foreach (var circuit in Circuits)
            circuit.IsSelected = ReferenceEquals(circuit, selected);
    }

    /// <summary>
    /// Drivers and standings for a session, kicked off together and cached. Drivers
    /// usually return well before standings, so callers await them independently.
    /// </summary>
    private LauncherSessionData GetLauncherDataAsync(string sessionId)
        => _launcherCache.Get(sessionId);

    /// <summary>Warm driver/standings fetches for every session of a circuit so picks are instant.</summary>
    private void PrimeCircuit(CircuitChoice? circuit)
    {
        if (circuit is null) return;
        foreach (var session in circuit.Sessions)
            _ = GetLauncherDataAsync(session.SessionId).Drivers;
    }

    private IReadOnlyList<SessionSummary> FilterSessions(IReadOnlyList<SessionSummary> sessions)
    {
        var query = SearchQuery.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return sessions;

        return sessions.Where(s =>
                Contains(s.EventName, query)
                || Contains(s.CircuitName, query)
                || Contains(s.Country, query)
                || Contains(s.SessionType, query)
                || Contains(s.SessionId, query)
                || s.Year.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(query, StringComparison.OrdinalIgnoreCase)
                || MatchesDriver(s, query))
            .ToArray();
    }

    /// <summary>True if a (already-warmed) session roster has a driver matching by code, first/last name.</summary>
    private bool MatchesDriver(SessionSummary session, string query)
    {
        var driversTask = _launcherCache.Get(session.SessionId).Drivers;
        if (!driversTask.IsCompletedSuccessfully)
            return false;

        foreach (var driver in driversTask.Result)
        {
            if (Contains(driver.DriverCode, query) || Contains(driver.FullName, query))
                return true;
        }

        return false;
    }

    private static bool Contains(string? value, string query)
        => value?.Contains(query, StringComparison.OrdinalIgnoreCase) is true;

    private async Task ApplySearchAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(220, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return;

            // Skip the rebuild when the filtered result is unchanged (e.g. "monz" -> "monza"):
            // rebuilding would needlessly reset the circuit/session/driver selection.
            await MainThread.InvokeOnMainThreadAsync(RefreshFilter);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string FilterSignature(IReadOnlyList<SessionSummary> sessions)
        => string.Join("|", sessions.Select(s => s.SessionId));

    private static string CircuitKey(SessionSummary session)
        => $"{session.CircuitName ?? session.EventName}|{session.Country}".ToUpperInvariant();

    /// <summary>National flag emoji for a country (a factual identifier, not a team livery — §2a).</summary>
    private static string CountryFlag(string? country)
    {
        if (string.IsNullOrWhiteSpace(country)) return "🏳";

        return country.Trim().ToUpperInvariant() switch
        {
            "AUSTRALIA" => "🇦🇺",
            "AUSTRIA" => "🇦🇹",
            "AZERBAIJAN" => "🇦🇿",
            "BAHRAIN" => "🇧🇭",
            "BELGIUM" => "🇧🇪",
            "BRAZIL" => "🇧🇷",
            "CANADA" => "🇨🇦",
            "CHINA" => "🇨🇳",
            "HUNGARY" => "🇭🇺",
            "ITALY" => "🇮🇹",
            "JAPAN" => "🇯🇵",
            "MEXICO" => "🇲🇽",
            "MONACO" => "🇲🇨",
            "NETHERLANDS" => "🇳🇱",
            "QATAR" => "🇶🇦",
            "SAUDI ARABIA" => "🇸🇦",
            "SINGAPORE" => "🇸🇬",
            "SPAIN" => "🇪🇸",
            "UNITED ARAB EMIRATES" => "🇦🇪",
            "UNITED KINGDOM" => "🇬🇧",
            "USA" or "UNITED STATES" => "🇺🇸",
            _ => "🏳",
        };
    }
}

public sealed partial class CircuitChoice : ObservableObject
{
    public CircuitChoice(
        string circuitName,
        string? country,
        string flag,
        int sessionCount,
        int latestYear,
        IReadOnlyList<SessionSummary> sessions)
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

    /// <summary>Distinct seasons available for this circuit, e.g. "2024 · 2025".</summary>
    public string YearsLabel => string.Join(" · ", Sessions.Select(s => s.Year).Distinct().OrderByDescending(y => y));
}

public sealed partial class DriverChoice : ObservableObject
{
    public DriverChoice(
        string driverCode,
        string? fullName,
        string? teamName,
        string railColor,
        int? position,
        bool isSelected)
    {
        DriverCode = driverCode;
        FullName = fullName;
        TeamName = teamName;
        RailColor = railColor;
        Position = position;
        _isSelected = isSelected;
    }

    public string DriverCode { get; }
    public string? FullName { get; }
    public string? TeamName { get; }
    public string RailColor { get; }

    /// <summary>Finishing position — filled lazily once standings resolve.</summary>
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

public sealed class BatchedObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotifications;

    public void ReplaceWith(IEnumerable<T> items)
    {
        _suppressNotifications = true;
        try
        {
            Items.Clear();
            foreach (var item in items)
                Items.Add(item);
        }
        finally
        {
            _suppressNotifications = false;
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_suppressNotifications)
            return;

        base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_suppressNotifications)
            return;

        base.OnPropertyChanged(e);
    }
}
