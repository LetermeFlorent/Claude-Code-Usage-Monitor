using System.Text;
using static ClaudeUsageMonitor.Ui.NativeMethods;

namespace ClaudeUsageMonitor.Ui;

/// <summary>Detects whether the foreground window is a fullscreen app (game/video),
/// so the overlay can hide instead of drawing on top of it.</summary>
public static class FullscreenDetector
{
    public static bool IsForegroundFullscreen()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;

        // Ignore the desktop / shell windows.
        var cls = new StringBuilder(256);
        GetClassName(fg, cls, cls.Capacity);
        var name = cls.ToString();
        if (name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow")
            return false;

        if (!GetWindowRect(fg, out var wr)) return false;

        var mon = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;

        var m = mi.rcMonitor;
        // Fullscreen = window covers the entire monitor (small tolerance).
        return wr.Left <= m.Left + 1 && wr.Top <= m.Top + 1 &&
               wr.Right >= m.Right - 1 && wr.Bottom >= m.Bottom - 1;
    }
}
