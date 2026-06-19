using RaceTelemetry.Contracts;
using RaceTelemetry.Data;
using System.Text.RegularExpressions;
using Npgsql;

namespace RaceTelemetry.QueryApi;

/// <summary>
/// Validates Query API route, query-string, and request-body inputs before they reach the data store.
/// </summary>
public static partial class RaceTelemetryApi
{
    private static bool ValidateSessionAndDriver(string sessionId, string driverCode, out IResult? error)
    {
        error = null;
        if (!IsValidSessionId(sessionId))
        {
            error = ValidationError("InvalidSessionId", "Session id must contain only lowercase letters, numbers, and hyphens.", ("sessionId", sessionId));
            return false;
        }

        if (!IsValidDriverCode(driverCode))
        {
            error = ValidationError("InvalidDriver", "Driver codes must contain 2 to 4 letters.", ("driverCode", driverCode));
            return false;
        }

        return true;
    }

    private static bool ValidateSessionDriverLap(string sessionId, string driverCode, int lapNumber, out IResult? error)
    {
        if (!ValidateSessionAndDriver(sessionId, driverCode, out error))
        {
            return false;
        }

        if (lapNumber < 1)
        {
            error = ValidationError("InvalidLapNumber", "Lap numbers must be positive.", ("lapNumber", lapNumber));
            return false;
        }

        return true;
    }

    private static IReadOnlyList<string> ParseChannels(
        string? value,
        IReadOnlySet<string> allowed,
        IReadOnlyList<string> defaults,
        out IResult? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaults;
        }

        var channels = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(channel => channel.ToLowerInvariant())
            .Distinct()
            .ToArray();

        var unknown = channels.Where(channel => !allowed.Contains(channel)).ToArray();
        if (unknown.Length > 0)
        {
            error = ValidationError(
                "InvalidChannels",
                "One or more requested channels are not supported.",
                ("unknown", unknown),
                ("allowed", allowed.Order().ToArray()));
        }

        return channels;
    }

    private static IReadOnlyList<string>? ParseDrivers(string? value, out IResult? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var drivers = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(driver => driver.ToUpperInvariant())
            .Distinct()
            .ToArray();

        var invalid = drivers.Where(driver => !IsValidDriverCode(driver)).ToArray();
        if (invalid.Length > 0)
        {
            error = ValidationError("InvalidDriver", "Driver codes must contain 2 to 4 letters.", ("drivers", invalid));
        }

        return drivers;
    }

    private static bool ValidateDrivers(IReadOnlyList<string>? drivers, out IResult? error)
    {
        error = null;
        if (drivers is not { Count: > 0 })
        {
            return true;
        }

        var invalid = drivers
            .Where(driver => string.IsNullOrWhiteSpace(driver) || !IsValidDriverCode(driver))
            .ToArray();
        if (invalid.Length == 0)
        {
            return true;
        }

        error = ValidationError("InvalidDriver", "Driver codes must contain 2 to 4 letters.", ("drivers", invalid));
        return false;
    }

    private static bool ValidateAllowedValues(
        IReadOnlyList<string>? values,
        IReadOnlySet<string> allowed,
        string code,
        string message,
        out IResult? error)
    {
        error = null;
        if (values is not { Count: > 0 })
        {
            return true;
        }

        var invalid = values
            .Where(value => string.IsNullOrWhiteSpace(value) || !allowed.Contains(value))
            .ToArray();
        if (invalid.Length == 0)
        {
            return true;
        }

        error = ValidationError(code, message, ("unknown", invalid), ("allowed", allowed.Order().ToArray()));
        return false;
    }

    private static bool ValidateLapRange(LapRange? lapRange, out IResult? error)
    {
        error = null;
        if (lapRange is null)
        {
            return true;
        }

        if (lapRange.From is < 1 || lapRange.To is < 1)
        {
            error = ValidationError("InvalidLapRange", "Lap range values must be positive.", ("lapRange", lapRange));
            return false;
        }

        if (lapRange.From is not null && lapRange.To is not null && lapRange.From > lapRange.To)
        {
            error = ValidationError("InvalidLapRange", "Lap range from must be less than or equal to lap range to.", ("lapRange", lapRange));
            return false;
        }

        return true;
    }

    private static bool ValidateDistanceRange(double? startDistanceM, double? endDistanceM, out IResult? error)
    {
        error = null;
        if (startDistanceM is < 0 || endDistanceM is < 0)
        {
            error = ValidationError(
                "InvalidDistanceRange",
                "Distance range values must be greater than or equal to zero.",
                ("startDistanceM", startDistanceM),
                ("endDistanceM", endDistanceM));
            return false;
        }

        if (startDistanceM is not null && endDistanceM is not null && startDistanceM > endDistanceM)
        {
            error = ValidationError(
                "InvalidDistanceRange",
                "startDistanceM must be less than or equal to endDistanceM.",
                ("startDistanceM", startDistanceM),
                ("endDistanceM", endDistanceM));
            return false;
        }

        return true;
    }

    private static bool IsValidSessionId(string sessionId) =>
        SessionIdPattern.IsMatch(sessionId);

    private static bool IsValidDriverCode(string driverCode) =>
        DriverCodePattern.IsMatch(driverCode);

    private static bool IsValidSessionType(string sessionType) =>
        sessionType.ToUpperInvariant() is "FP1" or "FP2" or "FP3" or "Q" or "SQ" or "S" or "R";

    private static IResult ValidationError(
        string code,
        string message,
        params (string Key, object? Value)[] details) =>
        Problem(400, "Invalid request", code, message, details);

    private static IResult NotFoundError(
        string code,
        string message,
        params (string Key, object? Value)[] details) =>
        Problem(404, "Resource not found", code, message, details);

    private static IResult Problem(
        int status,
        string title,
        string code,
        string message,
        params (string Key, object? Value)[] details) =>
        Results.Json(
            CreateProblem(status, title, code, message, details),
            statusCode: status,
            contentType: "application/problem+json");

    private static ApiProblem CreateProblem(
        int status,
        string title,
        string code,
        string detail,
        params (string Key, object? Value)[] errors) =>
        new(
            $"https://fblln.github.io/race-telemetry-workbench/problems#{ToProblemTypeSlug(code)}",
            title,
            status,
            detail,
            null,
            code,
            errors.Length == 0
                ? null
                : errors.ToDictionary(error => error.Key, error => error.Value));

    private static string ToProblemTypeSlug(string code) =>
        Regex.Replace(
                code,
                "([a-z0-9])([A-Z])",
                "$1-$2")
            .ToLowerInvariant();
}
