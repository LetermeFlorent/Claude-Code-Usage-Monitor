using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using ClaudeUsageMonitor.Api;
using ClaudeUsageMonitor.Models;
using ClaudeUsageMonitor.Settings;
using ClaudeUsageMonitor.Tray;
using ClaudeUsageMonitor.Ui;
using Application = System.Windows.Application;

namespace ClaudeUsageMonitor;

public partial class App : Application
{
    private AppSettings _settings = null!;
    private UsagePoller _poller = null!;
    private OverlayWindow _overlay = null!;
    private TrayController _tray = null!;

    private DispatcherTimer _pollTimer = null!;
    private DispatcherTimer _tickTimer = null!;
    private CancellationTokenSource? _inFlight;
    private UsageSnapshot _last = UsageSnapshot.Error(UsageState.NetworkError, "Chargement…");
    private DateTimeOffset _nextPollAt;
    private bool _shuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _settings = AppSettings.Load();
        _poller = new UsagePoller();

        _overlay = new OverlayWindow();
        _overlay.Show();          // triggers OnSourceInitialized → embeds into taskbar
        _overlay.SetLayout(LayoutMode.Stacked);
        _overlay.SetRefresh(_settings.ShowRefreshCountdown, _settings.PollSeconds);
        _overlay.Render(_last);
        Reposition();

        _tray = new TrayController(_settings);
        _tray.PositionChanged += _ => { _settings.Save(); Reposition(); };
        _tray.PollIntervalChanged += OnPollIntervalChanged;
        _tray.ShowCountdownChanged += show =>
        {
            _settings.Save();
            int secs = (int)Math.Ceiling((_nextPollAt - DateTimeOffset.Now).TotalSeconds);
            _overlay.SetRefresh(show, Math.Max(secs, _settings.PollSeconds));
            _overlay.Render(_last);
            Reposition();
        };
        _tray.RefreshRequested += () => _ = PollAsync();
        _tray.QuitRequested += Quit;

        _pollTimer = new DispatcherTimer { Interval = PollInterval() };
        _pollTimer.Tick += (_, _) => _ = PollAsync();
        _pollTimer.Start();

        // 1s UI tick: refresh countdowns; re-pin only when NOT embedded (fallback overlay).
        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tickTimer.Tick += (_, _) => OnTick();
        _tickTimer.Start();

        SystemEvents.DisplaySettingsChanged += (_, _) => Reposition();
        SystemEvents.UserPreferenceChanged += (_, ev) =>
        {
            if (ev.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
                _overlay.ApplyTheme();
        };

        _ = PollAsync();
    }

    private TimeSpan PollInterval() => TimeSpan.FromSeconds(Math.Max(15, _settings.PollSeconds));

    private void OnPollIntervalChanged(int seconds)
    {
        _settings.PollSeconds = seconds;
        _settings.Save();
        _pollTimer.Interval = PollInterval();
        _nextPollAt = DateTimeOffset.Now + PollInterval();
    }

    private void OnTick()
    {
        // Embedded child auto-hides with the taskbar, so it never needs topmost babysitting.
        // The non-embedded fallback must re-pin and dodge fullscreen apps.
        if (!_overlay.Embedded)
        {
            if (FullscreenDetector.IsForegroundFullscreen())
            {
                if (_overlay.IsVisible) _overlay.Hide();
                return;
            }
            if (!_overlay.IsVisible)
            {
                _overlay.Show();
                Reposition();
            }
            TaskbarPositioner.Reassert(_overlay.Handle);
        }

        int secs = (int)Math.Ceiling((_nextPollAt - DateTimeOffset.Now).TotalSeconds);
        _overlay.SetRefresh(_settings.ShowRefreshCountdown, secs);
        _overlay.Render(_last);
        _tray.SetRefreshIn(secs);
        // Width is reserved at poll time (largest countdown), so no per-second reposition needed.
    }

    private void Reposition()
    {
        double widthDip = _overlay.DesiredWidthDip();
        var p = TaskbarPositioner.Compute(_settings.Position, widthDip,
            _settings.OffsetX, _settings.OffsetY);
        if (p is not { } placement) return;

        if (_overlay.Embedded)
            TaskbarPositioner.ApplyEmbedded(_overlay.Handle, placement);
        else
            TaskbarPositioner.ApplyTopmost(_overlay.Handle, placement);
    }

    private async Task PollAsync()
    {
        if (_shuttingDown) return;

        _nextPollAt = DateTimeOffset.Now + PollInterval();

        _inFlight?.Cancel();
        var cts = new CancellationTokenSource();
        _inFlight = cts;

        try
        {
            var snap = await _poller.FetchAsync(cts.Token);
            if (cts.IsCancellationRequested) return;

            _last = snap;
            _overlay.SetRefresh(_settings.ShowRefreshCountdown, _settings.PollSeconds);
            _overlay.Render(snap);
            _tray.Update(snap);
            Reposition(); // re-fit width to the freshly rendered content
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer poll
        }
        catch (Exception ex)
        {
            _last = UsageSnapshot.Error(UsageState.NetworkError, ex.Message);
            _overlay.Render(_last);
            _tray.Update(_last);
        }
    }

    private void Quit()
    {
        _shuttingDown = true;
        _pollTimer?.Stop();
        _tickTimer?.Stop();
        _inFlight?.Cancel();
        _tray?.Dispose();
        _poller?.Dispose();
        _overlay?.Close();
        Shutdown();
    }
}
