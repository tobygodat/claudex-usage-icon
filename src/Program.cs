namespace ClaudexUsage;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Debug helper: render a sheet of sample tray icons to a PNG and exit.
        if (args.Length >= 2 && args[0] == "--render-test")
        {
            IconRenderer.RenderTestSheet(args[1]);
            return;
        }

        // Debug helper: fetch both services once and write a plain-text report (no secrets) to a file.
        if (args.Length >= 2 && args[0] == "--dump")
        {
            File.WriteAllText(args[1], DumpAsync().GetAwaiter().GetResult());
            return;
        }

        using var mutex = new Mutex(true, @"Local\ClaudexUsage", out bool first);
        if (!first) return; // already running

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }

    static async Task<string> DumpAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudexUsage/1.0");
        bool allowRefresh = Environment.GetEnvironmentVariable("CLAUDEX_NO_REFRESH") != "1";
        var sb = new System.Text.StringBuilder();
        foreach (IUsageProvider p in new IUsageProvider[] { new ClaudeProvider(), new CodexProvider() })
        {
            var s = await p.FetchAsync(http, allowRefresh, CancellationToken.None);
            sb.AppendLine($"== {p.Name} (plan: {s.Plan ?? "?"})");
            if (s.Error is not null) sb.AppendLine($"   error: {s.Error}");
            foreach (var w in s.Windows)
                sb.AppendLine($"   {w.Label,-24} {w.UsedPercent,5:0.#}% used   resets in {Fmt.Countdown(w.ResetsAt)} ({Fmt.Clock(w.ResetsAt)})");
        }
        return sb.ToString();
    }
}
