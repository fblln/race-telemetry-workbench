using RaceTelemetry.Contracts;
using System.Text.Json.Serialization;

namespace RaceTelemetry.AgentApi.AgUi;

public sealed class AgUiRequest
{
    [JsonPropertyName("threadId")]
    public required string ThreadId { get; init; }

    [JsonPropertyName("runId")]
    public string? RunId { get; init; }

    [JsonPropertyName("messages")]
    public IReadOnlyList<AgUiMessage>? Messages { get; init; }

    [JsonPropertyName("state")]
    public TelemetryWorkspaceContext? State { get; init; }
}

public sealed class AgUiMessage
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; init; }
}
