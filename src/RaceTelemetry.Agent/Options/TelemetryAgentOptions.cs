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

    // Reasoning models (gpt-5*) spend reasoning tokens out of this same budget before any
    // visible text. Too small and the completion is all-reasoning, zero output. Keep headroom.
    public int ToolPlanningMaxOutputTokens { get; init; } = 1500;

    public int FinalAnswerMaxOutputTokens { get; init; } = 2500;

    public int MaximumToolRounds { get; init; } = 6;

    public int MaximumToolCalls { get; init; } = 16;

    public int MaximumConcurrentToolCalls { get; init; } = 8;

    public TimeSpan ToolCallTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Process-local lifetime for successful read-only MCP tool results. Set to zero to disable
    /// cross-run caching; duplicate calls are still deduplicated within a single run.
    /// </summary>
    public TimeSpan ToolResultCacheTtl { get; init; } = TimeSpan.FromMinutes(2);

    public int MaximumCachedToolResults { get; init; } = 1_000;

    public int MaximumToolResultCharacters { get; init; } = 20_000;

    public int MaximumEvidenceCharacters { get; init; } = 60_000;
}
