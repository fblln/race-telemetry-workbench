using NpgsqlTypes;
using RaceTelemetry.Contracts;

namespace RaceTelemetry.Data;

/// <summary>
/// Field-oriented analytical queries that back the desktop Field tower, position
/// trace, and Track Incidents views (§6.11-6.13). Classified position is derived
/// from cumulative lap time because it is not stored on <c>laps</c>; the same
/// ranking feeds standings gaps and the position trace.
/// </summary>
public sealed partial class PostgresTelemetryQueryStore
{
    public async Task<StandingsResponse?> GetStandingsAsync(
        string sessionId,
        int? atLap,
        string sortBy,
        CancellationToken cancellationToken)
    {
        using var activity = StartStoreActivity("query_store.get_standings", sessionId);
        activity?.SetTag("race.query.at_lap", atLap);
        activity?.SetTag("race.query.sort_by", sortBy);

        if (!await SessionExistsAsync(sessionId, cancellationToken))
        {
            return null;
        }

        // Classification follows the lap chart: rank by laps completed, then by the
        // wall-clock time the driver crossed the line (lap_end_utc), which already
        // absorbs pit stops and time lost. Latest scoped lap supplies last lap,
        // compound, and tyre life.
        const string sql = """
            WITH bounds AS (
                SELECT coalesce(@atLap::int, max(lap_number)) AS at_lap
                FROM laps
                WHERE session_id = @sessionId AND NOT is_deleted
            ),
            scoped AS (
                SELECT l.driver_code, l.lap_number, l.lap_time_ms::bigint AS lap_time_ms,
                       l.compound, l.tyre_life, l.is_pit_in_lap, l.lap_end_utc
                FROM laps l CROSS JOIN bounds
                WHERE l.session_id = @sessionId AND NOT l.is_deleted
                  AND l.lap_number <= bounds.at_lap
            ),
            per_driver AS (
                SELECT driver_code,
                    max(lap_number) AS laps_done,
                    extract(epoch FROM max(lap_end_utc)) * 1000 AS last_end_ms,
                    count(*) FILTER (WHERE is_pit_in_lap)::int AS pit_count,
                    min(lap_time_ms) AS best_lap_ms,
                    (array_agg(lap_time_ms ORDER BY lap_number DESC)
                        FILTER (WHERE lap_time_ms IS NOT NULL))[1:5] AS recent_laps
                FROM scoped GROUP BY driver_code
            ),
            last_scoped AS (
                SELECT DISTINCT ON (driver_code) driver_code, lap_time_ms AS last_lap_ms, compound, tyre_life
                FROM scoped ORDER BY driver_code, lap_number DESC
            )
            SELECT
                (SELECT at_lap FROM bounds) AS at_lap,
                sd.driver_code, sd.full_name, sd.team_name,
                pd.laps_done, pd.last_end_ms, ls.last_lap_ms, pd.best_lap_ms,
                ls.compound, ls.tyre_life, coalesce(pd.pit_count, 0) AS pit_count, pd.recent_laps,
                (SELECT max(laps_done) FROM per_driver) AS field_max_lap
            FROM session_drivers sd
            LEFT JOIN per_driver pd ON pd.driver_code = sd.driver_code
            LEFT JOIN last_scoped ls ON ls.driver_code = sd.driver_code
            WHERE sd.session_id = @sessionId
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        AddNullable(command, "atLap", NpgsqlDbType.Integer, atLap);

        var rows = new List<StandingRowRaw>();
        var atLapValue = atLap ?? 0;
        long? fieldMaxLap = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                atLapValue = GetNullableInt32(reader, 0) ?? atLapValue;
                fieldMaxLap = GetNullableInt64(reader, 12);
                rows.Add(new StandingRowRaw(
                    DriverCode: reader.GetString(1),
                    FullName: GetNullableString(reader, 2),
                    TeamName: GetNullableString(reader, 3),
                    LapsDone: GetNullableInt64(reader, 4),
                    LastEndMs: GetNullableDouble(reader, 5),
                    LastLapMs: GetNullableInt64(reader, 6),
                    BestLapMs: GetNullableInt64(reader, 7),
                    Compound: GetNullableString(reader, 8),
                    TyreLife: GetNullableInt32(reader, 9),
                    PitCount: reader.GetInt32(10),
                    RecentLaps: reader.IsDBNull(11) ? null : reader.GetFieldValue<long[]>(11)));
            }
        }

        var sessionBestMs = rows
            .Select(r => r.BestLapMs)
            .Where(b => b is not null)
            .DefaultIfEmpty(null)
            .Min();

        // Rank by laps completed, then crossing time; cars with no scoped lap fall
        // to the back in driver-code order.
        var ranked = rows
            .OrderByDescending(r => r.LapsDone ?? -1)
            .ThenBy(r => r.LastEndMs ?? double.MaxValue)
            .ThenBy(r => r.DriverCode, StringComparer.Ordinal)
            .ToList();

        var leaderEnd = ranked.FirstOrDefault(r => r.LastEndMs is not null)?.LastEndMs;
        var items = new List<StandingRow>(ranked.Count);
        double? previousEnd = null;
        for (var i = 0; i < ranked.Count; i++)
        {
            var r = ranked[i];
            // Gap/interval are well-defined for lead-lap cars; clamp at zero so a
            // retired car's earlier final crossing never reads as a negative gap.
            var gap = r.LastEndMs is not null && leaderEnd is not null
                ? (long)Math.Max(0, r.LastEndMs.Value - leaderEnd.Value)
                : 0;
            var interval = r.LastEndMs is not null && previousEnd is not null
                ? (long)Math.Max(0, r.LastEndMs.Value - previousEnd.Value)
                : 0;
            if (r.LastEndMs is not null)
            {
                previousEnd = r.LastEndMs;
            }

            var status = fieldMaxLap is not null && (r.LapsDone ?? -1) >= fieldMaxLap.Value - 1
                ? "running"
                : "out";
            // recent_laps comes back newest-first; reverse so the sparkline reads old -> new.
            var recent = r.RecentLaps is { Length: > 0 } ? r.RecentLaps.Reverse().ToArray() : null;

            items.Add(new StandingRow(
                Position: i + 1,
                DriverCode: r.DriverCode,
                FullName: r.FullName,
                TeamName: r.TeamName,
                GapToLeaderMs: gap,
                IntervalMs: interval,
                LastLapMs: r.LastLapMs,
                BestLapMs: r.BestLapMs,
                IsSessionBestLap: r.BestLapMs is not null && r.BestLapMs == sessionBestMs,
                IsPersonalBestLap: r.LastLapMs is not null && r.LastLapMs == r.BestLapMs,
                Compound: r.Compound,
                TyreLife: r.TyreLife,
                PitCount: r.PitCount,
                Status: status,
                RecentLapMs: recent));
        }

        var sorted = sortBy switch
        {
            "last_lap_ms" => items.OrderBy(r => r.LastLapMs ?? long.MaxValue).ToList(),
            "best_lap_ms" => items.OrderBy(r => r.BestLapMs ?? long.MaxValue).ToList(),
            "gap_ms" => items.OrderBy(r => r.GapToLeaderMs).ToList(),
            "pit_count" => items.OrderByDescending(r => r.PitCount).ThenBy(r => r.Position).ToList(),
            _ => items,
        };

        return new StandingsResponse(sessionId, (int)atLapValue, sorted);
    }

    public async Task<PositionsResponse?> GetPositionsAsync(
        string sessionId,
        IReadOnlyList<string>? drivers,
        int? fromLap,
        int? toLap,
        CancellationToken cancellationToken)
    {
        using var activity = StartStoreActivity("query_store.get_positions", sessionId);

        if (!await SessionExistsAsync(sessionId, cancellationToken))
        {
            return null;
        }

        var maxLap = await GetMaxLapNumberAsync(sessionId, cancellationToken);
        var from = Math.Max(1, fromLap ?? 1);
        var to = Math.Max(from, toLap ?? maxLap);

        // Rank by lap_start_utc, which is fully populated and consistent across
        // drivers (lap_end_utc has gaps); this reads as the order entering each lap.
        const string sql = """
            WITH ranked AS (
                SELECT driver_code, lap_number,
                    rank() OVER (PARTITION BY lap_number ORDER BY lap_start_utc)::int AS position
                FROM laps
                WHERE session_id = @sessionId AND NOT is_deleted AND lap_start_utc IS NOT NULL
            )
            SELECT sd.driver_code, r.lap_number, r.position
            FROM session_drivers sd
            LEFT JOIN ranked r
                ON r.driver_code = sd.driver_code
                AND r.lap_number BETWEEN @fromLap AND @toLap
            WHERE sd.session_id = @sessionId
              AND (@drivers::text[] IS NULL OR sd.driver_code = ANY(@drivers::text[]))
            ORDER BY sd.driver_code, r.lap_number
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("fromLap", from);
        command.Parameters.AddWithValue("toLap", to);
        AddNullable(command, "drivers", NpgsqlDbType.Array | NpgsqlDbType.Text,
            drivers is { Count: > 0 } ? drivers.Select(d => d.ToUpperInvariant()).ToArray() : null);

        var length = to - from + 1;
        var byDriver = new Dictionary<string, int?[]>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var driverCode = reader.GetString(0);
            if (!byDriver.TryGetValue(driverCode, out var positions))
            {
                positions = new int?[length];
                byDriver[driverCode] = positions;
            }

            if (reader.IsDBNull(1))
            {
                continue;
            }

            var lap = reader.GetInt32(1);
            var index = lap - from;
            if (index >= 0 && index < length)
            {
                positions[index] = GetNullableInt32(reader, 2);
            }
        }

        var items = byDriver
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new DriverPositions(pair.Key, pair.Value))
            .ToArray();

        return new PositionsResponse(sessionId, from, to, items);
    }

    public async Task<RaceControlResponse?> GetRaceControlAsync(
        string sessionId,
        IReadOnlyList<string>? types,
        double minBrakingG,
        int maxResults,
        CancellationToken cancellationToken)
    {
        using var activity = StartStoreActivity("query_store.get_incidents", sessionId);
        activity?.SetTag("race.query.min_braking_g", minBrakingG);
        activity?.SetTag("race.query.max_results", maxResults);

        if (!await SessionExistsAsync(sessionId, cancellationToken))
        {
            return null;
        }

        var trackStatusTask = GetTrackStatusIncidentsAsync(sessionId, cancellationToken);
        var hardBrakingTask = GetHardBrakingIncidentsAsync(sessionId, minBrakingG, maxResults, cancellationToken);
        var raceControlTask = GetRaceControlIncidentsAsync(sessionId, maxResults, cancellationToken);

        await Task.WhenAll(trackStatusTask, hardBrakingTask, raceControlTask);

        var trackStatus = await trackStatusTask;
        var hardBraking = await hardBrakingTask;
        var raceControl = await raceControlTask;

        var typeFilter = types is { Count: > 0 }
            ? new HashSet<string>(types, StringComparer.OrdinalIgnoreCase)
            : null;

        var all = trackStatus.Concat(hardBraking).Concat(raceControl)
            .Where(i => typeFilter is null || typeFilter.Contains(i.Type))
            .OrderBy(i => i.SessionTimeMs is null)
            .ThenBy(i => i.SessionTimeMs ?? long.MaxValue)
            .Take(maxResults)
            .ToList();

        var hardestG = hardBraking
            .Select(i => i.Metrics?.PeakBrakingG)
            .Where(g => g is not null)
            .DefaultIfEmpty(null)
            .Max();
        var lapsUnderSafetyCar = trackStatus.Count(i => i.Type is "safety_car" or "vsc");

        var summary = new RaceControlListSummary(all.Count, hardestG, lapsUnderSafetyCar);
        return new RaceControlResponse(sessionId, all, summary);
    }

    private async Task<IReadOnlyList<RaceControlItem>> GetTrackStatusIncidentsAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT start_time_ms, status_code, status_name, message
            FROM track_status_periods
            WHERE session_id = @sessionId AND status_code IN ('2', '4', '5', '6', '7')
            ORDER BY start_time_ms
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);

        var incidents = new List<RaceControlItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var startMs = reader.GetInt64(0);
            var statusCode = reader.GetString(1);
            var statusName = GetNullableString(reader, 2);
            var message = GetNullableString(reader, 3);
            var (type, severity) = statusCode switch
            {
                "2" => ("yellow", "info"),
                "4" => ("safety_car", "high"),
                "5" => ("red", "high"),
                _ => ("vsc", "info"),
            };
            incidents.Add(new RaceControlItem(
                type,
                LapNumber: null,
                SessionTimeMs: startMs,
                Message: string.IsNullOrWhiteSpace(message) ? HumanizeStatus(statusName ?? type) : message!,
                NearestCorner: null,
                X: null,
                Y: null,
                DriverCode: null,
                Severity: severity,
                Metrics: null));
        }

        return incidents;
    }

    private async Task<IReadOnlyList<RaceControlItem>> GetHardBrakingIncidentsAsync(
        string sessionId,
        double minBrakingG,
        int maxResults,
        CancellationToken cancellationToken)
    {
        // Detect contiguous hard-braking windows from the telemetry stream so the
        // speed drop and duration (and thus braking g) are measured, never invented.
        // Position is a separate stream, so x/y is matched by nearest timestamp
        // within a bounded range and the dot is anchored to the nearest corner.
        const string sql = """
            WITH ordered AS (
                SELECT
                    t.sample_time_utc,
                    t.driver_code,
                    t.lap_number,
                    t.session_time_ms::bigint AS session_time_ms,
                    t.speed_kmh,
                    (t.brake_pct >= 80) AS is_event,
                    row_number() OVER (PARTITION BY t.driver_code ORDER BY t.session_time_ms NULLS LAST, t.sample_time_utc)
                    - row_number() OVER (PARTITION BY t.driver_code, (t.brake_pct >= 80) ORDER BY t.session_time_ms NULLS LAST, t.sample_time_utc) AS group_id
                FROM telemetry_samples t
                WHERE t.session_id = @sessionId AND t.session_time_ms IS NOT NULL
            ),
            windows AS (
                SELECT
                    driver_code,
                    min(lap_number) AS lap_number,
                    min(sample_time_utc) AS start_sample_time_utc,
                    min(session_time_ms) AS start_session_time_ms,
                    greatest(max(session_time_ms) - min(session_time_ms), 0)::bigint AS duration_ms,
                    (array_agg(speed_kmh ORDER BY session_time_ms, sample_time_utc))[1] AS entry_speed_kmh,
                    min(speed_kmh) AS min_speed_kmh
                FROM ordered
                WHERE is_event
                GROUP BY driver_code, group_id
                HAVING greatest(max(session_time_ms) - min(session_time_ms), 0) >= 200
            ),
            with_pos AS (
                SELECT w.*, pos.x, pos.y
                FROM windows w
                LEFT JOIN LATERAL (
                    SELECT p.x, p.y
                    FROM position_samples p
                    WHERE p.session_id = @sessionId
                      AND p.driver_code = w.driver_code
                      AND p.sample_time_utc BETWEEN w.start_sample_time_utc - interval '1 second'
                                               AND w.start_sample_time_utc + interval '1 second'
                    ORDER BY abs(extract(epoch FROM (p.sample_time_utc - w.start_sample_time_utc)))
                    LIMIT 1
                ) pos ON true
            )
            SELECT
                w.driver_code,
                w.lap_number,
                w.start_session_time_ms,
                w.duration_ms,
                w.entry_speed_kmh,
                w.min_speed_kmh,
                marker.marker_number,
                marker.marker_letter,
                marker.mx,
                marker.my,
                w.x,
                w.y
            FROM with_pos w
            LEFT JOIN LATERAL (
                SELECT cm.marker_number, cm.marker_letter, cm.x AS mx, cm.y AS my
                FROM circuit_markers cm
                WHERE cm.session_id = @sessionId AND cm.marker_type = 'corner'
                  AND w.x IS NOT NULL AND w.y IS NOT NULL
                ORDER BY sqrt(power(cm.x - w.x, 2) + power(cm.y - w.y, 2))
                LIMIT 1
            ) marker ON true
            ORDER BY (w.entry_speed_kmh - w.min_speed_kmh) DESC NULLS LAST
            LIMIT @scanLimit
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("scanLimit", Math.Max(maxResults, 50) * 3);

        var incidents = new List<RaceControlItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var driverCode = reader.GetString(0);
            var lapNumber = GetNullableInt32(reader, 1);
            var sessionTimeMs = GetNullableInt64(reader, 2);
            var durationMs = reader.GetInt64(3);
            var entrySpeed = GetNullableDouble(reader, 4);
            var minSpeed = GetNullableDouble(reader, 5);
            var markerNumber = GetNullableInt32(reader, 6);
            var markerLetter = GetNullableString(reader, 7);
            var markerX = GetNullableDouble(reader, 8);
            var markerY = GetNullableDouble(reader, 9);
            var carX = GetNullableDouble(reader, 10);
            var carY = GetNullableDouble(reader, 11);

            // Braking g is a window-averaged estimate (entry->apex), which runs
            // well below an instantaneous peak; minBrakingG is therefore treated as
            // an advisory floor and not used to drop hotspots from the heat map.
            var peakG = EstimateBrakingG(entrySpeed, minSpeed, durationMs);

            var corner = markerNumber is null
                ? null
                : new NearestCorner(markerNumber.Value, FormatCornerLabel(sessionId, markerNumber, markerLetter) ?? $"Turn {markerNumber}");

            incidents.Add(new RaceControlItem(
                "hard_braking",
                lapNumber,
                sessionTimeMs,
                corner is null ? $"{driverCode} hard braking" : $"{driverCode} hard braking into {corner.Label}",
                corner,
                markerX ?? carX,
                markerY ?? carY,
                driverCode,
                "info",
                new RaceControlMetrics(peakG, entrySpeed, minSpeed)));
        }

        return incidents
            .OrderByDescending(i => i.Metrics?.PeakBrakingG ?? 0)
            .Take(maxResults)
            .ToList();
    }

    private async Task<IReadOnlyList<RaceControlItem>> GetRaceControlIncidentsAsync(
        string sessionId,
        int maxResults,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT rci.session_time_ms, rci.lap_number, rci.message, sd.driver_code, rci.cluster_terms
            FROM race_control_event_index rci
            LEFT JOIN session_drivers sd
                ON sd.session_id = rci.session_id
                AND sd.driver_number = rci.racing_number
            WHERE rci.session_id = @sessionId
              AND (
                rci.message ILIKE '%spin%'
                OR rci.message ILIKE '%off track%'
                OR rci.message ILIKE '%off the track%'
                OR rci.message ILIKE '%collision%'
                OR rci.message ILIKE '%incident%'
              )
            ORDER BY rci.session_time_ms NULLS LAST
            LIMIT @maxResults
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("maxResults", maxResults);

        var incidents = new List<RaceControlItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var message = reader.GetString(2);
            var type = message.Contains("spin", StringComparison.OrdinalIgnoreCase) ? "spin" : "off_track";
            incidents.Add(new RaceControlItem(
                type,
                GetNullableInt32(reader, 1),
                GetNullableInt64(reader, 0),
                message,
                NearestCorner: null,
                X: null,
                Y: null,
                DriverCode: GetNullableString(reader, 3),
                Severity: "info",
                Metrics: null,
                ClusterTerms: GetNullableString(reader, 4)));
        }

        return incidents;
    }

    private async Task<int> GetMaxLapNumberAsync(string sessionId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT coalesce(max(lap_number), 0) FROM laps WHERE session_id = @sessionId AND NOT is_deleted";
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is int i ? i : Convert.ToInt32(value ?? 0);
    }

    private static double? EstimateBrakingG(double? entrySpeedKmh, double? minSpeedKmh, long durationMs)
    {
        if (entrySpeedKmh is null || minSpeedKmh is null || durationMs <= 0)
        {
            return null;
        }

        var deltaMs = (entrySpeedKmh.Value - minSpeedKmh.Value) / 3.6; // km/h -> m/s
        if (deltaMs <= 0)
        {
            return null;
        }

        var deceleration = deltaMs / (durationMs / 1000.0); // m/s^2
        return Math.Round(deceleration / 9.81, 2);
    }

    private static string HumanizeStatus(string status) =>
        status switch
        {
            "yellow_flag" or "yellow" => "Yellow flag",
            "safety_car" => "Safety car deployed",
            "red_flag" or "red" => "Red flag",
            "virtual_safety_car_deployed" or "vsc" => "Virtual safety car deployed",
            "virtual_safety_car_ending" => "Virtual safety car ending",
            _ => status,
        };

    private sealed record StandingRowRaw(
        string DriverCode,
        string? FullName,
        string? TeamName,
        long? LapsDone,
        double? LastEndMs,
        long? LastLapMs,
        long? BestLapMs,
        string? Compound,
        int? TyreLife,
        int PitCount,
        long[]? RecentLaps);
}
