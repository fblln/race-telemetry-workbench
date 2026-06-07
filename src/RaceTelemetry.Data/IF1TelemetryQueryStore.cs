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
}
