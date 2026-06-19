using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RaceTelemetry.Agent;
using RaceTelemetry.Agent.Options;
using RaceTelemetry.AgentApi.Sessions;
using RaceTelemetry.Contracts;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace RaceTelemetry.AgentApi.AgUi;

public sealed class AgentRunner
{
    private readonly IChatClient _chatClient;
    private readonly McpToolRegistry _mcpTools;
    private readonly TelemetryAgentOptions _options;
    private readonly ILogger<AgentRunner> _logger;

    public AgentRunner(
        IChatClient chatClient,
        McpToolRegistry mcpTools,
        IOptions<TelemetryAgentOptions> options,
        ILogger<AgentRunner> logger)
    {
        _chatClient = chatClient;
        _mcpTools = mcpTools;
        _options = options.Value;
        _logger = logger;
    }

    public async IAsyncEnumerable<AgUiEvent> RunAsync(
        string threadId,
        string runId,
        AgUiRequest request,
        SessionEntry session,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<AgUiEvent>(
            new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });

        var producerTask = Task.Run(async () =>
        {
            try
            {
                await ProduceEventsAsync(threadId, runId, request, session, channel.Writer, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in agent producer for thread {ThreadId}", threadId);
                channel.Writer.TryWrite(AgUiEvent.RunError("An unexpected error occurred.", "INTERNAL_ERROR"));
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, cancellationToken);

        await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken))
            yield return evt;

        await producerTask;
    }

    private async Task ProduceEventsAsync(
        string threadId,
        string runId,
        AgUiRequest request,
        SessionEntry session,
        ChannelWriter<AgUiEvent> writer,
        CancellationToken cancellationToken)
    {
        var runSw = Stopwatch.StartNew();
        AgentTelemetry.RunsStarted.Add(1);

        using var runSpan = AgentTelemetry.Activities.StartActivity("agent.run", ActivityKind.Server);
        runSpan?.SetTag("agent.thread_id_prefix", Truncate(threadId, 8));
        runSpan?.SetTag("agent.run_id", runId);
        runSpan?.SetTag("agent.turn", session.TurnCount + 1);

        writer.TryWrite(AgUiEvent.RunStarted(threadId, runId));

        var newUserMessage = ExtractNewUserMessage(request, session);
        if (string.IsNullOrWhiteSpace(newUserMessage))
        {
            writer.TryWrite(AgUiEvent.RunError("No user message found in request.", "INVALID_REQUEST"));
            return;
        }

        if (newUserMessage.Length > _options.MaximumMessageCharacters)
        {
            writer.TryWrite(AgUiEvent.RunError(
                $"Message exceeds maximum length of {_options.MaximumMessageCharacters} characters.",
                "MESSAGE_TOO_LONG"));
            return;
        }

        _logger.LogInformation(
            "AgentRun start  thread={ThreadPrefix} run={RunId} turn={Turn} question={Question}",
            Truncate(threadId, 8), runId, session.TurnCount + 1,
            Truncate(newUserMessage, 200));

        var userContent = BuildUserContent(newUserMessage, request.State);
        session.Messages.Add(new ChatMessage(ChatRole.User, userContent));

        var tools = _mcpTools.GetTools().ToList();
        var chatOptions = new ChatOptions
        {
            Tools = tools,
            ToolMode = ChatToolMode.Auto,
        };

        var history = session.Messages.Count > _options.MaxContextMessages
            ? session.Messages.Skip(session.Messages.Count - _options.MaxContextMessages).ToList()
            : session.Messages;

        var allMessages = new List<ChatMessage>
        {
            new(ChatRole.System, AgentInstructions.System),
        };
        allMessages.AddRange(history);

        runSpan?.SetTag("agent.context_messages", allMessages.Count);
        runSpan?.SetTag("agent.tools_available", tools.Count);

        try
        {
            var llmCallIndex = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                llmCallIndex++;
                var currentMessageId = Guid.NewGuid().ToString();
                bool messageStarted = false;
                var pendingToolCalls = new List<FunctionCallContent>();
                var updates = new List<ChatResponseUpdate>();

                using var llmSpan = AgentTelemetry.Activities.StartActivity("agent.llm.call", ActivityKind.Client);
                llmSpan?.SetTag("agent.llm.call_index", llmCallIndex);
                llmSpan?.SetTag("agent.llm.context_messages", allMessages.Count);

                AgentTelemetry.LlmCalls.Add(1);
                var llmSw = Stopwatch.StartNew();
                double? ttftMs = null;

                _logger.LogInformation(
                    "LLM call #{Index} start  messages={Messages} tools={Tools}",
                    llmCallIndex, allMessages.Count, tools.Count);

                await foreach (var update in _chatClient.GetStreamingResponseAsync(allMessages, chatOptions, cancellationToken))
                {
                    // Capture time-to-first-token on the very first update
                    if (ttftMs is null)
                    {
                        ttftMs = llmSw.Elapsed.TotalMilliseconds;
                        llmSpan?.SetTag("agent.llm.ttft_ms", (long)ttftMs.Value);
                        AgentTelemetry.LlmTtft.Record(ttftMs.Value);
                        _logger.LogInformation("LLM call #{Index} TTFT={Ttft:0}ms", llmCallIndex, ttftMs.Value);
                    }

                    updates.Add(update);

                    foreach (var content in update.Contents)
                    {
                        if (content is TextContent text && !string.IsNullOrEmpty(text.Text))
                        {
                            if (!messageStarted)
                            {
                                writer.TryWrite(AgUiEvent.TextMessageStart(currentMessageId));
                                messageStarted = true;
                            }
                            writer.TryWrite(AgUiEvent.TextMessageContent(currentMessageId, text.Text));
                        }
                        else if (content is FunctionCallContent call)
                        {
                            pendingToolCalls.Add(call);
                        }
                    }
                }

                var llmDuration = llmSw.Elapsed.TotalMilliseconds;
                AgentTelemetry.LlmStreamDuration.Record(llmDuration);
                llmSpan?.SetTag("agent.llm.stream_ms", (long)llmDuration);
                llmSpan?.SetTag("agent.llm.tool_calls", pendingToolCalls.Count);

                _logger.LogInformation(
                    "LLM call #{Index} done  ttft={Ttft:0}ms stream={Stream:0}ms toolCalls={Tools}",
                    llmCallIndex, ttftMs ?? 0, llmDuration, pendingToolCalls.Count);

                if (messageStarted)
                    writer.TryWrite(AgUiEvent.TextMessageEnd(currentMessageId));

                // Assemble assistant message
                var assistantContents = new List<AIContent>();
                var textParts = updates
                    .SelectMany(u => u.Contents)
                    .OfType<TextContent>()
                    .Where(t => !string.IsNullOrEmpty(t.Text))
                    .Select(t => t.Text!)
                    .ToList();

                if (textParts.Count > 0)
                    assistantContents.Add(new TextContent(string.Concat(textParts)));
                assistantContents.AddRange(pendingToolCalls);

                if (assistantContents.Count > 0)
                    allMessages.Add(new ChatMessage(ChatRole.Assistant, assistantContents));

                if (pendingToolCalls.Count == 0)
                    break;

                // Execute tool calls
                foreach (var toolCall in pendingToolCalls)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    var callId  = toolCall.CallId ?? Guid.NewGuid().ToString();
                    var toolName = toolCall.Name ?? "unknown";

                    using var toolSpan = AgentTelemetry.Activities.StartActivity("agent.tool.execute", ActivityKind.Client);
                    toolSpan?.SetTag("agent.tool.name", toolName);
                    toolSpan?.SetTag("agent.tool.call_id", callId);

                    writer.TryWrite(AgUiEvent.ToolCallStart(callId, toolName, currentMessageId));

                    if (toolCall.Arguments is not null)
                    {
                        var argsJson = JsonSerializer.Serialize(toolCall.Arguments);
                        writer.TryWrite(AgUiEvent.ToolCallArgs(callId, argsJson));
                        _logger.LogInformation("Tool call  name={Tool} args={Args}",
                            toolName, Truncate(argsJson, 300));
                    }

                    AgentTelemetry.ToolCalls.Add(1);
                    var toolSw = Stopwatch.StartNew();
                    string toolResult;
                    bool toolOk = true;

                    try
                    {
                        var tool = tools.OfType<AIFunction>().FirstOrDefault(t => t.Name == toolName);

                        if (tool is null)
                        {
                            toolResult = $"Tool '{toolName}' not found.";
                            toolOk = false;
                            _logger.LogWarning("Tool not found  name={Tool}", toolName);
                        }
                        else
                        {
                            var result = await tool.InvokeAsync(
                                new AIFunctionArguments(toolCall.Arguments ?? new Dictionary<string, object?>()),
                                cancellationToken);
                            toolResult = result?.ToString() ?? string.Empty;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        toolResult = $"Tool error: {ex.Message}";
                        toolOk = false;
                        AgentTelemetry.ToolFailures.Add(1);
                        _logger.LogWarning(ex, "Tool failed  name={Tool}", toolName);
                    }

                    var toolMs = toolSw.Elapsed.TotalMilliseconds;
                    AgentTelemetry.ToolDuration.Record(toolMs);
                    toolSpan?.SetTag("agent.tool.duration_ms", (long)toolMs);
                    toolSpan?.SetTag("agent.tool.success", toolOk);
                    toolSpan?.SetTag("agent.tool.result_chars", toolResult.Length);

                    _logger.LogInformation(
                        "Tool done  name={Tool} ok={Ok} duration={Duration:0}ms resultChars={Chars}",
                        toolName, toolOk, toolMs, toolResult.Length);

                    writer.TryWrite(AgUiEvent.ToolCallEnd(callId));
                    allMessages.Add(new ChatMessage(ChatRole.Tool,
                        [new FunctionResultContent(toolCall.CallId ?? callId, toolResult)]));
                }
            }

            session.Messages.Clear();
            session.Messages.AddRange(allMessages.Skip(1));
            session.CompleteTurn();

            var totalMs = runSw.Elapsed.TotalMilliseconds;
            AgentTelemetry.RunsFinished.Add(1);
            AgentTelemetry.RunDuration.Record(totalMs);
            runSpan?.SetTag("agent.run.duration_ms", (long)totalMs);
            runSpan?.SetTag("agent.run.llm_calls", llmCallIndex);

            _logger.LogInformation(
                "AgentRun done  thread={ThreadPrefix} run={RunId} duration={Duration:0}ms llmCalls={LlmCalls} turn={Turn}",
                Truncate(threadId, 8), runId, totalMs, llmCallIndex, session.TurnCount);

            writer.TryWrite(AgUiEvent.RunFinished(threadId, runId));
        }
        catch (OperationCanceledException)
        {
            AgentTelemetry.RunsFailed.Add(1);
            runSpan?.SetStatus(ActivityStatusCode.Error, "cancelled");
            _logger.LogInformation("AgentRun cancelled  thread={ThreadPrefix} run={RunId}", Truncate(threadId, 8), runId);
            writer.TryWrite(AgUiEvent.RunError("Run cancelled.", "RUN_CANCELLED"));
        }
        catch (Exception ex)
        {
            AgentTelemetry.RunsFailed.Add(1);
            runSpan?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "AgentRun failed  thread={ThreadPrefix} run={RunId}", Truncate(threadId, 8), runId);
            writer.TryWrite(AgUiEvent.RunError("An error occurred processing your request.", "AGENT_ERROR"));
        }
    }

    private static string ExtractNewUserMessage(AgUiRequest request, SessionEntry session)
    {
        if (request.Messages is null || request.Messages.Count == 0)
            return string.Empty;
        return request.Messages
            .LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
            ?.Content ?? string.Empty;
    }

    private static string BuildUserContent(string userMessage, TelemetryWorkspaceContext? context)
    {
        if (context is null) return userMessage;

        var sb = new StringBuilder();
        sb.AppendLine("Current workbench context:");
        if (!string.IsNullOrWhiteSpace(context.SessionKey))
            sb.AppendLine($"- Session: {context.SessionKey}");
        if (context.SelectedDrivers is { Count: > 0 })
            sb.AppendLine($"- Drivers: {string.Join(", ", context.SelectedDrivers)}");
        if (context.SelectedLap.HasValue)
            sb.AppendLine($"- Selected lap: {context.SelectedLap}");
        if (context.SelectedCorner.HasValue)
            sb.AppendLine($"- Selected corner: {context.SelectedCorner}");
        if (context.WindowStart.HasValue && context.WindowEnd.HasValue)
            sb.AppendLine($"- Time window: {context.WindowStart:O} → {context.WindowEnd:O}");
        if (!string.IsNullOrWhiteSpace(context.ActiveView))
            sb.AppendLine($"- Active view: {context.ActiveView}");
        sb.AppendLine();
        sb.AppendLine("User question:");
        sb.Append(userMessage);
        return sb.ToString();
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
