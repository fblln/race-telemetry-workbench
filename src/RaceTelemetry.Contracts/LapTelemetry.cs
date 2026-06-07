namespace RaceTelemetry.Contracts;

public sealed record LapTelemetryResponse(
    string SessionId,
    string DriverCode,
    int LapNumber,
    IReadOnlyList<string> Channels,
    IReadOnlyList<TelemetrySample> Items);

public sealed record TelemetrySample(
    DateTimeOffset SampleTimeUtc,
    long? SessionTimeMs,
    long? LapTimeMs,
    double? SpeedKmh,
    double? ThrottlePct,
    double? BrakePct,
    int? Gear,
    double? Rpm,
    int? Drs);
