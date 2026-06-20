using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RaceTelemetry.Agent;
using RaceTelemetry.Agent.Options;
using RaceTelemetry.AgentApi.AgUi;
using RaceTelemetry.AgentApi.Sessions;

int passed = 0, failed = 0;

static AgentSessionRegistry MakeRegistry(int maxSessions = 50) =>
    new AgentSessionRegistry(Options.Create(new TelemetryAgentOptions { MaximumSessions = maxSessions }));

static void Pass(string name) { Console.WriteLine($"  PASS  {name}"); }
static void Fail(string name, string reason) { Console.WriteLine($"  FAIL  {name}: {reason}"); }

// 1. Session created on first access
try
{
    var registry = MakeRegistry();
    var entry = await registry.GetOrCreateAsync("00000000-0000-7000-8000-000000000001");
    if (entry is null) throw new Exception("null entry");
    Pass("Session created on first access");
    passed++;
}
catch (Exception ex) { Fail("Session created on first access", ex.Message); failed++; }

// 2. Same session returned on second access
try
{
    var registry = MakeRegistry();
    var a = await registry.GetOrCreateAsync("00000000-0000-7000-8000-000000000001");
    var b = await registry.GetOrCreateAsync("00000000-0000-7000-8000-000000000001");
    if (!ReferenceEquals(a, b)) throw new Exception("different instances");
    Pass("Same session on second access");
    passed++;
}
catch (Exception ex) { Fail("Same session on second access", ex.Message); failed++; }

// 3. Concurrent first access creates one session
try
{
    var registry = MakeRegistry();
    var id = "00000000-0000-7000-8000-000000000001";
    var tasks = Enumerable.Range(0, 10).Select(_ => registry.GetOrCreateAsync(id).AsTask()).ToArray();
    var results = await Task.WhenAll(tasks);
    var unique = results.Distinct().Count();
    if (unique != 1) throw new Exception($"{unique} distinct sessions created");
    Pass("Concurrent first access creates one session");
    passed++;
}
catch (Exception ex) { Fail("Concurrent first access creates one session", ex.Message); failed++; }

// 4. Different sessions lock independently
try
{
    var registry = MakeRegistry();
    var e1 = await registry.GetOrCreateAsync("00000000-0000-7000-8000-000000000001");
    var e2 = await registry.GetOrCreateAsync("00000000-0000-7000-8000-000000000002");
    await e1.TurnLock.WaitAsync();
    var acquired2 = await e2.TurnLock.WaitAsync(TimeSpan.Zero);
    e1.TurnLock.Release();
    if (acquired2) e2.TurnLock.Release();
    if (!acquired2) throw new Exception("could not acquire second session lock independently");
    Pass("Different sessions lock independently");
    passed++;
}
catch (Exception ex) { Fail("Different sessions lock independently", ex.Message); failed++; }

// 5. Session removal is idempotent
try
{
    var registry = MakeRegistry();
    var id = "00000000-0000-7000-8000-000000000001";
    await registry.GetOrCreateAsync(id);
    registry.Remove(id);
    registry.Remove(id);
    Pass("Session removal is idempotent");
    passed++;
}
catch (Exception ex) { Fail("Session removal is idempotent", ex.Message); failed++; }

// 6. Idle sessions are evicted
try
{
    var registry = MakeRegistry();
    var id = "00000000-0000-7000-8000-000000000001";
    await registry.GetOrCreateAsync(id);
    var count = registry.RemoveExpired(DateTimeOffset.UtcNow.AddHours(2));
    if (count == 0) throw new Exception("no sessions evicted");
    Pass("Idle sessions are evicted");
    passed++;
}
catch (Exception ex) { Fail("Idle sessions are evicted", ex.Message); failed++; }

// 7. Active sessions are not evicted mid-run
try
{
    var registry = MakeRegistry();
    var id = "00000000-0000-7000-8000-000000000001";
    var entry = await registry.GetOrCreateAsync(id);
    await entry.TurnLock.WaitAsync();
    var countBefore = registry.Count;
    registry.RemoveExpired(DateTimeOffset.UtcNow.AddHours(2));
    var countAfter = registry.Count;
    entry.TurnLock.Release();
    if (countAfter != countBefore) throw new Exception("active session was evicted");
    Pass("Active sessions not evicted mid-run");
    passed++;
}
catch (Exception ex) { Fail("Active sessions not evicted mid-run", ex.Message); failed++; }

// 8. Thread ID validation rejects bad formats
try
{
    var registry = MakeRegistry();
    bool threw = false;
    try { await registry.GetOrCreateAsync("not-a-uuid"); }
    catch (ArgumentException) { threw = true; }
    if (!threw) throw new Exception("accepted invalid thread ID");
    Pass("Thread ID validation rejects bad format");
    passed++;
}
catch (Exception ex) { Fail("Thread ID validation rejects bad format", ex.Message); failed++; }

// 9. Capacity enforcement
try
{
    var registry = MakeRegistry(maxSessions: 2);
    await registry.GetOrCreateAsync("00000000-0000-7000-8000-000000000001");
    await registry.GetOrCreateAsync("00000000-0000-7000-8000-000000000002");
    bool threw = false;
    try { await registry.GetOrCreateAsync("00000000-0000-7000-8000-000000000003"); }
    catch (InvalidOperationException) { threw = true; }
    if (!threw) throw new Exception("capacity not enforced");
    Pass("Capacity enforcement rejects when full");
    passed++;
}
catch (Exception ex) { Fail("Capacity enforcement rejects when full", ex.Message); failed++; }

// 10. TurnCount increments on CompleteTurn
try
{
    var registry = MakeRegistry();
    var entry = await registry.GetOrCreateAsync("00000000-0000-7000-8000-000000000001");
    entry.CompleteTurn();
    entry.CompleteTurn();
    if (entry.TurnCount != 2) throw new Exception($"TurnCount was {entry.TurnCount}");
    Pass("TurnCount increments on CompleteTurn");
    passed++;
}
catch (Exception ex) { Fail("TurnCount increments on CompleteTurn", ex.Message); failed++; }

// ---- Tool-bundle routing ----
static IReadOnlyList<AITool> AllTools() => new[]
{
    "list_sessions","get_session_drivers","get_race_story","get_standings","generate_race_debrief",
    "summarize_strategy","analyze_driver_stints","analyze_pit_stops","get_positions",
    "compare_laps_story","compare_laps_by_distance","get_lap_braking_zones","get_driver_laps","get_lap_story","get_lap_quality",
    "list_incidents","get_race_control_timeline","get_weather_trend",
    "aggregate_telemetry","detect_telemetry_windows","get_lap_telemetry","get_replay_chunk","get_replay_context","search_telemetry_events"
}.Select(n => (AITool)AIFunctionFactory.Create((string s) => s, n)).ToList();

static void RouteCase(string name, string question, string[] mustInclude, string[] mustExclude, ref int passed, ref int failed)
{
    try
    {
        var names = ToolBundleRouter.Select(question, AllTools()).Select(t => t.Name).ToHashSet();
        foreach (var n in mustInclude) if (!names.Contains(n)) throw new Exception($"missing {n}");
        foreach (var n in mustExclude) if (names.Contains(n)) throw new Exception($"should not include {n}");
        Pass(name); passed++;
    }
    catch (Exception ex) { Fail(name, ex.Message); failed++; }
}

RouteCase("Router routes strategy questions to the strategy bundle",
    "Did the lap 24 undercut work for the strategy?",
    new[] { "summarize_strategy", "list_sessions" },
    new[] { "get_replay_chunk", "aggregate_telemetry" }, ref passed, ref failed);
RouteCase("Router routes comparison questions to the comparison bundle",
    "Compare the fastest lap between the two drivers",
    new[] { "compare_laps_by_distance", "list_sessions" },
    new[] { "generate_race_debrief" }, ref passed, ref failed);
RouteCase("Router routes incident/weather questions",
    "When did the safety car come out and did it rain?",
    new[] { "list_incidents", "get_weather_trend" },
    new[] { "get_replay_chunk" }, ref passed, ref failed);
RouteCase("Router defaults vague questions to the race bundle",
    "Tell me about it",
    new[] { "get_race_story", "generate_race_debrief", "list_sessions" },
    new[] { "get_replay_chunk" }, ref passed, ref failed);

// ---- Agent runner driven by a mocked LLM (no API key, no DB, no MCP) ----

// Drive the real AgentRunner with a fake IChatClient + canned tools, collecting every SSE event.
static async Task<List<AgUiEvent>> RunAgent(
    FakeChatClient chat, IEnumerable<AITool> tools, string question, TelemetryAgentOptions opts)
{
    var runner = new AgentRunner(
        chat,
        McpToolRegistry.ForTesting(tools),
        Options.Create(opts),
        NullLogger<AgentRunner>.Instance);
    var request = new AgUiRequest
    {
        ThreadId = "00000000-0000-7000-8000-000000000001",
        Messages = new[] { new AgUiMessage { Id = "1", Role = "user", Content = question } }
    };
    var session = new SessionEntry();
    var events = new List<AgUiEvent>();
    await foreach (var evt in runner.RunAsync(request.ThreadId, "run-1", request, session))
        events.Add(evt);
    return events;
}

static ChatResponseUpdate ToolCall(string callId, string name) =>
    new() { Role = ChatRole.Assistant, Contents = new List<AIContent> { new FunctionCallContent(callId, name, new Dictionary<string, object?>()) } };
static ChatResponseUpdate TextDelta(string text) =>
    new() { Role = ChatRole.Assistant, Contents = new List<AIContent> { new TextContent(text) } };
static AITool CannedTool(string name, string resultJson) =>
    AIFunctionFactory.Create(() => resultJson, name);

// Single tool call cap so the planner runs exactly once before the final answer.
var oneToolOpts = new TelemetryAgentOptions { MaximumToolCalls = 1, MaximumToolRounds = 2 };

// 11. Full happy path: plan -> tool exec -> grounded streamed answer, correct event order
try
{
    var toolResult = "{\"facts\":[{\"id\":\"f1\",\"text\":\"LEC set the fastest lap, 1:21.046\"}]}";
    var chat = new FakeChatClient(
        new[] { ToolCall("call-1", "get_driver_laps") },                                   // planning
        new[] { TextDelta("LEC was "), TextDelta("fastest "), TextDelta("at 1:21.046.") }); // final answer
    var events = await RunAgent(chat, new[] { CannedTool("get_driver_laps", toolResult), CannedTool("list_sessions", "{}") },
        "Who set the fastest lap?", oneToolOpts);

    var types = events.Select(e => e.Type).ToList();
    string[] expected = { "RUN_STARTED", "TOOL_CALL_START", "TOOL_CALL_ARGS", "TOOL_CALL_END",
        "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", "RUN_FINISHED" };
    foreach (var t in expected) if (!types.Contains(t)) throw new Exception($"missing event {t} (got: {string.Join(",", types)})");
    if (types[0] != "RUN_STARTED" || types[^1] != "RUN_FINISHED") throw new Exception("run not bracketed by STARTED/FINISHED");
    if (types.IndexOf("TOOL_CALL_START") > types.IndexOf("TEXT_MESSAGE_START")) throw new Exception("tool calls must precede the answer");
    var calledTool = events.First(e => e.Type == "TOOL_CALL_START").ToolCallName;
    if (calledTool != "get_driver_laps") throw new Exception($"wrong tool routed/called: {calledTool}");
    Pass("Agent run emits ordered plan/tool/answer events");
    passed++;
}
catch (Exception ex) { Fail("Agent run emits ordered plan/tool/answer events", ex.Message); failed++; }

// 12. Streaming is incremental: multiple TEXT_MESSAGE_CONTENT deltas that concatenate to the answer
try
{
    var chat = new FakeChatClient(
        new[] { ToolCall("call-1", "get_driver_laps") },
        new[] { TextDelta("LEC was "), TextDelta("fastest "), TextDelta("at 1:21.046.") });
    var events = await RunAgent(chat, new[] { CannedTool("get_driver_laps", "{}"), CannedTool("list_sessions", "{}") },
        "Who set the fastest lap?", oneToolOpts);
    var deltas = events.Where(e => e.Type == "TEXT_MESSAGE_CONTENT").Select(e => e.Delta).ToList();
    if (deltas.Count < 2) throw new Exception($"expected streamed deltas, got {deltas.Count}");
    var answer = string.Concat(deltas);
    if (answer != "LEC was fastest at 1:21.046.") throw new Exception($"reassembled answer wrong: '{answer}'");
    Pass("Streaming yields multiple deltas that reassemble the answer");
    passed++;
}
catch (Exception ex) { Fail("Streaming yields multiple deltas that reassemble the answer", ex.Message); failed++; }

// 13. Grounding: the tool result is fed into the final-answer LLM prompt (evidence packet)
try
{
    var toolResult = "{\"facts\":[{\"id\":\"f1\",\"text\":\"TOP_SPEED_362_KMH_BY_LEC\"}]}";
    var chat = new FakeChatClient(
        new[] { ToolCall("call-1", "aggregate_telemetry") },
        new[] { TextDelta("Top speed was 362 km/h.") });
    await RunAgent(chat, new[] { CannedTool("aggregate_telemetry", toolResult), CannedTool("list_sessions", "{}") },
        "What was the top speed?", oneToolOpts);
    // Last LLM call is the finalizer; its user message must carry the tool's evidence.
    var finalPrompt = string.Concat(chat.Received[^1].Select(m => m.Text));
    if (!finalPrompt.Contains("TOP_SPEED_362_KMH_BY_LEC"))
        throw new Exception("tool result did not reach the finalizer evidence packet");
    Pass("Tool evidence is grounded into the final answer prompt");
    passed++;
}
catch (Exception ex) { Fail("Tool evidence is grounded into the final answer prompt", ex.Message); failed++; }

// 14. No-tool path: planner asks for nothing -> still produces a streamed answer
try
{
    var chat = new FakeChatClient(
        Array.Empty<ChatResponseUpdate>(),                 // planning: no tool calls
        new[] { TextDelta("I don't have telemetry for that.") });
    var events = await RunAgent(chat, new[] { CannedTool("list_sessions", "{}") },
        "Hello", new TelemetryAgentOptions());
    var types = events.Select(e => e.Type).ToList();
    if (types.Contains("TOOL_CALL_START")) throw new Exception("no tool should have been called");
    if (!types.Contains("TEXT_MESSAGE_CONTENT") || types[^1] != "RUN_FINISHED") throw new Exception("expected a finished streamed answer");
    Pass("No-tool path still streams a grounded answer");
    passed++;
}
catch (Exception ex) { Fail("No-tool path still streams a grounded answer", ex.Message); failed++; }

// 15. Error path: LLM throws -> run surfaces RUN_ERROR, no crash
try
{
    var chat = new FakeChatClient(new InvalidOperationException("boom"));
    var events = await RunAgent(chat, new[] { CannedTool("list_sessions", "{}") },
        "Who won?", new TelemetryAgentOptions());
    var err = events.LastOrDefault(e => e.Type == "RUN_ERROR");
    if (err is null) throw new Exception($"expected RUN_ERROR (got: {string.Join(",", events.Select(e => e.Type))})");
    if (string.IsNullOrEmpty(err.Code)) throw new Exception("RUN_ERROR missing code");
    Pass("LLM failure surfaces RUN_ERROR with a code");
    passed++;
}
catch (Exception ex) { Fail("LLM failure surfaces RUN_ERROR with a code", ex.Message); failed++; }

// ---- Follow-up block parsing (chat UI consumes the marker the finalizer emits) ----
static void FollowUpCase(string name, string content, string expectBody, string[] expectFollowUps, ref int passed, ref int failed)
{
    try
    {
        var (body, ups) = RaceTelemetry.Contracts.ChatFollowUps.Split(content);
        if (body != expectBody) throw new Exception($"body was '{body}'");
        if (!ups.SequenceEqual(expectFollowUps)) throw new Exception($"follow-ups were [{string.Join(" | ", ups)}]");
        Pass(name); passed++;
    }
    catch (Exception ex) { Fail(name, ex.Message); failed++; }
}

FollowUpCase("Follow-ups: no marker leaves content untouched",
    "Just the answer, no marker.", "Just the answer, no marker.", Array.Empty<string>(), ref passed, ref failed);
FollowUpCase("Follow-ups: marker splits body from plain questions",
    "LEC was fastest.\n---FOLLOWUP---\nCompare the top three?\nWho led lap one?",
    "LEC was fastest.", new[] { "Compare the top three?", "Who led lap one?" }, ref passed, ref failed);
FollowUpCase("Follow-ups: bullets and quotes are stripped, blanks dropped",
    "Body here.\n---FOLLOWUP---\n- \"First question?\"\n\n* Second question?\n",
    "Body here.", new[] { "First question?", "Second question?" }, ref passed, ref failed);

Console.WriteLine();
Console.WriteLine($"Results: {passed} passed, {failed} failed");
if (failed > 0) Environment.Exit(1);

// Scripted IChatClient: each GetStreamingResponseAsync call replays the next turn of updates,
// or throws if constructed with an exception. Records the messages it received per call.
sealed class FakeChatClient : IChatClient
{
    private readonly Queue<IReadOnlyList<ChatResponseUpdate>> _turns;
    private readonly Exception? _throw;
    public List<IReadOnlyList<ChatMessage>> Received { get; } = new();

    public FakeChatClient(params IReadOnlyList<ChatResponseUpdate>[] turns) => _turns = new(turns);
    public FakeChatClient(Exception toThrow) { _turns = new(); _throw = toThrow; }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Received.Add(messages.ToList());
        if (_throw is not null) throw _throw;
        var turn = _turns.Count > 0 ? _turns.Dequeue() : Array.Empty<ChatResponseUpdate>();
        foreach (var update in turn) { await Task.Yield(); yield return update; }
    }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
