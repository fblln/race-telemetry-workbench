using Microsoft.Extensions.Options;
using RaceTelemetry.Agent.Options;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace RaceTelemetry.AgentApi.Sessions;

public sealed class AgentSessionRegistry
{
    private static readonly Regex ThreadIdPattern = new(
        @"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new();
    private readonly TelemetryAgentOptions _options;

    public AgentSessionRegistry(IOptions<TelemetryAgentOptions> options)
    {
        _options = options.Value;
    }

    public int Count => _sessions.Count;

    public ValueTask<SessionEntry> GetOrCreateAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        ValidateThreadId(threadId);

        if (_sessions.TryGetValue(threadId, out var existing))
        {
            existing.Touch();
            return ValueTask.FromResult(existing);
        }

        // Capacity check with expired eviction
        if (_sessions.Count >= _options.MaximumSessions)
        {
            var evicted = RemoveExpired(DateTimeOffset.UtcNow - _options.SessionIdleTimeout);
            if (evicted == 0 && _sessions.Count >= _options.MaximumSessions)
                throw new InvalidOperationException(
                    $"Session capacity reached ({_options.MaximumSessions}). Try again later.");
        }

        var entry = new SessionEntry();
        // GetOrAdd is atomic — if two threads race, only one wins and we return the winner
        var final = _sessions.GetOrAdd(threadId, entry);
        return ValueTask.FromResult(final);
    }

    public bool Remove(string threadId) => _sessions.TryRemove(threadId, out _);

    public int RemoveExpired(DateTimeOffset threshold)
    {
        var count = 0;
        foreach (var (key, entry) in _sessions)
        {
            // Skip sessions that might be in use
            if (entry.LastAccessUtc < threshold && entry.TurnLock.CurrentCount > 0)
            {
                if (_sessions.TryRemove(key, out _))
                    count++;
            }
        }
        return count;
    }

    private static void ValidateThreadId(string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId) || threadId.Length > 64)
            throw new ArgumentException("Invalid threadId length.", nameof(threadId));

        if (!ThreadIdPattern.IsMatch(threadId))
            throw new ArgumentException("threadId must be a valid UUID.", nameof(threadId));
    }
}
