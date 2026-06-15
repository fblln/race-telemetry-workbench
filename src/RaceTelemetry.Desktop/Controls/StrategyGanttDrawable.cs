namespace RaceTelemetry.Desktop.Controls;

using Microsoft.Maui.Graphics;

/// <summary>
/// Tire-strategy gantt drawable for the Strategy view (§8.15). One row per
/// driver; stints are compound-colored bars on a shared lap axis. Data comes
/// from /strategy/summarize and lap/stint summaries; a synthetic field is drawn
/// until a session is loaded.
/// </summary>
public sealed class StrategyGanttDrawable : IDrawable
{
    public IReadOnlyList<DriverStrategy> Drivers { get; set; } = DefaultStrategies();
    public int TotalLaps { get; set; } = 53;

    private static readonly Color RowLabel = Color.FromArgb("#BCB1A2");
    private static readonly Color Border = Color.FromArgb("#2A241F");

    private static Color Compound(string c) => c switch
    {
        "SOFT" => Color.FromArgb("#FF5A5F"),
        "MEDIUM" => Color.FromArgb("#FFCE1A"),
        "HARD" => Color.FromArgb("#D9D2C6"),
        "INTER" => Color.FromArgb("#27D98C"),
        "WET" => Color.FromArgb("#3E9BF5"),
        _ => Color.FromArgb("#8A7F70"),
    };

    public void Draw(ICanvas canvas, RectF rect)
    {
        const float labelW = 92f;
        const float rowH = 30f;
        const float gap = 8f;
        var trackX = rect.X + labelW;
        var trackW = rect.Width - labelW;

        canvas.FontSize = 12;
        var y = rect.Y + 6f;

        foreach (var d in Drivers)
        {
            canvas.FontColor = RowLabel;
            canvas.DrawString(d.Code, rect.X, y + 4, labelW - 8, rowH - 8,
                HorizontalAlignment.Left, VerticalAlignment.Center);

            var lap = 0;
            foreach (var s in d.Stints)
            {
                var x = trackX + (lap / (float)TotalLaps) * trackW;
                var w = (s.Laps / (float)TotalLaps) * trackW;

                canvas.FillColor = Compound(s.Compound).WithAlpha(0.85f);
                canvas.FillRoundedRectangle(x + 1, y, Math.Max(1f, w - 2), rowH - gap, 3);
                canvas.StrokeColor = Compound(s.Compound);
                canvas.StrokeSize = 1;
                canvas.DrawRoundedRectangle(x + 1, y, Math.Max(1f, w - 2), rowH - gap, 3);

                if (w > 36)
                {
                    canvas.FontColor = Color.FromArgb("#1A1206");
                    canvas.DrawString($"{s.Compound[..1]}·{s.Laps}", x + 1, y, Math.Max(1f, w - 2), rowH - gap,
                        HorizontalAlignment.Center, VerticalAlignment.Center);
                }
                lap += s.Laps;
            }
            y += rowH;
        }
    }

    public sealed record DriverStrategy(string Code, IReadOnlyList<Stint> Stints);
    public sealed record Stint(string Compound, int Laps);

    private static IReadOnlyList<DriverStrategy> DefaultStrategies() => new[]
    {
        new DriverStrategy("VER", new[] { new Stint("MEDIUM", 24), new Stint("HARD", 29) }),
        new DriverStrategy("HAM", new[] { new Stint("MEDIUM", 28), new Stint("HARD", 25) }),
        new DriverStrategy("LEC", new[] { new Stint("MEDIUM", 20), new Stint("HARD", 33) }),
        new DriverStrategy("PIA", new[] { new Stint("MEDIUM", 33), new Stint("HARD", 20) }),
        new DriverStrategy("NOR", new[] { new Stint("SOFT", 11), new Stint("MEDIUM", 23), new Stint("HARD", 19) }),
        new DriverStrategy("RUS", new[] { new Stint("MEDIUM", 25), new Stint("HARD", 16), new Stint("SOFT", 12) }),
    };
}
