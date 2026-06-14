using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RaceTelemetry.Contracts;
using RaceTelemetry.Desktop.Controls;
using RaceTelemetry.Desktop.Services;

namespace RaceTelemetry.Desktop.ViewModels;

/// <summary>
/// Overview (§8.3): result summary cards plus a full classification table.
/// Uses the current Query API surface and leaves unavailable race-control fields
/// explicit rather than inventing grid/pole data.
/// </summary>
public sealed partial class OverviewViewModel : ObservableObject
{
    private static readonly int[] RacePoints = [25, 18, 15, 12, 10, 8, 6, 4, 2, 1];

    private readonly ISessionPrefetchService _prefetch;
    private readonly AppState _state;

    public OverviewViewModel(ISessionPrefetchService prefetch, AppState state)
    {
        _prefetch = prefetch;
        _state = state;
    }

    public ObservableCollection<ClassificationRow> Classification { get; } = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private OverviewHeroMetric _winner = OverviewHeroMetric.Empty("Winner");

    [ObservableProperty]
    private OverviewHeroMetric _polePosition = new("Pole Position", "Not imported", "Qualifying/grid data unavailable", "--", DriverPalette.HexFor(""));

    [ObservableProperty]
    private OverviewHeroMetric _fastestLap = OverviewHeroMetric.Empty("Fastest Lap");

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_state.SessionId is null || IsLoading) return;

        IsLoading = true;
        Error = null;
        try
        {
            var snapshot = await _prefetch.GetAsync(_state.SessionId);
            var standings = snapshot.Standings;
            if (standings is null || standings.Items.Count == 0)
            {
                Error = "Classification unavailable for this session.";
                return;
            }

            var rows = standings.Items.OrderBy(row => row.Position).ToArray();
            var driverTeams = snapshot.Drivers.ToDictionary(d => d.DriverCode, d => d.TeamName, StringComparer.OrdinalIgnoreCase);
            var winnerRow = rows.First();
            var fastestRow = rows
                .Where(row => row.BestLapMs is not null)
                .OrderBy(row => row.BestLapMs)
                .FirstOrDefault();

            Winner = new OverviewHeroMetric(
                "Winner",
                DisplayName(winnerRow),
                snapshot.ReplayMetadata is { DurationMs: > 0 } meta ? FormatDuration(meta.DurationMs) : "Gap leader",
                winnerRow.DriverCode,
                DriverPalette.HexFor(winnerRow.DriverCode));

            PolePosition = new OverviewHeroMetric(
                "Pole Position",
                "Not imported",
                "Qualifying/grid data unavailable",
                "--",
                "#5C544A");

            FastestLap = fastestRow is null
                ? OverviewHeroMetric.Empty("Fastest Lap")
                : new OverviewHeroMetric(
                    "Fastest Lap",
                    DisplayName(fastestRow),
                    FormatLapTime(fastestRow.BestLapMs),
                    fastestRow.DriverCode,
                    DriverPalette.HexFor(fastestRow.DriverCode));

            Classification.Clear();
            foreach (var row in rows)
            {
                var team = row.TeamName;
                if (string.IsNullOrWhiteSpace(team) && driverTeams.TryGetValue(row.DriverCode, out var driverTeam))
                    team = driverTeam;

                Classification.Add(new ClassificationRow(
                    row.Position,
                    row.DriverCode,
                    DisplayName(row),
                    team ?? "--",
                    "--",
                    FormatGap(row, snapshot.ReplayMetadata),
                    FormatLapTime(row.BestLapMs),
                    row.IsSessionBestLap,
                    Math.Max(1, row.PitCount + 1),
                    standings.AtLap,
                    PointsFor(row.Position),
                    row.Status,
                    DriverPalette.HexFor(row.DriverCode)));
            }
        }
        catch (Exception ex)
        {
            Error = $"Overview unavailable: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string DisplayName(StandingRow row)
        => string.IsNullOrWhiteSpace(row.FullName) ? row.DriverCode : row.FullName;

    private static int? PointsFor(int position)
        => position > 0 && position <= RacePoints.Length ? RacePoints[position - 1] : null;

    private static string FormatGap(StandingRow row, ReplayMetadata? metadata)
    {
        if (row.Position == 1)
            return metadata is { DurationMs: > 0 } ? FormatDuration(metadata.DurationMs) : "--";
        return row.GapToLeaderMs > 0 ? $"+{row.GapToLeaderMs / 1000.0:0.000}" : "--";
    }

    private static string FormatDuration(long ms)
        => TimeSpan.FromMilliseconds(ms).ToString(@"h\:mm\:ss\.fff");

    private static string FormatLapTime(long? ms)
    {
        if (ms is null or <= 0) return "--";
        var time = TimeSpan.FromMilliseconds(ms.Value);
        return time.ToString(time.Hours > 0 ? @"h\:mm\:ss\.fff" : @"m\:ss\.fff");
    }
}

public sealed record OverviewHeroMetric(
    string Label,
    string DriverName,
    string Value,
    string DriverCode,
    string RailColor)
{
    public static OverviewHeroMetric Empty(string label) => new(label, "--", "--", "--", "#5C544A");
}

public sealed record ClassificationRow(
    int Position,
    string DriverCode,
    string DriverName,
    string TeamName,
    string Grid,
    string TimeOrGap,
    string FastestLap,
    bool IsFastestLap,
    int Stints,
    int Laps,
    int? Points,
    string Status,
    string RailColor)
{
    public string PointsText => Points?.ToString() ?? "--";
    public string StatusText => string.Equals(Status, "running", StringComparison.OrdinalIgnoreCase) ? string.Empty : Status;
}
