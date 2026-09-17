using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ClaudexUsage;

/// <summary>
/// Dark styling for the right-click menu: a flat charcoal panel with rounded corners (via DWM on Windows 11),
/// rounded hover highlights, plain check glyphs and generous row height. Always dark, like Windows 11's own flyouts.
/// </summary>
static class MenuTheme
{
    public static readonly Color Bg = Color.FromArgb(0x1F, 0x1F, 0x1E);
    public static readonly Color Hover = Color.FromArgb(0x32, 0x32, 0x30);
    public static readonly Color Border = Color.FromArgb(0x3D, 0x3D, 0x3B);
    public static readonly Color Separator = Color.FromArgb(0x36, 0x36, 0x34);
    public static readonly Color Fg = Color.FromArgb(0xF0, 0xED, 0xE6);
    public static readonly Color Muted = Color.FromArgb(0x9C, 0x98, 0x90);
    public static readonly Color Disabled = Color.FromArgb(0x6C, 0x69, 0x63);

    public static readonly Font Text = new("Segoe UI", 9.75f);
    public static readonly Font Bold = new("Segoe UI Semibold", 9.75f);
    public static readonly Font Small = new("Segoe UI", 8.5f);

    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const int DWMWA_BORDER_COLOR = 34;
    const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>True once Windows has accepted a rounded-corner request, so the renderer can skip its own square border.</summary>
    public static bool RoundedCorners { get; private set; }

    /// <summary>Apply the theme to the root menu. Submenus pick up the renderer automatically; call <see cref="Style(ToolStripDropDown)"/> on each.</summary>
    public static void Apply(ContextMenuStrip menu)
    {
        menu.Renderer = new DarkRenderer();
        Style(menu);
    }

    public static void Style(ToolStripDropDown dropDown)
    {
        dropDown.BackColor = Bg;
        dropDown.ForeColor = Fg;
        dropDown.Font = Text;
        dropDown.Padding = new Padding(4, 6, 4, 6);
        if (dropDown is ToolStripDropDownMenu m)
        {
            m.ShowImageMargin = false;
            m.ShowCheckMargin = true;
        }
        dropDown.HandleCreated += (s, _) => RoundWindow(((ToolStripDropDown)s!).Handle);
        if (dropDown.IsHandleCreated) RoundWindow(dropDown.Handle);
    }

    /// <summary>Row padding for an ordinary menu entry: taller rows make the menu feel like a flyout rather than a Win32 menu.</summary>
    public static ToolStripMenuItem Item(string text, EventHandler? onClick = null)
    {
        var item = new ToolStripMenuItem(text) { Padding = new Padding(6, 5, 10, 5) };
        if (onClick is not null) item.Click += onClick;
        return item;
    }

    static void RoundWindow(IntPtr hwnd)
    {
        try
        {
            int pref = DWMWCP_ROUND;
            if (DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)) == 0)
            {
                RoundedCorners = true;
                int colorRef = Border.R | (Border.G << 8) | (Border.B << 16);
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorRef, sizeof(int));
            }
        }
        catch { /* Windows 10: square corners, the renderer draws its own border */ }
    }

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.Clear(Bg);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            // Flat: the check column shares the panel colour.
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (RoundedCorners) return; // DWM draws the border in our colour
            using var pen = new Pen(Border);
            var r = e.AffectedBounds;
            e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(2, 0.5f, e.Item.Width - 4, e.Item.Height - 1);
            using var path = Rounded(r, 4);
            using var brush = new SolidBrush(Hover);
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Fg : Disabled;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // A plain check glyph instead of the boxed bitmap.
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            float s = Math.Min(r.Width, r.Height) * 0.55f;
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            using var pen = new Pen(e.Item.Enabled ? Fg : Disabled, 1.8f * (e.ToolStrip?.DeviceDpi ?? 96) / 96f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new[]
            {
                new PointF(cx - s / 2, cy),
                new PointF(cx - s / 8, cy + s * 0.38f),
                new PointF(cx + s / 2, cy - s * 0.38f),
            });
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            // Thin chevron for submenus.
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ArrowRectangle;
            float k = (e.Item?.Owner?.DeviceDpi ?? 96) / 96f;
            float cx = r.X + r.Width / 2f - k, cy = r.Y + r.Height / 2f, s = 3.5f * k;
            using var pen = new Pen(e.Item?.Enabled != false ? Muted : Disabled, 1.5f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new[] { new PointF(cx - s / 2, cy - s), new PointF(cx + s / 2, cy), new PointF(cx - s / 2, cy + s) });
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Separator);
            int y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }
    }

    sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Bg;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color SeparatorDark => Separator;
        public override Color SeparatorLight => Separator;
        public override Color CheckBackground => Color.Transparent;
        public override Color CheckSelectedBackground => Color.Transparent;
        public override Color CheckPressedBackground => Color.Transparent;
    }
}

/// <summary>
/// Read-only row at the top of the menu for one service: the live tray icon at 32 px, the service name and plan,
/// every window's percentage, and the next reset. Not clickable, but drawn in full colour rather than greyed.
/// </summary>
sealed class UsageHeaderItem : ToolStripItem
{
    readonly IUsageProvider provider;
    readonly UsageSnapshot? snap;
    readonly bool showRemaining;

    public UsageHeaderItem(IUsageProvider provider, UsageSnapshot? snap, bool showRemaining)
    {
        this.provider = provider;
        this.snap = snap;
        this.showRemaining = showRemaining;
        Enabled = false;
        AutoSize = true;
        Margin = new Padding(0, 1, 0, 1);
    }

    float Scale => (Owner?.DeviceDpi ?? 96) / 96f;
    int S(float px) => (int)Math.Round(px * Scale);

    public override Size GetPreferredSize(Size constrainingSize) => new(S(312), S(56));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var bg = new SolidBrush(MenuTheme.Bg)) g.FillRectangle(bg, new Rectangle(Point.Empty, Size));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        int pad = S(10), iconPx = S(32);
        int iconY = (Height - iconPx) / 2;
        var (rows, fill, fraction) = TrayApp.RingIcon(provider.Name, snap, showRemaining, light: false);
        using (var bmp = IconRenderer.RenderBitmap(iconPx, rows, BadgeShape.Ring, fill, fraction))
            g.DrawImageUnscaled(bmp, pad, iconY);

        int textX = pad + iconPx + S(12);
        int right = Width - S(12);
        using var fgBrush = new SolidBrush(MenuTheme.Fg);
        using var mutedBrush = new SolidBrush(MenuTheme.Muted);
        var line1 = new Rectangle(textX, S(9), right - textX, S(20));
        var line2 = new Rectangle(textX, S(29), right - textX, S(18));

        // Line 1: name, plan, and the reset countdown on the right.
        string title = provider.Name;
        var titleSize = TextRenderer.MeasureText(g, title, MenuTheme.Bold, line1.Size, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, title, MenuTheme.Bold, line1, MenuTheme.Fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        if (snap?.Plan is string plan)
        {
            var planRect = new Rectangle(line1.X + titleSize.Width + S(6), line1.Y, line1.Width - titleSize.Width - S(6), line1.Height);
            TextRenderer.DrawText(g, plan, MenuTheme.Small, planRect, MenuTheme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
        if (snap?.Primary is UsageWindow primary && primary.ResetsAt is DateTimeOffset at)
        {
            string reset = $"{primary.Label} resets in {Fmt.Countdown(at)}";
            TextRenderer.DrawText(g, reset, MenuTheme.Small, line1, MenuTheme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // Line 2: every window, or the loading / error state.
        string detail; Color detailColor = MenuTheme.Muted;
        if (snap is null) detail = "Loading…";
        else if (snap.Error is not null) { detail = snap.Error; detailColor = Palette.Critical(false); }
        else if (snap.Windows.Count == 0) { detail = "No usage windows reported"; }
        else
        {
            string suffix = showRemaining ? "left" : "used";
            detail = string.Join("   ·   ", snap.Windows.Take(3)
                .Select(w => $"{w.Label} {Math.Round(showRemaining ? w.RemainingPercent : w.UsedPercent)}% {suffix}"));
        }
        TextRenderer.DrawText(g, detail, MenuTheme.Small, line2, detailColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}
