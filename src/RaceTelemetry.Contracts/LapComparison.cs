namespace RaceTelemetry.Contracts;

public sealed record LapComparisonResponse(
    string SessionId,
    string DriverA,
    int LapA,
    string DriverB,
    int LapB,
    int TimeStepMs,
    IReadOnlyList<string> Channels,
    IReadOnlyList<LapComparisonPoint> Items,
    LapComparisonSummary Summary);

public sealed record LapComparisonPoint(
    long LapTimeMs,
    TelemetryChannelValues A,
    TelemetryChannelValues B,
    TelemetryChannelValues Delta);

public sealed record TelemetryChannelValues(
    double? SpeedKmh,
    double? ThrottlePct,
    double? BrakePct,
    double? Rpm,
    int? Gear);

public sealed record LapComparisonSummary(
    long? LapTimeDeltaMs,
    IReadOnlyList<long?> SectorDeltasMs,
    double? MaxSpeedDeltaKmh,
    double? AvgSpeedDeltaKmh);
