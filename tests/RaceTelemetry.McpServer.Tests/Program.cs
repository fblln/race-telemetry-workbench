using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));

var httpEndpoint = Environment.GetEnvironmentVariable("MCP_TEST_HTTP_ENDPOINT");
if (string.IsNullOrWhiteSpace(httpEndpoint))
{
    throw new InvalidOperationException("MCP_TEST_HTTP_ENDPOINT is required, for example http://127.0.0.1:5122/mcp.");
}

var transport = new HttpClientTransport(
    new HttpClientTransportOptions
    {
        Name = "race-telemetry-mcp-server-http-test",
        Endpoint = new Uri(httpEndpoint)
    },
    NullLoggerFactory.Instance);

await using var client = await McpClient.CreateAsync(
    transport,
    loggerFactory: NullLoggerFactory.Instance,
    cancellationToken: cancellation.Token);

var tools = await client.ListToolsAsync(cancellationToken: cancellation.Token);
var toolNames = tools.Select(tool => tool.Name).Order().ToArray();

AssertContains(toolNames, "list_sessions");
AssertContains(toolNames, "get_session_drivers");
AssertContains(toolNames, "get_driver_laps");
AssertContains(toolNames, "get_replay_metadata");
AssertContains(toolNames, "get_lap_telemetry");
AssertContains(toolNames, "get_lap_story");
AssertContains(toolNames, "get_lap_braking_zones");
AssertContains(toolNames, "compare_laps");
AssertContains(toolNames, "compare_laps_story");
AssertContains(toolNames, "get_race_story");
AssertContains(toolNames, "get_replay_chunk");
AssertContains(toolNames, "get_replay_context");
AssertContains(toolNames, "search_telemetry_events");
AssertContains(toolNames, "aggregate_telemetry");
AssertContains(toolNames, "detect_telemetry_windows");
AssertContains(toolNames, "analyze_driver_stints");

var sessionsResult = await client.CallToolAsync(
    "list_sessions",
    new Dictionary<string, object?>
    {
        ["sessionType"] = "R"
    },
    cancellationToken: cancellation.Token);
Assert(sessionsResult.IsError != true, "list_sessions returned an MCP tool error.");
Assert(sessionsResult.StructuredContent is not null, "list_sessions did not return structured content.");

var metadataResult = await client.CallToolAsync(
    "get_replay_metadata",
    new Dictionary<string, object?>
    {
        ["sessionId"] = "2025-italian-grand-prix-r"
    },
    cancellationToken: cancellation.Token);
Assert(metadataResult.IsError != true, "get_replay_metadata returned an MCP tool error.");
Assert(metadataResult.StructuredContent is not null, "get_replay_metadata did not return structured content.");

var raceStoryResult = await client.CallToolAsync(
    "get_race_story",
    new Dictionary<string, object?>
    {
        ["sessionId"] = "2025-italian-grand-prix-r",
        ["raceControlLimit"] = 10
    },
    cancellationToken: cancellation.Token);
Assert(raceStoryResult.IsError != true, "get_race_story returned an MCP tool error.");
Assert(raceStoryResult.StructuredContent is not null, "get_race_story did not return structured content.");

var aggregateResult = await client.CallToolAsync(
    "aggregate_telemetry",
    new Dictionary<string, object?>
    {
        ["sessionId"] = "2025-italian-grand-prix-r",
        ["drivers"] = "LEC",
        ["groupBy"] = "driver,stint,compound",
        ["metrics"] = "sample_count,avg_speed_kmh,drs_active_time_ms",
        ["limit"] = 25
    },
    cancellationToken: cancellation.Token);
Assert(aggregateResult.IsError != true, "aggregate_telemetry returned an MCP tool error.");
Assert(aggregateResult.StructuredContent is not null, "aggregate_telemetry did not return structured content.");

Console.WriteLine("RaceTelemetry.McpServer HTTP protocol smoke checks passed.");
Console.WriteLine($"Tools: {string.Join(", ", toolNames)}");

static void AssertContains(IReadOnlyCollection<string> values, string expected)
{
    if (!values.Contains(expected, StringComparer.Ordinal))
    {
        throw new InvalidOperationException($"Expected MCP tool '{expected}' was not listed.");
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
