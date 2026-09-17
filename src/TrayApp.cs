using System.Diagnostics;
using Microsoft.Win32;

namespace ClaudexUsage;

sealed class TrayApp : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "ClaudexUsage";

    sealed class Slot
    {
        public required IUsageProvider Provider { get; init; }
        public required NotifyIcon Icon { get; init; }
        public UsageSnapshot? Last { get; set; }
        public Icon? Current { get; set; }
        /// <summary>After an HTTP 429 we stop polling this service until this time.</summary>
        public DateTimeOffset BackoffUntil { get; set; } = DateTimeOffset.MinValue;
        /// <summary>Consecutive 429s; each one doubles the pause (5, 10, 20, 40, 60 min cap). Reset by any other result.</summary>
        public int Strikes { get; set; }
    }

    readonly AppSettings settings = AppSettings.Load();
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };
    readonly ContextMenuStrip menu = new();
    readonly System.Windows.Forms.Timer timer = new();
    readonly List<Slot> slots = new();
    readonly bool lightTheme = IsLightTaskbar();
    DetailsForm? details;
    bool fetching;

    /// <param name="menuShot">Debug only: path of a PNG to save a screenshot of the right-click menu to, then exit.</param>
    public TrayApp(string? menuShot = null)
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudexUsage/1.0");
        MenuTheme.Apply(menu);
        menu.Opening += (_, _) => BuildMenu();

        slots.Add(MakeSlot(new ClaudeProvider()));
        slots.Add(MakeSlot(new CodexProvider()));

        if (menuShot is not null)
        {
            // Debug: open the menu over sample data, screenshot it, and quit. No tray icons, no network.
            slots[0].Last = SampleSnapshot("Claude", "Max 5x", 37, 12);
            slots[1].Last = SampleSnapshot("Codex", "Plus", 62, 48);
            menu.AutoClose = false; // a process with no foreground window would otherwise lose the menu at once
            var shot = new System.Windows.Forms.Timer { Interval = 400 };
            shot.Tick += (_, _) =>
            {
                shot.Stop();
                var at = new Point(300, 200);
                menu.Show(at);
                var wait = new System.Windows.Forms.Timer { Interval = 700 };
                wait.Tick += (_, _) =>
                {
                    wait.Stop();
                    using (var bmp = new Bitmap(menu.Width, menu.Height))
                    {
                        menu.DrawToBitmap(bmp, new Rectangle(0, 0, menu.Width, menu.Height));
                        bmp.Save(menuShot, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    File.WriteAllText(Path.ChangeExtension(menuShot, ".txt"),
                        $"bounds={menu.Bounds} visible={menu.Visible} dpi={menu.DeviceDpi} rounded={MenuTheme.RoundedCorners} screen={Screen.PrimaryScreen?.Bounds}");
                    menu.Close();
                    ExitThread();
                };
                wait.Start();
            };
            shot.Start();
            return;
        }

        ApplyVisibility();
        foreach (var s in slots) UpdateIcon(s);

        // First tick fires quickly, then settles to the configured poll interval.
        timer.Interval = 300;
        timer.Tick += async (_, _) =>
        {
            timer.Interval = Math.Max(AppSettings.MinPollSeconds, settings.PollSeconds) * 1000;
            await RefreshAllAsync();
        };
        timer.Start();

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    static UsageSnapshot SampleSnapshot(string service, string plan, double fiveHour, double sevenDay) => new()
    {
        Service = service,
        Plan = plan,
        Windows =
        {
            new UsageWindow("5h", fiveHour, DateTimeOffset.Now.AddHours(2).AddMinutes(10), TimeSpan.FromHours(5)),
            new UsageWindow("7d", sevenDay, DateTimeOffset.Now.AddDays(3).AddHours(4), TimeSpan.FromDays(7)),
        },
        FetchedAt = DateTimeOffset.Now.AddMinutes(-2),
    };

    Slot MakeSlot(IUsageProvider provider)
    {
        var icon = new NotifyIcon { Text = provider.Name, ContextMenuStrip = menu, Visible = false };
        icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleDetails(); };
        return new Slot { Provider = provider, Icon = icon };
    }

    void ApplyVisibility()
    {
        foreach (var s in slots)
            s.Icon.Visible = s.Provider is ClaudeProvider ? settings.ShowClaude : settings.ShowCodex;
    }

    void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e) => RedrawAll();
    void OnDisplaySettingsChanged(object? sender, EventArgs e) => RedrawAll();

    // ---- data -------------------------------------------------------------------------------

    async Task RefreshAllAsync()
    {
        if (fetching) return;
        fetching = true;
        try
        {
            bool allowRefresh = Environment.GetEnvironmentVariable("CLAUDEX_NO_REFRESH") != "1";
            var now = DateTimeOffset.Now;
            var tasks = slots.Select(async s => now < s.BackoffUntil
                    ? null
                    : await s.Provider.FetchAsync(http, allowRefresh, CancellationToken.None)).ToArray();
            var results = await Task.WhenAll(tasks);
            for (int i = 0; i < slots.Count; i++)
            {
                if (results[i] is not UsageSnapshot snap) continue; // still backing off; keep last icon
                var slot = slots[i];
                if (snap.RateLimited)
                {
                    // Exponential backoff, never shorter than what the server's Retry-After asked for.
                    var wait = TimeSpan.FromMinutes(Math.Min(60, 5 << Math.Min(slot.Strikes, 4)));
                    if (snap.RetryAfter > wait) wait = snap.RetryAfter;
                    slot.Strikes++;
                    slot.BackoffUntil = DateTimeOffset.Now + wait;
                    snap = new UsageSnapshot
                    {
                        Service = snap.Service, Plan = snap.Plan, RateLimited = true, RetryAfter = snap.RetryAfter,
                        Error = $"rate limited; next try at {Fmt.Clock(slot.BackoffUntil)}",
                    };
                }
                else slot.Strikes = 0;
                slot.Last = snap;
                UpdateIcon(slots[i]);
            }
            details?.RefreshContents();

            // Windows 11 hides new tray icons in the overflow menu by default; on first run ask it to
            // keep ours in the corner (that is the whole point of the app).
            if (!settings.PromotedOnce && PromoteIcons())
            {
                settings.PromotedOnce = true;
                settings.Save();
            }
        }
        catch (Exception ex)
        {
            foreach (var s in slots)
            {
                s.Last = new UsageSnapshot { Service = s.Provider.Name, Error = ex.Message };
                UpdateIcon(s);
            }
        }
        finally
        {
            fetching = false;
        }
    }

    IReadOnlyList<(IUsageProvider, UsageSnapshot?)> Data() => slots.Select(s => (s.Provider, s.Last)).ToList();

    // ---- icons ------------------------------------------------------------------------------

    void RedrawAll()
    {
        foreach (var s in slots) UpdateIcon(s);
    }

    /// <summary>
    /// What the ring icon shows for a service: the digits, the ring colour and how far the arc sweeps.
    /// Shared by the tray icon and the menu header. The ring always tracks "% used" so it closes as usage
    /// fills, even when the digits show % remaining. Loading shows a dash, an error shows "!", both on an empty track.
    /// </summary>
    public static (IconRow[] rows, Color fill, double fraction) RingIcon(string name, UsageSnapshot? snap, bool showRemaining, bool light)
    {
        var fill = Palette.BadgeFill(name, light);
        var textColor = Palette.RingText(light);
        if (snap is null)
            return (new[] { new IconRow("–", textColor) }, fill, 0);
        if (snap.Error is not null || snap.Primary is null)
            return (new[] { new IconRow("!", textColor) }, fill, 0);
        var p = snap.Primary;
        string value = Math.Round(showRemaining ? p.RemainingPercent : p.UsedPercent).ToString();
        return (new[] { new IconRow(value, textColor) }, fill, p.UsedPercent / 100.0);
    }

    void UpdateIcon(Slot slot)
    {
        int size = Math.Clamp(SystemInformation.SmallIconSize.Width, 16, 64);
        var snap = slot.Last;
        string name = slot.Provider.Name;
        var (rows, fill, fraction) = RingIcon(name, snap, settings.ShowRemaining, lightTheme);
        string tip = snap is null ? $"{name}: loading..."
            : snap.Error is not null || snap.Primary is null ? $"{name}: {snap.Error ?? "no data"}"
            : Tooltip(slot.Provider, snap);

        var icon = IconRenderer.Render(size, rows, BadgeShape.Ring, fill, fraction);
        slot.Icon.Icon = icon;
        slot.Current?.Dispose();
        slot.Current = icon;
        slot.Icon.Text = tip.Length > 127 ? tip[..127] : tip; // NotifyIcon caps tooltip length
    }

    string Tooltip(IUsageProvider provider, UsageSnapshot snap)
    {
        bool rem = settings.ShowRemaining;
        string suffix = rem ? "left" : "used";
        var head = snap.Plan is null ? provider.Name : $"{provider.Name} ({snap.Plan})";
        var parts = snap.Windows.Take(3)
            .Select(w => $"{w.Label} {Math.Round(rem ? w.RemainingPercent : w.UsedPercent)}% {suffix}");
        var line1 = $"{head}: {string.Join(" · ", parts)}";
        var p = snap.Primary!;
        var line2 = p.ResetsAt is null ? "" : $"\n{p.Label} resets in {Fmt.Countdown(p.ResetsAt)}";
        return line1 + line2;
    }

    // ---- UI ---------------------------------------------------------------------------------

    void ToggleDetails()
    {
        details ??= new DetailsForm(Data, () => settings.ShowRemaining, () => _ = RefreshAllAsync(), lightTheme, _ => BadgeShape.Ring);
        if (details.Visible) details.Hide();
        else details.ShowNear(Cursor.Position);
    }

    /// <summary>
    /// Right-click menu. Top: one header row per service with its live ring, plan, windows and next reset.
    /// Then the actions, the display settings (submenus for the two-state choices) and Quit.
    /// </summary>
    void BuildMenu()
    {
        menu.Items.Clear();
        foreach (var s in slots)
            menu.Items.Add(new UsageHeaderItem(s.Provider, s.Last, settings.ShowRemaining));
        menu.Items.Add(new ToolStripSeparator());

        var newest = slots.Where(s => s.Last is not null).Select(s => s.Last!.FetchedAt).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        string refreshLabel = newest == DateTimeOffset.MinValue ? "Refresh now" : $"Refresh now   ·   updated {Fmt.Ago(newest)}";
        menu.Items.Add(MenuTheme.Item(refreshLabel, (_, _) => _ = RefreshAllAsync()));
        menu.Items.Add(new ToolStripSeparator());

        var rem = MenuTheme.Item("Show % remaining instead of % used");
        rem.Checked = settings.ShowRemaining;
        rem.CheckOnClick = true;
        rem.CheckedChanged += (_, _) => { settings.ShowRemaining = rem.Checked; settings.Save(); RedrawAll(); details?.RefreshContents(); };
        menu.Items.Add(rem);

        var icons = Submenu("Tray icons");
        var showClaude = MenuTheme.Item("Claude");
        showClaude.Checked = settings.ShowClaude; showClaude.CheckOnClick = true;
        var showCodex = MenuTheme.Item("Codex");
        showCodex.Checked = settings.ShowCodex; showCodex.CheckOnClick = true;
        showClaude.CheckedChanged += (_, _) =>
        {
            if (!showClaude.Checked && !settings.ShowCodex) { showClaude.Checked = true; return; }
            settings.ShowClaude = showClaude.Checked; settings.Save(); ApplyVisibility();
        };
        showCodex.CheckedChanged += (_, _) =>
        {
            if (!showCodex.Checked && !settings.ShowClaude) { showCodex.Checked = true; return; }
            settings.ShowCodex = showCodex.Checked; settings.Save(); ApplyVisibility();
        };
        icons.DropDownItems.Add(showClaude);
        icons.DropDownItems.Add(showCodex);
        menu.Items.Add(icons);

        var interval = Submenu("Refresh every");
        foreach (var (label, secs) in new[] { ("5 minutes", 300), ("10 minutes", 600), ("15 minutes", 900), ("30 minutes", 1800) })
        {
            var item = MenuTheme.Item(label);
            item.Checked = settings.PollSeconds == secs;
            int captured = secs;
            item.Click += (_, _) => { settings.PollSeconds = captured; settings.Save(); timer.Interval = captured * 1000; };
            interval.DropDownItems.Add(item);
        }
        menu.Items.Add(interval);

        var startup = MenuTheme.Item("Start with Windows");
        startup.Checked = IsStartupEnabled();
        startup.CheckOnClick = true;
        startup.CheckedChanged += (_, _) => SetStartup(startup.Checked);
        menu.Items.Add(startup);

        menu.Items.Add(MenuTheme.Item("Always show icons in taskbar corner", (_, _) =>
        {
            if (!PromoteIcons())
                MessageBox.Show("Windows has not registered the icons yet. Try again in a few seconds, or drag the icons out of the ^ overflow menu.",
                    "Claudex Usage", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }));

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(MenuTheme.Item("Quit", (_, _) => ExitThread()));
    }

    /// <summary>A menu entry with a submenu, styled to match the root menu (the renderer is inherited; padding and corners are not).</summary>
    static ToolStripMenuItem Submenu(string text)
    {
        var item = MenuTheme.Item(text);
        MenuTheme.Style(item.DropDown);
        return item;
    }

    // ---- system integration -----------------------------------------------------------------

    static bool IsLightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    /// <summary>
    /// Windows 11 stores each tray icon's "show in corner" flag under
    /// HKCU\Control Panel\NotifyIconSettings\{id}\IsPromoted, keyed by executable path.
    /// Returns true if at least one entry for this exe was found and set.
    /// </summary>
    static bool PromoteIcons()
    {
        try
        {
            if (Environment.ProcessPath is not string exe) return false;
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", writable: true);
            if (root is null) return false;
            bool any = false;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name, writable: true);
                if (key?.GetValue("ExecutablePath") is not string path) continue;
                if (!string.Equals(path, exe, StringComparison.OrdinalIgnoreCase)) continue;
                key.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                any = true;
            }
            return any;
        }
        catch { return false; }
    }

    static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        catch { return false; }
    }

    static void SetStartup(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is string exe) key.SetValue(RunValue, $"\"{exe}\"");
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not update startup setting:\n{ex.Message}", "Claudex Usage",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        timer.Stop();
        foreach (var s in slots)
        {
            s.Icon.Visible = false;
            s.Icon.Dispose();
            s.Current?.Dispose();
        }
        details?.Dispose();
        menu.Dispose();
        http.Dispose();
        base.ExitThreadCore();
    }
}
