using System.Data;
using System.Diagnostics;
using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using RaceTelemetry.Contracts;

namespace RaceTelemetry.Data;

public sealed class PostgresTelemetryQueryStore(NpgsqlDataSource dataSource) : IF1TelemetryQueryStore
{
    private static readonly ActivitySource ActivitySource = new("RaceTelemetry.Data");

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
                SELECT session_id, count(*)::int AS lap_count
                FROM laps
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

        await using var command = dataSource.CreateCommand(sql);
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

        await using var command = dataSource.CreateCommand(sql);
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

        return sessionExists ? drivers : null;
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

        await using var command = dataSource.CreateCommand(sql);
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

        if (!await SessionExistsAsync(sessionId, cancellationToken))
        {
            return null;
        }

        var boundsTask = GetReplayBoundsAsync(sessionId, cancellationToken);
        var driversTask = GetReplayDriversAsync(sessionId, cancellationToken);
        var trackMapTask = GetTrackMapAsync(sessionId, cancellationToken);
        var overlaysTask = GetEventOverlayAvailabilityAsync(sessionId, cancellationToken);
        var weatherSummaryTask = GetWeatherSummaryAsync(sessionId, cancellationToken);

        await Task.WhenAll(boundsTask, driversTask, trackMapTask, overlaysTask, weatherSummaryTask);

        var (startUtc, endUtc, startMs, endMs) = await boundsTask;

        return new ReplayMetadata(
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
            [0.25, 0.5, 1, 2, 5, 10, 20],
            1);
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
                    session_time_ms,
                    lap_time_ms,
                    speed_kmh,
                    throttle_pct,
                    brake_pct,
                    gear,
                    rpm,
                    drs,
                    row_number() OVER (ORDER BY lap_time_ms NULLS LAST, sample_time_utc) AS rn
                FROM telemetry_samples
                WHERE session_id = @sessionId
                  AND driver_code = upper(@driverCode)
                  AND lap_number = @lapNumber
            ),
            sampled AS (
                SELECT
                    sample_time_utc,
                    session_time_ms,
                    lap_time_ms,
                    speed_kmh,
                    throttle_pct,
                    brake_pct,
                    gear,
                    rpm,
                    drs
                FROM ordered
                WHERE ((rn - 1) % @sampleEvery) = 0
                ORDER BY lap_time_ms NULLS LAST, sample_time_utc
                LIMIT @maxSamples
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

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("driverCode", driverCode);
        command.Parameters.AddWithValue("lapNumber", lapNumber);
        command.Parameters.AddWithValue("sampleEvery", sampleEvery);
        command.Parameters.AddWithValue("maxSamples", maxSamples);

        var samples = new List<TelemetrySample>();
        var lapExists = false;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lapExists = reader.GetBoolean(0);
            if (!reader.IsDBNull(1))
            {
                samples.Add(ReadTelemetrySample(reader, offset: 1));
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

        await using var command = dataSource.CreateCommand(sql);
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

        await using var command = dataSource.CreateCommand(sql);
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

    public async Task<TelemetryAggregateResponse?> AggregateTelemetryAsync(
        string sessionId,
        TelemetryAggregateRequest request,
        CancellationToken cancellationToken)
    {
        using var activity = StartStoreActivity("query_store.aggregate_telemetry", sessionId);
        activity?.SetTag("race.query.limit", request.Limit);
        activity?.SetTag("race.query.time_bucket_ms", request.TimeBucketMs);

        if (!await SessionExistsAsync(sessionId, cancellationToken))
        {
            return null;
        }

        var groupBy = NormalizeValues(request.GroupBy, AggregateGroupBy, ["driver"]);
        var metrics = NormalizeValues(request.Metrics, AggregateMetrics, ["sample_count", "avg_speed_kmh"]);
        var includeTrackStatus = groupBy.Contains("track_status", StringComparer.OrdinalIgnoreCase)
            || request.Filters?.TrackStatus is { Count: > 0 };
        var includeTimeBucket = groupBy.Contains("time_bucket", StringComparer.OrdinalIgnoreCase);
        var timeBucketMs = request.TimeBucketMs ?? 60_000;
        var limit = request.Limit ?? 500;

        var groupExpressions = BuildAggregateGroupExpressions(groupBy, includeTimeBucket);
        var selectGroupColumns = string.Join(",\n                ", groupExpressions.Select(expression => $"{expression.Sql} AS {expression.Alias}"));
        var groupColumns = string.Join(", ", groupExpressions.Select(expression => expression.Sql));
        var orderColumns = string.Join(", ", groupExpressions.Select(expression => expression.Alias));
        var statusProjection = includeTrackStatus ? "coalesce(status.status_name, 'unknown')" : "'not_requested'";

        var sql = $"""
            WITH base AS (
                SELECT
                    t.driver_code,
                    t.lap_number,
                    l.stint_number,
                    l.compound,
                    {statusProjection} AS track_status,
                    CASE
                        WHEN @timeBucketMs::int IS NULL THEN NULL::bigint
                        ELSE (floor(t.session_time_ms::numeric / @timeBucketMs::int) * @timeBucketMs::int)::bigint
                    END AS bucket_start_ms,
                    CASE
                        WHEN @timeBucketMs::int IS NULL THEN NULL::bigint
                        ELSE ((floor(t.session_time_ms::numeric / @timeBucketMs::int) + 1) * @timeBucketMs::int)::bigint
                    END AS bucket_end_ms,
                    t.speed_kmh,
                    t.throttle_pct,
                    t.brake_pct,
                    t.drs,
                    least(greatest(coalesce(
                        lead(t.session_time_ms) OVER (
                            PARTITION BY t.driver_code
                            ORDER BY t.session_time_ms NULLS LAST, t.sample_time_utc
                        ) - t.session_time_ms,
                        0), 0), 2000)::bigint AS sample_duration_ms
                FROM telemetry_samples t
                LEFT JOIN laps l
                    ON l.session_id = t.session_id
                    AND l.driver_code = t.driver_code
                    AND l.lap_number = t.lap_number
                {(includeTrackStatus ? """
                LEFT JOIN LATERAL (
                    SELECT status_name
                    FROM track_status_periods tsp
                    WHERE tsp.session_id = t.session_id
                      AND t.session_time_ms >= tsp.start_time_ms
                      AND (tsp.end_time_ms IS NULL OR t.session_time_ms < tsp.end_time_ms)
                    ORDER BY tsp.start_time_ms DESC
                    LIMIT 1
                ) status ON true
                """ : "")}
                WHERE t.session_id = @sessionId
                  AND t.session_time_ms IS NOT NULL
                  AND (@drivers::text[] IS NULL OR t.driver_code = ANY(@drivers::text[]))
                  AND (@lapFrom::int IS NULL OR t.lap_number >= @lapFrom::int)
                  AND (@lapTo::int IS NULL OR t.lap_number <= @lapTo::int)
                  AND (@compound::text[] IS NULL OR l.compound = ANY(@compound::text[]))
                  AND (@excludePitLaps::boolean IS NOT TRUE OR (NOT coalesce(l.is_pit_in_lap, false) AND NOT coalesce(l.is_pit_out_lap, false)))
            ),
            filtered AS (
                SELECT *
                FROM base
                WHERE (@trackStatus::text[] IS NULL OR track_status = ANY(@trackStatus::text[]))
            )
            SELECT
                {selectGroupColumns},
                count(*)::int AS sample_count,
                avg(speed_kmh) AS avg_speed_kmh,
                max(speed_kmh) AS max_speed_kmh,
                avg(throttle_pct) AS avg_throttle_pct,
                avg(brake_pct) AS avg_brake_pct,
                sum(CASE WHEN brake_pct >= 80 THEN sample_duration_ms ELSE 0 END)::bigint AS brake_time_ms,
                sum(CASE WHEN drs IS NOT NULL AND drs > 0 THEN sample_duration_ms ELSE 0 END)::bigint AS drs_active_time_ms,
                count(*) FILTER (WHERE throttle_pct <= 10 AND speed_kmh >= 150)::int AS throttle_lift_count,
                sum(CASE WHEN speed_kmh >= 300 THEN sample_duration_ms ELSE 0 END)::bigint AS high_speed_time_ms
            FROM filtered
            GROUP BY {groupColumns}
            ORDER BY {orderColumns}
            LIMIT @limit
            """;

        await using var command = dataSource.CreateCommand(sql);
        AddAnalyticalCommonParameters(command, sessionId, request.Drivers, request.Filters);
        var timeBucketParameter = command.Parameters.Add("timeBucketMs", NpgsqlDbType.Integer);
        timeBucketParameter.Value = includeTimeBucket ? timeBucketMs : DBNull.Value;
        command.Parameters.AddWithValue("limit", limit);

        var items = new List<TelemetryAggregateItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var values = ReadAggregateGroupValues(reader, groupExpressions);
            var offset = groupExpressions.Count;
            items.Add(new TelemetryAggregateItem(
                values.DriverCode,
                values.LapNumber,
                values.StintNumber,
                values.Compound,
                values.TrackStatus,
                values.BucketStartMs,
                values.BucketEndMs,
                reader.GetInt32(offset),
                GetNullableDouble(reader, offset + 1),
                GetNullableDouble(reader, offset + 2),
                GetNullableDouble(reader, offset + 3),
                GetNullableDouble(reader, offset + 4),
                GetNullableInt64(reader, offset + 5),
                GetNullableInt64(reader, offset + 6),
                GetNullableInt32(reader, offset + 7),
                GetNullableInt64(reader, offset + 8)));
        }

        return new TelemetryAggregateResponse(sessionId, groupBy, metrics, items);
    }

    public async Task<TelemetryWindowResponse?> DetectTelemetryWindowsAsync(
        string sessionId,
        TelemetryWindowRequest request,
        CancellationToken cancellationToken)
    {
        using var activity = StartStoreActivity("query_store.detect_telemetry_windows", sessionId);
        activity?.SetTag("race.query.event_type", request.EventType);
        activity?.SetTag("race.query.limit", request.Limit);

        if (!await SessionExistsAsync(sessionId, cancellationToken))
        {
            return null;
        }

        var eventType = NormalizeRequiredValue(request.EventType, TelemetryWindowTypes);
        var minimumDurationMs = request.MinimumDurationMs ?? 250;
        var limit = request.Limit ?? 1_000;
        var condition = GetWindowCondition(eventType);

        var sql = $"""
            WITH ordered AS (
                SELECT
                    t.sample_time_utc,
                    t.driver_code,
                    t.lap_number,
                    t.session_time_ms::bigint AS session_time_ms,
                    t.lap_time_ms::bigint AS lap_time_ms,
                    t.speed_kmh,
                    t.throttle_pct,
                    t.brake_pct,
                    {condition} AS is_event,
                    row_number() OVER (
                        PARTITION BY t.driver_code
                        ORDER BY t.session_time_ms NULLS LAST, t.sample_time_utc
                    )
                    - row_number() OVER (
                        PARTITION BY t.driver_code, {condition}
                        ORDER BY t.session_time_ms NULLS LAST, t.sample_time_utc
                    ) AS group_id
                FROM telemetry_samples t
                WHERE t.session_id = @sessionId
                  AND t.session_time_ms IS NOT NULL
                  AND (@drivers::text[] IS NULL OR t.driver_code = ANY(@drivers::text[]))
                  AND (@lapFrom::int IS NULL OR t.lap_number >= @lapFrom::int)
                  AND (@lapTo::int IS NULL OR t.lap_number <= @lapTo::int)
            ),
            windows AS (
                SELECT
                    min(sample_time_utc) AS start_sample_time_utc,
                    driver_code,
                    min(lap_number) AS lap_number,
                    min(session_time_ms) AS start_session_time_ms,
                    max(session_time_ms) AS end_session_time_ms,
                    min(lap_time_ms) AS start_lap_time_ms,
                    max(lap_time_ms) AS end_lap_time_ms,
                    greatest(max(session_time_ms) - min(session_time_ms), 0)::bigint AS duration_ms,
                    (array_agg(speed_kmh ORDER BY session_time_ms, sample_time_utc))[1] AS entry_speed_kmh,
                    min(speed_kmh) AS minimum_speed_kmh,
                    max(speed_kmh) AS max_speed_kmh,
                    (array_agg(speed_kmh ORDER BY session_time_ms DESC, sample_time_utc DESC))[1] AS exit_speed_kmh,
                    max(brake_pct) AS max_brake_pct,
                    avg(throttle_pct) AS avg_throttle_pct
                FROM ordered
                WHERE is_event
                GROUP BY driver_code, group_id
                HAVING greatest(max(session_time_ms) - min(session_time_ms), 0) >= @minimumDurationMs
            ),
            windows_with_position AS (
                SELECT
                    w.*,
                    p.x,
                    p.y
                FROM windows w
                LEFT JOIN position_samples p
                    ON p.session_id = @sessionId
                    AND p.driver_code = w.driver_code
                    AND p.sample_time_utc = w.start_sample_time_utc
            )
            SELECT
                w.driver_code,
                w.lap_number,
                w.start_session_time_ms,
                w.end_session_time_ms,
                w.start_lap_time_ms,
                w.end_lap_time_ms,
                w.duration_ms,
                marker.marker_number,
                marker.marker_letter,
                marker.distance_to_corner,
                w.entry_speed_kmh,
                w.minimum_speed_kmh,
                w.max_speed_kmh,
                w.exit_speed_kmh,
                w.max_brake_pct,
                w.avg_throttle_pct
            FROM windows_with_position w
            LEFT JOIN LATERAL (
                SELECT
                    cm.marker_number,
                    cm.marker_letter,
                    sqrt(power(cm.x - w.x, 2) + power(cm.y - w.y, 2)) AS distance_to_corner
                FROM circuit_markers cm
                WHERE cm.session_id = @sessionId
                  AND cm.marker_type = 'corner'
                  AND @includeNearestCorner
                  AND w.x IS NOT NULL
                  AND w.y IS NOT NULL
                ORDER BY distance_to_corner
                LIMIT 1
            ) marker ON true
            ORDER BY w.start_session_time_ms, w.driver_code
            LIMIT @limit
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        AddNullable(command, "drivers", NpgsqlDbType.Array | NpgsqlDbType.Text, request.Drivers is { Count: > 0 } ? request.Drivers.Select(driver => driver.ToUpperInvariant()).ToArray() : null);
        AddNullable(command, "lapFrom", NpgsqlDbType.Integer, request.LapRange?.From);
        AddNullable(command, "lapTo", NpgsqlDbType.Integer, request.LapRange?.To);
        command.Parameters.AddWithValue("minimumDurationMs", minimumDurationMs);
        command.Parameters.AddWithValue("includeNearestCorner", request.IncludeNearestCorner ?? true);
        command.Parameters.AddWithValue("limit", limit);

        var items = new List<TelemetryWindowItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var markerNumber = GetNullableInt32(reader, 7);
            items.Add(new TelemetryWindowItem(
                reader.GetString(0),
                GetNullableInt32(reader, 1),
                GetNullableInt64(reader, 2),
                GetNullableInt64(reader, 3),
                GetNullableInt64(reader, 4),
                GetNullableInt64(reader, 5),
                reader.GetInt64(6),
                FormatCornerLabel(sessionId, markerNumber, GetNullableString(reader, 8)),
                GetNullableDouble(reader, 9),
                new TelemetryWindowSummary(
                    GetNullableDouble(reader, 10),
                    GetNullableDouble(reader, 11),
                    GetNullableDouble(reader, 12),
                    GetNullableDouble(reader, 13),
                    GetNullableDouble(reader, 14),
                    GetNullableDouble(reader, 15))));
        }

        return new TelemetryWindowResponse(sessionId, eventType, minimumDurationMs, items);
    }

    public async Task<StintAnalysisResponse?> AnalyzeDriverStintsAsync(
        string sessionId,
        StintAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        using var activity = StartStoreActivity("query_store.analyze_driver_stints", sessionId);
        activity?.SetTag("race.query.minimum_laps", request.MinimumLaps);

        if (!await SessionExistsAsync(sessionId, cancellationToken))
        {
            return null;
        }

        var metrics = NormalizeValues(request.Metrics, StintMetrics, ["lap_time_slope_ms_per_lap", "best_lap_time_ms", "average_lap_time_ms"]);
        var minimumLaps = request.MinimumLaps ?? 3;

        const string sql = """
            WITH filtered AS (
                SELECT
                    driver_code,
                    stint_number,
                    compound,
                    lap_number,
                    tyre_life,
                    lap_time_ms::bigint AS lap_time_ms
                FROM laps
                WHERE session_id = @sessionId
                  AND stint_number IS NOT NULL
                  AND lap_time_ms IS NOT NULL
                  AND NOT is_deleted
                  AND (@drivers::text[] IS NULL OR driver_code = ANY(@drivers::text[]))
                  AND (@compound::text[] IS NULL OR compound = ANY(@compound::text[]))
                  AND (@excludePitLaps::boolean IS NOT TRUE OR (NOT is_pit_in_lap AND NOT is_pit_out_lap))
            )
            SELECT
                driver_code,
                stint_number,
                compound,
                min(lap_number) AS first_lap_number,
                max(lap_number) AS last_lap_number,
                count(*)::int AS laps,
                min(tyre_life),
                max(tyre_life),
                round(avg(lap_time_ms))::bigint,
                min(lap_time_ms)::bigint,
                max(lap_time_ms)::bigint,
                regr_slope(lap_time_ms::double precision, lap_number::double precision)
            FROM filtered
            GROUP BY driver_code, stint_number, compound
            HAVING count(*) >= @minimumLaps
            ORDER BY driver_code, stint_number
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        AddNullable(command, "drivers", NpgsqlDbType.Array | NpgsqlDbType.Text, request.Drivers is { Count: > 0 } ? request.Drivers.Select(driver => driver.ToUpperInvariant()).ToArray() : null);
        AddNullable(command, "compound", NpgsqlDbType.Array | NpgsqlDbType.Text, request.Compound is { Count: > 0 } ? request.Compound.Select(compound => compound.ToUpperInvariant()).ToArray() : null);
        command.Parameters.AddWithValue("excludePitLaps", request.ExcludePitLaps ?? true);
        command.Parameters.AddWithValue("minimumLaps", minimumLaps);

        var items = new List<DriverStintAnalysisItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var slope = GetNullableDouble(reader, 11);
            items.Add(new DriverStintAnalysisItem(
                reader.GetString(0),
                reader.GetInt32(1),
                GetNullableString(reader, 2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                GetNullableInt32(reader, 6),
                GetNullableInt32(reader, 7),
                GetNullableInt64(reader, 8),
                GetNullableInt64(reader, 9),
                GetNullableInt64(reader, 10),
                slope,
                BuildStintInsights(slope, GetNullableString(reader, 2), reader.GetInt32(5))));
        }

        return new StintAnalysisResponse(sessionId, metrics, items);
    }

    public async Task<PitStopAnalysisResponse?> AnalyzePitStopsAsync(
        string sessionId,
        PitStopAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        using var activity = StartStoreActivity("query_store.analyze_pit_stops", sessionId);
        activity?.SetTag("race.query.limit", request.Limit);

        if (!await SessionExistsAsync(sessionId, cancellationToken))
        {
            return null;
        }

        var nearbyLapWindow = request.NearbyLapWindow ?? 3;
        var limit = request.Limit ?? 200;
        const string sql = """
            WITH pit_laps AS (
                SELECT
                    driver_code,
                    lap_number,
                    CASE
                        WHEN is_pit_in_lap AND is_pit_out_lap THEN 'pit_in_out'
                        WHEN is_pit_in_lap THEN 'pit_in'
                        ELSE 'pit_out'
                    END AS kind,
                    stint_number,
                    compound,
                    tyre_life,
                    lap_time_ms::bigint AS lap_time_ms
                FROM laps
                WHERE session_id = @sessionId
                  AND NOT is_deleted
                  AND (is_pit_in_lap OR is_pit_out_lap)
                  AND (@drivers::text[] IS NULL OR driver_code = ANY(@drivers::text[]))
            ),
            pit_with_session_time AS (
                SELECT
                    p.*,
                    min(t.session_time_ms)::bigint AS session_time_ms
                FROM pit_laps p
                LEFT JOIN telemetry_samples t
                    ON t.session_id = @sessionId
                    AND t.driver_code = p.driver_code
                    AND t.lap_number = p.lap_number
                GROUP BY p.driver_code, p.lap_number, p.kind, p.stint_number, p.compound, p.tyre_life, p.lap_time_ms
            )
            SELECT
                p.driver_code,
                p.lap_number,
                p.kind,
                p.stint_number,
                p.compound,
                p.tyre_life,
                p.lap_time_ms,
                p.session_time_ms,
                round(avg(b.lap_time_ms))::bigint AS nearby_baseline_lap_time_ms,
                (p.lap_time_ms - round(avg(b.lap_time_ms))::bigint) AS estimated_loss_ms
            FROM pit_with_session_time p
            LEFT JOIN laps b
                ON b.session_id = @sessionId
                AND b.driver_code = p.driver_code
                AND b.lap_time_ms IS NOT NULL
                AND NOT b.is_deleted
                AND NOT b.is_pit_in_lap
                AND NOT b.is_pit_out_lap
                AND abs(b.lap_number - p.lap_number) BETWEEN 1 AND @nearbyLapWindow
            GROUP BY p.driver_code, p.lap_number, p.kind, p.stint_number, p.compound, p.tyre_life, p.lap_time_ms, p.session_time_ms
            ORDER BY coalesce(p.session_time_ms, 9223372036854775807), p.driver_code, p.lap_number
            LIMIT @limit
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        AddNullable(command, "drivers", NpgsqlDbType.Array | NpgsqlDbType.Text, request.Drivers is { Count: > 0 } ? request.Drivers.Select(driver => driver.ToUpperInvariant()).ToArray() : null);
        command.Parameters.AddWithValue("nearbyLapWindow", nearbyLapWindow);
        command.Parameters.AddWithValue("limit", limit);

        var items = new List<PitStopAnalysisItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var estimatedLossMs = GetNullableInt64(reader, 9);
            items.Add(new PitStopAnalysisItem(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                GetNullableInt32(reader, 3),
                GetNullableString(reader, 4),
                GetNullableInt32(reader, 5),
                GetNullableInt64(reader, 6),
                GetNullableInt64(reader, 7),
                GetNullableInt64(reader, 8),
                estimatedLossMs,
                BuildPitStopInsights(estimatedLossMs)));
        }

        return new PitStopAnalysisResponse(sessionId, items);
    }

    public async Task<WeatherTrendResponse?> GetWeatherTrendAsync(
        string sessionId,
        WeatherTrendRequest request,
        CancellationToken cancellationToken)
    {
        using var activity = StartStoreActivity("query_store.weather_trend", sessionId);
        activity?.SetTag("race.query.from_ms", request.FromMs);
        activity?.SetTag("race.query.duration_ms", request.DurationMs);

        if (!await SessionExistsAsync(sessionId, cancellationToken))
        {
            return null;
        }

        const string sql = """
            WITH filtered AS (
                SELECT *
                FROM weather_samples
                WHERE session_id = @sessionId
                  AND (@fromMs::bigint IS NULL OR session_time_ms >= @fromMs::bigint)
                  AND (@toMs::bigint IS NULL OR session_time_ms < @toMs::bigint)
            )
            SELECT
                min(session_time_ms)::bigint,
                max(session_time_ms)::bigint,
                count(*)::int,
                (array_agg(air_temp_c ORDER BY session_time_ms))[1],
                (array_agg(air_temp_c ORDER BY session_time_ms DESC))[1],
                min(air_temp_c),
                max(air_temp_c),
                avg(air_temp_c),
                (array_agg(track_temp_c ORDER BY session_time_ms))[1],
                (array_agg(track_temp_c ORDER BY session_time_ms DESC))[1],
                min(track_temp_c),
                max(track_temp_c),
                avg(track_temp_c),
                (array_agg(humidity_pct ORDER BY session_time_ms))[1],
                (array_agg(humidity_pct ORDER BY session_time_ms DESC))[1],
                min(humidity_pct),
                max(humidity_pct),
                avg(humidity_pct),
                (array_agg(pressure_mbar ORDER BY session_time_ms))[1],
                (array_agg(pressure_mbar ORDER BY session_time_ms DESC))[1],
                min(pressure_mbar),
                max(pressure_mbar),
                avg(pressure_mbar),
                (array_agg(wind_speed_mps ORDER BY session_time_ms))[1],
                (array_agg(wind_speed_mps ORDER BY session_time_ms DESC))[1],
                min(wind_speed_mps),
                max(wind_speed_mps),
                avg(wind_speed_mps),
                bool_or(coalesce(rainfall, false))
            FROM filtered
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        var toMs = request.FromMs is not null && request.DurationMs is not null
            ? request.FromMs + request.DurationMs
            : null;
        AddNullable(command, "fromMs", NpgsqlDbType.Bigint, request.FromMs);
        AddNullable(command, "toMs", NpgsqlDbType.Bigint, toMs);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        var airTemp = ReadWeatherMetric(reader, 3);
        var trackTemp = ReadWeatherMetric(reader, 8);
        var humidity = ReadWeatherMetric(reader, 13);
        var pressure = ReadWeatherMetric(reader, 18);
        var windSpeed = ReadWeatherMetric(reader, 23);
        return new WeatherTrendResponse(
            sessionId,
            GetNullableInt64(reader, 0),
            GetNullableInt64(reader, 1),
            reader.GetInt32(2),
            airTemp,
            trackTemp,
            humidity,
            pressure,
            windSpeed,
            GetNullableBoolean(reader, 28) == true,
            BuildWeatherTrendInsights(trackTemp, airTemp, GetNullableBoolean(reader, 28) == true));
    }

    public async Task<RaceControlTimelineResponse?> GetRaceControlTimelineAsync(
        string sessionId,
        RaceControlTimelineRequest request,
        CancellationToken cancellationToken)
    {
        using var activity = StartStoreActivity("query_store.race_control_timeline", sessionId);
        activity?.SetTag("race.query.limit", request.Limit);

        if (!await SessionExistsAsync(sessionId, cancellationToken))
        {
            return null;
        }

        var limit = request.Limit ?? 200;
        const string sql = """
            SELECT
                session_time_ms,
                lap_number,
                category,
                message,
                status,
                flag,
                scope,
                sector,
                racing_number
            FROM race_control_event_index
            WHERE session_id = @sessionId
              AND (@categories::text[] IS NULL OR category = ANY(@categories::text[]))
              AND (@flags::text[] IS NULL OR flag = ANY(@flags::text[]))
              AND (@statuses::text[] IS NULL OR status = ANY(@statuses::text[]))
              AND (@scopes::text[] IS NULL OR scope = ANY(@scopes::text[]))
              AND (@racingNumbers::int[] IS NULL OR racing_number = ANY(@racingNumbers::int[]))
              AND (@lapFrom::int IS NULL OR lap_number >= @lapFrom::int)
              AND (@lapTo::int IS NULL OR lap_number <= @lapTo::int)
              AND (@search::text IS NULL OR search_text LIKE ('%' || lower(@search::text) || '%'))
            ORDER BY session_time_ms NULLS LAST, race_control_message_id
            LIMIT @limit
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        AddNullable(command, "categories", NpgsqlDbType.Array | NpgsqlDbType.Text, request.Categories is { Count: > 0 } ? request.Categories.ToArray() : null);
        AddNullable(command, "flags", NpgsqlDbType.Array | NpgsqlDbType.Text, request.Flags is { Count: > 0 } ? request.Flags.ToArray() : null);
        AddNullable(command, "statuses", NpgsqlDbType.Array | NpgsqlDbType.Text, request.Statuses is { Count: > 0 } ? request.Statuses.ToArray() : null);
        AddNullable(command, "scopes", NpgsqlDbType.Array | NpgsqlDbType.Text, request.Scopes is { Count: > 0 } ? request.Scopes.ToArray() : null);
        AddNullable(command, "racingNumbers", NpgsqlDbType.Array | NpgsqlDbType.Integer, request.RacingNumbers is { Count: > 0 } ? request.RacingNumbers.ToArray() : null);
        AddNullable(command, "lapFrom", NpgsqlDbType.Integer, request.LapRange?.From);
        AddNullable(command, "lapTo", NpgsqlDbType.Integer, request.LapRange?.To);
        AddNullable(command, "search", NpgsqlDbType.Text, string.IsNullOrWhiteSpace(request.Search) ? null : request.Search.Trim());
        command.Parameters.AddWithValue("limit", limit);

        var items = new List<RaceControlSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new RaceControlSummary(
                GetNullableInt64(reader, 0),
                GetNullableInt32(reader, 1),
                GetNullableString(reader, 2),
                reader.GetString(3),
                GetNullableString(reader, 4),
                GetNullableString(reader, 5),
                GetNullableString(reader, 6),
                GetNullableString(reader, 7),
                GetNullableInt32(reader, 8)));
        }

        return new RaceControlTimelineResponse(
            sessionId,
            items,
            BuildRaceControlBuckets(items.Select(item => item.Category)),
            BuildRaceControlBuckets(items.Select(item => item.Flag)),
            BuildRaceControlBuckets(items.Select(item => item.Status)),
            [new AnalysisInsight("race_control", $"Matched {items.Count} race-control message(s).", items.Count, "messages")]);
    }

    public async Task<CircuitContextResponse?> GetCircuitContextAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        using var activity = StartStoreActivity("query_store.circuit_context", sessionId);

        if (!await SessionExistsAsync(sessionId, cancellationToken))
        {
            return null;
        }

        const string metadataSql = """
            SELECT rotation_degrees, source
            FROM circuit_metadata
            WHERE session_id = @sessionId
            """;
        await using var metadataCommand = dataSource.CreateCommand(metadataSql);
        metadataCommand.Parameters.AddWithValue("sessionId", sessionId);
        double? rotationDegrees = null;
        string? source = null;
        await using (var metadataReader = await metadataCommand.ExecuteReaderAsync(cancellationToken))
        {
            if (await metadataReader.ReadAsync(cancellationToken))
            {
                rotationDegrees = GetNullableDouble(metadataReader, 0);
                source = GetNullableString(metadataReader, 1);
            }
        }

        const string markersSql = """
            SELECT marker_type, marker_number, marker_letter, x, y, angle_degrees, distance_m
            FROM circuit_markers
            WHERE session_id = @sessionId
            ORDER BY marker_type, marker_number NULLS LAST, marker_letter NULLS LAST
            """;
        await using var markersCommand = dataSource.CreateCommand(markersSql);
        markersCommand.Parameters.AddWithValue("sessionId", sessionId);

        var corners = new List<CircuitMarker>();
        var marshalLights = new List<CircuitMarker>();
        var marshalSectors = new List<CircuitMarker>();
        await using var markersReader = await markersCommand.ExecuteReaderAsync(cancellationToken);
        while (await markersReader.ReadAsync(cancellationToken))
        {
            var markerType = markersReader.GetString(0);
            var marker = new CircuitMarker(
                markerType,
                GetNullableInt32(markersReader, 1),
                GetNullableString(markersReader, 2),
                markersReader.GetDouble(3),
                markersReader.GetDouble(4),
                GetNullableDouble(markersReader, 5),
                GetNullableDouble(markersReader, 6));

            switch (markerType)
            {
                case "corner":
                    corners.Add(marker);
                    break;
                case "marshal_light":
                    marshalLights.Add(marker);
                    break;
                case "marshal_sector":
                    marshalSectors.Add(marker);
                    break;
            }
        }

        return new CircuitContextResponse(
            sessionId,
            rotationDegrees,
            source,
            corners,
            marshalLights,
            marshalSectors,
            [
                new AnalysisInsight("corners", $"Loaded {corners.Count} corner marker(s).", corners.Count, "markers"),
                new AnalysisInsight("marshal_lights", $"Loaded {marshalLights.Count} marshal light marker(s).", marshalLights.Count, "markers"),
                new AnalysisInsight("marshal_sectors", $"Loaded {marshalSectors.Count} marshal sector marker(s).", marshalSectors.Count, "markers")
            ]);
    }

    public async Task<ReplayChunkResponse?> GetReplayChunkAsync(
        string sessionId,
        long fromMs,
        long durationMs,
        IReadOnlyList<string>? drivers,
        IReadOnlyList<string> channels,
        int sampleEvery,
        CancellationToken cancellationToken)
    {
        using var activity = StartStoreActivity("query_store.get_replay_chunk", sessionId);
        activity?.SetTag("race.query.from_ms", fromMs);
        activity?.SetTag("race.query.duration_ms", durationMs);
        activity?.SetTag("race.query.sample_every", sampleEvery);

        var sessionExistsTask = SessionExistsAsync(sessionId, cancellationToken);
        var driversExistTask = drivers is { Count: > 0 }
            ? DriversExistAsync(sessionId, drivers, cancellationToken)
            : Task.FromResult(true);

        await Task.WhenAll(sessionExistsTask, driversExistTask);

        if (!await sessionExistsTask || !await driversExistTask)
        {
            return null;
        }

        const string sql = """
            WITH telemetry AS (
                SELECT
                    t.driver_code,
                    t.session_time_ms,
                    t.lap_number,
                    t.speed_kmh,
                    t.throttle_pct,
                    t.brake_pct,
                    t.gear,
                    t.rpm,
                    t.drs,
                    p.x,
                    p.y,
                    p.z,
                    row_number() OVER (
                        PARTITION BY t.driver_code
                        ORDER BY t.session_time_ms NULLS LAST, t.sample_time_utc
                    ) AS rn
                FROM telemetry_samples t
                LEFT JOIN position_samples p
                    ON p.session_id = t.session_id
                    AND p.driver_code = t.driver_code
                    AND p.sample_time_utc = t.sample_time_utc
                WHERE t.session_id = @sessionId
                  AND t.session_time_ms >= @fromMs
                  AND t.session_time_ms < (@fromMs + @durationMs)
              AND (@drivers::text[] IS NULL OR t.driver_code = ANY(@drivers::text[]))
            )
            SELECT
                driver_code,
                session_time_ms,
                lap_number,
                speed_kmh,
                throttle_pct,
                brake_pct,
                gear,
                rpm,
                drs,
                x,
                y,
                z
            FROM telemetry
            WHERE ((rn - 1) % @sampleEvery) = 0
            ORDER BY driver_code, session_time_ms
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("fromMs", fromMs);
        command.Parameters.AddWithValue("durationMs", durationMs);
        command.Parameters.AddWithValue("sampleEvery", sampleEvery);
        AddNullable(command, "drivers", NpgsqlDbType.Array | NpgsqlDbType.Text, drivers is { Count: > 0 } ? drivers.Select(d => d.ToUpperInvariant()).ToArray() : null);

        var chunks = new Dictionary<string, List<ReplaySample>>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var driverCode = reader.GetString(0);
            if (!chunks.TryGetValue(driverCode, out var samples))
            {
                samples = [];
                chunks[driverCode] = samples;
            }

            samples.Add(new ReplaySample(
                GetNullableInt64(reader, 1),
                GetNullableInt32(reader, 2),
                GetNullableDouble(reader, 3),
                GetNullableDouble(reader, 4),
                GetNullableDouble(reader, 5),
                GetNullableInt32(reader, 6),
                GetNullableDouble(reader, 7),
                GetNullableInt32(reader, 8),
                GetNullableDouble(reader, 9),
                GetNullableDouble(reader, 10),
                GetNullableDouble(reader, 11)));
        }

        return new ReplayChunkResponse(
            sessionId,
            fromMs,
            durationMs,
            fromMs + durationMs,
            channels,
            chunks
                .OrderBy(pair => pair.Key)
                .Select(pair => new ReplayDriverChunk(pair.Key, pair.Value))
                .ToArray());
    }

    public async Task<ReplayContextResponse?> GetReplayContextAsync(
        string sessionId,
        long fromMs,
        long durationMs,
        bool includeWeather,
        bool includeTrackStatus,
        bool includeRaceControl,
        CancellationToken cancellationToken)
    {
        using var activity = StartStoreActivity("query_store.get_replay_context", sessionId);
        activity?.SetTag("race.query.from_ms", fromMs);
        activity?.SetTag("race.query.duration_ms", durationMs);
        activity?.SetTag("race.query.include_weather", includeWeather);
        activity?.SetTag("race.query.include_track_status", includeTrackStatus);
        activity?.SetTag("race.query.include_race_control", includeRaceControl);

        if (!await SessionExistsAsync(sessionId, cancellationToken))
        {
            return null;
        }

        var weatherTask = includeWeather
            ? GetWeatherSamplesAsync(sessionId, fromMs, durationMs, cancellationToken)
            : Task.FromResult<IReadOnlyList<WeatherSample>>([]);
        var trackStatusTask = includeTrackStatus
            ? GetTrackStatusEventsAsync(sessionId, fromMs, durationMs, cancellationToken)
            : Task.FromResult<IReadOnlyList<TrackStatusEvent>>([]);
        var raceControlTask = includeRaceControl
            ? GetRaceControlMessagesAsync(sessionId, fromMs, durationMs, cancellationToken)
            : Task.FromResult<IReadOnlyList<RaceControlMessage>>([]);

        await Task.WhenAll(weatherTask, trackStatusTask, raceControlTask);

        return new ReplayContextResponse(
            sessionId,
            fromMs,
            durationMs,
            await weatherTask,
            await trackStatusTask,
            await raceControlTask);
    }

    public async Task<TelemetryEventSearchResponse?> SearchTelemetryEventsAsync(
        string sessionId,
        TelemetryEventSearchRequest request,
        CancellationToken cancellationToken)
    {
        using var activity = StartStoreActivity("query_store.search_telemetry_events", sessionId);
        activity?.SetTag("race.query.from_ms", request.FromMs);
        activity?.SetTag("race.query.duration_ms", request.DurationMs);
        activity?.SetTag("race.query.limit", request.Limit);

        const string sql = """
            WITH session_check AS (
                SELECT EXISTS (
                    SELECT 1
                    FROM sessions
                    WHERE session_id = @sessionId
                ) AS session_exists
            ),
            events AS (
                SELECT
                    sample_time_utc,
                    driver_code,
                    lap_number,
                    session_time_ms,
                    lap_time_ms,
                    speed_kmh,
                    throttle_pct,
                    brake_pct,
                    drs,
                    event_type
                FROM telemetry_event_candidates
                WHERE session_id = @sessionId
                  AND event_type IS NOT NULL
                  AND (@eventTypes::text[] IS NULL OR event_type = ANY(@eventTypes::text[]))
                  AND (@drivers::text[] IS NULL OR driver_code = ANY(@drivers::text[]))
                  AND (@fromMs::bigint IS NULL OR session_time_ms >= @fromMs::bigint)
                  AND (@toMs::bigint IS NULL OR session_time_ms < @toMs::bigint)
                ORDER BY session_time_ms, driver_code
                LIMIT @limit
            )
            SELECT
                session_check.session_exists,
                events.sample_time_utc,
                events.driver_code,
                events.lap_number,
                events.session_time_ms,
                events.lap_time_ms,
                events.speed_kmh,
                events.throttle_pct,
                events.brake_pct,
                events.drs,
                events.event_type
            FROM session_check
            LEFT JOIN events ON true
            ORDER BY events.session_time_ms, events.driver_code
            """;

        var fromMs = request.FromMs;
        var toMs = request.FromMs is not null && request.DurationMs is not null
            ? request.FromMs + request.DurationMs
            : null;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        AddNullable(command, "eventTypes", NpgsqlDbType.Array | NpgsqlDbType.Text, request.EventTypes is { Count: > 0 } ? request.EventTypes.ToArray() : null);
        AddNullable(command, "drivers", NpgsqlDbType.Array | NpgsqlDbType.Text, request.Drivers is { Count: > 0 } ? request.Drivers.Select(d => d.ToUpperInvariant()).ToArray() : null);
        AddNullable(command, "fromMs", NpgsqlDbType.Bigint, fromMs);
        AddNullable(command, "toMs", NpgsqlDbType.Bigint, toMs);
        command.Parameters.AddWithValue("limit", request.Limit ?? 500);

        var items = new List<TelemetryEventCandidate>();
        var sessionExists = false;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sessionExists = reader.GetBoolean(0);
            if (reader.IsDBNull(1))
            {
                continue;
            }

            items.Add(new TelemetryEventCandidate(
                GetNullableDateTimeOffset(reader, 1) ?? DateTimeOffset.UnixEpoch,
                reader.GetString(2),
                GetNullableInt32(reader, 3),
                GetNullableInt64(reader, 4),
                GetNullableInt64(reader, 5),
                GetNullableDouble(reader, 6),
                GetNullableDouble(reader, 7),
                GetNullableDouble(reader, 8),
                GetNullableInt32(reader, 9),
                reader.GetString(10)));
        }

        return sessionExists ? new TelemetryEventSearchResponse(sessionId, items) : null;
    }

    private static IReadOnlyList<string> NormalizeValues(
        IReadOnlyList<string>? values,
        IReadOnlySet<string> allowed,
        IReadOnlyList<string> defaults)
    {
        if (values is not { Count: > 0 })
        {
            return defaults;
        }

        var normalized = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray();

        if (normalized.Length == 0)
        {
            return defaults;
        }

        var invalid = normalized.Where(value => !allowed.Contains(value)).ToArray();
        if (invalid.Length > 0)
        {
            throw new ArgumentException($"Unsupported value(s): {string.Join(", ", invalid)}.");
        }

        return normalized;
    }

    private static string NormalizeRequiredValue(string value, IReadOnlySet<string> allowed)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (!allowed.Contains(normalized))
        {
            throw new ArgumentException($"Unsupported value: {value}.");
        }

        return normalized;
    }

    private static IReadOnlyList<AggregateGroupExpression> BuildAggregateGroupExpressions(
        IReadOnlyList<string> groupBy,
        bool includeTimeBucket)
    {
        var expressions = new List<AggregateGroupExpression>();
        foreach (var group in groupBy)
        {
            switch (group)
            {
                case "driver":
                    expressions.Add(new AggregateGroupExpression("driver", "driver_code", "driver_code"));
                    break;
                case "lap":
                    expressions.Add(new AggregateGroupExpression("lap", "lap_number", "lap_number"));
                    break;
                case "stint":
                    expressions.Add(new AggregateGroupExpression("stint", "stint_number", "stint_number"));
                    break;
                case "compound":
                    expressions.Add(new AggregateGroupExpression("compound", "compound", "compound"));
                    break;
                case "track_status":
                    expressions.Add(new AggregateGroupExpression("track_status", "track_status", "track_status"));
                    break;
                case "time_bucket":
                    expressions.Add(new AggregateGroupExpression("bucket_start", "bucket_start_ms", "bucket_start_ms"));
                    expressions.Add(new AggregateGroupExpression("bucket_end", "bucket_end_ms", "bucket_end_ms"));
                    break;
            }
        }

        if (includeTimeBucket && expressions.All(expression => expression.Key != "bucket_start"))
        {
            expressions.Add(new AggregateGroupExpression("bucket_start", "bucket_start_ms", "bucket_start_ms"));
            expressions.Add(new AggregateGroupExpression("bucket_end", "bucket_end_ms", "bucket_end_ms"));
        }

        return expressions;
    }

    private static AggregateGroupValues ReadAggregateGroupValues(
        IDataRecord reader,
        IReadOnlyList<AggregateGroupExpression> expressions)
    {
        string? driverCode = null;
        int? lapNumber = null;
        int? stintNumber = null;
        string? compound = null;
        string? trackStatus = null;
        long? bucketStartMs = null;
        long? bucketEndMs = null;

        for (var index = 0; index < expressions.Count; index++)
        {
            switch (expressions[index].Key)
            {
                case "driver":
                    driverCode = GetNullableString(reader, index);
                    break;
                case "lap":
                    lapNumber = GetNullableInt32(reader, index);
                    break;
                case "stint":
                    stintNumber = GetNullableInt32(reader, index);
                    break;
                case "compound":
                    compound = GetNullableString(reader, index);
                    break;
                case "track_status":
                    trackStatus = GetNullableString(reader, index);
                    break;
                case "bucket_start":
                    bucketStartMs = GetNullableInt64(reader, index);
                    break;
                case "bucket_end":
                    bucketEndMs = GetNullableInt64(reader, index);
                    break;
            }
        }

        return new AggregateGroupValues(driverCode, lapNumber, stintNumber, compound, trackStatus, bucketStartMs, bucketEndMs);
    }

    private static string GetWindowCondition(string eventType) =>
        eventType switch
        {
            "drs_active" => "t.drs IS NOT NULL AND t.drs > 0",
            "hard_braking" => "t.brake_pct >= 80",
            "throttle_lift" => "t.throttle_pct <= 10 AND t.speed_kmh >= 150",
            "high_speed" => "t.speed_kmh >= 300",
            _ => throw new ArgumentException($"Unsupported event type: {eventType}.")
        };

    private static void AddAnalyticalCommonParameters(
        NpgsqlCommand command,
        string sessionId,
        IReadOnlyList<string>? drivers,
        TelemetryAggregateFilters? filters)
    {
        command.Parameters.AddWithValue("sessionId", sessionId);
        AddNullable(command, "drivers", NpgsqlDbType.Array | NpgsqlDbType.Text, drivers is { Count: > 0 } ? drivers.Select(driver => driver.ToUpperInvariant()).ToArray() : null);
        AddNullable(command, "lapFrom", NpgsqlDbType.Integer, filters?.LapRange?.From);
        AddNullable(command, "lapTo", NpgsqlDbType.Integer, filters?.LapRange?.To);
        AddNullable(command, "compound", NpgsqlDbType.Array | NpgsqlDbType.Text, filters?.Compound is { Count: > 0 } ? filters.Compound.Select(compound => compound.ToUpperInvariant()).ToArray() : null);
        AddNullable(command, "excludePitLaps", NpgsqlDbType.Boolean, filters?.ExcludePitLaps);
        AddNullable(command, "trackStatus", NpgsqlDbType.Array | NpgsqlDbType.Text, filters?.TrackStatus is { Count: > 0 } ? filters.TrackStatus.Select(status => status.ToLowerInvariant()).ToArray() : null);
    }

    private static IReadOnlyList<AnalysisInsight> BuildStintInsights(
        double? lapTimeSlopeMsPerLap,
        string? compound,
        int laps)
    {
        var insights = new List<AnalysisInsight>();
        if (lapTimeSlopeMsPerLap is not null)
        {
            var direction = lapTimeSlopeMsPerLap > 0 ? "increased" : "improved";
            var absoluteSlope = Math.Abs(lapTimeSlopeMsPerLap.Value).ToString("0.0", CultureInfo.InvariantCulture);
            insights.Add(new AnalysisInsight(
                "lap_time_trend",
                $"Lap time trend {direction} by {absoluteSlope} ms per lap over {laps} lap(s).",
                lapTimeSlopeMsPerLap,
                "ms/lap"));
        }

        if (!string.IsNullOrWhiteSpace(compound))
        {
            insights.Add(new AnalysisInsight("compound", $"Stint compound was {compound}."));
        }

        return insights;
    }

    private static IReadOnlyList<AnalysisInsight> BuildPitStopInsights(long? estimatedLossMs)
    {
        if (estimatedLossMs is null)
        {
            return [new AnalysisInsight("pit_loss", "No nearby non-pit lap baseline was available for this pit marker.")];
        }

        return
        [
            new AnalysisInsight(
                "pit_loss",
                $"Pit marker lap was {estimatedLossMs.Value} ms slower than nearby non-pit laps.",
                estimatedLossMs.Value,
                "ms")
        ];
    }

    private static WeatherTrendMetric ReadWeatherMetric(IDataRecord reader, int offset)
    {
        var first = GetNullableDouble(reader, offset);
        var last = GetNullableDouble(reader, offset + 1);
        return new WeatherTrendMetric(
            first,
            last,
            GetNullableDouble(reader, offset + 2),
            GetNullableDouble(reader, offset + 3),
            GetNullableDouble(reader, offset + 4),
            first is null || last is null ? null : last - first);
    }

    private static IReadOnlyList<AnalysisInsight> BuildWeatherTrendInsights(
        WeatherTrendMetric trackTemp,
        WeatherTrendMetric airTemp,
        bool rainfallObserved)
    {
        var insights = new List<AnalysisInsight>();
        if (trackTemp.Delta is not null)
        {
            insights.Add(new AnalysisInsight(
                "track_temperature_trend",
                $"Track temperature changed by {trackTemp.Delta.Value.ToString("0.0", CultureInfo.InvariantCulture)} C over the selected window.",
                trackTemp.Delta,
                "C"));
        }

        if (airTemp.Delta is not null)
        {
            insights.Add(new AnalysisInsight(
                "air_temperature_trend",
                $"Air temperature changed by {airTemp.Delta.Value.ToString("0.0", CultureInfo.InvariantCulture)} C over the selected window.",
                airTemp.Delta,
                "C"));
        }

        insights.Add(new AnalysisInsight(
            "rainfall",
            rainfallObserved ? "Rainfall was observed in the selected weather samples." : "No rainfall was observed in the selected weather samples.",
            rainfallObserved ? 1 : 0,
            "boolean"));

        return insights;
    }

    private static IReadOnlyList<RaceControlBucket> BuildRaceControlBuckets(IEnumerable<string?> values) =>
        values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new RaceControlBucket(group.Key, group.Count()))
            .OrderByDescending(bucket => bucket.Count)
            .ThenBy(bucket => bucket.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private async Task<bool> SessionExistsAsync(string sessionId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT EXISTS (SELECT 1 FROM sessions WHERE session_id = @sessionId)";
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private async Task<bool> DriverExistsAsync(string sessionId, string driverCode, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM session_drivers
                WHERE session_id = @sessionId
                  AND driver_code = upper(@driverCode)
            )
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("driverCode", driverCode);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private async Task<bool> DriversExistAsync(
        string sessionId,
        IReadOnlyList<string> drivers,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT count(*)::int
            FROM session_drivers
            WHERE session_id = @sessionId
              AND driver_code = ANY(@drivers)
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("drivers", drivers.Select(driver => driver.ToUpperInvariant()).Distinct().ToArray());
        var count = (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
        return count == drivers.Select(driver => driver.ToUpperInvariant()).Distinct().Count();
    }

    private async Task<bool> LapExistsAsync(
        string sessionId,
        string driverCode,
        int lapNumber,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM laps
                WHERE session_id = @sessionId
                  AND driver_code = upper(@driverCode)
                  AND lap_number = @lapNumber
                  AND NOT is_deleted
            )
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("driverCode", driverCode);
        command.Parameters.AddWithValue("lapNumber", lapNumber);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private async Task<bool> RequestedLapsExistAsync(
        string sessionId,
        string driverA,
        int lapA,
        string driverB,
        int lapB,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT count(*)::int
            FROM laps
            WHERE session_id = @sessionId
              AND NOT is_deleted
              AND (
                (driver_code = upper(@driverA) AND lap_number = @lapA)
                OR (driver_code = upper(@driverB) AND lap_number = @lapB)
              )
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("driverA", driverA);
        command.Parameters.AddWithValue("lapA", lapA);
        command.Parameters.AddWithValue("driverB", driverB);
        command.Parameters.AddWithValue("lapB", lapB);

        var expectedCount = driverA.Equals(driverB, StringComparison.OrdinalIgnoreCase) && lapA == lapB
            ? 1
            : 2;
        var count = (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
        return count == expectedCount;
    }

    private async Task<(DateTimeOffset? StartUtc, DateTimeOffset? EndUtc, long StartMs, long EndMs)> GetReplayBoundsAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                min(sample_time_utc),
                max(sample_time_utc),
                coalesce(min(session_time_ms), 0)::bigint,
                coalesce(max(session_time_ms), 0)::bigint
            FROM (
                SELECT sample_time_utc, session_time_ms
                FROM telemetry_samples
                WHERE session_id = @sessionId
                UNION ALL
                SELECT sample_time_utc, NULL::bigint AS session_time_ms
                FROM position_samples
                WHERE session_id = @sessionId
            ) samples
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (null, null, 0, 0);
        }

        return (
            GetNullableDateTimeOffset(reader, 0),
            GetNullableDateTimeOffset(reader, 1),
            reader.GetInt64(2),
            reader.GetInt64(3));
    }

    private async Task<IReadOnlyList<string>> GetReplayDriversAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT DISTINCT driver_code
            FROM telemetry_samples
            WHERE session_id = @sessionId
            ORDER BY driver_code
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        var drivers = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            drivers.Add(reader.GetString(0));
        }

        return drivers;
    }

    private async Task<TrackMapMetadata?> GetTrackMapAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        const string metadataSql = """
            SELECT rotation_degrees
            FROM circuit_metadata
            WHERE session_id = @sessionId
            """;

        double? rotation;
        await using (var command = dataSource.CreateCommand(metadataSql))
        {
            command.Parameters.AddWithValue("sessionId", sessionId);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            rotation = value is null or DBNull ? null : (double)value;
        }

        const string markersSql = """
            SELECT
                marker_type,
                marker_number,
                marker_letter,
                x,
                y,
                angle_degrees,
                distance_m
            FROM circuit_markers
            WHERE session_id = @sessionId
            ORDER BY marker_type, marker_number NULLS LAST, marker_letter NULLS LAST
            """;

        await using var markersCommand = dataSource.CreateCommand(markersSql);
        markersCommand.Parameters.AddWithValue("sessionId", sessionId);
        var markers = new List<CircuitMarker>();
        await using var reader = await markersCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            markers.Add(new CircuitMarker(
                reader.GetString(0),
                GetNullableInt32(reader, 1),
                GetNullableString(reader, 2),
                reader.GetDouble(3),
                reader.GetDouble(4),
                GetNullableDouble(reader, 5),
                GetNullableDouble(reader, 6)));
        }

        return rotation is null && markers.Count == 0
            ? null
            : new TrackMapMetadata(rotation, "position_samples", markers);
    }

    private async Task<EventOverlayAvailability> GetEventOverlayAvailabilityAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                EXISTS (SELECT 1 FROM track_status_events WHERE session_id = @sessionId),
                EXISTS (SELECT 1 FROM race_control_messages WHERE session_id = @sessionId),
                EXISTS (SELECT 1 FROM weather_samples WHERE session_id = @sessionId)
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new EventOverlayAvailability(reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2));
    }

    private async Task<WeatherSummary?> GetWeatherSummaryAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                min_air_temp_c,
                max_air_temp_c,
                min_track_temp_c,
                max_track_temp_c,
                rainfall_observed
            FROM session_weather_summary
            WHERE session_id = @sessionId
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new WeatherSummary(
            GetNullableDouble(reader, 0),
            GetNullableDouble(reader, 1),
            GetNullableDouble(reader, 2),
            GetNullableDouble(reader, 3),
            reader.GetBoolean(4));
    }

    private async Task<Dictionary<long, TelemetryChannelValues>> GetComparisonBucketsAsync(
        string sessionId,
        string driverCode,
        int lapNumber,
        int timeStepMs,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                (floor(lap_time_ms::numeric / @timeStepMs) * @timeStepMs)::bigint AS bucket_ms,
                avg(speed_kmh),
                avg(throttle_pct),
                avg(brake_pct),
                avg(rpm),
                round(avg(gear))::int
            FROM telemetry_samples
            WHERE session_id = @sessionId
              AND driver_code = upper(@driverCode)
              AND lap_number = @lapNumber
              AND lap_time_ms IS NOT NULL
            GROUP BY bucket_ms
            ORDER BY bucket_ms
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("driverCode", driverCode);
        command.Parameters.AddWithValue("lapNumber", lapNumber);
        command.Parameters.AddWithValue("timeStepMs", timeStepMs);

        var buckets = new Dictionary<long, TelemetryChannelValues>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            buckets[reader.GetInt64(0)] = new TelemetryChannelValues(
                GetNullableDouble(reader, 1),
                GetNullableDouble(reader, 2),
                GetNullableDouble(reader, 3),
                GetNullableDouble(reader, 4),
                GetNullableInt32(reader, 5));
        }

        return buckets;
    }

    private async Task<LapComparisonSummary> GetLapComparisonSummaryAsync(
        string sessionId,
        string driverA,
        int lapA,
        string driverB,
        int lapB,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH selected AS (
                SELECT
                    driver_code,
                    lap_number,
                    lap_time_ms,
                    sector_1_ms,
                    sector_2_ms,
                    sector_3_ms
                FROM laps
                WHERE session_id = @sessionId
                  AND (
                    (driver_code = upper(@driverA) AND lap_number = @lapA)
                    OR (driver_code = upper(@driverB) AND lap_number = @lapB)
                  )
            ),
            speeds AS (
                SELECT
                    driver_code,
                    lap_number,
                    max(speed_kmh) AS max_speed_kmh,
                    avg(speed_kmh) AS avg_speed_kmh
                FROM telemetry_samples
                WHERE session_id = @sessionId
                  AND (
                    (driver_code = upper(@driverA) AND lap_number = @lapA)
                    OR (driver_code = upper(@driverB) AND lap_number = @lapB)
                  )
                GROUP BY driver_code, lap_number
            )
            SELECT
                a.lap_time_ms::bigint - b.lap_time_ms::bigint,
                a.sector_1_ms::bigint - b.sector_1_ms::bigint,
                a.sector_2_ms::bigint - b.sector_2_ms::bigint,
                a.sector_3_ms::bigint - b.sector_3_ms::bigint,
                sa.max_speed_kmh - sb.max_speed_kmh,
                sa.avg_speed_kmh - sb.avg_speed_kmh
            FROM selected a
            JOIN selected b ON true
            LEFT JOIN speeds sa
                ON sa.driver_code = a.driver_code
                AND sa.lap_number = a.lap_number
            LEFT JOIN speeds sb
                ON sb.driver_code = b.driver_code
                AND sb.lap_number = b.lap_number
            WHERE a.driver_code = upper(@driverA)
              AND a.lap_number = @lapA
              AND b.driver_code = upper(@driverB)
              AND b.lap_number = @lapB
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("driverA", driverA);
        command.Parameters.AddWithValue("lapA", lapA);
        command.Parameters.AddWithValue("driverB", driverB);
        command.Parameters.AddWithValue("lapB", lapB);

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new LapComparisonSummary(null, [null, null, null], null, null);
        }

        return new LapComparisonSummary(
            GetNullableInt64(reader, 0),
            [GetNullableInt64(reader, 1), GetNullableInt64(reader, 2), GetNullableInt64(reader, 3)],
            GetNullableDouble(reader, 4),
            GetNullableDouble(reader, 5));
    }

    private async Task<IReadOnlyList<LapComparisonSegment>> GetLapComparisonSegmentsAsync(
        string sessionId,
        string driverA,
        int lapA,
        string driverB,
        int lapB,
        int segmentCount,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH selected_samples AS (
                SELECT
                    driver_code,
                    lap_time_ms::bigint AS lap_time_ms,
                    speed_kmh,
                    throttle_pct,
                    brake_pct
                FROM telemetry_samples
                WHERE session_id = @sessionId
                  AND lap_time_ms IS NOT NULL
                  AND (
                    (driver_code = upper(@driverA) AND lap_number = @lapA)
                    OR (driver_code = upper(@driverB) AND lap_number = @lapB)
                  )
            ),
            bounds AS (
                SELECT greatest(coalesce(max(lap_time_ms), 0), 1)::bigint AS max_lap_time_ms
                FROM selected_samples
            ),
            segment_bounds AS (
                SELECT
                    segment,
                    floor(((segment - 1)::numeric / @segmentCount) * bounds.max_lap_time_ms)::bigint AS start_lap_time_ms,
                    CASE
                        WHEN segment = @segmentCount THEN bounds.max_lap_time_ms
                        ELSE floor((segment::numeric / @segmentCount) * bounds.max_lap_time_ms)::bigint
                    END AS end_lap_time_ms,
                    bounds.max_lap_time_ms
                FROM generate_series(1, @segmentCount) AS segment
                CROSS JOIN bounds
            ),
            aggregates AS (
                SELECT
                    s.driver_code,
                    b.segment,
                    avg(s.speed_kmh) AS avg_speed_kmh,
                    avg(s.throttle_pct) AS avg_throttle_pct,
                    avg(s.brake_pct) AS avg_brake_pct
                FROM segment_bounds b
                LEFT JOIN selected_samples s
                    ON s.lap_time_ms >= b.start_lap_time_ms
                    AND s.lap_time_ms < CASE WHEN b.segment = @segmentCount THEN b.end_lap_time_ms + 1 ELSE b.end_lap_time_ms END
                GROUP BY s.driver_code, b.segment
            )
            SELECT
                b.segment::int,
                b.start_lap_time_ms,
                b.end_lap_time_ms,
                a.avg_speed_kmh - bb.avg_speed_kmh,
                a.avg_throttle_pct - bb.avg_throttle_pct,
                a.avg_brake_pct - bb.avg_brake_pct
            FROM segment_bounds b
            LEFT JOIN aggregates a
                ON a.segment = b.segment
                AND a.driver_code = upper(@driverA)
            LEFT JOIN aggregates bb
                ON bb.segment = b.segment
                AND bb.driver_code = upper(@driverB)
            ORDER BY b.segment
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("driverA", driverA);
        command.Parameters.AddWithValue("lapA", lapA);
        command.Parameters.AddWithValue("driverB", driverB);
        command.Parameters.AddWithValue("lapB", lapB);
        command.Parameters.AddWithValue("segmentCount", segmentCount);

        var segments = new List<LapComparisonSegment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var speedDelta = GetNullableDouble(reader, 3);
            segments.Add(new LapComparisonSegment(
                reader.GetInt32(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                speedDelta,
                GetNullableDouble(reader, 4),
                GetNullableDouble(reader, 5),
                speedDelta switch
                {
                    > 2 => "driver_a",
                    < -2 => "driver_b",
                    _ => "even"
                }));
        }

        return segments;
    }

    private async Task<SessionSummary?> GetSessionSummaryAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH driver_counts AS (
                SELECT session_id, count(*)::int AS driver_count
                FROM session_drivers
                WHERE session_id = @sessionId
                GROUP BY session_id
            ),
            lap_counts AS (
                SELECT session_id, count(*)::int AS lap_count
                FROM laps
                WHERE session_id = @sessionId
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
                coalesce(dc.driver_count, 0),
                coalesce(lc.lap_count, 0)
            FROM sessions s
            LEFT JOIN driver_counts dc ON dc.session_id = s.session_id
            LEFT JOIN lap_counts lc ON lc.session_id = s.session_id
            WHERE s.session_id = @sessionId
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SessionSummary(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            GetNullableString(reader, 4),
            GetNullableString(reader, 5),
            GetNullableDateTimeOffset(reader, 6),
            reader.GetInt32(7),
            reader.GetInt32(8));
    }

    private async Task<IReadOnlyList<RaceStintSummary>> GetRaceStintsAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                driver_code,
                stint_number,
                compound,
                first_lap_number,
                last_lap_number,
                laps::int,
                min_tyre_life,
                max_tyre_life,
                round(avg_lap_time_ms)::bigint,
                best_lap_time_ms::bigint,
                worst_lap_time_ms::bigint
            FROM driver_stint_summaries
            WHERE session_id = @sessionId
            ORDER BY driver_code, stint_number
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);

        var stints = new List<RaceStintSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            stints.Add(new RaceStintSummary(
                reader.GetString(0),
                reader.GetInt32(1),
                GetNullableString(reader, 2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                GetNullableInt32(reader, 6),
                GetNullableInt32(reader, 7),
                GetNullableInt64(reader, 8),
                GetNullableInt64(reader, 9),
                GetNullableInt64(reader, 10)));
        }

        return stints;
    }

    private async Task<IReadOnlyList<PitStopSummary>> GetPitStopsAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH pit_laps AS (
                SELECT
                    driver_code,
                    lap_number,
                    CASE
                        WHEN is_pit_in_lap AND is_pit_out_lap THEN 'pit_in_out'
                        WHEN is_pit_in_lap THEN 'pit_in'
                        ELSE 'pit_out'
                    END AS kind,
                    stint_number,
                    compound,
                    tyre_life,
                    lap_time_ms::bigint,
                    lap_start_utc
                FROM laps
                WHERE session_id = @sessionId
                  AND NOT is_deleted
                  AND (is_pit_in_lap OR is_pit_out_lap)
            )
            SELECT
                p.driver_code,
                p.lap_number,
                p.kind,
                p.stint_number,
                p.compound,
                p.tyre_life,
                p.lap_time_ms,
                min(t.session_time_ms)::bigint
            FROM pit_laps p
            LEFT JOIN telemetry_samples t
                ON t.session_id = @sessionId
                AND t.driver_code = p.driver_code
                AND t.lap_number = p.lap_number
            GROUP BY
                p.driver_code,
                p.lap_number,
                p.kind,
                p.stint_number,
                p.compound,
                p.tyre_life,
                p.lap_time_ms,
                p.lap_start_utc
            ORDER BY coalesce(min(t.session_time_ms), 9223372036854775807), p.driver_code, p.lap_number
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);

        var pitStops = new List<PitStopSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            pitStops.Add(new PitStopSummary(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                GetNullableInt32(reader, 3),
                GetNullableString(reader, 4),
                GetNullableInt32(reader, 5),
                GetNullableInt64(reader, 6),
                GetNullableInt64(reader, 7)));
        }

        return pitStops;
    }

    private async Task<IReadOnlyList<TrackStatusPeriodSummary>> GetTrackStatusPeriodsAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                start_time_ms,
                end_time_ms,
                status_code,
                status_name,
                message
            FROM track_status_periods
            WHERE session_id = @sessionId
            ORDER BY start_time_ms
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);

        var periods = new List<TrackStatusPeriodSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            periods.Add(new TrackStatusPeriodSummary(
                reader.GetInt64(0),
                GetNullableInt64(reader, 1),
                reader.GetString(2),
                reader.GetString(3),
                GetNullableString(reader, 4)));
        }

        return periods;
    }

    private async Task<IReadOnlyList<RaceControlSummary>> GetRaceControlHighlightsAsync(
        string sessionId,
        int limit,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                session_time_ms,
                lap_number,
                category,
                message,
                status,
                flag,
                scope,
                sector,
                racing_number
            FROM race_control_event_index
            WHERE session_id = @sessionId
            ORDER BY session_time_ms NULLS LAST, race_control_message_id
            LIMIT @limit
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("limit", limit);

        var messages = new List<RaceControlSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new RaceControlSummary(
                GetNullableInt64(reader, 0),
                GetNullableInt32(reader, 1),
                GetNullableString(reader, 2),
                reader.GetString(3),
                GetNullableString(reader, 4),
                GetNullableString(reader, 5),
                GetNullableString(reader, 6),
                GetNullableString(reader, 7),
                GetNullableInt32(reader, 8)));
        }

        return messages;
    }

    private async Task<IReadOnlyList<WeatherSample>> GetWeatherSamplesAsync(
        string sessionId,
        long fromMs,
        long durationMs,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                session_time_ms,
                air_temp_c,
                track_temp_c,
                humidity_pct,
                pressure_mbar,
                rainfall,
                wind_direction_deg,
                wind_speed_mps
            FROM weather_samples
            WHERE session_id = @sessionId
              AND session_time_ms >= @fromMs
              AND session_time_ms < (@fromMs + @durationMs)
            ORDER BY session_time_ms
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("fromMs", fromMs);
        command.Parameters.AddWithValue("durationMs", durationMs);

        var samples = new List<WeatherSample>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            samples.Add(new WeatherSample(
                reader.GetInt64(0),
                GetNullableDouble(reader, 1),
                GetNullableDouble(reader, 2),
                GetNullableDouble(reader, 3),
                GetNullableDouble(reader, 4),
                GetNullableBoolean(reader, 5),
                GetNullableInt32(reader, 6),
                GetNullableDouble(reader, 7)));
        }

        return samples;
    }

    private async Task<IReadOnlyList<TrackStatusEvent>> GetTrackStatusEventsAsync(
        string sessionId,
        long fromMs,
        long durationMs,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT event_time_ms, status_code, message
            FROM track_status_events
            WHERE session_id = @sessionId
              AND event_time_ms >= @fromMs
              AND event_time_ms < (@fromMs + @durationMs)
            ORDER BY event_time_ms
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("fromMs", fromMs);
        command.Parameters.AddWithValue("durationMs", durationMs);

        var events = new List<TrackStatusEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new TrackStatusEvent(reader.GetInt64(0), reader.GetString(1), GetNullableString(reader, 2)));
        }

        return events;
    }

    private async Task<IReadOnlyList<RaceControlMessage>> GetRaceControlMessagesAsync(
        string sessionId,
        long fromMs,
        long durationMs,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                session_time_ms,
                lap_number,
                category,
                message,
                status,
                flag,
                scope,
                sector,
                racing_number
            FROM race_control_messages
            WHERE session_id = @sessionId
              AND session_time_ms >= @fromMs
              AND session_time_ms < (@fromMs + @durationMs)
            ORDER BY session_time_ms NULLS LAST, race_control_message_id
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("fromMs", fromMs);
        command.Parameters.AddWithValue("durationMs", durationMs);

        var messages = new List<RaceControlMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new RaceControlMessage(
                GetNullableInt64(reader, 0),
                GetNullableInt32(reader, 1),
                GetNullableString(reader, 2),
                reader.GetString(3),
                GetNullableString(reader, 4),
                GetNullableString(reader, 5),
                GetNullableString(reader, 6),
                GetNullableString(reader, 7),
                GetNullableInt32(reader, 8)));
        }

        return messages;
    }

    private static TelemetrySample ReadTelemetrySample(NpgsqlDataReader reader, int offset = 0) =>
        new(
            GetNullableDateTimeOffset(reader, offset) ?? DateTimeOffset.UnixEpoch,
            GetNullableInt64(reader, offset + 1),
            GetNullableInt64(reader, offset + 2),
            GetNullableDouble(reader, offset + 3),
            GetNullableDouble(reader, offset + 4),
            GetNullableDouble(reader, offset + 5),
            GetNullableInt32(reader, offset + 6),
            GetNullableDouble(reader, offset + 7),
            GetNullableInt32(reader, offset + 8));

    private static void AddNullable<T>(NpgsqlCommand command, string name, NpgsqlDbType type, T? value)
    {
        var parameter = command.Parameters.Add(name, type);
        parameter.Value = value is null ? DBNull.Value : value;
    }

    private static string? GetNullableString(IDataRecord reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? GetNullableInt32(IDataRecord reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static long? GetNullableInt64(IDataRecord reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static double? GetNullableDouble(IDataRecord reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private static bool? GetNullableBoolean(IDataRecord reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetBoolean(ordinal);

    private static DateTimeOffset? GetNullableDateTimeOffset(IDataRecord reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return reader.GetValue(ordinal) switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            _ => null
        };
    }

    private static double? Difference(double? a, double? b) =>
        a is null || b is null ? null : a - b;

    private static int? DifferenceInt(int? a, int? b) =>
        a is null || b is null ? null : a - b;

    private static IReadOnlyList<AnalysisInsight> BuildLapInsights(
        long? lapTimeMs,
        IReadOnlyList<long?> sectorTimesMs,
        string? compound,
        int? tyreLife,
        double? peakSpeedKmh,
        double? averageSpeedKmh,
        double? averageThrottlePct,
        int telemetrySamples)
    {
        var insights = new List<AnalysisInsight>();
        if (lapTimeMs is not null)
        {
            insights.Add(new AnalysisInsight("lap_time", $"Lap time was {FormatDuration(lapTimeMs.Value)}.", lapTimeMs, "ms"));
        }

        if (peakSpeedKmh is not null)
        {
            insights.Add(new AnalysisInsight("peak_speed", $"Peak sampled speed was {Math.Round(peakSpeedKmh.Value)} km/h.", peakSpeedKmh, "km/h"));
        }

        var indexedSectors = sectorTimesMs
            .Select((value, index) => new { Value = value, Sector = index + 1 })
            .Where(sector => sector.Value is not null)
            .ToArray();
        if (indexedSectors.Length > 0)
        {
            var fastest = indexedSectors.MinBy(sector => sector.Value);
            insights.Add(new AnalysisInsight("fastest_sector", $"Fastest sector was S{fastest!.Sector} at {FormatDuration(fastest.Value!.Value)}.", fastest.Value, "ms"));
        }

        if (!string.IsNullOrWhiteSpace(compound) || tyreLife is not null)
        {
            var tyreText = string.IsNullOrWhiteSpace(compound) ? "unknown compound" : compound;
            insights.Add(new AnalysisInsight("tyre", $"Tyre context: {tyreText}, tyre life {tyreLife?.ToString() ?? "unknown"}.", tyreLife, "laps"));
        }

        if (averageSpeedKmh is not null && averageThrottlePct is not null)
        {
            insights.Add(new AnalysisInsight("pace_shape", $"Average speed was {Math.Round(averageSpeedKmh.Value, 1)} km/h with {Math.Round(averageThrottlePct.Value, 1)}% average throttle.", averageSpeedKmh, "km/h"));
        }

        insights.Add(new AnalysisInsight("data_quality", $"Telemetry sample count for this lap is {telemetrySamples}.", telemetrySamples, "samples"));
        return insights;
    }

    private static IReadOnlyList<AnalysisInsight> BuildComparisonInsights(
        string driverA,
        string driverB,
        LapComparisonSummary summary,
        IReadOnlyList<LapComparisonSegment> segments)
    {
        driverA = driverA.ToUpperInvariant();
        driverB = driverB.ToUpperInvariant();

        var insights = new List<AnalysisInsight>();
        if (summary.LapTimeDeltaMs is not null)
        {
            var quicker = summary.LapTimeDeltaMs < 0 ? driverA : driverB;
            insights.Add(new AnalysisInsight(
                "lap_delta",
                $"{quicker} was {Math.Abs(summary.LapTimeDeltaMs.Value)} ms quicker overall.",
                summary.LapTimeDeltaMs,
                "ms"));
        }

        var sectorDeltas = summary.SectorDeltasMs
            .Select((delta, index) => new { Delta = delta, Sector = index + 1 })
            .Where(sector => sector.Delta is not null)
            .ToArray();
        if (sectorDeltas.Length > 0)
        {
            var biggest = sectorDeltas.MaxBy(sector => Math.Abs(sector.Delta!.Value));
            var quicker = biggest!.Delta < 0 ? driverA : driverB;
            insights.Add(new AnalysisInsight(
                "biggest_sector_delta",
                $"Largest sector delta was S{biggest.Sector}: {quicker} by {Math.Abs(biggest.Delta!.Value)} ms.",
                biggest.Delta,
                "ms"));
        }

        var driverASegments = segments.Count(segment => segment.Advantage == "driver_a");
        var driverBSegments = segments.Count(segment => segment.Advantage == "driver_b");
        if (driverASegments > 0 || driverBSegments > 0)
        {
            insights.Add(new AnalysisInsight(
                "segment_advantage",
                $"{driverA} had the higher average speed in {driverASegments} segment(s); {driverB} in {driverBSegments}."));
        }

        if (summary.AvgSpeedDeltaKmh is not null)
        {
            var faster = summary.AvgSpeedDeltaKmh > 0 ? driverA : driverB;
            insights.Add(new AnalysisInsight(
                "average_speed_delta",
                $"{faster} carried {Math.Abs(Math.Round(summary.AvgSpeedDeltaKmh.Value, 1))} km/h more average speed.",
                summary.AvgSpeedDeltaKmh,
                "km/h"));
        }

        return insights;
    }

    private static IReadOnlyList<AnalysisInsight> BuildRaceInsights(
        SessionSummary session,
        WeatherSummary? weather,
        IReadOnlyList<RaceStintSummary> stints,
        IReadOnlyList<PitStopSummary> pitStops,
        IReadOnlyList<TrackStatusPeriodSummary> trackStatus,
        IReadOnlyList<RaceControlSummary> raceControl)
    {
        var insights = new List<AnalysisInsight>
        {
            new("session_scope", $"{session.EventName} {session.Year} {session.SessionType}: {session.DriverCount} drivers and {session.LapCount} imported laps.")
        };

        if (weather is not null)
        {
            insights.Add(new AnalysisInsight(
                "weather",
                weather.RainfallObserved
                    ? "Rainfall was observed in the session weather samples."
                    : $"No rainfall observed; track temperature range was {FormatNullable(weather.TrackTempMinC)}-{FormatNullable(weather.TrackTempMaxC)} C."));
        }

        if (stints.Count > 0)
        {
            var compounds = stints
                .Select(stint => stint.Compound)
                .Where(compound => !string.IsNullOrWhiteSpace(compound))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order()
                .ToArray();
            insights.Add(new AnalysisInsight("tyre_strategy", $"Imported stint data contains {stints.Count} stints across compounds: {string.Join(", ", compounds)}."));
        }

        if (pitStops.Count > 0)
        {
            insights.Add(new AnalysisInsight("pit_stops", $"Detected {pitStops.Count} pit-in/out lap markers.", pitStops.Count, "events"));
        }

        var neutralized = trackStatus.Count(period => period.StatusCode is "2" or "4" or "5" or "6" or "7");
        if (neutralized > 0)
        {
            insights.Add(new AnalysisInsight("track_status", $"Track status includes {neutralized} non-clear period(s).", neutralized, "periods"));
        }

        if (raceControl.Count > 0)
        {
            insights.Add(new AnalysisInsight("race_control", $"Included {raceControl.Count} race-control messages for narrative context.", raceControl.Count, "messages"));
        }

        return insights;
    }

    private static string? FormatCornerLabel(string sessionId, int? markerNumber, string? markerLetter)
    {
        if (markerNumber is null)
        {
            return null;
        }

        if (IsMonzaSession(sessionId))
        {
            return markerNumber switch
            {
                1 or 2 => "Turn 1/2, Variante del Rettifilo",
                4 or 5 => "Turn 4/5, Variante della Roggia",
                6 => "Turn 6, Lesmo 1",
                7 => "Turn 7, Lesmo 2",
                8 or 9 or 10 => "Turn 8/9/10, Variante Ascari",
                11 => "Turn 11, Parabolica / Alboreto",
                _ => FormatGenericCorner(markerNumber.Value, markerLetter)
            };
        }

        return FormatGenericCorner(markerNumber.Value, markerLetter);
    }

    private static bool IsMonzaSession(string sessionId) =>
        sessionId.Contains("italian-grand-prix", StringComparison.OrdinalIgnoreCase)
        || sessionId.Contains("monza", StringComparison.OrdinalIgnoreCase);

    private static string FormatGenericCorner(int markerNumber, string? markerLetter) =>
        $"Turn {markerNumber}{markerLetter}";

    private static string FormatDuration(long durationMs)
    {
        var minutes = durationMs / 60_000;
        var seconds = (durationMs % 60_000) / 1_000;
        var millis = durationMs % 1_000;
        return minutes > 0
            ? $"{minutes}:{seconds:00}.{millis:000}"
            : $"{seconds}.{millis:000}s";
    }

    private static string FormatNullable(double? value) =>
        value is null ? "unknown" : Math.Round(value.Value, 1).ToString("0.0");

    private static Activity? StartStoreActivity(
        string name,
        string? sessionId = null,
        string? driverCode = null,
        int? lapNumber = null)
    {
        var activity = ActivitySource.StartActivity(name, ActivityKind.Internal);
        activity?.SetTag("component", "RaceTelemetry.Data");
        activity?.SetTag("race.session_id", sessionId);
        activity?.SetTag("race.driver_code", driverCode?.ToUpperInvariant());
        activity?.SetTag("race.lap_number", lapNumber);
        return activity;
    }

    private sealed record AggregateGroupExpression(
        string Key,
        string Sql,
        string Alias);

    private sealed record AggregateGroupValues(
        string? DriverCode,
        int? LapNumber,
        int? StintNumber,
        string? Compound,
        string? TrackStatus,
        long? BucketStartMs,
        long? BucketEndMs);

    private static readonly TelemetryChannelValues EmptyTelemetryChannelValues = new(
        null,
        null,
        null,
        null,
        null);
}
