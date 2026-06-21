using RaceTelemetry.Contracts;

namespace RaceTelemetry.Data;

public interface IF1TelemetryQueryStore
{
    Task<IReadOnlyList<SessionSummary>> GetSessionsAsync(
        int? year,
        string? eventName,
        string? sessionType,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DriverSummary>?> GetDriversAsync(
        string sessionId,
        CancellationToken cancellationToken);

    Task<SessionFactsResponse?> GetSessionFactsAsync(
        string sessionId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<LapSummary>?> GetLapsAsync(
        string sessionId,
        string driverCode,
        CancellationToken cancellationToken);

    Task<ReplayMetadata?> GetReplayMetadataAsync(
        string sessionId,
        CancellationToken cancellationToken);

    Task<LapTelemetryResponse?> GetLapTelemetryAsync(
        string sessionId,
        string driverCode,
        int lapNumber,
        IReadOnlyList<string> channels,
        int sampleEvery,
        int maxSamples,
        CancellationToken cancellationToken);

    Task<LapQualityResponse?> GetLapQualityAsync(
        string sessionId,
        string driverCode,
        int lapNumber,
        CancellationToken cancellationToken);

    Task<LapStoryResponse?> GetLapStoryAsync(
        string sessionId,
        string driverCode,
        int lapNumber,
        CancellationToken cancellationToken);

    Task<LapBrakingZonesResponse?> GetLapBrakingZonesAsync(
        string sessionId,
        string driverCode,
        int lapNumber,
        int brakeThresholdPct,
        int minimumDurationMs,
        CancellationToken cancellationToken);

    Task<LapComparisonResponse?> CompareLapsAsync(
        string sessionId,
        string driverA,
        int lapA,
        string driverB,
        int lapB,
        IReadOnlyList<string> channels,
        int timeStepMs,
        CancellationToken cancellationToken);

    Task<LapComparisonByDistanceResponse?> CompareLapsByDistanceAsync(
        string sessionId,
        string driverA,
        int lapA,
        string driverB,
        int lapB,
        double? startDistanceM,
        double? endDistanceM,
        CancellationToken cancellationToken);

    Task<LapComparisonStoryResponse?> CompareLapsStoryAsync(
        string sessionId,
        string driverA,
        int lapA,
        string driverB,
        int lapB,
        int segmentCount,
        CancellationToken cancellationToken);

    Task<RaceStoryResponse?> GetRaceStoryAsync(
        string sessionId,
        int raceControlLimit,
        CancellationToken cancellationToken);

    Task<TelemetryAggregateResponse?> AggregateTelemetryAsync(
        string sessionId,
        TelemetryAggregateRequest request,
        CancellationToken cancellationToken);

    Task<TelemetryWindowResponse?> DetectTelemetryWindowsAsync(
        string sessionId,
        TelemetryWindowRequest request,
        CancellationToken cancellationToken);

    Task<StintAnalysisResponse?> AnalyzeDriverStintsAsync(
        string sessionId,
        StintAnalysisRequest request,
        CancellationToken cancellationToken);

    Task<PitStopAnalysisResponse?> AnalyzePitStopsAsync(
        string sessionId,
        PitStopAnalysisRequest request,
        CancellationToken cancellationToken);

    Task<StrategySummaryResponse?> SummarizeStrategyAsync(
        string sessionId,
        StrategySummaryRequest request,
        CancellationToken cancellationToken);

    Task<RaceDebriefResponse?> GenerateRaceDebriefAsync(
        string sessionId,
        RaceDebriefRequest request,
        CancellationToken cancellationToken);

    Task<WeatherTrendResponse?> GetWeatherTrendAsync(
        string sessionId,
        WeatherTrendRequest request,
        CancellationToken cancellationToken);

    Task<RaceControlTimelineResponse?> GetRaceControlTimelineAsync(
        string sessionId,
        RaceControlTimelineRequest request,
        CancellationToken cancellationToken);

    Task<CircuitContextResponse?> GetCircuitContextAsync(
        string sessionId,
        CancellationToken cancellationToken);

    Task<ReplayChunkResponse?> GetReplayChunkAsync(
        string sessionId,
        long fromMs,
        long durationMs,
        IReadOnlyList<string>? drivers,
        IReadOnlyList<string> channels,
        int sampleEvery,
        CancellationToken cancellationToken);

    Task<ReplayContextResponse?> GetReplayContextAsync(
        string sessionId,
        long fromMs,
        long durationMs,
        bool includeWeather,
        bool includeTrackStatus,
        bool includeRaceControl,
        CancellationToken cancellationToken);

    Task<TelemetryEventSearchResponse?> SearchTelemetryEventsAsync(
        string sessionId,
        TelemetryEventSearchRequest request,
        CancellationToken cancellationToken);

    Task<StandingsResponse?> GetStandingsAsync(
        string sessionId,
        int? atLap,
        string sortBy,
        CancellationToken cancellationToken);

    Task<PositionChangesResponse?> GetPositionChangesAsync(
        string sessionId,
        CancellationToken cancellationToken);

    Task<RaceControlResponse?> GetRaceControlAsync(
        string sessionId,
        IReadOnlyList<string>? types,
        int maxResults,
        CancellationToken cancellationToken);

    Task<PositionsResponse?> GetPositionsAsync(
        string sessionId,
        IReadOnlyList<string>? drivers,
        int? fromLap,
        int? toLap,
        CancellationToken cancellationToken);
}
