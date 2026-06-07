using RaceTelemetry.Contracts;

namespace RaceTelemetry.Data;

public sealed class InMemoryTelemetryQueryStore : IF1TelemetryQueryStore
{
    private static readonly SessionSummary Monza2025 = new(
        SessionId: "2025-italian-grand-prix-r",
        Year: 2025,
        EventName: "Italian Grand Prix",
        SessionType: "R",
        CircuitName: "Monza",
        Country: "Italy",
        SessionStartUtc: new DateTimeOffset(2025, 9, 7, 13, 0, 0, TimeSpan.Zero),
        DriverCount: 20,
        LapCount: 1060);

    private static readonly DriverSummary[] Drivers =
    [
        new(Monza2025.SessionId, "LEC", "16", "Charles Leclerc", "Ferrari", 53),
        new(Monza2025.SessionId, "VER", "1", "Max Verstappen", "Red Bull Racing", 53)
    ];

    private static readonly Dictionary<string, LapSummary[]> LapsByDriver = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LEC"] =
        [
            new("2025-italian-grand-prix-r-lec-1", Monza2025.SessionId, "LEC", 1, 92_450, 4, false, false),
            new("2025-italian-grand-prix-r-lec-2", Monza2025.SessionId, "LEC", 2, 85_120, 4, false, false)
        ],
        ["VER"] =
        [
            new("2025-italian-grand-prix-r-ver-1", Monza2025.SessionId, "VER", 1, 91_980, 1, false, false),
            new("2025-italian-grand-prix-r-ver-2", Monza2025.SessionId, "VER", 2, 84_870, 1, false, false)
        ]
    };

    private static readonly ReplayMetadata ReplayMetadata = new(
        SessionId: Monza2025.SessionId,
        StartTimeUtc: Monza2025.SessionStartUtc,
        EndTimeUtc: Monza2025.SessionStartUtc?.AddMilliseconds(5_400_000),
        DurationMs: 5_400_000,
        Drivers: ["LEC", "VER"],
        ReplayStartMs: 0,
        ReplayEndMs: 5_400_000,
        AvailableChannels:
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
        ],
        ContextChannels:
        [
            "weather",
            "track_status",
            "session_status",
            "race_control",
            "circuit_markers"
        ],
        TrackMap: new TrackMapMetadata(
            RotationDegrees: 95.0,
            OutlineSource: "position_samples",
            Markers:
            [
                new CircuitMarker("corner", 1, null, -569.58, 8153.72, 153.79, null)
            ]),
        EventOverlays: new EventOverlayAvailability(true, true, true),
        WeatherSummary: new WeatherSummary(32.2, 34.1, 43.5, 54.6, false),
        RecommendedChunkDurationMs: 30_000,
        SupportedReplaySpeeds: [0.25, 0.5, 1, 2, 5, 10, 20],
        DefaultReplaySpeed: 1);

    public Task<IReadOnlyList<SessionSummary>> GetSessionsAsync(
        int? year,
        string? eventName,
        string? sessionType,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var matches = (year is null || Monza2025.Year == year)
            && (string.IsNullOrWhiteSpace(eventName)
                || Monza2025.EventName.Contains(eventName, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(sessionType)
                || string.Equals(Monza2025.SessionType, sessionType, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult<IReadOnlyList<SessionSummary>>(matches ? [Monza2025] : []);
    }

    public Task<IReadOnlyList<DriverSummary>?> GetDriversAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<IReadOnlyList<DriverSummary>?>(
            IsKnownSession(sessionId) ? Drivers : null);
    }

    public Task<IReadOnlyList<LapSummary>?> GetLapsAsync(
        string sessionId,
        string driverCode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsKnownSession(sessionId))
        {
            return Task.FromResult<IReadOnlyList<LapSummary>?>(null);
        }

        return Task.FromResult<IReadOnlyList<LapSummary>?>(
            LapsByDriver.TryGetValue(driverCode, out var laps) ? laps : null);
    }

    public Task<ReplayMetadata?> GetReplayMetadataAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<ReplayMetadata?>(
            IsKnownSession(sessionId) ? ReplayMetadata : null);
    }

    public Task<LapTelemetryResponse?> GetLapTelemetryAsync(
        string sessionId,
        string driverCode,
        int lapNumber,
        IReadOnlyList<string> channels,
        int sampleEvery,
        int maxSamples,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsKnownSession(sessionId) || !LapsByDriver.TryGetValue(driverCode, out var laps)
            || !laps.Any(lap => lap.LapNumber == lapNumber))
        {
            return Task.FromResult<LapTelemetryResponse?>(null);
        }

        return Task.FromResult<LapTelemetryResponse?>(new LapTelemetryResponse(
            sessionId,
            driverCode.ToUpperInvariant(),
            lapNumber,
            channels,
            [
                new TelemetrySample(DateTimeOffset.UtcNow, 190_000, 10_000, 312, 100, 0, 8, 11_750, 10),
                new TelemetrySample(DateTimeOffset.UtcNow.AddSeconds(10), 200_000, 20_000, 184, 5, 92, 4, 10_400, 0)
            ]));
    }

    public Task<LapComparisonResponse?> CompareLapsAsync(
        string sessionId,
        string driverA,
        int lapA,
        string driverB,
        int lapB,
        IReadOnlyList<string> channels,
        int timeStepMs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsKnownSession(sessionId)
            || !LapsByDriver.TryGetValue(driverA, out var lapsA)
            || !LapsByDriver.TryGetValue(driverB, out var lapsB)
            || !lapsA.Any(lap => lap.LapNumber == lapA)
            || !lapsB.Any(lap => lap.LapNumber == lapB))
        {
            return Task.FromResult<LapComparisonResponse?>(null);
        }

        return Task.FromResult<LapComparisonResponse?>(new LapComparisonResponse(
            sessionId,
            driverA.ToUpperInvariant(),
            lapA,
            driverB.ToUpperInvariant(),
            lapB,
            timeStepMs,
            channels,
            [
                new LapComparisonPoint(
                    10_000,
                    new TelemetryChannelValues(312, 100, 0, 11_750, 8),
                    new TelemetryChannelValues(295, 95, 0, 11_600, 8),
                    new TelemetryChannelValues(17, 5, 0, 150, 0))
            ],
            new LapComparisonSummary(-430, [-200, -100, -130], 17, 8.5)));
    }

    public Task<ReplayChunkResponse?> GetReplayChunkAsync(
        string sessionId,
        long fromMs,
        long durationMs,
        IReadOnlyList<string>? drivers,
        IReadOnlyList<string> channels,
        int sampleEvery,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsKnownSession(sessionId))
        {
            return Task.FromResult<ReplayChunkResponse?>(null);
        }

        var selectedDrivers = drivers is { Count: > 0 } ? drivers : ["LEC", "VER"];
        var items = selectedDrivers
            .Select(driver => new ReplayDriverChunk(
                driver.ToUpperInvariant(),
                [new ReplaySample(fromMs + 12, 1, 298.2, 100, 0, 8, 11_680, 0, 1234.5, -341.2, 0)]))
            .ToArray();

        return Task.FromResult<ReplayChunkResponse?>(new ReplayChunkResponse(
            sessionId,
            fromMs,
            durationMs,
            fromMs + durationMs,
            channels,
            items));
    }

    public Task<ReplayContextResponse?> GetReplayContextAsync(
        string sessionId,
        long fromMs,
        long durationMs,
        bool includeWeather,
        bool includeTrackStatus,
        bool includeRaceControl,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsKnownSession(sessionId))
        {
            return Task.FromResult<ReplayContextResponse?>(null);
        }

        return Task.FromResult<ReplayContextResponse?>(new ReplayContextResponse(
            sessionId,
            fromMs,
            durationMs,
            includeWeather ? [new WeatherSample(fromMs, 33.2, 52.1, 37, 993.9, false, 207, 1)] : [],
            includeTrackStatus ? [new TrackStatusEvent(fromMs, "1", "Track clear")] : [],
            includeRaceControl ? [new RaceControlMessage(fromMs, 1, "Drs", "DRS DISABLED", "DISABLED", null, null, null, null)] : []));
    }

    public Task<TelemetryEventSearchResponse?> SearchTelemetryEventsAsync(
        string sessionId,
        TelemetryEventSearchRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<TelemetryEventSearchResponse?>(
            IsKnownSession(sessionId)
                ? new TelemetryEventSearchResponse(
                    sessionId,
                    [new TelemetryEventCandidate(DateTimeOffset.UtcNow, "LEC", 1, 190_000, 10_000, 312, 100, 0, 10, "high_speed")])
                : null);
    }

    private static bool IsKnownSession(string sessionId) =>
        string.Equals(sessionId, Monza2025.SessionId, StringComparison.OrdinalIgnoreCase);
}
