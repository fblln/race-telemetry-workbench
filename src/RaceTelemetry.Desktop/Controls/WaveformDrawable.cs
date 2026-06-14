namespace RaceTelemetry.Desktop.Controls;

using Microsoft.Maui.Graphics;

/// <summary>
/// Viewport-aware waveform drawable (§8.5 Waveform panel). Renders the selected
/// telemetry channels with Carbon Signal channel colors, gridlines, lap
/// boundaries, and the amber replay cursor. Sample series are supplied by the
/// view model from /replay/chunk; this scaffold draws synthetic traces until a
/// session is loaded so the panel is never empty.
/// </summary>
public sealed class WaveformDrawable : IDrawable
{
    public IReadOnlyList<ChannelSeries> Channels { get; set; } = DefaultSeries();
    public double CursorFraction { get; set; } = 0.41;   // 0..1 across the window
    public double ReferenceFraction { get; set; } = 0.30;
    public IReadOnlyList<double> LapBoundaries { get; set; } = new[] { 0.18, 0.52, 0.86 };

    private static readonly Color Grid = Color.FromArgb("#2A2620");
    private static readonly Color GridMajor = Color.FromArgb("#3A342C");
    private static readonly Color Cursor = Color.FromArgb("#FFA60D");
    private static readonly Color CursorRef = Color.FromArgb("#7FA6C9");

    public void Draw(ICanvas canvas, RectF rect)
    {
        // horizontal gridlines
        canvas.StrokeColor = Grid;
        canvas.StrokeSize = 1;
        for (var g = 1; g < 5; g++)
        {
            var y = rect.Y + g * rect.Height / 5f;
            canvas.DrawLine(rect.X, y, rect.Right, y);
        }

        // lap boundaries
        canvas.StrokeColor = GridMajor;
        foreach (var b in LapBoundaries)
        {
            var x = rect.X + (float)b * rect.Width;
            canvas.DrawLine(x, rect.Y, x, rect.Bottom);
        }

        // channel traces
        foreach (var ch in Channels)
        {
            if (ch.Samples.Count < 2) continue;
            canvas.StrokeColor = Color.FromArgb(ch.HexColor);
            canvas.StrokeSize = 1.6f;
            canvas.StrokeLineJoin = LineJoin.Round;

            var path = new PathF();
            for (var i = 0; i < ch.Samples.Count; i++)
            {
                var x = rect.X + (i / (float)(ch.Samples.Count - 1)) * rect.Width;
                var y = rect.Bottom - (float)ch.Samples[i] * rect.Height;
                if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
            }
            canvas.DrawPath(path);
        }

        // reference cursor (cool, dashed)
        var rx = rect.X + (float)ReferenceFraction * rect.Width;
        canvas.StrokeColor = CursorRef;
        canvas.StrokeSize = 1;
        canvas.StrokeDashPattern = new float[] { 3, 3 };
        canvas.DrawLine(rx, rect.Y, rx, rect.Bottom);
        canvas.StrokeDashPattern = null;

        // primary cursor (amber)
        var cx = rect.X + (float)CursorFraction * rect.Width;
        canvas.StrokeColor = Cursor;
        canvas.StrokeSize = 1.5f;
        canvas.DrawLine(cx, rect.Y, cx, rect.Bottom);
    }

    public sealed record ChannelSeries(string Name, string HexColor, IReadOnlyList<double> Samples);

    private static IReadOnlyList<ChannelSeries> DefaultSeries()
    {
        var speed = new double[120];
        var brake = new double[120];
        for (var i = 0; i < 120; i++)
        {
            var ph = i / 120.0 * Math.PI * 6;
            speed[i] = 0.20 + 0.62 * Math.Abs(Math.Sin(ph));
            brake[i] = Math.Sin(ph) < -0.3 ? 0.62 : 0.05;
        }
        return new[]
        {
            new ChannelSeries("speed", "#22A7FF", speed),
            new ChannelSeries("brake", "#FF7A22", brake),
        };
    }
}
