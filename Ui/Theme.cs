using Color = System.Windows.Media.Color;

namespace ClaudeUsageMonitor.Ui;

/// <summary>Color helpers: utilization → green→amber→red gradient.</summary>
public static class Theme
{
    public static readonly Color TrackColor = Color.FromArgb(0x55, 0x3A, 0x3A, 0x3A);
    public static readonly Color TextColor = Color.FromRgb(0xEC, 0xEC, 0xEC);
    public static readonly Color MutedText = Color.FromRgb(0x9A, 0x9A, 0x9A);
    public static readonly Color WarnText = Color.FromRgb(0xFF, 0xB3, 0x4D);

    /// <summary>Anthropic accent (terracotta orange) for the filled segments.</summary>
    public static readonly Color Accent = Color.FromRgb(0xD9, 0x77, 0x57);

    public static Color FillFor(double fraction) => Accent;

    public static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }
}
