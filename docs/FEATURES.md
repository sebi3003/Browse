# Browse – behaviour list

What the Windows app does, feature by feature, with the exact rules and numbers, so the macOS app can behave the same.
The code is the truth (file names in brackets); look there when a detail is missing. Looks are in `DESIGN.md`, the sync file in `SYNC-FORMAT.md`.
Items marked **[engine]** depend on the web engine (WebView2 on Windows); on the Mac they need the WKWebView equivalent.

## 1. Window, tabs, address bar

* Starts maximized, dark title bar. The web view's background is `#0A0C10` until a page paints. Closing the window ends the process within 2 s.
* **Tabs** [MainWindow]: drag to reorder; middle-click or ✕ closes; ＋ opens a new tab. A new tab is **empty** (about:blank) with the cursor in the address bar.
  Closing the last tab of a profile closes the window. Tabs are created lazily: a restored tab loads only when first shown.
  Ctrl+T new, Ctrl+W close, Ctrl+Shift+T reopen last closed, Ctrl+Tab / Ctrl+Shift+Tab next/previous, Ctrl+1…8 jump, Ctrl+9 last.
* **Address bar**: Enter navigates. Input with a scheme (or `about:`, `file:`) is used as is; a single word with a dot (or `localhost`) gets `https://`
  (`http://` for localhost); anything else is a search (`https://www.google.com/search?q={0}`). While typing, up to 8 suggestions from bookmarks first, then
  history (every word must match title or URL; duplicates removed by URL); ↑/↓ choose, Esc closes. Ctrl+L / F6 / Alt+D focus it. ☆ (Ctrl+D) toggles a bookmark for the page.
  The tile shows the page's icon (a globe for a blank page). Ctrl+R reloads, F12 opens developer tools, F11 toggles fullscreen.
* **History**: up to 3000 entries (title, URL, time), newest first, one per URL; Ctrl+H shows recent history in the suggestions.
* **Popups**: a page can open a tab or window **only in response to a click**; scripted windows are dropped. Allowed ones open as tabs and keep their opener (sign-in popups work).
* **No password manager, no autofill.** Notification permission requests are always denied. Tracking prevention is at its strictest **[engine]**.
* **Fullscreen**: F11 hides tabs, toolbar and bookmarks (the page fills the screen); a page's own fullscreen (video) does the same. Leaving a page's fullscreen does not end an F11 fullscreen.
* **Menu ⋮**: History, Developer tools, Import bookmarks from Edge ▸, Bookmark sync ▸, Fetch bookmark icons, Add new feed channels from SebiTool,
  Import feed channels from an OPML file…, Clear browsing data (asks), version line.
* Updates: GitHub releases via Velopack; a ⬆ button appears when one is downloaded and restarts into it (the session is saved first).

## 2. Blocking, cookie banners, YouTube ads  [AdBlocker]

* **Domain blocking** **[engine: request interception]**: every request whose host (or any parent domain) is on the blocklist gets an empty 403. List: HaGeZi "Pro" domains
  (`https://raw.githubusercontent.com/hagezi/dns-blocklists/main/wildcard/pro-onlydomains.txt`, ~208 000 plain domains), cached in the app data folder, refreshed when older than 3 days,
  works offline from the cache. Plus a built-in list of cookie-consent platforms (cookiebot, onetrust, usercentrics, trustarc, didomi, sourcepoint, iubenda, …).
* **Cookie banners**: a script injected at document start hides ~50 common banner containers (by id/class patterns) and, if one was found and the page scroll is locked
  (`overflow:hidden`), restores scrolling (checked at load and 1.5 s later).
* **YouTube** (youtube.com / youtube-nocookie.com only), three layers: (1) remove `adPlacements`, `adSlots`, `playerAds`, `adBreakHeartbeatParams` from the player JSON
  (`JSON.parse`, `Response.json`, and the `ytInitialPlayerResponse` setter); (2) CSS-hide ad slots/banners/promoted items; (3) if the player has `.ad-showing`:
  mute, play at 16×, jump to the end, click the skip buttons; restore mute state and speed afterwards (poll every 250 ms).

## 3. Downloads  [MainWindow: TrackDownload, Models/DownloadItem]

* Always into the user's **Downloads** folder (asked from the OS, so a moved folder works), **no save-as prompt**; if the name exists: `name (1).ext`, `(2)`, …
* The browser's own download popup is hidden. A **card per download** slides up at the bottom: status ("Downloading" / "Downloaded" / reason), file name, percent, `done/total`, **speed**
  (bytes since the last sample over the time since, sampled every ≥ 0.5 s, smoothed 30 % new / 70 % old), time left (average rate so far; blank for the first 2 s or <1 %),
  **Cancel** (while running) and ✕ (when finished). Unknown total size: bytes so far and speed only.
* Finished cards remove themselves after 8 s, cancelled after 3 s, failed ones stay until dismissed; removal slides down.

## 4. Cache chip  [Services/CacheService]

* Shows the size of the engine's caches (page cache, code cache, GPU/shader caches, service-worker script cache and Cache API storage), unit by size: `B / KB / MB / GB`,
  one decimal below 100. Re-measured every 5 s and after each page load.
* **One click clears it, no question**: the engine's own cache clear (disk cache + cache storage), then deletes leftover cache files that are not locked.
  **Kept**: cookies, logins, history, local storage, bookmarks, all page icons.

## 5. Profiles: Work and Private

* One toggle (click flips). They differ in **bookmarks** and **open tabs**; cookies, history, cache, downloads are shared.
* Switching parks the tabs of the profile being left (their pages stay loaded, hidden) and shows the other profile's; a profile's tabs are restored the first time it is shown
  in a session. Both profiles' tabs are saved on exit. A profile with no tabs gets one blank tab. A cut bookmark stays cut across a switch (so it can be moved between profiles).
* The chosen profile is remembered. The address bar's text is refreshed when switching.

## 6. Bookmarks  [Services/BrowserStore, MainWindow]

* **Model**: a tree per profile; a node is a link (`Title`, `Url`) or a folder (`Title`, `Children`). Star adds/removes the current page at the top level (removal by URL anywhere in the tree).
* **Bar**: chips wrap onto more rows (max 4 visible, then it scrolls). Empty bar shows a hint. Click a link opens it in the current tab; a folder opens its panel.
* **Folder panels**: opens on click, or on hovering a folder ≥ 250 ms; while one is open, moving to another folder switches at once, moving onto a plain bookmark closes it. Subfolders fly out
  to the right on hover (180 ms grace so the pointer can cross other rows). Left-click a link opens and closes; **middle-click opens in a new tab**. The panels close when the pointer has been away
  ~0.6 s, on a click elsewhere, or when the app loses focus.
* **Right-click** a bookmark or folder (bar or panel): icon bar at the pointer — delete (**asks first**; a folder says how many bookmarks go with it), cut, paste (on folders, only when something is cut,
  never into itself), open in new tab, copy link (links only). Right-click an empty spot of the bar: paste to the top level. A hint on the bar shows what is cut (click to cancel).
* **Drag and drop**: drag any chip or panel row. Over a link: left half = before, right half = after; over a folder chip: left quarter before, right quarter after, **middle = into the folder**
  (panels use the same with top/bottom). Never a folder into itself. Works between the bar and panels, and shows a white marker where it will land.
* **Icons** [FaviconService]: a PNG per host (host without `www.`) in the app data folder. Taken from every page you visit (the engine reports the icon), and fetched in the background for bookmarks
  that lack one: the page's `<link rel=icon>` (plain icons first, touch icons after, SVG skipped), then `/favicon.ico`; 8 in parallel, 8 s timeout; images decoded, a frame near 32 px chosen,
  ≥ 8 px, scaled to ≤ 64 px, stored as PNG. A host that failed is not retried for 14 days (menu "Fetch bookmark icons" retries all). Shown 16×16 in the tile; globe until fetched.
* **Import from Edge** [EdgeImport]: reads Edge's profile `Bookmarks` files (works while Edge runs); the menu lists each Edge profile with its link count; imports into the **current** profile,
  keeping folders and skipping URLs already there (Edge's "other"/"mobile" roots become folders).

## 7. Bookmark sync  [BookmarkSyncService] — see `SYNC-FORMAT.md`

⋮ ▸ Bookmark sync ▸ *Sync with the default file* / *Choose another file…* / *Sync now* / *Turn sync off*, with a status line (path and last result). The path is stored per machine.

## 8. Feeds page  [Views/FeedsView, Services/FeedServices, Services/YouTubeChannelService]

* **Data**: channels (`Title`, `XmlUrl`, `HtmlUrl`, `Folder`, `ThumbnailUrl`) and an ordered list of folder names (empty ones too) in `feeds.json`; also remembers the last folder and filter (per machine).
  First run: the channel list is copied from SebiTool's `channels.json` if it exists (⋮ ▸ *Add new feed channels from SebiTool* adds later ones).
* **Opening the page** hides the bookmarks, tabs and page. Picking a folder fetches the Atom feed of **every channel in it** (8 in parallel, 15 s timeout; cached 10 minutes; YouTube Shorts left out),
  merged and sorted by date. A channel whose feed fails shows "feed unavailable".
* **Filters** (default *Week*): *Today* (published on today's local date), *Week* (last 7 days), *Last 5* (the 5 newest of each channel), *All*; *Refresh* clears the cache and reloads. Every channel of the folder
  is listed (those with nothing in the filter faded, "no videos in this filter"). Picking a channel narrows the list to it; clicking it again shows the whole folder. A picked channel with nothing in the
  filter shows its latest 5 ("nothing in this filter - latest 5").
* **Playing**: picking a video opens `https://www.youtube.com/watch?v=<id>` in the fourth column (the full page; the embeddable player refuses to run as a top-level page) and **starts it**: after the page loads,
  `video.play()` is run with the "user gesture" flag, retried every 500 ms up to 24 times until it is playing **[engine: needs a way to start playback without a click]**. The player uses the same profile
  (cookies) and the same blocking as the tabs; popups from it are dropped. Leaving the Feeds page stops the video. The video's own fullscreen folds away the other columns and makes the window fullscreen.
* **Adding channels**: right-click a **YouTube tab** ▸ *Add channel to folder* ▸ a folder, or *New folder…*. The channel is the page's channel (not the video): found by asking the page
  (`movie_player.getPlayerResponse().videoDetails.channelId` on watch pages, canonical link / meta tags on channel pages), else from the address, else by reading the page's HTML (`"externalId"`, `"channelId"`, `"browseId"`);
  name from the channel's feed, large picture from the channel page's `og:image`. A channel already present (same `XmlUrl`, any case) is reported, not added twice.
* **Folders**: **＋** next to *Folders* asks for a name (dialog: OK disabled while empty, Enter confirms); an existing name (any case) is reused. Right-click a folder ▸ *Remove folder* (asks; says how many
  channels go with it); right-click a channel ▸ *Remove channel* (asks).
* **OPML import** (⋮ ▸ *Import feed channels from an OPML file…*): the dialog opens in Downloads with the newest `.opml` chosen. Only YouTube *channel* feeds (`…videos.xml?channel_id=`) are imported, only the ones
  not present (by `XmlUrl`), into the folder the file names (sub-folders collapse into their top-level folder; existing folders are reused case-insensitively, new names become folders); pictures are fetched first,
  the list is saved once, and a summary says how many were added, already there, and skipped.
* Channels and folders are part of the sync file.
