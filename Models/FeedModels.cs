using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Browse.Services;

namespace Browse.Models
{
    /// <summary>A YouTube channel in the Feeds page (its address, its folder and its icon).</summary>
    public sealed class FeedChannel
    {
        public string Title { get; init; } = "";
        public string XmlUrl { get; init; } = "";
        public string HtmlUrl { get; init; } = "";
        public string FolderName { get; init; } = "";
        public string ThumbnailUrl { get; init; } = "";

        public ImageSource? Thumb => ImageCache.Get(ThumbnailUrl, 128);
        public string Initial => Title.Length > 0 ? Title[..1].ToUpperInvariant() : "?";
        public Visibility InitialVisibility => ThumbnailUrl.Length > 0 ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>The channel's videos page on YouTube, newest first. Built from the channel id kept in the stored addresses; "" if there is none.</summary>
        public string VideosUrl
        {
            get
            {
                var id = Regex.Match(XmlUrl, @"channel_id=([\w-]+)");
                if (!id.Success) id = Regex.Match(HtmlUrl, @"youtube\.com/channel/([\w-]+)");
                if (id.Success) return $"https://www.youtube.com/channel/{id.Groups[1].Value}/videos";
                return HtmlUrl.Length > 0 ? HtmlUrl.TrimEnd('/') + "/videos" : "";
            }
        }

        public override string ToString() => Title; // what screen readers and UI automation call the item
    }

    public sealed class FeedFolder(string title, List<FeedChannel> channels)
    {
        public string Title { get; } = title;
        public List<FeedChannel> Channels { get; } = channels;
        public string CountText => Channels.Count.ToString();
        public override string ToString() => Title;
    }
}
