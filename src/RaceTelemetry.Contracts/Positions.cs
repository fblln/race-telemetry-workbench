namespace RaceTelemetry.Contracts;

/// <summary>
/// Lap-by-lap classified position per driver for the position-trace view
/// (§6.12, §8.15). One short array per driver; <c>positions[i]</c> aligns to lap
/// <see cref="FromLap"/> + i, with a missing classification rendered as null.
/// </summary>
public sealed record PositionsResponse(
    string SessionId,
    int FromLap,
    int ToLap,
    IReadOnlyList<DriverPositions> Items);

public sealed record DriverPositions(string DriverCode, IReadOnlyList<int?> Positions);
