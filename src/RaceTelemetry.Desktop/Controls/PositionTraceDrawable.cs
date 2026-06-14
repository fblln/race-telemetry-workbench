namespace RaceTelemetry.Desktop.Controls;

using Microsoft.Maui.Graphics;

/// <summary>
/// Position-trace ("race trace") drawable for the Lap Analysis view (§8.15).
/// One line per driver in the Carbon Signal categorical palette over a P1..Pn
/// vertical axis; crossings read as overtakes and pit cycles. Lines come from
/// /positions (§6.12); a synthetic field is drawn until a session is loaded.
/// </summary>
public sealed class PositionTraceDrawable : IDrawable
{
    public IReadOnlyList<DriverLine> Drivers { get; set; } = DefaultField();
    public int FieldSize { get; set; } = 20;

    private static readonly Color Grid = Color.FromArgb("#2A2620");

    public void Draw(ICanvas canvas, RectF rect)
    {
        var laps = Drivers.Count == 0 ? 1 : Drivers[0].Positions.Count;

        canvas.StrokeColor = Grid;
        canvas.StrokeSize = 1;
        for (var l = 1; l < laps; l += Math.Max(1, laps / 6))
        {
            var x = rect.X + (l / (float)laps) * rect.Width;
            canvas.DrawLine(x, rect.Y, x, rect.Bottom);
        }

        foreach (var d in Drivers)
        {
            if (d.Positions.Count < 2) continue;
            canvas.StrokeColor = Color.FromArgb(d.HexColor);
            canvas.StrokeSize = 2;
            canvas.StrokeLineJoin = LineJoin.Round;
            canvas.StrokeLineCap = LineCap.Round;

            var path = new PathF();
            for (var i = 0; i < d.Positions.Count; i++)
            {
                var x = rect.X + (i / (float)(d.Positions.Count - 1)) * rect.Width;
                var y = rect.Y + ((d.Positions[i] - 1) / (float)(FieldSize - 1)) * rect.Height;
                if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
            }
            canvas.DrawPath(path);
        }
    }

    public sealed record DriverLine(string Code, string HexColor, IReadOnlyList<int> Positions);

    private static IReadOnlyList<DriverLine> DefaultField() => new[]
    {
        new DriverLine("VER", "#E86BE0", new[] { 4, 3, 2, 2, 1, 1, 1 }),
        new DriverLine("HAM", "#FF931A", new[] { 2, 2, 3, 3, 2, 2, 2 }),
        new DriverLine("LEC", "#22A7FF", new[] { 1, 1, 1, 2, 3, 4, 3 }),
        new DriverLine("PIA", "#FFCE1A", new[] { 8, 6, 5, 4, 5, 5, 5 }),
        new DriverLine("NOR", "#15D981", new[] { 12, 11, 9, 7, 6, 6, 6 }),
        new DriverLine("RUS", "#1FE0CE", new[] { 15, 14, 12, 11, 9, 9, 9 }),
    };
}
