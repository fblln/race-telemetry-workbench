namespace RaceTelemetry.Contracts;

public sealed record TelemetryEventSearchRequest(
    IReadOnlyList<string>? EventTypes,
    IReadOnlyList<string>? Drivers,
    long? FromMs,
    long? DurationMs,
    int? Limit);

public sealed record TelemetryEventSearchResponse(
    string SessionId,
    IReadOnlyList<TelemetryEventCandidate> Items);

public sealed record TelemetryEventCandidate(
    DateTimeOffset SampleTimeUtc,
    string DriverCode,
    int? LapNumber,
    long? SessionTimeMs,
    long? LapTimeMs,
    double? SpeedKmh,
    double? ThrottlePct,
    double? BrakePct,
    int? Drs,
    string EventType);
