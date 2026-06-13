using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RaceTelemetry.Desktop.Services;

namespace RaceTelemetry.Desktop.ViewModels;

/// <summary>
/// Track Incidents and Hard-Braking (§8.14), backed by /incidents (§6.13).
/// </summary>
public sealed partial class TrackIncidentsViewModel : ObservableObject
{
    private readonly IQueryApiClient _api;
    private readonly AppState _state;

    public TrackIncidentsViewModel(IQueryApiClient api, AppState state)
    {
        _api = api;
        _state = state;
    }

    public ObservableCollection<Incident> Incidents { get; } = new();

    [ObservableProperty]
    private IncidentSummary? _summary;

    [ObservableProperty]
    private Incident? _selected;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _error;

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_state.SessionId is null) return;
        IsLoading = true;
        Error = null;
        try
        {
            Incidents.Clear();
            var result = await _api.GetIncidentsAsync(_state.SessionId);
            if (result is not null)
            {
                foreach (var i in result.Items)
                    Incidents.Add(i);
                Summary = result.Summary;
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
