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

/// <summary>Formats a session as "Race · 2025" for the launcher session chips (§2a).</summary>
public sealed class SessionLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is RaceTelemetry.Contracts.SessionSummary s
            ? $"{RaceTelemetry.Desktop.ViewModels.CountryFlags.SessionTypeName(s.SessionType)} · {s.Year}"
            : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
