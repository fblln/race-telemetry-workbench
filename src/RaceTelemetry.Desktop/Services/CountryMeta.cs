namespace RaceTelemetry.Desktop.Services;

/// <summary>Flag emoji + 3-letter code per F1-calendar country, for breadcrumb + circuit cards.</summary>
public static class CountryMeta
{
    public static (string Flag, string Code) Of(string? country) =>
        country is not null && Map.TryGetValue(country, out var v) ? v : ("🏁", "—");

    static readonly Dictionary<string, (string, string)> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["United Arab Emirates"] = ("🇦🇪", "UAE"), ["Australia"] = ("🇦🇺", "AUS"), ["Austria"] = ("🇦🇹", "AUT"),
        ["Azerbaijan"] = ("🇦🇿", "AZE"), ["Bahrain"] = ("🇧🇭", "BHR"), ["Belgium"] = ("🇧🇪", "BEL"),
        ["Canada"] = ("🇨🇦", "CAN"), ["Netherlands"] = ("🇳🇱", "NED"), ["Italy"] = ("🇮🇹", "ITA"),
        ["Hungary"] = ("🇭🇺", "HUN"), ["Japan"] = ("🇯🇵", "JPN"), ["United States"] = ("🇺🇸", "USA"),
        ["Mexico"] = ("🇲🇽", "MEX"), ["Monaco"] = ("🇲🇨", "MON"), ["Spain"] = ("🇪🇸", "ESP"),
        ["United Kingdom"] = ("🇬🇧", "GBR"), ["Great Britain"] = ("🇬🇧", "GBR"), ["Saudi Arabia"] = ("🇸🇦", "KSA"),
        ["Singapore"] = ("🇸🇬", "SGP"), ["Brazil"] = ("🇧🇷", "BRA"), ["Qatar"] = ("🇶🇦", "QAT"), ["China"] = ("🇨🇳", "CHN"),
    };
}
