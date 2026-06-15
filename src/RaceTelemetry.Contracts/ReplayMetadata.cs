namespace RaceTelemetry.Contracts;

public sealed record ReplayMetadata(
    string SessionId,
    DateTimeOffset? StartTimeUtc,
    DateTimeOffset? EndTimeUtc,
    long DurationMs,
    IReadOnlyList<string> Drivers,
    long ReplayStartMs,
    long ReplayEndMs,
    IReadOnlyList<string> AvailableChannels,
    IReadOnlyList<string> ContextChannels,
    TrackMapMetadata? TrackMap,
    EventOverlayAvailability EventOverlays,
    WeatherSummary? WeatherSummary,
    long RecommendedChunkDurationMs,
    IReadOnlyList<double> SupportedReplaySpeeds,
    double DefaultReplaySpeed,
    double? MaterializedFrequencyHz = null,
    string? TelemetrySource = null);

public sealed record TrackMapMetadata(
    double? RotationDegrees,
    string OutlineSource,
    IReadOnlyList<CircuitMarker> Markers);

public sealed record CircuitMarker(
    string Type,
    int? Number,
    string? Letter,
    double X,
    double Y,
    double? AngleDegrees,
    double? DistanceM);

public sealed record EventOverlayAvailability(
    bool TrackStatus,
    bool RaceControlMessages,
    bool Weather);

public sealed record WeatherSummary(
    double? AirTempMinC,
    double? AirTempMaxC,
    double? TrackTempMinC,
    double? TrackTempMaxC,
    bool RainfallObserved);
