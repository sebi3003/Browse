using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Browse.Models;
using Browse.Services;

namespace Browse
{
    // Server chips on the far left of the Work bookmark bar: the servers ServerLauncher manages plus every Work bookmark that points at an IP
    // address or localhost, whenever they answer right now, shown as just their icon. CustomerSuccess Hub always comes first.
    public partial class MainWindow
    {
        private readonly DispatcherTimer _serverTimer = new() { Interval = TimeSpan.FromSeconds(30) };
        private bool _serversBusy;

        private void InitServers()
        {
            _serverTimer.Tick += (_, _) => _ = RefreshServersAsync();
            _serverTimer.Start();
            Loaded += (_, _) => _ = RefreshServersAsync();
            _store.Bookmarks.CollectionChanged += (_, _) => _ = RefreshServersAsync();
            FaviconService.Added += () => Dispatcher.BeginInvoke(() => ServerBar.Items.Refresh());
        }

        private static bool IsServerUrl(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
            && (u.HostNameType == UriHostNameType.IPv4 || u.IsLoopback);

        private static readonly HttpClient Probe = new(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true })
            { Timeout = TimeSpan.FromSeconds(2) };

        // This PC's own addresses: a bookmark to one of them with a launcher server's port is that same server.
        private static bool IsThisPc(string host) =>
            host is "localhost" or "127.0.0.1" || Dns.GetHostAddresses(Dns.GetHostName()).Any(a => a.ToString() == host);

        private async Task RefreshServersAsync()
        {
            if (_serversBusy) return;
            _serversBusy = true;
            try
            {
                var chips = new List<ServerChip>();
                if (_store.Profile == BrowserStore.Work)
                {
                    // The servers ServerLauncher manages, each with its program's icon; the ones answering /api/health get a chip.
                    var launcher = ServerLauncherList.Load();
                    var checks = await Task.WhenAll(launcher.Select(async s => (s, url: await LauncherUrlAsync(s.Port))));
                    foreach (var (s, url) in checks.Where(c => c.url != null))
                        chips.Add(new ServerChip(s.Name, url!, () => ServerLauncherList.IconOf(s.ExePath) ?? FaviconService.Get(url!)));

                    // Plus every Work bookmark that points at another IP address or localhost, one chip per host:port, first bookmark wins.
                    var ports = launcher.Select(l => l.Port).ToHashSet();
                    var found = BrowserStore.Flatten(_store.Bookmarks.ToList())
                        .Where(b => IsServerUrl(b.Url))
                        .Where(b => { var u = new Uri(b.Url); return !(ports.Contains(u.Port) && IsThisPc(u.Host)); })
                        .GroupBy(b => new Uri(b.Url).Authority)
                        .Select(g => g.First())
                        .ToList();
                    var up = await Task.WhenAll(found.Select(async b => (b, up: await IsUpAsync(new Uri(b.Url)))));
                    chips.AddRange(up.Where(c => c.up).Select(c => new ServerChip(c.b.Title, c.b.Url, () => BookmarkIcons.For(c.b))));
                }
                var online = chips
                    .OrderByDescending(c => c.Title.Contains("CustomerSuccess", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                ServerBar.ItemsSource = online;
                // The bookmark chips start after the server chips (32 px + 6 px gap each).
                BookmarkBar.Margin = new Thickness(6 + online.Count * 38, 0, 0, -6);
                _ = Task.Run(() => FaviconService.BackfillAsync(online.Select(c => c.Url).ToList()));
            }
            catch (Exception ex) { App.LogCrash("server check", ex); }
            finally { _serversBusy = false; }
        }

        // Same test ServerLauncher uses: /api/health on the plain port; a server with HTTPS redirects, which tells the scheme to open.
        private static async Task<string?> LauncherUrlAsync(int port)
        {
            try
            {
                using var resp = await Probe.GetAsync($"http://127.0.0.1:{port}/api/health");
                if (!resp.IsSuccessStatusCode) return null;
                return $"{resp.RequestMessage?.RequestUri?.Scheme ?? "http"}://localhost:{port}/";
            }
            catch { return null; }
        }

        private static async Task<bool> IsUpAsync(Uri u)
        {
            try
            {
                using var client = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
                await client.ConnectAsync(u.Host, u.Port, cts.Token);
                return true;
            }
            catch { return false; }
        }

        private void Server_Click(object sender, RoutedEventArgs e) =>
            Navigate(((ServerChip)((FrameworkElement)sender).Tag).Url);
    }
}
