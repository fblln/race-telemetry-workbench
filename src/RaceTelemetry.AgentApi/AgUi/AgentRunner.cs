using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RaceTelemetry.Agent;
using RaceTelemetry.Agent.Options;
using RaceTelemetry.AgentApi.Sessions;
using RaceTelemetry.Contracts;

namespace RaceTelemetry.AgentApi.AgUi;

public sealed class AgentRunner
{
    private static readonly JsonSerializerOptions ToolJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IChatClient _chatClient;
    private readonly McpToolRegistry _mcpTools;
    private readonly TelemetryAgentOptions _options;
    private readonly GroundedFrameVerifier _frameVerifier;
    private readonly ILogger<AgentRunner> _logger;

    public AgentRunner(
        IChatClient chatClient,
        McpToolRegistry mcpTools,
        IOptions<TelemetryAgentOptions> options,
        GroundedFrameVerifier frameVerifier,
        ILogger<AgentRunner> logger)
    {
        _chatClient = chatClient;
        _mcpTools = mcpTools;
        _options = options.Value;
        _frameVerifier = frameVerifier;
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
        {
            yield return evt;
        }

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

        writer.TryWrite(AgUiEvent.RunStarted(threadId, runId));
        var newUserMessage = ExtractNewUserMessage(request);
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

        var userContent = BuildUserContent(newUserMessage, request.State);
        session.Messages.Add(new ChatMessage(ChatRole.User, userContent));
        var history = session.Messages.Count > _options.MaxContextMessages
            ? session.Messages.Skip(session.Messages.Count - _options.MaxContextMessages).ToList()
            : session.Messages.ToList();

        var allTools = _mcpTools.GetTools();
        var tools = ToolBundleRouter.Select(newUserMessage, allTools);
        runSpan?.SetTag("agent.tools_available", tools.Count);

        var acquisitionMessages = new List<ChatMessage>
        {
            new(ChatRole.System, AgentInstructions.Acquisition)
        };
        acquisitionMessages.AddRange(history);

        var planningOptions = new ChatOptions
        {
            Tools = tools.ToList(),
            ToolMode = ChatToolMode.Auto,
            AllowMultipleToolCalls = true,
            MaxOutputTokens = _options.ToolPlanningMaxOutputTokens,
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low }
        };

        var ledger = new GroundedEvidenceLedger();
        var deduplicatedCalls = new Dictionary<string, Task<ToolExecutionResult>>(StringComparer.Ordinal);
        var totalToolCalls = 0;
        var llmCalls = 0;

        try
        {
            for (var round = 0; round < _options.MaximumToolRounds; round++)
            {
                llmCalls++;
                var response = await CollectPlanningResponseAsync(acquisitionMessages, planningOptions, cancellationToken);
                if (response.Contents.Count > 0)
                {
                    acquisitionMessages.Add(new ChatMessage(ChatRole.Assistant, response.Contents.ToList()));
                }

                if (response.ToolCalls.Count == 0)
                {
                    break;
                }

                var invocations = response.ToolCalls.Select((call, index) => new ToolInvocation(
                    index,
                    call,
                    call.Name ?? "unknown",
                    call.CallId ?? Guid.NewGuid().ToString(),
                    BuildCallKey(call))).ToArray();

                var results = await ExecuteToolBatchAsync(
                    invocations,
                    tools,
                    deduplicatedCalls,
                    totalToolCalls,
                    writer,
                    cancellationToken);
                totalToolCalls += invocations.Length;

                foreach (var result in results.OrderBy(result => result.Invocation.Index))
                {
                    acquisitionMessages.Add(new ChatMessage(ChatRole.Tool,
                        [new FunctionResultContent(result.Invocation.CallId, result.Result)]));
                    ledger.AddToolResult(result.Invocation.ToolName, totalToolCalls - invocations.Length + result.Invocation.Index, result.Result);
                }

                if (totalToolCalls >= _options.MaximumToolCalls)
                {
                    break;
                }
            }

            if (ledger.Facts.Count == 0)
            {
                ledger.AddToolResult("availability", 0, "No usable telemetry evidence was returned for this request.");
            }

            llmCalls++;
            var finalText = await StreamVerifiedFinalAnswerAsync(
                newUserMessage,
                ledger,
                writer,
                cancellationToken);

            session.Messages.Add(new ChatMessage(ChatRole.Assistant, finalText));
            CompactSession(session);
            session.CompleteTurn();

            var totalMs = runSw.Elapsed.TotalMilliseconds;
            AgentTelemetry.RunsFinished.Add(1);
            AgentTelemetry.RunDuration.Record(totalMs);
            runSpan?.SetTag("agent.run.duration_ms", (long)totalMs);
            runSpan?.SetTag("agent.run.llm_calls", llmCalls);
            runSpan?.SetTag("agent.run.tool_calls", totalToolCalls);
            writer.TryWrite(AgUiEvent.RunFinished(threadId, runId));
        }
        catch (OperationCanceledException)
        {
            AgentTelemetry.RunsFailed.Add(1);
            runSpan?.SetStatus(ActivityStatusCode.Error, "cancelled");
            writer.TryWrite(AgUiEvent.RunError("Run cancelled.", "RUN_CANCELLED"));
        }
        catch (Exception ex)
        {
            AgentTelemetry.RunsFailed.Add(1);
            runSpan?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "AgentRun failed thread={ThreadPrefix} run={RunId}", Truncate(threadId, 8), runId);
            writer.TryWrite(AgUiEvent.RunError("An error occurred processing your request.", "AGENT_ERROR"));
        }
    }

    private async Task<PlanningResponse> CollectPlanningResponseAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options,
        CancellationToken cancellationToken)
    {
        var updates = new List<ChatResponseUpdate>();
        var toolCalls = new List<FunctionCallContent>();
        AgentTelemetry.LlmCalls.Add(1);
        await foreach (var update in _chatClient.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            updates.Add(update);
            toolCalls.AddRange(update.Contents.OfType<FunctionCallContent>());
        }

        var contents = new List<AIContent>();
        var text = string.Concat(updates.SelectMany(update => update.Contents).OfType<TextContent>().Select(item => item.Text));
        if (!string.IsNullOrWhiteSpace(text))
        {
            contents.Add(new TextContent(text));
        }
        contents.AddRange(toolCalls);
        return new PlanningResponse(contents, toolCalls);
    }

    private async Task<IReadOnlyList<ToolExecutionResult>> ExecuteToolBatchAsync(
        IReadOnlyList<ToolInvocation> invocations,
        IReadOnlyList<AITool> tools,
        Dictionary<string, Task<ToolExecutionResult>> deduplicatedCalls,
        int callsBeforeBatch,
        ChannelWriter<AgUiEvent> writer,
        CancellationToken cancellationToken)
    {
        AgentTelemetry.ParallelToolBatches.Add(1);
        var semaphore = new SemaphoreSlim(Math.Max(1, _options.MaximumConcurrentToolCalls));
        var pending = new List<Task<ToolExecutionResult>>();

        foreach (var invocation in invocations)
        {
            writer.TryWrite(AgUiEvent.ToolCallStart(invocation.CallId, invocation.ToolName));
            writer.TryWrite(AgUiEvent.ToolCallArgs(
                invocation.CallId,
                JsonSerializer.Serialize(invocation.Call.Arguments ?? new Dictionary<string, object?>(), ToolJsonOptions)));

            Task<ToolExecutionResult> task;
            if (callsBeforeBatch + invocation.Index >= _options.MaximumToolCalls)
            {
                task = Task.FromResult(new ToolExecutionResult(
                    invocation,
                    "{\"isError\":true,\"message\":\"Maximum tool-call limit reached.\"}",
                    false,
                    0));
            }
            else if (deduplicatedCalls.TryGetValue(invocation.CanonicalKey, out var existing))
            {
                task = RebindInvocationAsync(existing, invocation);
            }
            else
            {
                task = ExecuteToolAsync(invocation, tools, semaphore, cancellationToken);
                deduplicatedCalls[invocation.CanonicalKey] = task;
            }
            pending.Add(task);
        }

        var completed = new List<ToolExecutionResult>(pending.Count);
        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending);
            pending.Remove(finished);
            var result = await finished;
            completed.Add(result);
            writer.TryWrite(AgUiEvent.ToolCallEnd(result.Invocation.CallId));
        }

        semaphore.Dispose();
        return completed;
    }

    private async Task<ToolExecutionResult> ExecuteToolAsync(
        ToolInvocation invocation,
        IReadOnlyList<AITool> tools,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ToolCallTimeout);
            var tool = tools.OfType<AIFunction>().FirstOrDefault(candidate => candidate.Name == invocation.ToolName);
            if (tool is null)
            {
                return new ToolExecutionResult(invocation,
                    $"{{\"isError\":true,\"message\":\"Tool '{invocation.ToolName}' was not available in this route.\"}}",
                    false,
                    stopwatch.Elapsed.TotalMilliseconds);
            }

            try
            {
                AgentTelemetry.ToolCalls.Add(1);
                var raw = await tool.InvokeAsync(
                    new AIFunctionArguments(invocation.Call.Arguments ?? new Dictionary<string, object?>()),
                    timeout.Token);
                var result = SerializeToolResult(raw, _options.MaximumToolResultCharacters);
                return new ToolExecutionResult(invocation, result, true, stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                AgentTelemetry.ToolFailures.Add(1);
                return new ToolExecutionResult(invocation,
                    "{\"isError\":true,\"message\":\"Tool call timed out.\"}",
                    false,
                    stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                AgentTelemetry.ToolFailures.Add(1);
                return new ToolExecutionResult(invocation,
                    JsonSerializer.Serialize(new { isError = true, message = ex.Message }, ToolJsonOptions),
                    false,
                    stopwatch.Elapsed.TotalMilliseconds);
            }
        }
        finally
        {
            AgentTelemetry.ToolDuration.Record(stopwatch.Elapsed.TotalMilliseconds);
            semaphore.Release();
        }
    }

    private async Task<string> StreamVerifiedFinalAnswerAsync(
        string question,
        GroundedEvidenceLedger ledger,
        ChannelWriter<AgUiEvent> writer,
        CancellationToken cancellationToken)
    {
        var evidence = ledger.BuildPrompt(_options.MaximumEvidenceCharacters);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, AgentInstructions.GroundedFinalizer),
            new(ChatRole.User, $"User question:\n{question}\n\nEvidence packet:\n{evidence}")
        };
        var options = new ChatOptions
        {
            MaxOutputTokens = _options.FinalAnswerMaxOutputTokens,
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low }
        };

        var messageId = Guid.NewGuid().ToString();
        var messageStarted = false;
        var emittedClaim = false;
        var followups = 0;
        var buffer = new StringBuilder();
        var finalText = new StringBuilder();

        AgentTelemetry.LlmCalls.Add(1);
        await foreach (var update in _chatClient.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            foreach (var text in update.Contents.OfType<TextContent>())
            {
                buffer.Append(text.Text);
                while (TryTakeLine(buffer, out var line))
                {
                    ProcessFrame(line, ledger, writer, messageId, ref messageStarted, ref emittedClaim, ref followups, finalText);
                }
            }
        }

        if (buffer.Length > 0)
        {
            ProcessFrame(buffer.ToString(), ledger, writer, messageId, ref messageStarted, ref emittedClaim, ref followups, finalText);
        }

        if (!emittedClaim)
        {
            var fallback = ledger.Facts.Values.FirstOrDefault(fact => fact.NarrationPolicy != "omit")?.Text
                ?? "The requested telemetry evidence is unavailable.";
            EmitText(writer, messageId, fallback + " ", ref messageStarted, finalText);
        }

        if (followups < 3)
        {
            if (followups == 0)
            {
                EmitText(writer, messageId, "\n\n---FOLLOWUP---\n", ref messageStarted, finalText);
            }
            var defaults = new[]
            {
                "Which driver's strategy should we inspect next?",
                "Should we compare the decisive laps?",
                "Do you want the incident and weather timeline?"
            };
            for (; followups < 3; followups++)
            {
                EmitText(writer, messageId, $"- {defaults[followups]}\n", ref messageStarted, finalText);
            }
        }

        if (messageStarted)
        {
            writer.TryWrite(AgUiEvent.TextMessageEnd(messageId));
        }
        return finalText.ToString();
    }

    private void ProcessFrame(
        string line,
        GroundedEvidenceLedger ledger,
        ChannelWriter<AgUiEvent> writer,
        string messageId,
        ref bool messageStarted,
        ref bool emittedClaim,
        ref int followups,
        StringBuilder finalText)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (!_frameVerifier.TryVerify(line.Trim(), ledger, out var frame, out var error) || frame is null)
        {
            AgentTelemetry.ClaimsRejected.Add(1);
            _logger.LogWarning("Rejected grounded stream frame: {Reason}", error);
            return;
        }

        switch (frame.Kind)
        {
            case "claim":
                AgentTelemetry.ClaimsVerified.Add(1);
                emittedClaim = true;
                EmitText(writer, messageId, frame.Text + " ", ref messageStarted, finalText);
                break;
            case "heading":
                EmitText(writer, messageId, $"\n\n{frame.Text}\n\n", ref messageStarted, finalText);
                break;
            case "followup" when followups < 3:
                if (followups == 0)
                {
                    EmitText(writer, messageId, "\n\n---FOLLOWUP---\n", ref messageStarted, finalText);
                }
                EmitText(writer, messageId, $"- {frame.Text}\n", ref messageStarted, finalText);
                followups++;
                break;
        }
    }

    private static void EmitText(
        ChannelWriter<AgUiEvent> writer,
        string messageId,
        string text,
        ref bool messageStarted,
        StringBuilder finalText)
    {
        if (!messageStarted)
        {
            writer.TryWrite(AgUiEvent.TextMessageStart(messageId));
            messageStarted = true;
        }
        writer.TryWrite(AgUiEvent.TextMessageContent(messageId, text));
        finalText.Append(text);
    }

    private static bool TryTakeLine(StringBuilder buffer, out string line)
    {
        for (var index = 0; index < buffer.Length; index++)
        {
            if (buffer[index] != '\n')
            {
                continue;
            }
            line = buffer.ToString(0, index).TrimEnd('\r');
            buffer.Remove(0, index + 1);
            return true;
        }
        line = string.Empty;
        return false;
    }

    private static async Task<ToolExecutionResult> RebindInvocationAsync(
        Task<ToolExecutionResult> existing,
        ToolInvocation invocation)
    {
        var result = await existing;
        return result with { Invocation = invocation };
    }

    private static string SerializeToolResult(object? value, int maximumCharacters)
    {
        string result;
        if (value is null)
        {
            result = string.Empty;
        }
        else if (value is string text)
        {
            result = text;
        }
        else
        {
            try
            {
                result = JsonSerializer.Serialize(value, value.GetType(), ToolJsonOptions);
            }
            catch (NotSupportedException)
            {
                result = value.ToString() ?? string.Empty;
            }
        }
        return result.Length <= maximumCharacters ? result : result[..maximumCharacters];
    }

    private static string BuildCallKey(FunctionCallContent call) =>
        $"{call.Name}:{JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>(), ToolJsonOptions)}";

    private void CompactSession(SessionEntry session)
    {
        if (session.Messages.Count <= _options.MaxContextMessages)
        {
            return;
        }
        var compacted = session.Messages.Skip(session.Messages.Count - _options.MaxContextMessages).ToArray();
        session.Messages.Clear();
        session.Messages.AddRange(compacted);
    }

    private static string ExtractNewUserMessage(AgUiRequest request) =>
        request.Messages?.LastOrDefault(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content
        ?? string.Empty;

    private static string BuildUserContent(string userMessage, TelemetryWorkspaceContext? context)
    {
        if (context is null)
        {
            return userMessage;
        }

        var builder = new StringBuilder("Current workbench context:\n");
        if (!string.IsNullOrWhiteSpace(context.SessionKey)) builder.AppendLine($"- Session: {context.SessionKey}");
        if (context.SelectedDrivers is { Count: > 0 }) builder.AppendLine($"- Drivers: {string.Join(", ", context.SelectedDrivers)}");
        if (context.SelectedLap.HasValue) builder.AppendLine($"- Selected lap: {context.SelectedLap}");
        if (context.SelectedCorner.HasValue) builder.AppendLine($"- Selected corner: {context.SelectedCorner}");
        if (context.WindowStart.HasValue && context.WindowEnd.HasValue) builder.AppendLine($"- Time window: {context.WindowStart:O} → {context.WindowEnd:O}");
        if (!string.IsNullOrWhiteSpace(context.ActiveView)) builder.AppendLine($"- Active view: {context.ActiveView}");
        builder.AppendLine().AppendLine("User question:").Append(userMessage);
        return builder.ToString();
    }

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum] + "…";

    private sealed record PlanningResponse(IReadOnlyList<AIContent> Contents, IReadOnlyList<FunctionCallContent> ToolCalls);

    private sealed record ToolInvocation(
        int Index,
        FunctionCallContent Call,
        string ToolName,
        string CallId,
        string CanonicalKey);

    private sealed record ToolExecutionResult(
        ToolInvocation Invocation,
        string Result,
        bool Success,
        double DurationMs);
}
