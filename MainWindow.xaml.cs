using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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
            InitializeComponent();
            TabStrip.ItemsSource = _tabs;
            _store.Bookmarks.CollectionChanged += (_, _) => { UpdateBookmarkBar(); UpdateChrome(); };
            _store.PrivateBookmarks.CollectionChanged += (_, _) => { UpdateBookmarkBar(); UpdateChrome(); };
            ApplyProfile();
            VersionItem.Header = $"Browse v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";
            try { _updates = new UpdateService(AppConfig.GitHubRepoUrl, AppConfig.GitHubAccessToken); } catch { }
            Loaded += (_, _) => { RestoreTabs(); StartUpdateChecks(); };
            Closed += (_, _) => ExitProcess();
            SourceInitialized += (_, _) => { int on = 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, sizeof(int)); }; // dark title bar
        }

        // ---------- Tabs ----------

        private void RestoreTabs()
        {
            UpdateBookmarkBar();
            // Restored tabs are created lazily: a WebView2 only starts when its tab is first shown.
            foreach (var url in _store.Session) CreateTab(url);
            if (_startUrl != null) OpenTab(Normalize(_startUrl));
            else if (_tabs.Count == 0) OpenTab(_store.HomeUrl);
            else TabStrip.SelectedIndex = Math.Clamp(_store.SessionIndex, 0, _tabs.Count - 1);
        }

        private BrowserTab CreateTab(string url)
        {
            var view = new WebView2 { CreationProperties = _props, Visibility = Visibility.Collapsed };
            var tab = new BrowserTab(view) { Url = url, Title = url };
            Host.Children.Add(view);
            _tabs.Add(tab);
            return tab;
        }

        private void OpenTab(string url) => TabStrip.SelectedItem = CreateTab(url);

        private async Task SwitchToAsync(BrowserTab tab)
        {
            if (_current != null) _current.View.Visibility = Visibility.Collapsed;
            _current = tab;
            tab.View.Visibility = Visibility.Visible;
            UpdateChrome();
            await EnsureInitAsync(tab);
            if (_current == tab && !AddressBox.IsKeyboardFocused) tab.View.Focus();
        }

        private void CloseTab(BrowserTab tab)
        {
            int index = _tabs.IndexOf(tab);
            if (index < 0) return;
            if (tab.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) _closed.Push(tab.Url);
            if (_tabs.Count == 1) { _tabs.Clear(); Close(); return; }
            if (_current == tab) TabStrip.SelectedIndex = index > 0 ? index - 1 : 1;
            _tabs.Remove(tab);
            Host.Children.Remove(tab.View);
            tab.View.Dispose();
        }

        private void SelectOffset(int delta)
        {
            if (_tabs.Count > 1) TabStrip.SelectedIndex = (TabStrip.SelectedIndex + delta + _tabs.Count) % _tabs.Count;
        }

        private BrowserTab? TabAt(object? source) =>
            (ItemsControl.ContainerFromElement(TabStrip, source as DependencyObject) as ListBoxItem)?.DataContext as BrowserTab;

        private void TabStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TabStrip.SelectedItem is BrowserTab t) _ = SwitchToAsync(t);
        }

        private void NewTab_Click(object sender, RoutedEventArgs e) => OpenTab(_store.HomeUrl);

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

            c.DocumentTitleChanged += (_, _) => { tab.Title = c.DocumentTitle is { Length: > 0 } t ? t : c.Source; if (tab == _current) UpdateChrome(); };
            c.SourceChanged += (_, _) => { tab.Url = c.Source; if (tab == _current) UpdateChrome(); };
            c.HistoryChanged += (_, _) => { if (tab == _current) UpdateChrome(); };
            c.NavigationStarting += (_, _) => { tab.IsLoading = true; if (tab == _current) UpdateChrome(); };
            c.NavigationCompleted += (_, e) =>
            {
                tab.IsLoading = false;
                if (e.IsSuccess && c.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    _store.AddHistory(c.DocumentTitle is { Length: > 0 } t ? t : c.Source, c.Source);
                if (tab == _current) UpdateChrome();
            };
            c.FaviconChanged += async (_, _) => tab.Icon = await LoadFaviconAsync(c);
            c.WindowCloseRequested += (_, _) => CloseTab(tab);
            c.ContainsFullScreenElementChanged += (_, _) => SetFullscreen(c.ContainsFullScreenElement);
            // Open links/popups as tabs; handing over the new tab's CoreWebView2 keeps window.opener (OAuth popups).
            // Pages may only open a tab/popup in response to a click; script-opened windows are dropped.
            c.NewWindowRequested += async (_, e) =>
            {
                if (!e.IsUserInitiated) { e.Handled = true; return; }
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
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = stream;
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
            var chrome = on ? Visibility.Collapsed : Visibility.Visible;
            TabRow.Visibility = Toolbar.Visibility = chrome;
            if (on)
            {
                _stateBeforeFullscreen = WindowState;
                BookmarkRow.Visibility = chrome;
                WindowStyle = WindowStyle.None;
                WindowState = WindowState.Normal; // re-maximize so the window covers the taskbar
                WindowState = WindowState.Maximized;
            }
            else
            {
                WindowStyle = WindowStyle.SingleBorderWindow;
                WindowState = _stateBeforeFullscreen;
                UpdateBookmarkBar();
            }
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
            ReloadButton.Content = tab.IsLoading ? "✕" : "⟳";
            StarButton.Content = _store.FindBookmark(tab.Url) != null ? "★" : "☆";
            Title = $"{tab.Title} - Browse";
        }

        private void UpdateBookmarkBar()
        {
            BookmarkRow.Visibility = _fullscreen ? Visibility.Collapsed : Visibility.Visible;
            BookmarkHint.Visibility = _store.ActiveBookmarks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------- Profiles (Work / Private): only the bookmarks differ ----------

        private void ApplyProfile()
        {
            bool priv = _store.Profile == BrowserStore.Private;
            BookmarkBar.ItemsSource = _store.ActiveBookmarks;
            ProfileLabel.Text = _store.Profile;
            ProfileButton.Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(priv ? "#8E4EC6" : "#3B6FD9"));
            UpdateBookmarkBar();
            UpdateChrome();
        }

        private void Profile_Click(object sender, RoutedEventArgs e)
        {
            ProfileMenu.PlacementTarget = ProfileButton;
            ProfileMenu.IsOpen = true;
        }

        private void Profile_Select(object sender, RoutedEventArgs e)
        {
            _store.Profile = (string)((MenuItem)sender).Header;
            _store.SaveSoon();
            ApplyProfile();
        }

        private void BookmarkScroll_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            BookmarkScroll.ScrollToHorizontalOffset(BookmarkScroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        }

        private string Normalize(string input)
        {
            input = input.Trim();
            bool hasScheme = input.Contains("://") || input.StartsWith("about:") || input.StartsWith("edge:") || input.StartsWith("file:");
            if (hasScheme) return input;
            bool looksLikeHost = !input.Contains(' ') && (input.Contains('.') || input.StartsWith("localhost"));
            if (looksLikeHost) return (input.StartsWith("localhost") ? "http://" : "https://") + input;
            return string.Format(_store.SearchUrl, Uri.EscapeDataString(input));
        }

        private void Navigate(string input)
        {
            if (_current?.View.CoreWebView2 is not { } c || string.IsNullOrWhiteSpace(input)) return;
            Suggestions.IsOpen = false;
            c.Navigate(Normalize(input));
            _current.View.Focus();
        }

        private void Back_Click(object sender, RoutedEventArgs e) => _current?.View.CoreWebView2?.GoBack();
        private void Forward_Click(object sender, RoutedEventArgs e) => _current?.View.CoreWebView2?.GoForward();
        private void Home_Click(object sender, RoutedEventArgs e) => Navigate(_store.HomeUrl);

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
            if (b.Children != null) ShowFolderMenu((FrameworkElement)sender, b); else Navigate(b.Url);
        }

        // Right-click removes a bookmark; a folder asks first since it takes its contents along.
        private void Bookmark_RightClick(object sender, MouseButtonEventArgs e)
        {
            var b = (Bookmark)((FrameworkElement)sender).DataContext;
            if (b.Children != null && MessageBox.Show(this, $"Remove folder \"{b.Title}\" and its {BrowserStore.Flatten(b.Children).Count()} bookmarks?",
                    "Browse", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            _store.RemoveBookmark(b);
            UpdateChrome();
        }

        private void ShowFolderMenu(FrameworkElement anchor, Bookmark folder)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var item in FolderItems(folder.Children!)) menu.Items.Add(item);
            menu.IsOpen = true;
        }

        // Headers are TextBlocks because a plain string would treat "_" in a title as an access key.
        private IEnumerable<MenuItem> FolderItems(List<Bookmark> items)
        {
            if (items.Count == 0) yield return new MenuItem { Header = "(empty)", IsEnabled = false };
            foreach (var b in items)
            {
                var mi = new MenuItem { Header = new TextBlock { Text = b.Title, MaxWidth = 320, TextTrimming = TextTrimming.CharacterEllipsis } };
                if (b.Children != null)
                {
                    mi.Header = new TextBlock { Text = "📁 " + b.Title, MaxWidth = 320, TextTrimming = TextTrimming.CharacterEllipsis };
                    foreach (var child in FolderItems(b.Children)) mi.Items.Add(child);
                }
                else
                {
                    mi.ToolTip = b.Url;
                    mi.Click += (_, _) => Navigate(b.Url);
                }
                yield return mi;
            }
        }

        // ---------- Edge import ----------

        private void Import_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource != ImportItem) return;
            ImportItem.Items.Clear();
            var profiles = EdgeImport.Profiles();
            if (profiles.Count == 0) ImportItem.Items.Add(new MenuItem { Header = "No Edge profiles found", IsEnabled = false });
            foreach (var p in profiles)
            {
                var mi = new MenuItem { Header = new TextBlock { Text = $"{p.Name} ({p.Count}) → {_store.Profile}" } };
                mi.Click += (_, _) => ImportFromEdge(p);
                ImportItem.Items.Add(mi);
            }
        }

        private void ImportFromEdge(EdgeImport.EdgeProfile p)
        {
            try
            {
                int added = _store.ImportBookmarks(EdgeImport.Read(p.Dir));
                MessageBox.Show(this, $"Imported {added} bookmarks into {_store.Profile}.", "Browse", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Import failed: " + ex.Message, "Browse", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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
            if (!ctrl || alt) return null;
            return key switch
            {
                Key.T when shift => () => { if (_closed.TryPop(out var url)) OpenTab(url); },
                Key.T => () => OpenTab(_store.HomeUrl),
                Key.W => () => { if (_current != null) CloseTab(_current); },
                Key.L => FocusAddress,
                Key.R => () => Reload_Click(this, null!),
                Key.D => () => Star_Click(this, null!),
                Key.H => ShowHistory,
                Key.J => () => Downloads_Click(this, null!),
                Key.Tab => () => SelectOffset(shift ? -1 : 1),
                >= Key.D1 and <= Key.D9 => () => TabStrip.SelectedIndex = key == Key.D9 ? _tabs.Count - 1 : Math.Min(key - Key.D1, _tabs.Count - 1),
                _ => null,
            };
        }

        private void FocusAddress() { AddressBox.Focus(); AddressBox.SelectAll(); }

        private void ShowHistory() { FocusAddress(); ShowSuggestions("", 20); }

        // ---------- Menu ----------

        private void Menu_Click(object sender, RoutedEventArgs e)
        {
            MainMenu.PlacementTarget = (Button)sender;
            MainMenu.IsOpen = true;
        }

        private void History_Click(object sender, RoutedEventArgs e) => ShowHistory();
        private void Downloads_Click(object sender, RoutedEventArgs e) => _current?.View.CoreWebView2?.OpenDefaultDownloadDialog();
        private void DevTools_Click(object sender, RoutedEventArgs e) => _current?.View.CoreWebView2?.OpenDevToolsWindow();

        private void SetHome_Click(object sender, RoutedEventArgs e)
        {
            if (_current is { Url.Length: > 0 } t) { _store.HomeUrl = t.Url; _store.SaveSoon(); }
        }

        private async void ClearData_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, "Clear history, cookies, cache and site data?", "Browse", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            _store.ClearHistory();
            if (_current?.View.CoreWebView2 is { } c) await c.Profile.ClearBrowsingDataAsync();
        }

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

        // The session is restored after the restart, so applying an update is non-destructive.
        private void Update_Click(object sender, RoutedEventArgs e)
        {
            SaveSession();
            _updates?.ApplyAndRestart();
        }

        private void SaveSession()
        {
            _store.Session = _tabs.Select(t => t.Url).Where(u => u.Length > 0).ToList();
            _store.SessionIndex = Math.Max(0, TabStrip.SelectedIndex);
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
            if (_tabs.Count > 0) SaveSession();
            else { _store.Session = []; _store.Save(); }
        }
    }
}
