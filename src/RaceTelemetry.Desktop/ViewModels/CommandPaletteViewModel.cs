using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RaceTelemetry.Contracts;
using RaceTelemetry.Desktop.Services;

namespace RaceTelemetry.Desktop.ViewModels;

/// <summary>
/// Global command palette (§2a): a ⌘K / "/" launcher over a dimmed faux-viewport
/// that fuzzy-matches imported sessions and quick actions. Grouped sections use
/// the uppercase micro-label; the highlighted row takes the amber left rail and an
/// "Open ↵" hint. Shared singleton so every page opens the same palette.
/// </summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private readonly ISessionPrefetchService _prefetch;
    private readonly AppState _state;
    private IReadOnlyList<SessionSummary> _sessions = Array.Empty<SessionSummary>();
    private IReadOnlyList<PaletteAction> _quickActions = Array.Empty<PaletteAction>();

    public CommandPaletteViewModel(ISessionPrefetchService prefetch, AppState state)
    {
        _prefetch = prefetch;
        _state = state;
    }

    public ObservableCollection<PaletteItem> SessionItems { get; } = new();
    public ObservableCollection<PaletteItem> ActionItems { get; } = new();

    public bool HasSessions => SessionItems.Count > 0;
    public bool HasActions => ActionItems.Count > 0;
    public bool HasResults => HasSessions || HasActions;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private PaletteItem? _selected;

    /// <summary>Host pages register their context-specific quick actions (view switches, navigation).</summary>
    public void SetQuickActions(IReadOnlyList<PaletteAction> actions) => _quickActions = actions;

    [RelayCommand]
    public async Task OpenAsync()
    {
        if (_sessions.Count == 0)
        {
            try { _sessions = await _prefetch.GetSessionsAsync(); }
            catch { /* leave palette usable for quick actions even if sessions fail */ }
        }

        Query = string.Empty;
        Rebuild();
        IsOpen = true;
    }

    [RelayCommand]
    public void Close() => IsOpen = false;

    [RelayCommand]
    public async Task ExecuteAsync(PaletteItem? item)
    {
        item ??= Selected ?? SessionItems.Concat(ActionItems).FirstOrDefault();
        if (item is null) return;

        IsOpen = false;
        await item.InvokeAsync();
    }

    /// <summary>Run the highlighted item — bound to Enter in the query field.</summary>
    [RelayCommand]
    public Task ActivateSelectedAsync() => ExecuteAsync(Selected);

    partial void OnQueryChanged(string value) => Rebuild();

    partial void OnSelectedChanged(PaletteItem? oldValue, PaletteItem? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    private void Rebuild()
    {
        var q = Query.Trim();

        SessionItems.Clear();
        foreach (var s in _sessions.Where(s => Matches(s, q))
                     .OrderByDescending(s => s.Year)
                     .ThenBy(s => s.EventName)
                     .Take(8))
            SessionItems.Add(SessionItem(s));

        ActionItems.Clear();
        foreach (var a in _quickActions.Where(a => string.IsNullOrEmpty(q) || a.Title.Contains(q, StringComparison.OrdinalIgnoreCase)))
            ActionItems.Add(new PaletteItem(a.Title, a.Subtitle, flag: string.Empty, icon: a.Icon, a.Run));

        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(HasActions));
        OnPropertyChanged(nameof(HasResults));

        Selected = SessionItems.Concat(ActionItems).FirstOrDefault();
    }

    private PaletteItem SessionItem(SessionSummary s) => new(
        $"{s.Year} {s.EventName}",
        $"{s.CircuitName} · {CountryFlags.SessionTypeName(s.SessionType)}",
        flag: CountryFlags.For(s.Country),
        icon: string.Empty,
        () =>
        {
            _state.SessionId = s.SessionId;
            _state.EventName = s.EventName;
            _state.Year = s.Year;
            _prefetch.Prime(s.SessionId);
            return Shell.Current.GoToAsync("console");
        });

    private static bool Matches(SessionSummary s, string q)
    {
        if (string.IsNullOrEmpty(q)) return true;
        return Has(s.EventName, q) || Has(s.CircuitName, q) || Has(s.Country, q)
            || Has(s.SessionType, q) || Has(s.SessionId, q)
            || s.Year.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Has(string? value, string q)
        => value?.Contains(q, StringComparison.OrdinalIgnoreCase) is true;
}

/// <summary>A single palette result with a title, subtitle, leading flag or icon, and action.</summary>
public sealed partial class PaletteItem : ObservableObject
{
    private readonly Func<Task> _run;

    public PaletteItem(string title, string subtitle, string flag, string icon, Func<Task> run)
    {
        Title = title;
        Subtitle = subtitle;
        Flag = flag;
        Icon = icon;
        _run = run;
    }

    public string Title { get; }
    public string Subtitle { get; }

    /// <summary>National flag for session rows (empty for actions).</summary>
    public string Flag { get; }
    public bool HasFlag => !string.IsNullOrEmpty(Flag);

    /// <summary>Glyph for quick-action rows (empty for sessions).</summary>
    public string Icon { get; }
    public bool HasIcon => !string.IsNullOrEmpty(Icon);

    /// <summary>True for the highlighted row — drives the amber left rail and the Open ↵ hint.</summary>
    [ObservableProperty]
    private bool _isSelected;

    public Task InvokeAsync() => _run();
}

/// <summary>A host-registered quick action shown in the palette.</summary>
public sealed record PaletteAction(string Title, string Subtitle, string Icon, Func<Task> Run);
