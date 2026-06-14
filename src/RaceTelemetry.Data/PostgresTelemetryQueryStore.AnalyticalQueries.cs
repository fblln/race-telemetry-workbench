using System.Data;
using System.Diagnostics;
using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using RaceTelemetry.Contracts;

namespace RaceTelemetry.Data;

/// <summary>
/// Contains analytical telemetry query endpoints backed by dynamic but allow-listed PostgreSQL SQL.
/// </summary>
public sealed partial class PostgresTelemetryQueryStore
{
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

        // The SELECT/GROUP BY fragments are generated only from allow-listed field names.
        // Npgsql parameters still carry all external values that can vary per request.
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

        await using var command = _dataSource.CreateCommand(sql);
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
        // Window detection first flags individual samples, then groups contiguous
        // flagged samples with a running count difference. That keeps short
        // braking/DRS/throttle windows readable without row-by-row C# processing.
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

        await using var command = _dataSource.CreateCommand(sql);
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

        await using var command = _dataSource.CreateCommand(sql);
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

        await using var command = _dataSource.CreateCommand(sql);
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

        await using var command = _dataSource.CreateCommand(sql);
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

        await using var command = _dataSource.CreateCommand(sql);
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
        await using var metadataCommand = _dataSource.CreateCommand(metadataSql);
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
        await using var markersCommand = _dataSource.CreateCommand(markersSql);
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

        var includePosition = IncludesChannel(channels, "x")
            || IncludesChannel(channels, "y")
            || IncludesChannel(channels, "z");
        var positionJoin = includePosition
            ? """
                LEFT JOIN LATERAL (
                    SELECT p.x, p.y, p.z
                    FROM position_samples p
                    WHERE p.session_id = t.session_id
                      AND p.driver_code = t.driver_code
                      AND p.sample_time_utc >= t.sample_time_utc - interval '500 milliseconds'
                      AND p.sample_time_utc <= t.sample_time_utc + interval '500 milliseconds'
                    ORDER BY abs(extract(epoch FROM (p.sample_time_utc - t.sample_time_utc)))
                    LIMIT 1
                ) p ON true
                """
            : "";
        var speedProjection = IncludesChannel(channels, "speed_kmh") ? "t.speed_kmh" : "NULL::double precision AS speed_kmh";
        var throttleProjection = IncludesChannel(channels, "throttle_pct") ? "t.throttle_pct" : "NULL::double precision AS throttle_pct";
        var brakeProjection = IncludesChannel(channels, "brake_pct") ? "t.brake_pct" : "NULL::double precision AS brake_pct";
        var gearProjection = IncludesChannel(channels, "gear") ? "t.gear" : "NULL::int AS gear";
        var rpmProjection = IncludesChannel(channels, "rpm") ? "t.rpm" : "NULL::double precision AS rpm";
        var drsProjection = IncludesChannel(channels, "drs") ? "t.drs" : "NULL::int AS drs";
        var xProjection = IncludesChannel(channels, "x") ? "p.x" : "NULL::double precision AS x";
        var yProjection = IncludesChannel(channels, "y") ? "p.y" : "NULL::double precision AS y";
        var zProjection = IncludesChannel(channels, "z") ? "p.z" : "NULL::double precision AS z";

        var sql = $"""
            SELECT
                t.driver_code,
                t.session_time_ms,
                t.lap_number,
                {speedProjection},
                {throttleProjection},
                {brakeProjection},
                {gearProjection},
                {rpmProjection},
                {drsProjection},
                {xProjection},
                {yProjection},
                {zProjection}
            FROM telemetry_samples t
            {positionJoin}
            WHERE t.session_id = @sessionId
              AND t.session_time_ms >= @fromMs
              AND t.session_time_ms < (@fromMs + @durationMs)
              AND (@drivers::text[] IS NULL OR t.driver_code = ANY(@drivers::text[]))
            ORDER BY t.driver_code, t.session_time_ms, t.sample_time_utc
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("fromMs", fromMs);
        command.Parameters.AddWithValue("durationMs", durationMs);
        AddNullable(command, "drivers", NpgsqlDbType.Array | NpgsqlDbType.Text, drivers is { Count: > 0 } ? drivers.Select(d => d.ToUpperInvariant()).ToArray() : null);

        var chunks = new Dictionary<string, List<ReplaySample>>(StringComparer.OrdinalIgnoreCase);
        var rowsByDriver = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var driverCode = reader.GetString(0);
            var rowIndex = rowsByDriver.GetValueOrDefault(driverCode);
            rowsByDriver[driverCode] = rowIndex + 1;
            if (rowIndex % sampleEvery != 0)
            {
                continue;
            }

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

        await using var command = _dataSource.CreateCommand(sql);
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
}
