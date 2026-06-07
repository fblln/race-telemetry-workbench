namespace RaceTelemetry.Contracts;

public sealed record LapSummary(
    string LapId,
    string SessionId,
    string DriverCode,
    int LapNumber,
    long? LapTimeMs,
    int? Position,
    bool IsPitOutLap,
    bool IsPitInLap);
