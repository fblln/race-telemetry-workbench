using NpgsqlTypes;
using RaceTelemetry.Contracts;

namespace RaceTelemetry.Data;

/// <summary>
/// Contains distance-domain lap quality and comparison queries.
/// </summary>
public sealed partial class PostgresTelemetryQueryStore
{
    public async Task<LapQualityResponse?> GetLapQualityAsync(
        string sessionId,
        string driverCode,
        int lapNumber,
        CancellationToken cancellationToken)
    {
        using var activity = StartStoreActivity("query_store.get_lap_quality", sessionId, driverCode, lapNumber);

        if (!await LapExistsAsync(sessionId, driverCode, lapNumber, cancellationToken))
        {
            return null;
        }

        if (!await TableExistsAsync("lap_telemetry_quality", cancellationToken))
        {
            return null;
        }

        const string sql = """
            SELECT
                q.official_lap_duration_ms::bigint,
                q.telemetry_covered_duration_ms::bigint,
                q.first_sample_offset_ms::bigint,
                q.last_sample_offset_ms::bigint,
                q.maximum_car_data_gap_ms::bigint,
                q.maximum_position_gap_ms::bigint,
                q.final_integrated_distance_m,
                q.interpolated_car_data_percentage,
                q.interpolated_position_percentage,
                q.stale_sample_percentage,
                q.distance_delta_validation_ms::bigint,
                q.quality_status,
                q.quality_messages
            FROM lap_telemetry_quality q
            JOIN session_drivers sd
              ON sd.session_id = q.session_id
             AND sd.driver_number = q.driver_number
            WHERE q.session_id = @sessionId
              AND sd.driver_code = upper(@driverCode)
              AND q.lap_number = @lapNumber
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("driverCode", driverCode);
        command.Parameters.AddWithValue("lapNumber", lapNumber);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new LapQualityResponse(
            sessionId,
            driverCode.ToUpperInvariant(),
            lapNumber,
            GetNullableInt64(reader, 0),
            GetNullableInt64(reader, 1),
            GetNullableInt64(reader, 2),
            GetNullableInt64(reader, 3),
            GetNullableInt64(reader, 4),
            GetNullableInt64(reader, 5),
            GetNullableDouble(reader, 6),
            GetNullableDouble(reader, 7),
            GetNullableDouble(reader, 8),
            GetNullableDouble(reader, 9),
            GetNullableInt64(reader, 10),
            reader.GetString(11),
            reader.IsDBNull(12) ? [] : reader.GetFieldValue<string[]>(12));
    }

    public async Task<LapComparisonByDistanceResponse?> CompareLapsByDistanceAsync(
        string sessionId,
        string driverA,
        int lapA,
        string driverB,
        int lapB,
        double? startDistanceM,
        double? endDistanceM,
        CancellationToken cancellationToken)
    {
        using var activity = StartStoreActivity("query_store.compare_laps_by_distance", sessionId, driverA, lapA);
        activity?.SetTag("race.query.driver_b", driverB.ToUpperInvariant());
        activity?.SetTag("race.query.lap_b", lapB);
        activity?.SetTag("race.query.start_distance_m", startDistanceM);
        activity?.SetTag("race.query.end_distance_m", endDistanceM);

        if (!await RequestedLapsExistAsync(sessionId, driverA, lapA, driverB, lapB, cancellationToken))
        {
            return null;
        }

        if (!await TableExistsAsync("lap_telemetry_by_distance", cancellationToken))
        {
            return null;
        }

        var summaryTask = GetLapComparisonByDistanceSummaryAsync(
            sessionId,
            driverA,
            lapA,
            driverB,
            lapB,
            cancellationToken);

        const string sql = """
            WITH a AS (
                SELECT
                    distance_m,
                    normalized_track_progress,
                    lap_elapsed_time_ms::bigint AS lap_elapsed_time_ms,
                    speed_kmh,
                    throttle_pct,
                    brake_pct,
                    rpm,
                    gear,
                    drs
                FROM lap_telemetry_by_distance
                WHERE session_id = @sessionId
                  AND driver_code = upper(@driverA)
                  AND lap_number = @lapA
                  AND (@startDistanceM IS NULL OR distance_m >= @startDistanceM)
                  AND (@endDistanceM IS NULL OR distance_m <= @endDistanceM)
            ),
            b AS (
                SELECT
                    distance_m,
                    normalized_track_progress,
                    lap_elapsed_time_ms::bigint AS lap_elapsed_time_ms,
                    speed_kmh,
                    throttle_pct,
                    brake_pct,
                    rpm,
                    gear,
                    drs
                FROM lap_telemetry_by_distance
                WHERE session_id = @sessionId
                  AND driver_code = upper(@driverB)
                  AND lap_number = @lapB
                  AND (@startDistanceM IS NULL OR distance_m >= @startDistanceM)
                  AND (@endDistanceM IS NULL OR distance_m <= @endDistanceM)
            )
            SELECT
                a.distance_m,
                ((a.normalized_track_progress + b.normalized_track_progress) / 2.0) AS normalized_track_progress,
                a.lap_elapsed_time_ms,
                b.lap_elapsed_time_ms,
                a.speed_kmh,
                a.throttle_pct,
                a.brake_pct,
                a.rpm,
                a.gear,
                a.drs,
                b.speed_kmh,
                b.throttle_pct,
                b.brake_pct,
                b.rpm,
                b.gear,
                b.drs
            FROM a
            JOIN b USING (distance_m)
            ORDER BY a.distance_m
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("driverA", driverA);
        command.Parameters.AddWithValue("lapA", lapA);
        command.Parameters.AddWithValue("driverB", driverB);
        command.Parameters.AddWithValue("lapB", lapB);
        AddNullable(command, "startDistanceM", NpgsqlDbType.Double, startDistanceM);
        AddNullable(command, "endDistanceM", NpgsqlDbType.Double, endDistanceM);

        var items = new List<LapComparisonByDistancePoint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var aElapsedMs = GetNullableInt64(reader, 2);
            var bElapsedMs = GetNullableInt64(reader, 3);
            var aValues = new DistanceTelemetryChannelValues(
                GetNullableDouble(reader, 4),
                GetNullableDouble(reader, 5),
                GetNullableDouble(reader, 6),
                GetNullableDouble(reader, 7),
                GetNullableInt32(reader, 8),
                GetNullableInt32(reader, 9));
            var bValues = new DistanceTelemetryChannelValues(
                GetNullableDouble(reader, 10),
                GetNullableDouble(reader, 11),
                GetNullableDouble(reader, 12),
                GetNullableDouble(reader, 13),
                GetNullableInt32(reader, 14),
                GetNullableInt32(reader, 15));

            items.Add(new LapComparisonByDistancePoint(
                reader.GetDouble(0),
                reader.GetDouble(1),
                aElapsedMs,
                bElapsedMs,
                aElapsedMs is null || bElapsedMs is null ? null : aElapsedMs - bElapsedMs,
                aValues,
                bValues,
                new DistanceTelemetryChannelValues(
                    Difference(aValues.SpeedKmh, bValues.SpeedKmh),
                    Difference(aValues.ThrottlePct, bValues.ThrottlePct),
                    Difference(aValues.BrakePct, bValues.BrakePct),
                    Difference(aValues.Rpm, bValues.Rpm),
                    DifferenceInt(aValues.Gear, bValues.Gear),
                    DifferenceInt(aValues.Drs, bValues.Drs))));
        }

        if (items.Count == 0)
        {
            return null;
        }

        return new LapComparisonByDistanceResponse(
            sessionId,
            driverA.ToUpperInvariant(),
            lapA,
            driverB.ToUpperInvariant(),
            lapB,
            5,
            "positive means driverA is slower",
            items,
            await summaryTask);
    }

    private async Task<LapComparisonByDistanceSummary> GetLapComparisonByDistanceSummaryAsync(
        string sessionId,
        string driverA,
        int lapA,
        string driverB,
        int lapB,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH lap_times AS (
                SELECT
                    driver_code,
                    lap_number,
                    lap_time_ms::bigint AS lap_time_ms
                FROM laps
                WHERE session_id = @sessionId
                  AND (
                    (driver_code = upper(@driverA) AND lap_number = @lapA)
                    OR (driver_code = upper(@driverB) AND lap_number = @lapB)
                  )
            ),
            quality AS (
                SELECT
                    sd.driver_code,
                    q.lap_number,
                    q.quality_status
                FROM lap_telemetry_quality q
                JOIN session_drivers sd
                  ON sd.session_id = q.session_id
                 AND sd.driver_number = q.driver_number
                WHERE q.session_id = @sessionId
                  AND (
                    (sd.driver_code = upper(@driverA) AND q.lap_number = @lapA)
                    OR (sd.driver_code = upper(@driverB) AND q.lap_number = @lapB)
                  )
            ),
            finish_rows AS (
                SELECT DISTINCT ON (driver_code, lap_number)
                    driver_code,
                    lap_number,
                    lap_elapsed_time_ms::bigint AS finish_elapsed_ms
                FROM lap_telemetry_by_distance
                WHERE session_id = @sessionId
                  AND (
                    (driver_code = upper(@driverA) AND lap_number = @lapA)
                    OR (driver_code = upper(@driverB) AND lap_number = @lapB)
                  )
                ORDER BY driver_code, lap_number, distance_m DESC
            )
            SELECT
                la.lap_time_ms - lb.lap_time_ms AS official_lap_time_delta_ms,
                fa.finish_elapsed_ms - fb.finish_elapsed_ms AS finish_delta_ms,
                abs((fa.finish_elapsed_ms - fb.finish_elapsed_ms) - (la.lap_time_ms - lb.lap_time_ms)) AS finish_delta_validation_ms,
                qa.quality_status AS a_quality_status,
                qb.quality_status AS b_quality_status
            FROM lap_times la
            JOIN lap_times lb ON true
            LEFT JOIN finish_rows fa
              ON fa.driver_code = la.driver_code
             AND fa.lap_number = la.lap_number
            LEFT JOIN finish_rows fb
              ON fb.driver_code = lb.driver_code
             AND fb.lap_number = lb.lap_number
            LEFT JOIN quality qa
              ON qa.driver_code = la.driver_code
             AND qa.lap_number = la.lap_number
            LEFT JOIN quality qb
              ON qb.driver_code = lb.driver_code
             AND qb.lap_number = lb.lap_number
            WHERE la.driver_code = upper(@driverA)
              AND la.lap_number = @lapA
              AND lb.driver_code = upper(@driverB)
              AND lb.lap_number = @lapB
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("driverA", driverA);
        command.Parameters.AddWithValue("lapA", lapA);
        command.Parameters.AddWithValue("driverB", driverB);
        command.Parameters.AddWithValue("lapB", lapB);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new LapComparisonByDistanceSummary(null, null, null, null, null);
        }

        return new LapComparisonByDistanceSummary(
            GetNullableInt64(reader, 0),
            GetNullableInt64(reader, 1),
            GetNullableInt64(reader, 2),
            GetNullableString(reader, 3),
            GetNullableString(reader, 4));
    }
}
