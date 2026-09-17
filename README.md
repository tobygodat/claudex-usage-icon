# Claudex Usage

A tiny Windows tray app that shows your **Claude** (Claude Code / claude.ai subscription) and
**Codex** (ChatGPT subscription) rate-limit usage as numbers in the taskbar corner, so you never
have to open `/usage` or `/status` to check.

```
 [37]   [96]     <- one number per service: % used of its shortest window
 Claude Codex    <- orange tile = Claude (rolling 5-hour), ChatGPT-green tile = Codex (weekly)
```

Each number sits on a solid tile that fills the whole tray slot: Claude terracotta (#D97757)
or the classic ChatGPT green (#10A37F). The colours are constant; they do not change with usage.

* Windows shown: Claude's 5-hour session limit, all-models weekly limit and any model-scoped
  weekly limit (e.g. "7d Fable"); Codex's account-wide window(s). Per-model promotional limits
  (Codex-Spark) and Anthropic's internal experiment keys are ignored.
* Hover an icon for a tooltip with every window and the next reset time.
* Left-click an icon for a details popup (all windows, progress bars, reset countdowns).
* Right-click for the menu: refresh, show % remaining instead of % used, icon style,
  poll interval, hide either icon, open the web usage pages, start with Windows, quit.
* `!` instead of a number means an error (hover to read it).
* **Icon style** also offers logo-inspired badges (a starburst for Claude, a hexagon for Codex),
  a plain number in the service colour, or plain two stacked numbers (5-hour over weekly) for
  plans that have both windows.

## How it gets the data

It reuses the sign-ins you already have. Nothing is sent anywhere except to the vendor that
issued the token.

| Service | Credentials file | Endpoint |
|---------|------------------|----------|
| Claude  | `%USERPROFILE%\.claude\.credentials.json` (or `%CLAUDE_CONFIG_DIR%`) | `https://api.anthropic.com/api/oauth/usage` |
| Codex   | `%USERPROFILE%\.codex\auth.json` (or `%CODEX_HOME%`) | `https://chatgpt.com/backend-api/wham/usage` |

When an access token has expired the app refreshes it with the same public OAuth refresh flow
the Claude Code and Codex CLIs use, and writes the new token back to the same file atomically, so
the CLIs keep working. Set `CLAUDEX_NO_REFRESH=1` to disable this (the icon then shows `!` until
you run the CLI, which refreshes the token itself).

Settings live in `%APPDATA%\ClaudexUsage\settings.json`.

## Build

Requires the .NET 8 SDK (`dotnet --version`).

```bash
cd src
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ../dist
```

`dist\ClaudexUsage.exe` is the whole app (needs the .NET 8 Desktop Runtime, which the SDK includes).
Run it, then use **Start with Windows** in the right-click menu to make it permanent.

## Windows 11 and hidden tray icons

Windows 11 puts new tray icons into the `^` overflow menu by default. On first run the app flips
its own icons to "always show" (the same switch as *Settings > Personalization > Taskbar > Other
system tray icons*). If they still land in the overflow, use **Always show icons in taskbar corner**
from the menu, or just drag them out of the `^` flyout onto the taskbar.

## Debug helpers

```bash
ClaudexUsage.exe --dump report.txt         # fetch once, write a plain-text report (no secrets)
ClaudexUsage.exe --render-test icons.png   # render sample icons at several sizes
```

## Download

Pre-built binaries are attached to each [GitHub Release](https://github.com/tobygodat/claudex-usage-icon/releases):

* `ClaudexUsage.exe` — small; needs the .NET 8 Desktop Runtime.
* `ClaudexUsage-standalone.exe` — larger; bundles the runtime, runs anywhere.

Releases are built by GitHub Actions whenever a `v*` tag is pushed.
