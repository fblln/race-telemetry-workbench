using System.Globalization;
using RaceTelemetry.Desktop.Controls;

namespace RaceTelemetry.Desktop;

/// <summary>True when the bound value is not null (and not empty/whitespace for strings).</summary>
public sealed class IsNotNullConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s ? !string.IsNullOrWhiteSpace(s) : value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Boolean negation for simple visibility bindings.</summary>
public sealed class InvertedBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}

/// <summary>Formats a millisecond lap time as M:SS.mmm (or SS.mmm under a minute).</summary>
public sealed class LapTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return "—";
        var ms = System.Convert.ToInt64(value);
        if (ms <= 0) return "—";
        var minutes = ms / 60_000;
        var seconds = (ms % 60_000) / 1_000;
        var millis = ms % 1_000;
        return minutes > 0 ? $"{minutes}:{seconds:00}.{millis:000}" : $"{seconds}.{millis:000}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Formats a millisecond gap as a leading-plus seconds value, blank for the leader.</summary>
public sealed class GapConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return string.Empty;
        var ms = System.Convert.ToInt64(value);
        if (ms <= 0) return "—";
        return $"+{ms / 1000.0:0.000}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps a bool to one of two colors. ConverterParameter is "trueHex;falseHex";
/// the false color defaults to text-secondary.
/// </summary>
public sealed class BoolToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var (trueHex, falseHex) = ParseParam(parameter as string);
        return Microsoft.Maui.Graphics.Color.FromArgb(value is true ? trueHex : falseHex);
    }

    private static (string, string) ParseParam(string? param)
    {
        var parts = param?.Split(';');
        var trueHex = parts is { Length: > 0 } && !string.IsNullOrWhiteSpace(parts[0]) ? parts[0] : "#27D98C";
        var falseHex = parts is { Length: > 1 } && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : "#BCB1A2";
        return (trueHex, falseHex);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps a driver status ("running"/"out") to a green or muted status-dot color.</summary>
public sealed class StatusColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Microsoft.Maui.Graphics.Color.FromArgb(
            string.Equals(value as string, "running", StringComparison.OrdinalIgnoreCase) ? "#27D98C" : "#5C544A");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps a driver code to its project-owned categorical color (§8.8).</summary>
public sealed class DriverColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => DriverPalette.ColorFor(value as string ?? string.Empty);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Returns the first non-empty string, else a fallback passed as parameter.</summary>
public sealed class DashIfEmptyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrWhiteSpace(s) ? s : (parameter as string ?? "—");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps a bool to one of two opacity values. ConverterParameter is "trueValue;falseValue",
/// e.g. "0.34;1" or "1;0.35". Used for locked rail items and disabled chrome.
/// </summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var (trueVal, falseVal) = ParseParam(parameter as string);
        return value is true ? trueVal : falseVal;
    }

    private static (double, double) ParseParam(string? param)
    {
        var parts = param?.Split(';');
        var trueVal = parts is { Length: > 0 } && double.TryParse(parts[0], System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out var t) ? t : 1.0;
        var falseVal = parts is { Length: > 1 } && double.TryParse(parts[1], System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out var f) ? f : 0.34;
        return (trueVal, falseVal);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Returns one of two strings depending on a bool value.</summary>
public sealed class BoolToStringConverter : IValueConverter
{
    private readonly string _trueValue;
    private readonly string _falseValue;

    public BoolToStringConverter(string trueValue, string falseValue)
    {
        _trueValue = trueValue;
        _falseValue = falseValue;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? _trueValue : _falseValue;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Returns one of two colors depending on a bool value (inline-constructable variant).</summary>
public sealed class BoolToColorConverter2 : IValueConverter
{
    private readonly Color _trueColor;
    private readonly Color _falseColor;

    public BoolToColorConverter2(string trueHex, string falseHex)
    {
        _trueColor = Color.FromArgb(trueHex);
        _falseColor = Color.FromArgb(falseHex);
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? _trueColor : _falseColor;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Returns false (hidden) when a list is null or empty. Works for ToolActivity and string lists.</summary>
public sealed class ToolListVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        IReadOnlyList<RaceTelemetry.Desktop.ViewModels.ToolActivity> list => list.Count > 0,
        IReadOnlyList<string> strs => strs.Count > 0,
        System.Collections.ICollection col => col.Count > 0,
        _ => false,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Formats a session label for the launcher session chips (§2a).</summary>
public sealed class SessionLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not RaceTelemetry.Contracts.SessionSummary s)
            return string.Empty;

        var sessionType = RaceTelemetry.Desktop.ViewModels.CountryFlags.SessionTypeName(s.SessionType);
        return string.Equals(parameter as string, "TypeOnly", StringComparison.OrdinalIgnoreCase)
            ? sessionType
            : $"{sessionType} · {s.Year}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
