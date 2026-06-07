namespace RaceTelemetry.Contracts;

public sealed record SessionSummary(
    string SessionId,
    int Year,
    string EventName,
    string SessionType,
    string? CircuitName,
    string? Country,
    DateTimeOffset? SessionStartUtc,
    int DriverCount,
    int LapCount);
