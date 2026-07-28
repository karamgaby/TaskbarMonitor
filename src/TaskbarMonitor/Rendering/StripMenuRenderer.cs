using System.Drawing.Drawing2D;

namespace TaskbarMonitor.Rendering;

/// <summary>
/// Dark/light renderer for the strip's context menu.
///
/// The default <see cref="ToolStripProfessionalRenderer"/> paints light-grey classic chrome with
/// square corners regardless of the Windows theme, which lands badly next to a strip that goes to
/// some trouble to look native on dark acrylic — and this menu is the app's only UI, so it is the
/// most-seen surface in the product. .NET 8 has no <c>Application.SetColorMode</c> (that is .NET 9),
/// so the colours are supplied here.
///
/// Theme is read per menu open rather than cached: <see cref="ThemeWatcher.SystemUsesLightTheme"/> is
/// a cheap registry read and the user can flip the system theme while the app runs.
/// </summary>
internal sealed class StripMenuRenderer : ToolStripProfessionalRenderer
{
    private const int CornerRadius = 8;

    public StripMenuRenderer() : base(new Colors()) => RoundedEdges = false;

    private static bool Light => ThemeWatcher.SystemUsesLightTheme();

    private static Color Surface => Light ? Color.FromArgb(0xF9, 0xF9, 0xF9) : Color.FromArgb(0x2C, 0x2C, 0x2C);
    private static Color BorderLine => Light ? Color.FromArgb(0xE5, 0xE5, 0xE5) : Color.FromArgb(0x45, 0x45, 0x45);
    private static Color Hover => Light ? Color.FromArgb(0xEA, 0xEA, 0xEA) : Color.FromArgb(0x3D, 0x3D, 0x3D);
    private static Color Ink => Light ? Color.FromArgb(0x1A, 0x1A, 0x1A) : Color.FromArgb(0xEC, 0xEC, 0xEC);
    private static Color InkDisabled => Light ? Color.FromArgb(0x9A, 0x9A, 0x9A) : Color.FromArgb(0x86, 0x86, 0x86);
    private static Color Rule => Light ? Color.FromArgb(0xE0, 0xE0, 0xE0) : Color.FromArgb(0x40, 0x40, 0x40);

    /// <summary>Win11 menus are rounded; a square popup reads as a legacy control.</summary>
    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d - 1, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d - 1, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(Point.Empty, e.ToolStrip.Size);
        using var path = RoundedRect(r, CornerRadius);
        using var brush = new SolidBrush(Surface);
        g.FillPath(brush, path);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(Point.Empty, e.ToolStrip.Size);
        r.Width -= 1; r.Height -= 1;
        using var path = RoundedRect(r, CornerRadius);
        using var pen = new Pen(BorderLine);
        g.DrawPath(pen, path);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // Inset so the highlight reads as a pill inside the popup, the way Win11 draws it
        var r = new Rectangle(3, 0, e.Item.Width - 7, e.Item.Height - 1);
        using var path = RoundedRect(r, 4);
        using var brush = new SolidBrush(Hover);
        g.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Ink : InkDisabled;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using var pen = new Pen(Rule);
        e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        // ToolStripArrowRenderEventArgs.Item is nullable; the base renderer copes, so mirror that
        // rather than assuming a submenu arrow always belongs to an item.
        e.ArrowColor = e.Item?.Enabled != false ? Ink : InkDisabled;
        base.OnRenderArrow(e);
    }

    /// <summary>
    /// Only the pieces ProfessionalRenderer paints without going through an override above — chiefly
    /// the submenu drop-down's own background and the (disabled) image margin.
    /// </summary>
    private sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuBorder => BorderLine;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Surface;
        public override Color MenuItemPressedGradientMiddle => Surface;
        public override Color MenuItemPressedGradientEnd => Surface;
        public override Color SeparatorDark => Rule;
        public override Color SeparatorLight => Rule;
    }
}
