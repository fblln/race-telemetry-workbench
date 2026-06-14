namespace RaceTelemetry.Contracts;

/// <summary>
/// Field snapshot for the timing tower (§6.11, §8.13). One row per driver,
/// classified as of <see cref="AtLap"/>.
/// </summary>
public sealed record StandingsResponse(
    string SessionId,
    int AtLap,
    IReadOnlyList<StandingRow> Items);

public sealed record StandingRow(
    int Position,
    string DriverCode,
    string? FullName,
    string? TeamName,
    long GapToLeaderMs,
    long IntervalMs,
    long? LastLapMs,
    long? BestLapMs,
    bool IsSessionBestLap,
    bool IsPersonalBestLap,
    string? Compound,
    int? TyreLife,
    int PitCount,
    string Status,
    IReadOnlyList<long>? RecentLapMs);
