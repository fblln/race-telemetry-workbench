namespace RaceTelemetry.Desktop.Controls;

/// <summary>
/// A block-cursor label (▋) that pulses its opacity when visible
/// and stops the animation when hidden.
/// </summary>
public sealed class BlinkingCursor : Label
{
    public BlinkingCursor()
    {
        Text = "▋";
        FontFamily = "JetBrainsMono";
        FontSize = 14;
        TextColor = Color.FromArgb("#FFA60D");
        HorizontalOptions = LayoutOptions.Start;
    }

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);

        if (propertyName == nameof(IsVisible))
        {
            if (IsVisible)
            {
                this.Animate("blink",
                    new Animation(v => Opacity = v, 1, 0),
                    length: 600,
                    easing: Easing.Linear,
                    repeat: () => IsVisible);
            }
            else
            {
                this.AbortAnimation("blink");
                Opacity = 1;
            }
        }
    }
}
