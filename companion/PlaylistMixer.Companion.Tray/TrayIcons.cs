using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PlaylistMixer.Companion.Tray;

/// <summary>Generates the tray icon at runtime so no .ico asset has to be shipped. The mark mirrors the
/// web app logo (web/public/favicon.svg): a persimmon rounded tile with the white "playlist lines +
/// mixer knobs" glyph.</summary>
internal static class TrayIcons
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    // The web BrandMark gradient stops (--accent-bright → --accent / brand-600).
    private static readonly Color Persimmon = Color.FromArgb(0xE4, 0x57, 0x2E);
    private static readonly Color PersimmonDeep = Color.FromArgb(0xC8, 0x45, 0x1B);

    /// <summary>The PlaylistMixer brand mark: a rounded persimmon tile with three white slider lines
    /// and three knobs. Coordinates are favicon.svg's 24-unit artwork scaled to 32px.</summary>
    public static Icon CreateAppIcon()
    {
        const int s = 32;
        const float k = s / 24f; // favicon viewBox is 24 units
        using var bmp = new Bitmap(s, s);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Rounded persimmon tile (favicon rx=6 on a 24 box).
            var tile = new RectangleF(0, 0, s - 1, s - 1);
            using (var path = RoundedRect(tile, 6f * k))
            using (var fill = new LinearGradientBrush(tile, Persimmon, PersimmonDeep, 145f))
                g.FillPath(fill, path);

            // White glyph: three slider lines + three knobs (favicon coordinates × scale).
            using var pen = new Pen(Color.White, 2f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, 5 * k, 7.5f * k, 19 * k, 7.5f * k);
            g.DrawLine(pen, 5 * k, 12f * k, 19 * k, 12f * k);
            g.DrawLine(pen, 5 * k, 16.5f * k, 19 * k, 16.5f * k);

            using var knob = new SolidBrush(Color.White);
            Knob(g, knob, 15f * k, 7.5f * k, 2.1f * k);
            Knob(g, knob, 9f * k, 12f * k, 2.1f * k);
            Knob(g, knob, 12.5f * k, 16.5f * k, 2.1f * k);
        }

        // GetHicon returns a GDI handle we don't own long-term; clone into a self-contained Icon and
        // free the handle so we don't leak it.
        var hicon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hicon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hicon);
        }
    }

    private static void Knob(Graphics g, Brush brush, float cx, float cy, float r) =>
        g.FillEllipse(brush, cx - r, cy - r, r * 2f, r * 2f);

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2f;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
