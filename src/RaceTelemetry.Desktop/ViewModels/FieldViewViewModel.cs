using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RaceTelemetry.Desktop.Services;

namespace RaceTelemetry.Desktop.ViewModels;

/// <summary>
/// Field View / timing tower (§8.13): all drivers at once, backed by /standings (§6.11).
/// </summary>
public sealed partial class FieldViewViewModel : ObservableObject
{
    private readonly IQueryApiClient _api;
    private readonly AppState _state;

    public FieldViewViewModel(IQueryApiClient api, AppState state)
    {
        _api = api;
        _state = state;
    }

    public ObservableCollection<StandingRow> Rows { get; } = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string _filter = string.Empty;

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_state.SessionId is null) return;
        IsLoading = true;
        Error = null;
        try
        {
            Rows.Clear();
            var standings = await _api.GetStandingsAsync(_state.SessionId);
            if (standings is not null)
                foreach (var row in standings.Items)
                    Rows.Add(row);
        }
        catch (Exception ex)
        {
            Error = $"Standings unavailable: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
