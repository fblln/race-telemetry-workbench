namespace RaceTelemetry.Contracts;

public sealed record TelemetryWorkspaceContext(
    string? SessionKey,
    IReadOnlyList<string>? SelectedDrivers,
    int? SelectedLap,
    int? SelectedCorner,
    DateTimeOffset? WindowStart,
    DateTimeOffset? WindowEnd,
    string? ActiveView);
