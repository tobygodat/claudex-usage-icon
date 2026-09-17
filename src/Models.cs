namespace ClaudexUsage;

/// <summary>One rate-limit window (e.g. the rolling 5-hour or 7-day window).</summary>
sealed record UsageWindow(string Label, double UsedPercent, DateTimeOffset? ResetsAt, TimeSpan? Length)
{
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}

sealed class UsageSnapshot
{
    public required string Service { get; init; }
    public string? Plan { get; init; }
    public List<UsageWindow> Windows { get; init; } = new();
    public string? Error { get; init; }
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>The window shown as the big number in the tray icon (shortest window).</summary>
    public UsageWindow? Primary => Windows.Count > 0 ? Windows[0] : null;

    /// <summary>The window shown as the thin bar under the number (next-longer window).</summary>
    public UsageWindow? Secondary => Windows.Count > 1 ? Windows[1] : null;

    /// <summary>The account-wide rolling 5-hour window, if the plan has one.</summary>
    public UsageWindow? FiveHour => Windows.FirstOrDefault(w => w.Label == "5h");

    /// <summary>The account-wide rolling 7-day window, if the plan has one.</summary>
    public UsageWindow? SevenDay => Windows.FirstOrDefault(w => w.Label == "7d");
}

interface IUsageProvider
{
    string Name { get; }
    Color Brand { get; }
    BadgeShape Shape { get; }
    string UsagePageUrl { get; }
    Task<UsageSnapshot> FetchAsync(HttpClient http, bool allowTokenRefresh, CancellationToken ct);
}

static class Fmt
{
    public static string Countdown(DateTimeOffset? at)
    {
        if (at is null) return "";
        var d = at.Value - DateTimeOffset.Now;
        if (d <= TimeSpan.Zero) return "now";
        if (d.TotalDays >= 1) return $"{(int)d.TotalDays}d {d.Hours}h";
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours}h {d.Minutes}m";
        return $"{Math.Max(1, (int)d.TotalMinutes)}m";
    }

    public static string Ago(DateTimeOffset at)
    {
        var d = DateTimeOffset.Now - at;
        if (d.TotalSeconds < 5) return "just now";
        if (d.TotalMinutes < 1) return $"{(int)d.TotalSeconds}s ago";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m ago";
        return $"{(int)d.TotalHours}h ago";
    }

    public static string Clock(DateTimeOffset? at)
    {
        if (at is null) return "";
        var local = at.Value.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("HH:mm") : local.ToString("ddd HH:mm");
    }

    public static string WindowLabel(long seconds) => seconds switch
    {
        18000 => "5h",
        604800 => "7d",
        _ when seconds % 86400 == 0 => $"{seconds / 86400}d",
        _ when seconds % 3600 == 0 => $"{seconds / 3600}h",
        _ => $"{seconds / 60}m",
    };

    public static string Pretty(string key)
    {
        if (string.IsNullOrEmpty(key)) return key;
        var s = key.Replace('_', ' ');
        return char.ToUpperInvariant(s[0]) + s[1..];
    }
}
