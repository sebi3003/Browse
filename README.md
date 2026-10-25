# Browse

A small Chromium-based browser for Windows: a WPF (.NET 10) shell around the evergreen **WebView2** runtime, so engine and security updates arrive via Edge/WebView2, not via this app.

## Features

- Tabs: drag to reorder, middle-click or `✕` to close, favicons, lazy session restore
- Address bar with search fallback and suggestions from bookmarks + history
- Bookmarks bar (`☆`, right-click a bookmark to remove), history (`Ctrl+H`), downloads (`Ctrl+J`)
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
- Crash log: `%AppData%\Browse\crash.log`

## Build / run

Requires the .NET 10 SDK and the WebView2 Runtime (preinstalled on Windows 11).

```
dotnet run
```

## Release

One-time: `dotnet tool install -g vpk`. Bump `<Version>` in `Browse.csproj`, then:

```
.\scripts\release.ps1 -Version 1.0.1
```

Tokens come from `GIT_TOKEN.txt` (`Publish` = read/write, never shipped; `Embedded Browse` = read-only, this repo only, injected into the exe as assembly metadata — falls back to `Embedded`). Dev and CI builds run without a token; update checks just stay off.
