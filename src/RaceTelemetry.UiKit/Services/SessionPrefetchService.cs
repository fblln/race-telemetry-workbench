using System.Collections.Concurrent;
using RaceTelemetry.Contracts;

namespace RaceTelemetry.Desktop.Services;

/// <summary>
/// Everything a session needs, fetched once and held in memory so view switches
/// are instant (§8.9). A snapshot is built by <see cref="SessionPrefetchService"/>.
/// </summary>
public sealed class SessionSnapshot
{
    public required string SessionId { get; init; }
    public IReadOnlyList<DriverSummary> Drivers { get; init; } = Array.Empty<DriverSummary>();
    public ReplayMetadata? ReplayMetadata { get; init; }
    public StandingsResponse? Standings { get; init; }
    public RaceControlResponse? Incidents { get; init; }
    public PositionsResponse? Positions { get; init; }
    public StintAnalysisResponse? Stints { get; init; }

    /// <summary>Data-derived track outline (x,y) sampled from one lap of position data.</summary>
    public IReadOnlyList<TrackPoint> TrackOutline { get; init; } = Array.Empty<TrackPoint>();
    public IReadOnlyDictionary<string, IReadOnlyList<LapSummary>> LapsByDriver { get; init; }
        = new Dictionary<string, IReadOnlyList<LapSummary>>();
    public DateTimeOffset LoadedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>True when the core panels (standings + incidents + metadata) are present.</summary>
    public bool IsComplete => ReplayMetadata is not null && Standings is not null && Incidents is not null;
}

/// <summary>A normalized track-outline point in source position-sample coordinates.</summary>
public readonly record struct TrackPoint(double X, double Y);

public interface ISessionPrefetchService
{
    /// <summary>Cached list of imported sessions for the launcher (fetched once).</summary>
    Task<IReadOnlyList<SessionSummary>> GetSessionsAsync(bool refresh = false, CancellationToken ct = default);

    /// <summary>Fetch (or return the in-flight/complete) snapshot for a session.</summary>
    Task<SessionSnapshot> GetAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Start warming a session in the background without awaiting (e.g. on row hover/selection).</summary>
    void Prime(string sessionId);

    /// <summary>Drop a cached session so the next access refetches.</summary>
    void Invalidate(string sessionId);
}

/// <summary>
/// In-memory prefetch cache. Each session is fetched exactly once; concurrent
/// callers share the same <see cref="Task{SessionSnapshot}"/>, so priming on
/// selection and awaiting on open never double-fetch. All session-scoped calls
/// (drivers, replay metadata, standings, incidents, positions, and per-driver
/// track outline) run in parallel. Per-driver laps are intentionally lazy; they
/// are expensive fan-out calls and most views do not need them.
/// </summary>
public sealed class SessionPrefetchService : ISessionPrefetchService
{
    private readonly IQueryApiClient _api;
    private readonly ConcurrentDictionary<string, Task<SessionSnapshot>> _sessions = new();
    private Task<IReadOnlyList<SessionSummary>>? _sessionList;
    private readonly object _listGate = new();

    public SessionPrefetchService(IQueryApiClient api) => _api = api;

    public Task<IReadOnlyList<SessionSummary>> GetSessionsAsync(bool refresh = false, CancellationToken ct = default)
    {
        lock (_listGate)
        {
            // Refetch when asked, when never fetched, or when the previous attempt
            // failed — otherwise a backend that was down at startup would stay
            // "empty" forever because the faulted task was cached.
            if (refresh || _sessionList is null || _sessionList.IsFaulted || _sessionList.IsCanceled)
                _sessionList = _api.GetSessionsAsync(ct: CancellationToken.None);
        }
        return _sessionList;
    }

    public Task<SessionSnapshot> GetAsync(string sessionId, CancellationToken ct = default)
    {
        // Drop a previously-failed warm so it refetches (defensive — FetchAsync
        // already swallows per-call failures and always returns a snapshot).
        if (_sessions.TryGetValue(sessionId, out var existing) && (existing.IsFaulted || existing.IsCanceled))
            _sessions.TryRemove(sessionId, out _);

        // Prefetch uses CancellationToken.None on purpose: a view switch must not
        // cancel a warm that another view is about to await.
        return _sessions.GetOrAdd(sessionId, id => FetchAsync(id));
    }

    public void Prime(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        // Fire-and-forget; swallow failures so priming never surfaces an error.
        _ = GetAsync(sessionId).ContinueWith(
            t => { _ = t.Exception; },
            TaskContinuationOptions.OnlyOnFaulted);
    }

    public void Invalidate(string sessionId) => _sessions.TryRemove(sessionId, out _);

    private async Task<SessionSnapshot> FetchAsync(string sessionId)
    {
        // Kick off the independent session-scoped calls together.
        var driversTask = Safe(() => _api.GetDriversAsync(sessionId), (IReadOnlyList<DriverSummary>)Array.Empty<DriverSummary>());
        var metaTask = Safe(() => _api.GetReplayMetadataAsync(sessionId), (ReplayMetadata?)null);
        var standingsTask = Safe(() => _api.GetStandingsAsync(sessionId), (StandingsResponse?)null);
        var incidentsTask = Safe(() => _api.GetRaceControlAsync(sessionId), (RaceControlResponse?)null);
        var positionsTask = Safe(() => _api.GetPositionsAsync(sessionId), (PositionsResponse?)null);
        var stintsTask = Safe(() => _api.GetStintsAsync(sessionId), (StintAnalysisResponse?)null);
        var outlineTask = Safe(() => GetTrackOutlineAsync(sessionId, metaTask), Array.Empty<TrackPoint>() as IReadOnlyList<TrackPoint>);

        await Task.WhenAll(driversTask, metaTask, standingsTask, incidentsTask, positionsTask, stintsTask, outlineTask)
            .ConfigureAwait(false);

        return new SessionSnapshot
        {
            SessionId = sessionId,
            Drivers = driversTask.Result,
            ReplayMetadata = metaTask.Result,
            Standings = standingsTask.Result,
            Incidents = incidentsTask.Result,
            Positions = positionsTask.Result,
            Stints = stintsTask.Result,
            TrackOutline = outlineTask.Result,
            LapsByDriver = new Dictionary<string, IReadOnlyList<LapSummary>>(),
        };
    }

    /// <summary>
    /// Derive a track outline from one lap of position data: fetch a single
    /// driver's x/y for a ~2-minute window (one racing lap) from the replay
    /// chunk. Downsampled server-side so the outline stays a few hundred points.
    /// </summary>
    private async Task<IReadOnlyList<TrackPoint>> GetTrackOutlineAsync(string sessionId, Task<ReplayMetadata?> metaTask)
    {
        var meta = await metaTask.ConfigureAwait(false);
        if (meta is null || meta.Drivers.Count == 0)
            return Array.Empty<TrackPoint>();

        var driver = meta.Drivers[0];
        var fromMs = Math.Max(0, meta.ReplayStartMs);
        var chunk = await _api.GetReplayChunkAsync(
            sessionId, fromMs, durationMs: 120_000, drivers: [driver], channels: ["x", "y"], sampleEvery: 4)
            .ConfigureAwait(false);

        var samples = chunk?.Items.FirstOrDefault()?.Samples;
        if (samples is null)
            return Array.Empty<TrackPoint>();

        var points = new List<TrackPoint>(samples.Count);
        foreach (var s in samples)
        {
            if (s.X is not null && s.Y is not null)
                points.Add(new TrackPoint(s.X.Value, s.Y.Value));
        }

        return points;
    }

    private static async Task<T> Safe<T>(Func<Task<T>> call, T fallback)
    {
        try { return await call().ConfigureAwait(false); }
        catch { return fallback; }
    }
}
