using CommunityToolkit.Mvvm.ComponentModel;
using RaceTelemetry.Desktop.Services;

namespace RaceTelemetry.Desktop.ViewModels;

/// <summary>
/// Replay Workspace (§8.5). Scaffold placeholder — wire to /replay/metadata,
/// /replay/chunk, and /replay/context, with the linked timebase (§7.7).
/// </summary>
public sealed partial class ReplayWorkspaceViewModel : ObservableObject
{
    private readonly IQueryApiClient _api;
    private readonly AppState _state;

    public ReplayWorkspaceViewModel(IQueryApiClient api, AppState state)
    {
        _api = api;
        _state = state;
    }

    [ObservableProperty]
    private string _status = "Replay workspace — scaffold. Wire chunk loading and the track map / waveform panels here.";
}

/// <summary>
/// Lap Comparison (§8.6). Scaffold placeholder — wire to /compare/laps (§6.5).
/// </summary>
public sealed partial class LapComparisonViewModel : ObservableObject
{
    private readonly IQueryApiClient _api;
    private readonly AppState _state;

    public LapComparisonViewModel(IQueryApiClient api, AppState state)
    {
        _api = api;
        _state = state;
    }

    [ObservableProperty]
    private string _status = "Lap comparison — scaffold. Wire the lap-relative overlay and sector/lap deltas here.";
}
