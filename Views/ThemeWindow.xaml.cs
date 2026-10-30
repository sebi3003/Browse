using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;
using Browse.Services;

namespace Browse.Views
{
    /// <summary>A clickable miniature of the browser: pick a part, then set its colour. Every mock part uses the same brush keys as the real UI.</summary>
    public partial class ThemeWindow : Window
    {
        private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
        private readonly List<Border> _tagged = [];
        private string _key = "ThemeTab";
        private bool _busy;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public ThemeWindow()
        {
            InitializeComponent();
            SourceInitialized += (_, _) =>
            {
                int on = 1, caption = 0x100C0A;   // dark title bar in the window colour (#0A0C10, BGR)
                var hwnd = new WindowInteropHelper(this).Handle;
                DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
                DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
            };
            MockRoot.SetResourceReference(Border.BackgroundProperty, "Bg");
            MockRoot.Tag = "Bg";
            _tagged.Add(MockRoot);
            BuildMock();
            Select(_key);
        }

        private void BuildMock()
        {
            // Toolbar: back, forward, address bar, downloads, menu.
            var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(right, Dock.Right);
            right.Children.Add(Tile("ThemeDownloads", ""));
            right.Children.Add(Tile("ThemeMenu", ""));
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(left, Dock.Left);
            left.Children.Add(Tile("ThemeTool", ""));
            left.Children.Add(Tile("ThemeTool", ""));
            var address = new Border
            {
                Height = 36, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromRgb(0x46, 0x48, 0x4C)),
                Margin = new Thickness(0, 0, 6, 0), IsHitTestVisible = true,
                Child = new TextBlock { Text = "https://example.com", Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA6)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) },
            };
            bar.Children.Add(right);
            bar.Children.Add(left);
            bar.Children.Add(address);
            Mock.Children.Add(bar);

            // Bookmarks: a link, a link under the mouse, a folder, a folder under the mouse.
            var marks = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            marks.Children.Add(Chip("ThemeChipIcon", "ThemeChip", "ThemeChipText", "Bookmark"));
            marks.Children.Add(Chip("ThemeChipIcon", "ThemeChipHover", "ThemeChipText", "Under the mouse"));
            marks.Children.Add(Chip(null, "ThemeFolder", "ThemeFolderText", "Folder"));
            marks.Children.Add(Chip(null, "ThemeFolderHover", "ThemeFolderText", "Folder, mouse"));
            Mock.Children.Add(marks);

            // Tabs: plain, under the mouse, selected, and the new-tab tile.
            var tabs = new StackPanel { Orientation = Orientation.Horizontal };
            tabs.Children.Add(Chip("ThemeTabIcon", "ThemeTab", "ThemeTabText", "Tab", close: true));
            tabs.Children.Add(Chip("ThemeTabIcon", "ThemeTabHover", "ThemeTabText", "Under the mouse", close: true));
            tabs.Children.Add(Chip("ThemeTabIcon", "ThemeTabActive", "ThemeTabActiveText", "Selected tab", close: true));
            tabs.Children.Add(Tile("ThemeTool", ""));
            Mock.Children.Add(tabs);
        }

        private Border Tagged(string key, double width = double.NaN)
        {
            var b = new Border { Tag = key, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Width = width };
            b.SetResourceReference(Border.BackgroundProperty, key);
            _tagged.Add(b);
            return b;
        }

        private Border Tile(string key, string glyph)
        {
            var t = Tagged(key, 36);
            t.Height = 36;
            t.Margin = new Thickness(0, 0, 6, 0);
            t.Child = new TextBlock { Text = glyph, FontFamily = new FontFamily(IconFont), Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            return t;
        }

        private StackPanel Chip(string? iconKey, string labelKey, string textKey, string text, bool close = false)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 6, 0) };
            if (iconKey != null)
            {
                var icon = Tagged(iconKey, 36);
                icon.Height = 36;
                icon.Margin = new Thickness(0, 0, 3, 0);
                icon.Child = new TextBlock { Text = "", FontFamily = new FontFamily(IconFont), Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(icon);
            }
            var label = Tagged(labelKey);
            label.Height = 36;
            label.Padding = new Thickness(8, 0, 8, 0);
            var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var tb = new TextBlock { Text = text, Tag = textKey, VerticalAlignment = VerticalAlignment.Center, MinWidth = close ? 70 : 0 };
            tb.SetResourceReference(TextBlock.ForegroundProperty, textKey);
            line.Children.Add(tb);
            if (close)
            {
                var x = new TextBlock { Text = "✕", FontSize = 10, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                x.SetResourceReference(TextBlock.ForegroundProperty, textKey);
                line.Children.Add(x);
            }
            label.Child = line;
            row.Children.Add(label);
            return row;
        }

        private void Mock_Click(object sender, MouseButtonEventArgs e)
        {
            DependencyObject? d = e.OriginalSource as DependencyObject;
            while (d != null)
            {
                if (d is FrameworkElement { Tag: string key } && ThemeService.Colors.Any(c => c.Key == key))
                {
                    if (_pipette)
                    {
                        // Copy that part's colour onto the selected part.
                        var c = ThemeService.Get(key);
                        SetPipette(false);
                        Apply(c);
                        ShowColor(c);
                        e.Handled = true;
                    }
                    else Select(key);
                    return;
                }
                d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
        }

        private void Select(string key)
        {
            _key = key;
            NameText.Text = ThemeService.Colors.First(c => c.Key == key).Label;
            foreach (var b in _tagged) b.BorderBrush = (string)b.Tag == key ? Brushes.White : Brushes.Transparent;
            ShowColor(ThemeService.Get(key));
        }

        private void ShowColor(Color c)
        {
            _busy = true;
            Swatch.Background = new SolidColorBrush(c);
            HexBox.Text = ThemeService.ToHex(c);
            AlphaSlider.Value = c.A;
            ToHsv(c);
            ValueSlider.Value = _v;
            _busy = false;
            DrawWheel();
        }

        private void Apply(Color c)
        {
            ThemeService.Set(_key, c);
            Swatch.Background = new SolidColorBrush(c);
        }

        // ---------- Colour wheel: angle = hue, distance from the centre = saturation; the brightness slider is the value ----------

        private const int WheelSize = 180;
        private double _h, _s, _v = 1;
        private bool _wheelDrag;

        // Grey and black have no hue (or saturation) of their own, so the last ones are kept and the marker stays put.
        private void ToHsv(Color c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            _v = max;
            if (max > 0) _s = d / max;
            if (d > 0)
            {
                double h = max == r ? ((g - b) / d) % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
                _h = (h * 60 + 360) % 360;
            }
        }

        private static (byte R, byte G, byte B) FromHsv(double h, double s, double v)
        {
            double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
            var (r, g, b) = (int)(h / 60) switch { 0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x), 3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x) };
            return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }

        private Color CurrentColor()
        {
            var (r, g, b) = FromHsv(_h, _s, _v);
            return Color.FromArgb((byte)AlphaSlider.Value, r, g, b);
        }

        private void DrawWheel()
        {
            var bmp = new System.Windows.Media.Imaging.WriteableBitmap(WheelSize, WheelSize, 96, 96, PixelFormats.Bgra32, null);
            var px = new byte[WheelSize * WheelSize * 4];
            double radius = WheelSize / 2.0;
            for (int y = 0; y < WheelSize; y++)
                for (int x = 0; x < WheelSize; x++)
                {
                    double dx = x + 0.5 - radius, dy = y + 0.5 - radius, d = Math.Sqrt(dx * dx + dy * dy);
                    if (d > radius) continue;
                    var (r, g, b) = FromHsv((Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360, d / radius, _v);
                    int i = (y * WheelSize + x) * 4;
                    px[i] = b; px[i + 1] = g; px[i + 2] = r;
                    px[i + 3] = d > radius - 1 ? (byte)(255 * (radius - d)) : (byte)255;   // soft edge
                    if (px[i + 3] < 255) { px[i] = (byte)(b * px[i + 3] / 255); px[i + 1] = (byte)(g * px[i + 3] / 255); px[i + 2] = (byte)(r * px[i + 3] / 255); }
                }
            bmp.WritePixels(new Int32Rect(0, 0, WheelSize, WheelSize), px, WheelSize * 4, 0);
            WheelImage.Source = bmp;
            double a = _h * Math.PI / 180, dist = _s * radius;
            Canvas.SetLeft(WheelMarker, radius + Math.Cos(a) * dist - 7);
            Canvas.SetTop(WheelMarker, radius + Math.Sin(a) * dist - 7);
        }

        private void Wheel_Down(object sender, MouseButtonEventArgs e) { _wheelDrag = true; WheelHost.CaptureMouse(); PickFromWheel(e.GetPosition(WheelHost)); }
        private void Wheel_Move(object sender, MouseEventArgs e) { if (_wheelDrag) PickFromWheel(e.GetPosition(WheelHost)); }
        private void Wheel_Up(object sender, MouseButtonEventArgs e) { _wheelDrag = false; WheelHost.ReleaseMouseCapture(); }

        private void PickFromWheel(Point p)
        {
            double radius = WheelSize / 2.0, dx = p.X - radius, dy = p.Y - radius;
            _h = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
            _s = Math.Min(1, Math.Sqrt(dx * dx + dy * dy) / radius);
            Commit(redrawWheel: true);
        }

        // One colour from the wheel and sliders: apply it, then bring the hex box (and the wheel) in line.
        private void Commit(bool redrawWheel)
        {
            var c = CurrentColor();
            Apply(c);
            _busy = true;
            HexBox.Text = ThemeService.ToHex(c);
            _busy = false;
            if (redrawWheel) DrawWheel();
        }

        private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_busy) return;
            _v = ValueSlider.Value;
            Commit(redrawWheel: true);
        }

        private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_busy || !HexBox.Text.StartsWith('#') || ThemeService.Parse(HexBox.Text) is not Color c) return;
            Apply(c);
            _busy = true;
            AlphaSlider.Value = c.A;
            ToHsv(c);
            ValueSlider.Value = _v;
            _busy = false;
            DrawWheel();
        }

        // ---------- Pipette: the next click on the preview copies that part's colour instead of selecting it ----------

        private bool _pipette;

        private void SetPipette(bool on)
        {
            _pipette = on;
            MockRoot.Cursor = on ? Cursors.Cross : null;
            PipetteButton.Background = on ? new SolidColorBrush(Color.FromRgb(0x2B, 0x4F, 0x94)) : new SolidColorBrush(Color.FromRgb(0x1A, 0x23, 0x32));
        }

        private void Pipette_Click(object sender, RoutedEventArgs e) => SetPipette(!_pipette);

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _pipette) { SetPipette(false); e.Handled = true; }
        }

        private void ResetOne_Click(object sender, RoutedEventArgs e) { ThemeService.Reset(_key); ShowColor(ThemeService.Get(_key)); }

        private void ResetAll_Click(object sender, RoutedEventArgs e) { ThemeService.ResetAll(); ShowColor(ThemeService.Get(_key)); }
    }
}
