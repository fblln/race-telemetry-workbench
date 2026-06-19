namespace RaceTelemetry.Contracts;

public sealed record LapQualityResponse(
    string SessionId,
    string DriverCode,
    int LapNumber,
    long? OfficialLapDurationMs,
    long? TelemetryCoveredDurationMs,
    long? FirstSampleOffsetMs,
    long? LastSampleOffsetMs,
    long? MaximumCarDataGapMs,
    long? MaximumPositionGapMs,
    double? FinalIntegratedDistanceM,
    double? InterpolatedCarDataPercentage,
    double? InterpolatedPositionPercentage,
    double? StaleSamplePercentage,
    long? DistanceDeltaValidationMs,
    string QualityStatus,
    IReadOnlyList<string> QualityMessages);
