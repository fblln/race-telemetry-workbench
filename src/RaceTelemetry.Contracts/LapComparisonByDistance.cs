namespace RaceTelemetry.Contracts;

public sealed record LapComparisonByDistanceResponse(
    string SessionId,
    string DriverA,
    int LapA,
    string DriverB,
    int LapB,
    double DistanceStepMeters,
    string DeltaSignConvention,
    IReadOnlyList<LapComparisonByDistancePoint> Items,
    LapComparisonByDistanceSummary Summary);

public sealed record LapComparisonByDistancePoint(
    double DistanceMeters,
    double NormalizedTrackProgress,
    long? AElapsedMs,
    long? BElapsedMs,
    long? DeltaMs,
    DistanceTelemetryChannelValues A,
    DistanceTelemetryChannelValues B,
    DistanceTelemetryChannelValues Delta);

public sealed record DistanceTelemetryChannelValues(
    double? SpeedKmh,
    double? ThrottlePct,
    double? BrakePct,
    double? Rpm,
    int? Gear,
    int? Drs);

public sealed record LapComparisonByDistanceSummary(
    long? OfficialLapTimeDeltaMs,
    long? FinishDeltaMs,
    long? FinishDeltaValidationMs,
    string? AQualityStatus,
    string? BQualityStatus);
