namespace RaceTelemetry.Contracts;

public sealed record ReplayChunkResponse(
    string SessionId,
    long FromMs,
    long DurationMs,
    long NextFromMs,
    IReadOnlyList<string> Channels,
    IReadOnlyList<ReplayDriverChunk> Items,
    double? FrequencyHz = null,
    string? TelemetrySource = null);

public sealed record ReplayDriverChunk(
    string DriverCode,
    IReadOnlyList<ReplaySample> Samples);

public sealed record ReplaySample(
    long? OffsetMs,
    int? LapNumber,
    double? SpeedKmh,
    double? ThrottlePct,
    double? BrakePct,
    int? Gear,
    double? Rpm,
    int? Drs,
    double? X,
    double? Y,
    double? Z,
    int? SampleIndex = null,
    DateTimeOffset? DateUtc = null,
    long? LapTimeMs = null,
    string? LocationStatus = null,
    IReadOnlyList<string>? QualityFlags = null,
    int? CarSampleAgeMs = null,
    int? LocationSampleAgeMs = null,
    bool? IsInterpolatedCar = null,
    bool? IsInterpolatedLocation = null);
