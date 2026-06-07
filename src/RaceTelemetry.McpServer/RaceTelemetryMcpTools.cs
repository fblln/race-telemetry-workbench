using System.ComponentModel;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using RaceTelemetry.Contracts;
using RaceTelemetry.Data;

namespace RaceTelemetry.McpServer;

[McpServerToolType]
public sealed partial class RaceTelemetryMcpTools(IF1TelemetryQueryStore store)
{
    private static readonly HashSet<string> SessionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "FP1",
        "FP2",
        "FP3",
        "Q",
        "SQ",
        "S",
        "R"
    };

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

    [McpServerTool(
        Name = "list_sessions",
        Title = "List Sessions",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("List imported sessions. Defaults to race sessions; pass another session type only when explicitly needed.")]
    public async Task<SessionsResponse> ListSessions(
        [Description("Optional season year, for example 2025.")] int? year = null,
        [Description("Optional case-insensitive event-name filter, for example Monza.")] string? eventName = null,
        [Description("Session type filter. Defaults to R. Valid values: FP1, FP2, FP3, Q, SQ, S, R.")] string? sessionType = "R",
        CancellationToken cancellationToken = default)
    {
        ValidateYear(year);
        ValidateSessionType(sessionType);

        var sessions = await store.GetSessionsAsync(year, eventName, NormalizeSessionType(sessionType), cancellationToken);
        return new SessionsResponse(sessions);
    }

    [McpServerTool(
        Name = "get_session_drivers",
        Title = "Get Session Drivers",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("List drivers for one imported session.")]
    public async Task<DriversResponse> GetSessionDrivers(
        [Description("Session id, for example 2025-italian-grand-prix-r.")] string sessionId,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);

        var drivers = await store.GetDriversAsync(sessionId, cancellationToken)
            ?? throw NotFound($"Session {sessionId} does not exist.");

        return new DriversResponse(sessionId, drivers);
    }

    [McpServerTool(
        Name = "get_driver_laps",
        Title = "Get Driver Laps",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("List non-deleted laps for a driver in a session.")]
    public async Task<LapsResponse> GetDriverLaps(
        [Description("Session id, for example 2025-italian-grand-prix-r.")] string sessionId,
        [Description("Driver abbreviation, for example LEC.")] string driverCode,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionAndDriver(sessionId, driverCode);

        var laps = await store.GetLapsAsync(sessionId, driverCode, cancellationToken)
            ?? throw NotFound($"Driver {driverCode.ToUpperInvariant()} does not exist in session {sessionId}.");

        return new LapsResponse(sessionId, driverCode.ToUpperInvariant(), laps);
    }

    [McpServerTool(
        Name = "get_replay_metadata",
        Title = "Get Replay Metadata",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Get replay bounds, available drivers, available channels, track markers, context availability, and weather summary.")]
    public async Task<ReplayMetadata> GetReplayMetadata(
        [Description("Session id, for example 2025-italian-grand-prix-r.")] string sessionId,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);

        return await store.GetReplayMetadataAsync(sessionId, cancellationToken)
            ?? throw NotFound($"Session {sessionId} does not exist.");
    }

    [McpServerTool(
        Name = "get_lap_telemetry",
        Title = "Get Lap Telemetry",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Get bounded telemetry samples for one driver's lap.")]
    public async Task<LapTelemetryResponse> GetLapTelemetry(
        [Description("Session id, for example 2025-italian-grand-prix-r.")] string sessionId,
        [Description("Driver abbreviation, for example LEC.")] string driverCode,
        [Description("Positive lap number.")] int lapNumber,
        [Description("Optional comma-separated channel list. Defaults to speed_kmh,throttle_pct,brake_pct,rpm,gear.")] string? channels = null,
        [Description("Read every Nth sample. Range: 1 to 100.")] int sampleEvery = 1,
        [Description("Maximum returned samples. Range: 1 to 50000.")] int maxSamples = 5_000,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionDriverLap(sessionId, driverCode, lapNumber);
        ValidateRange(sampleEvery, 1, 100, nameof(sampleEvery));
        ValidateRange(maxSamples, 1, 50_000, nameof(maxSamples));

        var selectedChannels = ParseChannels(
            channels,
            TelemetryChannels,
            ["speed_kmh", "throttle_pct", "brake_pct", "rpm", "gear"]);

        return await store.GetLapTelemetryAsync(
                sessionId,
                driverCode,
                lapNumber,
                selectedChannels,
                sampleEvery,
                maxSamples,
                cancellationToken)
            ?? throw NotFound($"Lap {lapNumber} for driver {driverCode.ToUpperInvariant()} does not exist in session {sessionId}.");
    }

    [McpServerTool(
        Name = "get_lap_story",
        Title = "Get Lap Story",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Get a compact analyst-ready lap summary with lap time, sectors, tyre context, speed/throttle/brake aggregates, and deterministic insights.")]
    public async Task<LapStoryResponse> GetLapStory(
        [Description("Session id, for example 2025-italian-grand-prix-r.")] string sessionId,
        [Description("Driver abbreviation, for example LEC.")] string driverCode,
        [Description("Positive lap number.")] int lapNumber,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionDriverLap(sessionId, driverCode, lapNumber);

        return await store.GetLapStoryAsync(sessionId, driverCode, lapNumber, cancellationToken)
            ?? throw NotFound($"Lap {lapNumber} for driver {driverCode.ToUpperInvariant()} does not exist in session {sessionId}.");
    }

    [McpServerTool(
        Name = "get_lap_braking_zones",
        Title = "Get Lap Braking Zones",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Detect contiguous braking windows for a lap and, when position/circuit-marker data aligns, attach the nearest corner label.")]
    public async Task<LapBrakingZonesResponse> GetLapBrakingZones(
        [Description("Session id, for example 2025-italian-grand-prix-r.")] string sessionId,
        [Description("Driver abbreviation, for example LEC.")] string driverCode,
        [Description("Positive lap number.")] int lapNumber,
        [Description("Brake percentage threshold for a braking sample. Range: 1 to 100.")] int brakeThresholdPct = 80,
        [Description("Minimum braking-zone duration in milliseconds. Range: 0 to 10000.")] int minimumDurationMs = 250,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionDriverLap(sessionId, driverCode, lapNumber);
        ValidateRange(brakeThresholdPct, 1, 100, nameof(brakeThresholdPct));
        ValidateRange(minimumDurationMs, 0, 10_000, nameof(minimumDurationMs));

        return await store.GetLapBrakingZonesAsync(
                sessionId,
                driverCode,
                lapNumber,
                brakeThresholdPct,
                minimumDurationMs,
                cancellationToken)
            ?? throw NotFound($"Lap {lapNumber} for driver {driverCode.ToUpperInvariant()} does not exist in session {sessionId}.");
    }

    [McpServerTool(
        Name = "compare_laps",
        Title = "Compare Laps",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Compare two laps by lap-relative telemetry time buckets.")]
    public async Task<LapComparisonResponse> CompareLaps(
        [Description("Session id, for example 2025-italian-grand-prix-r.")] string sessionId,
        [Description("First driver abbreviation.")] string driverA,
        [Description("First lap number.")] int lapA,
        [Description("Second driver abbreviation.")] string driverB,
        [Description("Second lap number.")] int lapB,
        [Description("Optional comma-separated channel list. Defaults to speed_kmh,throttle_pct,brake_pct.")] string? channels = null,
        [Description("Time-bucket size in milliseconds. Range: 20 to 5000.")] int timeStepMs = 100,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionDriverLap(sessionId, driverA, lapA);
        ValidateDriverCode(driverB);
        ValidateLapNumber(lapB);
        ValidateRange(timeStepMs, 20, 5_000, nameof(timeStepMs));

        var selectedChannels = ParseChannels(
            channels,
            TelemetryChannels,
            ["speed_kmh", "throttle_pct", "brake_pct"]);

        return await store.CompareLapsAsync(
                sessionId,
                driverA,
                lapA,
                driverB,
                lapB,
                selectedChannels,
                timeStepMs,
                cancellationToken)
            ?? throw NotFound("One or both requested laps do not exist.");
    }

    [McpServerTool(
        Name = "compare_laps_story",
        Title = "Compare Laps Story",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Compare two laps as an analyst story: total delta, sector deltas, segment-level speed/throttle/brake differences, and talking-point insights.")]
    public async Task<LapComparisonStoryResponse> CompareLapsStory(
        [Description("Session id, for example 2025-italian-grand-prix-r.")] string sessionId,
        [Description("First driver abbreviation. Delta values are driverA minus driverB.")] string driverA,
        [Description("First lap number.")] int lapA,
        [Description("Second driver abbreviation.")] string driverB,
        [Description("Second lap number.")] int lapB,
        [Description("Number of lap segments. Range: 2 to 12. Defaults to 3 for opening/middle/final thirds.")] int segmentCount = 3,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionDriverLap(sessionId, driverA, lapA);
        ValidateDriverCode(driverB);
        ValidateLapNumber(lapB);
        ValidateRange(segmentCount, 2, 12, nameof(segmentCount));

        return await store.CompareLapsStoryAsync(
                sessionId,
                driverA,
                lapA,
                driverB,
                lapB,
                segmentCount,
                cancellationToken)
            ?? throw NotFound("One or both requested laps do not exist.");
    }

    [McpServerTool(
        Name = "get_race_story",
        Title = "Get Race Story",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Get compact race-level context for natural-language analysis: weather, tyre stints, pit markers, track-status periods, race-control highlights, and insights.")]
    public async Task<RaceStoryResponse> GetRaceStory(
        [Description("Session id, for example 2025-italian-grand-prix-r.")] string sessionId,
        [Description("Maximum race-control messages to include. Range: 0 to 1000.")] int raceControlLimit = 100,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);
        ValidateRange(raceControlLimit, 0, 1_000, nameof(raceControlLimit));

        return await store.GetRaceStoryAsync(sessionId, raceControlLimit, cancellationToken)
            ?? throw NotFound($"Session {sessionId} does not exist.");
    }

    [McpServerTool(
        Name = "get_replay_chunk",
        Title = "Get Replay Chunk",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Get bounded replay samples for a session-relative time window.")]
    public async Task<ReplayChunkResponse> GetReplayChunk(
        [Description("Session id, for example 2025-italian-grand-prix-r.")] string sessionId,
        [Description("Window start in session-relative milliseconds.")] long fromMs,
        [Description("Window duration in milliseconds. Range: 1000 to 120000.")] long durationMs = 30_000,
        [Description("Optional comma-separated driver list, for example LEC,VER.")] string? drivers = null,
        [Description("Optional comma-separated channel list. Defaults to all replay channels.")] string? channels = null,
        [Description("Read every Nth sample. Range: 1 to 100.")] int sampleEvery = 1,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);
        ValidateRange(fromMs, 0, long.MaxValue, nameof(fromMs));
        ValidateRange(durationMs, 1_000, 120_000, nameof(durationMs));
        ValidateRange(sampleEvery, 1, 100, nameof(sampleEvery));

        var selectedDrivers = ParseDrivers(drivers);
        var selectedChannels = ParseChannels(channels, ReplayChannels, ReplayChannels.Order().ToArray());

        return await store.GetReplayChunkAsync(
                sessionId,
                fromMs,
                durationMs,
                selectedDrivers,
                selectedChannels,
                sampleEvery,
                cancellationToken)
            ?? throw NotFound("The session or requested driver set does not exist.");
    }

    [McpServerTool(
        Name = "get_replay_context",
        Title = "Get Replay Context",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Get weather, track status, and race-control context for a session-relative time window.")]
    public async Task<ReplayContextResponse> GetReplayContext(
        [Description("Session id, for example 2025-italian-grand-prix-r.")] string sessionId,
        [Description("Window start in session-relative milliseconds.")] long fromMs,
        [Description("Window duration in milliseconds. Range: 1000 to 600000.")] long durationMs = 30_000,
        [Description("Include weather samples.")] bool includeWeather = true,
        [Description("Include track-status events.")] bool includeTrackStatus = true,
        [Description("Include race-control messages.")] bool includeRaceControl = true,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);
        ValidateRange(fromMs, 0, long.MaxValue, nameof(fromMs));
        ValidateRange(durationMs, 1_000, 600_000, nameof(durationMs));

        return await store.GetReplayContextAsync(
                sessionId,
                fromMs,
                durationMs,
                includeWeather,
                includeTrackStatus,
                includeRaceControl,
                cancellationToken)
            ?? throw NotFound($"Session {sessionId} does not exist.");
    }

    [McpServerTool(
        Name = "search_telemetry_events",
        Title = "Search Telemetry Events",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Search bounded telemetry event candidates such as hard braking, high speed, DRS active, and throttle lift.")]
    public async Task<TelemetryEventSearchResponse> SearchTelemetryEvents(
        [Description("Session id, for example 2025-italian-grand-prix-r.")] string sessionId,
        [Description("Optional comma-separated event type list: hard_braking,high_speed,drs_active,throttle_lift.")] string? eventTypes = null,
        [Description("Optional comma-separated driver list, for example LEC,VER.")] string? drivers = null,
        [Description("Optional window start in session-relative milliseconds.")] long? fromMs = null,
        [Description("Optional window duration in milliseconds. Range: 1000 to 600000.")] long? durationMs = null,
        [Description("Maximum returned events. Range: 1 to 5000.")] int limit = 500,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);
        if (fromMs is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fromMs), "fromMs must be non-negative.");
        }

        if (durationMs is not null)
        {
            ValidateRange(durationMs.Value, 1_000, 600_000, nameof(durationMs));
        }

        ValidateRange(limit, 1, 5_000, nameof(limit));

        var selectedEventTypes = ParseEventTypes(eventTypes);
        var selectedDrivers = ParseDrivers(drivers);
        var request = new TelemetryEventSearchRequest(
            selectedEventTypes,
            selectedDrivers,
            fromMs,
            durationMs,
            limit);

        return await store.SearchTelemetryEventsAsync(sessionId, request, cancellationToken)
            ?? throw NotFound($"Session {sessionId} does not exist.");
    }

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

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex SessionIdPattern();

    [GeneratedRegex("^[A-Za-z]{2,4}$")]
    private static partial Regex DriverCodePattern();
}
