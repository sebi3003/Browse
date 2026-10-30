using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;

namespace Browse.Models
{
    public sealed class BrowserTab(WebView2 view) : INotifyPropertyChanged
    {
        private string _title = "New Tab";
        private ImageSource? _icon;

        public WebView2 View { get; } = view;
        public string Url { get; set; } = "";
        /// <summary>The Settings tab: shows the settings page instead of a web page; its WebView2 is never started and it is not saved in the session.</summary>
        public bool IsSettings { get; init; }
        public bool IsLoading { get; set; }
        public Task? Init { get; set; }
        /// <summary>PNG of the page as it was when the tab was last left; the tab switch effect blends from/to it.</summary>
        public byte[]? Snapshot { get; set; }

        public string Title { get => _title; set => Set(ref _title, value); }
        public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            field = value;
            PropertyChanged?.Invoke(this, new(name));
        }
    }
}
