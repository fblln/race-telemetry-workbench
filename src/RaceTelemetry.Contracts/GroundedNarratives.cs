namespace RaceTelemetry.Contracts;

public sealed record EvidenceReference(
    string Source,
    string? DriverCode = null,
    int? LapNumber = null,
    int? StintNumber = null,
    long? FromSessionTimeMs = null,
    long? ToSessionTimeMs = null);

public sealed record NarrativeFact(
    string Id,
    string Kind,
    string Text,
    double? Value,
    string? Unit,
    IReadOnlyList<EvidenceReference> Evidence,
    string QualityStatus = "supported",
    string NarrationPolicy = "assert");

public sealed record StoryQuality(
    string Status,
    int SupportedFacts,
    int DegradedFacts,
    int OmittedFacts,
    IReadOnlyList<string> Warnings);

public sealed record StrategySummaryRequest(
    IReadOnlyList<string>? Drivers,
    bool? CompareToFieldAverage);

public sealed record StrategySummaryResponse(
    string SessionId,
    IReadOnlyList<DriverStrategySummary> Items,
    IReadOnlyList<NarrativeFact> Facts,
    StoryQuality Quality);

public sealed record DriverStrategySummary(
    string DriverCode,
    IReadOnlyList<StrategyStopSummary> Stops,
    IReadOnlyList<string> NarrativeFactIds);

public sealed record StrategyStopSummary(
    int LapNumber,
    string? FromCompound,
    string? ToCompound,
    long? PitInSessionTimeMs,
    long? PitOutSessionTimeMs,
    long? PitLaneTimeMs,
    long? FieldAveragePitLaneTimeMs,
    string? TrackStatusAtStop,
    string StrategyLabel,
    string? RivalDriverCode,
    int? PositionBeforeStop,
    int? PositionAfterStops,
    int? PositionGain,
    string QualityStatus);

public sealed record RaceDebriefRequest(
    IReadOnlyList<string>? Drivers,
    IReadOnlyList<string>? Sections);

public sealed record RaceDebriefResponse(
    string SessionId,
    RaceDebriefOverview? Overview,
    StrategySummaryResponse? Strategy,
    IReadOnlyList<RaceControlItem> Incidents,
    RaceDebriefWeather? Weather,
    IReadOnlyList<NarrativeFact> Facts,
    StoryQuality Quality);

public sealed record RaceDebriefOverview(
    string? Winner,
    string Headline,
    int LapCount);

public sealed record RaceDebriefWeather(
    string Summary,
    double? AirTempMinC,
    double? AirTempMaxC,
    double? TrackTempMinC,
    double? TrackTempMaxC,
    bool RainfallObserved);
