namespace RaceTelemetry.Contracts;

/// <summary>
/// Unified, location-aware incident list for the Track Incidents view (§6.13,
/// §8.14). Composes track-status periods, race-control messages, and the
/// hard-braking helper view with corner attribution.
/// </summary>
public sealed record IncidentsResponse(
    string SessionId,
    IReadOnlyList<Incident> Items,
    IncidentSummary Summary);

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
