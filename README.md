# Browse

A small Chromium-based browser for Windows: a WPF (.NET 10) shell around the evergreen **WebView2** runtime, so engine and security updates arrive via Edge/WebView2, not via this app.

## Features

- Tabs: drag to reorder, middle-click or `✕` to close, favicons, lazy session restore
- Address bar with search fallback and suggestions from bookmarks + history
- Bookmarks bar: one horizontal row with folders (nested drop-downs), `☆` to add, right-click to remove; two profiles, **Work** and **Private**, that only differ in their bookmarks; import from Edge
- Bookmark sync (gear → Settings → Bookmark sync): both profiles' bookmarks, folders and order (plus Feeds channels and your theme colours) live in JSON files in one folder (pre-filled to the OneDrive `Install Kits\2026\Browser_Bookmarks` folder); every PC writes only its own file (`Browse bookmarks - <PC name>.json`, so OneDrive never makes conflict copies) a couple of seconds after you make a change, and a newer version from another PC's file replaces the local bookmarks (new ones appear, deleted ones go). The first sync with an existing file merges instead, and the replaced bookmarks are saved to `%AppData%\Browse\bookmarks-before-sync.json`
- Feeds (button right of Work/Private): hides the bookmarks, tabs and page and shows three columns — folders, channels and YouTube: clicking a channel opens its YouTube videos page in the third column, where you pick and play videos (`Views/FeedsView`). It no longer reads YouTube's RSS feeds (Google stopped them); Feeds only manages, shows and hides your channels. The channel list is copied once from SebiTool's `channels.json` into `%AppData%\Browse\feeds.json`; *Add new feed channels from SebiTool* adds channels added there later. Every channel of a folder is listed; clicking the selected channel again returns to its page. The **+** next to *Folders* creates a folder; right-click a folder or channel → *Remove* (asks first). Right-click a YouTube tab → *Add channel to folder* (or *New folder…*) adds the channel that page belongs to. The channel and folder lists are part of the bookmark sync file. The player shares the browser profile (same cookies and ad blocking) and presses play itself on watch pages, without changing autoplay for the tabs
- History (`Ctrl+H`), downloads (`Ctrl+J`); no password manager or autofill
- Ad/tracker blocking (HaGeZi list, cached in `%AppData%\Browse\blocklist.txt`), cookie-banner hiding, YouTube ad handling, popups only on click, notification prompts denied, click-jacking / popunder guard that blocks ad-opened tabs and passes the click on to the real element (`Services/AdBlocker`)
- Dark theme throughout (menus, context menus, scrollbars, title bar); starts maximized. Every panel colour can be changed in **Settings → Theme colours…** (a miniature browser: click a part, set its colour with the wheel, sliders or a hex code; applied live and saved in `browse.json`)
- Settings page (gear button, right of Downloads; opens as a tab, click again or `Ctrl+W` to close it): bookmark sync, fetch bookmark icons, theme colours, version and *Check for updates*
- More tab switch effects (Settings): Drop (new page falls in like a heavy box, bounces, rocks), Slip (old page hangs on two nails, one gives, it swings and falls), Page flip (book-style curl; with it chosen, moving into a lower corner of the page lifts that corner and dragging or clicking it turns to the next/previous tab)
- Settings: tab effect now has an Apply button (picking only selects) and a Random option (a different effect each switch, never None). Delete/remove confirmations use a dark dialog (`Views/ConfirmWindow`)
- Tab switch effect: the old page burns away into the new one (a fractal height field, run on screenshots of both pages, ~1.1 s; a new tab plays it too, from a dark frame; closing a tab switches plainly). Closing the last tab leaves the app open on a blank tab (`Services/TabTransition`)
- Sharp on scaled screens: per-monitor DPI awareness (`app.manifest`), 64 px site icons shown at 24 px, icon fetching prefers the largest icon and never replaces a sharper one
- Find (`Ctrl+F`), zoom, DevTools (`F12`), print, HTML5 fullscreen — all built into WebView2
- Popups and `target=_blank` open as tabs (keeps `window.opener`, so OAuth sign-in works)
- Auto-update via Velopack; an update downloads in the background and `⬆` restarts into it

| Shortcut | Action |
|---|---|
| `Ctrl+T` / `Ctrl+W` / `Ctrl+Shift+T` | New / close / reopen tab (the ↶ button right of the address bar does the same; each press goes one closed page further back) |
| `Ctrl+Tab`, `Ctrl+1…9` | Switch tabs |
| `Ctrl+L`, `F6` | Focus address bar |
| `Ctrl+R`, `Ctrl+D` | Reload, bookmark |

## Layout

- `MainWindow` — tab strip, toolbar, shortcuts, one `WebView2` per tab (shown/hidden)
- `Services/BrowserStore` — bookmarks, history, session, settings in `%AppData%\Browse\browse.json`; browser profile in `%LocalAppData%\BrowseData`
- `Services/UpdateService` — Velopack + GitHub Releases feed
- `Views/SettingsView`, `Views/ThemeWindow`, `Services/ThemeService` — Settings page, theme editor, the editable colour brushes (overrides in the store's `Theme` map)
- `Services/TabTransition` — tab switch effect
- `MainWindow.Servers` — icon-only chips at the far left of the Work bookmark bar for the IP/localhost bookmarks (servers) that answer right now, CustomerSuccess Hub first
- `.github/workflows/build.yml` — CI: restore and Release build on push/PR to `main`
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
