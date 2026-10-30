using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Browse.Models;
using Browse.Services;

namespace Browse
{
    // Page flip by mouse (tab effect "Page flip"): moving into a lower corner of the page lifts that corner like a book page,
    // dragging it across turns to the next tab (right corner) or the previous one (left corner). A WebView2 is a native window
    // that WPF can't draw over, so while the corner is near the page is swapped for a screenshot and the curl is drawn on that.
    public partial class MainWindow
    {
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint p);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X, Y; }

        private const double CurlZone = 80, CurlLeaveZone = 130; // DIPs around the corner that start / keep the lifted corner

        private sealed class CurlSession
        {
            public required TabTransition Fx;
            public required WriteableBitmap Bmp;
            public required BrowserTab From, To;
            public required bool Right;
            public float Mx, My, Tx, Ty;        // corner position now / heading to, in the curl's own (right corner) coordinates
            public bool Drag, Committing;
            public Point Down;
            public int Frames;
            public long Ticks;
        }

        private CurlSession? _curl;
        private bool _curlStarting;

        private void InitCurl()
        {
            var poll = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(33) };
            poll.Tick += (_, _) => CurlPoll();
            poll.Start();
        }

        private Point? CursorInHost()
        {
            if (!GetCursorPos(out var p)) return null;
            try { return Host.PointFromScreen(new Point(p.X, p.Y)); } catch { return null; }
        }

        private BrowserTab? Neighbour(bool right)
        {
            int i = _tabs.IndexOf(_current!) + (right ? 1 : -1);
            return i >= 0 && i < _tabs.Count ? _tabs[i] : null;
        }

        // Which corner (true = lower right) the cursor is near, and how near (1 = in the corner), within `zone` DIPs.
        private (bool Right, double Near)? CornerNear(double zone)
        {
            if (CursorInHost() is not { } c || Host.ActualWidth < 1) return null;
            double w = Host.ActualWidth, h = Host.ActualHeight;
            if (c.X < 0 || c.X > w || c.Y < 0 || c.Y > h) return null;
            bool right = c.X > w / 2;
            double d = Math.Sqrt(Math.Pow(right ? w - c.X : c.X, 2) + Math.Pow(h - c.Y, 2));
            return d <= zone ? (right, 1 - d / zone) : null;
        }

        private void CurlPoll()
        {
            if (_curl is { } s)
            {
                if (s.Drag || s.Committing) return;
                var near = IsActive ? CornerNear(CurlLeaveZone) : null;
                if (near is not { } n || n.Right != s.Right) { s.Tx = s.Fx.Width; s.Ty = s.Fx.Height; return; } // let go: the corner settles back
                SetHoverTarget(s, n.Near);
                return;
            }
            if (_curlStarting || _endFx != null || _store.TabEffect != TabEffect.Curl || !IsActive || !Host.IsVisible) return;
            if (_current == null || _tabs.Count < 2 || !_current.View.IsVisible || (GetAsyncKeyState(0x01) & 0x8000) != 0) return;
            if (CornerNear(CurlZone) is { } z && Neighbour(z.Right) is { } to) _ = StartCurlAsync(_current, to, z.Right);
        }

        private static void SetHoverTarget(CurlSession s, double near)
        {
            float lift = (14 + 52 * (float)near) * (s.Fx.Width / 1920f + 0.35f);
            s.Tx = s.Fx.Width - lift * 1.5f;
            s.Ty = s.Fx.Height - lift * 0.9f;
        }

        private async Task StartCurlAsync(BrowserTab from, BrowserTab to, bool right)
        {
            _curlStarting = true;
            try
            {
                if (await TabTransition.CaptureAsync(from.View) is not { } png) return;
                from.Snapshot = png;
                var pair = await Task.WhenAll(TabTransition.DecodeAsync(png), to.Snapshot == null ? Task.FromResult<PageFrame?>(null) : TabTransition.DecodeAsync(to.Snapshot));
                if (pair[0] is not { } a) return;
                var b = pair[1] ?? new PageFrame(a.Width, a.Height, Enumerable.Repeat(unchecked((int)0xFF0A0C10), a.Width * a.Height).ToArray());
                if (a.Width != b.Width || a.Height != b.Height) return;
                var fx = await Task.Run(() => new TabTransition(a, b, TabEffect.Curl, right));
                // the world may have moved on while the screenshots were taken
                if (_current != from || _endFx != null || CornerNear(CurlLeaveZone) is not { } near || near.Right != right) return;

                var s = new CurlSession
                {
                    Fx = fx, Right = right, From = from, To = to,
                    Bmp = new WriteableBitmap(a.Width, a.Height, 96, 96, PixelFormats.Pbgra32, null),
                    Ticks = System.Diagnostics.Stopwatch.GetTimestamp(),
                };
                s.Mx = fx.Width; s.My = fx.Height;
                _curl = s;
                SetHoverTarget(s, near.Near);
                Draw(s);
                TransitionLayer.Source = s.Bmp;
                TransitionLayer.IsHitTestVisible = true;
                TransitionLayer.Cursor = Cursors.Hand;
                TransitionLayer.Visibility = Visibility.Visible;
                _endFx = EndCurlNow;
                CompositionTarget.Rendering += CurlFrame;
            }
            catch { /* the view went away: no curl */ }
            finally { _curlStarting = false; }
        }

        private static void Draw(CurlSession s)
        {
            s.Fx.RenderCurl(s.Mx, s.My);
            s.Bmp.WritePixels(new Int32Rect(0, 0, s.Fx.Width, s.Fx.Height), s.Fx.Output, s.Fx.Width * 4, 0);
        }

        private void CurlFrame(object? sender, EventArgs e)
        {
            if (_curl is not { } s) return;
            if (++s.Frames == 1) s.From.View.Visibility = Visibility.Collapsed; // only after the layer has been drawn

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double dt = Math.Min(0.05, (now - s.Ticks) / (double)System.Diagnostics.Stopwatch.Frequency);
            s.Ticks = now;
            float k = 1 - MathF.Exp((float)(-dt * (s.Drag ? 40 : s.Committing ? 9 : 14)));
            float px = s.Mx, py = s.My;
            s.Mx += (s.Tx - s.Mx) * k;
            s.My += (s.Ty - s.My) * k;

            float w = s.Fx.Width, h = s.Fx.Height;
            if (s.Committing && s.Mx < -w - 20) { CommitCurl(s); return; }
            if (!s.Drag && !s.Committing && MathF.Abs(s.Mx - w) < 1.5f && MathF.Abs(s.My - h) < 1.5f
                && MathF.Abs(s.Tx - w) < 1 && MathF.Abs(s.Ty - h) < 1) { EndCurlNow(); HideLayerLater(3); return; }
            if (MathF.Abs(s.Mx - px) > 0.05f || MathF.Abs(s.My - py) > 0.05f) Draw(s);
        }

        // The page lies flat again (or another tab switch begins): the view comes back, the layer goes.
        private void EndCurlNow()
        {
            _endFx = null;
            if (_curl is not { } s) return;
            _curl = null;
            CompositionTarget.Rendering -= CurlFrame;
            if (TransitionLayer.IsMouseCaptured) TransitionLayer.ReleaseMouseCapture();
            TransitionLayer.IsHitTestVisible = false;
            TransitionLayer.Cursor = null;
            if (_current == s.From) s.From.View.Visibility = Visibility.Visible;
            if (_current == s.From && !AddressBox.IsKeyboardFocused) s.From.View.Focus();
            if (!_hidingLayer) { TransitionLayer.Visibility = Visibility.Collapsed; TransitionLayer.Source = null; }
        }

        private bool _hidingLayer;

        private void HideLayerLater(int frames)
        {
            _hidingLayer = true;
            TransitionLayer.Visibility = Visibility.Visible;
            int n = 0;
            void Tick(object? s, EventArgs e)
            {
                if (++n < frames) return;
                CompositionTarget.Rendering -= Tick;
                _hidingLayer = false;
                if (_curl == null && !_curlStarting && _endFx == null) { TransitionLayer.Visibility = Visibility.Collapsed; TransitionLayer.Source = null; }
            }
            CompositionTarget.Rendering += Tick;
        }

        // The page is fully turned: the layer shows the next page, which becomes the selected tab without a second effect.
        private void CommitCurl(CurlSession s)
        {
            _curl = null;
            _endFx = null;
            CompositionTarget.Rendering -= CurlFrame;
            if (TransitionLayer.IsMouseCaptured) TransitionLayer.ReleaseMouseCapture();
            TransitionLayer.IsHitTestVisible = false;
            TransitionLayer.Cursor = null;
            _skipFx = true;
            try { TabStrip.SelectedItem = s.To; }
            finally { _skipFx = false; }
            HideLayerLater(5);
        }

        // ---------- mouse on the layer ----------

        private Point LayerToCurl(CurlSession s, Point p)
        {
            double sx = s.Fx.Width / Math.Max(1, TransitionLayer.ActualWidth), sy = s.Fx.Height / Math.Max(1, TransitionLayer.ActualHeight);
            double x = p.X * sx, y = p.Y * sy;
            x = Math.Min(s.Right ? x : s.Fx.Width - x, s.Fx.Width - 2);
            y = Math.Clamp(y, 0, s.Fx.Height - 2);
            // the page is hinged at its lower far corner and can't be pulled past its own width from there
            double vx = x, vy = y - s.Fx.Height, len = Math.Sqrt(vx * vx + vy * vy);
            if (len > s.Fx.Width) { x = vx * s.Fx.Width / len; y = s.Fx.Height + vy * s.Fx.Width / len; }
            return new Point(x, y);
        }

        private void Curl_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_curl is not { Committing: false } s) return;
            s.Drag = true;
            s.Down = e.GetPosition(TransitionLayer);
            TransitionLayer.CaptureMouse();
            var m = LayerToCurl(s, s.Down);
            s.Tx = (float)m.X; s.Ty = (float)m.Y;
            e.Handled = true;
        }

        private void Curl_MouseMove(object sender, MouseEventArgs e)
        {
            if (_curl is not { Drag: true } s) return;
            var m = LayerToCurl(s, e.GetPosition(TransitionLayer));
            s.Tx = (float)m.X; s.Ty = (float)m.Y;
        }

        private void Curl_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_curl is not { Drag: true } s) return;
            s.Drag = false;
            TransitionLayer.ReleaseMouseCapture();
            var up = e.GetPosition(TransitionLayer);
            bool click = (up - s.Down).Length < 6;
            bool far = (s.Fx.Width - s.Mx) > s.Fx.Width * 0.3f;
            if (click || far)
            {
                s.Committing = true;
                s.Tx = -s.Fx.Width - 40; s.Ty = s.Fx.Height;
            }
            // otherwise the poll sends the corner back to where the mouse is (hover or flat)
        }

        private void Curl_LostCapture(object sender, MouseEventArgs e)
        {
            if (_curl is { Drag: true } s) s.Drag = false;
        }
    }
}
