using System.IO;
using System.Text.Json;

namespace ClaudeUsageMonitor.Settings;

public enum BarPosition
{
    Left,
    Center,
    Right
}

public enum LayoutMode
{
    Stacked, // two rows (5h above 7d)
    Inline   // one row, larger elements
}

/// <summary>
/// User-persisted configuration, stored at
/// %APPDATA%\ClaudeCodeUsageMonitor\settings.json
/// </summary>
public sealed class AppSettings
{
    public BarPosition Position { get; set; } = BarPosition.Right;

    /// <summary>Fine horizontal nudge in pixels (positive = right). Solves placement quirks.</summary>
    public int OffsetX { get; set; } = 0;

    /// <summary>Fine vertical nudge in pixels (positive = down).</summary>
    public int OffsetY { get; set; } = 0;

    /// <summary>Polling interval in seconds.</summary>
    public int PollSeconds { get; set; } = 60;

    public LayoutMode Layout { get; set; } = LayoutMode.Stacked;

    /// <summary>Show the "next refresh in Xs" countdown on the bars.</summary>
    public bool ShowRefreshCountdown { get; set; } = false;

    /// <summary>Overlay width in device-independent pixels (stacked mode).</summary>
    public double WidthDip { get; set; } = 220;

    /// <summary>Overlay width in device-independent pixels (inline mode).</summary>
    public double InlineWidthDip { get; set; } = 360;

    public static string Dir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClaudeUsageMonitorWpf");

    public static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch
        {
            // Corrupt/unreadable settings → fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Non-fatal: settings just won't persist this run.
        }
    }
}
