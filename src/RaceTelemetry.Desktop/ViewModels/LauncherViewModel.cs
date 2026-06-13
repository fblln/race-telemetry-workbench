using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RaceTelemetry.Contracts;
using RaceTelemetry.Desktop.Services;

namespace RaceTelemetry.Desktop.ViewModels;

/// <summary>
/// Home / Launcher (§8.11): pick a session, then open the console.
/// </summary>
public sealed partial class LauncherViewModel : ObservableObject
{
    private readonly IQueryApiClient _api;
    private readonly AppState _state;

    public LauncherViewModel(IQueryApiClient api, AppState state)
    {
        _api = api;
        _state = state;
    }

    public ObservableCollection<SessionSummary> Sessions { get; } = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private SessionSummary? _selectedSession;

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        Error = null;
        try
        {
            Sessions.Clear();
            foreach (var s in await _api.GetSessionsAsync())
                Sessions.Add(s);
        }
        catch (Exception ex)
        {
            Error = $"Could not reach the Query API: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenAsync()
    {
        if (SelectedSession is null) return;
        _state.SessionId = SelectedSession.SessionId;
        _state.EventName = SelectedSession.EventName;
        _state.Year = SelectedSession.Year;
        await Shell.Current.GoToAsync("console");
    }

    private bool CanOpen() => SelectedSession is not null;

    partial void OnSelectedSessionChanged(SessionSummary? value) => OpenCommand.NotifyCanExecuteChanged();
}
