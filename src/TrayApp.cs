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
    }

    readonly AppSettings settings = AppSettings.Load();
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };
    readonly ContextMenuStrip menu = new();
    readonly System.Windows.Forms.Timer timer = new();
    readonly List<Slot> slots = new();
    readonly bool lightTheme = IsLightTaskbar();
    DetailsForm? details;
    bool fetching;

    public TrayApp()
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudexUsage/1.0");
        menu.Opening += (_, _) => BuildMenu();

        slots.Add(MakeSlot(new ClaudeProvider()));
        slots.Add(MakeSlot(new CodexProvider()));
        ApplyVisibility();
        foreach (var s in slots) UpdateIcon(s);

        // First tick fires quickly, then settles to the configured poll interval.
        timer.Interval = 300;
        timer.Tick += async (_, _) =>
        {
            timer.Interval = Math.Max(15, settings.PollSeconds) * 1000;
            await RefreshAllAsync();
        };
        timer.Start();

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

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
                if (snap.Error is not null && snap.Error.StartsWith("HTTP 429"))
                {
                    slots[i].BackoffUntil = DateTimeOffset.Now.AddMinutes(5);
                    snap = new UsageSnapshot { Service = snap.Service, Plan = snap.Plan, Error = "rate limited; retrying in 5 min" };
                }
                slots[i].Last = snap;
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

    /// <summary>Badge silhouette for the current icon style: full-bleed squares by default, logo shapes on request.</summary>
    BadgeShape ShapeFor(IUsageProvider provider) => settings.IconStyle switch
    {
        "logo" => provider.Shape,
        "plain" or "two-rows" => BadgeShape.None,
        _ => BadgeShape.Square,
    };

    void UpdateIcon(Slot slot)
    {
        int size = Math.Clamp(SystemInformation.SmallIconSize.Width, 16, 64);
        var snap = slot.Last;
        string name = slot.Provider.Name;
        var brand = Palette.Brand(name, lightTheme);
        string style = settings.IconStyle; // "square" (default) | "logo" | "plain" | "two-rows"
        bool badge = style is not ("plain" or "two-rows");
        var shape = ShapeFor(slot.Provider);
        Color? fill = null;
        IconRow[] rows;
        string tip;

        string Value(UsageWindow? w) => w is null ? "–"
            : Math.Round(settings.ShowRemaining ? w.RemainingPercent : w.UsedPercent).ToString();
        IconRow PlainRow(UsageWindow? w) => Palette.Row(Value(w), w?.UsedPercent ?? 0, brand, lightTheme);

        if (snap is null)
        {
            fill = Palette.BadgeFill(name, lightTheme);
            var dash = new IconRow("–", badge ? Color.White : brand);
            rows = style == "two-rows" ? new[] { dash, dash } : new[] { dash };
            tip = $"{name}: loading...";
        }
        else if (snap.Error is not null || snap.Primary is null)
        {
            // Error: same service colour (the tile never changes colour), just a "!" instead of a number.
            fill = Palette.BadgeFill(name, lightTheme);
            rows = new[] { new IconRow("!", badge ? Color.White : brand) };
            tip = $"{name}: {snap.Error ?? "no data"}";
        }
        else if (badge)
        {
            // Logo-shaped badge: the shape says which service, the fill colour says how close to the limit.
            var p = snap.Primary;
            var (f, textColor) = Palette.Badge(p.UsedPercent, name, lightTheme);
            fill = f;
            rows = new[] { new IconRow(Value(p), textColor) };
            tip = Tooltip(slot.Provider, snap);
        }
        else if (style == "two-rows")
        {
            // Top = 5-hour window, bottom = 7-day window. A plan with only one of them gets a single big number.
            var top = snap.FiveHour;
            var bottom = snap.SevenDay ?? (top is null ? snap.Primary : snap.Secondary);
            rows = top is null ? new[] { PlainRow(bottom) }
                 : bottom is null ? new[] { PlainRow(top) }
                 : new[] { PlainRow(top), PlainRow(bottom) };
            tip = Tooltip(slot.Provider, snap);
        }
        else
        {
            // Plain: one number, the shortest account-wide window (5-hour for Claude, weekly for a Codex plan without one).
            rows = new[] { PlainRow(snap.Primary) };
            tip = Tooltip(slot.Provider, snap);
        }

        var icon = IconRenderer.Render(size, rows, shape, fill);
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

    string Summary(Slot slot)
    {
        var snap = slot.Last;
        if (snap is null) return $"{slot.Provider.Name}: loading...";
        if (snap.Error is not null) return $"{slot.Provider.Name}: {snap.Error}";
        bool rem = settings.ShowRemaining;
        var parts = snap.Windows.Take(2)
            .Select(w => $"{w.Label} {Math.Round(rem ? w.RemainingPercent : w.UsedPercent)}% {(rem ? "left" : "used")}");
        return $"{slot.Provider.Name}: {string.Join("  ·  ", parts)}";
    }

    // ---- UI ---------------------------------------------------------------------------------

    void ToggleDetails()
    {
        details ??= new DetailsForm(Data, () => settings.ShowRemaining, () => _ = RefreshAllAsync(), lightTheme, ShapeFor);
        if (details.Visible) details.Hide();
        else details.ShowNear(Cursor.Position);
    }

    void BuildMenu()
    {
        menu.Items.Clear();
        foreach (var s in slots)
            menu.Items.Add(new ToolStripMenuItem(Summary(s)) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Refresh now", null, (_, _) => _ = RefreshAllAsync());
        menu.Items.Add("Show details", null, (_, _) => ToggleDetails());

        var open = new ToolStripMenuItem("Open usage page");
        foreach (var s in slots)
        {
            var url = s.Provider.UsagePageUrl;
            open.DropDownItems.Add(s.Provider.Name, null, (_, _) => OpenUrl(url));
        }
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());

        var rem = new ToolStripMenuItem("Show % remaining instead of % used") { Checked = settings.ShowRemaining, CheckOnClick = true };
        rem.CheckedChanged += (_, _) => { settings.ShowRemaining = rem.Checked; settings.Save(); RedrawAll(); details?.RefreshContents(); };
        menu.Items.Add(rem);

        var style = new ToolStripMenuItem("Icon style");
        foreach (var (label, key) in new[]
                 {
                     ("Solid square (orange = Claude, green = Codex)", "square"),
                     ("Logo shapes (starburst = Claude, hexagon = Codex)", "logo"),
                     ("Plain number in service colour", "plain"),
                     ("Plain, two rows: 5-hour over weekly", "two-rows"),
                 })
        {
            var item = new ToolStripMenuItem(label) { Checked = settings.IconStyle == key };
            string captured = key;
            item.Click += (_, _) => { settings.IconStyle = captured; settings.Save(); RedrawAll(); };
            style.DropDownItems.Add(item);
        }
        menu.Items.Add(style);

        var icons = new ToolStripMenuItem("Tray icons");
        var showClaude = new ToolStripMenuItem("Claude") { Checked = settings.ShowClaude, CheckOnClick = true };
        var showCodex = new ToolStripMenuItem("Codex") { Checked = settings.ShowCodex, CheckOnClick = true };
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

        var interval = new ToolStripMenuItem("Refresh every");
        foreach (var (label, secs) in new[] { ("30 seconds", 30), ("1 minute", 60), ("2 minutes", 120), ("5 minutes", 300) })
        {
            var item = new ToolStripMenuItem(label) { Checked = settings.PollSeconds == secs };
            int captured = secs;
            item.Click += (_, _) => { settings.PollSeconds = captured; settings.Save(); timer.Interval = captured * 1000; };
            interval.DropDownItems.Add(item);
        }
        menu.Items.Add(interval);

        var startup = new ToolStripMenuItem("Start with Windows") { Checked = IsStartupEnabled(), CheckOnClick = true };
        startup.CheckedChanged += (_, _) => SetStartup(startup.Checked);
        menu.Items.Add(startup);

        menu.Items.Add("Always show icons in taskbar corner", null, (_, _) =>
        {
            if (!PromoteIcons())
                MessageBox.Show("Windows has not registered the icons yet. Try again in a few seconds, or drag the icons out of the ^ overflow menu.",
                    "Claudex Usage", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => ExitThread());
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
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
