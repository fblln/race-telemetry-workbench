namespace RaceTelemetry.Desktop.Controls;

/// <summary>
/// Project-owned team livery treatments for UI swatches. These are local color
/// bands derived from team identity, not external logos or downloaded artwork.
/// </summary>
public static class TeamLiveryAssets
{
    private static readonly Dictionary<string, TeamLivery> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["McLaren"] = new("#FF8000", "#111111", "MCL"),
        ["Ferrari"] = new("#E80020", "#FFF200", "FER"),
        ["Red Bull Racing"] = new("#1E41FF", "#E10600", "RBR"),
        ["Mercedes"] = new("#00D2BE", "#111111", "MER"),
        ["Williams"] = new("#00A3E0", "#001F60", "WIL"),
        ["Aston Martin"] = new("#006F62", "#CEDC00", "AMR"),
        ["Alpine"] = new("#0090FF", "#FF87BC", "ALP"),
        ["Haas F1 Team"] = new("#FFFFFF", "#E6002B", "HAA"),
        ["RB"] = new("#1434CB", "#FFFFFF", "VCB"),
        ["Racing Bulls"] = new("#1434CB", "#FFFFFF", "VCB"),
        ["Kick Sauber"] = new("#52E252", "#111111", "SAU"),
        ["Sauber"] = new("#52E252", "#111111", "SAU"),
    };

    public static TeamLivery For(string? teamName)
    {
        if (!string.IsNullOrWhiteSpace(teamName))
        {
            foreach (var (key, livery) in Known)
            {
                if (teamName.Contains(key, StringComparison.OrdinalIgnoreCase))
                    return livery;
            }
        }

        return new TeamLivery("#5C544A", "#2A2620", "---");
    }
}

public sealed record TeamLivery(string PrimaryHex, string SecondaryHex, string ShortCode);
