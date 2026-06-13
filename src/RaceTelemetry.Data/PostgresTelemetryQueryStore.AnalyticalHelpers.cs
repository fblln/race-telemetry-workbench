using System.Data;
using System.Diagnostics;
using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using RaceTelemetry.Contracts;

namespace RaceTelemetry.Data;

/// <summary>
/// Normalizes analytical query inputs and builds insight objects for aggregate, stint, weather, and control timelines.
/// </summary>
public sealed partial class PostgresTelemetryQueryStore
{
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
}
