using Microsoft.Extensions.AI;

namespace RaceTelemetry.AgentApi.Sessions;

public sealed class SessionEntry
{
    public List<ChatMessage> Messages { get; } = [];

    public SemaphoreSlim TurnLock { get; } = new(1, 1);

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset LastAccessUtc { get; private set; } = DateTimeOffset.UtcNow;

    public long TurnCount { get; private set; }

    public void Touch() => LastAccessUtc = DateTimeOffset.UtcNow;

    public void CompleteTurn()
    {
        TurnCount++;
        Touch();
    }
}
