using RaceTelemetry.Contracts;

var info = new ApiInfo(
    Name: "Race Telemetry MCP Server",
    Version: "0.1.0",
    Capabilities:
    [
        "list_sessions",
        "get_session_drivers",
        "get_driver_laps",
        "get_replay_metadata"
    ]);

Console.WriteLine($"{info.Name} {info.Version}");
Console.WriteLine("MCP transport wiring will be added after the Query API database-backed slice is in place.");
Console.WriteLine("Planned tools:");

foreach (var capability in info.Capabilities)
{
    Console.WriteLine($"- {capability}");
}
