namespace RaceTelemetry.Contracts;

public sealed record DriverSummary(
    string SessionId,
    string DriverCode,
    string? DriverNumber,
    string? FullName,
    string? TeamName,
    int LapCount);
