using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Browse.Services;

namespace Browse.Models
{
    /// <summary>A YouTube channel in the Feeds page (its RSS feed, its folder and its icon).</summary>
    public sealed class FeedChannel : INotifyPropertyChanged
    {
        private int _count;

        public string Title { get; init; } = "";
        public string XmlUrl { get; init; } = "";
        public string HtmlUrl { get; init; } = "";
        public string FolderName { get; init; } = "";
        public string ThumbnailUrl { get; init; } = "";

        public ImageSource? Thumb => ImageCache.Get(ThumbnailUrl, 128);
        public string Initial => Title.Length > 0 ? Title[..1].ToUpperInvariant() : "?";
        public Visibility InitialVisibility => ThumbnailUrl.Length > 0 ? Visibility.Collapsed : Visibility.Visible;

        private bool _failed;

        /// <summary>Videos of this channel in the current filter.</summary>
        public int Count { get => _count; set { _count = value; OnChanged(nameof(CountText)); OnChanged(nameof(Dim)); } }

        /// <summary>The channel's feed could not be read the last time it was loaded.</summary>
        public bool Failed { get => _failed; set { _failed = value; OnChanged(nameof(CountText)); OnChanged(nameof(Dim)); } }

        public string CountText => _failed ? "feed unavailable" : _count == 0 ? "no videos in this filter" : _count == 1 ? "1 video" : $"{_count} videos";

        /// <summary>Channels with nothing to show right now stay in the list, but faded.</summary>
        public double Dim => _failed || _count == 0 ? 0.5 : 1.0;

        public override string ToString() => Title; // what screen readers and UI automation call the item

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    }

    public sealed class FeedFolder(string title, List<FeedChannel> channels)
    {
        public string Title { get; } = title;
        public List<FeedChannel> Channels { get; } = channels;
        public string CountText => Channels.Count.ToString();
        public override string ToString() => Title;
    }

    public sealed class FeedVideo
    {
        public string Title { get; init; } = "";
        public string Link { get; init; } = "";
        public string VideoId { get; init; } = "";
        public DateTime? Published { get; init; }   // UTC
        public string ThumbnailUrl { get; init; } = "";
        public FeedChannel? Channel { get; set; }

        public ImageSource? Thumb => ImageCache.Get(ThumbnailUrl, 240);

        public string Subtitle => $"{Channel?.Title}  ·  {AgeText}";

        public override string ToString() => Title;

        public string AgeText
        {
            get
            {
                if (Published is not { } p) return "";
                var age = DateTime.UtcNow - p;
                if (age.TotalMinutes < 1) return "just now";
                if (age.TotalHours < 1) return $"{(int)age.TotalMinutes} min ago";
                if (age.TotalDays < 1) return $"{(int)age.TotalHours} h ago";
                if (age.TotalDays < 14) return $"{(int)age.TotalDays} d ago";
                return p.ToLocalTime().ToString("d");
            }
        }
    }
}
