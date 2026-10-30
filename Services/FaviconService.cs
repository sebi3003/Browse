using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Browse.Services
{
    /// <summary>
    /// Site icons for the bookmarks bar, cached as small PNGs per host in %AppData%\Browse\favicons.
    /// Icons arrive two ways: pages report their own icon while you browse (<see cref="Save"/>), and
    /// <see cref="BackfillAsync"/> fetches missing ones from the sites in the background (page's
    /// &lt;link rel="icon"&gt;, falling back to /favicon.ico). Hosts that failed are not retried for two weeks.
    /// </summary>
    public static class FaviconService
    {
        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Browse", "favicons");
        private static readonly string FailedPath = Path.Combine(Dir, "_failed.txt");
        private static readonly TimeSpan RetryAfter = TimeSpan.FromDays(14);

        private static readonly ConcurrentDictionary<string, ImageSource> Mem = new();
        private static readonly ConcurrentDictionary<string, DateTime> Failed = LoadFailed();

        private static readonly HttpClient Http = CreateClient();

        /// <summary>Raised (on any thread) after an icon was stored for a host.</summary>
        public static event Action? Added;

        private static HttpClient CreateClient()
        {
            var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
            http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
            return http;
        }

        /// <summary>Cache key for a page URL: its host without a leading "www." (host:port for an IP address or localhost); empty for anything but http(s).</summary>
        public static string HostOf(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return "";
            var host = u.IdnHost.ToLowerInvariant();
            // Servers on an IP address or localhost are told apart by port: several apps share one machine, each with its own icon.
            if (u.HostNameType == UriHostNameType.IPv4 || u.IsLoopback) return u.IsDefaultPort ? host : $"{host}:{u.Port}";
            return host.StartsWith("www.") ? host[4..] : host;
        }

        private static string PathFor(string host)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) host = host.Replace(c, '_');
            return Path.Combine(Dir, host + ".png");
        }

        private static bool Has(string host) => File.Exists(PathFor(host));

        /// <summary>The cached icon for a page URL, or null if there is none yet.</summary>
        public static ImageSource? Get(string url)
        {
            var host = HostOf(url);
            if (host.Length == 0) return null;
            if (Mem.TryGetValue(host, out var cached)) return cached;
            var path = PathFor(host);
            if (!File.Exists(path)) return null;
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad; // releases the file straight away
                img.DecodePixelWidth = 64; // enough for 200 % scaling; the Image downsamples with HighQuality
                img.UriSource = new Uri(path);
                img.EndInit();
                img.Freeze();
                Mem[host] = img;
                return img;
            }
            catch { return null; }
        }

        /// <summary>Stores an icon a page reported itself (PNG bytes from WebView2). Unchanged icons are ignored.</summary>
        public static void Save(string host, byte[] png)
        {
            if (host.Length == 0 || png.Length == 0) return;
            try
            {
                var path = PathFor(host);
                if (File.Exists(path))
                {
                    var existing = File.ReadAllBytes(path);
                    // The page's own icon is often only 16 px: never replace a sharper icon fetched earlier with it.
                    if (existing.AsSpan().SequenceEqual(png) || PngWidth(existing) > PngWidth(png)) return;
                }
                Directory.CreateDirectory(Dir);
                File.WriteAllBytes(path, png);
                Mem.TryRemove(host, out _);
                Failed.TryRemove(host, out _);
                Added?.Invoke();
            }
            catch { }
        }

        /// <summary>Fetches the missing icons for these page URLs. Returns how many were found.</summary>
        public static async Task<int> BackfillAsync(IEnumerable<string> urls, bool retryFailed = false)
        {
            if (retryFailed) Failed.Clear();
            var todo = urls
                .Select(u => (Url: u, Host: HostOf(u)))
                .Where(x => x.Host.Length > 0 && (!Has(x.Host) || retryFailed && SavedWidth(x.Host) < 64) && !RecentlyFailed(x.Host)) // a manual fetch also upgrades small icons
                .DistinctBy(x => x.Host)
                .ToList();
            if (todo.Count == 0) return 0;

            int found = 0;
            using var gate = new SemaphoreSlim(8);
            await Task.WhenAll(todo.Select(async x =>
            {
                await gate.WaitAsync();
                try
                {
                    if (await FetchAsync(x.Url, x.Host)) Interlocked.Increment(ref found);
                    else Failed[x.Host] = DateTime.UtcNow;
                }
                finally { gate.Release(); }
            }));
            SaveFailed();
            return found;
        }

        private static bool RecentlyFailed(string host) => Failed.TryGetValue(host, out var t) && DateTime.UtcNow - t < RetryAfter;

        private static async Task<bool> FetchAsync(string pageUrl, string host)
        {
            try
            {
                var candidates = new List<string>();
                Uri baseUri = new(pageUrl);
                try
                {
                    using var resp = await Http.GetAsync(pageUrl, HttpCompletionOption.ResponseHeadersRead);
                    baseUri = resp.RequestMessage?.RequestUri ?? baseUri;
                    if (resp.IsSuccessStatusCode && resp.Content.Headers.ContentType?.MediaType?.Contains("html") == true)
                    {
                        using var s = await resp.Content.ReadAsStreamAsync();
                        var buf = new byte[200_000];
                        int n = 0, r;
                        while (n < buf.Length && (r = await s.ReadAsync(buf.AsMemory(n))) > 0) n += r;
                        candidates.AddRange(IconLinks(Encoding.UTF8.GetString(buf, 0, n)));
                    }
                }
                catch { /* page unreachable: still try /favicon.ico below */ }
                candidates.Add("/favicon.ico");

                // Candidates come biggest first; stop at the first one that is sharp enough for the 24 px tiles (64 px for 200 % scaling),
                // otherwise keep the largest seen.
                byte[]? best = null;
                int bestWidth = 0;
                foreach (var href in candidates.Distinct().Take(6))
                {
                    var data = await DownloadAsync(baseUri, href);
                    if (data == null || Normalize(data) is not { } png) continue;
                    int width = PngWidth(png);
                    if (width > bestWidth) { best = png; bestWidth = width; }
                    if (bestWidth >= 64) break;
                }
                if (best != null && bestWidth > SavedWidth(host))
                {
                    Directory.CreateDirectory(Dir);
                    File.WriteAllBytes(PathFor(host), best);
                    Mem.TryRemove(host, out _);
                    Added?.Invoke();
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static int SavedWidth(string host)
        {
            try { return File.Exists(PathFor(host)) ? PngWidth(File.ReadAllBytes(PathFor(host))) : 0; }
            catch { return 0; }
        }

        /// <summary>Pixel width from a PNG's IHDR chunk (0 if it is not a PNG).</summary>
        private static int PngWidth(byte[] png) =>
            png.Length > 24 && png[1] == (byte)'P' && png[2] == (byte)'N' && png[3] == (byte)'G'
                ? (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19] : 0;

        /// <summary>Icon hrefs from the page's &lt;link&gt; tags, largest first (by the sizes attribute; touch icons count as 180 px), SVG skipped.</summary>
        private static IEnumerable<string> IconLinks(string html)
        {
            var found = new List<(int Size, string Href)>();
            foreach (Match tag in Regex.Matches(html, @"<link\b[^>]*>", RegexOptions.IgnoreCase))
            {
                var rel = Regex.Match(tag.Value, @"\brel\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase).Groups[1].Value.ToLowerInvariant();
                var href = Regex.Match(tag.Value, @"\bhref\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase).Groups[1].Value;
                if (!rel.Contains("icon") || rel.Contains("mask-icon") || href.Length == 0) continue;
                href = WebUtility.HtmlDecode(href);
                if (href.Contains(".svg", StringComparison.OrdinalIgnoreCase) || href.StartsWith("data:image/svg", StringComparison.OrdinalIgnoreCase)) continue;
                var sizes = Regex.Match(tag.Value, @"\bsizes\s*=\s*[""'](\d+)x\d+", RegexOptions.IgnoreCase);
                int size = sizes.Success ? int.Parse(sizes.Groups[1].Value) : rel.Contains("apple") ? 180 : 16;
                found.Add((size, href));
            }
            return found.OrderByDescending(f => f.Size).Select(f => f.Href);
        }

        private static async Task<byte[]?> DownloadAsync(Uri baseUri, string href)
        {
            try
            {
                if (href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    int i = href.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
                    return i > 0 ? Convert.FromBase64String(href[(i + 7)..]) : null;
                }
                if (!Uri.TryCreate(baseUri, href, out var uri) || uri.Scheme is not ("http" or "https")) return null;
                using var resp = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode || resp.Content.Headers.ContentLength > 512_000) return null;
                return await resp.Content.ReadAsByteArrayAsync();
            }
            catch { return null; }
        }

        /// <summary>Decodes any image the browser can show (ico/png/gif/jpg), picks the smallest frame of 64 px or more (else the largest) and re-encodes it as PNG.</summary>
        private static byte[]? Normalize(byte[] data)
        {
            try
            {
                using var input = new MemoryStream(data);
                var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames.Where(f => f.PixelWidth >= 64).OrderBy(f => f.PixelWidth).FirstOrDefault()
                            ?? decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
                if (frame.PixelWidth < 8 || frame.PixelHeight < 8) return null; // 1x1 tracking pixels and the like
                BitmapSource source = frame;
                if (frame.PixelWidth > 64)
                    source = new TransformedBitmap(frame, new ScaleTransform(64.0 / frame.PixelWidth, 64.0 / frame.PixelWidth));
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                using var output = new MemoryStream();
                encoder.Save(output);
                return output.ToArray();
            }
            catch { return null; }
        }

        private static ConcurrentDictionary<string, DateTime> LoadFailed()
        {
            var map = new ConcurrentDictionary<string, DateTime>();
            try
            {
                foreach (var line in File.ReadLines(FailedPath))
                {
                    var parts = line.Split('|');
                    if (parts.Length == 2 && long.TryParse(parts[1], out var ticks)) map[parts[0]] = new DateTime(ticks, DateTimeKind.Utc);
                }
            }
            catch { }
            return map;
        }

        private static void SaveFailed()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllLines(FailedPath, Failed.Select(f => $"{f.Key}|{f.Value.Ticks}"));
            }
            catch { }
        }
    }

    /// <summary>The icon a bookmark shows: the yellow folder, the site's own icon, or a globe until that icon is fetched.</summary>
    public static class BookmarkIcons
    {
        public static ImageSource For(Bookmark b) =>
            b.Children != null ? (ImageSource)Application.Current.FindResource("FolderIcon")
                               : FaviconService.Get(b.Url) ?? (ImageSource)Application.Current.FindResource("GlobeIcon");
    }

    /// <summary>XAML: Bookmark -> its icon (see <see cref="BookmarkIcons"/>).</summary>
    public sealed class BookmarkIconConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is Bookmark b ? BookmarkIcons.For(b) : null;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
