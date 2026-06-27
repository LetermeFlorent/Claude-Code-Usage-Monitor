using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ClaudeUsageMonitor.Credentials;
using ClaudeUsageMonitor.Models;

namespace ClaudeUsageMonitor.Api;

/// <summary>
/// Fetches Claude usage / rate-limit windows from Anthropic's OAuth usage endpoint,
/// falling back to rate-limit response headers on the Messages API.
/// </summary>
public sealed class UsagePoller : IDisposable
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string MessagesUrl = "https://api.anthropic.com/v1/messages";
    private const string OAuthBeta = "oauth-2025-04-20";

    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public async Task<UsageSnapshot> FetchAsync(CancellationToken ct)
    {
        var creds = CredentialsReader.TryRead();
        if (creds is null)
            return UsageSnapshot.Error(UsageState.NoCredentials,
                "Fichier .credentials.json introuvable. Lance Claude Code et connecte-toi.");

        if (creds.IsExpired)
            return UsageSnapshot.Error(UsageState.TokenExpired,
                "Token expiré. Relance Claude Code pour le rafraîchir.");

        try
        {
            var primary = await TryPrimaryAsync(creds.AccessToken, ct);
            if (primary is not null)
                return primary;

            var fallback = await TryFallbackAsync(creds.AccessToken, ct);
            if (fallback is not null)
                return fallback;

            return UsageSnapshot.Error(UsageState.NetworkError,
                "Réponse API illisible (endpoints usage + fallback).");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return UsageSnapshot.Error(UsageState.NetworkError, $"Réseau: {ex.Message}");
        }
        catch (Exception ex)
        {
            return UsageSnapshot.Error(UsageState.NetworkError, ex.Message);
        }
    }

    private async Task<UsageSnapshot?> TryPrimaryAsync(string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("anthropic-beta", OAuthBeta);

        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return UsageSnapshot.Error(UsageState.AuthFailed,
                $"Auth refusée ({(int)resp.StatusCode}). Relance Claude Code.");

        if (!resp.IsSuccessStatusCode)
            return null; // let fallback try

        var body = await resp.Content.ReadAsStringAsync(ct);
        return ParseUsageJson(body);
    }

    private static UsageSnapshot? ParseUsageJson(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var five = ParseWindow(root, "five_hour");
            var seven = ParseWindow(root, "seven_day");
            if (five is null && seven is null)
                return null;

            return new UsageSnapshot
            {
                State = UsageState.Ok,
                FiveHour = five ?? default,
                SevenDay = seven ?? default,
                FetchedAt = DateTimeOffset.Now
            };
        }
        catch
        {
            return null;
        }
    }

    private static UsageWindow? ParseWindow(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var win) || win.ValueKind != JsonValueKind.Object)
            return null;

        double util = 0;
        if (win.TryGetProperty("utilization", out var u))
        {
            if (u.ValueKind == JsonValueKind.Number)
                util = u.GetDouble();
            else if (u.ValueKind == JsonValueKind.String &&
                     double.TryParse(u.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var us))
                util = us;
        }
        // Normalize 0..100 to 0..1.
        if (util > 1.0) util /= 100.0;

        DateTimeOffset? reset = null;
        if (win.TryGetProperty("resets_at", out var r))
            reset = ParseReset(r);

        return new UsageWindow(util, reset);
    }

    private static DateTimeOffset? ParseReset(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Number when el.TryGetInt64(out var num):
                // Heuristic: ms vs seconds since epoch.
                return num > 9_999_999_999L
                    ? DateTimeOffset.FromUnixTimeMilliseconds(num)
                    : DateTimeOffset.FromUnixTimeSeconds(num);
            case JsonValueKind.String:
                var s = el.GetString();
                if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
                    return dto;
                if (long.TryParse(s, out var asNum))
                    return asNum > 9_999_999_999L
                        ? DateTimeOffset.FromUnixTimeMilliseconds(asNum)
                        : DateTimeOffset.FromUnixTimeSeconds(asNum);
                break;
        }
        return null;
    }

    /// <summary>
    /// Fallback: a minimal Messages request whose response headers carry unified rate-limit info.
    /// </summary>
    private async Task<UsageSnapshot?> TryFallbackAsync(string token, CancellationToken ct)
    {
        var payload = """
        {"model":"claude-haiku-4-5-20251001","max_tokens":1,"messages":[{"role":"user","content":"."}]}
        """;

        using var req = new HttpRequestMessage(HttpMethod.Post, MessagesUrl)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        req.Headers.TryAddWithoutValidation("anthropic-beta", OAuthBeta);

        using var resp = await _http.SendAsync(req, ct);

        var five = ReadHeaderWindow(resp,
            "anthropic-ratelimit-unified-5h-utilization",
            "anthropic-ratelimit-unified-5h-reset");
        var seven = ReadHeaderWindow(resp,
            "anthropic-ratelimit-unified-7d-utilization",
            "anthropic-ratelimit-unified-7d-reset");

        if (five is null && seven is null)
        {
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return UsageSnapshot.Error(UsageState.AuthFailed,
                    $"Auth refusée ({(int)resp.StatusCode}).");
            return null;
        }

        return new UsageSnapshot
        {
            State = UsageState.Ok,
            FiveHour = five ?? default,
            SevenDay = seven ?? default,
            FetchedAt = DateTimeOffset.Now
        };
    }

    private static UsageWindow? ReadHeaderWindow(HttpResponseMessage resp, string utilKey, string resetKey)
    {
        double? util = null;
        DateTimeOffset? reset = null;

        if (resp.Headers.TryGetValues(utilKey, out var uv))
        {
            var raw = uv.FirstOrDefault();
            if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var u))
                util = u > 1.0 ? u / 100.0 : u;
        }

        if (resp.Headers.TryGetValues(resetKey, out var rv))
        {
            var raw = rv.FirstOrDefault();
            if (long.TryParse(raw, out var epoch))
                reset = epoch > 9_999_999_999L
                    ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                    : DateTimeOffset.FromUnixTimeSeconds(epoch);
            else if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                         DateTimeStyles.AssumeUniversal, out var dto))
                reset = dto;
        }

        if (util is null && reset is null) return null;
        return new UsageWindow(util ?? 0, reset);
    }

    public void Dispose() => _http.Dispose();
}
