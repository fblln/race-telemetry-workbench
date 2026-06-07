using System.Data;
using Npgsql;
using NpgsqlTypes;
using RaceTelemetry.Contracts;

namespace RaceTelemetry.Data;

public sealed class PostgresTelemetryQueryStore(NpgsqlDataSource dataSource) : IF1TelemetryQueryStore
{
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

    public async Task<IReadOnlyList<SessionSummary>> GetSessionsAsync(
        int? year,
        string? eventName,
        string? sessionType,
        CancellationToken cancellationToken)
    {
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
        if (!await SessionExistsAsync(sessionId, cancellationToken))
        {
            return null;
        }

        const string sql = """
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
            ORDER BY sd.driver_code
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);

        var drivers = new List<DriverSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            drivers.Add(new DriverSummary(
                reader.GetString(0),
                reader.GetString(1),
                GetNullableString(reader, 2),
                GetNullableString(reader, 3),
                GetNullableString(reader, 4),
                reader.GetInt32(5)));
        }

        return drivers;
    }

    public async Task<IReadOnlyList<LapSummary>?> GetLapsAsync(
        string sessionId,
        string driverCode,
        CancellationToken cancellationToken)
    {
        if (!await DriverExistsAsync(sessionId, driverCode, cancellationToken))
        {
            return null;
        }

        const string sql = """
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
            ORDER BY lap_number
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("driverCode", driverCode);

        var laps = new List<LapSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            laps.Add(new LapSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                GetNullableInt64(reader, 4),
                GetNullableInt32(reader, 5),
                reader.GetBoolean(6),
                reader.GetBoolean(7)));
        }

        return laps;
    }

    public async Task<ReplayMetadata?> GetReplayMetadataAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (!await SessionExistsAsync(sessionId, cancellationToken))
        {
            return null;
        }

        var (startUtc, endUtc, startMs, endMs) = await GetReplayBoundsAsync(sessionId, cancellationToken);
        var drivers = await GetReplayDriversAsync(sessionId, cancellationToken);
        var trackMap = await GetTrackMapAsync(sessionId, cancellationToken);
        var overlays = await GetEventOverlayAvailabilityAsync(sessionId, cancellationToken);
        var weatherSummary = await GetWeatherSummaryAsync(sessionId, cancellationToken);

        return new ReplayMetadata(
            sessionId,
            startUtc,
            endUtc,
            Math.Max(0, endMs - startMs),
            drivers,
            startMs,
            endMs,
            ReplayChannels,
            ContextChannels,
            trackMap,
            overlays,
            weatherSummary,
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
        if (!await LapExistsAsync(sessionId, driverCode, lapNumber, cancellationToken))
        {
            return null;
        }

        const string sql = """
            WITH ordered AS (
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
            )
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
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("driverCode", driverCode);
        command.Parameters.AddWithValue("lapNumber", lapNumber);
        command.Parameters.AddWithValue("sampleEvery", sampleEvery);
        command.Parameters.AddWithValue("maxSamples", maxSamples);

        var samples = new List<TelemetrySample>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            samples.Add(ReadTelemetrySample(reader));
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
        if (!await LapExistsAsync(sessionId, driverA, lapA, cancellationToken)
            || !await LapExistsAsync(sessionId, driverB, lapB, cancellationToken))
        {
            return null;
        }

        var a = await GetComparisonBucketsAsync(sessionId, driverA, lapA, timeStepMs, cancellationToken);
        var b = await GetComparisonBucketsAsync(sessionId, driverB, lapB, timeStepMs, cancellationToken);
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

        var summary = await GetLapComparisonSummaryAsync(
            sessionId,
            driverA,
            lapA,
            driverB,
            lapB,
            cancellationToken);

        return new LapComparisonResponse(
            sessionId,
            driverA.ToUpperInvariant(),
            lapA,
            driverB.ToUpperInvariant(),
            lapB,
            timeStepMs,
            channels,
            points,
            summary);
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
        if (!await SessionExistsAsync(sessionId, cancellationToken))
        {
            return null;
        }

        if (drivers is { Count: > 0 } && !await DriversExistAsync(sessionId, drivers, cancellationToken))
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
        if (!await SessionExistsAsync(sessionId, cancellationToken))
        {
            return null;
        }

        var weather = includeWeather
            ? await GetWeatherSamplesAsync(sessionId, fromMs, durationMs, cancellationToken)
            : [];
        var trackStatus = includeTrackStatus
            ? await GetTrackStatusEventsAsync(sessionId, fromMs, durationMs, cancellationToken)
            : [];
        var raceControl = includeRaceControl
            ? await GetRaceControlMessagesAsync(sessionId, fromMs, durationMs, cancellationToken)
            : [];

        return new ReplayContextResponse(sessionId, fromMs, durationMs, weather, trackStatus, raceControl);
    }

    public async Task<TelemetryEventSearchResponse?> SearchTelemetryEventsAsync(
        string sessionId,
        TelemetryEventSearchRequest request,
        CancellationToken cancellationToken)
    {
        if (!await SessionExistsAsync(sessionId, cancellationToken))
        {
            return null;
        }

        const string sql = """
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
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new TelemetryEventCandidate(
                GetNullableDateTimeOffset(reader, 0) ?? DateTimeOffset.UnixEpoch,
                reader.GetString(1),
                GetNullableInt32(reader, 2),
                GetNullableInt64(reader, 3),
                GetNullableInt64(reader, 4),
                GetNullableDouble(reader, 5),
                GetNullableDouble(reader, 6),
                GetNullableDouble(reader, 7),
                GetNullableInt32(reader, 8),
                reader.GetString(9)));
        }

        return new TelemetryEventSearchResponse(sessionId, items);
    }

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

    private static TelemetrySample ReadTelemetrySample(NpgsqlDataReader reader) =>
        new(
            GetNullableDateTimeOffset(reader, 0) ?? DateTimeOffset.UnixEpoch,
            GetNullableInt64(reader, 1),
            GetNullableInt64(reader, 2),
            GetNullableDouble(reader, 3),
            GetNullableDouble(reader, 4),
            GetNullableDouble(reader, 5),
            GetNullableInt32(reader, 6),
            GetNullableDouble(reader, 7),
            GetNullableInt32(reader, 8));

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

    private static readonly TelemetryChannelValues EmptyTelemetryChannelValues = new(
        null,
        null,
        null,
        null,
        null);
}
