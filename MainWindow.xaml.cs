using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Browse.Models;
using Browse.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Browse
{
    public partial class MainWindow : Window
    {
        private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(4);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid folderId, uint flags, IntPtr token, out IntPtr path);

        // Asked from Windows, so a Downloads folder the user has moved is respected.
        private static readonly string DownloadsDir = FindDownloadsDir();

        private static string FindDownloadsDir()
        {
            try
            {
                if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0)
                {
                    var dir = Marshal.PtrToStringUni(p);
                    Marshal.FreeCoTaskMem(p);
                    if (!string.IsNullOrEmpty(dir)) return dir;
                }
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        private static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int i = 1; ; i++)
            {
                var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(candidate)) return candidate;
            }
        }

        private readonly BrowserStore _store = BrowserStore.Load();
        private readonly ObservableCollection<BrowserTab> _tabs = [];
        private readonly Stack<string> _closed = new();
        private readonly CoreWebView2CreationProperties _props = new() { UserDataFolder = BrowserStore.ProfileDir };
        private readonly UpdateService? _updates;
        private readonly string? _startUrl;
        private BrowserTab? _current;
        private bool _suppressText, _fullscreen;
        private WindowState _stateBeforeFullscreen;
        private Point _dragStart;
        private BrowserTab? _dragTab;

        public MainWindow(string? startUrl)
        {
            _startUrl = startUrl;
            AdBlocker.Start();
            ThemeService.Init(_store);
            InitializeComponent();
            TabStrip.ItemsSource = _tabs;
            DownloadStrip.ItemsSource = _downloads;
            _store.Bookmarks.CollectionChanged += (_, e) => OnBookmarksChanged(e);
            _store.PrivateBookmarks.CollectionChanged += (_, e) => OnBookmarksChanged(e);
            FaviconService.Added += ScheduleBarRefresh;
            _store.Profile = BrowserStore.Work; // always start on Work, even if Private was open when Browse closed
            ApplyProfile();
            try { _updates = new UpdateService(AppConfig.GitHubRepoUrl, AppConfig.GitHubAccessToken); } catch { }
            Loaded += (_, _) => { RestoreTabs(); StartUpdateChecks(); StartIconBackfill(); };
            Closed += (_, _) => ExitProcess();
            InitFolderMenus();
            InitCacheChip();
            InitBookmarkSync();
            InitSettings();
            InitCurl();
            InitServers();
            FeedsPanel.Initialize(_props); // the Feeds page's player shares the browser profile
            // The video's own fullscreen (F key, YouTube's button) makes the whole window fullscreen, not just its column.
            FeedsPanel.PlayerFullscreenChanged += on => SetFullscreen(on || _manualFullscreen);
            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int on = 1, caption = 0x100C0A; // COLORREF is BGR: #0A0C10 like the rest of the window (Windows 11; ignored on older builds)
                DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));       // dark title bar
                DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));  // title bar colour
            };
        }

        // ---------- Tabs ----------

        private void RestoreTabs()
        {
            UpdateBookmarkBar();
            // Restored tabs are created lazily: a WebView2 only starts when its tab is first shown. Only the active profile's
            // tabs are opened now; the other profile's are opened the first time you switch to it.
            var (urls, index) = SessionOf(_store.Profile);
            foreach (var url in urls) CreateTab(url);
            if (_startUrl != null) OpenTab(Normalize(_startUrl));
            else if (_tabs.Count == 0) NewBlankTab();
            else TabStrip.SelectedIndex = Math.Clamp(index, 0, _tabs.Count - 1);
        }

        // ---------- Tabs per profile ----------
        // Work and Private each have their own open tabs. Switching profile parks the tabs of the one being left (their pages
        // stay loaded, just hidden) and shows the other's, so you come back to exactly what you had open.

        private readonly Dictionary<string, List<BrowserTab>> _parkedTabs = new();
        private readonly Dictionary<string, int> _parkedIndex = new();

        private (List<string> Urls, int Index) SessionOf(string profile) =>
            profile == BrowserStore.Private ? (_store.PrivateSession, _store.PrivateSessionIndex) : (_store.Session, _store.SessionIndex);

        private void SwapTabs(string from, string to)
        {
            // A blank tab leaves the cursor in the address bar, and a focused address bar keeps its text instead of showing the new tab's URL.
            Keyboard.ClearFocus();
            _parkedIndex[from] = Math.Max(0, TabStrip.SelectedIndex);
            _parkedTabs[from] = _tabs.ToList();
            if (_current != null) _current.View.Visibility = Visibility.Collapsed;
            _current = null;
            ApplyChrome();
            _tabs.Clear(); // the strip empties; a selection change with nothing selected is ignored

            int index;
            if (_parkedTabs.Remove(to, out var parked))
            {
                foreach (var t in parked) _tabs.Add(t);
                index = _parkedIndex.GetValueOrDefault(to);
            }
            else
            {
                // first visit in this run: open what that profile had open last time
                var (urls, saved) = SessionOf(to);
                foreach (var url in urls) CreateTab(url);
                index = saved;
            }

            if (_tabs.Count == 0) NewBlankTab();
            else TabStrip.SelectedIndex = Math.Clamp(index, 0, _tabs.Count - 1);
        }

        private BrowserTab CreateTab(string url)
        {
            // Dark until the first page paints (matches the window's Bg brush), instead of WebView2's default white flash.
            var view = new WebView2
            {
                CreationProperties = _props,
                Visibility = Visibility.Collapsed,
                DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0A, 0x0C, 0x10),
            };
            view.GotFocus += (_, _) => CloseFolderMenu(); // a click into the page (the WebView is a separate window) dismisses an open folder panel
            var tab = new BrowserTab(view) { Url = url, Title = url };
            Host.Children.Add(view);
            _tabs.Add(tab);
            return tab;
        }

        private void OpenTab(string url)
        {
            SetFeedsMode(false); // a new tab leaves the Feeds page
            TabStrip.SelectedItem = CreateTab(url);
        }

        private Action? _endFx; // ends a running tab switch effect right away (the page it was heading to becomes visible)
        private bool _skipFx;   // closing a tab switches without the effect: the old page is about to be disposed

        private async Task SwitchToAsync(BrowserTab tab)
        {
            _endFx?.Invoke();
            var old = _current;
            bool fx = !_skipFx && _store.TabEffect != TabEffect.None && old != null && old != tab && IsLoaded && Host.IsVisible
                      && !tab.IsSettings && !old.IsSettings;
            _current = tab;
            ApplyChrome();
            UpdateChrome();
            if (tab.IsSettings)
            {
                if (old != null) old.View.Visibility = Visibility.Collapsed;
                return; // the settings page needs no WebView2
            }
            if (!(fx && await PlayTransitionAsync(old!, tab)))
            {
                if (old != null) old.View.Visibility = Visibility.Collapsed;
                if (_current == tab) tab.View.Visibility = Visibility.Visible;
            }
            await EnsureInitAsync(tab);
            if (_current == tab && !AddressBox.IsKeyboardFocused) tab.View.Focus();
        }

        // Burns the old page away into the new one. Returns false when there is nothing to blend (no snapshot of the target yet,
        // size changed, superseded by another switch); the caller then switches plainly. Snapshots are taken when a tab is left.
        private async Task<bool> PlayTransitionAsync(BrowserTab from, BrowserTab to)
        {
            var target = to.Snapshot == null ? Task.FromResult<PageFrame?>(null) : TabTransition.DecodeAsync(to.Snapshot);
            if (from.View.IsVisible && await TabTransition.CaptureAsync(from.View) is { } png) from.Snapshot = png;
            if (from.Snapshot == null || _current != to) return false;

            var pair = await Task.WhenAll(TabTransition.DecodeAsync(from.Snapshot), target);
            if (_current != to || pair[0] is not { } a) return false;
            // A tab that was never shown (a new one) has no snapshot: it starts dark like the window, so a dark frame stands in for it.
            var b = pair[1] ?? new PageFrame(a.Width, a.Height, Enumerable.Repeat(unchecked((int)0xFF0A0C10), a.Width * a.Height).ToArray());
            if (a.Width != b.Width || a.Height != b.Height) return false;
            var effect = TabEffects.Resolve(_store.TabEffect);
            double durationMs = TabTransition.DurationMs(effect);
            bool forward = _tabs.IndexOf(to) >= _tabs.IndexOf(from);
            var fx = await Task.Run(() => new TabTransition(a, b, effect, forward));
            if (_current != to) return false;

            var bmp = new WriteableBitmap(a.Width, a.Height, 96, 96, PixelFormats.Pbgra32, null);
            var rect = new Int32Rect(0, 0, a.Width, a.Height);
            void Draw(double t) { fx.Render(t); bmp.WritePixels(rect, fx.Output, a.Width * 4, 0); }
            Draw(0);
            TransitionLayer.Source = bmp;
            TransitionLayer.Visibility = Visibility.Visible;

            // A WebView2 hides and shows a frame or two off from WPF's own drawing, so each handover overlaps: the old page is
            // hidden only after the layer has been drawn, and the layer stays up a few frames after the new page is shown.
            var done = new TaskCompletionSource();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int frames = 0, shownAt = 0;
            void Frame(object? s, EventArgs e)
            {
                if (++frames == 1) { from.View.Visibility = Visibility.Collapsed; clock.Restart(); }
                double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / durationMs);
                Draw(t);
                if (t < 1) return;
                if (shownAt == 0)
                {
                    shownAt = frames;
                    if (_current == to) to.View.Visibility = Visibility.Visible;
                }
                else if (frames - shownAt >= 3) End();
            }
            void End()
            {
                CompositionTarget.Rendering -= Frame;
                from.View.Visibility = Visibility.Collapsed;
                if (_current == to) to.View.Visibility = Visibility.Visible;
                TransitionLayer.Visibility = Visibility.Collapsed;
                TransitionLayer.Source = null;
                _endFx = null;
                done.TrySetResult();
            }
            _endFx = End;
            CompositionTarget.Rendering += Frame;
            await done.Task;
            return true;
        }

        private void CloseTab(BrowserTab tab)
        {
            int index = _tabs.IndexOf(tab);
            if (index < 0) return;
            if (tab.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) _closed.Push(tab.Url);
            bool last = _tabs.Count == 1;
            if (last && tab.Url is "about:blank") return;   // already the blank tab the app rests on
            if (_current == tab)
            {
                _skipFx = true;
                try
                {
                    if (last) OpenTab("about:blank");       // closing the last tab leaves the app open on a blank one
                    else TabStrip.SelectedIndex = index > 0 ? index - 1 : 1;
                }
                finally { _skipFx = false; }
            }
            _tabs.Remove(tab);
            Host.Children.Remove(tab.View);
            tab.View.Dispose();
            if (last) FocusAddress();
        }

        private void SelectOffset(int delta)
        {
            SetFeedsMode(false);
            if (_tabs.Count > 1) TabStrip.SelectedIndex = (TabStrip.SelectedIndex + delta + _tabs.Count) % _tabs.Count;
        }

        private BrowserTab? TabAt(object? source) =>
            (ItemsControl.ContainerFromElement(TabStrip, source as DependencyObject) as ListBoxItem)?.DataContext as BrowserTab;

        private void TabStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TabStrip.SelectedItem is BrowserTab t) _ = SwitchToAsync(t);
        }

        // New tabs are empty (about:blank, dark like the window) with the cursor in the address bar, ready to type.
        private void NewBlankTab()
        {
            OpenTab("about:blank");
            FocusAddress();
        }

        private void NewTab_Click(object sender, RoutedEventArgs e) => NewBlankTab();

        // Opens the most recently closed page; pressing again opens the one closed before it, and so on back through the history.
        private void ReopenClosedTab() { if (_closed.TryPop(out var url)) OpenTab(url); }

        private void Reopen_Click(object sender, RoutedEventArgs e) => ReopenClosedTab();

        private void CloseTab_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is BrowserTab t) CloseTab(t);
            e.Handled = true;
        }

        private void TabStrip_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            var tab = TabAt(e.OriginalSource);
            if (e.ChangedButton == MouseButton.Middle && tab != null) { CloseTab(tab); e.Handled = true; }
            else if (e.ChangedButton == MouseButton.Left) { _dragStart = e.GetPosition(null); _dragTab = tab; }
        }

        private void TabStrip_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragTab == null) return;
            if (Math.Abs(e.GetPosition(null).X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance) return;
            var tab = _dragTab;
            _dragTab = null;
            DragDrop.DoDragDrop(TabStrip, tab, DragDropEffects.Move);
        }

        private void TabStrip_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(typeof(BrowserTab)) is BrowserTab src && TabAt(e.OriginalSource) is { } dst && src != dst)
                _tabs.Move(_tabs.IndexOf(src), _tabs.IndexOf(dst));
        }

        // ---------- WebView2 wiring ----------

        private Task EnsureInitAsync(BrowserTab tab) => tab.Init ??= InitCoreAsync(tab);

        private async Task InitCoreAsync(BrowserTab tab)
        {
            var view = tab.View;
            await view.EnsureCoreWebView2Async();
            var c = view.CoreWebView2;
            c.Settings.IsPasswordAutosaveEnabled = false;
            c.Settings.IsGeneralAutofillEnabled = false;
            await AdBlocker.AttachAsync(c);

            // Ask pages for their dark theme (prefers-color-scheme: dark); sites without one are unaffected.
            c.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;

            // Downloads always land in the current user's Downloads folder, without a "save as" prompt.
            c.Profile.DefaultDownloadFolderPath = DownloadsDir;
            c.DownloadStarting += (_, e) =>
            {
                // WebView2 swallows exceptions thrown inside its events, so log them or a failure here is invisible.
                try
                {
                    e.ResultFilePath = UniquePath(Path.Combine(DownloadsDir, Path.GetFileName(e.ResultFilePath)));
                    e.Handled = true; // our own progress strip replaces the browser's download popup
                    TrackDownload(e.DownloadOperation);
                }
                catch (Exception ex) { App.LogCrash("download", ex); }
            };

            c.DocumentTitleChanged += (_, _) => { tab.Title = c.DocumentTitle is { Length: > 0 } t ? t : c.Source; if (tab == _current) UpdateChrome(); };
            c.SourceChanged += (_, _) => { tab.Url = c.Source; if (tab == _current) UpdateChrome(); };
            c.HistoryChanged += (_, _) => { if (tab == _current) UpdateChrome(); };
            c.NavigationStarting += (_, _) => { tab.IsLoading = true; if (tab == _current) UpdateChrome(); };
            c.NavigationCompleted += (_, e) =>
            {
                tab.IsLoading = false;
                if (e.IsSuccess && c.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    _store.AddHistory(c.DocumentTitle is { Length: > 0 } t ? t : c.Source, c.Source);
                if (Environment.TickCount64 - _cacheScanAt > 2000) _ = RefreshCacheSizeAsync(); // a page load just changed the cache
                if (tab == _current) UpdateChrome();
            };
            c.FaviconChanged += async (_, _) => { tab.Icon = await LoadFaviconAsync(c); if (tab == _current) UpdateChrome(); };
            c.WindowCloseRequested += (_, _) => CloseTab(tab);
            // A page leaving its own fullscreen (video) must not end a fullscreen the user started with F11.
            c.ContainsFullScreenElementChanged += (_, _) => SetFullscreen(c.ContainsFullScreenElement || _manualFullscreen);
            // Open links/popups as tabs; handing over the new tab's CoreWebView2 keeps window.opener (OAuth popups).
            // Pages may only open a tab/popup in response to a click; script-opened windows are dropped.
            c.NewWindowRequested += async (_, e) =>
            {
                if (!e.IsUserInitiated || AdBlocker.IsBlocked(e.Uri)) { e.Handled = true; return; }
                var deferral = e.GetDeferral();
                var popup = CreateTab("");
                TabStrip.SelectedItem = popup;
                await EnsureInitAsync(popup);
                e.NewWindow = popup.View.CoreWebView2;
                e.Handled = true;
                deferral.Complete();
            };
            if (tab.Url.Length > 0) c.Navigate(tab.Url);
        }

        private static async Task<BitmapImage?> LoadFaviconAsync(CoreWebView2 c)
        {
            try
            {
                using var stream = await c.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
                if (stream == null) return null;
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy);
                var bytes = copy.ToArray();
                FaviconService.Save(FaviconService.HostOf(c.Source), bytes); // the page's own icon also feeds the bookmarks bar
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = new MemoryStream(bytes);
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch { return null; }
        }

        private void SetFullscreen(bool on)
        {
            if (on == _fullscreen) return;
            _fullscreen = on;
            if (on)
            {
                _stateBeforeFullscreen = WindowState;
                WindowStyle = WindowStyle.None;
                WindowState = WindowState.Normal; // re-maximize so the window covers the taskbar
                WindowState = WindowState.Maximized;
            }
            else
            {
                WindowStyle = WindowStyle.SingleBorderWindow;
                WindowState = _stateBeforeFullscreen;
            }
            ApplyChrome();
        }

        // ---------- Right-click on a YouTube tab: add its channel to a Feeds folder ----------

        private void TabStrip_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (TabAt(e.OriginalSource) is not { } tab || !YouTubeChannelService.IsYouTubeUrl(tab.Url)) return; // only YouTube tabs get this menu
            e.Handled = true;

            var root = new MenuItem
            {
                Header = "Add channel to folder",
                Icon = new Image { Source = (ImageSource)FindResource("YouTubeIcon"), Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) },
            };
            var folders = FeedStore.FolderNames();
            foreach (var folder in folders)
            {
                // Headers are TextBlocks because a plain string would treat "_" in a folder name as an access key.
                var item = new MenuItem { Header = new TextBlock { Text = folder } };
                item.Click += (_, _) => _ = AddChannelToFolderAsync(tab, folder);
                root.Items.Add(item);
            }
            // A new folder for this channel: ask for a name, create it, then add the channel to it.
            if (folders.Count > 0) root.Items.Add(new Separator());
            var newFolder = new MenuItem { Header = "New folder…" };
            newFolder.Click += (_, _) =>
            {
                var name = Views.NameDialog.Ask(this, "New folder", "Name of the new folder for this channel:");
                if (name == null) return;
                var (_, folderName) = FeedStore.AddFolder(name); // an existing folder with that name is simply used
                _ = AddChannelToFolderAsync(tab, folderName);
            };
            root.Items.Add(newFolder);

            var menu = new ContextMenu { PlacementTarget = TabStrip };
            menu.Items.Add(root);
            menu.IsOpen = true;
        }

        // Adds the channel the tab is showing (not the video) to the folder, and says what happened.
        private async Task AddChannelToFolderAsync(BrowserTab tab, string folder)
        {
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                var channel = await YouTubeChannelService.ResolveAsync(tab.View.CoreWebView2, tab.Url);
                var (added, existing) = FeedStore.AddChannel(folder, channel);
                FeedsPanel.ChannelsChanged();
                Mouse.OverrideCursor = null;
                MessageBox.Show(this,
                    added ? $"Added the channel \"{channel.Title}\" to the folder \"{folder}\"." : $"\"{channel.Title}\" is already in the folder \"{existing}\".",
                    "Browse", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Mouse.OverrideCursor = null;
                MessageBox.Show(this, "Could not add the channel: " + ex.Message, "Browse", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ---------- Feeds page ----------
        // The Feeds button swaps the browser (bookmarks, tabs and the page) for the Feeds page; the toolbar stays.

        private bool _feedsMode;

        // One place decides what is visible, from fullscreen and the Feeds page.
        private void ApplyChrome()
        {
            Toolbar.Visibility = _fullscreen ? Visibility.Collapsed : Visibility.Visible;
            // Settings is a tab: the tab and bookmark rows stay, only the page area shows the settings page.
            TabRow.Visibility = BookmarkRow.Visibility = _fullscreen || _feedsMode ? Visibility.Collapsed : Visibility.Visible;
            Host.Visibility = _feedsMode ? Visibility.Collapsed : Visibility.Visible;
            FeedsPanel.Visibility = _feedsMode ? Visibility.Visible : Visibility.Collapsed;
            SettingsPanel.Visibility = _settingsMode && !_feedsMode ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetFeedsMode(bool on)
        {
            if (on == _feedsMode) return;
            _feedsMode = on;
            CloseFolderMenu();
            ApplyChrome();
            if (on) FeedsPanel.Open(); else FeedsPanel.Close();
            FeedsButton.Background = on ? ChipBrush("#B3261E") : ThemeService.Brush("ThemeChip");   // YouTube red while the page is open
            FeedsButton.BorderBrush = on ? ChipBrush("#7A1B16") : ThemeService.Brush("ThemeTool");
        }

        private void Feeds_Click(object sender, RoutedEventArgs e) => SetFeedsMode(!_feedsMode);

        // F11: the whole window becomes the page (tabs, toolbar and bookmarks hidden); F11 again brings everything back.
        private bool _manualFullscreen;

        private void ToggleFullscreen()
        {
            _manualFullscreen = !_fullscreen;
            SetFullscreen(_manualFullscreen);
        }

        // ---------- Chrome (address bar, buttons) ----------

        private void UpdateChrome()
        {
            if (_current is not { } tab) return;
            var c = tab.View.CoreWebView2;
            _suppressText = true;
            if (!AddressBox.IsKeyboardFocused) AddressBox.Text = tab.Url is "about:blank" ? "" : tab.Url;
            _suppressText = false;
            BackButton.IsEnabled = c?.CanGoBack ?? false;
            ForwardButton.IsEnabled = c?.CanGoForward ?? false;
            StarButton.Content = _store.FindBookmark(tab.Url) != null ? "★" : "☆";
            AddressIcon.Source = tab.Icon ?? (ImageSource)FindResource("GlobeIcon");
            Title = $"{tab.Title} - Browse";
        }

        private void UpdateBookmarkBar()
        {
            ApplyChrome();
            BookmarkHint.Visibility = _store.ActiveBookmarks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------- Cache chip ----------

        private readonly DispatcherTimer _cacheTimer = new() { Interval = TimeSpan.FromSeconds(5) };
        private long _cacheScanAt;
        private bool _clearingCache;

        private void InitCacheChip()
        {
            _cacheTimer.Tick += async (_, _) => await RefreshCacheSizeAsync();
            _cacheTimer.Start();
            Loaded += async (_, _) => await RefreshCacheSizeAsync();
        }

        // The size is live: re-measured every few seconds and after each page load, off the UI thread.
        private async Task RefreshCacheSizeAsync()
        {
            if (_clearingCache) return;
            _cacheScanAt = Environment.TickCount64;
            long size = await Task.Run(CacheService.Measure);
            if (_clearingCache) return;
            CacheButton.Content = CacheService.Format(size);
            CacheButton.ToolTip = $"Cache: {CacheService.Format(size)} - click to clear it (page icons are kept)";
        }

        // One click, no confirmation: the engine's own cache and Cache API storage, then any cache files it left behind.
        // Cookies, logins, history and all page icons are untouched.
        private async void ClearCache_Click(object sender, RoutedEventArgs e)
        {
            if (_clearingCache) return;
            _clearingCache = true;
            CacheButton.IsEnabled = false;
            CacheButton.Content = "Clearing…";
            try
            {
                var core = _tabs.Select(t => t.View.CoreWebView2).FirstOrDefault(c => c != null);
                if (core != null)
                    await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage);
                await Task.Run(CacheService.DeleteLeftovers);
            }
            catch (Exception ex) { App.LogCrash("clear cache", ex); }
            finally
            {
                _clearingCache = false;
                CacheButton.IsEnabled = true;
                await RefreshCacheSizeAsync();
            }
        }

        // ---------- Bookmark icons ----------

        private readonly DispatcherTimer _barRefresh = new() { Interval = TimeSpan.FromSeconds(3) };

        private void OnBookmarksChanged(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            UpdateBookmarkBar();
            UpdateChrome();
            // New bookmarks (starred, imported) get their icons fetched right away.
            if (e.NewItems != null)
            {
                var urls = BrowserStore.Flatten(e.NewItems.Cast<Bookmark>()).Select(b => b.Url).ToList();
                _ = Task.Run(() => FaviconService.BackfillAsync(urls));
            }
        }

        // Icons trickle in from the background; redraw the bar at most every few seconds and keep its scroll position.
        private void ScheduleBarRefresh()
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_barRefresh.IsEnabled) return;
                _barRefresh.Tick += RefreshBar;
                _barRefresh.Start();
            });
        }

        private void RefreshBar(object? sender, EventArgs e)
        {
            _barRefresh.Stop();
            _barRefresh.Tick -= RefreshBar;
            double offset = BookmarkScroll.VerticalOffset;
            BookmarkBar.Items.Refresh();
            Dispatcher.BeginInvoke(() => BookmarkScroll.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
        }

        // A few seconds after start, collect the icons that are still missing (both profiles) without slowing startup.
        private void StartIconBackfill()
        {
            var urls = BrowserStore.Flatten(_store.Bookmarks.Concat(_store.PrivateBookmarks)).Select(b => b.Url).ToList();
            _ = Task.Run(async () => { await Task.Delay(5000); await FaviconService.BackfillAsync(urls); });
        }

        private async void FetchIcons_Click(object sender, RoutedEventArgs e)
        {
            var urls = BrowserStore.Flatten(_store.Bookmarks.Concat(_store.PrivateBookmarks)).Select(b => b.Url).ToList();
            int found = await Task.Run(() => FaviconService.BackfillAsync(urls, retryFailed: true));
            RefreshBar(null, EventArgs.Empty);
            MessageBox.Show(this, $"Found {found} new site icons.", "Browse", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ---------- Profiles (Work / Private): only the bookmarks differ ----------

        private void ApplyProfile()
        {
            bool priv = _store.Profile == BrowserStore.Private;
            // Feeds exists only in Private: leaving Private also leaves the Feeds page and hides its button.
            if (!priv && _feedsMode) SetFeedsMode(false);
            FeedsButton.Visibility = priv ? Visibility.Visible : Visibility.Collapsed;
            BookmarkBar.ItemsSource = _store.ActiveBookmarks;
            if (IsLoaded) _ = RefreshServersAsync();
            // Label panel / darker icon panel / icon (briefcase for Work, lock for Private) of the profile chip.
            ProfileButton.Content = _store.Profile;
            ProfileButton.ToolTip = priv ? $"{_store.Profile} bookmarks - click to switch to {BrowserStore.Work}" : null; // Work gives no hint that Private exists
            ProfileButton.Background = ChipBrush(priv ? "#8E4EC6" : "#3B6FD9");
            ProfileButton.BorderBrush = ChipBrush(priv ? "#5E2D8C" : "#1F4FA8");
            ProfileButton.Tag = priv ? "" : "";
            UpdateBookmarkBar();
            UpdateChrome();
        }

        // A toggle: each click switches to the other profile.
        // Private is a hidden switch: from Work only Ctrl+Alt+click enters it; a plain click does nothing. Leaving Private needs no keys.
        private void Profile_Click(object sender, RoutedEventArgs e)
        {
            if (_store.Profile != BrowserStore.Private && Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Alt)) return;
            CloseFolderMenu(); // an open folder panel belongs to the profile being left
            string from = _store.Profile;
            _store.Profile = from == BrowserStore.Private ? BrowserStore.Work : BrowserStore.Private;
            _store.SaveSoon();
            ApplyProfile();
            SwapTabs(from, _store.Profile); // each profile has its own open tabs
        }

        private string Normalize(string input)
        {
            input = input.Trim().Trim('"');
            if (Path.IsPathRooted(input) && !input.Contains("://") && File.Exists(input))
                return new Uri(input).AbsoluteUri; // double-clicked / dropped local file
            bool hasScheme = input.Contains("://") || input.StartsWith("about:") || input.StartsWith("edge:") || input.StartsWith("file:");
            if (hasScheme) return input;
            bool looksLikeHost = !input.Contains(' ') && (input.Contains('.') || input.StartsWith("localhost"));
            if (looksLikeHost) return (input.StartsWith("localhost") ? "http://" : "https://") + input;
            return string.Format(_store.SearchUrl, Uri.EscapeDataString(input));
        }

        private void Navigate(string input)
        {
            if (_current is { IsSettings: true } && !string.IsNullOrWhiteSpace(input)) { Suggestions.IsOpen = false; OpenTab(Normalize(input)); return; } // Settings has no page to navigate
            if (_current?.View.CoreWebView2 is not { } c || string.IsNullOrWhiteSpace(input)) return;
            Suggestions.IsOpen = false;
            SetFeedsMode(false); // an address typed or a bookmark clicked leaves the Feeds page
            c.Navigate(Normalize(input));
            _current.View.Focus();
        }

        private void Back_Click(object sender, RoutedEventArgs e) => _current?.View.CoreWebView2?.GoBack();
        private void Forward_Click(object sender, RoutedEventArgs e) => _current?.View.CoreWebView2?.GoForward();

        private void Reload_Click(object sender, RoutedEventArgs e)
        {
            if (_current?.View.CoreWebView2 is not { } c) return;
            if (_current.IsLoading) c.Stop(); else c.Reload();
        }

        private void Star_Click(object sender, RoutedEventArgs e)
        {
            if (_current is { Url.Length: > 0 } t) _store.ToggleBookmark(t.Title, t.Url);
        }

        private void Bookmark_Click(object sender, RoutedEventArgs e)
        {
            var b = (Bookmark)((FrameworkElement)sender).DataContext;
            if (b.Children == null) { Navigate(b.Url); return; }
            _hoverTimer.Stop();
            if (!FolderOpen || !ReferenceEquals(_openFolder, b)) ShowFolderMenu((FrameworkElement)sender, b);
        }

        // Right-click shows a small icon bar under the bookmark: delete (red trash can, asks first), open in new tab, copy link.
        private Bookmark? _actionBookmark;

        private void Bookmark_RightClick(object sender, MouseButtonEventArgs e)
        {
            var anchor = (FrameworkElement)sender;
            ShowBookmarkActions(anchor, (Bookmark)anchor.DataContext);
            e.Handled = true;
        }

        // b == null: the empty part of the bar, which can only paste a cut bookmark back to the top level.
        private void ShowBookmarkActions(FrameworkElement anchor, Bookmark? b)
        {
            _actionBookmark = b;
            static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
            bool isLink = b is { Children: null }, isFolder = b is { Children: not null };
            BookmarkDeleteButton.Visibility = BookmarkCutButton.Visibility = Show(b != null);
            BookmarkOpenButton.Visibility = BookmarkCopyButton.Visibility = Show(isLink);
            // Paste targets a folder or the bar itself, and never a folder inside the cut item.
            BookmarkPasteButton.Visibility = Show(_cut != null && (b == null || (isFolder && !BrowserStore.IsInside(_cut, b!))));
            BookmarkActions.IsOpen = false; // reopen at the cursor even if it was already showing for another bookmark
            BookmarkActions.IsOpen = true;  // Placement="Mouse": the bar appears next to the pointer
        }

        // ---------- Cut / paste ----------

        private Bookmark? _cut;

        private void SetCut(Bookmark? b)
        {
            _cut = b;
            CutHint.Text = b == null ? "" : $"✂ {b.Title} — right-click a folder or the empty bar to paste";
            CutHint.Visibility = b == null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void BookmarkCut_Click(object sender, RoutedEventArgs e)
        {
            BookmarkActions.IsOpen = false;
            SetCut(_actionBookmark);
        }

        private void BookmarkPaste_Click(object sender, RoutedEventArgs e)
        {
            BookmarkActions.IsOpen = false;
            if (_cut is not { } item) return;
            // _actionBookmark is the target folder, or null for the top level of the bar.
            if (_store.MoveBookmark(item, _actionBookmark)) { CloseFolderMenu(); SetCut(null); RefreshBar(null, EventArgs.Empty); UpdateChrome(); }
        }

        private void CutHint_Click(object sender, MouseButtonEventArgs e) => SetCut(null);

        private void BookmarkRow_RightClick(object sender, MouseButtonEventArgs e)
        {
            // Only reached for the empty part of the bar: a bookmark's own right-click is handled there.
            if (_cut != null) ShowBookmarkActions(BookmarkRow, null);
            e.Handled = true;
        }

        private void BookmarkDelete_Click(object sender, RoutedEventArgs e)
        {
            BookmarkActions.IsOpen = false;
            if (_actionBookmark is not { } b) return;
            string what = b.Children != null
                ? $"folder \"{b.Title}\" and its {BrowserStore.Flatten(b.Children).Count()} bookmarks"
                : $"bookmark \"{b.Title}\"";
            if (!Views.ConfirmWindow.Ask(this, $"Delete {what}?")) return;
            CloseFolderMenu(); // its list would still show the deleted bookmark
            _store.RemoveBookmark(b);
            UpdateChrome();
        }

        private void BookmarkOpen_Click(object sender, RoutedEventArgs e)
        {
            BookmarkActions.IsOpen = false;
            if (_actionBookmark is { Children: null } b) { CloseFolderMenu(); OpenTab(b.Url); }
        }

        private void BookmarkCopy_Click(object sender, RoutedEventArgs e)
        {
            BookmarkActions.IsOpen = false;
            if (_actionBookmark is not { Children: null } b) return;
            try { Clipboard.SetText(b.Url); } catch { /* clipboard busy */ }
        }

        // ---------- Folder drop-downs: our own rounded popup panels (not ContextMenus, which fight over focus and capture) ----------
        // A folder opens on hover (or click); subfolders fly out to the right; the panels stay open while the icon bar is used
        // and close when the pointer has been away for a moment, or you click elsewhere in the window, the page, or another app.

        private readonly List<Popup> _folderPopups = [];   // index = nesting level: 0 hangs under the bar button, deeper ones to the right of a row
        private FrameworkElement? _folderAnchor;
        private Bookmark? _openFolder;
        private FrameworkElement? _hoverButton;
        private int _awayTicks;
        private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly DispatcherTimer _menuWatch = new() { Interval = TimeSpan.FromMilliseconds(300) };
        private readonly DispatcherTimer _rowTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
        private (Border Row, Bookmark Item, int Level)? _pendingRow;

        private bool FolderOpen => _folderPopups.Count > 0;

        private void InitFolderMenus()
        {
            _hoverTimer.Tick += (_, _) =>
            {
                _hoverTimer.Stop();
                if (_hoverButton is { IsMouseOver: true } btn && btn.DataContext is Bookmark { Children: not null } f && !FolderOpen && !BookmarkActions.IsOpen)
                    ShowFolderMenu(btn, f);
            };
            // Short grace before a row changes the flyout, so the pointer can cross other rows on its way into a submenu.
            _rowTimer.Tick += (_, _) =>
            {
                _rowTimer.Stop();
                if (_pendingRow is not { } p || !p.Row.IsMouseOver) return;
                if (p.Item.Children != null) OpenPanel(p.Item.Children, p.Level + 1, p.Row); else TrimPanels(p.Level + 1);
            };
            _menuWatch.Tick += (_, _) =>
            {
                if (!FolderOpen) { _menuWatch.Stop(); return; }
                bool over = (_folderAnchor?.IsMouseOver ?? false) || _folderPopups.Any(p => p.Child is { IsMouseOver: true }) || BookmarkActions.IsOpen;
                _awayTicks = over ? 0 : _awayTicks + 1;
                if (_awayTicks >= 2) CloseFolderMenu();
            };
            PreviewMouseDown += (_, _) => CloseFolderMenu();
            Deactivated += (_, _) => CloseFolderMenu();
        }

        private void Bookmark_MouseEnter(object sender, MouseEventArgs e)
        {
            var button = (FrameworkElement)sender;
            var b = (Bookmark)button.DataContext;
            _hoverTimer.Stop();
            _hoverButton = null;
            if (BookmarkActions.IsOpen) return;
            if (b.Children == null) { CloseFolderMenu(); return; }          // moved onto a plain bookmark
            if (FolderOpen) { if (!ReferenceEquals(_openFolder, b)) ShowFolderMenu(button, b); } // switch folders at once
            else { _hoverButton = button; _hoverTimer.Start(); }            // rest on a folder briefly to roll it down
        }

        private void Bookmark_MouseLeave(object sender, MouseEventArgs e)
        {
            _hoverTimer.Stop();
            _hoverButton = null;
        }

        private void TrimPanels(int keep)
        {
            for (int i = _folderPopups.Count - 1; i >= keep; i--)
            {
                _folderPopups[i].IsOpen = false;
                _folderPopups.RemoveAt(i);
            }
        }

        private void CloseFolderMenu()
        {
            _rowTimer.Stop();
            TrimPanels(0);
            _openFolder = null;
            _folderAnchor = null;
            _menuWatch.Stop();
        }

        private void ShowFolderMenu(FrameworkElement anchor, Bookmark folder)
        {
            CloseFolderMenu();
            _folderAnchor = anchor;
            _openFolder = folder;
            _awayTicks = 0;
            OpenPanel(folder.Children!, 0, anchor);
            _menuWatch.Start();
        }

        private void OpenPanel(List<Bookmark> items, int level, FrameworkElement anchor)
        {
            TrimPanels(level);
            var rows = new StackPanel();
            if (items.Count == 0)
                rows.Children.Add(new TextBlock { Text = "(empty)", Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(14, 6, 14, 6) });
            foreach (var b in items) rows.Children.Add(BuildRow(b, level));

            var panel = new Border
            {
                Background = (Brush)FindResource("Surface"),
                BorderBrush = (Brush)FindResource("Hover"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(0, 3, 0, 3),
                Child = new ScrollViewer
                {
                    MaxHeight = 600,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = rows,
                },
            };
            var popup = new Popup
            {
                AllowsTransparency = true, // lets the rounded corners show instead of a square window behind them
                StaysOpen = true,          // closed by CloseFolderMenu, never by focus or capture changes
                PopupAnimation = PopupAnimation.None,
                PlacementTarget = anchor,
                Placement = level == 0 ? PlacementMode.Bottom : PlacementMode.Right,
                HorizontalOffset = level == 0 ? 0 : -2,
                VerticalOffset = level == 0 ? 2 : -5,
                Child = panel,
            };
            _folderPopups.Add(popup);
            popup.IsOpen = true;
        }

        private static SolidColorBrush ChipBrush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        private Border BuildRow(Bookmark b, int level)
        {
            bool folder = b.Children != null;
            // Same colours as the chips on the bar: folders dark blue with white text, links the unselected-tab slate with light text.
            Brush fg = ThemeService.Brush(folder ? "ThemeFolderText" : "ThemeChipText");
            var fill = ThemeService.Brush(folder ? "ThemeFolder" : "ThemeChip");
            var hover = ThemeService.Brush(folder ? "ThemeFolderHover" : "ThemeChipHover");

            // Two separate panels side by side: the icon tile (yellow folder on a darker navy, or the page's icon on the same
            // slate as the label) and, next to it, the label panel. The label panel's text and arrow share one Grid.
            var image = new Image { Source = BookmarkIcons.For(b), Width = 16, Height = 16 };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var tile = new Border
            {
                Width = 32, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 3, 0),
                Background = ThemeService.Brush(folder ? "ThemeFolder" : "ThemeChipIcon"), Child = image,
            };

            var labelGrid = new Grid { MinWidth = 190 };
            labelGrid.ColumnDefinitions.Add(new ColumnDefinition());
            labelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            labelGrid.Children.Add(new TextBlock
            {
                Text = b.Title, Foreground = fg, FontFamily = new FontFamily("Segoe UI"), FontSize = 14, MaxWidth = 300,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            });
            if (folder)
            {
                var arrow = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M0,0 L4,4 L0,8 Z"), Fill = fg,
                    Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(arrow, 1);
                labelGrid.Children.Add(arrow);
            }
            var label = new Border { Background = fill, CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 0, 10, 0), Child = labelGrid };

            var panels = new Grid { Height = 32 }; // every small panel in the app is 32 px tall
            panels.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            panels.ColumnDefinitions.Add(new ColumnDefinition());
            if (folder)
            {
                // Folders have no icon tile: the label panel takes the whole row, so it lines up with the link rows above and below.
                Grid.SetColumnSpan(label, 2);
            }
            else
            {
                Grid.SetColumn(label, 1);
                panels.Children.Add(tile);
            }
            panels.Children.Add(label);

            // The row itself is transparent: it takes the mouse and drag/drop events and shows the drop marker (see MarkRow).
            var row = new Border
            {
                Child = panels, Background = Brushes.Transparent,
                Margin = new Thickness(6, 0, 6, 0), Padding = new Thickness(0, 3, 0, 3),
                ToolTip = folder ? null : b.Url,
            };
            row.MouseEnter += (_, _) => { label.Background = hover; _pendingRow = (row, b, level); _rowTimer.Stop(); _rowTimer.Start(); };
            row.MouseLeave += (_, _) => label.Background = fill;
            row.MouseUp += (_, e) =>
            {
                if (e.ChangedButton == MouseButton.Left)
                {
                    if (folder) OpenPanel(b.Children!, level + 1, row);
                    else { CloseFolderMenu(); Navigate(b.Url); }
                }
                else if (e.ChangedButton == MouseButton.Middle && !folder) { CloseFolderMenu(); OpenTab(b.Url); }
            };
            // Right-click: the panels stay open and the icon bar appears at the pointer.
            row.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowBookmarkActions(row, b); };
            WireRowDragDrop(row, b);
            return row;
        }

        // ---------- Drag & drop: reorder bookmarks and folders on the bar and inside folder panels ----------
        // Drop before/after a bookmark to reorder, or onto the middle of a folder to put it inside. Works between the bar
        // and any open folder panel too.

        private enum DropZone { Before, After, Into }

        private Bookmark? _bmDragItem;
        private Point _bmDragStart;

        private static T? Ancestor<T>(DependencyObject? d) where T : DependencyObject
        {
            while (d != null && d is not T)
                d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            return d as T;
        }

        private static bool Moved(Point from, Point to) =>
            Math.Abs(to.X - from.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(to.Y - from.Y) > SystemParameters.MinimumVerticalDragDistance;

        private void StartDrag(UIElement source, Bookmark item)
        {
            _bmDragItem = null;
            DragDrop.DoDragDrop(source, new DataObject(typeof(Bookmark), item), DragDropEffects.Move);
            HideDropMarker();
        }

        private static Bookmark? Dragged(DragEventArgs e) => e.Data.GetData(typeof(Bookmark)) as Bookmark;

        // Never onto itself, never a folder into its own contents, and "into" only for folders.
        private static bool CanDrop(Bookmark item, Bookmark? target, DropZone zone) =>
            target == null || (!ReferenceEquals(item, target) && !BrowserStore.IsInside(item, target) && (zone != DropZone.Into || target.Children != null));

        private void ApplyDrop(Bookmark item, Bookmark? target, DropZone zone)
        {
            bool ok = target == null ? _store.MoveBookmark(item, null)
                    : zone == DropZone.Into ? _store.MoveBookmark(item, target)
                    : _store.MoveRelative(item, target, zone == DropZone.After);
            if (!ok) return;
            RefreshBar(null, EventArgs.Empty); // moves inside a folder don't notify the bar
            UpdateChrome();
        }

        // -- the bar --

        private void BookmarkBar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _bmDragItem = Ancestor<Button>(e.OriginalSource as DependencyObject)?.DataContext as Bookmark;
            _bmDragStart = e.GetPosition(null);
        }

        private void BookmarkBar_PreviewMouseUp(object sender, MouseButtonEventArgs e) => _bmDragItem = null;

        private void BookmarkBar_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && _bmDragItem is { } item && Moved(_bmDragStart, e.GetPosition(null)))
                StartDrag(BookmarkBar, item);
        }

        // The chip under the pointer (the 6 px gaps right of and below a chip count for that chip) and which part of it. Past the end
        // of a row, the pointer means "after the last chip of that row"; below everything, after the very last chip.
        private (Bookmark? Target, DropZone Zone, Rect Rect) BarTarget(DragEventArgs e)
        {
            var pos = e.GetPosition(BookmarkRow);
            var chips = new List<(Bookmark B, Rect Cell, Rect Chip)>();
            for (int i = 0; i < BookmarkBar.Items.Count; i++)
            {
                if (BookmarkBar.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c || BookmarkBar.Items[i] is not Bookmark b) continue;
                var cell = new Rect(c.TranslatePoint(new Point(0, 0), BookmarkRow), new Size(c.ActualWidth, c.ActualHeight)); // includes the chip's margin
                chips.Add((b, cell, new Rect(cell.Location, new Size(Math.Max(1, cell.Width - 6), Math.Max(1, cell.Height - 6)))));
            }
            if (chips.Count == 0) return (null, DropZone.After, Rect.Empty);

            var hit = chips.FirstOrDefault(c => c.Cell.Contains(pos));
            if (hit.B == null)
            {
                // Not over a chip: take the last chip of the row the pointer is in, else the last chip overall.
                var row = chips.Where(c => pos.Y >= c.Cell.Top && pos.Y < c.Cell.Bottom).ToList();
                var last = row.Count > 0 ? row[^1] : chips[^1];
                return (last.B, DropZone.After, last.Chip);
            }
            double rel = (pos.X - hit.Chip.Left) / hit.Chip.Width;
            var zone = hit.B.Children != null ? (rel < 0.25 ? DropZone.Before : rel > 0.75 ? DropZone.After : DropZone.Into)
                                              : (rel < 0.5 ? DropZone.Before : DropZone.After);
            return (hit.B, zone, hit.Chip);
        }

        private void BookmarkRow_DragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;
            e.Effects = DragDropEffects.None;
            if (Dragged(e) is not { } item) { HideDropMarker(); return; }

            // A bar that is taller than its four visible rows scrolls while a bookmark is dragged near its top or bottom edge.
            double y = e.GetPosition(BookmarkScroll).Y;
            if (y < 12) BookmarkScroll.ScrollToVerticalOffset(BookmarkScroll.VerticalOffset - 12);
            else if (y > BookmarkScroll.ActualHeight - 12) BookmarkScroll.ScrollToVerticalOffset(BookmarkScroll.VerticalOffset + 12);

            var (target, zone, rect) = BarTarget(e);
            if (!CanDrop(item, target, zone)) { HideDropMarker(); return; }
            e.Effects = DragDropEffects.Move;
            if (target == null) { HideDropMarker(); return; } // empty bar: dropping appends
            ShowDropMarker(zone, rect);
        }

        private void BookmarkRow_DragLeave(object sender, DragEventArgs e) => HideDropMarker();

        private void BookmarkRow_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            HideDropMarker();
            if (Dragged(e) is not { } item) return;
            var (target, zone, _) = BarTarget(e);
            if (CanDrop(item, target, zone)) ApplyDrop(item, target, zone);
            Dispatcher.BeginInvoke(CloseFolderMenu); // an open folder panel would show the old order
        }

        private void ShowDropMarker(DropZone zone, Rect r)
        {
            // r is the chip's rectangle in BookmarkRow coordinates (rows can now be anywhere in the bar, so top and height matter too).
            if (zone == DropZone.Into)
            {
                DropMarker.Margin = new Thickness(r.Left - 2, r.Top - 2, 0, 0);
                DropMarker.Width = r.Width + 4;
                DropMarker.Height = r.Height + 4;
                DropMarker.Background = Brushes.Transparent;
                DropMarker.BorderBrush = Brushes.White;
                DropMarker.BorderThickness = new Thickness(2);
            }
            else
            {
                DropMarker.Margin = new Thickness((zone == DropZone.Before ? r.Left - 3 : r.Right + 3) - 1.5, r.Top, 0, 0);
                DropMarker.Width = 3;
                DropMarker.Height = r.Height;
                DropMarker.Background = Brushes.White;
                DropMarker.BorderThickness = new Thickness(0);
            }
            DropMarker.Visibility = Visibility.Visible;
        }

        private void HideDropMarker() => DropMarker.Visibility = Visibility.Collapsed;

        // -- folder panel rows --

        private static DropZone RowZone(Border row, Bookmark b, DragEventArgs e)
        {
            double rel = e.GetPosition(row).Y / Math.Max(1, row.ActualHeight);
            return b.Children != null ? (rel < 0.25 ? DropZone.Before : rel > 0.75 ? DropZone.After : DropZone.Into)
                                      : (rel < 0.5 ? DropZone.Before : DropZone.After);
        }

        // The marker is a border on the row; padding is adjusted by the same amount so rows never change size mid-drag.
        private static void MarkRow(Border row, DropZone? zone)
        {
            row.BorderBrush = Brushes.White;
            switch (zone)
            {
                case DropZone.Before: row.BorderThickness = new Thickness(0, 2, 0, 0); row.Padding = new Thickness(0, 1, 0, 3); break;
                case DropZone.After: row.BorderThickness = new Thickness(0, 0, 0, 2); row.Padding = new Thickness(0, 3, 0, 1); break;
                case DropZone.Into: row.BorderThickness = new Thickness(2); row.Padding = new Thickness(0, 1, 0, 1); break;
                default: row.BorderThickness = new Thickness(0); row.Padding = new Thickness(0, 3, 0, 3); break;
            }
        }

        private void WireRowDragDrop(Border row, Bookmark b)
        {
            row.AllowDrop = true;
            row.PreviewMouseLeftButtonDown += (_, e) => { _bmDragItem = b; _bmDragStart = e.GetPosition(null); };
            row.PreviewMouseLeftButtonUp += (_, _) => _bmDragItem = null;
            row.PreviewMouseMove += (_, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed && ReferenceEquals(_bmDragItem, b) && Moved(_bmDragStart, e.GetPosition(null)))
                    StartDrag(row, b);
            };
            row.DragOver += (_, e) =>
            {
                e.Handled = true;
                var zone = RowZone(row, b, e);
                bool ok = Dragged(e) is { } item && CanDrop(item, b, zone);
                e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
                MarkRow(row, ok ? zone : null);
            };
            row.DragLeave += (_, _) => MarkRow(row, null);
            row.Drop += (_, e) =>
            {
                e.Handled = true;
                var zone = RowZone(row, b, e);
                MarkRow(row, null);
                if (Dragged(e) is { } item && CanDrop(item, b, zone)) ApplyDrop(item, b, zone);
                Dispatcher.BeginInvoke(CloseFolderMenu); // the panel's rows are stale after a move
            };
        }

        // ---------- Download progress strip (same look as File Manager's copy progress) ----------

        private readonly ObservableCollection<DownloadItem> _downloads = [];

        private void TrackDownload(CoreWebView2DownloadOperation op)
        {
            var item = new DownloadItem(Path.GetFileName(op.ResultFilePath), op.Cancel);
            void Refresh() => item.Update((long)op.BytesReceived, (long)(op.TotalBytesToReceive ?? 0));

            op.BytesReceivedChanged += (_, _) => Refresh();
            op.StateChanged += (_, _) =>
            {
                switch (op.State)
                {
                    case CoreWebView2DownloadState.Completed:
                        Refresh();
                        item.Complete();
                        DismissLater(item, 8);
                        break;
                    case CoreWebView2DownloadState.Interrupted:
                        bool cancelled = op.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled;
                        item.Fail(cancelled ? "Cancelled" : $"Download failed ({op.InterruptReason})");
                        if (cancelled) DismissLater(item, 3); // failures stay until dismissed
                        break;
                }
            };
            _downloads.Add(item);
            Refresh();
        }

        // Slides the card out, then removes it.
        private void DismissLater(DownloadItem item, double seconds)
        {
            var wait = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            wait.Tick += (_, _) => { wait.Stop(); Dismiss(item); };
            wait.Start();
        }

        private void Dismiss(DownloadItem item)
        {
            if (item.IsClosing || !_downloads.Contains(item)) return;
            item.IsClosing = true;
            var slide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            slide.Tick += (_, _) => { slide.Stop(); _downloads.Remove(item); };
            slide.Start();
        }

        private void DownloadCancel_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is DownloadItem d) d.Cancel();
        }

        private void DownloadDismiss_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is DownloadItem d) Dismiss(d);
        }

        // ---------- Bookmark sync (shared file, e.g. in OneDrive) ----------

        private BookmarkSyncService? _sync;

        private void InitBookmarkSync()
        {
            _sync = new BookmarkSyncService(_store, Dispatcher);
            // A newer bookmark file arrived from another PC and replaced the local bookmarks: redraw what shows them.
            _sync.Applied += () => Dispatcher.BeginInvoke(() => { CloseFolderMenu(); UpdateBookmarkBar(); UpdateChrome(); FeedsPanel.ChannelsChanged(); });
            Loaded += (_, _) => { if (_store.SyncFile is { Length: > 0 } file) _ = _sync.StartAsync(file); };
        }

        private void ChooseSyncFile()
        {
            // The dialog opens in the pre-filled folder (or OneDrive) with the default file name; an existing file is fine, it is not overwritten.
            string defaultDir = Path.GetDirectoryName(AppConfig.DefaultSyncFile)!;
            string oneDrive = Environment.GetEnvironmentVariable("OneDriveCommercial") ?? Environment.GetEnvironmentVariable("OneDrive") ?? "";
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Choose or create the bookmark sync file",
                Filter = "Bookmark sync file (*.json)|*.json",
                DefaultExt = "json",
                AddExtension = true,
                CheckFileExists = false,
                OverwritePrompt = false,
                FileName = Path.GetFileName(AppConfig.DefaultSyncFile),
                InitialDirectory = Directory.Exists(defaultDir) ? defaultDir : oneDrive,
            };
            if (dlg.ShowDialog(this) == true) StartSync(dlg.FileName);
        }

        private void StartSync(string file)
        {
            _store.SyncFile = file;
            _store.SaveSoon();
            _ = _sync!.StartAsync(file);
        }

        // ---------- Address bar suggestions (bookmarks + history) ----------

        private void ShowSuggestions(string query, int max)
        {
            var items = _store.Suggest(query, max);
            SuggestionList.ItemsSource = items;
            SuggestionList.SelectedIndex = -1;
            Suggestions.IsOpen = items.Count > 0;
        }

        private void AddressBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e) =>
            Dispatcher.BeginInvoke(AddressBox.SelectAll, DispatcherPriority.Input);

        private void AddressBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => Suggestions.IsOpen = false;

        private void AddressBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressText || !AddressBox.IsKeyboardFocused) return;
            if (AddressBox.Text.Length == 0) Suggestions.IsOpen = false; else ShowSuggestions(AddressBox.Text, 8);
        }

        private void AddressBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Down or Key.Up when Suggestions.IsOpen:
                    int n = SuggestionList.Items.Count, i = SuggestionList.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
                    SuggestionList.SelectedIndex = (i + n + 1) % (n + 1) - 1; // -1 = the typed text
                    e.Handled = true;
                    break;
                case Key.Enter:
                    Navigate(SuggestionList.SelectedItem is Bookmark b && Suggestions.IsOpen ? b.Url : AddressBox.Text);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    Suggestions.IsOpen = false;
                    UpdateChrome();
                    _current?.View.Focus();
                    e.Handled = true;
                    break;
            }
        }

        private void Suggestion_Click(object sender, MouseButtonEventArgs e)
        {
            if (ItemsControl.ContainerFromElement(SuggestionList, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: Bookmark b })
                Navigate(b.Url);
        }

        // ---------- Shortcuts ----------

        // The WPF WebView2 control forwards the browser's accelerator keys here, so this one handler also
        // covers shortcuts pressed while a page has focus. Only shell shortcuts are claimed; zoom, find,
        // F12, print etc. stay with the browser. Deferred so a tab never closes inside its own key event.
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (ShortcutFor(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers) is { } action)
            {
                e.Handled = true;
                Dispatcher.BeginInvoke(action);
            }
        }

        private Action? ShortcutFor(Key key, ModifierKeys mods)
        {
            bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift), alt = mods.HasFlag(ModifierKeys.Alt);
            if (key == Key.F6 || (alt && key == Key.D)) return FocusAddress;
            if (key == Key.F11 && !ctrl && !alt) return ToggleFullscreen;
            if (!ctrl || alt) return null;
            return key switch
            {
                Key.T when shift => ReopenClosedTab,
                Key.T => NewBlankTab,
                Key.W => () => { if (_feedsMode) SetFeedsMode(false); else if (_current != null) CloseTab(_current); }, // never closes a tab you cannot see
                Key.L => FocusAddress,
                Key.R => () => Reload_Click(this, null!),
                Key.D => () => Star_Click(this, null!),
                Key.H => ShowHistory,
                Key.J => () => Downloads_Click(this, null!),
                Key.Tab => () => SelectOffset(shift ? -1 : 1),
                >= Key.D1 and <= Key.D9 => () => { SetFeedsMode(false); TabStrip.SelectedIndex = key == Key.D9 ? _tabs.Count - 1 : Math.Min(key - Key.D1, _tabs.Count - 1); },
                _ => null,
            };
        }

        private void FocusAddress() { AddressBox.Focus(); AddressBox.SelectAll(); }

        private void ShowHistory() { FocusAddress(); ShowSuggestions("", 20); }

        // ---------- Menu ----------

        // The gear opens Settings as a tab (one per profile): it selects the existing Settings tab, or adds it. Clicking the gear
        // while on the Settings tab closes it again.
        private bool _settingsMode => _current?.IsSettings == true;

        private void Menu_Click(object sender, RoutedEventArgs e)
        {
            SetFeedsMode(false);
            CloseFolderMenu();
            if (_current is { IsSettings: true } open) { CloseTab(open); return; }
            var tab = _tabs.FirstOrDefault(t => t.IsSettings);
            if (tab == null)
            {
                tab = new BrowserTab(new WebView2 { Visibility = Visibility.Collapsed }) { IsSettings = true, Title = "Settings" };
                Host.Children.Add(tab.View);
                _tabs.Add(tab);
            }
            TabStrip.SelectedItem = tab;
        }

        private void InitSettings()
        {
            SettingsPanel.Version = $"Browse v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";
            SettingsPanel.SyncStateProvider = () => new(_sync?.Status ?? "Off", _sync?.FilePath, _sync?.IsRunning ?? false);
            SettingsPanel.SyncNowRequested += async () => { if (_sync != null) await _sync.SyncNowAsync(); };
            SettingsPanel.SyncDefaultRequested += () => StartSync(AppConfig.DefaultSyncFile);
            SettingsPanel.SyncChooseRequested += ChooseSyncFile;
            SettingsPanel.SyncOffRequested += () => { _sync?.Stop(); _store.SyncFile = null; _store.SaveSoon(); };
            SettingsPanel.FetchIconsRequested += () => FetchIcons_Click(this, null!);
            SettingsPanel.TabEffect = _store.TabEffect;
            SettingsPanel.TabEffectChanged += effect => { _store.TabEffect = effect; _store.SaveSoon(); };
            SettingsPanel.ThemeRequested +=() => Theme_Click(this, null!);
            SettingsPanel.CheckUpdatesRequested += () => CheckUpdates_Click(this, null!);
        }

        private void Theme_Click(object sender, RoutedEventArgs e) => new Views.ThemeWindow { Owner = this }.Show();

        private void Downloads_Click(object sender, RoutedEventArgs e) => _current?.View.CoreWebView2?.OpenDefaultDownloadDialog();
        // ---------- Updates & shutdown ----------

        private void StartUpdateChecks()
        {
            if (_updates == null) return;
            var timer = new DispatcherTimer { Interval = UpdateInterval };
            timer.Tick += (_, _) => _ = CheckUpdateAsync();
            timer.Start();
            _ = CheckUpdateAsync();
        }

        private async Task CheckUpdateAsync()
        {
            try { if (await _updates!.CheckAsync()) UpdateButton.Visibility = Visibility.Visible; }
            catch { /* silent: next timer tick or launch retries */ }
        }

        private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            if (_updates is not { IsInstalled: true })
            {
                MessageBox.Show(this, "Updates only work in the installed version of Browse.", "Browse", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                if (!await _updates.CheckAsync())
                {
                    MessageBox.Show(this, "You are up to date.", "Browse", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                UpdateButton.Visibility = Visibility.Visible;
                if (MessageBox.Show(this, "An update has been downloaded. Restart Browse now to install it?", "Browse", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    Update_Click(sender, e);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not check for updates: " + ex.Message, "Browse", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // The session is restored after the restart, so applying an update is non-destructive.
        private void Update_Click(object sender, RoutedEventArgs e)
        {
            SaveSession();
            _updates?.ApplyAndRestart();
        }

        // Both profiles' tabs are saved: the active one from the strip, the other from its parked tabs (or left as stored
        // if it was never opened in this run).
        private void SaveSession()
        {
            void Put(string profile, List<string> urls, int index)
            {
                if (profile == BrowserStore.Private) { _store.PrivateSession = urls; _store.PrivateSessionIndex = index; }
                else { _store.Session = urls; _store.SessionIndex = index; }
            }
            List<string> UrlsOf(IEnumerable<BrowserTab> tabs) => tabs.Where(t => !t.IsSettings).Select(t => t.Url).Where(u => u.Length > 0).ToList();
            // The Settings tab is not saved, so tabs before the selected one that are Settings do not count in the saved index.
            int IndexOf(List<BrowserTab> tabs, int selected) => Math.Max(0, tabs.Take(Math.Max(0, selected)).Count(t => !t.IsSettings));

            Put(_store.Profile, UrlsOf(_tabs), IndexOf(_tabs.ToList(), TabStrip.SelectedIndex));
            string other = _store.Profile == BrowserStore.Private ? BrowserStore.Work : BrowserStore.Private;
            if (_parkedTabs.TryGetValue(other, out var parked)) Put(other, UrlsOf(parked), IndexOf(parked, _parkedIndex.GetValueOrDefault(other)));
            _store.Save();
        }

        // Everything is saved in Window_Closing. A DevTools window, a pending WebView2 call or a stuck page
        // can otherwise keep the process alive with no window, so force the exit if a normal shutdown stalls.
        private void ExitProcess()
        {
            new Thread(() => { Thread.Sleep(2000); Environment.Exit(0); }) { IsBackground = true }.Start();
            Application.Current.Shutdown();
        }

        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            SaveSession(); // an empty strip (last tab closed) saves an empty session for that profile only
        }
    }
}
