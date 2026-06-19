using Microsoft.Extensions.Options;
using RaceTelemetry.Agent.Options;
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

Console.WriteLine();
Console.WriteLine($"Results: {passed} passed, {failed} failed");
if (failed > 0) Environment.Exit(1);
