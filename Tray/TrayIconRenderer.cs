using System.Drawing;
using System.Drawing.Drawing2D;
using ClaudeUsageMonitor.Ui;
using Drawing = System.Drawing;

namespace ClaudeUsageMonitor.Tray;

/// <summary>Builds a small tray icon showing the 5h utilization as a colored fill.</summary>
internal static class TrayIconRenderer
{
    public static Icon Build(double fiveHourFraction, bool error)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);

            var track = Drawing.Color.FromArgb(120, 60, 60, 60);
            using var trackBrush = new SolidBrush(track);
            g.FillRectangle(trackBrush, 6, 4, 20, 24);

            if (error)
            {
                using var ePen = new Pen(Drawing.Color.FromArgb(255, 248, 81, 73), 3);
                g.DrawLine(ePen, 10, 8, 22, 24);
                g.DrawLine(ePen, 22, 8, 10, 24);
            }
            else
            {
                double frac = Math.Clamp(fiveHourFraction, 0, 1);
                int fillH = (int)Math.Round(24 * frac);
                var c = Theme.FillFor(frac);
                using var fb = new SolidBrush(Drawing.Color.FromArgb(255, c.R, c.G, c.B));
                g.FillRectangle(fb, 6, 4 + (24 - fillH), 20, fillH);
            }
        }

        IntPtr h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(h);
        }
    }
}
