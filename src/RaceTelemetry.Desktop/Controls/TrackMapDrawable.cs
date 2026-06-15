namespace RaceTelemetry.Desktop.Controls;

using Microsoft.Maui.Graphics;
using RaceTelemetry.Desktop.Services;

/// <summary>
/// Track map drawable (§7.3, §8.14). The outline is data-derived from imported
/// position samples for the open session — never an external track asset. Incident
/// heat dots (hard-braking load) and driver markers are overlaid in the same
/// coordinate space. When no outline has loaded yet a neutral placeholder is drawn
/// so the panel is never blank.
/// </summary>
public sealed class TrackMapDrawable : IDrawable
{
    /// <summary>Data-derived outline in source position-sample coordinates.</summary>
    public IReadOnlyList<TrackPoint> Outline { get; set; } = Array.Empty<TrackPoint>();

    /// <summary>Hard-braking heat dots in the same coordinate space as the outline.</summary>
    public IReadOnlyList<IncidentDot> Incidents { get; set; } = Array.Empty<IncidentDot>();

    /// <summary>Live driver markers (replay), in outline coordinate space.</summary>
    public IReadOnlyList<DriverMarker> Drivers { get; set; } = Array.Empty<DriverMarker>();

    public Color OutlineColor { get; set; } = Color.FromArgb("#524537");
    public Color KerbColor { get; set; } = Color.FromArgb("#FFA60D");

    private static readonly Color Heat = Color.FromArgb("#FFA60D");

    public void Draw(ICanvas canvas, RectF rect)
    {
        if (Outline.Count < 2)
        {
            DrawPlaceholder(canvas, rect);
            return;
        }

        // Fit the outline to the canvas, preserving aspect, with a small margin and
        // a Y flip (track Y points up, screen Y points down).
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        foreach (var p in Outline)
        {
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }

        var spanX = Math.Max(1, maxX - minX);
        var spanY = Math.Max(1, maxY - minY);
        const float margin = 0.10f;
        var scale = (float)Math.Min(rect.Width * (1 - margin) / spanX, rect.Height * (1 - margin) / spanY);
        var midX = (minX + maxX) / 2;
        var midY = (minY + maxY) / 2;
        var cx = rect.X + rect.Width / 2f;
        var cy = rect.Y + rect.Height / 2f;

        PointF Map(double x, double y) => new(
            cx + (float)((x - midX) * scale),
            cy - (float)((y - midY) * scale));

        var path = new PathF();
        var first = Map(Outline[0].X, Outline[0].Y);
        path.MoveTo(first.X, first.Y);
        for (var i = 1; i < Outline.Count; i++)
        {
            var pt = Map(Outline[i].X, Outline[i].Y);
            path.LineTo(pt.X, pt.Y);
        }

        canvas.StrokeLineJoin = LineJoin.Round;
        canvas.StrokeLineCap = LineCap.Round;
        canvas.StrokeColor = OutlineColor;
        canvas.StrokeSize = 6f;
        canvas.DrawPath(path);
        canvas.StrokeColor = KerbColor.WithAlpha(0.35f);
        canvas.StrokeSize = 1.2f;
        canvas.DrawPath(path);

        // Hard-braking heat dots — amber, sized and opacity-scaled by intensity.
        foreach (var dot in Incidents)
        {
            var p = Map(dot.X, dot.Y);
            var intensity = (float)Math.Clamp(dot.Intensity, 0, 1);
            var radius = 3f + intensity * 9f;
            canvas.FillColor = Heat.WithAlpha(dot.IsSelected ? 0.95f : 0.30f + intensity * 0.45f);
            canvas.FillCircle(p.X, p.Y, radius);
            if (dot.IsSelected)
            {
                canvas.StrokeColor = Heat;
                canvas.StrokeSize = 2f;
                canvas.DrawCircle(p.X, p.Y, radius + 3f);
            }
        }

        // Live driver markers (replay) with a canvas-colored halo + amber cursor ring.
        foreach (var marker in Drivers)
        {
            var p = Map(marker.X, marker.Y);
            canvas.FillColor = Color.FromArgb("#14110E");
            canvas.FillCircle(p.X, p.Y, 7f);
            canvas.FillColor = Color.FromArgb(marker.HexColor);
            canvas.FillCircle(p.X, p.Y, 5f);
            canvas.StrokeColor = KerbColor;
            canvas.StrokeSize = 2f;
            canvas.DrawCircle(p.X, p.Y, 8f);
        }
    }

    private static void DrawPlaceholder(ICanvas canvas, RectF rect)
    {
        canvas.FontColor = Color.FromArgb("#5C544A");
        canvas.FontSize = 12;
        canvas.DrawString("Track outline loads from imported position samples…",
            rect.X, rect.Y, rect.Width, rect.Height,
            HorizontalAlignment.Center, VerticalAlignment.Center);
    }

    public readonly record struct IncidentDot(double X, double Y, double Intensity, bool IsSelected);

    public readonly record struct DriverMarker(string Code, string HexColor, double X, double Y);
}
