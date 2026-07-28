using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace TaskbarMonitor.Rendering;

/// <summary>
/// Draws the strip onto a per-pixel-alpha surface, and measures it with the same engine and hints
/// so the measured size and the drawn glyphs always agree — a mismatch would show up as horizontal
/// jitter, which the fixed-width formatter contract exists to prevent.
/// </summary>
public static class StripRenderer
{
    /// <summary>
    /// Fully transparent pixels are hit-transparent on a layered window, which would leave the
    /// right-click menu reachable only on the glyphs themselves. Alpha 1 is imperceptible
    /// (0.4% coverage) and keeps the whole strip clickable.
    /// </summary>
    public const int MinBackgroundAlpha = 1;

    /// <summary>
    /// GenericTypographic: the default StringFormat adds side bearings that vary with the string,
    /// which would inflate the measurement and shift the draw origin. Near/Near because the origin
    /// is computed once from a cached measurement — Center would re-measure on every DrawString.
    /// </summary>
    private static StringFormat CreateFormat() => new(StringFormat.GenericTypographic)
    {
        Alignment = StringAlignment.Near,
        LineAlignment = StringAlignment.Near,
        FormatFlags = StringFormatFlags.NoClip | StringFormatFlags.NoWrap
                    | StringFormatFlags.MeasureTrailingSpaces,
        Trimming = StringTrimming.None,
    };

    /// <summary>
    /// Grayscale AA with grid fitting. ClearType must never be used here: GDI-style subpixel AA
    /// fringes and writes opaque glyph backgrounds on an alpha surface.
    /// </summary>
    public static void ConfigureQuality(Graphics g)
    {
        g.PageUnit = GraphicsUnit.Pixel;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // Not HighQuality: gamma-corrected blending rounds the alpha-1 hit-test wash away to 0
        g.CompositingQuality = CompositingQuality.Default;
    }

    /// <summary>Measured once per (font, DPI, metric config); the result drives both ContentSize and the draw origin.</summary>
    public static SizeF MeasureLine(Graphics g, string line, Font font)
    {
        ConfigureQuality(g);
        using var format = CreateFormat();
        return g.MeasureString(line, font, int.MaxValue, format);
    }

    /// <summary>
    /// <paramref name="textSize"/> is the cached measurement of the widest sample, not of
    /// <paramref name="line"/> — centering off a constant keeps the text pinned across refreshes.
    /// </summary>
    public static void Draw(Graphics g, string line, Font font, Size bounds, SizeF textSize,
        StripPalette palette, bool shadow)
    {
        ConfigureQuality(g);

        int alpha = Math.Max(MinBackgroundAlpha, (int)palette.Background.A);
        // At the hit-test floor use pure black: RGB must not exceed alpha on a premultiplied
        // surface, and ARGB(1,0,0,0) is valid by construction and genuinely invisible.
        var wash = alpha <= MinBackgroundAlpha
            ? Color.FromArgb(alpha, 0, 0, 0)
            : Color.FromArgb(alpha, palette.Background);

        // Clear under SourceCopy writes the wash bytes exactly — it both erases the previous
        // frame's glyphs and lays the hit-test floor. A SourceOver fill would blend the alpha-1
        // wash back down to 0 and the strip would stop receiving right-clicks.
        g.CompositingMode = CompositingMode.SourceCopy;
        g.Clear(wash);
        g.CompositingMode = CompositingMode.SourceOver;

        if (string.IsNullOrEmpty(line)) return;

        using var format = CreateFormat();
        float x = (bounds.Width - textSize.Width) / 2f;
        float y = (bounds.Height - textSize.Height) / 2f;

        if (shadow)
        {
            using var shadowBrush = new SolidBrush(palette.Shadow);
            g.DrawString(line, font, shadowBrush, x + 1, y + 1, format);
        }

        using var textBrush = new SolidBrush(palette.Text);
        g.DrawString(line, font, textBrush, x, y, format);
    }
}
