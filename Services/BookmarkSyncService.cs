using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace Browse.Services
{
    /// <summary>
    /// Keeps both profiles' bookmarks (folders and order included) and the Feeds channel list in one shared JSON file, for example in
    /// OneDrive, so several PCs end up with the same bookmarks and channels, and the file doubles as their backup. The file is the
    /// truth: whenever a newer version arrives from another PC it replaces the local bookmarks and channels (new ones appear, deleted
    /// ones go); every local change is written to the file a couple of seconds later. When both sides changed, the newer edit wins.
    /// The very first sync with an existing file merges instead, so nothing local is lost. Before a remote version overwrites the
    /// local data, the local data is saved to bookmarks-before-sync.json. Which folder or filter the Feeds page last showed stays per PC.
    /// </summary>
    public sealed class BookmarkSyncService : IDisposable
    {
        // Feeds is missing in files written before channels were synced: such a file says nothing about channels, so local ones are kept.
        // The format written by this version. A file with a higher number was written by a newer Browse: it is read-only here, never overwritten.
        // Revision counts the writes of the file (0 = written by a version that had no counter). The format is specified in docs/SYNC-FORMAT.md.
        private const int FormatVersion = 3;

        private sealed record SyncFile(int Format, DateTime Modified, string Device, List<Bookmark> Work, List<Bookmark> Private,
                                       List<FeedStore.StoredChannel>? Feeds = null, List<string>? FeedFolders = null, long Revision = 0);

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
        private static readonly string BackupPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Browse", "bookmarks-before-sync.json");

        private readonly BrowserStore _store;
        private readonly Dispatcher _ui;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Timer _pushTimer;   // waits a moment after the last local edit
        private readonly Timer _pullTimer;   // waits a moment after the file changed (OneDrive writes it in several steps)
        private readonly Timer _pollTimer;   // safety net in case a file-change notification is missed
        private FileSystemWatcher? _watcher;
        private string _path = "";
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
        public string FilePath => _path;

        /// <summary>A remote version replaced local bookmarks or channels (raised on a background thread).</summary>
        public event Action? Applied;

        public async Task StartAsync(string path)
        {
            Stop();
            _path = path;
            Status = "Connecting…";
            await _gate.WaitAsync();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path))
                {
                    // An existing file that cannot be read (empty, damaged, or from a newer Browse) is never overwritten: sync stays off.
                    if (await ReadAsync() is not { } remote) return;
                    // first contact: keep everything local and add what the file has
                    await _ui.InvokeAsync(() =>
                    {
                        _store.MergeBookmarks(remote.Work, remote.Private);
                        if (remote.Feeds != null) FeedStore.MergeChannels(remote.Feeds, remote.FeedFolders);
                    });
                }
                await WriteAsync();                                   // creates the file, or writes the union back
            }
            catch (Exception ex) { Status = "Error: " + ex.Message; return; }
            finally { _gate.Release(); }

            _running = true;
            _store.BookmarksChanged += OnLocalChange;
            FeedStore.ChannelsChanged += OnLocalChange;
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler changed = (_, _) => _pullTimer.Change(1500, Timeout.Infinite);
            _watcher.Changed += changed;
            _watcher.Created += changed;
            _watcher.Renamed += (_, _) => _pullTimer.Change(1500, Timeout.Infinite);
            _pollTimer.Change(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        public void Stop()
        {
            _running = false;
            _store.BookmarksChanged -= OnLocalChange;
            FeedStore.ChannelsChanged -= OnLocalChange;
            _watcher?.Dispose();
            _watcher = null;
            _pushTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _pullTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _dirty = false;
            Status = "Off";
        }

        /// <summary>Checks the file now, and writes local edits that are not in it yet.</summary>
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
                // Another PC may have written since this one last looked. If its version is newer than this edit, leave it to the
                // pull to apply instead of overwriting it.
                if (await ReadCurrentAsync() is { } current && IsNewer(current) && current.Modified > _localChangedAt)
                {
                    _pullTimer.Change(0, Timeout.Infinite);
                    return;
                }
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
                if (await ReadAsync() is not { } remote) return;
                if (!IsNewer(remote)) return;                                    // nothing newer than what this PC already has
                if (_dirty && _localChangedAt > remote.Modified) return;         // a newer local edit is about to be written

                // What differs? Compared on this PC's data (read on the UI thread, where it is edited).
                var local = await _ui.InvokeAsync(() => (
                    Bookmarks: Serialize(_store.Bookmarks, _store.PrivateBookmarks),
                    BookmarksEmpty: _store.Bookmarks.Count == 0 && _store.PrivateBookmarks.Count == 0,
                    Channels: FeedStore.LoadChannels(),
                    Folders: FeedStore.LoadFolderNames()));

                bool remoteBookmarksEmpty = remote.Work.Count == 0 && remote.Private.Count == 0;
                bool applyBookmarks = !(remoteBookmarksEmpty && !local.BookmarksEmpty)          // an empty file would delete every bookmark here
                                      && Serialize(remote.Work, remote.Private) != local.Bookmarks;
                bool applyChannels = remote.Feeds is { } rf
                                     && !(rf.Count == 0 && local.Channels.Count > 0)             // same for the channel list
                                     && (JsonSerializer.Serialize(rf, Json) != JsonSerializer.Serialize(local.Channels, Json)
                                         || (remote.FeedFolders != null && JsonSerializer.Serialize(remote.FeedFolders, Json) != JsonSerializer.Serialize(local.Folders, Json)));

                if (!applyBookmarks && !applyChannels)
                {
                    await _ui.InvokeAsync(() => { _store.SyncStamp = remote.Modified; _store.SyncRevision = remote.Revision; _store.SaveSoon(); }); // same content, just newer
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
                            new { Work = _store.Bookmarks, Private = _store.PrivateBookmarks, Feeds = local.Channels, FeedFolders = local.Folders }, Json));
                    }
                    catch { /* the backup is a courtesy */ }
                    if (applyBookmarks) _store.ReplaceBookmarks(remote.Work, remote.Private);
                    if (applyChannels) FeedStore.ReplaceChannels(remote.Feeds!, remote.FeedFolders);
                    _store.SyncStamp = remote.Modified;
                    _store.SyncRevision = remote.Revision;
                    _store.SaveSoon();
                });
                _dirty = false;
                _pushTimer.Change(Timeout.Infinite, Timeout.Infinite);
                Status = $"Updated from {remote.Device} ({DateTime.Now:HH:mm:ss})";
                Applied?.Invoke();
            }
            catch (Exception ex) { Status = "Error: " + ex.Message; }
            finally { _gate.Release(); }
        }

        // Writes the current bookmarks and channels, via a temporary file so a half-written file is never visible. The gate must be held.
        private async Task WriteAsync()
        {
            var modified = DateTime.UtcNow;
            // The revision is one more than the highest this PC knows of, including whatever is in the file right now.
            long revision = Math.Max(_store.SyncRevision, (await ReadCurrentAsync())?.Revision ?? 0) + 1;
            string json = await _ui.InvokeAsync(() => JsonSerializer.Serialize(
                new SyncFile(FormatVersion, modified, Environment.MachineName, _store.Bookmarks.ToList(), _store.PrivateBookmarks.ToList(),
                             FeedStore.LoadChannels(), FeedStore.LoadFolderNames(), revision), Json));
            var tmp = _path + ".tmp";
            await File.WriteAllTextAsync(tmp, json);
            File.Move(tmp, _path, overwrite: true);
            await _ui.InvokeAsync(() => { _store.SyncStamp = modified; _store.SyncRevision = revision; _store.SaveSoon(); });
            Status = $"In sync ({DateTime.Now:HH:mm:ss})";
        }

        // Is this file newer than what this PC last wrote or applied? Compared by revision; a file from a version without a revision
        // counter (Revision 0), and two files with the same revision, are compared by their Modified time.
        private bool IsNewer(SyncFile file) =>
            file.Revision == 0 || file.Revision == _store.SyncRevision
                ? file.Modified > _store.SyncStamp
                : file.Revision > _store.SyncRevision;

        // The file as it is now, without touching the status; null if there is none or it cannot be read.
        private async Task<SyncFile?> ReadCurrentAsync() => File.Exists(_path) ? await ReadAsync(quiet: true) : null;

        // Reads the file, waiting out the moments when OneDrive is still downloading or writing it.
        private async Task<SyncFile?> ReadAsync(bool quiet = false)
        {
            void Say(string text) { if (!quiet) Status = text; }
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    var file = JsonSerializer.Deserialize<SyncFile>(await File.ReadAllTextAsync(_path), Json);
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
}
