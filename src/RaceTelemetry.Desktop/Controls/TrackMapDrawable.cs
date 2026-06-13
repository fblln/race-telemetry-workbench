namespace RaceTelemetry.Desktop.Controls;

using Microsoft.Maui.Graphics;

/// <summary>
/// Track map drawable (§7.3, §8.14). The outline is the data-derived Autodromo
/// Nazionale Monza lap (from imported position samples), normalized to a 520x300
/// design space and scaled to the canvas — never an external track asset.
/// Replace the outline at runtime with the session's own outline from
/// /replay/metadata once a session is open.
/// </summary>
public sealed class TrackMapDrawable : IDrawable
{
    // Monza outline in 520x300 design units (x0,y0, x1,y1, ...).
    private static readonly float[] Outline =
    {
        382.8f, 249.4f, 383.5f, 252.1f, 381.8f, 254.7f, 376.9f, 257.3f, 368.9f, 257.3f, 360.6f, 257.3f, 352.3f, 257.3f, 344.0f, 257.4f, 335.6f, 257.6f, 327.3f, 258.0f, 319.1f, 258.5f, 310.9f, 259.1f, 302.8f, 259.8f, 294.7f, 260.3f, 286.7f, 260.8f, 278.5f, 261.3f, 270.4f, 261.7f, 262.3f, 262.1f, 254.1f, 262.5f, 245.7f, 263.0f, 237.2f, 263.3f, 228.7f, 263.5f, 220.7f, 263.3f, 213.0f, 262.1f, 205.8f, 260.1f, 198.7f, 258.3f, 191.4f, 258.3f, 184.0f, 259.9f, 176.7f, 261.9f, 169.1f, 263.6f, 161.0f, 264.6f, 152.2f, 265.2f, 143.3f, 265.2f, 135.1f, 264.6f, 127.6f, 263.2f, 119.8f, 260.8f, 112.3f, 257.7f, 105.3f, 253.6f, 99.2f, 248.7f, 93.3f, 242.5f, 88.1f, 235.9f, 83.5f, 228.2f, 80.0f, 221.0f, 77.1f, 212.7f, 75.0f, 205.3f, 73.0f, 197.2f, 71.4f, 189.6f, 69.8f, 181.3f, 68.4f, 173.1f, 67.0f, 164.7f, 65.7f, 156.6f, 64.5f, 148.5f, 63.1f, 140.7f, 60.7f, 133.2f, 57.1f, 126.4f, 52.7f, 119.8f, 48.5f, 113.2f, 45.0f, 106.2f, 41.9f, 98.7f, 38.6f, 91.1f, 34.9f, 83.2f, 31.4f, 75.3f, 28.7f, 67.3f, 28.1f, 59.7f, 30.2f, 52.6f, 35.0f, 47.0f, 41.5f, 43.0f, 48.6f, 40.6f, 56.1f, 38.9f, 63.8f, 37.5f, 72.0f, 36.1f, 80.2f, 35.7f, 87.8f, 37.0f, 94.6f, 40.7f, 100.1f, 46.0f, 104.9f, 52.5f, 109.3f, 59.0f, 113.9f, 65.7f, 118.6f, 72.4f, 123.6f, 79.5f, 128.8f, 86.6f, 133.9f, 93.4f, 139.2f, 99.9f, 144.5f, 105.8f, 149.9f, 111.4f, 155.5f, 116.8f, 161.5f, 122.5f, 167.7f, 128.2f, 174.0f, 134.2f, 180.3f, 140.0f, 186.5f, 145.8f, 192.7f, 151.6f, 198.6f, 157.1f, 204.2f, 162.3f, 209.9f, 167.6f, 215.9f, 173.2f, 222.3f, 179.2f, 228.9f, 184.8f, 235.8f, 189.4f, 243.1f, 192.3f, 250.9f, 193.8f, 258.7f, 195.2f, 266.1f, 197.4f, 273.5f, 200.4f, 281.3f, 203.3f, 289.6f, 205.1f, 298.0f, 205.9f, 306.1f, 206.0f, 314.0f, 205.9f, 322.0f, 205.8f, 330.0f, 205.7f, 338.3f, 205.6f, 346.8f, 205.5f, 355.3f, 205.4f, 363.3f, 205.3f, 371.0f, 205.2f, 378.7f, 205.1f, 387.0f, 205.0f, 395.9f, 204.9f, 404.8f, 204.8f, 413.0f, 204.7f, 420.8f, 204.6f, 428.7f, 204.5f, 437.1f, 204.3f, 445.5f, 204.3f, 454.0f, 204.2f, 462.5f, 204.4f, 470.6f, 205.0f, 478.2f, 206.3f, 484.4f, 209.3f, 489.1f, 214.3f, 491.4f, 221.3f, 490.7f, 229.1f, 487.3f, 236.8f, 481.6f, 243.3f, 475.2f, 248.2f, 468.2f, 251.7f, 460.9f, 254.2f, 452.6f, 256.0f, 444.2f, 257.3f, 435.6f, 258.3f, 427.9f, 258.6f, 420.5f, 258.5f, 413.4f, 257.9f, 405.9f, 257.5f, 398.2f, 257.3f, 390.3f, 254.7f, 385.1f, 252.1f, 382.8f, 249.4f
    };

    private const float DesignW = 520f;
    private const float DesignH = 300f;

    public Color OutlineColor { get; set; } = Color.FromArgb("#524537");
    public Color KerbColor { get; set; } = Color.FromArgb("#FFA60D");

    public void Draw(ICanvas canvas, RectF rect)
    {
        var scale = Math.Min(rect.Width / DesignW, rect.Height / DesignH);
        var offX = rect.X + (rect.Width - DesignW * scale) / 2f;
        var offY = rect.Y + (rect.Height - DesignH * scale) / 2f;

        var path = new PathF();
        path.MoveTo(offX + Outline[0] * scale, offY + Outline[1] * scale);
        for (var i = 2; i < Outline.Length; i += 2)
            path.LineTo(offX + Outline[i] * scale, offY + Outline[i + 1] * scale);
        path.Close();

        canvas.StrokeColor = OutlineColor;
        canvas.StrokeSize = 8f * scale;
        canvas.StrokeLineJoin = LineJoin.Round;
        canvas.StrokeLineCap = LineCap.Round;
        canvas.DrawPath(path);

        canvas.StrokeColor = KerbColor.WithAlpha(0.45f);
        canvas.StrokeSize = 1.3f * scale;
        canvas.DrawPath(path);
    }
}
