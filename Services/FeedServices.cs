using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Browse.Models;

namespace Browse.Services
{
    /// <summary>Thumbnails for the Feeds page, downloaded by WPF in the background and kept for the session.</summary>
    public static class ImageCache
    {
        private static readonly ConcurrentDictionary<string, ImageSource> Cache = new();

        public static ImageSource? Get(string url, int decodeWidth)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
            return Cache.GetOrAdd($"{decodeWidth}|{url}", _ =>
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.UriSource = uri;
                img.DecodePixelWidth = decodeWidth;
                img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                img.EndInit(); // a web address downloads in the background; the image appears when it arrives
                return img;
            });
        }
    }

    /// <summary>
    /// The channel list of the Feeds page, in %AppData%\Browse\feeds.json. The first time, it is copied from SebiTool's channel list
    /// (%AppData%\SebiTool\channels.json), where these channels were collected; after that Browse keeps its own copy.
    /// </summary>
    public static class FeedStore
    {
        private static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        private static readonly string FilePath = Path.Combine(AppData, "Browse", "feeds.json");
        private static readonly string SebiToolPath = Path.Combine(AppData, "SebiTool", "channels.json");
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        public sealed class StoredChannel
        {
            public string Title { get; set; } = "";
            public string XmlUrl { get; set; } = "";
            public string? HtmlUrl { get; set; }
            public string? Folder { get; set; }
            public string? ThumbnailUrl { get; set; }
        }

        public sealed class Data
        {
            public List<StoredChannel> Channels { get; set; } = [];
            // The folders in their order, empty ones included (a folder is otherwise only a name on channels). Folders that
            // only exist on channels are appended after these, so older files work unchanged.
            public List<string> Folders { get; set; } = [];
            public string LastFolder { get; set; } = "";
            public string Filter { get; set; } = "week";
        }

        public static Data Load()
        {
            try
            {
                if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new();
            }
            catch { /* fall through to a fresh copy */ }
            var data = new Data();
            ImportFromSebiTool(data);
            Save(data);
            return data;
        }

        public static void Save(Data data)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(data, Json));
            }
            catch { }
        }

        /// <summary>Raised after the channel list was changed on this PC (a channel added or imported), so the bookmark sync can write it out.</summary>
        public static event Action? ChannelsChanged;

        /// <summary>Adds the channels from SebiTool that are not here yet (matched by feed address). Returns how many were added.</summary>
        public static int ImportFromSebiTool(Data data)
        {
            try
            {
                if (!File.Exists(SebiToolPath)) return 0;
                var theirs = JsonSerializer.Deserialize<List<StoredChannel>>(File.ReadAllText(SebiToolPath)) ?? [];
                var known = data.Channels.Select(c => c.XmlUrl).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var fresh = theirs.Where(c => c.XmlUrl.Length > 0 && known.Add(c.XmlUrl)).ToList();
                data.Channels.AddRange(fresh);
                if (fresh.Count > 0) ChannelsChanged?.Invoke();
                return fresh.Count;
            }
            catch { return 0; }
        }

        // ---- used by the bookmark sync, which carries the channel list in its file ----

        public static List<StoredChannel> LoadChannels() => Load().Channels;

        /// <summary>All folder names in order, empty folders included.</summary>
        public static List<string> LoadFolderNames() => FolderNames();

        /// <summary>
        /// Makes the channel list and folder list exactly these (what the sync file from another PC says). A null folder list
        /// (an older file) keeps the local one. Not a local edit: no <see cref="ChannelsChanged"/>.
        /// </summary>
        public static void ReplaceChannels(List<StoredChannel> channels, List<string>? folders)
        {
            var data = Load();
            data.Channels = channels;
            if (folders != null) data.Folders = folders;
            Save(data);
        }

        /// <summary>Adds the channels and folders this PC lacks (the first sync with an existing file never loses local ones).</summary>
        public static void MergeChannels(IEnumerable<StoredChannel> channels, IEnumerable<string>? folders)
        {
            var data = Load();
            var known = data.Channels.Select(c => c.XmlUrl).ToHashSet(StringComparer.OrdinalIgnoreCase);
            data.Channels.AddRange(channels.Where(c => c.XmlUrl.Length > 0 && known.Add(c.XmlUrl)));
            var names = BuildFolders(data).Select(f => f.Title).ToList();
            foreach (var f in folders ?? []) if (!names.Contains(f, StringComparer.OrdinalIgnoreCase)) names.Add(f);
            data.Folders = names;
            Save(data);
        }

        /// <summary>The names of the folders, in the order the Feeds page shows them (empty folders too).</summary>
        public static List<string> FolderNames() => BuildFolders(Load()).Select(f => f.Title).ToList();

        public sealed record OpmlResult(int Added, int AlreadyThere, int Skipped);

        /// <summary>
        /// Imports the YouTube channels of an OPML file (for example a FeedBro export) that are not in the list yet, into the folders
        /// the file names them in (an existing folder is reused, a new name becomes a new folder). Other kinds of feeds are skipped.
        /// </summary>
        public static async Task<OpmlResult> ImportOpmlAsync(string path)
        {
            var doc = XDocument.Load(path);
            var found = new List<(string Folder, StoredChannel Channel)>();
            int skipped = 0;
            void Walk(XElement parent, string folder)
            {
                foreach (var o in parent.Elements("outline"))
                {
                    string xml = ((string?)o.Attribute("xmlUrl") ?? "").Trim();
                    string title = ((string?)o.Attribute("title") ?? (string?)o.Attribute("text") ?? "").Trim();
                    if (xml.Length == 0) { Walk(o, folder.Length == 0 ? title : folder); continue; } // sub-folders collapse into their top-level folder
                    var id = System.Text.RegularExpressions.Regex.Match(xml, @"youtube\.com/feeds/videos\.xml\?channel_id=([\w-]+)");
                    if (!id.Success || title.Length == 0) { skipped++; continue; }                     // other feeds, YouTube searches
                    found.Add((folder, new StoredChannel
                    {
                        Title = title, XmlUrl = xml, HtmlUrl = $"https://www.youtube.com/channel/{id.Groups[1].Value}", ThumbnailUrl = "",
                    }));
                }
            }
            if (doc.Root?.Element("body") is { } body) Walk(body, "");

            // The icons come from the channel pages: fetch them before saving, so the list is written once, complete.
            var known = Load().Channels.Select(c => c.XmlUrl).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidates = found.Where(f => !known.Contains(f.Channel.XmlUrl)).ToList();
            using (var gate = new SemaphoreSlim(5))
            {
                await Task.WhenAll(candidates.Select(async f =>
                {
                    await gate.WaitAsync();
                    try { f.Channel.ThumbnailUrl = await YouTubeChannelService.FetchThumbnailAsync(f.Channel.HtmlUrl ?? "") ?? ""; }
                    finally { gate.Release(); }
                }));
            }

            var data = Load(); // read again now: the list may have changed while the icons were loading
            known = data.Channels.Select(c => c.XmlUrl).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var fresh = found.Where(f => known.Add(f.Channel.XmlUrl)).ToList(); // also drops duplicates inside the file
            var names = BuildFolders(data).Select(f => f.Title).ToList();
            foreach (var (folder, channel) in fresh)
            {
                string wanted = folder.Length == 0 ? "Unsorted" : folder;
                string name = names.FirstOrDefault(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase)) ?? wanted;
                if (!names.Contains(name)) names.Add(name);
                channel.Folder = name == "Unsorted" ? "" : name;
                data.Channels.Add(channel);
            }
            if (fresh.Count > 0)
            {
                data.Folders = names;
                Save(data);
                ChannelsChanged?.Invoke();
            }
            return new OpmlResult(fresh.Count, found.Count - fresh.Count, skipped);
        }

        /// <summary>Removes a channel from the list (by feed address). Returns false if it was not there.</summary>
        public static bool RemoveChannel(string xmlUrl)
        {
            var data = Load();
            int removed = data.Channels.RemoveAll(c => string.Equals(c.XmlUrl, xmlUrl, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            Save(data);
            ChannelsChanged?.Invoke();
            return true;
        }

        /// <summary>Removes a folder together with the channels in it. Returns how many channels went with it.</summary>
        public static int RemoveFolder(string name)
        {
            var data = Load();
            bool IsIn(StoredChannel c) => string.Equals(string.IsNullOrEmpty(c.Folder) ? "Unsorted" : c.Folder, name, StringComparison.OrdinalIgnoreCase);
            int channels = data.Channels.RemoveAll(IsIn);
            data.Folders.RemoveAll(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
            Save(data);
            ChannelsChanged?.Invoke();
            return channels;
        }

        /// <summary>Creates an empty folder. Returns false, and the name it clashes with, if one with that name (any case) exists.</summary>
        public static (bool Created, string Name) AddFolder(string name)
        {
            name = name.Trim();
            var data = Load();
            var names = BuildFolders(data).Select(f => f.Title).ToList();
            if (names.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) is { } clash) return (false, clash);
            names.Add(name);
            data.Folders = names;
            Save(data);
            ChannelsChanged?.Invoke();
            return (true, name);
        }

        /// <summary>
        /// Adds a channel to a folder. Returns false, and the folder it is already in, when a channel with the same feed address exists.
        /// </summary>
        public static (bool Added, string? ExistingFolder) AddChannel(string folder, YouTubeChannelService.Channel channel)
        {
            var data = Load();
            var existing = data.Channels.FirstOrDefault(c => string.Equals(c.XmlUrl, channel.XmlUrl, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return (false, string.IsNullOrEmpty(existing.Folder) ? "Unsorted" : existing.Folder);
            data.Channels.Add(new StoredChannel
            {
                Title = channel.Title, XmlUrl = channel.XmlUrl, HtmlUrl = channel.HtmlUrl, ThumbnailUrl = channel.ThumbnailUrl,
                Folder = folder == "Unsorted" ? "" : folder, // "Unsorted" is only how the page names channels without a folder
            });
            Save(data);
            ChannelsChanged?.Invoke();
            return (true, null);
        }

        /// <summary>The folders in order: the explicit list first (so empty folders show), then any folder only channels mention.</summary>
        public static List<FeedFolder> BuildFolders(Data data)
        {
            var byFolder = data.Channels
                .Select(c => new FeedChannel
                {
                    Title = c.Title, XmlUrl = c.XmlUrl, HtmlUrl = c.HtmlUrl ?? "",
                    FolderName = c.Folder ?? "", ThumbnailUrl = c.ThumbnailUrl ?? "",
                })
                .GroupBy(c => c.FolderName.Length > 0 ? c.FolderName : "Unsorted")
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var order = new List<string>();
            foreach (var name in data.Folders.Concat(byFolder.Keys))
                if (name.Length > 0 && !order.Contains(name, StringComparer.OrdinalIgnoreCase)) order.Add(name);

            return order.Select(n => new FeedFolder(n, byFolder.TryGetValue(n, out var list) ? list : [])).ToList();
        }
    }

    /// <summary>
    /// Reads a channel's latest videos from its YouTube videos page (YouTube's RSS feeds no longer exist). The page carries its
    /// video list as JSON (<c>ytInitialData</c>), with ages as "3 days ago" only, so the dates are approximate. Results are kept
    /// for a few minutes so switching folders is quick.
    /// </summary>
    public static class FeedFetchService
    {
        private static readonly HttpClient Http = CreateClient();
        private static readonly ConcurrentDictionary<string, (DateTime At, List<FeedVideo> Items)> Cache = new();
        private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);
        private static readonly System.Text.RegularExpressions.Regex DataRx = new(@"var ytInitialData = (\{.*?\});</script>", System.Text.RegularExpressions.RegexOptions.Singleline);
        private static readonly System.Text.RegularExpressions.Regex AgeRx = new(@"(\d+)\s*(second|minute|hour|day|week|month|year)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static HttpClient CreateClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
            http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9"); // the ages are read as English ("3 days ago")
            http.DefaultRequestHeaders.Add("Cookie", "CONSENT=YES+1; SOCS=CAI");   // skips the cookie-consent page
            return http;
        }

        public static void ClearCache() => Cache.Clear();

        public static async Task<List<FeedVideo>> FetchAsync(FeedChannel channel)
        {
            string url = channel.VideosUrl;
            if (url.Length == 0) throw new InvalidOperationException("no channel address");
            if (Cache.TryGetValue(url, out var hit) && DateTime.UtcNow - hit.At < MaxAge) return hit.Items;

            var m = DataRx.Match(await Http.GetStringAsync(url));
            if (!m.Success) throw new InvalidOperationException("no video data on the channel page");
            using var doc = JsonDocument.Parse(m.Groups[1].Value);

            var now = DateTime.UtcNow;
            var items = new List<FeedVideo>();
            void Walk(JsonElement e)
            {
                switch (e.ValueKind)
                {
                    case JsonValueKind.Object:
                        if (e.TryGetProperty("lockupViewModel", out var lockup) && ReadVideo(lockup, now) is { } v) { items.Add(v); return; }
                        foreach (var p in e.EnumerateObject()) Walk(p.Value);
                        break;
                    case JsonValueKind.Array:
                        foreach (var x in e.EnumerateArray()) Walk(x);
                        break;
                }
            }
            Walk(doc.RootElement);

            Cache[url] = (now, items);
            return items;
        }

        private static FeedVideo? ReadVideo(JsonElement lockup, DateTime now)
        {
            if (Str(lockup, "contentType") != "LOCKUP_CONTENT_TYPE_VIDEO") return null;
            string id = Str(lockup, "contentId");
            if (id.Length == 0 || !lockup.TryGetProperty("metadata", out var meta1) || !meta1.TryGetProperty("lockupMetadataViewModel", out var meta)) return null;
            string title = meta.TryGetProperty("title", out var t) ? Str(t, "content") : "";

            DateTime? published = null;
            bool live = false;
            if (meta.TryGetProperty("metadata", out var m1) && m1.TryGetProperty("contentMetadataViewModel", out var m2) && m2.TryGetProperty("metadataRows", out var rows))
                foreach (var row in rows.EnumerateArray())
                    if (row.TryGetProperty("metadataParts", out var parts))
                        foreach (var part in parts.EnumerateArray())
                        {
                            string text = part.TryGetProperty("text", out var tx) ? Str(tx, "content") : "";
                            string label = Str(part, "accessibilityLabel");
                            if (text.Contains("watching", StringComparison.OrdinalIgnoreCase)) live = true;
                            var age = AgeRx.Match(label.Length > 0 ? label : text);
                            if (published == null && age.Success && (text.Contains("ago", StringComparison.OrdinalIgnoreCase) || label.Contains("ago", StringComparison.OrdinalIgnoreCase)))
                            {
                                int n = int.Parse(age.Groups[1].Value);
                                published = now - age.Groups[2].Value.ToLowerInvariant() switch
                                {
                                    "second" => TimeSpan.FromSeconds(n), "minute" => TimeSpan.FromMinutes(n), "hour" => TimeSpan.FromHours(n),
                                    "day" => TimeSpan.FromDays(n), "week" => TimeSpan.FromDays(7 * n), "month" => TimeSpan.FromDays(30 * n),
                                    _ => TimeSpan.FromDays(365 * n),
                                };
                            }
                        }
            if (live) published = now;       // a stream that is on right now
            if (published == null) return null; // scheduled or premiering later: nothing to show yet

            return new FeedVideo
            {
                Title = title.Length > 0 ? title : "Untitled", VideoId = id, Published = published,
                Link = $"https://www.youtube.com/watch?v={id}", ThumbnailUrl = $"https://i.ytimg.com/vi/{id}/mqdefault.jpg",
            };
        }

        private static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
    }
}
