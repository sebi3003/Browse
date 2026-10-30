# Changes on Windows that the Mac app must follow

Newest first. Each entry says what changed, why, and what to do on the Mac. The full rules stay in `../docs/`; this file only points at them.

## 2026-10-29 - Sync: do not merge on every start (bug fix, **important**)

**Problem (found on Windows).** The first-connection merge ran on *every* launch. It adds every link that any sync file has and the local data lacks.
So a bookmark deleted here came back after a restart: the other devices' files (and the old `<stem>.json`) still held it, the merge re-added it, and
the own file was rewritten with it - then the other devices pulled it back too.

**Rule now** (`../docs/SYNC-FORMAT.md`, section 4, "Starting again (every later run)"):

* Merge **only** on a real first connection: the own file does not exist, or this device has never written it (`SyncRevision == 0`).
* On every later start, when the own file exists and `SyncRevision > 0`: **no merge**. Instead
  1. compare the local bookmarks with the own file; if they differ, an edit never reached the file (app closed within the 2 s push delay):
     `localChangedAt = now`, `dirty = true`, push after 2 s;
  2. start the folder watcher and the 30 s poll;
  3. run a normal **Pull** at once, to catch up with what the other devices changed while the app was closed.

**To do on the Mac.** Check `BookmarkSyncService.swift`: if the start-up path always merges all files and then writes, change it as above. Keep
`SyncRevision` and `SyncPeers` in the local settings across runs - the rule depends on them. Add a unit test: local data without link X, own file
(revision > 0) without X, a peer file with X and an already-seen revision -> after start-up X must still be absent.

Bookmarks that came back because of this bug are in the files now: delete them once more after both apps run the fixed logic.

## Since 1.0.22 (not released yet) - what else is new on Windows

All of it is described in `../docs/FEATURES.md` and `../docs/DESIGN.md`; the Mac should match these when it reaches that step of the plan:

* **Theme colours** (Settings → Theme colours…): every panel colour is editable and **synced** in the `Theme` map of the sync file (only changed keys;
  format stays 3). Rules: `../docs/SYNC-FORMAT.md` "Theme" and `docs/sync-samples/v3-theme.json` (a unit-test input: expected overrides
  `TabSelected = #8A3FD1`, `Menu = #C0E07B16`, `FolderText = #FFCC00`). Look: `../docs/DESIGN.md` section 7.
* **One sync file per device** (`<stem> - <DEVICE>.json`), already in the checklist of section 9 of `SYNC-FORMAT.md`.
* **Settings page** replaced the ⋮ menu; sections Bookmark sync, Bookmarks (fetch icons), Appearance, Updates (`DESIGN.md` section 7).
* **Tabs:** closing the last tab leaves a blank tab instead of closing the window; a reopen-closed-tab button next to the address bar (each press
  goes one further back); tab labels are green (`#3E6B52`, selected `#5A9472`), icons 24 px.
* **Tab switch effects** (Settings → Appearance; stored by name in the store, default `Burn`): `Burn`, `Fade`, `None`, `Drop`, `Slip`, `Curl`.
  All work on **screenshots** of the old and new page (taken when a tab is left), because a web view cannot be blended or animated; on the Mac use
  `WKWebView.takeSnapshot` and draw the frames in a layer above the web views. If a screenshot is missing (a tab never shown) a window-dark frame
  stands in; if the size changed or another switch started, switch plainly. Closing a tab always switches plainly. Durations:
  Fade 220 ms, Curl 750 ms, Burn 1100 ms, Drop 1250 ms, Slip 1450 ms.
  * `Burn`: both pages are random fractal height fields (terrain, sampled every 4 px); the old page sinks and the new one rises, and where the
    surfaces cross the old page chars along contour lines and burns away while the new one glows in.
  * `Fade`: plain cross-fade. `None`: instant.
  * `Drop`: the new page falls in from above like a heavy box, bounces and rocks on its bottom edge, then settles.
  * `Slip`: the old page hangs on two nails; one gives, it slips and swings, then the other gives and it falls (random side), over the new page.
  * `Curl` (page flip): the old page is turned from a lower corner like a leaf of a book (forward = lower right corner, backward = lower left,
    mirrored); the new page lies beneath, shaded by the curl. With `Curl` chosen, moving the mouse into a lower corner of the page lifts that corner
    and dragging or clicking it turns to the next/previous tab.
  Reference code: `Services/TabTransition.cs` (the rendering) and `PlayTransitionAsync` in `MainWindow.xaml.cs` (the hand-over: the old page is hidden
  only after the first frame is drawn, the new page is shown at the end, the layer stays up 3 more frames).
* **Click-jacking / popunder guard** in the ad blocker: new tabs only after a real click/key press within 1.5 s, not on an invisible overlay
  (`FEATURES.md`, section 2). Worth porting to the WKWebView user script.
* **Icons:** fetched at up to 64 px (largest candidate first, never replace a sharper icon), shown 24 px; per-monitor DPI.

## 2026-10-28 - Settings opens as a tab

On Windows the gear opens Settings as a real tab (one per profile) instead of replacing the whole window. Details in `../docs/FEATURES.md` (tabs).
Rules worth copying: the Settings tab is **not saved** in the session (and the saved selected-tab index counts only the other tabs); clicking the gear
while on it closes it; typing an address while on it opens the page in a new tab.

## 2026-10-28 - Icons of IP / localhost sites are cached per host **and port**

Site icons are cached per host; for an IPv4 address or localhost the key is now `host:port` (default ports omitted), because several apps run on one
machine. Hosts still lose a leading `www.`. Only matters if the Mac app shows icons for local servers.

## Windows only (nothing to do on the Mac)

* **Server chips** on the Work bar (the servers of the ServerLauncher app, shown by their program icon) - ServerLauncher is a Windows program.
