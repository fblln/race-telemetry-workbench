namespace RaceTelemetry.Contracts;

public sealed record ApiProblem(
    string Type,
    string Title,
    int Status,
    string Detail,
    string? Instance,
    string Code,
    IReadOnlyDictionary<string, object?>? Errors = null);
