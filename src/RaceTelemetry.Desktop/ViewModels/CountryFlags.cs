namespace RaceTelemetry.Desktop.ViewModels;

/// <summary>
/// National flag emoji for a country — a factual identifier, never a team livery (§2a/§8.8).
/// Shared by the launcher circuit cards and the command palette session rows.
/// </summary>
public static class CountryFlags
{
    public static string For(string? country)
    {
        if (string.IsNullOrWhiteSpace(country)) return "🏳";

        return country.Trim().ToUpperInvariant() switch
        {
            "AUSTRALIA" => "🇦🇺",
            "AUSTRIA" => "🇦🇹",
            "AZERBAIJAN" => "🇦🇿",
            "BAHRAIN" => "🇧🇭",
            "BELGIUM" => "🇧🇪",
            "BRAZIL" => "🇧🇷",
            "CANADA" => "🇨🇦",
            "CHINA" => "🇨🇳",
            "HUNGARY" => "🇭🇺",
            "ITALY" => "🇮🇹",
            "JAPAN" => "🇯🇵",
            "MEXICO" => "🇲🇽",
            "MONACO" => "🇲🇨",
            "NETHERLANDS" => "🇳🇱",
            "QATAR" => "🇶🇦",
            "SAUDI ARABIA" => "🇸🇦",
            "SINGAPORE" => "🇸🇬",
            "SPAIN" => "🇪🇸",
            "UNITED ARAB EMIRATES" => "🇦🇪",
            "UNITED KINGDOM" => "🇬🇧",
            "USA" or "UNITED STATES" => "🇺🇸",
            _ => "🏳",
        };
    }

    /// <summary>Human session-type name from the FastF1 code (R, Q, S, FP1…).</summary>
    public static string SessionTypeName(string? type) => type?.Trim().ToUpperInvariant() switch
    {
        "R" => "Race",
        "Q" => "Qualifying",
        "S" or "SQ" or "SS" => "Sprint",
        "FP1" => "Practice 1",
        "FP2" => "Practice 2",
        "FP3" => "Practice 3",
        null or "" => "Session",
        _ => type!,
    };
}
