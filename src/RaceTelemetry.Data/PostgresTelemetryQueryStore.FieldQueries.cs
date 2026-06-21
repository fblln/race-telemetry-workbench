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

    public async Task<PositionChangesResponse?> GetPositionChangesAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        using var activity = StartStoreActivity("query_store.get_position_changes", sessionId);

        var finish = await GetStandingsAsync(sessionId, null, "position", cancellationToken);
        if (finish is null)
        {
            return null;
        }

        // Prefer the real starting grid; fall back per-driver to the order after lap 1 where grid is unknown.
        var grid = await GetGridPositionsAsync(sessionId, cancellationToken);
        var lapOne = await GetStandingsAsync(sessionId, 1, "position", cancellationToken);
        var lapOnePositions = lapOne?.Items.ToDictionary(r => r.DriverCode, r => r.Position, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        int? StartPosition(string code)
        {
            if (grid.TryGetValue(code, out var g) && g > 0)
            {
                return g;
            }
            return lapOnePositions.TryGetValue(code, out var l) ? l : null;
        }

        var changes = finish.Items
            .Select(row => new { row, start = StartPosition(row.DriverCode) })
            .Where(x => x.start is not null)
            .Select(x => new PositionChange(
                x.row.DriverCode,
                x.row.FullName,
                x.start!.Value,
                x.row.Position,
                x.start!.Value - x.row.Position))
            .OrderByDescending(change => change.Delta)
            .ThenBy(change => change.FinishPosition)
            .ToList();

        return new PositionChangesResponse(sessionId, changes);
    }

    private async Task<Dictionary<string, int>> GetGridPositionsAsync(string sessionId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT driver_code, grid_position
            FROM session_drivers
            WHERE session_id = @sessionId AND grid_position IS NOT NULL
            """;
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);

        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            map[reader.GetString(0)] = reader.GetInt32(1);
        }
        return map;
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
        int maxResults,
        CancellationToken cancellationToken)
    {
        using var activity = StartStoreActivity("query_store.get_incidents", sessionId);
        activity?.SetTag("race.query.max_results", maxResults);

        if (!await SessionExistsAsync(sessionId, cancellationToken))
        {
            return null;
        }

        var trackStatusTask = GetTrackStatusIncidentsAsync(sessionId, cancellationToken);
        var raceControlTask = GetRaceControlIncidentsAsync(sessionId, maxResults, cancellationToken);

        await Task.WhenAll(trackStatusTask, raceControlTask);

        var trackStatus = await trackStatusTask;
        var raceControl = await raceControlTask;

        var typeFilter = types is { Count: > 0 }
            ? new HashSet<string>(types, StringComparer.OrdinalIgnoreCase)
            : null;

        var all = trackStatus.Concat(raceControl)
            .Where(i => typeFilter is null || typeFilter.Contains(i.Type))
            .OrderBy(i => i.SessionTimeMs is null)
            .ThenBy(i => i.SessionTimeMs ?? long.MaxValue)
            .Take(maxResults)
            .ToList();

        var lapsUnderSafetyCar = trackStatus.Count(i => i.Type is "safety_car" or "vsc");

        var summary = new RaceControlListSummary(all.Count, lapsUnderSafetyCar);
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
                Severity: severity));
        }

        return incidents;
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
