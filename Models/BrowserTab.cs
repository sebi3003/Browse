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
        public bool IsLoading { get; set; }
        public Task? Init { get; set; }

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
