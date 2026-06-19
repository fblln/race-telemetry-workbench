namespace RaceTelemetry.Agent.Options;

public sealed class TelemetryAgentOptions
{
    public const string SectionName = "TelemetryAgent";

    public TimeSpan SessionIdleTimeout { get; init; } = TimeSpan.FromHours(1);

    public int MaximumSessions { get; init; } = 250;

    public int MaximumMessageCharacters { get; init; } = 20_000;

    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan SessionCleanupInterval { get; init; } = TimeSpan.FromMinutes(5);

    public string McpEndpoint { get; init; } = "http://localhost:5122/mcp";

    /// <summary>
    /// Maximum number of messages kept in the session history sent to the model.
    /// Older messages are dropped (keeping the most recent turns) to stay within
    /// model context / TPM limits. Each user+assistant exchange = 2 messages;
    /// tool call pairs add 2 more. Default 20 = ~10 turns.
    /// </summary>
    public int MaxContextMessages { get; init; } = 20;
}
