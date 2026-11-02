using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace Browse.Services
{
    /// <summary>
    /// Keeps both profiles' bookmarks (folders and order included), the Feeds channel list and the theme colours the same on several
    /// PCs through JSON files in a shared folder, for example in OneDrive. Every device writes only its own file
    /// ("Browse bookmarks - DEVICE.json") and reads the files of the others, so no file ever has two writers and OneDrive never makes
    /// conflict copies. Whenever a newer version arrives from another device it replaces the local bookmarks and channels (new ones
    /// appear, deleted ones go); every local change is written to this device's file a couple of seconds later. When several sides
    /// changed, the newest edit wins. The very first sync merges all existing files instead, so nothing local is lost. Before a remote
    /// version overwrites the local data, the local data is saved to bookmarks-before-sync.json. Which folder or filter the Feeds page
    /// last showed stays per PC. The single shared file of the older setup ("Browse bookmarks.json") is still read, as another device,
    /// but never written.
    /// </summary>
    public sealed class BookmarkSyncService : IDisposable
    {
        // Feeds is missing in files written before channels were synced: such a file says nothing about channels, so local ones are kept.
        // The format written by this version. A file with a higher number was written by a newer Browse: it is read-only here, never overwritten.
        // Revision counts the writes of the file (0 = written by a version that had no counter). The format is specified in docs/SYNC-FORMAT.md.
        private const int FormatVersion = 3;

        private sealed record SyncFile(int Format, DateTime Modified, string Device, List<Bookmark> Work, List<Bookmark> Private,
                                       List<FeedStore.StoredChannel>? Feeds = null, List<string>? FeedFolders = null, long Revision = 0,
                                       Dictionary<string, string?>? Theme = null);

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
        private static readonly string BackupPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Browse", "bookmarks-before-sync.json");

        private readonly BrowserStore _store;
        private readonly Dispatcher _ui;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Timer _pushTimer;   // waits a moment after the last local edit
        private readonly Timer _pullTimer;   // waits a moment after a file changed (OneDrive writes it in several steps)
        private readonly Timer _pollTimer;   // safety net in case a file-change notification is missed
        private FileSystemWatcher? _watcher;
        private string _dir = "", _stem = "", _path = "", _legacyPath = "";   // _path: this device's own file
        private volatile bool _running, _dirty;
        private DateTime _localChangedAt;    // when bookmarks or channels were last edited here (UTC)

        public BookmarkSyncService(BrowserStore store, Dispatcher ui)
        {
            _store = store;
            _ui = ui;
            _pushTimer = new Timer(_ => _ = PushAsync(), null, Timeout.Infinite, Timeout.Infinite);
            _pullTimer = new Timer(_ => _ = PullAsync(), null, Timeout.Infinite, Timeout.Infinite);
            _pollTimer = new Timer(_ => _ = PullAsync(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public string Status { get; private set; } = "Off";
        public bool IsRunning => _running;
        /// <summary>This device's own sync file; the other devices' files sit next to it.</summary>
        public string FilePath => _path;

        /// <summary>A remote version replaced local bookmarks or channels (raised on a background thread).</summary>
        public event Action? Applied;

        /// <param name="path">The shared file chosen in Settings, e.g. ...\Browse bookmarks.json: its folder and name give the files of all devices.</param>
        public async Task StartAsync(string path)
        {
            Stop();
            _dir = Path.GetDirectoryName(path)!;
            _stem = Path.GetFileNameWithoutExtension(path);
            string own = " - " + SafeName(Environment.MachineName);
            if (_stem.EndsWith(own, StringComparison.OrdinalIgnoreCase)) _stem = _stem[..^own.Length];   // a device's own file was picked
            _path = Path.Combine(_dir, _stem + own + ".json");
            _legacyPath = Path.Combine(_dir, _stem + ".json");
            Status = "Connecting…";
            bool resumed = false;
            await _gate.WaitAsync();
            try
            {
                Directory.CreateDirectory(_dir);
                // This device's own file (after a reinstall it still holds this device's data) must be readable: it is never overwritten otherwise.
                var sources = new List<(string Key, SyncFile File)>();
                SyncFile? ownFile = null;
                if (File.Exists(_path))
                {
                    if (await ReadAsync(_path) is not { } mine) return;
                    ownFile = mine;
                    sources.Add((Key(_path), mine));
                }
                // Resuming: this PC already wrote its file in an earlier run. Merging again would bring back everything deleted here
                // since, because the other devices' files still hold it; the normal pull below catches up with their changes instead.
                if (ownFile != null && _store.SyncRevision > 0)
                {
                    bool unsaved = await _ui.InvokeAsync(() => Serialize(_store.Bookmarks, _store.PrivateBookmarks) != Serialize(ownFile.Work, ownFile.Private));
                    if (unsaved) { _localChangedAt = DateTime.UtcNow; _dirty = true; }   // an edit made just before the last exit never reached the file
                    resumed = true;
                }
                else
                {
                    foreach (var p in PeerPaths())
                        if (await ReadAsync(p, quiet: true) is { } f) sources.Add((Key(p), f));
                    // first contact: keep everything local and add what the files have, newest first
                    await _ui.InvokeAsync(() =>
                    {
                        foreach (var (_, remote) in sources.OrderByDescending(x => x.File.Modified))
                        {
                            _store.MergeBookmarks(remote.Work, remote.Private);
                            if (remote.Feeds != null) FeedStore.MergeChannels(remote.Feeds, remote.FeedFolders);
                            if (remote.Theme != null) ThemeService.MergeFromSync(remote.Theme);
                        }
                        MarkSeen(sources.Where(x => x.Key != Key(_path)));
                    });
                    await WriteAsync();                               // creates this device's file, or writes the union back
                }
            }
            catch (Exception ex) { Status = "Error: " + ex.Message; return; }
            finally { _gate.Release(); }

            _running = true;
            _store.BookmarksChanged += OnLocalChange;
            FeedStore.ChannelsChanged += OnLocalChange;
            ThemeService.Changed += OnLocalChange;
            _watcher = new FileSystemWatcher(_dir, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler changed = (_, e) => { if (IsPeerName(e.Name)) _pullTimer.Change(1500, Timeout.Infinite); };
            _watcher.Changed += changed;
            _watcher.Created += changed;
            _watcher.Renamed += (_, e) => { if (IsPeerName(e.Name)) _pullTimer.Change(1500, Timeout.Infinite); };
            _pollTimer.Change(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            if (resumed)
            {
                Status = $"In sync ({DateTime.Now:HH:mm:ss})";
                _pullTimer.Change(0, Timeout.Infinite);                       // catch up with what the other devices changed meanwhile
                if (_dirty) _pushTimer.Change(2000, Timeout.Infinite);
            }
        }

        public void Stop()
        {
            _running = false;
            _store.BookmarksChanged -= OnLocalChange;
            FeedStore.ChannelsChanged -= OnLocalChange;
            ThemeService.Changed -= OnLocalChange;
            _watcher?.Dispose();
            _watcher = null;
            _pushTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _pullTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _dirty = false;
            Status = "Off";
        }

        /// <summary>Checks the other devices' files now, and writes local edits that are not in this device's file yet.</summary>
        public async Task SyncNowAsync()
        {
            if (!_running) return;
            await PullAsync();
            if (_dirty) await PushAsync();
        }

        private void OnLocalChange()
        {
            _localChangedAt = DateTime.UtcNow;
            _dirty = true;
            _pushTimer.Change(2000, Timeout.Infinite);
        }

        private async Task PushAsync()
        {
            if (!_running) return;
            await _gate.WaitAsync();
            try
            {
                // Another device may have written since this one last looked. If its version is newer than this edit, leave it to the
                // pull to apply; an older one loses against this edit and is skipped.
                var fresh = await FreshPeersAsync();
                if (fresh.Any(p => p.File.Modified > _localChangedAt))
                {
                    _pullTimer.Change(0, Timeout.Infinite);
                    return;
                }
                await _ui.InvokeAsync(() => MarkSeen(fresh));
                await WriteAsync();
                _dirty = false;
            }
            catch (Exception ex)
            {
                Status = "Could not write the file (retrying): " + ex.Message;
                _pushTimer.Change(10_000, Timeout.Infinite); // typically OneDrive holding the file for a moment
            }
            finally { _gate.Release(); }
        }

        private async Task PullAsync()
        {
            if (!_running) return;
            await _gate.WaitAsync();
            try
            {
                var fresh = await FreshPeersAsync();
                if (fresh.Count == 0) return;                                    // nothing newer than what this PC already has
                var remote = fresh.OrderByDescending(p => p.File.Modified).First().File;
                if (_dirty && _localChangedAt > remote.Modified)                 // a newer local edit is about to be written: it wins
                {
                    await _ui.InvokeAsync(() => MarkSeen(fresh));
                    return;
                }

                // What differs? Compared on this PC's data (read on the UI thread, where it is edited).
                var local = await _ui.InvokeAsync(() => (
                    Bookmarks: Serialize(_store.Bookmarks, _store.PrivateBookmarks),
                    BookmarksEmpty: _store.Bookmarks.Count == 0 && _store.PrivateBookmarks.Count == 0,
                    Channels: FeedStore.LoadChannels(),
                    Folders: FeedStore.LoadFolderNames(),
                    Theme: ThemeService.ToSync()));

                bool remoteBookmarksEmpty = remote.Work.Count == 0 && remote.Private.Count == 0;
                bool applyBookmarks = !(remoteBookmarksEmpty && !local.BookmarksEmpty)          // an empty file would delete every bookmark here
                                      && Serialize(remote.Work, remote.Private) != local.Bookmarks;
                bool applyChannels = remote.Feeds is { } rf
                                     && !(rf.Count == 0 && local.Channels.Count > 0)             // same for the channel list
                                     && (JsonSerializer.Serialize(rf, Json) != JsonSerializer.Serialize(local.Channels, Json)
                                         || (remote.FeedFolders != null && JsonSerializer.Serialize(remote.FeedFolders, Json) != JsonSerializer.Serialize(local.Folders, Json)));

                // No empty guard for the theme: {} means "all defaults".
                bool applyTheme = remote.Theme is { } rt
                                  && JsonSerializer.Serialize(new SortedDictionary<string, string>(ThemeService.NormalizeSync(rt), StringComparer.OrdinalIgnoreCase), Json)
                                     != JsonSerializer.Serialize(new SortedDictionary<string, string>(local.Theme, StringComparer.OrdinalIgnoreCase), Json);

                if (!applyBookmarks && !applyChannels && !applyTheme)
                {
                    await _ui.InvokeAsync(() => MarkSeen(fresh));                // same content, just newer
                    Status = remoteBookmarksEmpty && !local.BookmarksEmpty
                        ? "Ignored an empty sync file (it would delete every bookmark here)"
                        : $"In sync ({DateTime.Now:HH:mm:ss})";
                    return;
                }

                await _ui.InvokeAsync(() =>
                {
                    try
                    {
                        File.WriteAllText(BackupPath, JsonSerializer.Serialize(
                            new { Work = _store.Bookmarks, Private = _store.PrivateBookmarks, Feeds = local.Channels, FeedFolders = local.Folders, Theme = local.Theme }, Json));
                    }
                    catch { /* the backup is a courtesy */ }
                    if (applyBookmarks) _store.ReplaceBookmarks(remote.Work, remote.Private);
                    if (applyChannels) FeedStore.ReplaceChannels(remote.Feeds!, remote.FeedFolders);
                    if (applyTheme) ThemeService.ApplyFromSync(remote.Theme!);
                    MarkSeen(fresh);
                });
                _dirty = false;
                _pushTimer.Change(Timeout.Infinite, Timeout.Infinite);
                Applied?.Invoke();
                // Copy the applied state into this device's own file too, so every file is a full snapshot (a device that joins later
                // must not bring back what was deleted). It keeps the time of the edit it copies, so it never looks like a newer edit.
                try { await WriteAsync(remote.Modified); }
                catch { _dirty = true; _pushTimer.Change(10_000, Timeout.Infinite); }
                Status = $"Updated from {remote.Device} ({DateTime.Now:HH:mm:ss})";
            }
            catch (Exception ex) { Status = "Error: " + ex.Message; }
            finally { _gate.Release(); }
        }

        // Writes the current bookmarks, channels and colours to this device's file, via a temporary file so a half-written file is never
        // visible. Modified is now, or the given time when the write only copies a state applied from another device. The gate must be held.
        private async Task WriteAsync(DateTime? modifiedAt = null)
        {
            var modified = modifiedAt ?? DateTime.UtcNow;
            // The revision is one more than the highest this PC knows of, including whatever is in its file right now.
            long revision = Math.Max(_store.SyncRevision, (await ReadCurrentAsync())?.Revision ?? 0) + 1;
            string json = await _ui.InvokeAsync(() => JsonSerializer.Serialize(
                new SyncFile(FormatVersion, modified, Environment.MachineName, _store.Bookmarks.ToList(), _store.PrivateBookmarks.ToList(),
                             FeedStore.LoadChannels(), FeedStore.LoadFolderNames(), revision,
                             ThemeService.ToSync().ToDictionary(kv => kv.Key, kv => (string?)kv.Value)), Json));
            var tmp = _path + ".tmp";
            await File.WriteAllTextAsync(tmp, json);
            File.Move(tmp, _path, overwrite: true);
            await _ui.InvokeAsync(() => { _store.SyncStamp = modified; _store.SyncRevision = revision; _store.SaveSoon(); });
            Status = $"In sync ({DateTime.Now:HH:mm:ss})";
        }

        // The other devices' files: "<name> - <DEVICE>.json" next to this device's file, plus the single shared file of the older
        // setup ("<name>.json"). OneDrive conflict copies ("<name>-<DEVICE>.json", no spaces) do not match.
        private IEnumerable<string> PeerPaths()
        {
            var paths = Directory.GetFiles(_dir, _stem + " - *.json").ToList();
            if (File.Exists(_legacyPath)) paths.Add(_legacyPath);
            return paths.Where(p => !string.Equals(p, _path, StringComparison.OrdinalIgnoreCase));
        }

        private bool IsPeerName(string? name) =>
            name != null && !string.Equals(name, Path.GetFileName(_path), StringComparison.OrdinalIgnoreCase)
            && (name.StartsWith(_stem + " - ", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, Path.GetFileName(_legacyPath), StringComparison.OrdinalIgnoreCase));

        private static string Key(string path) => Path.GetFileName(path).ToLowerInvariant();

        private static string SafeName(string name) =>
            string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        // The readable files of the other devices that are newer than what this PC last applied from them. Unreadable ones are
        // skipped and tried again at the next check.
        private async Task<List<(string Key, SyncFile File)>> FreshPeersAsync()
        {
            var seen = await _ui.InvokeAsync(() => new Dictionary<string, SyncPeerState>(_store.SyncPeers));
            var fresh = new List<(string Key, SyncFile File)>();
            foreach (var p in PeerPaths())
            {
                if (await ReadAsync(p, quiet: true) is not { } file) { Status = $"Could not read {Path.GetFileName(p)} yet (will retry)"; continue; }
                if (IsNewer(file, seen.GetValueOrDefault(Key(p)))) fresh.Add((Key(p), file));
            }
            return fresh;
        }

        // Remembers that these files' states are now in this PC (applied, equal, or lost against a newer local edit). UI thread.
        private void MarkSeen(IEnumerable<(string Key, SyncFile File)> files)
        {
            foreach (var (key, file) in files) _store.SyncPeers[key] = new SyncPeerState { Modified = file.Modified, Revision = file.Revision };
            _store.SaveSoon();
        }

        // Is this file newer than what this PC last applied from that device? Compared by revision; a file from a version without a
        // revision counter (Revision 0), and two files with the same revision, are compared by their Modified time. A file never seen
        // before is newer.
        private static bool IsNewer(SyncFile file, SyncPeerState? seen) =>
            seen == null
            || (file.Revision == 0 || file.Revision == seen.Revision
                ? file.Modified > seen.Modified
                : file.Revision > seen.Revision);

        // This device's own file as it is now, without touching the status; null if there is none or it cannot be read.
        private async Task<SyncFile?> ReadCurrentAsync() => File.Exists(_path) ? await ReadAsync(_path, quiet: true) : null;

        // Reads a file, waiting out the moments when OneDrive is still downloading or writing it.
        private async Task<SyncFile?> ReadAsync(string path, bool quiet = false)
        {
            void Say(string text) { if (!quiet) Status = text; }
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    var file = JsonSerializer.Deserialize<SyncFile>(await File.ReadAllTextAsync(path), Json);
                    if (file is not { Work: not null, Private: not null }) { Say("The sync file is not a Browse bookmark file"); return null; }
                    if (file.Format > FormatVersion) { Say($"The sync file was written by a newer Browse (format {file.Format}); update Browse to sync with it"); return null; }
                    return file;
                }
                catch (IOException) { await Task.Delay(700); }
                catch (JsonException) { Say("The sync file could not be read yet (will retry)"); return null; }
            }
            Say("The sync file is busy (will retry)");
            return null;
        }

        private static string Serialize(IEnumerable<Bookmark> work, IEnumerable<Bookmark> priv) =>
            JsonSerializer.Serialize(new { Work = work, Private = priv }, Json);

        public void Dispose()
        {
            Stop();
            _pushTimer.Dispose();
            _pullTimer.Dispose();
            _pollTimer.Dispose();
        }
    }

    /// <summary>What this PC last applied from another device's sync file (see BookmarkSyncService).</summary>
    public sealed class SyncPeerState
    {
        public DateTime Modified { get; set; }
        public long Revision { get; set; }
    }
}
