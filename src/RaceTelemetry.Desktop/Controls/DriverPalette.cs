namespace RaceTelemetry.Desktop.Controls;

using Microsoft.Maui.Graphics;

/// <summary>
/// Project-owned categorical driver palette (§8.8, design system §2a). Original,
/// colorblind-safe (Okabe-Ito derived) hues — never team liveries. A small fixed
/// map keeps the regular grid stable; any other code falls back to a deterministic
/// slot so colors stay consistent across the position trace, field rail, replay
/// markers, and the launcher driver multi-select. Amber is reserved for the
/// cursor and is never used here.
/// </summary>
public static class DriverPalette
{
    private static readonly Dictionary<string, string> Fixed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LEC"] = "#22A7FF", // sky blue
        ["HAM"] = "#FF931A", // orange
        ["NOR"] = "#15D981", // green
        ["PIA"] = "#FFCE1A", // gold
        ["VER"] = "#E86BE0", // orchid
        ["RUS"] = "#1FE0CE", // teal
        ["SAI"] = "#FF5A8A", // pink
        ["ALO"] = "#3E9BF5", // azure
        ["PER"] = "#9B8CFF", // periwinkle
        ["GAS"] = "#5AD0FF", // cyan
    };

    // Okabe-Ito derived fallback ramp for the rest of the field.
    private static readonly string[] Fallback =
    {
        "#56B4E9", "#E69F00", "#009E73", "#F0E442", "#CC79A7",
        "#0072B2", "#D55E00", "#8FD14F", "#B98CFF", "#00C2A8",
    };

    public static string HexFor(string driverCode)
    {
        if (string.IsNullOrWhiteSpace(driverCode))
        {
            return Fallback[0];
        }

        if (Fixed.TryGetValue(driverCode, out var hex))
        {
            return hex;
        }

        // Stable, case-insensitive hash so a given code always lands on one slot.
        var sum = 0;
        foreach (var ch in driverCode.ToUpperInvariant())
        {
            sum = (sum * 31 + ch) & 0x7fffffff;
        }

        return Fallback[sum % Fallback.Length];
    }

    public static Color ColorFor(string driverCode) => Color.FromArgb(HexFor(driverCode));
}
