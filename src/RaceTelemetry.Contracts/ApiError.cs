namespace RaceTelemetry.Contracts;

public sealed record ApiErrorResponse(ApiError Error);

public sealed record ApiError(
    string Code,
    string Message,
    IReadOnlyDictionary<string, object?>? Details = null);
