using System.Data;
using System.Diagnostics;
using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using RaceTelemetry.Contracts;

namespace RaceTelemetry.Data;

/// <summary>
/// Maps database records to contracts and formats small narrative insight values.
/// </summary>
public sealed partial class PostgresTelemetryQueryStore
{
    private static string CacheKey(string kind, string sessionId) =>
        string.Create(CultureInfo.InvariantCulture, $"race-telemetry:{kind}:{sessionId}");

    private static bool IncludesChannel(IReadOnlyList<string> channels, string channel) =>
        channels.Contains(channel, StringComparer.OrdinalIgnoreCase);

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

    private async Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("SELECT to_regclass(@tableName) IS NOT NULL");
        command.Parameters.AddWithValue("tableName", tableName);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
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
