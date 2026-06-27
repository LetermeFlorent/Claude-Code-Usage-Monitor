using System.IO;
using System.Text.Json;

namespace ClaudeUsageMonitor.Credentials;

public sealed record ClaudeCredentials(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt,
    string? SubscriptionType)
{
    public bool IsExpired => ExpiresAt is { } e && e <= DateTimeOffset.Now;
}

/// <summary>
/// Reads OAuth credentials written by Claude Code at
/// %USERPROFILE%\.claude\.credentials.json
/// </summary>
public static class CredentialsReader
{
    public static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", ".credentials.json");

    public static ClaudeCredentials? TryRead()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth))
                return null;

            if (!oauth.TryGetProperty("accessToken", out var tokenEl))
                return null;

            var token = tokenEl.GetString();
            if (string.IsNullOrWhiteSpace(token))
                return null;

            string? refresh = oauth.TryGetProperty("refreshToken", out var r) ? r.GetString() : null;
            string? sub = oauth.TryGetProperty("subscriptionType", out var s) ? s.GetString() : null;

            DateTimeOffset? expires = null;
            if (oauth.TryGetProperty("expiresAt", out var exp) &&
                exp.ValueKind == JsonValueKind.Number &&
                exp.TryGetInt64(out var ms))
            {
                expires = DateTimeOffset.FromUnixTimeMilliseconds(ms);
            }

            return new ClaudeCredentials(token!, refresh, expires, sub);
        }
        catch
        {
            return null;
        }
    }
}
