using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Web.WebView2.Core;

namespace Browse.Services
{
    /// <summary>
    /// Finds the channel behind a YouTube page (a video, a channel, a handle) for "Add channel to folder": its name, RSS feed address and icon.
    /// The open page is asked first (exact for the video showing right now, even after YouTube changed videos without reloading);
    /// failing that, the address and the page's HTML are read. The approach follows SebiTool's former channel resolver.
    /// </summary>
    public static class YouTubeChannelService
    {
        public sealed record Channel(string Title, string XmlUrl, string HtmlUrl, string ThumbnailUrl);

        private sealed record PageInfo(string? Id, string? Title, string? Thumb);

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
            return http;
        }

        // Runs inside the YouTube tab. movie_player.getPlayerResponse() is the player's current video (updated when YouTube
        // switches videos in place); on channel pages the canonical link and the page's own meta tags name the channel.
        private const string PageScript = """
            (() => {
              const q = s => document.querySelector(s);
              const onChannelPage = /^\/(channel|@|c\/|user\/)/.test(location.pathname);
              let id = '', title = '', thumb = '';
              try {
                const d = document.getElementById('movie_player')?.getPlayerResponse?.()?.videoDetails;
                if (d && location.pathname.startsWith('/watch')) { id = d.channelId || ''; title = d.author || ''; }
              } catch (e) {}
              const canonical = q('link[rel="canonical"]')?.href || '';
              if (!id) { const m = canonical.match(/\/channel\/(UC[\w-]{20,})/); if (m) id = m[1]; }
              if (!id) id = q('meta[itemprop="channelId"]')?.content || q('meta[itemprop="identifier"]')?.content || '';
              if (onChannelPage) { title = title || q('meta[property="og:title"]')?.content || ''; thumb = q('meta[property="og:image"]')?.content || ''; }
              if (!thumb) thumb = q('#owner img, ytd-video-owner-renderer img')?.src || '';
              return JSON.stringify({ id, title, thumb });
            })()
            """;

        /// <summary>True for YouTube pages (www, m, music), not for youtu.be or other sites.</summary>
        public static bool IsYouTubeUrl(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
            && (u.Host == "youtube.com" || u.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase));

        public static async Task<Channel> ResolveAsync(CoreWebView2? page, string pageUrl)
        {
            var info = await AskPageAsync(page);
            string? id = IsChannelId(info?.Id) ? info!.Id : ChannelIdFromUrl(pageUrl) ?? await ScrapeChannelIdAsync(pageUrl);
            if (id == null) throw new InvalidOperationException("No channel was found on this page.");

            string xmlUrl = $"https://www.youtube.com/feeds/videos.xml?channel_id={id}";
            string htmlUrl = $"https://www.youtube.com/channel/{id}";

            string title = await FeedTitleAsync(xmlUrl) ?? info?.Title ?? "";
            if (title.Length == 0) throw new InvalidOperationException("The channel's feed could not be read.");

            // The channel page's own picture is large (900 px); the small one from a watch page is only a fallback.
            string thumb = await ScrapeThumbnailAsync(htmlUrl) ?? info?.Thumb ?? "";
            return new Channel(title, xmlUrl, htmlUrl, thumb);
        }

        private static async Task<PageInfo?> AskPageAsync(CoreWebView2? page)
        {
            if (page == null) return null;
            try
            {
                string raw = await page.ExecuteScriptAsync(PageScript);          // a JSON string holding JSON
                string? json = JsonSerializer.Deserialize<string>(raw);
                return json == null ? null : JsonSerializer.Deserialize<PageInfo>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch { return null; }
        }

        private static bool IsChannelId(string? id) => id != null && Regex.IsMatch(id, @"^UC[\w-]{20,}$");

        private static string? ChannelIdFromUrl(string url)
        {
            var m = Regex.Match(url, @"youtube\.com/channel/(UC[\w-]{20,})");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static async Task<string?> FeedTitleAsync(string xmlUrl)
        {
            try
            {
                var doc = XDocument.Parse(await Http.GetStringAsync(xmlUrl));
                XNamespace atom = "http://www.w3.org/2005/Atom";
                return doc.Root?.Element(atom + "title")?.Value is { Length: > 0 } t ? t.Trim() : null;
            }
            catch { return null; }
        }

        /// <summary>The channel page's picture (large), or null if it could not be read.</summary>
        public static Task<string?> FetchThumbnailAsync(string channelHtmlUrl) => ScrapeThumbnailAsync(channelHtmlUrl);

        private static async Task<string?> ScrapeThumbnailAsync(string channelHtmlUrl)
        {
            try
            {
                string html = await Http.GetStringAsync(channelHtmlUrl);
                foreach (var pattern in new[] { @"<meta property=""og:image"" content=""([^""]+)""", @"<meta property='og:image' content='([^']+)'", @"""avatar""\s*:\s*\{[^}]*""url""\s*:\s*""([^""]+)""" })
                {
                    var m = Regex.Match(html, pattern);
                    if (m.Success) return m.Groups[1].Value;
                }
            }
            catch { }
            return null;
        }

        // For pages that name a channel only by handle or in their HTML.
        private static async Task<string?> ScrapeChannelIdAsync(string url)
        {
            try
            {
                string html = await Http.GetStringAsync(url);
                foreach (var pattern in new[]
                {
                    @"<link rel=[""']canonical[""'] href=[""']https://www\.youtube\.com/channel/(UC[\w-]{20,})[""']",
                    @"<meta property=[""']og:url[""'] content=[""']https://www\.youtube\.com/channel/(UC[\w-]{20,})[""']",
                    @"""externalId""\s*:\s*""(UC[\w-]{20,})""",
                    @"""channelId""\s*:\s*""(UC[\w-]{20,})""",
                    @"""browseId""\s*:\s*""(UC[\w-]{20,})""",
                })
                {
                    var m = Regex.Match(html, pattern);
                    if (m.Success) return m.Groups[1].Value;
                }
            }
            catch { }
            return null;
        }
    }
}
