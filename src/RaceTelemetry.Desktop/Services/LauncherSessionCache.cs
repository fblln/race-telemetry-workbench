using System.Collections.Concurrent;
using RaceTelemetry.Contracts;

namespace RaceTelemetry.Desktop.Services;

public interface ILauncherSessionCache
{
    LauncherSessionData Get(string sessionId);
}

public sealed class LauncherSessionCache : ILauncherSessionCache
{
    private readonly IQueryApiClient _api;
    private readonly ConcurrentDictionary<string, LauncherSessionData> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public LauncherSessionCache(IQueryApiClient api) => _api = api;

    public LauncherSessionData Get(string sessionId)
        => _sessions.GetOrAdd(sessionId, id => new LauncherSessionData(
            () => _api.GetDriversAsync(id),
            () => _api.GetStandingsAsync(id)));
}

public sealed class LauncherSessionData
{
    private readonly Func<Task<IReadOnlyList<DriverSummary>>> _loadDrivers;
    private readonly Func<Task<StandingsResponse?>> _loadStandings;
    private readonly object _gate = new();
    private Task<IReadOnlyList<DriverSummary>>? _drivers;
    private Task<StandingsResponse?>? _standings;

    public LauncherSessionData(
        Func<Task<IReadOnlyList<DriverSummary>>> loadDrivers,
        Func<Task<StandingsResponse?>> loadStandings)
    {
        _loadDrivers = loadDrivers;
        _loadStandings = loadStandings;
    }

    public Task<IReadOnlyList<DriverSummary>> Drivers => GetOrCreate(ref _drivers, _loadDrivers);

    public Task<StandingsResponse?> Standings => GetOrCreate(ref _standings, _loadStandings);

    public Task<StandingsResponse?>? TryGetStandingsTask()
    {
        lock (_gate)
        {
            return _standings;
        }
    }

    private Task<T> GetOrCreate<T>(ref Task<T>? task, Func<Task<T>> factory)
    {
        lock (_gate)
        {
            task ??= factory();
            return task;
        }
    }
}
