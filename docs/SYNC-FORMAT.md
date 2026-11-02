# Browse sync file – format and rules (format version 3)

This is the contract between every Browse app that shares bookmarks and Feeds channels through files in one OneDrive folder (the Windows
app, `Services/BookmarkSyncService.cs`, and the macOS app, `mac/Browse/BookmarkSyncService.swift`). **The file format and the rules below are the whole protocol.** There is no
server and no shared code, so two implementations stay compatible only if they follow this document exactly.

What is synced: both profiles' bookmarks (folders, order), the Feeds channel list and the Feeds folder list (empty folders included),
and the theme colours (Settings → Theme colours, see "Theme").
What is **not** synced: open tabs, history, cookies/logins, caches, favicons, the Feeds page's last folder, window state.

---------------------------------------------------------------------------------------------------------------------------------------

## 1. The files

**One file per device.** OneDrive makes conflict copies when two machines write the same file, so no file has two writers: every
device writes **only its own file** and only reads the others'. All files sit in one folder and have the same layout (below).

* The user picks one path **per machine** (OneDrive paths differ per computer), e.g.
  `…\OneDrive\Install Kits\2026\Browser_Bookmarks\Browse bookmarks.json`. Its folder and file name without `.json` (the **stem**,
  here `Browse bookmarks`) name all the files:
  * **own file**: `<stem> - <DEVICE>.json`, `DEVICE` = this machine's name with characters illegal in file names replaced by `_`
    (`Browse bookmarks - TAREEN.json`). If the picked name already is the own file, the stem is the name without ` - <DEVICE>`.
  * **other devices' files** ("peers"): every other file in the folder matching `<stem> - *.json`.
  * **legacy file**: `<stem>.json` (the single shared file of the older setup). It is read like a peer, **never written**.
  * Anything else (e.g. OneDrive conflict copies `<stem>-<DEVICE>.json`, without spaces) is ignored.
* UTF-8 **without** BOM. Readers must also accept a BOM.
* Written atomically: write `<name>.tmp` in the same folder, then replace the real file with it (Windows `File.Move(overwrite)`,
  macOS `rename(2)` / `FileManager.replaceItemAt`). A half-written file must never be visible under the real name.
* Property names are `PascalCase`, exactly as below. Readers should match names **case-insensitively** and **ignore unknown properties**.

### Top level

| Property      | Type                  | Required | Meaning |
|---------------|-----------------------|----------|---------|
| `Format`      | integer               | yes      | Format version of the writer: `1`, `2`, `3`. See section 2. |
| `Modified`    | string (date, UTC)    | yes      | When the content was last changed on the writer. A plain write: now. A write that only copies a state applied from another device keeps that state's `Modified` (see "Pull"). See "Dates". |
| `Device`      | string                | yes      | Machine name of the writer (display only, e.g. in "Updated from TAREEN"). |
| `Work`        | array of Bookmark     | yes      | The Work profile's bookmark bar, in order. May be empty. |
| `Private`     | array of Bookmark     | yes      | The Private profile's bookmark bar, in order. May be empty. |
| `Feeds`       | array of Channel      | no       | Feeds channels. **Absent (or `null`) = "this file has no opinion about channels"**: keep the local ones. |
| `FeedFolders` | array of string       | no       | All Feeds folder names in display order, empty folders included. Absent = no opinion about folders. |
| `Revision`    | integer (≥ 0)         | no       | Counts the writes of **this device's file**. Absent or `0` = written by a version without the counter. |
| `Theme`       | object (key → colour) | no       | The user's colour overrides. **Absent (or `null`) = no opinion**: keep the local colours. `{}` = all defaults. See "Theme". |

### Bookmark (a tree node)

| Property   | Type                       | Meaning |
|------------|----------------------------|---------|
| `Title`    | string                     | Shown name. |
| `Url`      | string                     | Address of a link. **`""` (empty string) for a folder.** |
| `Children` | array of Bookmark or `null`| **Non-null = folder** (may be an empty array), `null` or absent = link. |

Folders nest to any depth. Order of the array is the display order. A link has `"Children": null` (the Windows app writes the `null`
explicitly; do not rely on it being present).

### Channel (Feeds)

| Property       | Type   | Meaning |
|----------------|--------|---------|
| `Title`        | string | Channel name. |
| `XmlUrl`       | string | YouTube RSS address `https://www.youtube.com/feeds/videos.xml?channel_id=UC…`. **The identity of a channel**; compare case-insensitively. |
| `HtmlUrl`      | string or null | `https://www.youtube.com/channel/UC…` |
| `Folder`       | string or null | Folder name. `""`/`null` means the folder that is *displayed* as **`Unsorted`**. |
| `ThumbnailUrl` | string or null | Channel picture address (may be empty). |

Folder names are matched case-insensitively. The display name `Unsorted` and the stored empty string are the same folder.
The channel order in the array is the order shown within a folder.

### Theme

Only the colours the user **changed** are stored; a missing key means that part uses its default. Keys are matched case-insensitively;
unknown keys are ignored. Each app maps these names to its own setting names.

| Key               | Part (DESIGN.md §7)        | Default     |
|-------------------|----------------------------|-------------|
| `Window`          | Window background          | `#0A0C10`   |
| `ToolbarTile`     | Toolbar tiles              | `#0B2A5B`   |
| `Downloads`       | Downloads tile             | `#E0E07B16` |
| `Menu`            | Settings / menu tile       | `#802E9E4F` |
| `Tab`             | Tab label                  | `#3E6B52`   |
| `TabHover`        | Tab label, hover           | `#4A7D61`   |
| `TabSelected`     | Selected tab label         | `#5A9472`   |
| `TabText`         | Tab text                   | `#E7EDF5`   |
| `TabSelectedText` | Selected tab text          | `#FFFFFF`   |
| `TabIcon`         | Tab icon tile              | `#0B2A5B`   |
| `Bookmark`        | Bookmark label             | `#1A2332`   |
| `BookmarkHover`   | Bookmark label, hover      | `#232F42`   |
| `BookmarkText`    | Bookmark text              | `#E7EDF5`   |
| `BookmarkIcon`    | Bookmark icon tile         | `#1A2332`   |
| `Folder`          | Folder chip                | `#0B2A5B`   |
| `FolderHover`     | Folder chip, hover         | `#123A7A`   |
| `FolderText`      | Folder text                | `#FFFFFF`   |

A colour is a string `#RRGGBB` or `#AARRGGBB` (alpha **first**, as in WPF), hex digits in either case. Writers write upper case with
the `#`; readers also accept it without the `#`. An opaque colour is written as `#RRGGBB`. A value that does not parse is ignored.

Example: `"Theme": { "TabSelected": "#8A3FD1", "Menu": "#C0E07B16" }`.

### Dates

`Modified` is UTC in ISO 8601 with a `Z` suffix. The Windows app writes **seven** fractional digits
(`2026-10-25T16:16:07.5361211Z`), other writers may write fewer (≥ milliseconds recommended). **Readers must accept 0–7 fractional
digits.** Apple's `ISO8601DateFormatter` rejects more than 3 digits in some OS versions: truncate the fraction to 3 digits before
parsing, or parse by hand. Compare dates only with dates produced by the same rule (never with local time).

### Escaping

Standard JSON. The Windows app escapes `&`, `<`, `>` and `'` as `&` etc. inside strings: this is valid JSON and decodes to the
plain character; any JSON parser handles it. Writers need not escape them.

---------------------------------------------------------------------------------------------------------------------------------------

## 2. Versions and compatibility

| Format | Written by                 | Content |
|-------:|----------------------------|---------|
| 1      | first Windows version      | `Work`, `Private` only. |
| 2      | Windows, after Feeds sync  | + `Feeds`, `FeedFolders`. |
| 3      | current                    | + `Revision`; later + `Theme` and one file per device (no layout change inside a file, see below). |

* A reader must accept formats `1` … its own. Missing `Feeds`/`FeedFolders` mean "no opinion"; missing `Revision` means `0`.
* **A file with a `Format` higher than the implementation supports is read-only**: do not apply it, and **never overwrite it**. Show
  a message ("written by a newer Browse, update to sync"). Unknown properties would be lost by a rewrite, which is why this matters.
* A writer always writes its **own** current format number, and writes only properties it knows.
* `Theme` was added **without** a format bump on purpose: it is optional, "absent" means "no opinion", and losing it is harmless.
  A format-3 app that does not know it simply drops it when it rewrites the file; the other apps then keep their local colours
  until it is written again. (A bump to 4 would have made every older app treat the file as read-only.)
* One file per device needed **no** format bump: the layout of a file is unchanged. An older app that still uses the single
  `<stem>.json` keeps working (it is read as the legacy file) but does not see the per-device files, so it should be updated.
* An existing **own** file that cannot be read at all (empty, damaged, not a Browse file, newer format) must **never be overwritten**
  by turning sync on: leave sync off and show the reason. An unreadable **peer** file is only skipped (and retried at the next check).

---------------------------------------------------------------------------------------------------------------------------------------

## 3. State each device keeps locally (not in the file)

| Name           | Meaning |
|----------------|---------|
| `SyncFile`     | Path of the file on this machine, or none (sync off). |
| `SyncStamp`    | The `Modified` this device last wrote to its own file. |
| `SyncRevision` | The `Revision` of its own file as it last wrote it. |
| `SyncPeers`    | Per peer file (key = lower-case file name): `{ Modified, Revision }` of the state this device last **applied, found equal, or discarded** from it. |
| `localChangedAt` (memory) | UTC time of the last local edit to bookmarks or channels. |
| `dirty` (memory)          | A local edit has not been written to the file yet. |

## 4. The rules

### isNewer(file)  – is this peer file newer than what the device already took from it?

```
seen = SyncPeers[file's name]
if seen is none:                                return true      # never seen: newer
if file.Revision == 0 or file.Revision == seen.Revision:
    return file.Modified > seen.Modified        # legacy writer, or same revision: fall back to the time
return file.Revision > seen.Revision            # otherwise the counter decides, independent of clocks
```

Each peer is judged against its **own** history, so the clocks of different devices are never compared here; they only meet in the
"newest edit wins" rule below.

### Write (push)

```
current  = read this device's OWN file as it is now (none/unreadable -> treat as Revision 0)
revision = max(SyncRevision, current.Revision) + 1
modified = now (UTC)
write the OWN file: Format = 3, Modified = modified, Device = machine name, Revision = revision,
                Work/Private = local bookmarks, Feeds = local channels, FeedFolders = local folder names,
                Theme = local colour overrides (if the app supports themes; `{}` when nothing is changed)
after the write: SyncStamp = modified; SyncRevision = revision; dirty = false
```

Before a push, re-read the peer files: **if any peer file is `isNewer` and its `Modified > localChangedAt`, do not push** – another
device wrote something newer than this device's edit; let the pull apply it (the local edit loses, see conflicts). Newer peer files
that are older than the edit lose against it: record them in `SyncPeers` (so they are not applied later), then push.

### Pull (apply)

Triggers: file-change notification for a peer file, not the own one (debounce **1.5 s**), a poll every **30 s**, "sync now".

```
fresh = every readable peer file with isNewer(file)     # read with retries (section 6); unreadable/unsupported ones are skipped, status shown
if fresh is empty:                       stop
file  = the one in fresh with the latest Modified        # whole-file, newest wins; the others in fresh lose
if dirty and localChangedAt > file.Modified:   record all of fresh in SyncPeers; stop   # a newer local edit is about to be written; it wins
applyBookmarks = not (file's Work and Private both empty while local is not)      # empty-file guard
                 and file's bookmark trees differ from the local ones
applyChannels  = file.Feeds exists
                 and not (file.Feeds empty while local has channels)               # empty-file guard
                 and (file.Feeds differs from local channels
                      or (file.FeedFolders exists and differs from local folders))
applyTheme     = file.Theme exists and differs from the local overrides            # no empty guard: {} = "all defaults"
if none: record all of fresh in SyncPeers; stop                               # same content, just newer
save a backup of the local data (see below)
if applyBookmarks: local Work and Private := file's (whole trees, not a merge)
if applyChannels:  local channels := file.Feeds; if file.FeedFolders exists: local folders := file.FeedFolders
if applyTheme:     local overrides := file.Theme (keys and values normalised; unknown keys dropped); repaint
record all of fresh in SyncPeers; dirty = false; cancel a pending push
refresh the UI
write the OWN file as a copy of the new local state, with Modified = file.Modified        # every file stays a full snapshot (section 6)
```

"Differs" is a structural comparison of the data (titles, urls, children, order). Compare data, not text.

### Local edit

On any local change to bookmarks, channels, folders or theme colours: `localChangedAt = now; dirty = true;` schedule a push **2 s** later (a new edit
restarts the 2 s). If the push fails (file locked by OneDrive), retry after **10 s**.

### Starting again (every later run)

If the own file exists and `SyncRevision > 0` (this device wrote it in an earlier run), the app **does not merge**: a merge would bring back
everything deleted here since, because the peers' files still hold it. Instead it compares the local bookmarks with the own file (a difference
= an edit that never reached the file: `localChangedAt = now`, `dirty = true`, push after 2 s), starts watching, and runs a normal **Pull** at once.

### Turning sync on (first connection)

```
files = the own file (if it exists it must be readable, else sync stays off: never overwrite)
        + every readable peer file and the legacy file
if there are files:
    MERGE every file, newest Modified first, into the local data (nothing local is lost); then record each peer in SyncPeers:
        bookmarks: for each profile, add the items of the file that are not there yet (links matched by exact Url; a folder is added
                   with only the links that are missing locally; empty results are dropped). Details that matter:
                   - "known" URLs are collected from the LOCAL tree once, before anything is added; so the same URL twice inside the
                     file counts as missing both times and is added twice;
                   - a folder of the file is NEVER merged into a local folder of the same name: it is added as another folder
                     (the pruned copy, containing only the missing links), at the end of that profile's bar;
                   - the added items keep the file's order and come after all local items.
                   (docs/sync-samples/merge/ has inputs and the exact expected result.)
        channels:  add channels whose XmlUrl (case-insensitive) is not there; folders: append names not there (case-insensitive)
        theme:     add the file's colours for keys this device has not changed (a local change wins)
write (as above), which creates the own file or writes the union back
start watching: file-change notification for the folder + 30 s poll; subscribe to local edits
```

### Backup before an apply

Before local data is overwritten by a pull, the old local data is saved to **`bookmarks-before-sync.json`** in the app's data folder
(Windows `%AppData%\Browse\`): `{ "Work": [...], "Private": [...], "Feeds": [...], "FeedFolders": [...], "Theme": {...} }`. Only the latest
backup is kept.

### Conflicts (both sides edited)

Resolved per whole file, newest wins: the edit with the later time (`localChangedAt` vs `file.Modified`) survives, the other is lost.
The revision counter does not change that; it makes *ordering* reliable when clocks differ, and the time is only the tie-breaker for a
true concurrent edit (two devices that wrote on top of the same revision). Both then write revision + 1 from the same base.

---------------------------------------------------------------------------------------------------------------------------------------

## 5. Guards, summarised

1. Never overwrite a file that cannot be read, is empty, or has a newer `Format`.
2. Never let an **empty** `Work`+`Private` (or an empty `Feeds`) from the file wipe non-empty local data (a glitch or a half-synced file).
3. A file without `Feeds` does not touch the local channels.
4. Always write through a temp file and replace.
5. Back up local data before an apply.

## 6. Reading the file (OneDrive)

* OneDrive may keep the file online-only ("Files On-Demand"); the first read can take a moment. Retry **4 times, 700 ms apart** on I/O
  errors (file locked or still downloading). A parse error is not retried: report it and try again at the next trigger.
* Because no file has two writers, OneDrive should no longer create conflict copies. One that still appears
  (`Browse bookmarks-<DEVICE>.json`, no spaces) is **not** read or merged by this protocol.
* **Every file is a full snapshot.** After a pull applied another device's state, the own file is rewritten with it (keeping that
  state's `Modified`, so it does not look like a newer edit); otherwise it would keep old content, and a device joining later would
  merge it and bring back deleted items.

## 7. Known limitations (and the planned next step)

* Whole-file, newest-wins over all the files. Two devices editing at the same moment lose one side's edit (without a conflict copy now).
  Fine for one person using one machine at a time.
* The file of a device that is no longer used stays until it is deleted by hand (harmless: it is never newer than what was applied).
* No per-item identity, so a deletion cannot be told from "not yet synced" except through the whole-file rule above.
* Planned (format 4, if needed): an `Id` per bookmark/channel plus tombstones for deletions, for a real three-way merge.

## 8. Test files

`docs/sync-samples/` holds small files that exercise each rule. An implementation should load every one without crashing and behave
as the table says.

| File                     | What it checks | Expected |
|--------------------------|----------------|----------|
| `v3-full.json`           | Everything: nested folders, both profiles, channels, empty folder, `Unsorted`, 7-digit date, `&` | Loads; `Work` has 2 top-level items (link + folder `Dev` containing a link and folder `Nested`); `Private` has 1 link; 3 channels; folders in order `Lets Play, News, Empty folder, Unsorted`. |
| `v2-no-revision.json`    | No `Revision`                         | Loads as revision 0; compared by `Modified`. |
| `v1-bookmarks-only.json` | No `Feeds`/`FeedFolders`              | Loads; local channels and folders are left alone. |
| `v3-empty-lists.json`    | Empty `Work`, `Private`, `Feeds`      | If the device has local data: **not applied** (empty-file guard). On a device with no data: applied. |
| `v3-theme.json`          | `Theme` with an 8-digit colour, a 6-digit colour without `#`, lower case, an unknown key and a bad value | Loads; theme overrides are exactly `TabSelected = #8A3FD1`, `Menu = #C0E07B16`, `FolderText = #FFCC00`; `Sparkles` and `Bookmark` (bad value) are ignored. |
| `v99-future.json`        | `Format` 99 and unknown properties    | **Not applied and never overwritten**; status "written by a newer Browse". |
| `is-newer-cases.json`    | The `isNewer` rule: revision vs clock, legacy files, a device that never synced | Every case has an `expected` boolean; compute `isNewer(state, file)` and compare. |
| `merge/basic/`           | First-connection merge of bookmarks (`local.json` + `remote.json` -> `expected.json`) | The result must equal `expected.json` (Work and Private, order included). |
| `merge/nested-and-empty/`| Nested known links, an empty remote folder, a remote folder whose links are all known, a URL twice in the file | Same. The expected files were produced by running the Windows implementation. |

---------------------------------------------------------------------------------------------------------------------------------------

## 9. Checklist for the macOS app (moving from the single file to one file per device)

The Windows app (1.0.13 and later, one file per device) already works this way. The macOS app must change **how it finds, reads and
writes files**; the content of a file stays exactly as in section 1.

1. **Own file.** Write only `<stem> - <DEVICE>.json` next to the chosen file (`stem` = chosen file name without `.json`, `DEVICE` = the
   Mac's name, e.g. `MacBook Air von Sebi`, illegal characters such as `/` and `:` replaced by `_`). Never write `<stem>.json` again.
2. **Peers.** Read every other `<stem> - *.json` in the folder (the Windows PCs' files) and also `<stem>.json` (legacy, read-only).
   Ignore the own file and anything that does not match (OneDrive conflict copies are `<stem>-<DEVICE>.json`, without spaces).
3. **State.** Keep one `{ Modified, Revision }` per peer file name (lower case) in the app's local settings (`SyncPeers`, section 3).
   `SyncStamp`/`SyncRevision` now describe only the own file. Use the per-peer `isNewer` of section 4; do not compare revisions between files.
4. **Pull.** Collect the peers that are newer, apply the one with the latest `Modified`, record all of them in `SyncPeers`, then copy the
   applied state into the own file with `Modified` = the applied file's `Modified` (section 4, "Pull"). A newer local edit wins instead: record the peers and push.
5. **Push.** Before writing, check the peers: a newer peer that is newer than the local edit means pull first (section 4, "Write").
6. **First connection.** Merge the own file (if any), all peers and the legacy file, newest first, then write the own file (section 4).
7. **Watching.** Watch the folder; react to peer files only, not to the own file. Keep the 30 s poll and the 1.5 s debounce.
8. **Theme.** Read and write `Theme` as in section 1 ("Theme"); the Windows app already writes it.
9. **Cleanup.** Once both apps use per-device files, the old `<stem>.json` and the `…-MacBook Air von Sebi.*` conflict copies can be deleted by hand.

Until the Mac app is changed it keeps writing `<stem>.json`: the Windows PCs still read it (as a peer), but the Mac does not see the Windows files.
