# Browse sync file – format and rules (format version 3)

This is the contract between every Browse app that shares bookmarks and Feeds channels through one file in OneDrive (today the Windows
app, `Services/BookmarkSyncService.cs`; later a macOS app). **The file format and the rules below are the whole protocol.** There is no
server and no shared code, so two implementations stay compatible only if they follow this document exactly.

What is synced: both profiles' bookmarks (folders, order), the Feeds channel list and the Feeds folder list (empty folders included).
What is **not** synced: open tabs, history, cookies/logins, caches, favicons, the Feeds page's last folder/filter, window state.

---------------------------------------------------------------------------------------------------------------------------------------

## 1. The file

* One JSON file, path chosen **per machine** (OneDrive paths differ per computer). Default on the author's PCs:
  `…\OneDrive\Install Kits\2026\Browser_Bookmarks\Browse bookmarks.json`.
* UTF-8 **without** BOM. Readers must also accept a BOM.
* Written atomically: write `<name>.tmp` in the same folder, then replace the real file with it (Windows `File.Move(overwrite)`,
  macOS `rename(2)` / `FileManager.replaceItemAt`). A half-written file must never be visible under the real name.
* Property names are `PascalCase`, exactly as below. Readers should match names **case-insensitively** and **ignore unknown properties**.

### Top level

| Property      | Type                  | Required | Meaning |
|---------------|-----------------------|----------|---------|
| `Format`      | integer               | yes      | Format version of the writer: `1`, `2`, `3`. See section 2. |
| `Modified`    | string (date, UTC)    | yes      | When the writer wrote this file. See "Dates". |
| `Device`      | string                | yes      | Machine name of the writer (display only, e.g. in "Updated from TAREEN"). |
| `Work`        | array of Bookmark     | yes      | The Work profile's bookmark bar, in order. May be empty. |
| `Private`     | array of Bookmark     | yes      | The Private profile's bookmark bar, in order. May be empty. |
| `Feeds`       | array of Channel      | no       | Feeds channels. **Absent (or `null`) = "this file has no opinion about channels"**: keep the local ones. |
| `FeedFolders` | array of string       | no       | All Feeds folder names in display order, empty folders included. Absent = no opinion about folders. |
| `Revision`    | integer (≥ 0)         | no       | Counts the writes of this file. Absent or `0` = written by a version without the counter. |

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
| 3      | current                    | + `Revision`. |

* A reader must accept formats `1` … its own. Missing `Feeds`/`FeedFolders` mean "no opinion"; missing `Revision` means `0`.
* **A file with a `Format` higher than the implementation supports is read-only**: do not apply it, and **never overwrite it**. Show
  a message ("written by a newer Browse, update to sync"). Unknown properties would be lost by a rewrite, which is why this matters.
* A writer always writes its **own** current format number, and writes only properties it knows.
* An existing file that cannot be read at all (empty, damaged, not a Browse file, newer format) must **never be overwritten** by
  turning sync on: leave sync off and show the reason.

---------------------------------------------------------------------------------------------------------------------------------------

## 3. State each device keeps locally (not in the file)

| Name           | Meaning |
|----------------|---------|
| `SyncFile`     | Path of the file on this machine, or none (sync off). |
| `SyncStamp`    | The `Modified` of the file state this device last **wrote or applied**. |
| `SyncRevision` | The `Revision` of that state. |
| `localChangedAt` (memory) | UTC time of the last local edit to bookmarks or channels. |
| `dirty` (memory)          | A local edit has not been written to the file yet. |

## 4. The rules

### isNewer(file)  – is this file newer than what the device already has?

```
if file.Revision == 0 or file.Revision == SyncRevision:
    return file.Modified > SyncStamp            # legacy writer, or same revision: fall back to the time
return file.Revision > SyncRevision             # otherwise the counter decides, independent of clocks
```

### Write (push)

```
current  = read the file as it is now (none/unreadable -> treat as Revision 0)
revision = max(SyncRevision, current.Revision) + 1
modified = now (UTC)
write the file: Format = 3, Modified = modified, Device = machine name, Revision = revision,
                Work/Private = local bookmarks, Feeds = local channels, FeedFolders = local folder names
after the write: SyncStamp = modified; SyncRevision = revision; dirty = false
```

Before a push, re-read the file: **if `isNewer(file)` and `file.Modified > localChangedAt`, do not push** – another device wrote
something newer than this device's edit; let the pull apply it (the local edit loses, see conflicts).

### Pull (apply)

Triggers: file-change notification (debounce **1.5 s**), a poll every **30 s**, "sync now".

```
file = read (retry on IO errors, see section 6); if unreadable/unsupported: stop (keep local, show status)
if not isNewer(file):                    stop
if dirty and localChangedAt > file.Modified:   stop      # a newer local edit is about to be written; it wins
applyBookmarks = not (file's Work and Private both empty while local is not)      # empty-file guard
                 and file's bookmark trees differ from the local ones
applyChannels  = file.Feeds exists
                 and not (file.Feeds empty while local has channels)               # empty-file guard
                 and (file.Feeds differs from local channels
                      or (file.FeedFolders exists and differs from local folders))
if neither: SyncStamp = file.Modified; SyncRevision = file.Revision; stop     # same content, just newer
save a backup of the local data (see below)
if applyBookmarks: local Work and Private := file's (whole trees, not a merge)
if applyChannels:  local channels := file.Feeds; if file.FeedFolders exists: local folders := file.FeedFolders
SyncStamp = file.Modified; SyncRevision = file.Revision; dirty = false; cancel a pending push
refresh the UI
```

"Differs" is a structural comparison of the data (titles, urls, children, order). Compare data, not text.

### Local edit

On any local change to bookmarks, channels or folders: `localChangedAt = now; dirty = true;` schedule a push **2 s** later (a new edit
restarts the 2 s). If the push fails (file locked by OneDrive), retry after **10 s**.

### Turning sync on (first connection)

```
if the file exists:
    file = read; if unreadable/unsupported -> sync stays off (never overwrite)
    MERGE the file into the local data (nothing local is lost):
        bookmarks: for each profile, add the items of the file that are not there yet (links matched by exact Url; a folder is added
                   with only the links that are missing locally; empty results are dropped). Details that matter:
                   - "known" URLs are collected from the LOCAL tree once, before anything is added; so the same URL twice inside the
                     file counts as missing both times and is added twice;
                   - a folder of the file is NEVER merged into a local folder of the same name: it is added as another folder
                     (the pruned copy, containing only the missing links), at the end of that profile's bar;
                   - the added items keep the file's order and come after all local items.
                   (docs/sync-samples/merge/ has inputs and the exact expected result.)
        channels:  add channels whose XmlUrl (case-insensitive) is not there; folders: append names not there (case-insensitive)
write (as above), which creates the file or writes the union back
start watching: file-change notification + 30 s poll; subscribe to local edits
```

### Backup before an apply

Before local data is overwritten by a pull, the old local data is saved to **`bookmarks-before-sync.json`** in the app's data folder
(Windows `%AppData%\Browse\`): `{ "Work": [...], "Private": [...], "Feeds": [...], "FeedFolders": [...] }`. Only the latest backup is kept.

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
* A conflict copy made by OneDrive (`Browse bookmarks-<DEVICE>.json`) is **not** read or merged by this protocol.

## 7. Known limitations (and the planned next step)

* Whole-file, newest-wins. Two devices editing at the same moment lose one side's edit. Fine for one person using one machine at a time.
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
| `v99-future.json`        | `Format` 99 and unknown properties    | **Not applied and never overwritten**; status "written by a newer Browse". |
| `is-newer-cases.json`    | The `isNewer` rule: revision vs clock, legacy files, a device that never synced | Every case has an `expected` boolean; compute `isNewer(state, file)` and compare. |
| `merge/basic/`           | First-connection merge of bookmarks (`local.json` + `remote.json` -> `expected.json`) | The result must equal `expected.json` (Work and Private, order included). |
| `merge/nested-and-empty/`| Nested known links, an empty remote folder, a remote folder whose links are all known, a URL twice in the file | Same. The expected files were produced by running the Windows implementation. |
