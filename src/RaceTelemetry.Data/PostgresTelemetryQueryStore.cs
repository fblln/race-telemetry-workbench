using System.Data;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using NpgsqlTypes;
using RaceTelemetry.Contracts;

namespace RaceTelemetry.Data;

/// <summary>
/// Queries session, lap, replay metadata, and race-story data from PostgreSQL for the Query API and MCP server.
/// </summary>
public sealed partial class PostgresTelemetryQueryStore(NpgsqlDataSource dataSource, IMemoryCache cache) : IF1TelemetryQueryStore
{
    private readonly NpgsqlDataSource _dataSource = dataSource;
    private readonly IMemoryCache _cache = cache;

    private static readonly ActivitySource ActivitySource = new("RaceTelemetry.Data");
    private static readonly MemoryCacheEntryOptions MetadataCacheOptions = new MemoryCacheEntryOptions()
        .SetSize(1)
        .SetSlidingExpiration(TimeSpan.FromMinutes(10))
        .SetAbsoluteExpiration(TimeSpan.FromHours(1));

    private static readonly string[] ReplayChannels =
    [
        "speed_kmh",
        "throttle_pct",
        "brake_pct",
        "gear",
        "rpm",
        "drs",
        "session_time_ms",
        "lap_time_ms",
        "x",
        "y",
        "z"
    ];

    private static readonly string[] ContextChannels =
    [
        "weather",
        "track_status",
        "session_status",
        "race_control",
        "circuit_markers"
    ];

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

    private static readonly HashSet<string> TelemetryWindowTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "drs_active",
        "hard_braking",
        "throttle_lift",
        "high_speed"
    };

    private static readonly HashSet<string> StintMetrics = new(StringComparer.OrdinalIgnoreCase)
    {
        "lap_time_slope_ms_per_lap",
        "best_lap_time_ms",
        "average_lap_time_ms",
        "worst_lap_time_ms"
    };

    public async Task<IReadOnlyList<SessionSummary>> GetSessionsAsync(
        int? year,
        string? eventName,
        string? sessionType,
        CancellationToken cancellationToken)
    {
        using var activity = StartStoreActivity("query_store.get_sessions");
        activity?.SetTag("race.query.year", year);
        activity?.SetTag("race.query.event", eventName);
        activity?.SetTag("race.query.session_type", sessionType);

        const string sql = """
            WITH driver_counts AS (
                SELECT session_id, count(*)::int AS driver_count
                FROM session_drivers
                GROUP BY session_id
            ),
            lap_counts AS (
                -- Race distance (highest lap number reached), not the count of all drivers' lap rows.
                SELECT session_id, coalesce(max(lap_number), 0)::int AS lap_count
                FROM laps
                WHERE NOT is_deleted
                GROUP BY session_id
            )
            SELECT
                s.session_id,
                s.year,
                s.event_name,
                s.session_type,
                s.circuit_name,
                s.country,
                s.session_start_utc,
                coalesce(dc.driver_count, 0) AS driver_count,
                coalesce(lc.lap_count, 0) AS lap_count
            FROM sessions s
            LEFT JOIN driver_counts dc ON dc.session_id = s.session_id
            LEFT JOIN lap_counts lc ON lc.session_id = s.session_id
            WHERE (@year IS NULL OR s.year = @year)
              AND (@eventName IS NULL OR s.event_name ILIKE ('%' || @eventName || '%'))
              AND (@sessionType IS NULL OR s.session_type = upper(@sessionType))
            ORDER BY s.year DESC, s.event_name, s.session_type
            """;

        await using var command = _dataSource.CreateCommand(sql);
        AddNullable(command, "year", NpgsqlDbType.Integer, year);
        AddNullable(command, "eventName", NpgsqlDbType.Text, string.IsNullOrWhiteSpace(eventName) ? null : eventName);
        AddNullable(command, "sessionType", NpgsqlDbType.Text, string.IsNullOrWhiteSpace(sessionType) ? null : sessionType);

        var sessions = new List<SessionSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sessions.Add(new SessionSummary(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                GetNullableString(reader, 4),
                GetNullableString(reader, 5),
                GetNullableDateTimeOffset(reader, 6),
                reader.GetInt32(7),
                reader.GetInt32(8)));
        }

        return sessions;
    }

    public async Task<IReadOnlyList<DriverSummary>?> GetDriversAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        using var activity = StartStoreActivity("query_store.get_drivers", sessionId);

        var cacheKey = CacheKey("drivers", sessionId);
        if (_cache.TryGetValue<IReadOnlyList<DriverSummary>>(cacheKey, out var cachedDrivers))
        {
            return cachedDrivers;
        }

        const string sql = """
            WITH session_check AS (
                SELECT EXISTS (
                    SELECT 1
                    FROM sessions
                    WHERE session_id = @sessionId
                ) AS session_exists
            ),
            drivers AS (
                SELECT
                    sd.session_id,
                    sd.driver_code,
                    sd.driver_number::text,
                    sd.full_name,
                    sd.team_name,
                    count(l.lap_id)::int AS lap_count
                FROM session_drivers sd
                LEFT JOIN laps l
                    ON l.session_id = sd.session_id
                    AND l.driver_code = sd.driver_code
                    AND NOT l.is_deleted
                WHERE sd.session_id = @sessionId
                GROUP BY
                    sd.session_id,
                    sd.driver_code,
                    sd.driver_number,
                    sd.full_name,
                    sd.team_name
            )
            SELECT
                session_check.session_exists,
                drivers.session_id,
                drivers.driver_code,
                drivers.driver_number,
                drivers.full_name,
                drivers.team_name,
                drivers.lap_count
            FROM session_check
            LEFT JOIN drivers ON true
            ORDER BY drivers.driver_code
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);

        var drivers = new List<DriverSummary>();
        var sessionExists = false;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sessionExists = reader.GetBoolean(0);
            if (reader.IsDBNull(1))
            {
                continue;
            }

            drivers.Add(new DriverSummary(
                reader.GetString(1),
                reader.GetString(2),
                GetNullableString(reader, 3),
                GetNullableString(reader, 4),
                GetNullableString(reader, 5),
                reader.GetInt32(6)));
        }

        if (!sessionExists)
        {
            return null;
        }

        _cache.Set(cacheKey, drivers, MetadataCacheOptions);
        return drivers;
    }

    public async Task<SessionFactsResponse?> GetSessionFactsAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        using var activity = StartStoreActivity("query_store.get_session_facts", sessionId);

        var cacheKey = CacheKey("session_facts", sessionId);
        if (_cache.TryGetValue<SessionFactsResponse>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        // Track-status codes: 4=SC deployed, 5=red flag, 6=VSC deployed (stored as text).
        const string sql = """
            SELECT
                s.circuit_name,
                s.country,
                (SELECT count(DISTINCT driver_code) FROM laps WHERE session_id = @sessionId AND NOT is_deleted)::int,
                (SELECT coalesce(max(lap_number), 0) FROM laps WHERE session_id = @sessionId AND NOT is_deleted)::int,
                (SELECT count(*) FROM track_status_events WHERE session_id = @sessionId AND status_code = '4')::int,
                (SELECT count(*) FROM track_status_events WHERE session_id = @sessionId AND status_code = '5')::int,
                (SELECT count(*) FROM track_status_events WHERE session_id = @sessionId AND status_code = '6')::int,
                fl.driver_code, fl.lap_time_ms::bigint,
                ts.driver_code, ts.top_speed,
                w.peak_temp, coalesce(w.rained, false)
            FROM sessions s
            LEFT JOIN LATERAL (
                SELECT driver_code, lap_time_ms FROM laps
                WHERE session_id = @sessionId AND lap_time_ms IS NOT NULL AND NOT is_deleted
                ORDER BY lap_time_ms ASC LIMIT 1
            ) fl ON true
            LEFT JOIN LATERAL (
                SELECT driver_code, max(speed_kmh) AS top_speed FROM telemetry_samples
                WHERE session_id = @sessionId AND speed_kmh IS NOT NULL
                GROUP BY driver_code ORDER BY max(speed_kmh) DESC LIMIT 1
            ) ts ON true
            LEFT JOIN LATERAL (
                SELECT max(track_temp_c) AS peak_temp, bool_or(rainfall) AS rained
                FROM weather_samples WHERE session_id = @sessionId
            ) w ON true
            WHERE s.session_id = @sessionId
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var facts = new SessionFactsResponse(
            SessionId: sessionId,
            CircuitName: reader.GetString(0),
            Country: GetNullableString(reader, 1),
            DriverCount: reader.GetInt32(2),
            TotalLaps: reader.GetInt32(3),
            SafetyCarDeployments: reader.GetInt32(4),
            RedFlagCount: reader.GetInt32(5),
            VirtualSafetyCarDeployments: reader.GetInt32(6),
            FastestLapDriver: GetNullableString(reader, 7),
            FastestLapMs: GetNullableInt64(reader, 8),
            TopSpeedDriver: GetNullableString(reader, 9),
            TopSpeedKmh: GetNullableDouble(reader, 10),
            PeakTrackTempC: GetNullableDouble(reader, 11),
            RainObserved: reader.GetBoolean(12));

        _cache.Set(cacheKey, facts, MetadataCacheOptions);
        return facts;
    }

    public async Task<IReadOnlyList<LapSummary>?> GetLapsAsync(
        string sessionId,
        string driverCode,
        CancellationToken cancellationToken)
    {
        using var activity = StartStoreActivity("query_store.get_laps", sessionId, driverCode);

        const string sql = """
            WITH driver_check AS (
                SELECT EXISTS (
                    SELECT 1
                    FROM session_drivers
                    WHERE session_id = @sessionId
                      AND driver_code = upper(@driverCode)
                ) AS driver_exists
            ),
            lap_rows AS (
                SELECT
                    lap_id,
                    session_id,
                    driver_code,
                    lap_number,
                    lap_time_ms::bigint,
                    NULL::int AS position,
                    is_pit_out_lap,
                    is_pit_in_lap
                FROM laps
                WHERE session_id = @sessionId
                  AND driver_code = upper(@driverCode)
                  AND NOT is_deleted
            )
            SELECT
                driver_check.driver_exists,
                lap_rows.lap_id,
                lap_rows.session_id,
                lap_rows.driver_code,
                lap_rows.lap_number,
                lap_rows.lap_time_ms,
                lap_rows.position,
                lap_rows.is_pit_out_lap,
                lap_rows.is_pit_in_lap
            FROM driver_check
            LEFT JOIN lap_rows ON true
            ORDER BY lap_rows.lap_number
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("driverCode", driverCode);

        var laps = new List<LapSummary>();
        var driverExists = false;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            driverExists = reader.GetBoolean(0);
            if (reader.IsDBNull(1))
            {
                continue;
            }

            laps.Add(new LapSummary(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                GetNullableInt64(reader, 5),
                GetNullableInt32(reader, 6),
                reader.GetBoolean(7),
                reader.GetBoolean(8)));
        }

        return driverExists ? laps : null;
    }

    public async Task<ReplayMetadata?> GetReplayMetadataAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        using var activity = StartStoreActivity("query_store.get_replay_metadata", sessionId);

        var cacheKey = CacheKey("replay_metadata", sessionId);
        if (_cache.TryGetValue<ReplayMetadata>(cacheKey, out var cachedMetadata))
        {
            return cachedMetadata;
        }

        if (!await SessionExistsAsync(sessionId, cancellationToken))
        {
            return null;
        }

        var boundsTask = GetReplayBoundsAsync(sessionId, cancellationToken);
        var driversTask = GetReplayDriversAsync(sessionId, cancellationToken);
        var trackMapTask = GetTrackMapAsync(sessionId, cancellationToken);
        var overlaysTask = GetEventOverlayAvailabilityAsync(sessionId, cancellationToken);
        var weatherSummaryTask = GetWeatherSummaryAsync(sessionId, cancellationToken);
        var hasAlignedTelemetryTask = SessionHasAlignedTelemetryAsync(sessionId, cancellationToken);

        await Task.WhenAll(boundsTask, driversTask, trackMapTask, overlaysTask, weatherSummaryTask, hasAlignedTelemetryTask);

        var (startUtc, endUtc, startMs, endMs) = await boundsTask;
        var hasAlignedTelemetry = await hasAlignedTelemetryTask;

        var metadata = new ReplayMetadata(
            sessionId,
            startUtc,
            endUtc,
            Math.Max(0, endMs - startMs),
            await driversTask,
            startMs,
            endMs,
            ReplayChannels,
            ContextChannels,
            await trackMapTask,
            await overlaysTask,
            await weatherSummaryTask,
            30_000,
            [0.5, 1, 5],
            1,
            hasAlignedTelemetry ? 10 : null,
            hasAlignedTelemetry ? "aligned_telemetry_10hz" : "raw");

        _cache.Set(cacheKey, metadata, MetadataCacheOptions);
        return metadata;
    }

    private async Task<bool> SessionHasAlignedTelemetryAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync("aligned_telemetry_10hz", cancellationToken))
        {
            return false;
        }

        await using var command = _dataSource.CreateCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM aligned_telemetry_10hz
                WHERE session_id = @sessionId
            )
            """);
        command.Parameters.AddWithValue("sessionId", sessionId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    public async Task<LapTelemetryResponse?> GetLapTelemetryAsync(
        string sessionId,
        string driverCode,
        int lapNumber,
        IReadOnlyList<string> channels,
        int sampleEvery,
        int maxSamples,
        CancellationToken cancellationToken)
    {
        using var activity = StartStoreActivity("query_store.get_lap_telemetry", sessionId, driverCode, lapNumber);
        activity?.SetTag("race.query.sample_every", sampleEvery);
        activity?.SetTag("race.query.max_samples", maxSamples);

        var speedProjection = IncludesChannel(channels, "speed_kmh") ? "speed_kmh" : "NULL::double precision AS speed_kmh";
        var throttleProjection = IncludesChannel(channels, "throttle_pct") ? "throttle_pct" : "NULL::double precision AS throttle_pct";
        var brakeProjection = IncludesChannel(channels, "brake_pct") ? "brake_pct" : "NULL::double precision AS brake_pct";
        var gearProjection = IncludesChannel(channels, "gear") ? "gear" : "NULL::int AS gear";
        var rpmProjection = IncludesChannel(channels, "rpm") ? "rpm" : "NULL::double precision AS rpm";
        var drsProjection = IncludesChannel(channels, "drs") ? "drs" : "NULL::int AS drs";
        var scanLimit = checked(maxSamples * sampleEvery);

        var sql = $"""
            WITH lap_check AS (
                SELECT EXISTS (
                    SELECT 1
                    FROM laps
                    WHERE session_id = @sessionId
                      AND driver_code = upper(@driverCode)
                      AND lap_number = @lapNumber
                      AND NOT is_deleted
                ) AS lap_exists
            ),
            sampled AS (
                SELECT
                    sample_time_utc,
                    session_time_ms,
                    lap_time_ms,
                    {speedProjection},
                    {throttleProjection},
                    {brakeProjection},
                    {gearProjection},
                    {rpmProjection},
                    {drsProjection}
                FROM telemetry_samples
                WHERE session_id = @sessionId
                  AND driver_code = upper(@driverCode)
                  AND lap_number = @lapNumber
                ORDER BY lap_time_ms NULLS LAST, sample_time_utc
                LIMIT @scanLimit
            )
            SELECT
                lap_check.lap_exists,
                sampled.sample_time_utc,
                sampled.session_time_ms,
                sampled.lap_time_ms,
                sampled.speed_kmh,
                sampled.throttle_pct,
                sampled.brake_pct,
                sampled.gear,
                sampled.rpm,
                sampled.drs
            FROM lap_check
            LEFT JOIN sampled ON true
            ORDER BY sampled.lap_time_ms NULLS LAST, sampled.sample_time_utc
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("driverCode", driverCode);
        command.Parameters.AddWithValue("lapNumber", lapNumber);
        command.Parameters.AddWithValue("scanLimit", scanLimit);

        var samples = new List<TelemetrySample>();
        var lapExists = false;
        var rowIndex = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lapExists = reader.GetBoolean(0);
            if (!reader.IsDBNull(1))
            {
                if (rowIndex++ % sampleEvery != 0)
                {
                    continue;
                }

                samples.Add(ReadTelemetrySample(reader, offset: 1));
                if (samples.Count >= maxSamples)
                {
                    break;
                }
            }
        }

        if (!lapExists)
        {
            return null;
        }

        return new LapTelemetryResponse(sessionId, driverCode.ToUpperInvariant(), lapNumber, channels, samples);
    }

    public async Task<LapComparisonResponse?> CompareLapsAsync(
        string sessionId,
        string driverA,
        int lapA,
        string driverB,
        int lapB,
        IReadOnlyList<string> channels,
        int timeStepMs,
        CancellationToken cancellationToken)
    {
        using var activity = StartStoreActivity("query_store.compare_laps", sessionId, driverA, lapA);
        activity?.SetTag("race.query.driver_b", driverB.ToUpperInvariant());
        activity?.SetTag("race.query.lap_b", lapB);
        activity?.SetTag("race.query.time_step_ms", timeStepMs);

        if (!await RequestedLapsExistAsync(sessionId, driverA, lapA, driverB, lapB, cancellationToken))
        {
            return null;
        }

        var aTask = GetComparisonBucketsAsync(sessionId, driverA, lapA, timeStepMs, cancellationToken);
        var bTask = GetComparisonBucketsAsync(sessionId, driverB, lapB, timeStepMs, cancellationToken);
        var summaryTask = GetLapComparisonSummaryAsync(
            sessionId,
            driverA,
            lapA,
            driverB,
            lapB,
            cancellationToken);

        await Task.WhenAll(aTask, bTask, summaryTask);

        var a = await aTask;
        var b = await bTask;
        var points = a.Keys.Union(b.Keys)
            .Order()
            .Select(bucket =>
            {
                var av = a.GetValueOrDefault(bucket, EmptyTelemetryChannelValues);
                var bv = b.GetValueOrDefault(bucket, EmptyTelemetryChannelValues);
                return new LapComparisonPoint(
                    bucket,
                    av,
                    bv,
                    new TelemetryChannelValues(
                        Difference(av.SpeedKmh, bv.SpeedKmh),
                        Difference(av.ThrottlePct, bv.ThrottlePct),
                        Difference(av.BrakePct, bv.BrakePct),
                        Difference(av.Rpm, bv.Rpm),
                        DifferenceInt(av.Gear, bv.Gear)));
            })
            .ToArray();

        return new LapComparisonResponse(
            sessionId,
            driverA.ToUpperInvariant(),
            lapA,
            driverB.ToUpperInvariant(),
            lapB,
            timeStepMs,
            channels,
            points,
            await summaryTask);
    }

    public async Task<LapStoryResponse?> GetLapStoryAsync(
        string sessionId,
        string driverCode,
        int lapNumber,
        CancellationToken cancellationToken)
    {
        using var activity = StartStoreActivity("query_store.get_lap_story", sessionId, driverCode, lapNumber);

        const string sql = """
            SELECT
                lap_time_ms::bigint,
                sector_1_ms::bigint,
                sector_2_ms::bigint,
                sector_3_ms::bigint,
                compound,
                tyre_life,
                max_speed_kmh,
                avg_speed_kmh,
                avg_throttle_pct,
                avg_brake_pct,
                telemetry_samples::int
            FROM lap_summaries
            WHERE session_id = @sessionId
              AND driver_code = upper(@driverCode)
              AND lap_number = @lapNumber
              AND NOT is_deleted
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("driverCode", driverCode);
        command.Parameters.AddWithValue("lapNumber", lapNumber);

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var lapTimeMs = GetNullableInt64(reader, 0);
        var sectorTimes = new long?[]
        {
            GetNullableInt64(reader, 1),
            GetNullableInt64(reader, 2),
            GetNullableInt64(reader, 3)
        };
        var compound = GetNullableString(reader, 4);
        var tyreLife = GetNullableInt32(reader, 5);
        var peakSpeed = GetNullableDouble(reader, 6);
        var avgSpeed = GetNullableDouble(reader, 7);
        var avgThrottle = GetNullableDouble(reader, 8);
        var avgBrake = GetNullableDouble(reader, 9);
        var samples = reader.GetInt32(10);

        return new LapStoryResponse(
            sessionId,
            driverCode.ToUpperInvariant(),
            lapNumber,
            lapTimeMs,
            sectorTimes,
            compound,
            tyreLife,
            peakSpeed,
            avgSpeed,
            avgThrottle,
            avgBrake,
            samples,
            BuildLapInsights(lapTimeMs, sectorTimes, compound, tyreLife, peakSpeed, avgSpeed, avgThrottle, samples));
    }

    public async Task<LapBrakingZonesResponse?> GetLapBrakingZonesAsync(
        string sessionId,
        string driverCode,
        int lapNumber,
        int brakeThresholdPct,
        int minimumDurationMs,
        CancellationToken cancellationToken)
    {
        using var activity = StartStoreActivity("query_store.get_lap_braking_zones", sessionId, driverCode, lapNumber);
        activity?.SetTag("race.query.brake_threshold_pct", brakeThresholdPct);
        activity?.SetTag("race.query.minimum_duration_ms", minimumDurationMs);

        const string sql = """
            WITH lap_check AS (
                SELECT EXISTS (
                    SELECT 1
                    FROM laps
                    WHERE session_id = @sessionId
                      AND driver_code = upper(@driverCode)
                      AND lap_number = @lapNumber
                      AND NOT is_deleted
                ) AS lap_exists
            ),
            ordered AS (
                SELECT
                    sample_time_utc,
                    lap_time_ms::bigint AS lap_time_ms,
                    speed_kmh,
                    brake_pct,
                    brake_pct >= @brakeThresholdPct AS is_braking,
                    row_number() OVER (ORDER BY lap_time_ms NULLS LAST, sample_time_utc)
                    - row_number() OVER (PARTITION BY brake_pct >= @brakeThresholdPct ORDER BY lap_time_ms NULLS LAST, sample_time_utc) AS group_id
                FROM telemetry_samples
                WHERE session_id = @sessionId
                  AND driver_code = upper(@driverCode)
                  AND lap_number = @lapNumber
                  AND lap_time_ms IS NOT NULL
            ),
            zones AS (
                SELECT
                    min(sample_time_utc) AS start_sample_time_utc,
                    min(lap_time_ms) AS start_lap_time_ms,
                    max(lap_time_ms) AS end_lap_time_ms,
                    greatest(max(lap_time_ms) - min(lap_time_ms), 0)::bigint AS duration_ms,
                    (array_agg(speed_kmh ORDER BY lap_time_ms, sample_time_utc))[1] AS entry_speed_kmh,
                    min(speed_kmh) AS minimum_speed_kmh,
                    (array_agg(speed_kmh ORDER BY lap_time_ms DESC, sample_time_utc DESC))[1] AS exit_speed_kmh,
                    max(brake_pct) AS max_brake_pct
                FROM ordered
                WHERE is_braking
                GROUP BY group_id
                HAVING greatest(max(lap_time_ms) - min(lap_time_ms), 0) >= @minimumDurationMs
            ),
            zones_with_position AS (
                SELECT
                    zones.*,
                    p.x,
                    p.y
                FROM zones
                LEFT JOIN position_samples p
                    ON p.session_id = @sessionId
                    AND p.driver_code = upper(@driverCode)
                    AND p.sample_time_utc = zones.start_sample_time_utc
            ),
            zone_rows AS (
                SELECT
                    row_number() OVER (ORDER BY z.start_lap_time_ms)::int AS zone_index,
                    z.start_lap_time_ms,
                    z.end_lap_time_ms,
                    z.duration_ms,
                    z.entry_speed_kmh,
                    z.minimum_speed_kmh,
                    z.exit_speed_kmh,
                    z.max_brake_pct,
                    marker.marker_number,
                    marker.marker_letter,
                    marker.distance_to_corner
                FROM zones_with_position z
                LEFT JOIN LATERAL (
                    SELECT
                        cm.marker_number,
                        cm.marker_letter,
                        sqrt(power(cm.x - z.x, 2) + power(cm.y - z.y, 2)) AS distance_to_corner
                    FROM circuit_markers cm
                    WHERE cm.session_id = @sessionId
                      AND cm.marker_type = 'corner'
                      AND z.x IS NOT NULL
                      AND z.y IS NOT NULL
                    ORDER BY distance_to_corner
                    LIMIT 1
                ) marker ON true
            )
            SELECT
                lap_check.lap_exists,
                zone_rows.zone_index,
                zone_rows.start_lap_time_ms,
                zone_rows.end_lap_time_ms,
                zone_rows.duration_ms,
                zone_rows.entry_speed_kmh,
                zone_rows.minimum_speed_kmh,
                zone_rows.exit_speed_kmh,
                zone_rows.max_brake_pct,
                zone_rows.marker_number,
                zone_rows.marker_letter,
                zone_rows.distance_to_corner
            FROM lap_check
            LEFT JOIN zone_rows ON true
            ORDER BY zone_rows.start_lap_time_ms NULLS LAST
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("driverCode", driverCode);
        command.Parameters.AddWithValue("lapNumber", lapNumber);
        command.Parameters.AddWithValue("brakeThresholdPct", brakeThresholdPct);
        command.Parameters.AddWithValue("minimumDurationMs", minimumDurationMs);

        var zones = new List<LapBrakingZone>();
        var lapExists = false;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lapExists = reader.GetBoolean(0);
            if (reader.IsDBNull(1))
            {
                continue;
            }

            var markerNumber = GetNullableInt32(reader, 9);
            zones.Add(new LapBrakingZone(
                reader.GetInt32(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                GetNullableDouble(reader, 5),
                GetNullableDouble(reader, 6),
                GetNullableDouble(reader, 7),
                GetNullableDouble(reader, 8),
                FormatCornerLabel(sessionId, markerNumber, GetNullableString(reader, 10)),
                GetNullableDouble(reader, 11)));
        }

        return lapExists
            ? new LapBrakingZonesResponse(sessionId, driverCode.ToUpperInvariant(), lapNumber, brakeThresholdPct, minimumDurationMs, zones)
            : null;
    }

    public async Task<LapComparisonStoryResponse?> CompareLapsStoryAsync(
        string sessionId,
        string driverA,
        int lapA,
        string driverB,
        int lapB,
        int segmentCount,
        CancellationToken cancellationToken)
    {
        using var activity = StartStoreActivity("query_store.compare_laps_story", sessionId, driverA, lapA);
        activity?.SetTag("race.query.driver_b", driverB.ToUpperInvariant());
        activity?.SetTag("race.query.lap_b", lapB);
        activity?.SetTag("race.query.segment_count", segmentCount);

        if (!await RequestedLapsExistAsync(sessionId, driverA, lapA, driverB, lapB, cancellationToken))
        {
            return null;
        }

        var summaryTask = GetLapComparisonSummaryAsync(sessionId, driverA, lapA, driverB, lapB, cancellationToken);
        var segmentsTask = GetLapComparisonSegmentsAsync(sessionId, driverA, lapA, driverB, lapB, segmentCount, cancellationToken);

        await Task.WhenAll(summaryTask, segmentsTask);
        var summary = await summaryTask;
        var segments = await segmentsTask;

        return new LapComparisonStoryResponse(
            sessionId,
            driverA.ToUpperInvariant(),
            lapA,
            driverB.ToUpperInvariant(),
            lapB,
            summary.LapTimeDeltaMs,
            summary.SectorDeltasMs,
            summary.MaxSpeedDeltaKmh,
            summary.AvgSpeedDeltaKmh,
            segments,
            BuildComparisonInsights(driverA, driverB, summary, segments));
    }

    public async Task<RaceStoryResponse?> GetRaceStoryAsync(
        string sessionId,
        int raceControlLimit,
        CancellationToken cancellationToken)
    {
        using var activity = StartStoreActivity("query_store.get_race_story", sessionId);
        activity?.SetTag("race.query.race_control_limit", raceControlLimit);

        var sessionTask = GetSessionSummaryAsync(sessionId, cancellationToken);
        var weatherTask = GetWeatherSummaryAsync(sessionId, cancellationToken);
        var stintsTask = GetRaceStintsAsync(sessionId, cancellationToken);
        var pitStopsTask = GetPitStopsAsync(sessionId, cancellationToken);
        var trackStatusTask = GetTrackStatusPeriodsAsync(sessionId, cancellationToken);
        var raceControlTask = GetRaceControlHighlightsAsync(sessionId, raceControlLimit, cancellationToken);

        await Task.WhenAll(sessionTask, weatherTask, stintsTask, pitStopsTask, trackStatusTask, raceControlTask);

        var session = await sessionTask;
        if (session is null)
        {
            return null;
        }

        var weather = await weatherTask;
        var stints = await stintsTask;
        var pitStops = await pitStopsTask;
        var trackStatus = await trackStatusTask;
        var raceControl = await raceControlTask;

        return new RaceStoryResponse(
            sessionId,
            session,
            weather,
            stints,
            pitStops,
            trackStatus,
            raceControl,
            BuildRaceInsights(session, weather, stints, pitStops, trackStatus, raceControl));
    }
}
