using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ClaudexUsage;

/// <summary>Borderless popup (shown on left-click) listing every rate-limit window for each service.</summary>
sealed class DetailsForm : Form
{
    readonly Func<IReadOnlyList<(IUsageProvider provider, UsageSnapshot? snap)>> getData;
    readonly Func<bool> showRemaining;
    readonly Action onRefresh;
    readonly bool lightTheme;

    readonly Font titleFont = new("Segoe UI", 10f, FontStyle.Bold);
    readonly Font textFont = new("Segoe UI", 9.5f);
    readonly Font smallFont = new("Segoe UI", 8.5f);

    Color Bg => lightTheme ? Color.FromArgb(0xF3, 0xF3, 0xF3) : Color.FromArgb(0x20, 0x20, 0x20);
    Color Fg => lightTheme ? Color.Black : Color.White;
    Color Muted => lightTheme ? Color.FromArgb(0x60, 0x60, 0x60) : Color.FromArgb(0xA6, 0xA6, 0xA6);
    Color Track => lightTheme ? Color.FromArgb(0xD8, 0xD8, 0xD8) : Color.FromArgb(0x3A, 0x3A, 0x3A);
    Color Border => lightTheme ? Color.FromArgb(0xC8, 0xC8, 0xC8) : Color.FromArgb(0x45, 0x45, 0x45);

    readonly Func<IUsageProvider, BadgeShape> shapeFor;

    public DetailsForm(Func<IReadOnlyList<(IUsageProvider, UsageSnapshot?)>> getData, Func<bool> showRemaining, Action onRefresh, bool lightTheme, Func<IUsageProvider, BadgeShape> shapeFor)
    {
        this.getData = getData;
        this.showRemaining = showRemaining;
        this.onRefresh = onRefresh;
        this.lightTheme = lightTheme;
        this.shapeFor = shapeFor;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        KeyPreview = true;
        BackColor = Bg;
        Cursor = Cursors.Hand;
    }

    // Do not steal focus when first shown in a way that makes the taskbar flash; still activates via ShowNear.
    protected override bool ShowWithoutActivation => false;

    int S(double v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public void ShowNear(Point anchor)
    {
        Width = S(330);
        Height = Measure();
        var wa = Screen.FromPoint(anchor).WorkingArea;
        int x = Math.Clamp(anchor.X - Width / 2, wa.Left + S(8), Math.Max(wa.Left + S(8), wa.Right - Width - S(8)));
        int y = anchor.Y < wa.Top + wa.Height / 2 ? wa.Top + S(8) : wa.Bottom - Height - S(8);
        Location = new Point(x, y);
        Invalidate();
        Show();
        Activate();
    }

    public void RefreshContents()
    {
        if (!Visible) return;
        Height = Measure();
        Invalidate();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Hide();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Hide();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left) onRefresh();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }

    int Measure()
    {
        int h = S(14);
        foreach (var (_, snap) in getData())
        {
            h += S(26);
            if (snap is null) h += S(22);
            else if (snap.Error is not null) h += S(22);
            else h += snap.Windows.Count * S(36);
            h += S(10);
        }
        h += S(20) + S(10);
        return h;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Bg);
        using (var pen = new Pen(Border))
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

        int pad = S(14);
        int y = pad;
        int w = Width - pad * 2;
        bool remaining = showRemaining();
        DateTimeOffset? newest = null;

        using var fgBrush = new SolidBrush(Fg);
        using var mutedBrush = new SolidBrush(Muted);
        using var trackBrush = new SolidBrush(Track);
        var right = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        var left = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };

        foreach (var (provider, snap) in getData())
        {
            // Title row: the service's badge silhouette + name, plan on the right.
            using (var marker = new SolidBrush(Palette.BadgeFill(provider.Name, lightTheme)))
            using (var path = IconRenderer.ShapePath(MarkerShape(provider), new RectangleF(pad, y + S(6), S(14), S(14))))
                g.FillPath(marker, path);
            g.DrawString(provider.Name, titleFont, fgBrush, new RectangleF(pad + S(20), y, w, S(26)), left);
            if (snap?.Plan is string plan)
                g.DrawString(plan, smallFont, mutedBrush, new RectangleF(pad, y, w, S(26)), right);
            y += S(26);

            if (snap is null)
            {
                g.DrawString("Loading...", textFont, mutedBrush, new RectangleF(pad, y, w, S(22)), left);
                y += S(22);
            }
            else if (snap.Error is not null)
            {
                using var err = new SolidBrush(Color.FromArgb(0xE5, 0x53, 0x53));
                g.DrawString(snap.Error, textFont, err, new RectangleF(pad, y, w, S(22)), left);
                y += S(22);
            }
            else
            {
                if (newest is null || snap.FetchedAt > newest) newest = snap.FetchedAt;
                foreach (var win in snap.Windows)
                {
                    double shown = remaining ? win.RemainingPercent : win.UsedPercent;
                    string pct = $"{Math.Round(shown)}% {(remaining ? "left" : "used")}";
                    string reset = win.ResetsAt is null ? "" : $"resets in {Fmt.Countdown(win.ResetsAt)} ({Fmt.Clock(win.ResetsAt)})";

                    var line = new RectangleF(pad, y, w, S(20));
                    g.DrawString(win.Label, textFont, fgBrush, line, left);
                    g.DrawString(pct, textFont, fgBrush, line, right);
                    if (reset.Length > 0)
                    {
                        var labelW = g.MeasureString(win.Label, textFont).Width + S(8);
                        var pctW = g.MeasureString(pct, textFont).Width + S(8);
                        var mid = new RectangleF(pad + labelW, y, Math.Max(0, w - labelW - pctW), S(20));
                        g.DrawString(reset, smallFont, mutedBrush, mid, left);
                    }

                    // Bar
                    int barY = y + S(22), barH = S(6);
                    var track = new Rectangle(pad, barY, w, barH);
                    FillRounded(g, trackBrush, track, barH / 2);
                    double fillFrac = Math.Clamp(win.UsedPercent / 100.0, 0, 1);
                    int fillW = (int)Math.Round(w * fillFrac);
                    if (fillW > 0)
                    {
                        var color = win.UsedPercent >= 90 ? Color.FromArgb(0xE5, 0x53, 0x53)
                                  : win.UsedPercent >= 75 ? Color.FromArgb(0xE8, 0xA3, 0x17)
                                  : provider.Brand;
                        using var fill = new SolidBrush(color);
                        FillRounded(g, fill, new Rectangle(pad, barY, Math.Max(fillW, barH), barH), barH / 2);
                    }
                    y += S(36);
                }
            }
            y += S(10);
        }

        string footer = newest is null ? "Click to refresh" : $"Updated {Fmt.Ago(newest.Value)} · click to refresh";
        g.DrawString(footer, smallFont, mutedBrush, new RectangleF(pad, y, w, S(20)), left);
    }

    /// <summary>Marker next to the service name: the same silhouette the tray icon uses (a dot for the plain styles).</summary>
    BadgeShape MarkerShape(IUsageProvider provider)
    {
        var s = shapeFor(provider);
        return s == BadgeShape.None ? BadgeShape.Square : s;
    }

    static void FillRounded(Graphics g, Brush brush, Rectangle r, int radius)
    {
        if (radius <= 0 || r.Width <= radius * 2) { g.FillRectangle(brush, r); return; }
        using var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            titleFont.Dispose();
            textFont.Dispose();
            smallFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
