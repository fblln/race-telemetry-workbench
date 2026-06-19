namespace RaceTelemetry.Desktop.Views;

public sealed class PlaceholderView : ContentView
{
    public PlaceholderView()
    {
        BackgroundColor = Application.Current?.Resources?.TryGetValue("BgCanvas", out var bg) is true
            ? (Color)bg : Colors.Transparent;

        Content = new VerticalStackLayout
        {
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Spacing = 8,
            Children =
            {
                new Label
                {
                    Text = "Coming soon",
                    HorizontalTextAlignment = TextAlignment.Center,
                    FontSize = 20,
                    TextColor = Color.FromArgb("#F4EEE6"),
                },
                new Label
                {
                    Text = "This view is not available yet.",
                    HorizontalTextAlignment = TextAlignment.Center,
                    FontSize = 12,
                    TextColor = Color.FromArgb("#5C544A"),
                },
            },
        };
    }
}
