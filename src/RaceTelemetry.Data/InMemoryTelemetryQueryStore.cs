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
                ProjectTelemetrySample(channels, DateTimeOffset.UtcNow, 190_000, 10_000, 312, 100, 0, 8, 11_750, 10),
                ProjectTelemetrySample(channels, DateTimeOffset.UtcNow.AddSeconds(10), 200_000, 20_000, 184, 5, 92, 4, 10_400, 0)
            ]));
    }

    public Task<LapQualityResponse?> GetLapQualityAsync(
        string sessionId,
        string driverCode,
        int lapNumber,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsKnownSession(sessionId) || !LapsByDriver.TryGetValue(driverCode, out var laps)
            || !laps.Any(lap => lap.LapNumber == lapNumber))
        {
            return Task.FromResult<LapQualityResponse?>(null);
        }

        return Task.FromResult<LapQualityResponse?>(new LapQualityResponse(
            sessionId,
            driverCode.ToUpperInvariant(),
            lapNumber,
            85_120,
            84_760,
            110,
            250,
            420,
            680,
            5_786.4,
            97.1,
            88.4,
            7.5,
            36,
            "valid_with_warnings",
            ["WARNING_PIT_OUT_LAP"]));
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

    public Task<LapComparisonByDistanceResponse?> CompareLapsByDistanceAsync(
        string sessionId,
        string driverA,
        int lapA,
        string driverB,
        int lapB,
        double? startDistanceM,
        double? endDistanceM,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsKnownSession(sessionId)
            || !LapsByDriver.TryGetValue(driverA, out var lapsA)
            || !LapsByDriver.TryGetValue(driverB, out var lapsB)
            || !lapsA.Any(lap => lap.LapNumber == lapA)
            || !lapsB.Any(lap => lap.LapNumber == lapB))
        {
            return Task.FromResult<LapComparisonByDistanceResponse?>(null);
        }

        return Task.FromResult<LapComparisonByDistanceResponse?>(new LapComparisonByDistanceResponse(
            sessionId,
            driverA.ToUpperInvariant(),
            lapA,
            driverB.ToUpperInvariant(),
            lapB,
            5,
            "positive means driverA is slower",
            [
                new LapComparisonByDistancePoint(
                    0,
                    0,
                    0,
                    0,
                    0,
                    new DistanceTelemetryChannelValues(282.4, 100, 0, 11_700, 8, 10),
                    new DistanceTelemetryChannelValues(279.1, 100, 0, 11_630, 8, 10),
                    new DistanceTelemetryChannelValues(3.3, 0, 0, 70, 0, 0)),
                new LapComparisonByDistancePoint(
                    500,
                    0.086,
                    6_820,
                    6_640,
                    180,
                    new DistanceTelemetryChannelValues(301.2, 99, 0, 11_910, 8, 12),
                    new DistanceTelemetryChannelValues(304.8, 100, 0, 11_980, 8, 12),
                    new DistanceTelemetryChannelValues(-3.6, -1, 0, -70, 0, 0))
            ],
            new LapComparisonByDistanceSummary(-430, -401, 29, "valid_with_warnings", "valid")));
    }

    public Task<LapStoryResponse?> GetLapStoryAsync(
        string sessionId,
        string driverCode,
        int lapNumber,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsKnownSession(sessionId) || !LapsByDriver.TryGetValue(driverCode, out var laps)
            || !laps.Any(lap => lap.LapNumber == lapNumber))
        {
            return Task.FromResult<LapStoryResponse?>(null);
        }

        return Task.FromResult<LapStoryResponse?>(new LapStoryResponse(
            sessionId,
            driverCode.ToUpperInvariant(),
            lapNumber,
            85_120,
            [28_100, 28_400, 28_620],
            "MEDIUM",
            12,
            342,
            247.8,
            74.2,
            12.5,
            1_420,
            [
                new AnalysisInsight("lap_time", "Lap time was 1:25.120.", 85_120, "ms"),
                new AnalysisInsight("peak_speed", "Peak sampled speed was 342 km/h.", 342, "km/h"),
                new AnalysisInsight("tyre", "Tyre context: MEDIUM, tyre life 12.", 12, "laps")
            ]));
    }

    public Task<LapBrakingZonesResponse?> GetLapBrakingZonesAsync(
        string sessionId,
        string driverCode,
        int lapNumber,
        int brakeThresholdPct,
        int minimumDurationMs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsKnownSession(sessionId) || !LapsByDriver.TryGetValue(driverCode, out var laps)
            || !laps.Any(lap => lap.LapNumber == lapNumber))
        {
            return Task.FromResult<LapBrakingZonesResponse?>(null);
        }

        return Task.FromResult<LapBrakingZonesResponse?>(new LapBrakingZonesResponse(
            sessionId,
            driverCode.ToUpperInvariant(),
            lapNumber,
            brakeThresholdPct,
            minimumDurationMs,
            [
                new LapBrakingZone(1, 8_600, 13_100, 4_500, 342, 87, 132, 100, "Turn 1/2, Variante del Rettifilo", 28.4),
                new LapBrakingZone(2, 55_600, 57_000, 1_400, 319, 171, 206, 94, "Turn 8/9/10, Variante Ascari", 41.2)
            ]));
    }

    public Task<LapComparisonStoryResponse?> CompareLapsStoryAsync(
        string sessionId,
        string driverA,
        int lapA,
        string driverB,
        int lapB,
        int segmentCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsKnownSession(sessionId)
            || !LapsByDriver.TryGetValue(driverA, out var lapsA)
            || !LapsByDriver.TryGetValue(driverB, out var lapsB)
            || !lapsA.Any(lap => lap.LapNumber == lapA)
            || !lapsB.Any(lap => lap.LapNumber == lapB))
        {
            return Task.FromResult<LapComparisonStoryResponse?>(null);
        }

        return Task.FromResult<LapComparisonStoryResponse?>(new LapComparisonStoryResponse(
            sessionId,
            driverA.ToUpperInvariant(),
            lapA,
            driverB.ToUpperInvariant(),
            lapB,
            -430,
            [-200, -100, -130],
            17,
            8.5,
            [
                new LapComparisonSegment(1, 0, 28_000, 2.1, 1.3, -3.2, "driver_a"),
                new LapComparisonSegment(2, 28_000, 56_000, 9.4, 4.6, 8.1, "driver_a"),
                new LapComparisonSegment(3, 56_000, 85_120, -0.8, -1.0, 2.4, "even")
            ],
            [
                new AnalysisInsight("lap_delta", $"{driverA.ToUpperInvariant()} was 430 ms quicker overall.", -430, "ms"),
                new AnalysisInsight("biggest_sector_delta", $"Largest sector delta was S1: {driverA.ToUpperInvariant()} by 200 ms.", -200, "ms")
            ]));
    }

    public Task<RaceStoryResponse?> GetRaceStoryAsync(
        string sessionId,
        int raceControlLimit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsKnownSession(sessionId))
        {
            return Task.FromResult<RaceStoryResponse?>(null);
        }

        return Task.FromResult<RaceStoryResponse?>(new RaceStoryResponse(
            sessionId,
            Monza2025,
            new WeatherSummary(32.2, 34.1, 43.5, 54.6, false),
            [
                new RaceStintSummary("LEC", 1, "MEDIUM", 1, 24, 24, 1, 24, 86_400, 84_900, 92_450),
                new RaceStintSummary("LEC", 2, "HARD", 25, 53, 29, 1, 29, 85_900, 84_700, 96_200)
            ],
            [
                new PitStopSummary("LEC", 24, "pit_in", 1, "MEDIUM", 24, 96_200, 2_060_000),
                new PitStopSummary("LEC", 25, "pit_out", 2, "HARD", 1, 94_300, 2_150_000)
            ],
            [new TrackStatusPeriodSummary(0, null, "1", "track_clear", "Track clear")],
            [new RaceControlSummary(100_000, 2, "Drs", "DRS ENABLED", "ENABLED", null, null, null, null)],
            [
                new AnalysisInsight("session_scope", "Italian Grand Prix 2025 R: 20 drivers and 1060 imported laps."),
                new AnalysisInsight("pit_stops", "Detected 2 pit-in/out lap markers.", 2, "events")
            ]));
    }

    public Task<TelemetryAggregateResponse?> AggregateTelemetryAsync(
        string sessionId,
        TelemetryAggregateRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsKnownSession(sessionId))
        {
            return Task.FromResult<TelemetryAggregateResponse?>(null);
        }

        var groupBy = request.GroupBy is { Count: > 0 } ? request.GroupBy : ["driver", "stint", "compound"];
        var metrics = request.Metrics is { Count: > 0 }
            ? request.Metrics
            : ["sample_count", "avg_speed_kmh", "drs_active_time_ms", "brake_time_ms"];

        return Task.FromResult<TelemetryAggregateResponse?>(new TelemetryAggregateResponse(
            sessionId,
            groupBy,
            metrics,
            [
                new TelemetryAggregateItem(
                    "LEC",
                    null,
                    1,
                    "MEDIUM",
                    "track_clear",
                    null,
                    null,
                    14_520,
                    238.4,
                    342.0,
                    73.1,
                    12.8,
                    41_200,
                    184_200,
                    18,
                    604_000)
            ]));
    }

    public Task<TelemetryWindowResponse?> DetectTelemetryWindowsAsync(
        string sessionId,
        TelemetryWindowRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsKnownSession(sessionId))
        {
            return Task.FromResult<TelemetryWindowResponse?>(null);
        }

        var minimumDurationMs = request.MinimumDurationMs ?? 250;
        return Task.FromResult<TelemetryWindowResponse?>(new TelemetryWindowResponse(
            sessionId,
            request.EventType,
            minimumDurationMs,
            [
                new TelemetryWindowItem(
                    "LEC",
                    1,
                    190_000,
                    194_500,
                    8_600,
                    13_100,
                    4_500,
                    request.IncludeNearestCorner == false ? null : "Turn 1/2, Variante del Rettifilo",
                    request.IncludeNearestCorner == false ? null : 28.4,
                    new TelemetryWindowSummary(342, 87, 342, 132, 100, 12.1))
            ]));
    }

    public Task<StintAnalysisResponse?> AnalyzeDriverStintsAsync(
        string sessionId,
        StintAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsKnownSession(sessionId))
        {
            return Task.FromResult<StintAnalysisResponse?>(null);
        }

        var metrics = request.Metrics is { Count: > 0 }
            ? request.Metrics
            : ["lap_time_slope_ms_per_lap", "best_lap_time_ms", "average_lap_time_ms"];

        return Task.FromResult<StintAnalysisResponse?>(new StintAnalysisResponse(
            sessionId,
            metrics,
            [
                new DriverStintAnalysisItem(
                    "LEC",
                    1,
                    "MEDIUM",
                    1,
                    24,
                    24,
                    1,
                    24,
                    86_400,
                    84_900,
                    92_450,
                    82.4,
                    [new AnalysisInsight("degradation", "Lap time trend increased by 82.4 ms per lap.", 82.4, "ms/lap")])
            ]));
    }

    public Task<PitStopAnalysisResponse?> AnalyzePitStopsAsync(
        string sessionId,
        PitStopAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsKnownSession(sessionId))
        {
            return Task.FromResult<PitStopAnalysisResponse?>(null);
        }

        return Task.FromResult<PitStopAnalysisResponse?>(new PitStopAnalysisResponse(
            sessionId,
            [
                new PitStopAnalysisItem(
                    "LEC",
                    24,
                    "pit_in",
                    1,
                    "MEDIUM",
                    24,
                    96_200,
                    2_060_000,
                    84_800,
                    11_400,
                    [new AnalysisInsight("pit_loss", "Pit lap was 11400 ms slower than nearby non-pit laps.", 11_400, "ms")])
            ]));
    }

    public Task<StrategySummaryResponse?> SummarizeStrategyAsync(
        string sessionId,
        StrategySummaryRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsKnownSession(sessionId))
        {
            return Task.FromResult<StrategySummaryResponse?>(null);
        }

        var fact = new NarrativeFact(
            "strategy-lec-24",
            "strategy_stop",
            "LEC stopped on lap 24 for HARD in 23.800s; the undercut on VER changed track position.",
            23_800,
            "ms",
            [
                new EvidenceReference("strategy/summarize", "LEC", 24, 1, 2_060_000, 2_083_800),
                new EvidenceReference("positions", "LEC", 23),
                new EvidenceReference("positions", "LEC", 27)
            ]);
        return Task.FromResult<StrategySummaryResponse?>(new StrategySummaryResponse(
            sessionId,
            [
                new DriverStrategySummary(
                    "LEC",
                    [new StrategyStopSummary(24, "MEDIUM", "HARD", 2_060_000, 2_083_800, 23_800, 24_100,
                        "track_clear", "undercut", "VER", 2, 1, 1, "supported")],
                    [fact.Id])
            ],
            [fact],
            new StoryQuality("supported", 1, 0, 0, [])));
    }

    public async Task<RaceDebriefResponse?> GenerateRaceDebriefAsync(
        string sessionId,
        RaceDebriefRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsKnownSession(sessionId))
        {
            return null;
        }

        var sections = new HashSet<string>(
            request.Sections is { Count: > 0 } ? request.Sections : ["overview", "strategy", "incidents", "weather"],
            StringComparer.OrdinalIgnoreCase);
        var strategy = sections.Contains("strategy")
            ? await SummarizeStrategyAsync(sessionId, new StrategySummaryRequest(request.Drivers, true), cancellationToken)
            : null;
        var facts = new List<NarrativeFact>
        {
            new("debrief-winner", "winner", "VER was classified first after 53 laps.", 1, "position",
                [new EvidenceReference("standings", "VER", 53)])
        };
        if (strategy is not null)
        {
            facts.AddRange(strategy.Facts);
        }
        if (sections.Contains("weather"))
        {
            facts.Add(new NarrativeFact("debrief-weather", "weather", "No rainfall was observed during the session.",
                0, "boolean", [new EvidenceReference("weather/trend")]));
        }

        return new RaceDebriefResponse(
            sessionId,
            sections.Contains("overview") ? new RaceDebriefOverview("VER", "VER won the Italian Grand Prix.", 53) : null,
            strategy,
            sections.Contains("incidents")
                ? [new Incident("safety_car", 20, 1_820_000, "Safety car deployed", null, null, null, null, "high", null)]
                : [],
            sections.Contains("weather") ? new RaceDebriefWeather("No rainfall was observed during the session.", 32.2, 34.1, 43.5, 54.6, false) : null,
            facts,
            new StoryQuality("supported", facts.Count, 0, 0, []));
    }

    public Task<WeatherTrendResponse?> GetWeatherTrendAsync(
        string sessionId,
        WeatherTrendRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsKnownSession(sessionId))
        {
            return Task.FromResult<WeatherTrendResponse?>(null);
        }

        return Task.FromResult<WeatherTrendResponse?>(new WeatherTrendResponse(
            sessionId,
            request.FromMs,
            request.FromMs + request.DurationMs,
            2,
            new WeatherTrendMetric(32.2, 33.1, 32.2, 33.1, 32.65, 0.9),
            new WeatherTrendMetric(43.5, 45.0, 43.5, 45.0, 44.25, 1.5),
            new WeatherTrendMetric(54.6, 52.0, 52.0, 54.6, 53.3, -2.6),
            new WeatherTrendMetric(993.9, 993.8, 993.8, 993.9, 993.85, -0.1),
            new WeatherTrendMetric(1.0, 1.4, 1.0, 1.4, 1.2, 0.4),
            false,
            [new AnalysisInsight("weather_trend", "Track temperature increased by 1.5 C over the selected window.", 1.5, "C")]));
    }

    public Task<RaceControlTimelineResponse?> GetRaceControlTimelineAsync(
        string sessionId,
        RaceControlTimelineRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsKnownSession(sessionId))
        {
            return Task.FromResult<RaceControlTimelineResponse?>(null);
        }

        var item = new RaceControlSummary(100_000, 2, "Drs", "DRS ENABLED", "ENABLED", null, null, null, null);
        return Task.FromResult<RaceControlTimelineResponse?>(new RaceControlTimelineResponse(
            sessionId,
            [item],
            [new RaceControlBucket("Drs", 1)],
            [],
            [new RaceControlBucket("ENABLED", 1)],
            [new AnalysisInsight("race_control", "Matched 1 race-control message.", 1, "messages")]));
    }

    public Task<CircuitContextResponse?> GetCircuitContextAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsKnownSession(sessionId))
        {
            return Task.FromResult<CircuitContextResponse?>(null);
        }

        var corner = new CircuitMarker("corner", 1, null, -569.58, 8153.72, 153.79, null);
        return Task.FromResult<CircuitContextResponse?>(new CircuitContextResponse(
            sessionId,
            0,
            "fastf1",
            [corner],
            [],
            [],
            [new AnalysisInsight("corners", "Loaded 1 circuit corner marker.", 1, "markers")]));
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
                [ProjectReplaySample(channels, fromMs + 12, 1, 298.2, 100, 0, 8, 11_680, 0, 1234.5, -341.2, 0)]))
            .ToArray();

        return Task.FromResult<ReplayChunkResponse?>(new ReplayChunkResponse(
            sessionId,
            fromMs,
            durationMs,
            fromMs + durationMs,
            channels,
            items,
            FrequencyHz: 10,
            TelemetrySource: "in_memory"));
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

    public Task<StandingsResponse?> GetStandingsAsync(
        string sessionId,
        int? atLap,
        string sortBy,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsKnownSession(sessionId))
        {
            return Task.FromResult<StandingsResponse?>(null);
        }

        return Task.FromResult<StandingsResponse?>(new StandingsResponse(
            sessionId,
            atLap ?? 53,
            [
                new StandingRow(1, "VER", "Max Verstappen", "Red Bull Racing", 0, 0, 84_870, 84_870, true, true, "HARD", 24, 1, "running", [85_100, 84_980, 84_920, 84_900, 84_870]),
                new StandingRow(2, "LEC", "Charles Leclerc", "Ferrari", 2_340, 2_340, 85_120, 84_900, false, false, "HARD", 24, 1, "running", [85_400, 85_220, 85_180, 85_120, 84_900])
            ]));
    }

    public Task<IncidentsResponse?> GetIncidentsAsync(
        string sessionId,
        IReadOnlyList<string>? types,
        double minBrakingG,
        int maxResults,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsKnownSession(sessionId))
        {
            return Task.FromResult<IncidentsResponse?>(null);
        }

        return Task.FromResult<IncidentsResponse?>(new IncidentsResponse(
            sessionId,
            [
                new Incident("safety_car", null, 1_820_000, "Safety car deployed", null, null, null, null, "high", null),
                new Incident("hard_braking", 5, 320_000, "LEC hard braking into Turn 1/2, Variante del Rettifilo", new NearestCorner(1, "Turn 1/2, Variante del Rettifilo"), -569.58, 8153.72, "LEC", "info", new IncidentMetrics(5.1, 342, 132))
            ],
            new IncidentSummary(2, 5.1, 1)));
    }

    public Task<PositionsResponse?> GetPositionsAsync(
        string sessionId,
        IReadOnlyList<string>? drivers,
        int? fromLap,
        int? toLap,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsKnownSession(sessionId))
        {
            return Task.FromResult<PositionsResponse?>(null);
        }

        var from = fromLap ?? 1;
        var to = toLap ?? 3;
        var length = Math.Max(1, to - from + 1);
        return Task.FromResult<PositionsResponse?>(new PositionsResponse(
            sessionId,
            from,
            to,
            [
                new DriverPositions("VER", Enumerable.Repeat<int?>(1, length).ToArray()),
                new DriverPositions("LEC", Enumerable.Repeat<int?>(2, length).ToArray())
            ]));
    }

    private static bool IsKnownSession(string sessionId) =>
        string.Equals(sessionId, Monza2025.SessionId, StringComparison.OrdinalIgnoreCase);

    private static TelemetrySample ProjectTelemetrySample(
        IReadOnlyList<string> channels,
        DateTimeOffset sampleTimeUtc,
        long? sessionTimeMs,
        long? lapTimeMs,
        double? speedKmh,
        double? throttlePct,
        double? brakePct,
        int? gear,
        double? rpm,
        int? drs) =>
        new(
            sampleTimeUtc,
            sessionTimeMs,
            lapTimeMs,
            ChannelSelected(channels, "speed_kmh") ? speedKmh : null,
            ChannelSelected(channels, "throttle_pct") ? throttlePct : null,
            ChannelSelected(channels, "brake_pct") ? brakePct : null,
            ChannelSelected(channels, "gear") ? gear : null,
            ChannelSelected(channels, "rpm") ? rpm : null,
            ChannelSelected(channels, "drs") ? drs : null);

    private static ReplaySample ProjectReplaySample(
        IReadOnlyList<string> channels,
        long? offsetMs,
        int? lapNumber,
        double? speedKmh,
        double? throttlePct,
        double? brakePct,
        int? gear,
        double? rpm,
        int? drs,
        double? x,
        double? y,
        double? z) =>
        new(
            offsetMs,
            lapNumber,
            ChannelSelected(channels, "speed_kmh") ? speedKmh : null,
            ChannelSelected(channels, "throttle_pct") ? throttlePct : null,
            ChannelSelected(channels, "brake_pct") ? brakePct : null,
            ChannelSelected(channels, "gear") ? gear : null,
            ChannelSelected(channels, "rpm") ? rpm : null,
            ChannelSelected(channels, "drs") ? drs : null,
            ChannelSelected(channels, "x") ? x : null,
            ChannelSelected(channels, "y") ? y : null,
            ChannelSelected(channels, "z") ? z : null,
            QualityFlags: ["OK"]);

    private static bool ChannelSelected(IReadOnlyList<string> channels, string channel) =>
        channels.Contains(channel, StringComparer.OrdinalIgnoreCase);
}
