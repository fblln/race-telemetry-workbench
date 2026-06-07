namespace RaceTelemetry.Contracts;

public sealed record SessionsResponse(IReadOnlyList<SessionSummary> Items);

public sealed record DriversResponse(
    string SessionId,
    IReadOnlyList<DriverSummary> Items);

public sealed record LapsResponse(
    string SessionId,
    string DriverCode,
    IReadOnlyList<LapSummary> Items);
