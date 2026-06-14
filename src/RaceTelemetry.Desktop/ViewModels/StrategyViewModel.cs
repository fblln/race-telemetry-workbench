using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RaceTelemetry.Desktop.Controls;
using RaceTelemetry.Desktop.Services;

namespace RaceTelemetry.Desktop.ViewModels;

/// <summary>
/// Strategy view (§8.15): tire-strategy gantt. One row per driver with
/// compound-colored stint bars on a shared lap axis, built from the session's
/// stint analysis (/stints/analyze). Pit-loss and degradation cards (§6.10.4,
/// §6.14) layer on later.
/// </summary>
public sealed partial class StrategyViewModel : ObservableObject
{
    private readonly ISessionPrefetchService _prefetch;
    private readonly AppState _state;

    public StrategyViewModel(ISessionPrefetchService prefetch, AppState state)
    {
        _prefetch = prefetch;
        _state = state;
    }

    [ObservableProperty]
    private string _caption = "Stints per driver on a shared lap axis — pit boundaries are the segment edges.";

    public IReadOnlyList<StrategyGanttDrawable.DriverStrategy> Drivers { get; private set; }
        = Array.Empty<StrategyGanttDrawable.DriverStrategy>();

    public int TotalLaps { get; private set; } = 1;

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_state.SessionId is null) return;
        try
        {
            var snapshot = await _prefetch.GetAsync(_state.SessionId);
            var stints = snapshot.Stints;
            if (stints is null || stints.Items.Count == 0)
            {
                Caption = "Strategy unavailable (start the Query API and reopen the session).";
                return;
            }

            TotalLaps = Math.Max(1, stints.Items.Max(s => s.LastLapNumber));

            // Optional finishing order from standings so the gantt reads top-down by result.
            var order = snapshot.Standings?.Items
                .Select((row, index) => (row.DriverCode, index))
                .ToDictionary(pair => pair.DriverCode, pair => pair.index, StringComparer.OrdinalIgnoreCase);

            Drivers = stints.Items
                .GroupBy(s => s.DriverCode, StringComparer.OrdinalIgnoreCase)
                .Select(group => new StrategyGanttDrawable.DriverStrategy(
                    group.Key,
                    group.OrderBy(s => s.StintNumber)
                        .Select(s => new StrategyGanttDrawable.Stint(s.Compound ?? "UNKNOWN", s.Laps))
                        .ToList()))
                .OrderBy(d => order is not null && order.TryGetValue(d.Code, out var pos) ? pos : int.MaxValue)
                .ThenBy(d => d.Code, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            Caption = $"Strategy unavailable: {ex.Message}";
        }
    }
}
