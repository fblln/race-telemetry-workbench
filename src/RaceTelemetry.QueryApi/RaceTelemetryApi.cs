using RaceTelemetry.Contracts;
using RaceTelemetry.Data;
using System.Text.RegularExpressions;
using Npgsql;

namespace RaceTelemetry.QueryApi;

public static class RaceTelemetryApi
{
    private static readonly Regex SessionIdPattern = new("^[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled);
    private static readonly Regex DriverCodePattern = new("^[A-Za-z]{2,4}$", RegexOptions.Compiled);

    private static readonly HashSet<string> TelemetryChannels = new(StringComparer.OrdinalIgnoreCase)
    {
        "speed_kmh",
        "throttle_pct",
        "brake_pct",
        "gear",
        "rpm",
        "drs",
        "session_time_ms",
        "lap_time_ms"
    };

    private static readonly HashSet<string> ReplayChannels = new(TelemetryChannels, StringComparer.OrdinalIgnoreCase)
    {
        "x",
        "y",
        "z"
    };

    private static readonly HashSet<string> TelemetryEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "hard_braking",
        "high_speed",
        "drs_active",
        "throttle_lift"
    };

    public static WebApplication CreateApp(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.AddServiceDefaults();
        builder.Services.AddProblemDetails();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddOpenApi();
        builder.Services.AddTelemetryQueryStore(builder.Configuration);

        var app = builder.Build();

        app.MapDefaultEndpoints();
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.MapTelemetryEndpoints();

        return app;
    }

    private static IServiceCollection AddTelemetryQueryStore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var databaseUrl = configuration.GetConnectionString("RaceTelemetry")
            ?? configuration["RACE_TELEMETRY_DATABASE_URL"]
            ?? Environment.GetEnvironmentVariable("RACE_TELEMETRY_DATABASE_URL");

        if (string.IsNullOrWhiteSpace(databaseUrl))
        {
            services.AddSingleton<IF1TelemetryQueryStore, InMemoryTelemetryQueryStore>();
            return services;
        }

        services.AddSingleton(_ =>
        {
            var connectionString = PostgresConnectionString.Normalize(databaseUrl);
            return new NpgsqlDataSourceBuilder(connectionString).Build();
        });
        services.AddHostedService<PostgresConnectionWarmupService>();
        services.AddSingleton<IF1TelemetryQueryStore, PostgresTelemetryQueryStore>();
        return services;
    }

    private static RouteGroupBuilder MapTelemetryEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api")
            .WithTags("Race Telemetry");

        api.MapGet("/", () => new ApiInfo(
                Name: "Race Telemetry Query API",
                Version: "0.1.0",
                Capabilities:
                [
                    "sessions",
                    "drivers",
                    "laps",
                    "replay-metadata",
                    "lap-telemetry",
                    "lap-story",
                    "lap-braking-zones",
                    "lap-comparison",
                    "lap-comparison-story",
                    "race-story",
                    "replay-chunks",
                    "replay-context",
                    "telemetry-event-search",
                    "mcp-ready-query-contracts"
                ]))
            .WithName("GetApiInfo");

        api.MapGet("/sessions", async (
                int? year,
                string? @event,
                string? sessionType,
                IF1TelemetryQueryStore store,
                CancellationToken cancellationToken) =>
            {
                if (year is not null and < 1950)
                {
                    return ValidationError("InvalidYear", "Year must be 1950 or later.", ("year", year));
                }

                if (!string.IsNullOrWhiteSpace(sessionType) && !IsValidSessionType(sessionType))
                {
                    return ValidationError("InvalidSessionType", "Session type must be one of FP1, FP2, FP3, Q, SQ, S, or R.", ("sessionType", sessionType));
                }

                var sessions = await store.GetSessionsAsync(year, @event, sessionType, cancellationToken);
                return Results.Ok(new SessionsResponse(sessions));
            })
            .WithName("GetSessions");

        api.MapGet("/sessions/{sessionId}/drivers", async (
                string sessionId,
                IF1TelemetryQueryStore store,
                CancellationToken cancellationToken) =>
            {
                if (!IsValidSessionId(sessionId))
                {
                    return ValidationError("InvalidSessionId", "Session id must contain only lowercase letters, numbers, and hyphens.", ("sessionId", sessionId));
                }

                var drivers = await store.GetDriversAsync(sessionId, cancellationToken);
                return drivers is null
                    ? NotFoundError("SessionNotFound", $"Session {sessionId} does not exist.", ("sessionId", sessionId))
                    : Results.Ok(new DriversResponse(sessionId, drivers));
            })
            .WithName("GetSessionDrivers");

        api.MapGet("/sessions/{sessionId}/drivers/{driverCode}/laps", async (
                string sessionId,
                string driverCode,
                IF1TelemetryQueryStore store,
                CancellationToken cancellationToken) =>
            {
                if (!ValidateSessionAndDriver(sessionId, driverCode, out var error))
                {
                    return error;
                }

                var laps = await store.GetLapsAsync(sessionId, driverCode, cancellationToken);
                return laps is null
                    ? NotFoundError("DriverNotFound", $"Driver {driverCode.ToUpperInvariant()} does not exist in session {sessionId}.", ("sessionId", sessionId), ("driverCode", driverCode.ToUpperInvariant()))
                    : Results.Ok(new LapsResponse(sessionId, driverCode.ToUpperInvariant(), laps));
            })
            .WithName("GetDriverLaps");

        api.MapGet("/sessions/{sessionId}/drivers/{driverCode}/laps/{lapNumber:int}/telemetry", async (
                string sessionId,
                string driverCode,
                int lapNumber,
                string? channels,
                int? sampleEvery,
                int? maxSamples,
                IF1TelemetryQueryStore store,
                CancellationToken cancellationToken) =>
            {
                if (!ValidateSessionDriverLap(sessionId, driverCode, lapNumber, out var error))
                {
                    return error;
                }

                var selectedChannels = ParseChannels(channels, TelemetryChannels, ["speed_kmh", "throttle_pct", "brake_pct", "rpm", "gear"], out error);
                if (error is not null)
                {
                    return error;
                }

                var sampleEveryValue = sampleEvery ?? 1;
                if (sampleEveryValue is < 1 or > 100)
                {
                    return ValidationError("InvalidSampleEvery", "sampleEvery must be between 1 and 100.", ("sampleEvery", sampleEveryValue));
                }

                var maxSamplesValue = maxSamples ?? 5_000;
                if (maxSamplesValue is < 1 or > 50_000)
                {
                    return ValidationError("InvalidMaxSamples", "maxSamples must be between 1 and 50000.", ("maxSamples", maxSamplesValue));
                }

                var telemetry = await store.GetLapTelemetryAsync(
                    sessionId,
                    driverCode,
                    lapNumber,
                    selectedChannels,
                    sampleEveryValue,
                    maxSamplesValue,
                    cancellationToken);

                return telemetry is null
                    ? NotFoundError("LapNotFound", $"Lap {lapNumber} for driver {driverCode.ToUpperInvariant()} does not exist in session {sessionId}.", ("sessionId", sessionId), ("driverCode", driverCode.ToUpperInvariant()), ("lapNumber", lapNumber))
                    : Results.Ok(telemetry);
            })
            .WithName("GetLapTelemetry");

        api.MapGet("/sessions/{sessionId}/drivers/{driverCode}/laps/{lapNumber:int}/story", async Task<IResult> (
                string sessionId,
                string driverCode,
                int lapNumber,
                IF1TelemetryQueryStore store,
                CancellationToken cancellationToken) =>
            {
                if (!ValidateSessionDriverLap(sessionId, driverCode, lapNumber, out var error))
                {
                    return error;
                }

                var story = await store.GetLapStoryAsync(sessionId, driverCode, lapNumber, cancellationToken);
                return story is null
                    ? NotFoundError("LapNotFound", $"Lap {lapNumber} for driver {driverCode.ToUpperInvariant()} does not exist in session {sessionId}.", ("sessionId", sessionId), ("driverCode", driverCode.ToUpperInvariant()), ("lapNumber", lapNumber))
                    : Results.Ok(story);
            })
            .WithName("GetLapStory");

        api.MapGet("/sessions/{sessionId}/drivers/{driverCode}/laps/{lapNumber:int}/braking-zones", async Task<IResult> (
                string sessionId,
                string driverCode,
                int lapNumber,
                int? brakeThresholdPct,
                int? minimumDurationMs,
                IF1TelemetryQueryStore store,
                CancellationToken cancellationToken) =>
            {
                if (!ValidateSessionDriverLap(sessionId, driverCode, lapNumber, out var error))
                {
                    return error;
                }

                var threshold = brakeThresholdPct ?? 80;
                if (threshold is < 1 or > 100)
                {
                    return ValidationError("InvalidBrakeThreshold", "brakeThresholdPct must be between 1 and 100.", ("brakeThresholdPct", threshold));
                }

                var duration = minimumDurationMs ?? 250;
                if (duration is < 0 or > 10_000)
                {
                    return ValidationError("InvalidMinimumDuration", "minimumDurationMs must be between 0 and 10000.", ("minimumDurationMs", duration));
                }

                var zones = await store.GetLapBrakingZonesAsync(sessionId, driverCode, lapNumber, threshold, duration, cancellationToken);
                return zones is null
                    ? NotFoundError("LapNotFound", $"Lap {lapNumber} for driver {driverCode.ToUpperInvariant()} does not exist in session {sessionId}.", ("sessionId", sessionId), ("driverCode", driverCode.ToUpperInvariant()), ("lapNumber", lapNumber))
                    : Results.Ok(zones);
            })
            .WithName("GetLapBrakingZones");

        api.MapGet("/sessions/{sessionId}/compare/laps", async (
                string sessionId,
                string driverA,
                int lapA,
                string driverB,
                int lapB,
                string? channels,
                int? timeStepMs,
                IF1TelemetryQueryStore store,
                CancellationToken cancellationToken) =>
            {
                if (!IsValidSessionId(sessionId))
                {
                    return ValidationError("InvalidSessionId", "Session id must contain only lowercase letters, numbers, and hyphens.", ("sessionId", sessionId));
                }

                if (!IsValidDriverCode(driverA) || !IsValidDriverCode(driverB))
                {
                    return ValidationError("InvalidDriver", "Driver codes must contain 2 to 4 letters.", ("driverA", driverA), ("driverB", driverB));
                }

                if (lapA < 1 || lapB < 1)
                {
                    return ValidationError("InvalidLapNumber", "Lap numbers must be positive.", ("lapA", lapA), ("lapB", lapB));
                }

                var selectedChannels = ParseChannels(channels, TelemetryChannels, ["speed_kmh", "throttle_pct", "brake_pct"], out var error);
                if (error is not null)
                {
                    return error;
                }

                var step = timeStepMs ?? 100;
                if (step is < 20 or > 5_000)
                {
                    return ValidationError("InvalidTimeStep", "timeStepMs must be between 20 and 5000.", ("timeStepMs", step));
                }

                var comparison = await store.CompareLapsAsync(
                    sessionId,
                    driverA,
                    lapA,
                    driverB,
                    lapB,
                    selectedChannels,
                    step,
                    cancellationToken);

                return comparison is null
                    ? NotFoundError("LapComparisonNotFound", "One or both requested laps do not exist.", ("sessionId", sessionId), ("driverA", driverA.ToUpperInvariant()), ("lapA", lapA), ("driverB", driverB.ToUpperInvariant()), ("lapB", lapB))
                    : Results.Ok(comparison);
            })
            .WithName("CompareLaps");

        api.MapGet("/sessions/{sessionId}/compare/laps/story", async Task<IResult> (
                string sessionId,
                string driverA,
                int lapA,
                string driverB,
                int lapB,
                int? segmentCount,
                IF1TelemetryQueryStore store,
                CancellationToken cancellationToken) =>
            {
                if (!IsValidSessionId(sessionId))
                {
                    return ValidationError("InvalidSessionId", "Session id must contain only lowercase letters, numbers, and hyphens.", ("sessionId", sessionId));
                }

                if (!IsValidDriverCode(driverA) || !IsValidDriverCode(driverB))
                {
                    return ValidationError("InvalidDriver", "Driver codes must contain 2 to 4 letters.", ("driverA", driverA), ("driverB", driverB));
                }

                if (lapA < 1 || lapB < 1)
                {
                    return ValidationError("InvalidLapNumber", "Lap numbers must be positive.", ("lapA", lapA), ("lapB", lapB));
                }

                var segments = segmentCount ?? 3;
                if (segments is < 2 or > 12)
                {
                    return ValidationError("InvalidSegmentCount", "segmentCount must be between 2 and 12.", ("segmentCount", segments));
                }

                var story = await store.CompareLapsStoryAsync(sessionId, driverA, lapA, driverB, lapB, segments, cancellationToken);
                return story is null
                    ? NotFoundError("LapComparisonNotFound", "One or both requested laps do not exist.", ("sessionId", sessionId), ("driverA", driverA.ToUpperInvariant()), ("lapA", lapA), ("driverB", driverB.ToUpperInvariant()), ("lapB", lapB))
                    : Results.Ok(story);
            })
            .WithName("CompareLapsStory");

        api.MapGet("/sessions/{sessionId}/story", async Task<IResult> (
                string sessionId,
                int? raceControlLimit,
                IF1TelemetryQueryStore store,
                CancellationToken cancellationToken) =>
            {
                if (!IsValidSessionId(sessionId))
                {
                    return ValidationError("InvalidSessionId", "Session id must contain only lowercase letters, numbers, and hyphens.", ("sessionId", sessionId));
                }

                var limit = raceControlLimit ?? 100;
                if (limit is < 0 or > 1_000)
                {
                    return ValidationError("InvalidRaceControlLimit", "raceControlLimit must be between 0 and 1000.", ("raceControlLimit", limit));
                }

                var story = await store.GetRaceStoryAsync(sessionId, limit, cancellationToken);
                return story is null
                    ? NotFoundError("SessionNotFound", $"Session {sessionId} does not exist.", ("sessionId", sessionId))
                    : Results.Ok(story);
            })
            .WithName("GetRaceStory");

        api.MapGet("/sessions/{sessionId}/replay/metadata", async (
                string sessionId,
                IF1TelemetryQueryStore store,
                CancellationToken cancellationToken) =>
            {
                if (!IsValidSessionId(sessionId))
                {
                    return ValidationError("InvalidSessionId", "Session id must contain only lowercase letters, numbers, and hyphens.", ("sessionId", sessionId));
                }

                var metadata = await store.GetReplayMetadataAsync(sessionId, cancellationToken);
                return metadata is null
                    ? NotFoundError("SessionNotFound", $"Session {sessionId} does not exist.", ("sessionId", sessionId))
                    : Results.Ok(metadata);
            })
            .WithName("GetReplayMetadata");

        api.MapGet("/sessions/{sessionId}/replay/chunk", async (
                string sessionId,
                long? fromMs,
                long? durationMs,
                string? drivers,
                string? channels,
                int? sampleEvery,
                IF1TelemetryQueryStore store,
                CancellationToken cancellationToken) =>
            {
                if (!IsValidSessionId(sessionId))
                {
                    return ValidationError("InvalidSessionId", "Session id must contain only lowercase letters, numbers, and hyphens.", ("sessionId", sessionId));
                }

                if (fromMs is null or < 0)
                {
                    return ValidationError("InvalidTimeRange", "fromMs is required and must be greater than or equal to 0.", ("fromMs", fromMs));
                }

                if (durationMs is null or < 1_000 or > 120_000)
                {
                    return ValidationError("InvalidTimeRange", "durationMs is required and must be between 1000 and 120000.", ("durationMs", durationMs));
                }

                var selectedDrivers = ParseDrivers(drivers, out var error);
                if (error is not null)
                {
                    return error;
                }

                var selectedChannels = ParseChannels(channels, ReplayChannels, ReplayChannels.ToArray(), out error);
                if (error is not null)
                {
                    return error;
                }

                var sampleEveryValue = sampleEvery ?? 1;
                if (sampleEveryValue is < 1 or > 100)
                {
                    return ValidationError("InvalidSampleEvery", "sampleEvery must be between 1 and 100.", ("sampleEvery", sampleEveryValue));
                }

                var chunk = await store.GetReplayChunkAsync(
                    sessionId,
                    fromMs.Value,
                    durationMs.Value,
                    selectedDrivers,
                    selectedChannels,
                    sampleEveryValue,
                    cancellationToken);

                return chunk is null
                    ? NotFoundError("ReplayChunkNotFound", "The session or requested driver set does not exist.", ("sessionId", sessionId), ("drivers", selectedDrivers))
                    : Results.Ok(chunk);
            })
            .WithName("GetReplayChunk");

        api.MapGet("/sessions/{sessionId}/replay/context", async (
                string sessionId,
                long? fromMs,
                long? durationMs,
                bool? includeWeather,
                bool? includeTrackStatus,
                bool? includeRaceControl,
                IF1TelemetryQueryStore store,
                CancellationToken cancellationToken) =>
            {
                if (!IsValidSessionId(sessionId))
                {
                    return ValidationError("InvalidSessionId", "Session id must contain only lowercase letters, numbers, and hyphens.", ("sessionId", sessionId));
                }

                if (fromMs is null or < 0)
                {
                    return ValidationError("InvalidTimeRange", "fromMs is required and must be greater than or equal to 0.", ("fromMs", fromMs));
                }

                if (durationMs is null or < 1_000 or > 600_000)
                {
                    return ValidationError("InvalidTimeRange", "durationMs is required and must be between 1000 and 600000.", ("durationMs", durationMs));
                }

                var context = await store.GetReplayContextAsync(
                    sessionId,
                    fromMs.Value,
                    durationMs.Value,
                    includeWeather ?? true,
                    includeTrackStatus ?? true,
                    includeRaceControl ?? true,
                    cancellationToken);

                return context is null
                    ? NotFoundError("SessionNotFound", $"Session {sessionId} does not exist.", ("sessionId", sessionId))
                    : Results.Ok(context);
            })
            .WithName("GetReplayContext");

        api.MapPost("/sessions/{sessionId}/telemetry-events/search", async (
                string sessionId,
                TelemetryEventSearchRequest? request,
                IF1TelemetryQueryStore store,
                CancellationToken cancellationToken) =>
            {
                if (!IsValidSessionId(sessionId))
                {
                    return ValidationError("InvalidSessionId", "Session id must contain only lowercase letters, numbers, and hyphens.", ("sessionId", sessionId));
                }

                request ??= new TelemetryEventSearchRequest(null, null, null, null, null);

                if (request.EventTypes is { Count: > 0 }
                    && request.EventTypes.Any(eventType => !TelemetryEventTypes.Contains(eventType)))
                {
                    return ValidationError("InvalidEventType", "Unknown telemetry event type.", ("allowed", TelemetryEventTypes.ToArray()));
                }

                if (request.Drivers is { Count: > 0 }
                    && request.Drivers.Any(driver => !IsValidDriverCode(driver)))
                {
                    return ValidationError("InvalidDriver", "Driver codes must contain 2 to 4 letters.", ("drivers", request.Drivers));
                }

                if (request.FromMs is < 0 || request.DurationMs is < 1_000 or > 600_000)
                {
                    return ValidationError("InvalidTimeRange", "fromMs must be non-negative and durationMs must be between 1000 and 600000.", ("fromMs", request.FromMs), ("durationMs", request.DurationMs));
                }

                if (request.Limit is < 1 or > 5_000)
                {
                    return ValidationError("InvalidLimit", "limit must be between 1 and 5000.", ("limit", request.Limit));
                }

                var response = await store.SearchTelemetryEventsAsync(sessionId, request, cancellationToken);
                return response is null
                    ? NotFoundError("SessionNotFound", $"Session {sessionId} does not exist.", ("sessionId", sessionId))
                    : Results.Ok(response);
            })
            .WithName("SearchTelemetryEvents");

        return api;
    }

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
        Results.BadRequest(CreateError(code, message, details));

    private static IResult NotFoundError(
        string code,
        string message,
        params (string Key, object? Value)[] details) =>
        Results.NotFound(CreateError(code, message, details));

    private static ApiErrorResponse CreateError(
        string code,
        string message,
        params (string Key, object? Value)[] details) =>
        new(new ApiError(
            code,
            message,
            details.Length == 0
                ? null
                : details.ToDictionary(detail => detail.Key, detail => detail.Value)));
}
