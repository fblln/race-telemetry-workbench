using System.Diagnostics;
using System.Globalization;
using NpgsqlTypes;
using RaceTelemetry.Contracts;

namespace RaceTelemetry.Data;

public sealed partial class PostgresTelemetryQueryStore
{
    public async Task<StrategySummaryResponse?> SummarizeStrategyAsync(
        string sessionId,
        StrategySummaryRequest request,
        CancellationToken cancellationToken)
    {
        using var activity = StartStoreActivity("query_store.summarize_strategy", sessionId);

        if (!await SessionExistsAsync(sessionId, cancellationToken))
        {
            return null;
        }

        const string sql = """
            SELECT
                pit_in.driver_code,
                pit_in.lap_number,
                pit_in.stint_number,
                pit_in.compound,
                next_stint.compound AS to_compound,
                pit_in.pit_in_session_time_ms,
                pit_out.pit_out_session_time_ms,
                CASE
                    WHEN pit_in.pit_in_session_time_ms IS NOT NULL
                     AND pit_out.pit_out_session_time_ms >= pit_in.pit_in_session_time_ms
                    THEN pit_out.pit_out_session_time_ms - pit_in.pit_in_session_time_ms
                END AS pit_lane_time_ms,
                coalesce(status.status_name, 'unknown') AS track_status,
                pit_in.is_accurate
            FROM laps pit_in
            LEFT JOIN LATERAL (
                SELECT l.pit_out_session_time_ms
                FROM laps l
                WHERE l.session_id = pit_in.session_id
                  AND l.driver_code = pit_in.driver_code
                  AND l.pit_out_session_time_ms IS NOT NULL
                  AND l.lap_number BETWEEN pit_in.lap_number AND pit_in.lap_number + 2
                ORDER BY l.lap_number
                LIMIT 1
            ) pit_out ON true
            LEFT JOIN LATERAL (
                SELECT l.compound
                FROM laps l
                WHERE l.session_id = pit_in.session_id
                  AND l.driver_code = pit_in.driver_code
                  AND l.lap_number > pit_in.lap_number
                  AND NOT l.is_deleted
                  AND (pit_in.stint_number IS NULL OR l.stint_number IS DISTINCT FROM pit_in.stint_number)
                ORDER BY l.lap_number
                LIMIT 1
            ) next_stint ON true
            LEFT JOIN LATERAL (
                SELECT p.status_name
                FROM track_status_periods p
                WHERE p.session_id = pit_in.session_id
                  AND pit_in.pit_in_session_time_ms >= p.start_time_ms
                  AND (p.end_time_ms IS NULL OR pit_in.pit_in_session_time_ms < p.end_time_ms)
                ORDER BY p.start_time_ms DESC
                LIMIT 1
            ) status ON true
            WHERE pit_in.session_id = @sessionId
              AND pit_in.pit_in_session_time_ms IS NOT NULL
              AND NOT pit_in.is_deleted
              AND (@drivers::text[] IS NULL OR pit_in.driver_code = ANY(@drivers::text[]))
            ORDER BY pit_in.lap_number, pit_in.driver_code
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sessionId", sessionId);
        AddNullable(command, "drivers", NpgsqlDbType.Array | NpgsqlDbType.Text,
            request.Drivers is { Count: > 0 }
                ? request.Drivers.Select(driver => driver.ToUpperInvariant()).ToArray()
                : null);

        var rawStops = new List<RawStrategyStop>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rawStops.Add(new RawStrategyStop(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    GetNullableInt32(reader, 2),
                    GetNullableString(reader, 3),
                    GetNullableString(reader, 4),
                    GetNullableInt64(reader, 5),
                    GetNullableInt64(reader, 6),
                    GetNullableInt64(reader, 7),
                    GetNullableString(reader, 8),
                    reader.IsDBNull(9) ? null : reader.GetBoolean(9)));
            }
        }

        var positions = await GetPositionsAsync(sessionId, null, 1, null, cancellationToken);
        var positionMap = positions?.Items.ToDictionary(item => item.DriverCode, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, DriverPositions>(StringComparer.OrdinalIgnoreCase);
        // Finishing classification so a strategy comparison can name "the top 3" without a second tool call.
        var standings = await GetStandingsAsync(sessionId, null, "position", cancellationToken);
        var finishPositions = standings?.Items.ToDictionary(item => item.DriverCode, item => item.Position, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fieldAverage = request.CompareToFieldAverage == false
            ? null
            : AverageMilliseconds(rawStops.Select(stop => stop.PitLaneTimeMs));

        var facts = new List<NarrativeFact>();
        var driverItems = new List<DriverStrategySummary>();
        foreach (var driverGroup in rawStops.GroupBy(stop => stop.DriverCode).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var stops = new List<StrategyStopSummary>();
            var factIds = new List<string>();
            foreach (var stop in driverGroup)
            {
                var beforeLap = Math.Max(1, stop.LapNumber - 1);
                var beforePosition = PositionAt(positionMap, positions?.FromLap ?? 1, stop.DriverCode, beforeLap);
                var rival = FindRival(stop, rawStops, positionMap, positions?.FromLap ?? 1, beforePosition);
                var afterLap = Math.Min(positions?.ToLap ?? stop.LapNumber + 2,
                    Math.Max(stop.LapNumber, rival?.LapNumber ?? stop.LapNumber) + 2);
                var afterPosition = PositionAt(positionMap, positions?.FromLap ?? 1, stop.DriverCode, afterLap);
                var rivalBefore = rival is null ? null : PositionAt(positionMap, positions?.FromLap ?? 1, rival.DriverCode, beforeLap);
                var rivalAfter = rival is null ? null : PositionAt(positionMap, positions?.FromLap ?? 1, rival.DriverCode, afterLap);
                var neutralized = !string.Equals(stop.TrackStatus, "track_clear", StringComparison.OrdinalIgnoreCase);
                var complete = stop.PitInSessionTimeMs is not null
                    && stop.PitOutSessionTimeMs is not null
                    && stop.PitLaneTimeMs is > 0
                    && stop.IsAccurate != false
                    && beforePosition is not null
                    && afterPosition is not null;

                var label = ClassifyStrategy(
                    stop,
                    rival,
                    beforePosition,
                    afterPosition,
                    rivalBefore,
                    rivalAfter,
                    complete,
                    neutralized);
                var quality = complete && !neutralized ? "supported" : "degraded";
                var gain = beforePosition is null || afterPosition is null ? null : beforePosition - afterPosition;

                stops.Add(new StrategyStopSummary(
                    stop.LapNumber,
                    stop.FromCompound,
                    stop.ToCompound,
                    stop.PitInSessionTimeMs,
                    stop.PitOutSessionTimeMs,
                    stop.PitLaneTimeMs,
                    fieldAverage,
                    stop.TrackStatus,
                    label,
                    rival?.DriverCode,
                    beforePosition,
                    afterPosition,
                    gain,
                    quality));

                var factId = $"strategy-{stop.DriverCode.ToLowerInvariant()}-{stop.LapNumber}";
                factIds.Add(factId);
                var text = BuildStrategyFactText(stop, label, rival?.DriverCode, gain, fieldAverage);
                facts.Add(new NarrativeFact(
                    factId,
                    "strategy_stop",
                    text,
                    stop.PitLaneTimeMs,
                    "ms",
                    [
                        new EvidenceReference("strategy/summarize", stop.DriverCode, stop.LapNumber, stop.StintNumber,
                            stop.PitInSessionTimeMs, stop.PitOutSessionTimeMs),
                        new EvidenceReference("positions", stop.DriverCode, beforeLap),
                        new EvidenceReference("positions", stop.DriverCode, afterLap)
                    ],
                    quality,
                    quality == "supported" ? "assert" : "caveat"));
            }

            var finish = finishPositions.TryGetValue(driverGroup.Key, out var pos) ? pos : (int?)null;
            driverItems.Add(new DriverStrategySummary(driverGroup.Key, finish, stops, factIds));
        }

        // Order by finishing position (classified drivers first) so "the top N" is obvious in the evidence.
        driverItems = driverItems
            .OrderBy(item => item.FinishPosition ?? int.MaxValue)
            .ThenBy(item => item.DriverCode, StringComparer.Ordinal)
            .ToList();

        return new StrategySummaryResponse(sessionId, driverItems, facts, BuildStoryQuality(facts));
    }

    public async Task<RaceDebriefResponse?> GenerateRaceDebriefAsync(
        string sessionId,
        RaceDebriefRequest request,
        CancellationToken cancellationToken)
    {
        using var activity = StartStoreActivity("query_store.generate_race_debrief", sessionId);

        var sections = new HashSet<string>(
            request.Sections is { Count: > 0 } ? request.Sections : ["overview", "strategy", "incidents", "weather"],
            StringComparer.OrdinalIgnoreCase);

        var storyTask = GetRaceStoryAsync(sessionId, 100, cancellationToken);
        var standingsTask = GetStandingsAsync(sessionId, null, "position", cancellationToken);
        var strategyTask = sections.Contains("strategy")
            ? SummarizeStrategyAsync(sessionId, new StrategySummaryRequest(request.Drivers, true), cancellationToken)
            : Task.FromResult<StrategySummaryResponse?>(null);
        var incidentsTask = sections.Contains("incidents")
            ? GetRaceControlAsync(sessionId, null, 100, cancellationToken)
            : Task.FromResult<RaceControlResponse?>(null);

        await Task.WhenAll(storyTask, standingsTask, strategyTask, incidentsTask);
        var story = await storyTask;
        if (story is null)
        {
            return null;
        }

        var standings = await standingsTask;
        var strategy = await strategyTask;
        var incidents = await incidentsTask;
        var facts = new List<NarrativeFact>();

        var winner = standings?.Items.OrderBy(item => item.Position).FirstOrDefault()?.DriverCode;
        if (!string.IsNullOrWhiteSpace(winner))
        {
            facts.Add(new NarrativeFact(
                "debrief-winner",
                "winner",
                $"{winner} was classified first after {story.Session.LapCount} laps.",
                1,
                "position",
                [new EvidenceReference("standings", winner, story.Session.LapCount)]));
        }

        if (strategy is not null)
        {
            facts.AddRange(strategy.Facts);
        }

        var filteredIncidents = (incidents?.Items ?? [])
            .Where(item => request.Drivers is not { Count: > 0 }
                || item.DriverCode is null
                || request.Drivers.Contains(item.DriverCode, StringComparer.OrdinalIgnoreCase))
            .Take(50)
            .ToArray();
        foreach (var incident in filteredIncidents.Take(10))
        {
            var id = $"incident-{facts.Count + 1}";
            facts.Add(new NarrativeFact(
                id,
                "incident",
                incident.LapNumber is null ? incident.Message : $"Lap {incident.LapNumber}: {incident.Message}",
                incident.LapNumber,
                incident.LapNumber is null ? null : "lap",
                [new EvidenceReference("incidents", incident.DriverCode, incident.LapNumber, FromSessionTimeMs: incident.SessionTimeMs)]));
        }

        RaceDebriefWeather? weather = null;
        if (sections.Contains("weather") && story.Weather is not null)
        {
            var summary = story.Weather.RainfallObserved
                ? "Rainfall was observed during the session."
                : "No rainfall was observed during the session.";
            weather = new RaceDebriefWeather(
                summary,
                story.Weather.AirTempMinC,
                story.Weather.AirTempMaxC,
                story.Weather.TrackTempMinC,
                story.Weather.TrackTempMaxC,
                story.Weather.RainfallObserved);
            facts.Add(new NarrativeFact(
                "debrief-weather",
                "weather",
                summary,
                story.Weather.RainfallObserved ? 1 : 0,
                "boolean",
                [new EvidenceReference("weather/trend")]));
        }

        RaceDebriefOverview? overview = null;
        if (sections.Contains("overview"))
        {
            var keyStrategyFact = strategy?.Facts.FirstOrDefault(fact => fact.NarrationPolicy == "assert");
            var headline = winner is null
                ? "The imported classification is unavailable."
                : keyStrategyFact is null
                    ? $"{winner} won the {story.Session.EventName}."
                    : $"{winner} won the {story.Session.EventName}; {keyStrategyFact.Text}";
            overview = new RaceDebriefOverview(winner, headline, story.Session.LapCount);
        }

        return new RaceDebriefResponse(
            sessionId,
            overview,
            strategy,
            sections.Contains("incidents") ? filteredIncidents : [],
            weather,
            facts,
            BuildStoryQuality(facts));
    }

    private static RawStrategyStop? FindRival(
        RawStrategyStop stop,
        IReadOnlyList<RawStrategyStop> allStops,
        IReadOnlyDictionary<string, DriverPositions> positions,
        int fromLap,
        int? driverPosition)
    {
        return allStops
            .Where(candidate => !string.Equals(candidate.DriverCode, stop.DriverCode, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => Math.Abs(candidate.LapNumber - stop.LapNumber) <= 3)
            .Select(candidate => new
            {
                Stop = candidate,
                Position = PositionAt(positions, fromLap, candidate.DriverCode, Math.Max(1, stop.LapNumber - 1))
            })
            .Where(candidate => candidate.Position is not null)
            .OrderBy(candidate => driverPosition is null ? int.MaxValue : Math.Abs(candidate.Position!.Value - driverPosition.Value))
            .ThenBy(candidate => Math.Abs(candidate.Stop.LapNumber - stop.LapNumber))
            .Select(candidate => candidate.Stop)
            .FirstOrDefault();
    }

    private static string ClassifyStrategy(
        RawStrategyStop stop,
        RawStrategyStop? rival,
        int? before,
        int? after,
        int? rivalBefore,
        int? rivalAfter,
        bool complete,
        bool neutralized)
    {
        if (!complete || neutralized)
        {
            return "unknown";
        }

        if (rival is null)
        {
            return "scheduled";
        }

        var reversed = before is not null && after is not null && rivalBefore is not null && rivalAfter is not null
            && before > rivalBefore && after < rivalAfter;
        if (reversed && stop.LapNumber < rival.LapNumber)
        {
            return "undercut";
        }

        if (reversed && stop.LapNumber > rival.LapNumber)
        {
            return "overcut";
        }

        return stop.LapNumber >= rival.LapNumber && stop.LapNumber - rival.LapNumber <= 1
            ? "reactive"
            : "scheduled";
    }

    private static int? PositionAt(
        IReadOnlyDictionary<string, DriverPositions> positions,
        int fromLap,
        string driverCode,
        int lapNumber)
    {
        if (!positions.TryGetValue(driverCode, out var item))
        {
            return null;
        }

        var index = lapNumber - fromLap;
        return index >= 0 && index < item.Positions.Count ? item.Positions[index] : null;
    }

    private static long? AverageMilliseconds(IEnumerable<long?> values)
    {
        var available = values.Where(value => value is > 0).Select(value => value!.Value).ToArray();
        return available.Length == 0 ? null : (long)Math.Round(available.Average());
    }

    private static string BuildStrategyFactText(
        RawStrategyStop stop,
        string label,
        string? rival,
        int? positionGain,
        long? fieldAverage)
    {
        var compound = stop.ToCompound is null ? "an unknown compound" : stop.ToCompound;
        var text = $"{stop.DriverCode} stopped on lap {stop.LapNumber} for {compound}";
        if (stop.PitLaneTimeMs is not null)
        {
            text += $" in {(stop.PitLaneTimeMs.Value / 1000d).ToString("0.000", CultureInfo.InvariantCulture)}s";
        }
        if (fieldAverage is not null && stop.PitLaneTimeMs is not null)
        {
            var delta = stop.PitLaneTimeMs.Value - fieldAverage.Value;
            text += $", {Math.Abs(delta) / 1000d:0.000}s {(delta <= 0 ? "faster" : "slower")} than the field average";
        }
        if (label is "undercut" or "overcut")
        {
            text += $"; the {label} on {rival} changed track position";
        }
        else if (positionGain is not null && positionGain != 0)
        {
            text += $"; net position change {positionGain:+#;-#;0}";
        }
        return text + ".";
    }

    private static StoryQuality BuildStoryQuality(IReadOnlyList<NarrativeFact> facts)
    {
        var supported = facts.Count(fact => fact.QualityStatus == "supported" && fact.NarrationPolicy != "omit");
        var degraded = facts.Count(fact => fact.QualityStatus == "degraded" && fact.NarrationPolicy != "omit");
        var omitted = facts.Count(fact => fact.NarrationPolicy == "omit");
        var status = degraded > 0 ? "degraded" : supported > 0 ? "supported" : "unavailable";
        var warnings = degraded > 0 ? ["Some facts require a data-quality caveat."] : Array.Empty<string>();
        return new StoryQuality(status, supported, degraded, omitted, warnings);
    }

    private sealed record RawStrategyStop(
        string DriverCode,
        int LapNumber,
        int? StintNumber,
        string? FromCompound,
        string? ToCompound,
        long? PitInSessionTimeMs,
        long? PitOutSessionTimeMs,
        long? PitLaneTimeMs,
        string? TrackStatus,
        bool? IsAccurate);
}
