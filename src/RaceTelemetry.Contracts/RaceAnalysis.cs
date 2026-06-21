namespace RaceTelemetry.Contracts;

public sealed record LapStoryResponse(
    string SessionId,
    string DriverCode,
    int LapNumber,
    long? LapTimeMs,
    IReadOnlyList<long?> SectorTimesMs,
    string? Compound,
    int? TyreLife,
    double? PeakSpeedKmh,
    double? AverageSpeedKmh,
    double? AverageThrottlePct,
    double? AverageBrakePct,
    int TelemetrySamples,
    IReadOnlyList<AnalysisInsight> Insights);

public sealed record LapBrakingZonesResponse(
    string SessionId,
    string DriverCode,
    int LapNumber,
    int BrakeThresholdPct,
    int MinimumDurationMs,
    IReadOnlyList<LapBrakingZone> Items);

public sealed record LapBrakingZone(
    int Index,
    long StartLapTimeMs,
    long EndLapTimeMs,
    long DurationMs,
    double? EntrySpeedKmh,
    double? MinimumSpeedKmh,
    double? ExitSpeedKmh,
    double? MaxBrakePct,
    string? NearestCorner,
    double? DistanceToCorner);

public sealed record LapComparisonStoryResponse(
    string SessionId,
    string DriverA,
    int LapA,
    string DriverB,
    int LapB,
    long? LapTimeDeltaMs,
    IReadOnlyList<long?> SectorDeltasMs,
    double? PeakSpeedDeltaKmh,
    double? AverageSpeedDeltaKmh,
    IReadOnlyList<LapComparisonSegment> Segments,
    IReadOnlyList<AnalysisInsight> Insights);

public sealed record LapComparisonSegment(
    int Segment,
    long StartLapTimeMs,
    long EndLapTimeMs,
    double? AverageSpeedDeltaKmh,
    double? AverageThrottleDeltaPct,
    double? AverageBrakeDeltaPct,
    string Advantage);

/// <summary>Formats race times the F1 way so the agent echoes them instead of doing the maths.</summary>
public static class RaceTime
{
    /// <summary>Lap time as m:ss.sss, for example 73481 -> "1:13.481".</summary>
    public static string LapTime(long ms) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{ms / 60_000}:{(ms % 60_000) / 1000d:00.000}");
}

/// <summary>
/// Headline, pre-counted facts for one session so the agent can answer "how many / who / what was the"
/// questions without counting verbose arrays. Counts come straight from the DB, not the LLM.
/// </summary>
public sealed record SessionFactsResponse(
    string SessionId,
    string CircuitName,
    string? Country,
    int DriverCount,
    int TotalLaps,
    int SafetyCarDeployments,
    int RedFlagCount,
    int VirtualSafetyCarDeployments,
    string? FastestLapDriver,
    long? FastestLapMs,
    string? TopSpeedDriver,
    double? TopSpeedKmh,
    double? PeakTrackTempC,
    bool RainObserved)
{
    /// <summary>Pre-formatted fastest lap (m:ss.sss) so the agent never converts milliseconds itself.</summary>
    public string? FastestLapDisplay => FastestLapMs is { } ms ? RaceTime.LapTime(ms) : null;
}

public sealed record RaceStoryResponse(
    string SessionId,
    SessionSummary Session,
    WeatherSummary? Weather,
    IReadOnlyList<RaceStintSummary> Stints,
    IReadOnlyList<PitStopSummary> PitStops,
    IReadOnlyList<TrackStatusPeriodSummary> TrackStatusPeriods,
    IReadOnlyList<RaceControlSummary> RaceControlMessages,
    IReadOnlyList<AnalysisInsight> Insights);

public sealed record RaceStintSummary(
    string DriverCode,
    int StintNumber,
    string? Compound,
    int FirstLapNumber,
    int LastLapNumber,
    int Laps,
    int? MinTyreLife,
    int? MaxTyreLife,
    long? AverageLapTimeMs,
    long? BestLapTimeMs,
    long? WorstLapTimeMs);

public sealed record PitStopSummary(
    string DriverCode,
    int LapNumber,
    string Kind,
    int? StintNumber,
    string? Compound,
    int? TyreLife,
    long? LapTimeMs,
    long? SessionTimeMs);

public sealed record TrackStatusPeriodSummary(
    long StartTimeMs,
    long? EndTimeMs,
    string StatusCode,
    string StatusName,
    string? Message);

public sealed record RaceControlSummary(
    long? SessionTimeMs,
    int? LapNumber,
    string? Category,
    string Message,
    string? Status,
    string? Flag,
    string? Scope,
    string? Sector,
    int? RacingNumber);

public sealed record AnalysisInsight(
    string Kind,
    string Text,
    double? Value = null,
    string? Unit = null);
