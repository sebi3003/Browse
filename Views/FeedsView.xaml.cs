using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Browse.Models;
using Browse.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Browse.Views
{
    /// <summary>
    /// Feeds: folders, channels, a merged feed of videos, and the YouTube page of the clicked video, in four columns.
    /// Picking a folder loads the RSS feeds of all its channels; picking a channel narrows the list; picking a video opens
    /// its YouTube watch page in the fourth column and starts it.
    /// </summary>
    public partial class FeedsView : UserControl
    {
        private const string PlayScript =
            "(() => { const v = document.querySelector('video.html5-main-video') || document.querySelector('video');" +
            " if (!v) return 'none'; if (!v.paused && v.readyState > 2) return 'playing';" +
            " v.play().catch(() => {}); return 'started'; })()";

        private readonly ObservableCollection<FeedFolder> _folders = [];
        private readonly ObservableCollection<FeedChannel> _channels = [];
        private readonly ObservableCollection<FeedVideo> _videos = [];
        private FeedStore.Data _data = new();
        private FeedFolder? _folder;
        private List<FeedVideo> _folderVideos = [], _filtered = [];
        private string _filter = "week";
        private int _loadId, _playId;
        private bool _updating, _started;
        private object? _channelSelectedAtMouseDown;
        private CoreWebView2CreationProperties? _props;
        private WebView2? _player;

        public FeedsView()
        {
            InitializeComponent();
            FolderList.ItemsSource = _folders;
            ChannelList.ItemsSource = _channels;
            VideoList.ItemsSource = _videos;
            ChannelList.PreviewMouseLeftButtonDown += (_, _) => _channelSelectedAtMouseDown = ChannelList.SelectedItem;
            FolderList.MouseRightButtonUp += FolderList_RightClick;
            ChannelList.MouseRightButtonUp += ChannelList_RightClick;
        }

        // ---------- right-click: remove a folder or a channel ----------

        private void ShowRemoveMenu(ListBox list, string text, Action remove)
        {
            var item = new MenuItem
            {
                Header = text,
                Icon = new Image { Source = (ImageSource)FindResource("DeleteIcon"), Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) }, // Capture's red trash can
            };
            item.Click += (_, _) => remove();
            var menu = new ContextMenu { PlacementTarget = list };
            menu.Items.Add(item);
            menu.IsOpen = true;
        }

        private void FolderList_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (ItemsControl.ContainerFromElement(FolderList, e.OriginalSource as DependencyObject) is not ListBoxItem { DataContext: FeedFolder folder }) return;
            e.Handled = true;
            ShowRemoveMenu(FolderList, "Remove folder", () => RemoveFolder(folder));
        }

        private void ChannelList_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (ItemsControl.ContainerFromElement(ChannelList, e.OriginalSource as DependencyObject) is not ListBoxItem { DataContext: FeedChannel channel }) return;
            e.Handled = true;
            ShowRemoveMenu(ChannelList, "Remove channel", () => RemoveChannel(channel));
        }

        // Removing asks first, like deleting a bookmark does.
        private void RemoveFolder(FeedFolder folder)
        {
            int n = folder.Channels.Count;
            string what = n == 0 ? $"the empty folder \"{folder.Title}\"" : $"the folder \"{folder.Title}\" and its {(n == 1 ? "1 channel" : $"{n} channels")}";
            if (MessageBox.Show(Window.GetWindow(this), $"Remove {what}?", "Browse", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            FeedStore.RemoveFolder(folder.Title);
            _data = FeedStore.Load();                       // pick up the change, so a later save here cannot bring it back
            if (_data.LastFolder == folder.Title) _data.LastFolder = "";
            FeedStore.Save(_data);
            RebuildFolders();
        }

        private void RemoveChannel(FeedChannel channel)
        {
            string where = string.IsNullOrEmpty(channel.FolderName) ? "Unsorted" : channel.FolderName;
            if (MessageBox.Show(Window.GetWindow(this), $"Remove the channel \"{channel.Title}\" from the folder \"{where}\"?", "Browse",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            FeedStore.RemoveChannel(channel.XmlUrl);
            _data = FeedStore.Load();
            RebuildFolders();                               // stays in the same folder (LastFolder is still it)
        }

        /// <summary>The browser profile the player shares, so YouTube sees the same cookies and the same blocking as the tabs.</summary>
        public void Initialize(CoreWebView2CreationProperties props) => _props = props;

        /// <summary>The page was opened.</summary>
        public void Open()
        {
            if (_started) return;
            _started = true;
            _data = FeedStore.Load();
            _filter = _data.Filter is "today" or "week" or "last5" or "all" ? _data.Filter : "week";
            RebuildFolders();
        }

        /// <summary>The page was closed: stop the video.</summary>
        public void Close()
        {
            _playId++;
            if (_player?.CoreWebView2 is { } c) c.Navigate("about:blank");
            NowPlaying.Text = "Pick a video";
            _updating = true;
            VideoList.SelectedItem = null; // so picking the same video again plays it again
            _updating = false;
        }

        /// <summary>Adds the channels SebiTool has and this page lacks. Returns how many were added.</summary>
        public int ReimportChannels()
        {
            _data = FeedStore.Load();
            int added = FeedStore.ImportFromSebiTool(_data);
            FeedStore.Save(_data);
            if (_started) RebuildFolders();
            return added;
        }

        /// <summary>A channel was added from outside (a YouTube tab's menu): reload the list, so a later save here cannot drop it.</summary>
        public void ChannelsChanged()
        {
            if (!_started) return; // not opened yet: Open() loads the current file anyway
            _data = FeedStore.Load();
            RebuildFolders();
        }

        // The "+" next to Folders: ask for a name and create an empty folder (channels are added from a YouTube tab's menu).
        private void NewFolder_Click(object sender, RoutedEventArgs e)
        {
            var name = NameDialog.Ask(Window.GetWindow(this), "New folder", "Name of the new Feeds folder:");
            if (name == null) return;
            var (created, folderName) = FeedStore.AddFolder(name);
            _data = FeedStore.Load();          // pick up the change, so a later save here cannot drop it
            _data.LastFolder = folderName;     // and show the new (or already existing) folder
            FeedStore.Save(_data);
            RebuildFolders();
            if (!created) StatusText.Text = $"\"{folderName}\" already exists";
        }

        // ---------- folders, channels, videos ----------

        private void RebuildFolders()
        {
            var folders = FeedStore.BuildFolders(_data);
            _updating = true;
            _folders.Clear();
            foreach (var f in folders) _folders.Add(f);
            _updating = false;
            UpdateChips();
            FolderList.SelectedItem = folders.FirstOrDefault(f => f.Title == _data.LastFolder) ?? folders.FirstOrDefault();
            if (folders.Count == 0) StatusText.Text = "No channels yet (none found in SebiTool)";
        }

        private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updating || FolderList.SelectedItem is not FeedFolder folder) return;
            _folder = folder;
            _data.LastFolder = folder.Title;
            FeedStore.Save(_data);
            _ = LoadFolderAsync(folder);
        }

        private async Task LoadFolderAsync(FeedFolder folder)
        {
            int id = ++_loadId;
            StatusText.Text = "Loading…";
            using var gate = new SemaphoreSlim(8);
            var results = await Task.WhenAll(folder.Channels.Select(async ch =>
            {
                await gate.WaitAsync();
                try
                {
                    var items = await FeedFetchService.FetchAsync(ch.XmlUrl);
                    foreach (var v in items) v.Channel = ch;
                    ch.Failed = false;
                    return items;
                }
                catch
                {
                    ch.Failed = true; // shown on the channel, so a missing feed is not mistaken for "nothing new"
                    return new List<FeedVideo>();
                }
                finally { gate.Release(); }
            }));
            if (id != _loadId) return; // another folder was picked meanwhile
            _folderVideos = results.SelectMany(x => x).ToList();
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            if (_folder == null) return;
            var keep = ChannelList.SelectedItem as FeedChannel;

            IEnumerable<FeedVideo> q = _folderVideos;
            q = _filter switch
            {
                "today" => q.Where(v => v.Published?.ToLocalTime().Date == DateTime.Now.Date),
                "week" => q.Where(v => v.Published >= DateTime.UtcNow.AddDays(-7)),
                "last5" => q.GroupBy(v => v.Channel).SelectMany(g => g.OrderByDescending(v => v.Published ?? DateTime.MinValue).Take(5)),
                _ => q,
            };
            _filtered = q.OrderByDescending(v => v.Published ?? DateTime.MinValue).ToList();

            foreach (var ch in _folder.Channels) ch.Count = _filtered.Count(v => ReferenceEquals(v.Channel, ch));
            _updating = true;
            _channels.Clear();
            foreach (var ch in _folder.Channels) _channels.Add(ch); // every channel of the folder; those with nothing in this filter are faded
            ChannelList.SelectedItem = keep != null && _channels.Contains(keep) ? keep : null;
            _updating = false;
            ShowVideos();
        }

        private void ShowVideos()
        {
            var channel = ChannelList.SelectedItem as FeedChannel;
            var list = channel == null ? _filtered : _filtered.Where(v => ReferenceEquals(v.Channel, channel)).ToList();
            bool fallback = false;
            if (channel != null && list.Count == 0)
            {
                // A channel you picked is never a dead end (a newly added one may not have posted this week): show its latest videos.
                list = _folderVideos.Where(v => ReferenceEquals(v.Channel, channel)).OrderByDescending(v => v.Published ?? DateTime.MinValue).Take(5).ToList();
                fallback = list.Count > 0;
            }
            _updating = true;
            _videos.Clear();
            foreach (var v in list) _videos.Add(v);
            _updating = false;
            StatusText.Text = _folder is { Channels.Count: 0 } ? "empty folder - add channels from a YouTube tab's right-click menu"
                : fallback ? "nothing in this filter - latest 5" : list.Count == 1 ? "1 video" : $"{list.Count} videos";
        }

        private void ChannelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_updating) ShowVideos();
        }

        // Clicking the channel that is already selected selects none again, which shows the whole folder's feed.
        private void ChannelList_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (ItemsControl.ContainerFromElement(ChannelList, e.OriginalSource as DependencyObject) is ListBoxItem { DataContext: FeedChannel ch }
                && ReferenceEquals(ch, _channelSelectedAtMouseDown))
                ChannelList.SelectedItem = null;
        }

        private void Filter_Click(object sender, RoutedEventArgs e)
        {
            _filter = (string)((Button)sender).Tag;
            _data.Filter = _filter;
            FeedStore.Save(_data);
            UpdateChips();
            ApplyFilter();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            FeedFetchService.ClearCache();
            if (_folder != null) _ = LoadFolderAsync(_folder);
        }

        private void UpdateChips()
        {
            var on = (Brush)FindResource("Selected");
            var off = (Brush)FindResource("Slate");
            foreach (var chip in new[] { TodayChip, WeekChip, Last5Chip, AllChip })
                chip.Background = (string)chip.Tag == _filter ? on : off;
        }

        // ---------- the player (4th column) ----------

        private void VideoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_updating && VideoList.SelectedItem is FeedVideo video) _ = PlayAsync(video);
        }

        private async Task PlayAsync(FeedVideo video)
        {
            int id = ++_playId;
            NowPlaying.Text = video.Channel == null ? video.Title : $"{video.Title}  —  {video.Channel.Title}";
            try
            {
                if (_player == null) await CreatePlayerAsync();
                if (id != _playId || _player?.CoreWebView2 is not { } c) return;
                // The full watch page, not the embeddable player (which refuses to run as a top-level page).
                c.Navigate($"https://www.youtube.com/watch?v={video.VideoId}");
            }
            catch (Exception ex) { App.LogCrash("feeds player", ex); }
        }

        private async Task CreatePlayerAsync()
        {
            _player = new WebView2 { CreationProperties = _props, DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0A, 0x0C, 0x10) };
            PlayerHost.Child = _player;
            await _player.EnsureCoreWebView2Async();
            var c = _player.CoreWebView2;
            await AdBlocker.AttachAsync(c); // the same blocking as the tabs, YouTube ads included
            c.Settings.IsPasswordAutosaveEnabled = false;
            c.Settings.IsGeneralAutofillEnabled = false;
            c.NewWindowRequested += (_, e) => e.Handled = true; // no popups out of the player
            c.NavigationCompleted += (_, _) => _ = EnsurePlayingAsync(_playId);
            c.ContainsFullScreenElementChanged += (_, _) => SetPlayerFullscreen(c.ContainsFullScreenElement);
        }

        // ---------- real fullscreen ----------
        // The F key or YouTube's fullscreen button only fills the player's own area. So the other columns are folded away and the
        // owner (the main window) makes the window itself fullscreen: the video then fills the whole screen. Leaving it restores all.

        private bool _playerFullscreen;
        private GridLength[] _savedWidths = [];

        /// <summary>The player entered or left fullscreen; the owner should make the window fullscreen accordingly.</summary>
        public event Action<bool>? PlayerFullscreenChanged;

        private void SetPlayerFullscreen(bool on)
        {
            if (on == _playerFullscreen) return;
            _playerFullscreen = on;
            var defs = Columns.ColumnDefinitions;
            if (on) _savedWidths = defs.Take(6).Select(d => d.Width).ToArray();
            for (int i = 0; i < 6; i++) defs[i].Width = on ? new GridLength(0) : _savedWidths[i]; // keeps any widths the user dragged
            foreach (UIElement child in Columns.Children)
                if (Grid.GetColumn(child) < 6) child.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            PlayerHeader.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            PlayerHost.CornerRadius = on ? new CornerRadius(0) : new CornerRadius(4);
            Columns.Margin = on ? new Thickness(0) : new Thickness(6, 6, 6, 6);
            PlayerFullscreenChanged?.Invoke(on);
        }

        // A page that was just opened does not start by itself (autoplay with sound needs a user action), so press play for the
        // user: run play() with the "user gesture" flag, until the video is playing. Only this player does this, not the tabs.
        private async Task EnsurePlayingAsync(int id)
        {
            string args = JsonSerializer.Serialize(new { expression = PlayScript, userGesture = true, returnByValue = true });
            for (int i = 0; i < 24 && id == _playId; i++)
            {
                await Task.Delay(500);
                if (id != _playId || _player?.CoreWebView2 is not { } c || c.Source.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
                try
                {
                    if ((await c.CallDevToolsProtocolMethodAsync("Runtime.evaluate", args)).Contains("\"playing\"")) return;
                }
                catch { /* the page is still loading */ }
            }
        }
    }
}
