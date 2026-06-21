using System.Data;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using NpgsqlTypes;
using RaceTelemetry.Contracts;

namespace RaceTelemetry.Data;

/// <summary>
/// Contains private database lookup queries used to assemble replay, comparison, and context responses.
/// </summary>
public sealed partial class PostgresTelemetryQueryStore
{
    private async Task<bool> SessionExistsAsync(string sessionId, CancellationToken cancellationToken)
    {
        var cacheKey = CacheKey("session_exists", sessionId);
        if (_cache.TryGetValue<bool>(cacheKey, out var cachedExists))
        {
            return cachedExists;
        }

        const string sql = "SELECT EXISTS (SELECT 1 FROM sessions WHERE session_id = @sessionId)";
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        var exists = (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
        _cache.Set(cacheKey, exists, MetadataCacheOptions);
        return exists;
    }

    private async Task<bool> DriverExistsAsync(string sessionId, string driverCode, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue<IReadOnlyList<DriverSummary>>(CacheKey("drivers", sessionId), out var cachedDrivers)
            && cachedDrivers is not null)
        {
            return cachedDrivers.Any(driver => driver.DriverCode.Equals(driverCode, StringComparison.OrdinalIgnoreCase));
        }

        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM session_drivers
                WHERE session_id = @sessionId
                  AND driver_code = upper(@driverCode)
            )
            """;
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("driverCode", driverCode);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private async Task<bool> DriversExistAsync(
        string sessionId,
        IReadOnlyList<string> drivers,
        CancellationToken cancellationToken)
    {
        var normalizedDrivers = drivers.Select(driver => driver.ToUpperInvariant()).Distinct().ToArray();
        if (_cache.TryGetValue<IReadOnlyList<DriverSummary>>(CacheKey("drivers", sessionId), out var cachedDrivers)
            && cachedDrivers is not null)
        {
            var cachedCodes = cachedDrivers.Select(driver => driver.DriverCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return normalizedDrivers.All(cachedCodes.Contains);
        }

        const string sql = """
            SELECT count(*)::int
            FROM session_drivers
            WHERE session_id = @sessionId
              AND driver_code = ANY(@drivers)
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("drivers", normalizedDrivers);
        var count = (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
        return count == normalizedDrivers.Length;
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
        await using var command = _dataSource.CreateCommand(sql);
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

        await using var command = _dataSource.CreateCommand(sql);
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
        // Lap comparison aligns both laps onto fixed lap-time buckets so deltas
        // are based on comparable progress through the lap rather than raw sample timestamps.
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

        await using var command = _dataSource.CreateCommand(sql);
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

        await using var command = _dataSource.CreateCommand(sql);
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
        await using (var command = _dataSource.CreateCommand(metadataSql))
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

        await using var markersCommand = _dataSource.CreateCommand(markersSql);
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

        await using var command = _dataSource.CreateCommand(sql);
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

        await using var command = _dataSource.CreateCommand(sql);
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

        await using var command = _dataSource.CreateCommand(sql);
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

        await using var command = _dataSource.CreateCommand(sql);
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

        await using var command = _dataSource.CreateCommand(sql);
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
                -- Race distance (highest lap number reached), not the count of all drivers' lap rows.
                SELECT session_id, coalesce(max(lap_number), 0)::int AS lap_count
                FROM laps
                WHERE session_id = @sessionId AND NOT is_deleted
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

        await using var command = _dataSource.CreateCommand(sql);
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

        await using var command = _dataSource.CreateCommand(sql);
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

        await using var command = _dataSource.CreateCommand(sql);
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

        await using var command = _dataSource.CreateCommand(sql);
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

        await using var command = _dataSource.CreateCommand(sql);
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

        await using var command = _dataSource.CreateCommand(sql);
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

        await using var command = _dataSource.CreateCommand(sql);
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

        await using var command = _dataSource.CreateCommand(sql);
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
}
