using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using RaceTelemetry.Agent;
using RaceTelemetry.Agent.Options;

namespace RaceTelemetry.AgentApi.AgUi;

/// <summary>
/// Process-local cache for successful read-only MCP tool results. Entries are shared across
/// threads and agent runs, bounded by count, and expire quickly so newly imported data appears.
/// </summary>
public sealed class ToolResultCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl;
    private readonly int _maximumEntries;
    private long _sequence;

    public ToolResultCache(IOptions<TelemetryAgentOptions> options)
    {
        _ttl = options.Value.ToolResultCacheTtl;
        _maximumEntries = options.Value.MaximumCachedToolResults;
    }

    public bool TryGet(string key, out string result)
    {
        result = string.Empty;
        if (_ttl <= TimeSpan.Zero || _maximumEntries <= 0)
        {
            return false;
        }

        if (_entries.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAtUtc > DateTimeOffset.UtcNow)
            {
                AgentTelemetry.ToolCacheHits.Add(1);
                result = entry.Result;
                return true;
            }

            _entries.TryRemove(new KeyValuePair<string, CacheEntry>(key, entry));
        }

        AgentTelemetry.ToolCacheMisses.Add(1);
        return false;
    }

    public void Set(string key, string result)
    {
        if (_ttl <= TimeSpan.Zero || _maximumEntries <= 0)
        {
            return;
        }

        var entry = new CacheEntry(
            result,
            DateTimeOffset.UtcNow.Add(_ttl),
            Interlocked.Increment(ref _sequence));
        _entries[key] = entry;

        if (_entries.Count <= _maximumEntries)
        {
            return;
        }

        RemoveExpired();
        while (_entries.Count > _maximumEntries)
        {
            var oldest = _entries.MinBy(pair => pair.Value.Sequence);
            if (oldest.Key is null || !_entries.TryRemove(oldest))
            {
                break;
            }
        }
    }

    private void RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _entries)
        {
            if (pair.Value.ExpiresAtUtc <= now)
            {
                _entries.TryRemove(pair);
            }
        }
    }

    private sealed record CacheEntry(string Result, DateTimeOffset ExpiresAtUtc, long Sequence);
}
