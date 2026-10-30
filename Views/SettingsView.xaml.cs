using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Browse.Services;

namespace Browse.Views
{
    /// <summary>
    /// The Settings page, opened by the gear in the toolbar. It only shows the state it is given and raises an event per
    /// action; MainWindow owns the sync service, the icon fetch, the theme window and the updater.
    /// </summary>
    public partial class SettingsView : UserControl
    {
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

        public SettingsView()
        {
            InitializeComponent();
            _timer.Tick += (_, _) => Refresh();
        }

        public record SyncState(string Status, string? File, bool Running);

        /// <summary>Asked for the current sync state while the page is visible.</summary>
        public Func<SyncState>? SyncStateProvider { get; set; }

        public string Version { set => VersionText.Text = value; }

        public TabEffect TabEffect
        {
            set
            {
                _applied = _picked = value;
                ApplyEffectButton.IsEnabled = false;
                EffectRandom.IsChecked = value == TabEffect.Random;
                EffectBurn.IsChecked = value == TabEffect.Burn;
                EffectDrop.IsChecked = value == TabEffect.Drop;
                EffectSlip.IsChecked = value == TabEffect.Slip;
                EffectCurl.IsChecked = value == TabEffect.Curl;
                EffectFade.IsChecked = value == TabEffect.Fade;
                EffectNone.IsChecked = value == TabEffect.None;
            }
        }

        public event Action<TabEffect>? TabEffectChanged;

        private TabEffect _applied, _picked;

        // Picking only selects; the effect changes when Apply is pressed.
        private void Effect_Click(object sender, RoutedEventArgs e)
        {
            _picked = sender == EffectFade ? TabEffect.Fade : sender == EffectNone ? TabEffect.None
                : sender == EffectDrop ? TabEffect.Drop : sender == EffectSlip ? TabEffect.Slip : sender == EffectCurl ? TabEffect.Curl
                : sender == EffectRandom ? TabEffect.Random : TabEffect.Burn;
            ApplyEffectButton.IsEnabled = _picked != _applied;
        }

        private void ApplyEffect_Click(object sender, RoutedEventArgs e)
        {
            _applied = _picked;
            ApplyEffectButton.IsEnabled = false;
            TabEffectChanged?.Invoke(_applied);
        }

        public event Action? SyncNowRequested, SyncDefaultRequested, SyncChooseRequested, SyncOffRequested;
        public event Action? FetchIconsRequested, ThemeRequested, CheckUpdatesRequested;

        public void Refresh()
        {
            var s = SyncStateProvider?.Invoke() ?? new SyncState("Off", null, false);
            SyncStatus.Text = s.Status;
            SyncFile.Text = s.Running ? s.File : "";
            SyncFile.Visibility = s.Running ? Visibility.Visible : Visibility.Collapsed;
            SyncNowButton.Visibility = SyncOffButton.Visibility = s.Running ? Visibility.Visible : Visibility.Collapsed;
            SyncDefaultButton.Visibility = s.Running ? Visibility.Collapsed : Visibility.Visible;
        }

        // The sync status changes by itself (a file arrives from another PC), so poll while the page is showing.
        private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible) { Refresh(); _timer.Start(); } else _timer.Stop();
        }

        private void SyncNow_Click(object sender, RoutedEventArgs e) { SyncNowRequested?.Invoke(); Refresh(); }
        private void SyncDefault_Click(object sender, RoutedEventArgs e) { SyncDefaultRequested?.Invoke(); Refresh(); }
        private void SyncChoose_Click(object sender, RoutedEventArgs e) { SyncChooseRequested?.Invoke(); Refresh(); }
        private void SyncOff_Click(object sender, RoutedEventArgs e) { SyncOffRequested?.Invoke(); Refresh(); }
        private void FetchIcons_Click(object sender, RoutedEventArgs e) => FetchIconsRequested?.Invoke();
        private void Theme_Click(object sender, RoutedEventArgs e) => ThemeRequested?.Invoke();
        private void CheckUpdates_Click(object sender, RoutedEventArgs e) => CheckUpdatesRequested?.Invoke();
    }
}
