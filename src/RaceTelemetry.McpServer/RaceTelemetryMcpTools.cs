using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using RaceTelemetry.Contracts;
using RaceTelemetry.Data;


namespace RaceTelemetry.McpServer;

/// <summary>
/// Exposes race telemetry read-only tools to MCP clients by validating requests and delegating to the query store.
/// </summary>
[McpServerToolType]
public sealed partial class RaceTelemetryMcpTools(IF1TelemetryQueryStore store)
{
    private static readonly ActivitySource ActivitySource = new("RaceTelemetry.McpServer");

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

    private static readonly HashSet<string> AggregateGroupBy = new(StringComparer.OrdinalIgnoreCase)
    {
        "driver",
        "lap",
        "stint",
        "compound",
        "track_status",
        "time_bucket"
    };

    private static readonly HashSet<string> AggregateMetrics = new(StringComparer.OrdinalIgnoreCase)
    {
        "sample_count",
        "avg_speed_kmh",
        "max_speed_kmh",
        "avg_throttle_pct",
        "avg_brake_pct",
        "brake_time_ms",
        "drs_active_time_ms",
        "throttle_lift_count",
        "high_speed_time_ms"
    };

    private static readonly HashSet<string> StintMetrics = new(StringComparer.OrdinalIgnoreCase)
    {
        "lap_time_slope_ms_per_lap",
        "best_lap_time_ms",
        "average_lap_time_ms",
        "worst_lap_time_ms"
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
        using var activity = StartToolActivity("list_sessions");
        activity?.SetTag("race.query.year", year);
        activity?.SetTag("race.query.event", eventName);
        activity?.SetTag("race.query.session_type", sessionType);

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
        using var activity = StartToolActivity("get_session_drivers", sessionId);

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
        using var activity = StartToolActivity("get_driver_laps", sessionId, driverCode);

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
        using var activity = StartToolActivity("get_replay_metadata", sessionId);

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
        using var activity = StartToolActivity("get_lap_telemetry", sessionId, driverCode, lapNumber);
        activity?.SetTag("race.query.sample_every", sampleEvery);
        activity?.SetTag("race.query.max_samples", maxSamples);

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
        Name = "get_lap_quality",
        Title = "Get Lap Quality",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Get objective lap-level distance-alignment quality metrics and validation status.")]
    public async Task<LapQualityResponse> GetLapQuality(
        [Description("Session id, for example 2025-italian-grand-prix-r.")] string sessionId,
        [Description("Driver abbreviation, for example LEC.")] string driverCode,
        [Description("Positive lap number.")] int lapNumber,
        CancellationToken cancellationToken = default)
    {
        using var activity = StartToolActivity("get_lap_quality", sessionId, driverCode, lapNumber);

        ValidateSessionDriverLap(sessionId, driverCode, lapNumber);

        return await store.GetLapQualityAsync(sessionId, driverCode, lapNumber, cancellationToken)
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
        using var activity = StartToolActivity("get_lap_story", sessionId, driverCode, lapNumber);

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
        using var activity = StartToolActivity("get_lap_braking_zones", sessionId, driverCode, lapNumber);
        activity?.SetTag("race.query.brake_threshold_pct", brakeThresholdPct);
        activity?.SetTag("race.query.minimum_duration_ms", minimumDurationMs);

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
        using var activity = StartToolActivity("compare_laps", sessionId, driverA, lapA);
        activity?.SetTag("race.query.driver_b", driverB.ToUpperInvariant());
        activity?.SetTag("race.query.lap_b", lapB);
        activity?.SetTag("race.query.time_step_ms", timeStepMs);

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
        Name = "compare_laps_by_distance",
        Title = "Compare Laps By Distance",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Compare two laps in the distance domain so the response answers where performance was gained or lost.")]
    public async Task<LapComparisonByDistanceResponse> CompareLapsByDistance(
        [Description("Session id, for example 2025-italian-grand-prix-r.")] string sessionId,
        [Description("First driver abbreviation.")] string driverA,
        [Description("First lap number.")] int lapA,
        [Description("Second driver abbreviation.")] string driverB,
        [Description("Second lap number.")] int lapB,
        [Description("Optional lower distance bound in metres.")] double? startDistanceM = null,
        [Description("Optional upper distance bound in metres.")] double? endDistanceM = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = StartToolActivity("compare_laps_by_distance", sessionId, driverA, lapA);
        activity?.SetTag("race.query.driver_b", driverB.ToUpperInvariant());
        activity?.SetTag("race.query.lap_b", lapB);
        activity?.SetTag("race.query.start_distance_m", startDistanceM);
        activity?.SetTag("race.query.end_distance_m", endDistanceM);

        ValidateSessionDriverLap(sessionId, driverA, lapA);
        ValidateDriverCode(driverB);
        ValidateLapNumber(lapB);
        ValidateDistanceRange(startDistanceM, endDistanceM);

        return await store.CompareLapsByDistanceAsync(
                sessionId,
                driverA,
                lapA,
                driverB,
                lapB,
                startDistanceM,
                endDistanceM,
                cancellationToken)
            ?? throw NotFound("One or both requested laps do not exist, or no distance-aligned telemetry is available.");
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
        using var activity = StartToolActivity("compare_laps_story", sessionId, driverA, lapA);
        activity?.SetTag("race.query.driver_b", driverB.ToUpperInvariant());
        activity?.SetTag("race.query.lap_b", lapB);
        activity?.SetTag("race.query.segment_count", segmentCount);

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
        using var activity = StartToolActivity("get_race_story", sessionId);
        activity?.SetTag("race.query.race_control_limit", raceControlLimit);

        ValidateSessionId(sessionId);
        ValidateRange(raceControlLimit, 0, 1_000, nameof(raceControlLimit));

        return await store.GetRaceStoryAsync(sessionId, raceControlLimit, cancellationToken)
            ?? throw NotFound($"Session {sessionId} does not exist.");
    }

    [McpServerTool(
        Name = "aggregate_telemetry",
        Title = "Aggregate Telemetry",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Aggregate telemetry by driver, lap, stint, compound, track status, or time bucket. Use this before requesting raw samples for tyre degradation, DRS time, braking time, and speed summaries.")]
    public async Task<TelemetryAggregateResponse> AggregateTelemetry(
        [Description("Session id, for example 2025-italian-grand-prix-r.")] string sessionId,
        [Description("Optional comma-separated driver list, for example LEC,VER.")] string? drivers = null,
        [Description("Comma-separated grouping list. Allowed: driver,lap,stint,compound,track_status,time_bucket.")] string groupBy = "driver",
        [Description("Comma-separated metric list. Allowed: sample_count,avg_speed_kmh,max_speed_kmh,avg_throttle_pct,avg_brake_pct,brake_time_ms,drs_active_time_ms,throttle_lift_count,high_speed_time_ms.")] string metrics = "sample_count,avg_speed_kmh",
        [Description("Optional first lap in the range.")] int? lapFrom = null,
        [Description("Optional last lap in the range.")] int? lapTo = null,
        [Description("Optional comma-separated tyre compound filter, for example SOFT,MEDIUM,HARD.")] string? compound = null,
        [Description("Exclude pit-in and pit-out laps when lap data is available.")] bool excludePitLaps = true,
        [Description("Optional comma-separated track-status filter, for example green,yellow,safety_car.")] string? trackStatus = null,
        [Description("Time bucket in milliseconds when groupBy includes time_bucket. Range: 1000 to 600000.")] int? timeBucketMs = null,
        [Description("Maximum returned aggregate rows. Range: 1 to 5000.")] int limit = 500,
        CancellationToken cancellationToken = default)
    {
        using var activity = StartToolActivity("aggregate_telemetry", sessionId);
        activity?.SetTag("race.query.group_by", groupBy);
        activity?.SetTag("race.query.metrics", metrics);
        activity?.SetTag("race.query.limit", limit);

        ValidateSessionId(sessionId);
        ValidateLapRange(lapFrom, lapTo);
        ValidateRange(limit, 1, 5_000, nameof(limit));
        if (timeBucketMs is not null)
        {
            ValidateRange(timeBucketMs.Value, 1_000, 600_000, nameof(timeBucketMs));
        }

        var selectedGroupBy = ParseAllowedList(groupBy, AggregateGroupBy, ["driver"], nameof(groupBy));
        var selectedMetrics = ParseAllowedList(metrics, AggregateMetrics, ["sample_count", "avg_speed_kmh"], nameof(metrics));
        var selectedDrivers = ParseDrivers(drivers);
        var request = new TelemetryAggregateRequest(
            selectedDrivers,
            selectedGroupBy,
            selectedMetrics,
            new TelemetryAggregateFilters(
                lapFrom is null && lapTo is null ? null : new LapRange(lapFrom, lapTo),
                ParseUpperList(compound),
                excludePitLaps,
                ParseLowerList(trackStatus)),
            timeBucketMs,
            limit);

        return await store.AggregateTelemetryAsync(sessionId, request, cancellationToken)
            ?? throw NotFound($"Session {sessionId} does not exist.");
    }

    [McpServerTool(
        Name = "detect_telemetry_windows",
        Title = "Detect Telemetry Windows",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Detect contiguous DRS, hard-braking, throttle-lift, or high-speed windows without returning raw telemetry samples.")]
    public async Task<TelemetryWindowResponse> DetectTelemetryWindows(
        [Description("Session id, for example 2025-italian-grand-prix-r.")] string sessionId,
        [Description("Event type. Allowed: drs_active,hard_braking,throttle_lift,high_speed.")] string eventType,
        [Description("Optional comma-separated driver list, for example LEC,VER.")] string? drivers = null,
        [Description("Optional first lap in the range.")] int? lapFrom = null,
        [Description("Optional last lap in the range.")] int? lapTo = null,
        [Description("Minimum event duration in milliseconds. Range: 0 to 10000.")] int minimumDurationMs = 250,
        [Description("Attach nearest circuit corner when position/circuit-marker data is available.")] bool includeNearestCorner = true,
        [Description("Maximum returned windows. Range: 1 to 5000.")] int limit = 1_000,
        CancellationToken cancellationToken = default)
    {
        using var activity = StartToolActivity("detect_telemetry_windows", sessionId);
        activity?.SetTag("race.query.event_type", eventType);
        activity?.SetTag("race.query.minimum_duration_ms", minimumDurationMs);
        activity?.SetTag("race.query.limit", limit);

        ValidateSessionId(sessionId);
        ValidateTelemetryEventType(eventType);
        ValidateLapRange(lapFrom, lapTo);
        ValidateRange(minimumDurationMs, 0, 10_000, nameof(minimumDurationMs));
        ValidateRange(limit, 1, 5_000, nameof(limit));

        var request = new TelemetryWindowRequest(
            ParseDrivers(drivers),
            eventType.ToLowerInvariant(),
            lapFrom is null && lapTo is null ? null : new LapRange(lapFrom, lapTo),
            minimumDurationMs,
            includeNearestCorner,
            limit);

        return await store.DetectTelemetryWindowsAsync(sessionId, request, cancellationToken)
            ?? throw NotFound($"Session {sessionId} does not exist.");
    }

    [McpServerTool(
        Name = "analyze_driver_stints",
        Title = "Analyze Driver Stints",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Summarize tyre stints and lap-time trend by driver/compound without downloading lap telemetry.")]
    public async Task<StintAnalysisResponse> AnalyzeDriverStints(
        [Description("Session id, for example 2025-italian-grand-prix-r.")] string sessionId,
        [Description("Optional comma-separated driver list, for example LEC,VER.")] string? drivers = null,
        [Description("Optional comma-separated tyre compound filter, for example SOFT,MEDIUM,HARD.")] string? compound = null,
        [Description("Exclude pit-in and pit-out laps.")] bool excludePitLaps = true,
        [Description("Minimum counted laps per stint. Range: 1 to 100.")] int minimumLaps = 3,
        [Description("Comma-separated metric list. Allowed: lap_time_slope_ms_per_lap,best_lap_time_ms,average_lap_time_ms,worst_lap_time_ms.")] string metrics = "lap_time_slope_ms_per_lap,best_lap_time_ms,average_lap_time_ms",
        CancellationToken cancellationToken = default)
    {
        using var activity = StartToolActivity("analyze_driver_stints", sessionId);
        activity?.SetTag("race.query.minimum_laps", minimumLaps);
        activity?.SetTag("race.query.metrics", metrics);

        ValidateSessionId(sessionId);
        ValidateRange(minimumLaps, 1, 100, nameof(minimumLaps));

        var request = new StintAnalysisRequest(
            ParseDrivers(drivers),
            ParseUpperList(compound),
            excludePitLaps,
            minimumLaps,
            ParseAllowedList(metrics, StintMetrics, ["lap_time_slope_ms_per_lap", "best_lap_time_ms", "average_lap_time_ms"], nameof(metrics)));

        return await store.AnalyzeDriverStintsAsync(sessionId, request, cancellationToken)
            ?? throw NotFound($"Session {sessionId} does not exist.");
    }

    [McpServerTool(
        Name = "analyze_pit_stops",
        Title = "Analyze Pit Stops",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Analyze pit-in/out lap markers and estimate pit-lap loss against nearby non-pit laps.")]
    public async Task<PitStopAnalysisResponse> AnalyzePitStops(
        [Description("Session id, for example 2025-italian-grand-prix-r.")] string sessionId,
        [Description("Optional comma-separated driver list, for example LEC,VER.")] string? drivers = null,
        [Description("Number of nearby non-pit laps on either side used for baseline. Range: 1 to 10.")] int nearbyLapWindow = 3,
        [Description("Maximum returned pit markers. Range: 1 to 1000.")] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        using var activity = StartToolActivity("analyze_pit_stops", sessionId);
        activity?.SetTag("race.query.nearby_lap_window", nearbyLapWindow);
        activity?.SetTag("race.query.limit", limit);

        ValidateSessionId(sessionId);
        ValidateRange(nearbyLapWindow, 1, 10, nameof(nearbyLapWindow));
        ValidateRange(limit, 1, 1_000, nameof(limit));

        var request = new PitStopAnalysisRequest(ParseDrivers(drivers), nearbyLapWindow, limit);
        return await store.AnalyzePitStopsAsync(sessionId, request, cancellationToken)
            ?? throw NotFound($"Session {sessionId} does not exist.");
    }

    [McpServerTool(
        Name = "get_weather_trend",
        Title = "Get Weather Trend",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Summarize weather changes over a session or selected time window without returning every weather sample.")]
    public async Task<WeatherTrendResponse> GetWeatherTrend(
        [Description("Session id, for example 2025-italian-grand-prix-r.")] string sessionId,
        [Description("Optional window start in session-relative milliseconds.")] long? fromMs = null,
        [Description("Optional window duration in milliseconds. Range: 1000 to 86400000.")] long? durationMs = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = StartToolActivity("get_weather_trend", sessionId);
        activity?.SetTag("race.query.from_ms", fromMs);
        activity?.SetTag("race.query.duration_ms", durationMs);

        ValidateSessionId(sessionId);
        if (fromMs is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fromMs), "fromMs must be non-negative.");
        }

        if (durationMs is not null)
        {
            ValidateRange(durationMs.Value, 1_000, 86_400_000, nameof(durationMs));
        }

        var request = new WeatherTrendRequest(fromMs, durationMs);
        return await store.GetWeatherTrendAsync(sessionId, request, cancellationToken)
            ?? throw NotFound($"Session {sessionId} does not exist.");
    }

    [McpServerTool(
        Name = "get_race_control_timeline",
        Title = "Get Race Control Timeline",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Return a filtered race-control timeline with category, flag, and status counts.")]
    public async Task<RaceControlTimelineResponse> GetRaceControlTimeline(
        [Description("Session id, for example 2025-italian-grand-prix-r.")] string sessionId,
        [Description("Optional comma-separated category filter.")] string? categories = null,
        [Description("Optional comma-separated flag filter.")] string? flags = null,
        [Description("Optional comma-separated status filter.")] string? statuses = null,
        [Description("Optional comma-separated scope filter.")] string? scopes = null,
        [Description("Optional comma-separated racing-number filter, for example 16,44.")] string? racingNumbers = null,
        [Description("Optional first lap in the range.")] int? lapFrom = null,
        [Description("Optional last lap in the range.")] int? lapTo = null,
        [Description("Optional text search over category, flag, status, scope, sector, and message.")] string? search = null,
        [Description("Maximum returned messages. Range: 1 to 1000.")] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        using var activity = StartToolActivity("get_race_control_timeline", sessionId);
        activity?.SetTag("race.query.limit", limit);
        activity?.SetTag("race.query.search", search);

        ValidateSessionId(sessionId);
        ValidateLapRange(lapFrom, lapTo);
        ValidateRange(limit, 1, 1_000, nameof(limit));
        if (search is { Length: > 200 })
        {
            throw new ArgumentException("search must be 200 characters or fewer.", nameof(search));
        }

        var request = new RaceControlTimelineRequest(
            ParseRawList(categories),
            ParseRawList(flags),
            ParseRawList(statuses),
            ParseRawList(scopes),
            ParseIntegerList(racingNumbers),
            lapFrom is null && lapTo is null ? null : new LapRange(lapFrom, lapTo),
            search,
            limit);

        return await store.GetRaceControlTimelineAsync(sessionId, request, cancellationToken)
            ?? throw NotFound($"Session {sessionId} does not exist.");
    }

    [McpServerTool(
        Name = "get_circuit_context",
        Title = "Get Circuit Context",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Get imported circuit rotation and corner/marshal markers for mapping telemetry windows to track context.")]
    public async Task<CircuitContextResponse> GetCircuitContext(
        [Description("Session id, for example 2025-italian-grand-prix-r.")] string sessionId,
        CancellationToken cancellationToken = default)
    {
        using var activity = StartToolActivity("get_circuit_context", sessionId);

        ValidateSessionId(sessionId);
        return await store.GetCircuitContextAsync(sessionId, cancellationToken)
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
        using var activity = StartToolActivity("get_replay_chunk", sessionId);
        activity?.SetTag("race.query.from_ms", fromMs);
        activity?.SetTag("race.query.duration_ms", durationMs);
        activity?.SetTag("race.query.sample_every", sampleEvery);

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
        using var activity = StartToolActivity("get_replay_context", sessionId);
        activity?.SetTag("race.query.from_ms", fromMs);
        activity?.SetTag("race.query.duration_ms", durationMs);
        activity?.SetTag("race.query.include_weather", includeWeather);
        activity?.SetTag("race.query.include_track_status", includeTrackStatus);
        activity?.SetTag("race.query.include_race_control", includeRaceControl);

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
        using var activity = StartToolActivity("search_telemetry_events", sessionId);
        activity?.SetTag("race.query.from_ms", fromMs);
        activity?.SetTag("race.query.duration_ms", durationMs);
        activity?.SetTag("race.query.limit", limit);

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
}
