using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using RaceTelemetry.Contracts;
using RaceTelemetry.Data;


namespace RaceTelemetry.McpServer;

/// <summary>
/// Parses and validates MCP tool parameters while preserving the race-session default behavior.
/// </summary>
public sealed partial class RaceTelemetryMcpTools
{
    private static void ValidateSessionAndDriver(string sessionId, string driverCode)
    {
        ValidateSessionId(sessionId);
        ValidateDriverCode(driverCode);
    }

    private static void ValidateSessionDriverLap(string sessionId, string driverCode, int lapNumber)
    {
        ValidateSessionAndDriver(sessionId, driverCode);
        ValidateLapNumber(lapNumber);
    }

    private static void ValidateYear(int? year)
    {
        if (year is not null and < 1950)
        {
            throw new ArgumentOutOfRangeException(nameof(year), "Year must be 1950 or later.");
        }
    }

    private static void ValidateSessionType(string? sessionType)
    {
        if (!string.IsNullOrWhiteSpace(sessionType) && !SessionTypes.Contains(sessionType))
        {
            throw new ArgumentException("Session type must be one of FP1, FP2, FP3, Q, SQ, S, or R.", nameof(sessionType));
        }
    }

    private static string? NormalizeSessionType(string? sessionType) =>
        string.IsNullOrWhiteSpace(sessionType) ? null : sessionType.ToUpperInvariant();

    private static void ValidateSessionId(string sessionId)
    {
        if (!SessionIdPattern().IsMatch(sessionId))
        {
            throw new ArgumentException("Session id must contain only lowercase letters, numbers, and hyphens.", nameof(sessionId));
        }
    }

    private static void ValidateDriverCode(string driverCode)
    {
        if (!DriverCodePattern().IsMatch(driverCode))
        {
            throw new ArgumentException("Driver codes must contain 2 to 4 letters.", nameof(driverCode));
        }
    }

    private static void ValidateLapNumber(int lapNumber)
    {
        if (lapNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(lapNumber), "Lap numbers must be positive.");
        }
    }

    private static void ValidateLapRange(int? lapFrom, int? lapTo)
    {
        if (lapFrom is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(lapFrom), "lapFrom must be positive.");
        }

        if (lapTo is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(lapTo), "lapTo must be positive.");
        }

        if (lapFrom is not null && lapTo is not null && lapFrom > lapTo)
        {
            throw new ArgumentException("lapFrom must be less than or equal to lapTo.");
        }
    }

    private static void ValidateTelemetryEventType(string eventType)
    {
        if (string.IsNullOrWhiteSpace(eventType) || !TelemetryEventTypes.Contains(eventType))
        {
            throw new ArgumentException(
                $"Unsupported event type. Allowed event types: {string.Join(", ", TelemetryEventTypes.Order())}.",
                nameof(eventType));
        }
    }

    private static void ValidateRange(long value, long min, long max, string parameterName)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"{parameterName} must be between {min} and {max}.");
        }
    }

    private static IReadOnlyList<string>? ParseDrivers(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var drivers = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(driver => driver.ToUpperInvariant())
            .Distinct()
            .ToArray();

        foreach (var driver in drivers)
        {
            ValidateDriverCode(driver);
        }

        return drivers;
    }

    private static IReadOnlyList<string>? ParseEventTypes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var eventTypes = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(eventType => eventType.ToLowerInvariant())
            .Distinct()
            .ToArray();

        var unknown = eventTypes.Where(eventType => !TelemetryEventTypes.Contains(eventType)).ToArray();
        if (unknown.Length > 0)
        {
            throw new ArgumentException(
                $"Unknown telemetry event type(s): {string.Join(", ", unknown)}.",
                nameof(value));
        }

        return eventTypes;
    }

    private static IReadOnlyList<string>? ParseUpperList(string? value) =>
        ParseList(value, item => item.ToUpperInvariant());

    private static IReadOnlyList<string>? ParseLowerList(string? value) =>
        ParseList(value, item => item.ToLowerInvariant());

    private static IReadOnlyList<string>? ParseRawList(string? value) =>
        ParseList(value, item => item);

    private static IReadOnlyList<string>? ParseList(string? value, Func<string, string> normalize)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(normalize)
            .Distinct()
            .ToArray();
    }

    private static IReadOnlyList<int>? ParseIntegerList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var values = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item =>
            {
                if (!int.TryParse(item, CultureInfo.InvariantCulture, out var parsed))
                {
                    throw new ArgumentException($"Invalid integer value: {item}.", nameof(value));
                }

                ValidateRange(parsed, 1, 999, nameof(value));
                return parsed;
            })
            .Distinct()
            .ToArray();

        return values.Length == 0 ? null : values;
    }

    private static IReadOnlyList<string> ParseAllowedList(
        string? value,
        IReadOnlySet<string> allowed,
        IReadOnlyList<string> defaults,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaults;
        }

        var items = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.ToLowerInvariant())
            .Distinct()
            .ToArray();
        if (items.Length == 0)
        {
            return defaults;
        }

        var unknown = items.Where(item => !allowed.Contains(item)).ToArray();
        if (unknown.Length > 0)
        {
            throw new ArgumentException(
                $"Unsupported value(s): {string.Join(", ", unknown)}. Allowed values: {string.Join(", ", allowed.Order())}.",
                parameterName);
        }

        return items;
    }

    private static IReadOnlyList<string> ParseChannels(
        string? value,
        IReadOnlySet<string> allowed,
        IReadOnlyList<string> defaults)
    {
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
            throw new ArgumentException(
                $"Unsupported channel(s): {string.Join(", ", unknown)}. Allowed channels: {string.Join(", ", allowed.Order())}.",
                nameof(value));
        }

        return channels;
    }

    private static KeyNotFoundException NotFound(string message) => new(message);

    private static Activity? StartToolActivity(
        string toolName,
        string? sessionId = null,
        string? driverCode = null,
        int? lapNumber = null)
    {
        var activity = ActivitySource.StartActivity($"mcp.tool.{toolName}", ActivityKind.Server);
        activity?.SetTag("component", "RaceTelemetry.McpServer");
        activity?.SetTag("mcp.tool.name", toolName);
        activity?.SetTag("race.session_id", sessionId);
        activity?.SetTag("race.driver_code", driverCode?.ToUpperInvariant());
        activity?.SetTag("race.lap_number", lapNumber);
        return activity;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex SessionIdPattern();

    [GeneratedRegex("^[A-Za-z]{2,4}$")]
    private static partial Regex DriverCodePattern();
}
