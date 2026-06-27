using ClaudeUsageMonitor.Settings;
using static ClaudeUsageMonitor.Ui.NativeMethods;

namespace ClaudeUsageMonitor.Ui;

/// <summary>
/// Embeds the overlay as a child of the primary taskbar (Shell_TrayWnd) so it behaves
/// like a native taskbar widget: moves with the bar, never covered by other windows,
/// and auto-hides whenever the taskbar does (e.g. fullscreen apps).
/// </summary>
public static class TaskbarPositioner
{
    private const double VerticalPadDip = 4;
    private const double EdgePadDip = 8;
    private const double LeftReserveDip = 12;

    public readonly record struct Placement(int X, int Y, int Width, int Height, int TbLeft, int TbTop);

    private static IntPtr Taskbar => FindWindow("Shell_TrayWnd", null);

    public static Placement? Compute(BarPosition position, double widthDip, int offsetX, int offsetY)
    {
        var taskbar = Taskbar;
        if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out var tb))
            return null;

        double dpi = GetDpiForWindow(taskbar);
        if (dpi <= 0) dpi = 96;
        double scale = dpi / 96.0;

        int vPad = (int)Math.Round(VerticalPadDip * scale);
        int edgePad = (int)Math.Round(EdgePadDip * scale);
        int leftReserve = (int)Math.Round(LeftReserveDip * scale);
        int width = (int)Math.Round(widthDip * scale);

        int rightBound = tb.Right - edgePad;
        var tray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray != IntPtr.Zero && GetWindowRect(tray, out var tr) && tr.Left > tb.Left)
            rightBound = tr.Left - edgePad;

        int leftBound = tb.Left + leftReserve;
        int band = Math.Max(40, rightBound - leftBound);
        if (width > band) width = band;

        int height = Math.Max(20, tb.Height - vPad * 2);
        int y = tb.Top + vPad;

        int x = position switch
        {
            BarPosition.Left => leftBound,
            BarPosition.Right => rightBound - width,
            _ => leftBound + (band - width) / 2
        };

        x += (int)Math.Round(offsetX * scale);
        y += (int)Math.Round(offsetY * scale);
        x = Math.Clamp(x, tb.Left, tb.Right - width);

        return new Placement(x, y, width, height, tb.Left, tb.Top);
    }

    /// <summary>
    /// Reparent the window into the taskbar. Returns true if embedded.
    /// </summary>
    public static bool Embed(IntPtr hwnd)
    {
        var taskbar = Taskbar;
        if (taskbar == IntPtr.Zero) return false;

        int style = GetWindowLong(hwnd, GWL_STYLE);
        style = (style & ~WS_POPUP) | WS_CHILD;
        SetWindowLong(hwnd, GWL_STYLE, style);

        return SetParent(hwnd, taskbar) != IntPtr.Zero;
    }

    /// <summary>Position an embedded child using parent-relative (taskbar-local) coordinates.</summary>
    public static void ApplyEmbedded(IntPtr hwnd, Placement p)
    {
        SetWindowPos(hwnd, IntPtr.Zero,
            p.X - p.TbLeft, p.Y - p.TbTop, p.Width, p.Height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>Fallback (non-embedded): topmost overlay in screen coordinates.</summary>
    public static void ApplyTopmost(IntPtr hwnd, Placement p)
    {
        SetWindowPos(hwnd, HWND_TOPMOST, p.X, p.Y, p.Width, p.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    public static void Reassert(IntPtr hwnd)
    {
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public static void MakeOverlayStyle(IntPtr hwnd)
    {
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }
}
