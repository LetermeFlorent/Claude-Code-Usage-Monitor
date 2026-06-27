using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using ClaudeUsageMonitor.Models;
using ClaudeUsageMonitor.Settings;
using Color = System.Windows.Media.Color;

namespace ClaudeUsageMonitor.Ui;

public partial class OverlayWindow : Window
{
    private const int SegmentCount = 10;
    private const double SegmentGap = 1;
    private const double SegmentRadius = 2;

    private readonly Border[] _seg5h;
    private readonly Border[] _seg7d;
    private readonly Border[] _seg5hI;
    private readonly Border[] _seg7dI;

    private Color _trackColor = Color.FromRgb(0x44, 0x44, 0x44);
    private LayoutMode _layout = LayoutMode.Stacked;
    private bool _showRefresh;
    private int _refreshSecs;

    public OverlayWindow()
    {
        InitializeComponent();
        _seg5h = BuildSegments(Seg5h, 10, 13);
        _seg7d = BuildSegments(Seg7d, 10, 13);
        _seg5hI = BuildSegments(Seg5hI, 12, 20);
        _seg7dI = BuildSegments(Seg7dI, 12, 20);
        ApplyTheme();
    }

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    public bool Embedded { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TaskbarPositioner.MakeOverlayStyle(Handle);
        Embedded = TaskbarPositioner.Embed(Handle);
    }

    public void SetRefresh(bool show, int seconds)
    {
        _showRefresh = show;
        _refreshSecs = Math.Max(0, seconds);
    }

    /// <summary>Measured width (DIP) of the active layout's content, so the host
    /// window can be sized to fit and nothing gets clipped.</summary>
    public double DesiredWidthDip()
    {
        FrameworkElement root = _layout == LayoutMode.Inline ? InlineRoot : StackedRoot;
        root.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        double pad = RootPanel.Padding.Left + RootPanel.Padding.Right;
        double w = root.DesiredSize.Width + pad + 6;
        return Math.Max(120, w);
    }

    public void SetLayout(LayoutMode mode)
    {
        _layout = mode;
        StackedRoot.Visibility = mode == LayoutMode.Stacked ? Visibility.Visible : Visibility.Collapsed;
        InlineRoot.Visibility = mode == LayoutMode.Inline ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Border[] BuildSegments(StackPanel host, double w, double h)
    {
        var arr = new Border[SegmentCount];
        for (int i = 0; i < SegmentCount; i++)
        {
            var b = new Border
            {
                Width = w,
                Height = h,
                CornerRadius = new CornerRadius(SegmentRadius),
                Margin = new Thickness(0, 0, i == SegmentCount - 1 ? 0 : SegmentGap, 0)
            };
            host.Children.Add(b);
            arr[i] = b;
        }
        return arr;
    }

    private static bool IsLightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var v = key?.GetValue("SystemUsesLightTheme");
            if (v is int i) return i != 0;
        }
        catch { }
        return false;
    }

    public void ApplyTheme()
    {
        bool light = IsLightTaskbar();
        var primary = light ? Colors.Black : Colors.White;
        var muted = light ? Color.FromRgb(0x40, 0x40, 0x40) : Color.FromRgb(0xC8, 0xC8, 0xC8);
        _trackColor = light ? Color.FromRgb(0xAA, 0xAA, 0xAA) : Color.FromRgb(0x44, 0x44, 0x44);

        var primaryBrush = new SolidColorBrush(primary);
        var mutedBrush = new SolidColorBrush(muted);

        foreach (var t in new[] { Label5h, Label7d, Label5hI, Label7dI })
            t.Foreground = primaryBrush;
        foreach (var t in new[] { Tag5h, Tag7d, Tag5hI, Tag7dI })
            t.Foreground = mutedBrush;
    }

    public void Render(UsageSnapshot snap)
    {
        if (snap.State != UsageState.Ok)
        {
            StatusText.Text = snap.Message ?? "Indisponible";
            StatusText.Visibility = Visibility.Visible;
            StackedRoot.Visibility = Visibility.Collapsed;
            InlineRoot.Visibility = Visibility.Collapsed;
            return;
        }

        StatusText.Visibility = Visibility.Collapsed;
        SetLayout(_layout); // restore the active layout's visibility
        var now = snap.FetchedAt;

        SetRow(_seg5h, Label5h, snap.FiveHour, now);
        SetRow(_seg7d, Label7d, snap.SevenDay, now);
        SetRow(_seg5hI, Label5hI, snap.FiveHour, now);
        SetRow(_seg7dI, Label7dI, snap.SevenDay, now);

        if (_showRefresh)
        {
            var suffix = $"   ↻{_refreshSecs}s";
            Label7d.Text += suffix;
            Label7dI.Text += suffix;
        }
    }

    private void SetRow(Border[] segs, TextBlock label, UsageWindow w, DateTimeOffset now)
    {
        PaintRow(segs, w.Fraction);
        label.Text = $"{w.Percent}%  {w.Countdown(now)}";
    }

    private void PaintRow(Border[] segs, double frac)
    {
        frac = Math.Clamp(frac, 0, 1);
        int filled = (int)Math.Round(frac * SegmentCount);
        if (filled == 0 && frac > 0) filled = 1;

        var fillBrush = new SolidColorBrush(Theme.FillFor(frac));
        fillBrush.Freeze();
        var trackBrush = new SolidColorBrush(_trackColor) { Opacity = 0.45 };
        trackBrush.Freeze();

        for (int i = 0; i < segs.Length; i++)
            segs[i].Background = i < filled ? fillBrush : trackBrush;
    }
}
