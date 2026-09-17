using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudexUsage;

static class JsonFiles
{
    static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Write JSON to a temp file next to the target, then atomically replace the target.</summary>
    public static void WriteAtomic(string path, JsonNode node, bool indented)
    {
        string tmp = path + ".claudex-tmp";
        File.WriteAllText(tmp, node.ToJsonString(indented ? Indented : Compact));
        File.Move(tmp, path, overwrite: true);
    }

    public static DateTimeOffset? JwtExpiry(string? jwt)
    {
        try
        {
            if (string.IsNullOrEmpty(jwt)) return null;
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            string p = parts[1].Replace('-', '+').Replace('_', '/');
            p = p.PadRight(p.Length + (4 - p.Length % 4) % 4, '=');
            var json = JsonNode.Parse(Convert.FromBase64String(p));
            if (json?["exp"] is JsonValue v) return DateTimeOffset.FromUnixTimeSeconds(v.GetValue<long>());
        }
        catch { }
        return null;
    }

    public static string Trim(string s, int max = 160)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= max ? s : s[..max] + "...";
    }
}

/// <summary>Outcome of one usage GET: status, body, and how long the server asked us to wait (zero if it did not say).</summary>
sealed record UsageResponse(HttpStatusCode Status, string Body, TimeSpan RetryAfter)
{
    public static async Task<UsageResponse> ReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var ra = resp.Headers.RetryAfter;
        TimeSpan wait = ra?.Delta
                        ?? (ra?.Date is DateTimeOffset at ? at - DateTimeOffset.UtcNow : TimeSpan.Zero);
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        return new UsageResponse(resp.StatusCode, await resp.Content.ReadAsStringAsync(ct), wait);
    }
}

/// <summary>Claude usage via the claude.ai OAuth session that Claude Code stores in ~/.claude/.credentials.json.</summary>
sealed class ClaudeProvider : IUsageProvider
{
    const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e"; // Claude Code's public OAuth client id
    const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    const string TokenUrl = "https://platform.claude.com/v1/oauth/token";

    public string Name => "Claude";
    public Color Brand => Color.FromArgb(0xD9, 0x77, 0x57);
    public BadgeShape Shape => BadgeShape.Starburst;
    public string UsagePageUrl => "https://claude.ai/settings/usage";

    static string CredPath
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(dir))
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            return Path.Combine(dir, ".credentials.json");
        }
    }

    public async Task<UsageSnapshot> FetchAsync(HttpClient http, bool allowTokenRefresh, CancellationToken ct)
    {
        string? plan = null;
        try
        {
            if (!File.Exists(CredPath))
                throw new Exception("not signed in (run: claude auth login)");

            var root = JsonNode.Parse(await File.ReadAllTextAsync(CredPath, ct))?.AsObject()
                       ?? throw new Exception("credentials file is empty");
            var oauth = root["claudeAiOauth"]?.AsObject()
                        ?? throw new Exception("no claude.ai login found (run: claude auth login)");

            string access = oauth["accessToken"]?.GetValue<string>() ?? throw new Exception("no access token");
            long expiresAt = oauth["expiresAt"]?.GetValue<long>() ?? 0;
            plan = PlanName(oauth);

            bool expired = expiresAt > 0 &&
                           DateTimeOffset.FromUnixTimeMilliseconds(expiresAt) < DateTimeOffset.UtcNow.AddMinutes(1);
            if (expired && allowTokenRefresh)
                access = await RefreshAsync(root, oauth, http, ct);

            var (status, body, retryAfter) = await GetUsageAsync(http, access, ct);
            if (status == HttpStatusCode.Unauthorized && allowTokenRefresh && !expired)
            {
                access = await RefreshAsync(root, oauth, http, ct);
                (status, body, retryAfter) = await GetUsageAsync(http, access, ct);
            }
            if (status == HttpStatusCode.Unauthorized)
                throw new Exception("session expired; open Claude Code to sign in again");
            if (status == HttpStatusCode.TooManyRequests)
                return new UsageSnapshot { Service = Name, Plan = plan, Error = "rate limited", RateLimited = true, RetryAfter = retryAfter };
            if (status != HttpStatusCode.OK)
                throw new Exception($"HTTP {(int)status}: {JsonFiles.Trim(body)}");

            return new UsageSnapshot { Service = Name, Plan = plan, Windows = ParseWindows(body) };
        }
        catch (Exception ex)
        {
            return new UsageSnapshot { Service = Name, Plan = plan, Error = ex.Message };
        }
    }

    static string? PlanName(JsonObject oauth)
    {
        var sub = oauth["subscriptionType"]?.GetValue<string>();
        var tier = oauth["rateLimitTier"]?.GetValue<string>() ?? "";
        if (sub is null) return null;
        var name = Fmt.Pretty(sub);
        // e.g. "default_claude_max_5x" -> "5x"
        var mult = tier.Split('_').LastOrDefault(p => p.EndsWith("x") && p.Length <= 4 && char.IsDigit(p[0]));
        return mult is null ? name : $"{name} {mult}";
    }

    static async Task<UsageResponse> GetUsageAsync(HttpClient http, string access, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + access);
        req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        using var resp = await http.SendAsync(req, ct);
        return await UsageResponse.ReadAsync(resp, ct);
    }

    static async Task<string> RefreshAsync(JsonObject root, JsonObject oauth, HttpClient http, CancellationToken ct)
    {
        string refresh = oauth["refreshToken"]?.GetValue<string>()
                         ?? throw new Exception("no refresh token; open Claude Code to sign in again");
        var payload = new JsonObject
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
            ["client_id"] = ClientId,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"token refresh failed (HTTP {(int)resp.StatusCode}); open Claude Code to sign in again");

        var tok = JsonNode.Parse(body)!.AsObject();
        string access = tok["access_token"]?.GetValue<string>() ?? throw new Exception("refresh returned no access token");
        oauth["accessToken"] = access;
        if (tok["refresh_token"] is JsonValue rt) oauth["refreshToken"] = rt.GetValue<string>();
        long expiresIn = tok["expires_in"]?.GetValue<long>() ?? 3600;
        oauth["expiresAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + expiresIn * 1000;
        if (tok["scope"] is JsonValue sc)
            oauth["scopes"] = new JsonArray(sc.GetValue<string>()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());

        JsonFiles.WriteAtomic(CredPath, root, indented: false);
        return access;
    }

    /// <summary>
    /// Windows shown for Claude: the 5-hour session limit, the all-models weekly limit, and any
    /// model-scoped weekly limit (e.g. "7d Fable"). Read from the structured "limits" array; the
    /// loose top-level keys (nimbus_quill and friends) are internal experiments and are ignored.
    /// </summary>
    static List<UsageWindow> ParseWindows(string body)
    {
        var obj = JsonNode.Parse(body)?.AsObject() ?? throw new Exception("empty usage response");
        var list = new List<UsageWindow>();

        if (obj["limits"] is JsonArray limits)
        {
            foreach (var item in limits)
            {
                if (item is not JsonObject o || o["percent"] is not JsonValue pct) continue;
                string kind = o["kind"]?.GetValue<string>() ?? "";
                DateTimeOffset? resets = null;
                if (o["resets_at"] is JsonValue r && DateTimeOffset.TryParse(r.GetValue<string>(), out var dt)) resets = dt;

                string? label = kind switch
                {
                    "session" => "5h",
                    "weekly_all" => "7d",
                    "weekly_scoped" => "7d " + (ScopeName(o["scope"]) ?? "scoped"),
                    _ => null,
                };
                if (label is null) continue;
                var len = kind == "session" ? TimeSpan.FromHours(5) : TimeSpan.FromDays(7);
                list.Add(new UsageWindow(label, pct.GetValue<double>(), resets, len));
            }
        }

        if (list.Count == 0)
        {
            // Older response shape: only the two account-wide windows.
            foreach (var (key, label, len) in new[] { ("five_hour", "5h", TimeSpan.FromHours(5)), ("seven_day", "7d", TimeSpan.FromDays(7)) })
            {
                if (obj[key] is not JsonObject o || o["utilization"] is not JsonValue u) continue;
                DateTimeOffset? resets = null;
                if (o["resets_at"] is JsonValue r && DateTimeOffset.TryParse(r.GetValue<string>(), out var dt)) resets = dt;
                list.Add(new UsageWindow(label, u.GetValue<double>(), resets, len));
            }
        }

        if (list.Count == 0) throw new Exception("no usage windows in response");
        // 5h first, then 7d, then scoped weeklies.
        return list.OrderBy(w => w.Length ?? TimeSpan.MaxValue).ThenBy(w => w.Label.Length).ThenBy(w => w.Label).ToList();
    }

    static string? ScopeName(JsonNode? scope)
    {
        if (scope is not JsonObject s) return null;
        if (s["model"] is JsonObject m && m["display_name"] is JsonValue dn) return dn.GetValue<string>();
        if (s["surface"] is JsonValue sf) return Fmt.Pretty(sf.GetValue<string>());
        return null;
    }
}

/// <summary>Codex usage via the ChatGPT OAuth session that the Codex CLI stores in ~/.codex/auth.json.</summary>
sealed class CodexProvider : IUsageProvider
{
    const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann"; // Codex CLI's public OAuth client id
    const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    const string TokenUrl = "https://auth.openai.com/oauth/token";

    public string Name => "Codex";
    public Color Brand => Color.FromArgb(0x10, 0xA3, 0x7F);
    public BadgeShape Shape => BadgeShape.Hexagon;
    public string UsagePageUrl => "https://chatgpt.com/codex/settings/usage";

    static string AuthPath
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(dir))
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            return Path.Combine(dir, "auth.json");
        }
    }

    public async Task<UsageSnapshot> FetchAsync(HttpClient http, bool allowTokenRefresh, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(AuthPath))
                throw new Exception("not signed in (run: codex login)");

            var root = JsonNode.Parse(await File.ReadAllTextAsync(AuthPath, ct))?.AsObject()
                       ?? throw new Exception("auth file is empty");
            var tokens = root["tokens"]?.AsObject()
                         ?? throw new Exception("no ChatGPT login found (run: codex login)");
            string access = tokens["access_token"]?.GetValue<string>() ?? throw new Exception("no access token");
            string? account = tokens["account_id"]?.GetValue<string>();

            bool expired = JsonFiles.JwtExpiry(access) is DateTimeOffset exp && exp < DateTimeOffset.UtcNow.AddMinutes(1);
            if (expired && allowTokenRefresh)
                access = await RefreshAsync(root, tokens, http, ct);

            var (status, body, retryAfter) = await GetUsageAsync(http, access, account, ct);
            if (status == HttpStatusCode.Unauthorized && allowTokenRefresh && !expired)
            {
                access = await RefreshAsync(root, tokens, http, ct);
                (status, body, retryAfter) = await GetUsageAsync(http, access, account, ct);
            }
            if (status == HttpStatusCode.Unauthorized)
                throw new Exception("session expired; run: codex login");
            if (status == HttpStatusCode.TooManyRequests)
                return new UsageSnapshot { Service = Name, Error = "rate limited", RateLimited = true, RetryAfter = retryAfter };
            if (status != HttpStatusCode.OK)
                throw new Exception($"HTTP {(int)status}: {JsonFiles.Trim(body)}");

            var obj = JsonNode.Parse(body)?.AsObject() ?? throw new Exception("empty usage response");
            var plan = obj["plan_type"]?.GetValue<string>();
            return new UsageSnapshot
            {
                Service = Name,
                Plan = plan is null ? null : Fmt.Pretty(plan),
                Windows = ParseWindows(obj),
            };
        }
        catch (Exception ex)
        {
            return new UsageSnapshot { Service = Name, Error = ex.Message };
        }
    }

    static async Task<UsageResponse> GetUsageAsync(HttpClient http, string access, string? account, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + access);
        if (!string.IsNullOrEmpty(account)) req.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", account);
        using var resp = await http.SendAsync(req, ct);
        return await UsageResponse.ReadAsync(resp, ct);
    }

    static async Task<string> RefreshAsync(JsonObject root, JsonObject tokens, HttpClient http, CancellationToken ct)
    {
        string refresh = tokens["refresh_token"]?.GetValue<string>()
                         ?? throw new Exception("no refresh token; run: codex login");
        var payload = new JsonObject
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
            ["scope"] = "openid profile email",
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"token refresh failed (HTTP {(int)resp.StatusCode}); run: codex login");

        var tok = JsonNode.Parse(body)!.AsObject();
        string access = tok["access_token"]?.GetValue<string>() ?? throw new Exception("refresh returned no access token");
        tokens["access_token"] = access;
        if (tok["id_token"] is JsonValue id) tokens["id_token"] = id.GetValue<string>();
        if (tok["refresh_token"] is JsonValue rt) tokens["refresh_token"] = rt.GetValue<string>();
        root["last_refresh"] = DateTimeOffset.UtcNow.ToString("o");

        JsonFiles.WriteAtomic(AuthPath, root, indented: true);
        return access;
    }

    static List<UsageWindow> ParseWindows(JsonObject obj)
    {
        var main = new List<UsageWindow>();
        if (obj["rate_limit"] is JsonObject rl)
        {
            AddWindow(main, rl["primary_window"], "");
            AddWindow(main, rl["secondary_window"], "");
        }
        // Only the account-wide windows. "additional_rate_limits" (per-model promos such as Codex-Spark) are ignored.
        main = main.OrderBy(w => w.Length ?? TimeSpan.MaxValue).ToList();
        if (main.Count == 0) throw new Exception("no rate-limit windows in response");
        return main;
    }

    static void AddWindow(List<UsageWindow> into, JsonNode? node, string prefix)
    {
        if (node is not JsonObject w || w["used_percent"] is not JsonValue used) return;
        long secs = w["limit_window_seconds"]?.GetValue<long>() ?? 0;
        DateTimeOffset? resets = null;
        if (w["reset_at"] is JsonValue ra) resets = DateTimeOffset.FromUnixTimeSeconds(ra.GetValue<long>());
        else if (w["reset_after_seconds"] is JsonValue rs) resets = DateTimeOffset.UtcNow.AddSeconds(rs.GetValue<long>());
        into.Add(new UsageWindow(prefix + Fmt.WindowLabel(secs), used.GetValue<double>(), resets,
            secs > 0 ? TimeSpan.FromSeconds(secs) : null));
    }
}
