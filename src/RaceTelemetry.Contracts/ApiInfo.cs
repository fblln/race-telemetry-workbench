namespace RaceTelemetry.Contracts;

public sealed record ApiInfo(
    string Name,
    string Version,
    IReadOnlyList<string> Capabilities);
