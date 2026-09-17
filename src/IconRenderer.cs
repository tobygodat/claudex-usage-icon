using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace ClaudexUsage;

/// <summary>One line of text inside the tray icon. A non-null Background draws a filled pill behind it (alerts).</summary>
sealed record IconRow(string Text, Color Color, Color? Background = null);

/// <summary>Badge silhouette behind the number. Each service owns one, so the shape alone identifies it.</summary>
enum BadgeShape
{
    None,
    /// <summary>Solid tile filling the whole slot, with slightly softened corners.</summary>
    Square,
    /// <summary>12-ray burst, after the Claude sparkle.</summary>
    Starburst,
    /// <summary>Pointy-top hexagon, after the OpenAI hexagonal knot.</summary>
    Hexagon,
    /// <summary>
    /// Hollow circle in the service colour that closes as usage fills: a faint full track plus an arc
    /// from 12 o'clock, clockwise, whose sweep is the used fraction. No fill, so the digits sit on the taskbar.
    /// </summary>
    Ring,
}

/// <summary>
/// Draws the tray icon. Layouts:
///  * badge: a filled logo-shaped silhouette with one big number inside;
///  * ring: a progress ring in the service colour around a white number;
///  * plain: one number (or two stacked rows) in the service colour with no shape.
/// </summary>
static class IconRenderer
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyIcon(IntPtr handle);

    /// <param name="ringFraction">For <see cref="BadgeShape.Ring"/>: how much of the circle to draw, 0..1.</param>
    public static Icon Render(int size, IconRow[] rows, BadgeShape shape = BadgeShape.None, Color? badgeFill = null, double ringFraction = 0)
    {
        using var bmp = RenderBitmap(size, rows, shape, badgeFill, ringFraction);
        IntPtr h = bmp.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(h);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }

    public static Bitmap RenderBitmap(int size, IconRow[] rows, BadgeShape shape = BadgeShape.None, Color? badgeFill = null, double ringFraction = 0)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        Rectangle textArea;
        if (shape == BadgeShape.Ring)
        {
            DrawRing(g, size, badgeFill ?? Color.Gray, ringFraction);
            textArea = TextRectFor(shape, size);
        }
        else if (shape != BadgeShape.None)
        {
            // Use the entire slot: Windows already pads tray icons, so no margin of our own.
            var box = new RectangleF(0f, 0f, size, size);
            using var path = ShapePath(shape, box);
            using (var fill = new SolidBrush(badgeFill ?? Color.Gray))
                g.FillPath(fill, path);
            // Hairline of darker edge, drawn inside the outline, so the badge keeps its silhouette on a light taskbar.
            using (var edge = new Pen(Color.FromArgb(60, 0, 0, 0), 1f) { Alignment = PenAlignment.Inset })
                g.DrawPath(edge, path);
            textArea = TextRectFor(shape, size);
        }
        else
        {
            textArea = new Rectangle(0, 0, size, size);
        }

        if (rows.Length == 1)
        {
            DrawRow(g, rows[0], textArea, size);
        }
        else
        {
            int gap = Math.Max(1, size / 16);
            int rowH = (textArea.Height - gap) / 2;
            DrawRow(g, rows[0], new Rectangle(textArea.X, textArea.Y, textArea.Width, rowH), size);
            DrawRow(g, rows[1], new Rectangle(textArea.X, textArea.Y + rowH + gap, textArea.Width, textArea.Height - rowH - gap), size);
        }
        return bmp;
    }

    // ---- ring -------------------------------------------------------------------------------

    /// <summary>Stroke width of the progress ring: an eighth of the icon, so 2 px at 16 px and 4 px at 32 px.</summary>
    public static float RingStroke(float size) => Math.Max(1.5f, size * 0.125f);

    /// <summary>
    /// Faint full circle (the track) with a solid arc over it. Both are full bleed: the outer edge of the
    /// stroke touches the icon slot. The arc starts at 12 o'clock and runs clockwise for <paramref name="fraction"/> of a turn.
    /// </summary>
    public static void DrawRing(Graphics g, float size, Color color, double fraction)
    {
        float sw = RingStroke(size);
        var box = new RectangleF(sw / 2f, sw / 2f, size - sw, size - sw);
        using (var track = new Pen(Color.FromArgb(72, color), sw))
            g.DrawEllipse(track, box);
        float sweep = (float)(360.0 * Math.Clamp(fraction, 0, 1));
        if (sweep <= 0f) return;
        using var arc = new Pen(color, sw) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
        if (sweep >= 359.5f) g.DrawEllipse(arc, box);
        else g.DrawArc(arc, box, -90f, sweep);
    }

    // ---- badge geometry ---------------------------------------------------------------------

    /// <summary>Outline of a badge shape fitted inside <paramref name="box"/>. Shared with the details popup.</summary>
    public static GraphicsPath ShapePath(BadgeShape shape, RectangleF box)
    {
        var path = new GraphicsPath();
        float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
        float R = Math.Min(box.Width, box.Height) / 2f;
        switch (shape)
        {
            case BadgeShape.Square:
            {
                // Full-bleed tile. Corners are softened by ~12 % so it reads as an app tile rather than a pixel block.
                float rad = Math.Max(1f, Math.Min(box.Width, box.Height) * 0.12f);
                float d = rad * 2;
                path.AddArc(box.Left, box.Top, d, d, 180, 90);
                path.AddArc(box.Right - d, box.Top, d, d, 270, 90);
                path.AddArc(box.Right - d, box.Bottom - d, d, d, 0, 90);
                path.AddArc(box.Left, box.Bottom - d, d, d, 90, 90);
                break;
            }
            case BadgeShape.Starburst:
            {
                const int rays = 12;
                float r = R * 0.86f; // shallow rays: keeps the inner disc, and so the digits, as large as possible
                var pts = new PointF[rays * 2];
                for (int i = 0; i < pts.Length; i++)
                {
                    // Rotate so a ray sits on each diagonal; the number's corners then land on filled area.
                    double a = Math.PI / 180 * (15 + i * (180.0 / rays));
                    float rad = i % 2 == 0 ? R : r;
                    pts[i] = new PointF(cx + (float)(rad * Math.Cos(a)), cy + (float)(rad * Math.Sin(a)));
                }
                path.AddPolygon(pts);
                break;
            }
            case BadgeShape.Hexagon:
            {
                // Pointy-top hexagon stretched to fill the whole square: vertical sides span the middle 56 %,
                // which leaves a wide, tall box for the digits.
                float l = box.Left, t = box.Top, rgt = box.Right, btm = box.Bottom, hgt = box.Height;
                var pts = new[]
                {
                    new PointF(cx, t),
                    new PointF(rgt, t + hgt * 0.22f),
                    new PointF(rgt, t + hgt * 0.78f),
                    new PointF(cx, btm),
                    new PointF(l, t + hgt * 0.78f),
                    new PointF(l, t + hgt * 0.22f),
                };
                path.AddPolygon(pts);
                break;
            }
            default:
                path.AddEllipse(box);
                break;
        }
        path.CloseFigure();
        return path;
    }

    /// <summary>Largest box for two heavy digits inside each shape, as a fraction of the icon size.</summary>
    static Rectangle TextRectFor(BadgeShape shape, int size)
    {
        float w, h;
        switch (shape)
        {
            case BadgeShape.Square:
                // Nearly the whole tile: two digits end up ~0.62·size tall, width-limited.
                h = 0.74f * size; w = 0.90f * size;
                break;
            case BadgeShape.Starburst:
                // Inner disc radius is 0.43·size; two digits at 0.54·size tall reach 0.47·size at the
                // box corners, which land on the diagonal rays (filled), not in the valleys.
                h = 0.54f * size; w = 0.78f * size;
                break;
            case BadgeShape.Hexagon:
                // The stretched hexagon is full width between 22 % and 78 % of its height and only
                // narrows gently above that, so the digits can be 0.6·size tall.
                h = 0.60f * size; w = 0.88f * size;
                break;
            case BadgeShape.Ring:
                // Inner edge of the stroke is at radius 0.375·size. A 0.64 × 0.50 box has its corners at
                // radius 0.41·size, so two heavy digits only graze the ring at their rounded corners.
                h = 0.50f * size; w = 0.64f * size;
                break;
            default:
                h = 0.6f * size; w = 0.8f * size;
                break;
        }
        int wi = Math.Max(1, (int)Math.Round(w)), hi = Math.Max(1, (int)Math.Round(h));
        return new Rectangle((size - wi) / 2, (size - hi) / 2, wi, hi);
    }

    // ---- text -------------------------------------------------------------------------------

    static void DrawRow(Graphics g, IconRow row, Rectangle rect, int iconSize)
    {
        var textRect = rect;
        if (row.Background is Color bg)
        {
            using var brush = new SolidBrush(bg);
            FillRounded(g, brush, rect, Math.Max(1, rect.Height / 4));
            int inset = iconSize >= 24 ? Math.Max(1, iconSize / 16) : 0;
            textRect = Rectangle.Inflate(rect, -inset, -inset / 2);
        }
        if (row.Text is "–" or "--" or "-")
        {
            // Placeholder (no such window / not loaded yet): a short centred dash, not a stretched glyph.
            int w = Math.Max(3, rect.Width * 2 / 5);
            int h = Math.Max(2, rect.Height / 4);
            using var brush = new SolidBrush(Color.FromArgb(220, row.Color));
            FillRounded(g, brush, new Rectangle(rect.X + (rect.Width - w) / 2, rect.Y + (rect.Height - h) / 2, w, h), h / 2);
            return;
        }
        DrawFittedText(g, row.Text, textRect, row.Color);
    }

    /// <summary>Render text large, crop to its ink bounds, then scale it to fill the target rectangle.</summary>
    static void DrawFittedText(Graphics g, string text, Rectangle rect, Color color)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || string.IsNullOrEmpty(text)) return;
        const int big = 256;
        using var mask = new Bitmap(big, big, PixelFormat.Format32bppArgb);
        using (var mg = Graphics.FromImage(mask))
        {
            mg.Clear(Color.Transparent);
            mg.TextRenderingHint = TextRenderingHint.AntiAlias;
            using var font = DigitFont();
            using var brush = new SolidBrush(color);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            mg.DrawString(text, font, brush, new RectangleF(0, 0, big, big), fmt);
        }

        var bounds = InkBounds(mask);
        if (bounds.IsEmpty) return;

        float scale = Math.Min((float)rect.Width / bounds.Width, (float)rect.Height / bounds.Height);
        int w = Math.Max(1, (int)Math.Round(bounds.Width * scale));
        int h = Math.Max(1, (int)Math.Round(bounds.Height * scale));
        var dest = new Rectangle(rect.X + (rect.Width - w) / 2, rect.Y + (rect.Height - h) / 2, w, h);

        var prevInterp = g.InterpolationMode;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.DrawImage(mask, dest, bounds, GraphicsUnit.Pixel);
        // Downscaling blurs the strokes; draw once more on top to push edge alpha up so digits stay crisp.
        g.DrawImage(mask, dest, bounds, GraphicsUnit.Pixel);
        g.InterpolationMode = prevInterp;
    }

    static readonly bool HasSegoeBlack = FontFamily.Families.Any(f => f.Name == "Segoe UI Black");

    /// <summary>Heaviest Segoe weight available: thicker strokes survive downscaling to 16 px far better.</summary>
    static Font DigitFont() => HasSegoeBlack
        ? new Font("Segoe UI Black", 110, FontStyle.Regular, GraphicsUnit.Pixel)
        : new Font("Segoe UI", 110, FontStyle.Bold, GraphicsUnit.Pixel);

    static void FillRounded(Graphics g, Brush brush, Rectangle r, int radius)
    {
        if (radius <= 0 || r.Width <= radius * 2 || r.Height <= radius * 2) { g.FillRectangle(brush, r); return; }
        using var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    static Rectangle InkBounds(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var buf = new byte[data.Stride * bmp.Height];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (int y = 0; y < bmp.Height; y++)
            {
                int row = y * data.Stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    if (buf[row + x * 4 + 3] > 24)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            return maxX < 0 ? Rectangle.Empty : new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    // ---- debug ------------------------------------------------------------------------------

    /// <summary>Debug: write a sheet of sample icons (several sizes, both themes, badge and plain styles) to a PNG.</summary>
    public static void RenderTestSheet(string path)
    {
        int[] sizes = { 16, 20, 24, 32 };
        (string text, int used)[] samples = { ("7", 7), ("37", 37), ("78", 78), ("96", 96), ("100", 100), ("–", 0), ("!", -1) };
        var shapes = new[] { ("Claude", BadgeShape.Square), ("Codex", BadgeShape.Square) };
        int cell = 44, pad = 8;
        int cols = samples.Length * 4 + 2;
        int width = pad + cols * cell + pad;
        int height = pad + sizes.Length * cell * 2 + pad;
        using var sheet = new Bitmap(width, height);
        using var g = Graphics.FromImage(sheet);
        g.Clear(Color.FromArgb(32, 32, 32));
        g.FillRectangle(Brushes.WhiteSmoke, new Rectangle(0, height / 2, width, height / 2));

        for (int si = 0; si < sizes.Length; si++)
        {
            for (int themeIdx = 0; themeIdx < 2; themeIdx++)
            {
                bool light = themeIdx == 1;
                int yBase = (light ? height / 2 : 0) + pad + si * cell + (cell - sizes[si]) / 2;
                int col = 0;
                foreach (var (text, used) in samples)
                {
                    foreach (var (service, shape) in shapes)
                    {
                        Color fill; IconRow row;
                        if (used < 0) { fill = Palette.BadgeFill(service, light); row = new IconRow("!", Color.White); }
                        else
                        {
                            var (f, t) = Palette.Badge(used, service, light);
                            fill = f; row = new IconRow(text, t);
                        }
                        using var bmp = RenderBitmap(sizes[si], new[] { row }, shape, fill);
                        g.DrawImageUnscaled(bmp, pad + col * cell + (cell - sizes[si]) / 2, yBase);
                        col++;
                    }
                }
                // plain style examples
                foreach (var (service, _) in shapes)
                {
                    var brand = Palette.Brand(service, light);
                    using var bmp = RenderBitmap(sizes[si], new[] { Palette.Row("37", 37, brand, light) });
                    g.DrawImageUnscaled(bmp, pad + col * cell + (cell - sizes[si]) / 2, yBase);
                    col++;
                }
                // ring style examples: the arc closes as usage fills
                foreach (var (text, used) in samples)
                {
                    foreach (var (service, _) in shapes)
                    {
                        var row = new IconRow(text, Palette.RingText(light));
                        using var bmp = RenderBitmap(sizes[si], new[] { row }, BadgeShape.Ring,
                            Palette.BadgeFill(service, light), Math.Max(0, used) / 100.0);
                        g.DrawImageUnscaled(bmp, pad + col * cell + (cell - sizes[si]) / 2, yBase);
                        col++;
                    }
                }
            }
        }
        sheet.Save(path, ImageFormat.Png);
    }
}

/// <summary>Colours for the tray icon, chosen for contrast on a dark or light taskbar.</summary>
static class Palette
{
    /// <summary>Digit colour for the plain (no badge) style.</summary>
    public static Color Brand(string service, bool light) => (service, light) switch
    {
        ("Claude", false) => Color.FromArgb(0xF2, 0x9B, 0x76),
        ("Claude", true) => Color.FromArgb(0xC2, 0x41, 0x0C),
        ("Codex", false) => Color.FromArgb(0x33, 0xD6, 0x9C),
        ("Codex", true) => Color.FromArgb(0x0B, 0x7A, 0x5C),
        (_, false) => Color.White,
        _ => Color.Black,
    };

    /// <summary>Badge fill in the service's own colour: Claude terracotta, the classic ChatGPT green.</summary>
    public static Color BadgeFill(string service, bool light) => service switch
    {
        "Claude" => Color.FromArgb(0xD9, 0x77, 0x57),
        "Codex" => Color.FromArgb(0x10, 0xA3, 0x7F),
        _ => Color.Gray,
    };

    /// <summary>Digit colour for the ring style: white on a dark taskbar, near-black on a light one (white would vanish).</summary>
    public static Color RingText(bool light) => light ? Color.FromArgb(0x1F, 0x1F, 0x1E) : Color.White;

    public static Color Critical(bool light) => light ? Color.FromArgb(0xD1, 0x24, 0x2B) : Color.FromArgb(0xE5, 0x48, 0x4D);
    public static Color Warning(bool light) => light ? Color.FromArgb(0xE0, 0x8A, 0x00) : Color.FromArgb(0xF2, 0xA9, 0x00);

    /// <summary>Badge fill and digit colour. The service colour is constant: it never changes with usage.</summary>
    public static (Color fill, Color text) Badge(double usedPercent, string service, bool light)
        => (BadgeFill(service, light), Color.White);

    /// <summary>Plain style: digits in the service colour, constant at any usage level.</summary>
    public static IconRow Row(string text, double usedPercent, Color brand, bool light)
        => new IconRow(text, brand);
}
