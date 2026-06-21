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

/// <summary>
/// Net track-position change per driver across the race, sorted biggest gainer first.
/// Start position is the starting grid where imported, otherwise the order after lap 1.
/// Delta is positive when the driver gained places (StartPosition - FinishPosition).
/// </summary>
public sealed record PositionChangesResponse(
    string SessionId,
    IReadOnlyList<PositionChange> Items);

public sealed record PositionChange(
    string DriverCode,
    string? FullName,
    int StartPosition,
    int FinishPosition,
    int Delta);
