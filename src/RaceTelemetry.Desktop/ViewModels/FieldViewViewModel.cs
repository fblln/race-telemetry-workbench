using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RaceTelemetry.Contracts;
using RaceTelemetry.Desktop.Services;

namespace RaceTelemetry.Desktop.ViewModels;

/// <summary>
/// Field View / timing tower (§8.13). Reads from the prefetched snapshot, so it
/// renders instantly when the session was warmed at open (§8.9).
/// </summary>
public sealed partial class FieldViewViewModel : ObservableObject
{
    private readonly ISessionPrefetchService _prefetch;
    private readonly AppState _state;

    public FieldViewViewModel(ISessionPrefetchService prefetch, AppState state)
    {
        _prefetch = prefetch;
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
            var snapshot = await _prefetch.GetAsync(_state.SessionId);
            Rows.Clear();
            if (snapshot.Standings is not null)
                foreach (var row in snapshot.Standings.Items)
                    Rows.Add(row);
            else
                Error = "Standings unavailable (start the Query API and reopen the session).";
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
