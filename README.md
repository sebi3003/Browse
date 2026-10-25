# Browse

A small Chromium-based browser for Windows: a WPF (.NET 10) shell around the evergreen **WebView2** runtime, so engine and security updates arrive via Edge/WebView2, not via this app.

## Features

- Tabs: drag to reorder, middle-click or `✕` to close, favicons, lazy session restore
- Address bar with search fallback and suggestions from bookmarks + history
- Bookmarks bar: one horizontal row with folders (nested drop-downs), `☆` to add, right-click to remove; two profiles, **Work** and **Private**, that only differ in their bookmarks; import from Edge (⋮ menu)
- Bookmark sync (⋮ menu → Bookmark sync): both profiles' bookmarks, folders and order live in one JSON file (pre-filled to the OneDrive `Install Kits\2026\Browser_Bookmarks` folder); changes are written to it a couple of seconds after you make them, and a newer version from another PC replaces the local bookmarks (new ones appear, deleted ones go). The first sync with an existing file merges instead, and the replaced bookmarks are saved to `%AppData%\Browse\bookmarks-before-sync.json`
- Feeds (button right of Work/Private): hides the bookmarks, tabs and page and shows four columns — folders, channels, the merged feed of videos (Today / Week / Last 5 / All) and the YouTube page of the clicked video, which opens and starts playing in the fourth column (`Views/FeedsView`). The channel list is copied once from SebiTool's `channels.json` into `%AppData%\Browse\feeds.json`; ⋮ → *Add new feed channels from SebiTool* adds channels added there later. Every channel of a folder is listed, those with nothing in the current filter faded. The **+** next to *Folders* creates a folder; right-click a folder or channel → *Remove* (asks first). Right-click a YouTube tab → *Add channel to folder* (or *New folder…*) adds the channel that page belongs to. The channel and folder lists are part of the bookmark sync file. The player shares the browser profile (same cookies and ad blocking) and presses play itself, without changing autoplay for the tabs
- History (`Ctrl+H`), downloads (`Ctrl+J`); no password manager or autofill
- Ad/tracker blocking (HaGeZi list, cached in `%AppData%\Browse\blocklist.txt`), cookie-banner hiding, YouTube ad handling, popups only on click, notification prompts denied (`Services/AdBlocker`)
- Dark theme throughout (menus, context menus, scrollbars, title bar); starts maximized
- Find (`Ctrl+F`), zoom, DevTools (`F12`), print, HTML5 fullscreen — all built into WebView2
- Popups and `target=_blank` open as tabs (keeps `window.opener`, so OAuth sign-in works)
- Auto-update via Velopack; an update downloads in the background and `⬆` restarts into it

| Shortcut | Action |
|---|---|
| `Ctrl+T` / `Ctrl+W` / `Ctrl+Shift+T` | New / close / reopen tab |
| `Ctrl+Tab`, `Ctrl+1…9` | Switch tabs |
| `Ctrl+L`, `F6` | Focus address bar |
| `Ctrl+R`, `Ctrl+D` | Reload, bookmark |

## Layout

- `MainWindow` — tab strip, toolbar, shortcuts, one `WebView2` per tab (shown/hidden)
- `Services/BrowserStore` — bookmarks, history, session, settings in `%AppData%\Browse\browse.json`; browser profile in `%LocalAppData%\BrowseData`
- `Services/UpdateService` — Velopack + GitHub Releases feed
- `docs/SYNC-FORMAT.md` — the specification of the bookmark/feeds sync file (format 3, revision counter, rules, test files in `docs/sync-samples/`); any other Browse app (e.g. macOS) follows it
- `Services/AdBlocker`, `Services/EdgeImport` — request blocking / banner hiding, Edge bookmark import
- Crash log: `%AppData%\Browse\crash.log`

## Build / run

Requires the .NET 10 SDK and the WebView2 Runtime (preinstalled on Windows 11).

```
dotnet run
```

## Release

Velopack, published with the shared `..\Scripts\release.ps1` (`-ProjectName Browse -CsprojPath ...\Browse\Browse.csproj -RepoUrl https://github.com/sebi3003/Browse`), the same way as Picture. One-time: `dotnet tool install -g vpk`. Bump `<Version>` in `Browse.csproj` (commit `Bump version to x.y.z`), then run the script with the same `-Version`. The installer and update packages land in `..\Builds\Browse`; `-SkipUpload` builds them without publishing to GitHub.

The script writes the update token into `Services/AppConfig.Secrets.cs` (gitignored; an empty stub is included so the project builds). Installed copies check GitHub for a newer release shortly after start and offer to update; builds run from a folder never update.
