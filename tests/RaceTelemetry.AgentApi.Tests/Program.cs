using Microsoft.Extensions.AI;
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

// ---- Grounded frame verifier ----
// One ledger shared by the verifier cases. fact-1 supported, fact-2 degraded, fact-3 omit.
static GroundedEvidenceLedger MakeLedger()
{
    var ledger = new GroundedEvidenceLedger();
    ledger.AddToolResult("summarize_strategy", 0,
        """
        {"facts":[
          {"id":"fact-1","text":"LEC gained 1 position through the lap 24 undercut","qualityStatus":"supported","narrationPolicy":"assert"},
          {"id":"fact-2","text":"LEC tyre wear was high (estimated)","qualityStatus":"degraded","narrationPolicy":"assert"},
          {"id":"fact-3","text":"internal marker 999","qualityStatus":"supported","narrationPolicy":"omit"}
        ]}
        """);
    return ledger;
}

static void VerifierCase(string name, string frame, bool expectAccept, ref int passed, ref int failed)
{
    try
    {
        var ok = new GroundedFrameVerifier().TryVerify(frame, MakeLedger(), out var parsed, out var error);
        if (ok != expectAccept) throw new Exception(expectAccept ? $"expected accept, rejected: {error}" : $"expected reject, accepted");
        if (ok && parsed is null) throw new Exception("accepted but null frame");
        Pass(name); passed++;
    }
    catch (Exception ex) { Fail(name, ex.Message); failed++; }
}

VerifierCase("Verifier accepts valid grounded claim",
    """{"k":"claim","f":["fact-1"],"t":"LEC gained 1 position through the lap 24 undercut."}""", true, ref passed, ref failed);
VerifierCase("Verifier rejects altered number",
    """{"k":"claim","f":["fact-1"],"t":"LEC gained 3 positions through the lap 24 undercut."}""", false, ref passed, ref failed);
VerifierCase("Verifier rejects unknown fact id",
    """{"k":"claim","f":["fact-99"],"t":"LEC gained 1 position."}""", false, ref passed, ref failed);
VerifierCase("Verifier rejects omit fact",
    """{"k":"claim","f":["fact-3"],"t":"Marker 999 noted."}""", false, ref passed, ref failed);
VerifierCase("Verifier rejects claim with no fact ids",
    """{"k":"claim","f":[],"t":"Something happened."}""", false, ref passed, ref failed);
VerifierCase("Verifier rejects degraded fact without caveat",
    """{"k":"claim","f":["fact-2"],"t":"LEC tyre wear was high."}""", false, ref passed, ref failed);
VerifierCase("Verifier accepts degraded fact with caveat",
    """{"k":"claim","f":["fact-2"],"t":"Available data indicates LEC tyre wear was high."}""", true, ref passed, ref failed);
VerifierCase("Verifier rejects unsupported entity token",
    """{"k":"claim","f":["fact-1"],"t":"VER gained 1 position on lap 24."}""", false, ref passed, ref failed);
VerifierCase("Verifier accepts allow-listed heading",
    """{"k":"heading","t":"## Strategy"}""", true, ref passed, ref failed);
VerifierCase("Verifier rejects unknown heading",
    """{"k":"heading","t":"## Secrets"}""", false, ref passed, ref failed);
VerifierCase("Verifier accepts follow-up question",
    """{"k":"followup","t":"Should we compare the decisive laps?"}""", true, ref passed, ref failed);
VerifierCase("Verifier rejects non-question follow-up",
    """{"k":"followup","t":"Compare the decisive laps."}""", false, ref passed, ref failed);
VerifierCase("Verifier rejects malformed frame",
    """not json""", false, ref passed, ref failed);

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

Console.WriteLine();
Console.WriteLine($"Results: {passed} passed, {failed} failed");
if (failed > 0) Environment.Exit(1);
