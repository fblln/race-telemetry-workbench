namespace RaceTelemetry.Contracts;

/// <summary>
/// Unified, location-aware race-control list for the Race Control view (§6.13,
/// §8.14). Composes track-status periods and race-control messages.
/// </summary>
public sealed record RaceControlResponse(
    string SessionId,
    IReadOnlyList<RaceControlItem> Items,
    RaceControlListSummary Summary);

public sealed record RaceControlItem(
    string Type,
    int? LapNumber,
    long? SessionTimeMs,
    string Message,
    NearestCorner? NearestCorner,
    double? X,
    double? Y,
    string? DriverCode,
    string Severity,
    string? ClusterTerms = null);

public sealed record NearestCorner(int Number, string Label);

public sealed record RaceControlListSummary(int IncidentCount, int LapsUnderSafetyCar);
