using System.Drawing;
using System.Windows.Forms;
using ClaudeUsageMonitor.Models;
using ClaudeUsageMonitor.Settings;

namespace ClaudeUsageMonitor.Tray;

/// <summary>Owns the system-tray NotifyIcon and its right-click menu.</summary>
public sealed class TrayController : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly AppSettings _settings;
    private readonly ToolStripMenuItem _left;
    private readonly ToolStripMenuItem _right;
    private readonly List<(ToolStripMenuItem item, int seconds)> _intervals = new();
    private readonly ToolStripMenuItem _showCountdown;
    private readonly ToolStripMenuItem _startup;
    private Icon? _current;
    private string _summary = "Claude Usage — chargement…";

    public event Action<BarPosition>? PositionChanged;
    public event Action<int>? PollIntervalChanged;
    public event Action<bool>? ShowCountdownChanged;
    public event Action? RefreshRequested;
    public event Action? QuitRequested;

    public TrayController(AppSettings settings)
    {
        _settings = settings;

        var menu = new ContextMenuStrip();

        var posHeader = new ToolStripMenuItem("Position des barres") { Enabled = false };
        _left = new ToolStripMenuItem("Gauche", null, (_, _) => Choose(BarPosition.Left));
        _right = new ToolStripMenuItem("Droite", null, (_, _) => Choose(BarPosition.Right));

        // Refresh-interval submenu.
        var intervalMenu = new ToolStripMenuItem("Actualisation");
        foreach (var (label, secs) in new[]
                 {
                     ("30 secondes", 30),
                     ("1 minute", 60),
                     ("2 minutes", 120),
                     ("5 minutes", 300)
                 })
        {
            var item = new ToolStripMenuItem(label, null, (_, _) => ChooseInterval(secs));
            _intervals.Add((item, secs));
            intervalMenu.DropDownItems.Add(item);
        }

        _showCountdown = new ToolStripMenuItem("Afficher temps avant actualisation", null,
            (_, _) =>
            {
                _settings.ShowRefreshCountdown = !_settings.ShowRefreshCountdown;
                SyncChecks();
                ShowCountdownChanged?.Invoke(_settings.ShowRefreshCountdown);
            })
        { CheckOnClick = false };

        _startup = new ToolStripMenuItem("Démarrer avec Windows", null,
            (_, _) =>
            {
                StartupManager.SetEnabled(!StartupManager.IsEnabled());
                SyncChecks();
            })
        { CheckOnClick = false };

        var refresh = new ToolStripMenuItem("Rafraîchir maintenant", null, (_, _) => RefreshRequested?.Invoke());
        var quit = new ToolStripMenuItem("Quitter", null, (_, _) => QuitRequested?.Invoke());

        menu.Items.Add(posHeader);
        menu.Items.Add(_left);
        menu.Items.Add(_right);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(intervalMenu);
        menu.Items.Add(_showCountdown);
        menu.Items.Add(_startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(refresh);
        menu.Items.Add(quit);

        _current = TrayIconRenderer.Build(0, error: false);
        _icon = new NotifyIcon
        {
            Text = "Claude Usage Monitor",
            Visible = true,
            Icon = _current,
            ContextMenuStrip = menu
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                RefreshRequested?.Invoke();
        };

        SyncChecks();
    }

    private void Choose(BarPosition p)
    {
        _settings.Position = p;
        SyncChecks();
        PositionChanged?.Invoke(p);
    }

    private void ChooseInterval(int seconds)
    {
        _settings.PollSeconds = seconds;
        SyncChecks();
        PollIntervalChanged?.Invoke(seconds);
    }

    private void SyncChecks()
    {
        _left.Checked = _settings.Position == BarPosition.Left;
        _right.Checked = _settings.Position == BarPosition.Right;
        _showCountdown.Checked = _settings.ShowRefreshCountdown;
        _startup.Checked = StartupManager.IsEnabled();
        foreach (var (item, secs) in _intervals)
            item.Checked = _settings.PollSeconds == secs;
    }

    /// <summary>Update tray glyph + tooltip from latest snapshot.</summary>
    public void Update(UsageSnapshot snap)
    {
        bool error = snap.State != UsageState.Ok;
        var next = TrayIconRenderer.Build(snap.FiveHour.Fraction, error);
        _icon.Icon = next;

        var old = _current;
        _current = next;
        old?.Dispose();

        _summary = error
            ? "Claude Usage — indisponible"
            : $"5h {snap.FiveHour.Percent}% · 7d {snap.SevenDay.Percent}%";
        _icon.Text = _summary;
    }

    /// <summary>Append next-refresh countdown to the tray tooltip.</summary>
    public void SetRefreshIn(int seconds)
    {
        var txt = $"{_summary} · ↻ {Math.Max(0, seconds)}s";
        if (txt.Length > 63) txt = txt.Substring(0, 63);
        _icon.Text = txt;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _current?.Dispose();
    }
}
