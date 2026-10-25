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

        public string HomeUrl { get; set; } = "https://www.google.com";
        public string SearchUrl { get; set; } = "https://www.google.com/search?q={0}";
        public const string Work = "Work", Private = "Private";

        /// <summary>The Work bookmarks (the original single list, so existing bookmarks stay under Work).</summary>
        public ObservableCollection<Bookmark> Bookmarks { get; set; } = [];
        public ObservableCollection<Bookmark> PrivateBookmarks { get; set; } = [];
        public string Profile { get; set; } = Work;
        public List<HistoryEntry> History { get; set; } = [];
        public List<string> Session { get; set; } = [];
        public int SessionIndex { get; set; }

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
            SaveSoon();
        }

        public void RemoveBookmark(Bookmark b) { RemoveFrom(ActiveBookmarks, b); SaveSoon(); }

        /// <summary>Adds the items to the active profile, keeping folders and skipping URLs it already has. Returns the number of links added.</summary>
        public int ImportBookmarks(IEnumerable<Bookmark> items)
        {
            var known = Flatten(ActiveBookmarks).Select(b => b.Url).ToHashSet();
            List<Bookmark> Prune(IEnumerable<Bookmark> src) => src
                .Select(b => b.Children is { } c ? b with { Children = Prune(c) } : known.Contains(b.Url) ? null : b)
                .OfType<Bookmark>().Where(b => b.Children is not { Count: 0 }).ToList();
            var fresh = Prune(items);
            foreach (var b in fresh) ActiveBookmarks.Add(b);
            SaveSoon();
            return Flatten(fresh).Count();
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
