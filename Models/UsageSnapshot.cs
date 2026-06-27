namespace ClaudeUsageMonitor.Models;

public enum UsageState
{
    Ok,
    NoCredentials,
    TokenExpired,
    AuthFailed,
    NetworkError
}

/// <summary>One rate-limit window (5h or 7d).</summary>
public readonly record struct UsageWindow(double Utilization, DateTimeOffset? ResetsAt)
{
    /// <summary>Utilization clamped to 0..1 (input may be 0..100 or 0..1; normalized on parse).</summary>
    public double Fraction => Math.Clamp(Utilization, 0.0, 1.0);

    public int Percent => (int)Math.Round(Fraction * 100.0);

    /// <summary>Human countdown until reset, e.g. "2h 13m" or "—".</summary>
    public string Countdown(DateTimeOffset now)
    {
        if (ResetsAt is not { } reset) return "—";
        var span = reset - now;
        if (span <= TimeSpan.Zero) return "now";
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        return $"{span.Minutes}m";
    }
}

/// <summary>Immutable snapshot of current usage, produced by the poller.</summary>
public sealed class UsageSnapshot
{
    public UsageState State { get; init; } = UsageState.Ok;
    public UsageWindow FiveHour { get; init; }
    public UsageWindow SevenDay { get; init; }
    public DateTimeOffset FetchedAt { get; init; }
    public string? Message { get; init; }

    public static UsageSnapshot Error(UsageState state, string message) => new()
    {
        State = state,
        Message = message,
        FetchedAt = DateTimeOffset.Now
    };
}
