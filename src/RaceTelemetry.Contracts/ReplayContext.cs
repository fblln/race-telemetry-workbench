namespace RaceTelemetry.Contracts;

public sealed record ReplayContextResponse(
    string SessionId,
    long FromMs,
    long DurationMs,
    IReadOnlyList<WeatherSample> WeatherSamples,
    IReadOnlyList<TrackStatusEvent> TrackStatusEvents,
    IReadOnlyList<RaceControlMessage> RaceControlMessages);

public sealed record WeatherSample(
    long SampleTimeMs,
    double? AirTempC,
    double? TrackTempC,
    double? HumidityPct,
    double? PressureMbar,
    bool? Rainfall,
    int? WindDirectionDeg,
    double? WindSpeedMps);

public sealed record TrackStatusEvent(
    long EventTimeMs,
    string StatusCode,
    string? Message);

public sealed record RaceControlMessage(
    long? SessionTimeMs,
    int? LapNumber,
    string? Category,
    string Message,
    string? Status,
    string? Flag,
    string? Scope,
    string? Sector,
    int? RacingNumber);
