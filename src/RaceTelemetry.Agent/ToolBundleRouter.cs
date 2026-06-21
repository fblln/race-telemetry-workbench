using Microsoft.Extensions.AI;

namespace RaceTelemetry.Agent;

public static class ToolBundleRouter
{
    private static readonly string[] Common = ["list_sessions", "get_session_drivers", "get_session_facts"];

    private static readonly IReadOnlyDictionary<string, string[]> Bundles =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // get_race_story (slimmed) + standings cover overviews. generate_race_debrief is the
            // everything-at-once mega-tool; leave it out so the model composes from focused tools.
            ["race"] = ["get_race_story", "get_standings", "get_positions", "get_position_changes"],
            ["strategy"] = ["summarize_strategy", "analyze_driver_stints", "analyze_pit_stops", "get_standings", "get_positions"],
            ["comparison"] = ["get_driver_laps", "get_lap_story", "get_lap_quality", "compare_laps_story", "compare_laps_by_distance", "get_lap_braking_zones"],
            ["incident"] = ["get_race_control_timeline", "get_weather_trend", "get_race_story"],
            ["raw"] = ["aggregate_telemetry", "detect_telemetry_windows", "get_lap_telemetry", "get_replay_chunk", "get_replay_context", "search_telemetry_events"]
        };

    public static IReadOnlyList<AITool> Select(string question, IReadOnlyList<AITool> available)
    {
        var requested = new HashSet<string>(Common, StringComparer.Ordinal);
        var text = question.ToLowerInvariant();

        AddWhen(text, requested, "race", "race", "overview", "debrief", "summary", "winner", "result",
            "podium", "standing", "finish", "position", "order", "mover", "gained", "lost", "climbed", "dropped", "lead change", "overtake");
        AddWhen(text, requested, "strategy", "strategy", "pit", "stint", "tyre", "tire", "undercut", "overcut", "degradation");
        AddWhen(text, requested, "comparison", "compare", "comparison", "lap", "sector", "corner", "braking");
        AddWhen(text, requested, "incident", "incident", "safety car", "vsc", "yellow", "red flag", "rain", "weather", "race control");
        AddWhen(text, requested, "raw", "raw", "telemetry", "replay", "channel", "speed", "throttle", "brake", "drs");

        if (requested.Count == Common.Length)
        {
            foreach (var name in Bundles["race"])
            {
                requested.Add(name);
            }
        }

        return available
            .OfType<AIFunction>()
            .Where(tool => requested.Contains(tool.Name))
            .Cast<AITool>()
            .ToArray();
    }

    private static void AddWhen(string text, HashSet<string> requested, string bundle, params string[] terms)
    {
        if (!terms.Any(text.Contains))
        {
            return;
        }

        foreach (var name in Bundles[bundle])
        {
            requested.Add(name);
        }
    }
}
