using System.Text.Json;

namespace ClaudexUsage;

sealed class AppSettings
{
    public bool ShowRemaining { get; set; }
    /// <summary>"square" = number on a full-bleed tile; "logo" = number inside a logo-shaped badge; "plain" = number only; "two-rows" = plain 5h over 7d.</summary>
    public string IconStyle { get; set; } = "square";
    /// <summary>
    /// Seconds between polls. The usage endpoints are meant for on-demand reads and return HTTP 429
    /// when hit every minute, so the floor is 5 minutes.
    /// </summary>
    public int PollSeconds { get; set; } = MinPollSeconds;
    public const int MinPollSeconds = 300;
    public bool ShowClaude { get; set; } = true;
    public bool ShowCodex { get; set; } = true;
    /// <summary>Set after the first run has asked Windows to keep our icons in the taskbar corner.</summary>
    public bool PromotedOnce { get; set; }

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudexUsage");

    static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
                s.PollSeconds = Math.Max(MinPollSeconds, s.PollSeconds); // settings saved by older builds
                return s;
            }
        }
        catch { /* fall through to defaults */ }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-fatal */ }
    }
}
