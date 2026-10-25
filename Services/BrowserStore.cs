using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Browse.Services
{
    /// <summary>A link, or a folder when <see cref="Children"/> is set (folders nest; their Url is empty).</summary>
    public sealed record Bookmark(string Title, string Url, List<Bookmark>? Children = null)
    {
        [JsonIgnore] public bool IsFolder => Children != null;
        [JsonIgnore] public string Label => IsFolder ? "📁 " + Title : Title;
    }
    public sealed record HistoryEntry(string Title, string Url, DateTime Time);

    /// <summary>All user data in one small JSON file (Roaming\Browse\browse.json); the WebView2 profile lives elsewhere.</summary>
    public sealed class BrowserStore
    {
        private const int MaxHistory = 3000;
        private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Browse");
        private static readonly string FilePath = Path.Combine(Dir, "browse.json");
        // Velopack owns %LocalAppData%\Browse, so the (large) browser profile sits in a sibling folder.
        public static readonly string ProfileDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrowseData");

        public string SearchUrl { get; set; } = "https://www.google.com/search?q={0}";
        public const string Work = "Work", Private = "Private";

        /// <summary>The Work bookmarks (the original single list, so existing bookmarks stay under Work).</summary>
        public ObservableCollection<Bookmark> Bookmarks { get; set; } = [];
        public ObservableCollection<Bookmark> PrivateBookmarks { get; set; } = [];
        public string Profile { get; set; } = Work;
        public List<HistoryEntry> History { get; set; } = [];
        public List<string> Session { get; set; } = [];
        public int SessionIndex { get; set; }
        // Each profile keeps its own open tabs: Session is the Work profile's (the original single session), these are Private's.
        public List<string> PrivateSession { get; set; } = [];
        public int PrivateSessionIndex { get; set; }

        // Bookmark sync (see BookmarkSyncService): the shared file chosen on THIS PC (OneDrive paths differ per PC), and the
        // 'modified' time of the file state this PC last applied or wrote.
        public string? SyncFile { get; set; }
        public DateTime SyncStamp { get; set; }
        /// <summary>The revision number of the sync file state this PC last wrote or applied (see docs/SYNC-FORMAT.md).</summary>
        public long SyncRevision { get; set; }

        /// <summary>When a bookmark was last changed by the user on this PC (not by a sync).</summary>
        [JsonIgnore] public DateTime LocalChangedAt { get; private set; }

        /// <summary>Raised after any local bookmark change: starring, removing, moving, importing.</summary>
        public event Action? BookmarksChanged;

        private void BookmarksTouched()
        {
            LocalChangedAt = DateTime.UtcNow;
            SaveSoon();
            BookmarksChanged?.Invoke();
        }

        private Timer? _timer;

        public static BrowserStore Load()
        {
            try { return JsonSerializer.Deserialize<BrowserStore>(File.ReadAllText(FilePath)) ?? new(); }
            catch { return new(); }
        }

        public void AddHistory(string title, string url)
        {
            lock (History)
            {
                History.RemoveAll(h => h.Url == url);
                History.Insert(0, new(title, url, DateTime.Now));
                if (History.Count > MaxHistory) History.RemoveRange(MaxHistory, History.Count - MaxHistory);
            }
            SaveSoon();
        }

        public void ClearHistory() { lock (History) History.Clear(); SaveSoon(); }

        /// <summary>Bookmarks of the selected profile; history, cookies and tabs are shared between profiles.</summary>
        [JsonIgnore]
        public ObservableCollection<Bookmark> ActiveBookmarks => Profile == Private ? PrivateBookmarks : Bookmarks;

        public static IEnumerable<Bookmark> Flatten(IEnumerable<Bookmark> items) =>
            items.SelectMany(b => b.Children is { } c ? Flatten(c) : new[] { b });

        private static bool RemoveFrom(IList<Bookmark> list, Bookmark b) =>
            list.Remove(b) || list.Any(x => x.Children != null && RemoveFrom(x.Children, b));

        public Bookmark? FindBookmark(string url) => Flatten(ActiveBookmarks).FirstOrDefault(b => b.Url == url);

        public void ToggleBookmark(string title, string url)
        {
            if (FindBookmark(url) is { } b) RemoveFrom(ActiveBookmarks, b); else ActiveBookmarks.Add(new(title, url));
            BookmarksTouched();
        }

        public void RemoveBookmark(Bookmark b) { RemoveFrom(ActiveBookmarks, b); BookmarksTouched(); }

        /// <summary>True when <paramref name="target"/> is <paramref name="root"/> itself or somewhere below it.</summary>
        public static bool IsInside(Bookmark root, Bookmark target) =>
            ReferenceEquals(root, target) || (root.Children?.Any(c => IsInside(c, target)) ?? false);

        // By reference, so an identical bookmark elsewhere is never taken by mistake.
        private static bool DetachRef(IList<Bookmark> list, Bookmark b)
        {
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], b)) { list.RemoveAt(i); return true; }
            return list.Any(x => x.Children != null && DetachRef(x.Children, b));
        }

        private static IList<Bookmark>? ListContaining(IList<Bookmark> list, Bookmark b)
        {
            if (list.Any(x => ReferenceEquals(x, b))) return list;
            foreach (var x in list)
                if (x.Children != null && ListContaining(x.Children, b) is { } found) return found;
            return null;
        }

        private static int IndexOfRef(IList<Bookmark> list, Bookmark b)
        {
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], b)) return i;
            return -1;
        }

        /// <summary>Moves a bookmark or folder to sit right before/after another one, in whichever list that one lives in (bar or folder).</summary>
        public bool MoveRelative(Bookmark item, Bookmark target, bool after)
        {
            if (ReferenceEquals(item, target) || IsInside(item, target)) return false; // never into itself
            var source = ListContaining(Bookmarks, item) ?? ListContaining(PrivateBookmarks, item);
            var dest = ListContaining(Bookmarks, target) ?? ListContaining(PrivateBookmarks, target);
            if (source == null || dest == null) return false;
            source.RemoveAt(IndexOfRef(source, item));
            dest.Insert(IndexOfRef(dest, target) + (after ? 1 : 0), item); // index looked up after the removal, which may have shifted it
            BookmarksTouched();
            return true;
        }

        /// <summary>Moves a bookmark (from either profile) into a folder, or onto the active profile's bar when <paramref name="folder"/> is null.</summary>
        public bool MoveBookmark(Bookmark item, Bookmark? folder)
        {
            if (folder != null && (folder.Children == null || IsInside(item, folder))) return false; // never into itself
            if (!DetachRef(Bookmarks, item) && !DetachRef(PrivateBookmarks, item)) return false;
            if (folder == null) ActiveBookmarks.Add(item); else folder.Children!.Add(item);
            BookmarksTouched();
            return true;
        }

        // Adds the items that are not in the list yet (by URL), keeping folders. Returns the number of links added.
        private static int MergeInto(ObservableCollection<Bookmark> target, IEnumerable<Bookmark> items)
        {
            var known = Flatten(target).Select(b => b.Url).ToHashSet();
            List<Bookmark> Prune(IEnumerable<Bookmark> src) => src
                .Select(b => b.Children is { } c ? b with { Children = Prune(c) } : known.Contains(b.Url) ? null : b)
                .OfType<Bookmark>().Where(b => b.Children is not { Count: 0 }).ToList();
            var fresh = Prune(items);
            foreach (var b in fresh) target.Add(b);
            return Flatten(fresh).Count();
        }

        /// <summary>Adds the items to the active profile, keeping folders and skipping URLs it already has. Returns the number of links added.</summary>
        public int ImportBookmarks(IEnumerable<Bookmark> items)
        {
            int added = MergeInto(ActiveBookmarks, items);
            BookmarksTouched();
            return added;
        }

        /// <summary>Adds what the other lists have and this PC lacks, in both profiles (the first sync with an existing file never loses local bookmarks).</summary>
        public void MergeBookmarks(IEnumerable<Bookmark> work, IEnumerable<Bookmark> priv)
        {
            MergeInto(Bookmarks, work);
            MergeInto(PrivateBookmarks, priv);
            BookmarksTouched();
        }

        /// <summary>Makes both profiles exactly these lists: what a sync file written on another PC says. Not a local edit, so it does not raise <see cref="BookmarksChanged"/>.</summary>
        public void ReplaceBookmarks(IEnumerable<Bookmark> work, IEnumerable<Bookmark> priv)
        {
            static void Replace(ObservableCollection<Bookmark> target, IEnumerable<Bookmark> items)
            {
                target.Clear();
                foreach (var b in items) target.Add(b);
            }
            Replace(Bookmarks, work);
            Replace(PrivateBookmarks, priv);
            SaveSoon();
        }

        /// <summary>Every word must match title or url; bookmarks first. An empty query lists recent history.</summary>
        public List<Bookmark> Suggest(string query, int max)
        {
            var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool Match(Bookmark b) => words.All(w => b.Title.Contains(w, StringComparison.OrdinalIgnoreCase) || b.Url.Contains(w, StringComparison.OrdinalIgnoreCase));
            lock (History)
                return (words.Length == 0 ? [] : Flatten(ActiveBookmarks))
                    .Concat(History.Select(h => new Bookmark(h.Title, h.Url)))
                    .Where(Match).DistinctBy(b => b.Url).Take(max).ToList();
        }

        public void SaveSoon() => (_timer ??= new Timer(_ => Save())).Change(3000, Timeout.Infinite);

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string json;
                lock (History) json = JsonSerializer.Serialize(this);
                File.WriteAllText(FilePath + ".tmp", json);
                File.Move(FilePath + ".tmp", FilePath, overwrite: true);
            }
            catch { }
        }
    }
}
