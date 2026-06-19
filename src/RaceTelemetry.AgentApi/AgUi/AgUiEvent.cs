using System.Text.Json.Serialization;

namespace RaceTelemetry.AgentApi.AgUi;

public sealed class AgUiEvent
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("threadId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThreadId { get; init; }

    [JsonPropertyName("runId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    [JsonPropertyName("messageId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageId { get; init; }

    [JsonPropertyName("role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; init; }

    [JsonPropertyName("delta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Delta { get; init; }

    [JsonPropertyName("toolCallId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("toolCallName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallName { get; init; }

    [JsonPropertyName("parentMessageId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentMessageId { get; init; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }

    [JsonPropertyName("code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; init; }

    public static AgUiEvent RunStarted(string threadId, string runId) => new()
    {
        Type = "RUN_STARTED",
        ThreadId = threadId,
        RunId = runId,
    };

    public static AgUiEvent TextMessageStart(string messageId) => new()
    {
        Type = "TEXT_MESSAGE_START",
        MessageId = messageId,
        Role = "assistant",
    };

    public static AgUiEvent TextMessageContent(string messageId, string delta) => new()
    {
        Type = "TEXT_MESSAGE_CONTENT",
        MessageId = messageId,
        Delta = delta,
    };

    public static AgUiEvent TextMessageEnd(string messageId) => new()
    {
        Type = "TEXT_MESSAGE_END",
        MessageId = messageId,
    };

    public static AgUiEvent ToolCallStart(string toolCallId, string toolCallName, string? parentMessageId = null) => new()
    {
        Type = "TOOL_CALL_START",
        ToolCallId = toolCallId,
        ToolCallName = toolCallName,
        ParentMessageId = parentMessageId,
    };

    public static AgUiEvent ToolCallArgs(string toolCallId, string delta) => new()
    {
        Type = "TOOL_CALL_ARGS",
        ToolCallId = toolCallId,
        Delta = delta,
    };

    public static AgUiEvent ToolCallEnd(string toolCallId) => new()
    {
        Type = "TOOL_CALL_END",
        ToolCallId = toolCallId,
    };

    public static AgUiEvent RunFinished(string threadId, string runId) => new()
    {
        Type = "RUN_FINISHED",
        ThreadId = threadId,
        RunId = runId,
    };

    public static AgUiEvent RunError(string message, string code = "AGENT_ERROR") => new()
    {
        Type = "RUN_ERROR",
        Message = message,
        Code = code,
    };
}
