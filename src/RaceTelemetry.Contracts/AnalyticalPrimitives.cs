namespace RaceTelemetry.Contracts;

public sealed record LapRange(
    int? From,
    int? To);

public sealed record TelemetryAggregateRequest(
    IReadOnlyList<string>? Drivers,
    IReadOnlyList<string>? GroupBy,
    IReadOnlyList<string>? Metrics,
    TelemetryAggregateFilters? Filters,
    int? TimeBucketMs,
    int? Limit);

public sealed record TelemetryAggregateFilters(
    LapRange? LapRange,
    IReadOnlyList<string>? Compound,
    bool? ExcludePitLaps,
    IReadOnlyList<string>? TrackStatus);

public sealed record TelemetryAggregateResponse(
    string SessionId,
    IReadOnlyList<string> GroupBy,
    IReadOnlyList<string> Metrics,
    IReadOnlyList<TelemetryAggregateItem> Items);

public sealed record TelemetryAggregateItem(
    string? DriverCode,
    int? LapNumber,
    int? StintNumber,
    string? Compound,
    string? TrackStatus,
    long? BucketStartMs,
    long? BucketEndMs,
    int SampleCount,
    double? AverageSpeedKmh,
    double? MaxSpeedKmh,
    double? AverageThrottlePct,
    double? AverageBrakePct,
    long? BrakeTimeMs,
    long? DrsActiveTimeMs,
    int? ThrottleLiftCount,
    long? HighSpeedTimeMs);

public sealed record TelemetryWindowRequest(
    IReadOnlyList<string>? Drivers,
    string EventType,
    LapRange? LapRange,
    int? MinimumDurationMs,
    bool? IncludeNearestCorner,
    int? Limit);

public sealed record TelemetryWindowResponse(
    string SessionId,
    string EventType,
    int MinimumDurationMs,
    IReadOnlyList<TelemetryWindowItem> Items);

public sealed record TelemetryWindowItem(
    string DriverCode,
    int? LapNumber,
    long? StartSessionTimeMs,
    long? EndSessionTimeMs,
    long? StartLapTimeMs,
    long? EndLapTimeMs,
    long DurationMs,
    string? NearestCorner,
    double? DistanceToCorner,
    TelemetryWindowSummary Summary);

public sealed record TelemetryWindowSummary(
    double? EntrySpeedKmh,
    double? MinimumSpeedKmh,
    double? MaxSpeedKmh,
    double? ExitSpeedKmh,
    double? MaxBrakePct,
    double? AverageThrottlePct);

public sealed record StintAnalysisRequest(
    IReadOnlyList<string>? Drivers,
    IReadOnlyList<string>? Compound,
    bool? ExcludePitLaps,
    int? MinimumLaps,
    IReadOnlyList<string>? Metrics);

public sealed record StintAnalysisResponse(
    string SessionId,
    IReadOnlyList<string> Metrics,
    IReadOnlyList<DriverStintAnalysisItem> Items);

public sealed record DriverStintAnalysisItem(
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
    long? WorstLapTimeMs,
    double? LapTimeSlopeMsPerLap,
    IReadOnlyList<AnalysisInsight> Insights);
