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
    /// Feeds: folders, channels and the YouTube page, in three columns. It no longer reads YouTube's RSS feeds (they stopped
    /// working); it only manages and shows the channels. Picking a channel opens its YouTube videos page in the third column,
    /// where videos are picked and played like in any tab.
    /// </summary>
    public partial class FeedsView : UserControl
    {
        private const string PlayScript =
            "(() => { const v = document.querySelector('video.html5-main-video') || document.querySelector('video');" +
            " if (!v) return 'none'; if (!v.paused && v.readyState > 2) return 'playing';" +
            " v.play().catch(() => {}); return 'started'; })()";

        private readonly ObservableCollection<FeedFolder> _folders = [];
        private readonly ObservableCollection<FeedChannel> _channels = [];
        private FeedStore.Data _data = new();
        private int _playId;
        private bool _updating, _started;
        private object? _channelSelectedAtMouseDown;
        private CoreWebView2CreationProperties? _props;
        private WebView2? _player;

        public FeedsView()
        {
            InitializeComponent();
            FolderList.ItemsSource = _folders;
            ChannelList.ItemsSource = _channels;
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
            if (!ConfirmWindow.Ask(Window.GetWindow(this), $"Remove {what}?", "Remove")) return;
            FeedStore.RemoveFolder(folder.Title);
            _data = FeedStore.Load();                       // pick up the change, so a later save here cannot bring it back
            if (_data.LastFolder == folder.Title) _data.LastFolder = "";
            FeedStore.Save(_data);
            RebuildFolders();
        }

        private void RemoveChannel(FeedChannel channel)
        {
            string where = string.IsNullOrEmpty(channel.FolderName) ? "Unsorted" : channel.FolderName;
            if (!ConfirmWindow.Ask(Window.GetWindow(this), $"Remove the channel \"{channel.Title}\" from the folder \"{where}\"?", "Remove")) return;
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
            RebuildFolders();
        }

        /// <summary>The page was closed: stop the video.</summary>
        public void Close()
        {
            _playId++;
            if (_player?.CoreWebView2 is { } c) c.Navigate("about:blank");
            NowPlaying.Text = "Pick a channel";
            _updating = true;
            ChannelList.SelectedItem = null; // so picking the same channel again opens it again
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
            if (!created) NowPlaying.Text = $"\"{folderName}\" already exists";
        }

        // ---------- folders and channels ----------

        private void RebuildFolders()
        {
            var folders = FeedStore.BuildFolders(_data);
            _updating = true;
            _folders.Clear();
            foreach (var f in folders) _folders.Add(f);
            _updating = false;
            FolderList.SelectedItem = folders.FirstOrDefault(f => f.Title == _data.LastFolder) ?? folders.FirstOrDefault();
            if (folders.Count == 0) NowPlaying.Text = "No channels yet - add them from a YouTube tab's right-click menu";
        }

        private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updating || FolderList.SelectedItem is not FeedFolder folder) return;
            _data.LastFolder = folder.Title;
            FeedStore.Save(_data);
            _updating = true;
            _channels.Clear();
            foreach (var ch in folder.Channels) _channels.Add(ch);
            _updating = false;
            if (folder.Channels.Count == 0) NowPlaying.Text = "Empty folder - add channels from a YouTube tab's right-click menu";
        }

        private void ChannelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_updating && ChannelList.SelectedItem is FeedChannel channel) _ = OpenChannelAsync(channel);
        }

        // Clicking the channel that is already selected opens its page again (the way back after clicking around in the player).
        private void ChannelList_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (ItemsControl.ContainerFromElement(ChannelList, e.OriginalSource as DependencyObject) is ListBoxItem { DataContext: FeedChannel ch }
                && ReferenceEquals(ch, _channelSelectedAtMouseDown))
                _ = OpenChannelAsync(ch);
        }

        // ---------- the player (3rd column) ----------

        private async Task OpenChannelAsync(FeedChannel channel)
        {
            int id = ++_playId;
            NowPlaying.Text = channel.Title;
            string url = channel.VideosUrl;
            if (url.Length == 0) { NowPlaying.Text = $"{channel.Title}  -  no channel address"; return; }
            try
            {
                if (_player == null) await CreatePlayerAsync();
                if (id != _playId || _player?.CoreWebView2 is not { } c) return;
                c.Navigate(url);
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
            // YouTube moves between its pages without loading a new document, so watch the address as well as the loads.
            c.SourceChanged += (_, _) => StartIfWatchPage();
            c.NavigationCompleted += (_, _) => StartIfWatchPage();
            c.DocumentTitleChanged += (_, _) => { if (!c.DocumentTitle.StartsWith("about:", StringComparison.OrdinalIgnoreCase) && c.DocumentTitle.Length > 0) NowPlaying.Text = c.DocumentTitle; };
            c.ContainsFullScreenElementChanged += (_, _) => SetPlayerFullscreen(c.ContainsFullScreenElement);
        }

        private void StartIfWatchPage()
        {
            if (_player?.CoreWebView2 is { } c && c.Source.Contains("/watch", StringComparison.OrdinalIgnoreCase))
                _ = EnsurePlayingAsync(++_playId);
        }

        // ---------- real fullscreen ----------
        // The F key or YouTube's fullscreen button only fills the player's own area. So the other columns are folded away and the
        // owner (the main window) makes the window itself fullscreen: the video then fills the whole screen. Leaving it restores all.

        private bool _playerFullscreen;
        private GridLength[] _savedWidths = [];
        private const int SideColumns = 4; // folders, divider, channels, divider

        /// <summary>The player entered or left fullscreen; the owner should make the window fullscreen accordingly.</summary>
        public event Action<bool>? PlayerFullscreenChanged;

        private void SetPlayerFullscreen(bool on)
        {
            if (on == _playerFullscreen) return;
            _playerFullscreen = on;
            var defs = Columns.ColumnDefinitions;
            if (on) _savedWidths = defs.Take(SideColumns).Select(d => d.Width).ToArray();
            for (int i = 0; i < SideColumns; i++) defs[i].Width = on ? new GridLength(0) : _savedWidths[i]; // keeps any widths the user dragged
            foreach (UIElement child in Columns.Children)
                if (Grid.GetColumn(child) < SideColumns) child.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
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
                if (id != _playId || _player?.CoreWebView2 is not { } c || !c.Source.Contains("/watch", StringComparison.OrdinalIgnoreCase)) return;
                try
                {
                    if ((await c.CallDevToolsProtocolMethodAsync("Runtime.evaluate", args)).Contains("\"playing\"")) return;
                }
                catch { /* the page is still loading */ }
            }
        }
    }
}
