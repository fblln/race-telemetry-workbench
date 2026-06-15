namespace RaceTelemetry.Desktop.Views;

/// <summary>
/// Generic placeholder for views not yet implemented (Overview, Strategy,
/// Head to head, Telemetry). Keeps the console navigable while those panels
/// are built out (§8.3).
/// </summary>
public sealed class PlaceholderView : ContentView
{
    public PlaceholderView(string title)
    {
        Padding = 16;

        var heading = new Label
        {
            Text = title,
            FontFamily = "InterSemiBold",
            FontSize = 17,
        };
        heading.SetAppThemeColor(Label.TextColorProperty,
            (Color)(Application.Current?.Resources["TextPrimary"] ?? Colors.White),
            (Color)(Application.Current?.Resources["TextPrimary"] ?? Colors.White));

        var note = new Label
        {
            Text = $"“{title}” view — scaffold placeholder. This panel is defined in the spec (§8.3) and will be built on the same Query API contracts.",
            FontSize = 13,
            TextColor = (Color)(Application.Current?.Resources["TextTertiary"] ?? Colors.Gray),
        };

        Content = new VerticalStackLayout
        {
            Spacing = 8,
            Children = { heading, note },
        };
    }
}
