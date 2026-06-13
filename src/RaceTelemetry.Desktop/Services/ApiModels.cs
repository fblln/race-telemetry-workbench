namespace RaceTelemetry.Desktop.Services;

// Desktop-local DTOs for the newer Query API endpoints (§6.11–6.16) that are not
// yet in RaceTelemetry.Contracts. When the backend ships these, move the records
// into Contracts and delete the duplicates here.

public sealed record StandingsResponse(int AtLap, IReadOnlyList<StandingRow> Items);

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

public sealed record IncidentsResponse(IReadOnlyList<Incident> Items, IncidentSummary Summary);

public sealed record Incident(
    string Type,
    int? LapNumber,
    long? SessionTimeMs,
    string Message,
    NearestCorner? NearestCorner,
    double? X,
    double? Y,
    string? DriverCode,
    string Severity,
    IncidentMetrics? Metrics);

public sealed record NearestCorner(int Number, string Label);

public sealed record IncidentMetrics(double? PeakBrakingG, double? EntrySpeedKmh, double? MinSpeedKmh);

public sealed record IncidentSummary(int IncidentCount, double? HardestBrakingG, int LapsUnderSafetyCar);

public sealed record PositionsResponse(int FromLap, int ToLap, IReadOnlyList<DriverPositions> Items);

public sealed record DriverPositions(string DriverCode, IReadOnlyList<int?> Positions);
