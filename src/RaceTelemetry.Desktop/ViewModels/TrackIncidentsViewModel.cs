using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RaceTelemetry.Contracts;
using RaceTelemetry.Desktop.Controls;
using RaceTelemetry.Desktop.Services;

namespace RaceTelemetry.Desktop.ViewModels;

/// <summary>
/// Track Incidents and Hard-Braking (§8.14). Reads from the prefetched snapshot;
/// places hard-braking hotspots on the data-derived outline and keeps the list
/// and map in sync.
/// </summary>
public sealed partial class TrackIncidentsViewModel : ObservableObject
{
    private readonly ISessionPrefetchService _prefetch;
    private readonly AppState _state;
    private double _maxBrakingG = 1;

    public TrackIncidentsViewModel(ISessionPrefetchService prefetch, AppState state)
    {
        _prefetch = prefetch;
        _state = state;
    }

    public ObservableCollection<Incident> Incidents { get; } = new();

    /// <summary>Data-derived outline for the map drawable.</summary>
    public IReadOnlyList<TrackPoint> Outline { get; private set; } = Array.Empty<TrackPoint>();

    [ObservableProperty]
    private IncidentSummary? _summary;

    [ObservableProperty]
    private Incident? _selected;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _error;

    /// <summary>Build amber heat dots for hard-braking incidents that carry a position.</summary>
    public IReadOnlyList<TrackMapDrawable.IncidentDot> BuildDots() => Incidents
        .Where(i => i.Type == "hard_braking" && i.X is not null && i.Y is not null)
        .Select(i => new TrackMapDrawable.IncidentDot(
            i.X!.Value,
            i.Y!.Value,
            (i.Metrics?.PeakBrakingG ?? 0) / _maxBrakingG,
            ReferenceEquals(i, Selected)))
        .ToList();

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_state.SessionId is null) return;
        IsLoading = true;
        Error = null;
        try
        {
            var snapshot = await _prefetch.GetAsync(_state.SessionId);
            Outline = snapshot.TrackOutline;
            Incidents.Clear();
            if (snapshot.Incidents is not null)
            {
                foreach (var i in snapshot.Incidents.Items)
                    Incidents.Add(i);
                Summary = snapshot.Incidents.Summary;
                _maxBrakingG = Math.Max(0.1, Incidents
                    .Select(i => i.Metrics?.PeakBrakingG ?? 0)
                    .DefaultIfEmpty(0)
                    .Max());
            }
            else
            {
                Error = "Incidents unavailable (start the Query API and reopen the session).";
            }
        }
        catch (Exception ex)
        {
            Error = $"Incidents unavailable: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
