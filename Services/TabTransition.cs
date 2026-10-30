using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Browse.Services
{
    /// <summary>Decoded pixels (premultiplied BGRA) of a page screenshot.</summary>
    public sealed record PageFrame(int Width, int Height, int[] Pixels);

    /// <summary>The tab switch effect chosen in Settings; stored by name in browse.json.</summary>
    public enum TabEffect { Burn, Fade, None, Drop, Slip, Curl, Random }

    public static class TabEffects
    {
        private static readonly TabEffect[] Real = [TabEffect.Burn, TabEffect.Fade, TabEffect.Drop, TabEffect.Slip, TabEffect.Curl];

        /// <summary>Random becomes one of the real effects, picked anew each time; every other effect stays as it is.</summary>
        public static TabEffect Resolve(TabEffect effect) => effect == TabEffect.Random ? Real[Random.Shared.Next(Real.Length)] : effect;
    }

    /// <summary>
    /// Tab switch effect. Both pages are treated as terrain: a fractal height field each, random hills and valleys. The old page
    /// sinks while the new one rises along the screen's vertical axis, and wherever the two surfaces cross, the page burns away:
    /// the old page chars along contour lines, a flame core eats along the crossing, the new page glows as it is uncovered.
    /// A WebView2 is a native window that WPF can't blend or animate, so the effect runs on screenshots, on the CPU.
    /// </summary>
    public sealed class TabTransition
    {
        private const int Cell = 4; // the height field is sampled every 4 px and interpolated

        private readonly int _w, _h;
        private readonly int[] _a, _b;
        private readonly float[] _d; // heightB - heightA per pixel; the new page shows where _d + shift > 0
        private readonly float _band, _span;

        public int[] Output { get; }

        private readonly bool _fade;

        /// <summary>How long the effect runs.</summary>
        public static double DurationMs(TabEffect effect) => effect switch
        {
            TabEffect.Fade => 220, TabEffect.Drop => 1250, TabEffect.Slip => 1450, TabEffect.Curl => 750, _ => 1100,
        };

        private readonly TabEffect _effect;
        private readonly float _dir;     // random side for Drop / Slip: which way it tilts or which nail gives
        private readonly bool _mirror;   // Curl from the lower left corner: rendered mirrored
        private readonly float _rMax;    // widest curl radius

        /// <param name="forward">Curl only: true turns the page from the lower right corner, false from the lower left.</param>
        public TabTransition(PageFrame from, PageFrame to, TabEffect effect = TabEffect.Burn, bool forward = true)
        {
            _w = from.Width; _h = from.Height;
            _a = from.Pixels; _b = to.Pixels;
            Output = new int[_w * _h];
            _d = [];
            _effect = effect;
            _dir = Random.Shared.Next(2) == 0 ? -1f : 1f;
            _mirror = !forward;
            _rMax = Math.Clamp(_w * 0.035f, 24f, 70f);
            if (effect is TabEffect.Drop or TabEffect.Slip or TabEffect.Curl) return; // rigid / geometric: nothing to precompute
            if (effect == TabEffect.Fade) { _fade = true; return; } // a plain cross-fade needs no terrain

            var rnd = Random.Shared;
            int seedA = rnd.Next(), seedB = rnd.Next();
            float period = Math.Max(240f, Math.Min(_w, _h) * 0.6f);

            int cw = _w / Cell + 2, ch = _h / Cell + 2;
            var coarse = new float[cw * ch];
            Parallel.For(0, ch, cy =>
            {
                for (int cx = 0; cx < cw; cx++)
                {
                    float x = cx * Cell / period, y = cy * Cell / period;
                    coarse[cy * cw + cx] = Fbm(x, y, seedB) - Fbm(x, y, seedA);
                }
            });

            float relief = 0;
            foreach (var v in coarse) relief = Math.Max(relief, Math.Abs(v));
            relief = Math.Max(relief, 0.05f);
            _band = relief * 0.08f;
            _span = relief + 1.5f * _band; // at shift = -span the old page is untouched everywhere, at +span the new one is

            _d = new float[_w * _h];
            Parallel.For(0, _h, y =>
            {
                int cy = y / Cell;
                float fy = (y % Cell) / (float)Cell;
                for (int x = 0; x < _w; x++)
                {
                    int cx = x / Cell;
                    float fx = (x % Cell) / (float)Cell;
                    int i = cy * cw + cx;
                    float top = coarse[i] + (coarse[i + 1] - coarse[i]) * fx;
                    float bottom = coarse[i + cw] + (coarse[i + cw + 1] - coarse[i + cw]) * fx;
                    _d[y * _w + x] = top + (bottom - top) * fy;
                }
            });
        }

        /// <summary>Draws the state at t (0 = old page, 1 = new page) into <see cref="Output"/>.</summary>
        public void Render(double t)
        {
            if (_fade) { RenderFade(t); return; }
            switch (_effect)
            {
                case TabEffect.Drop: RenderDrop(t); return;
                case TabEffect.Slip: RenderSlip(t); return;
                case TabEffect.Curl: RenderCurlAuto(t); return;
            }
            float e = (float)(t * t * (3 - 2 * t));
            float shift = (2 * e - 1) * _span;
            float lo = -1.2f * _band, hi = _band, inv = 1f / _band;
            float flicker = 0.9f + 0.1f * MathF.Sin((float)t * 70f);
            int w = _w;
            int[] a = _a, b = _b, o = Output;
            float[] dm = _d;

            Parallel.For(0, _h, y =>
            {
                int row = y * w;
                for (int i = row; i < row + w; i++)
                {
                    float d = dm[i] + shift;
                    o[i] = d < lo ? a[i] : d > hi ? b[i] : Burn(a[i], b[i], d * inv, dm[i], flicker);
                }
            });
        }

        private void RenderFade(double t)
        {
            int k = (int)(Math.Clamp(t, 0, 1) * 256), ik = 256 - k;
            int w = _w;
            int[] a = _a, b = _b, o = Output;
            Parallel.For(0, _h, y =>
            {
                int row = y * w;
                for (int i = row; i < row + w; i++)
                {
                    int p = a[i], q = b[i];
                    int r = (((p >> 16) & 255) * ik + ((q >> 16) & 255) * k) >> 8;
                    int g = (((p >> 8) & 255) * ik + ((q >> 8) & 255) * k) >> 8;
                    int bl = ((p & 255) * ik + (q & 255) * k) >> 8;
                    o[i] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | bl;
                }
            });
        }

        // s is the signed distance to the crossing in band widths: negative is still the old page, positive the new one.
        private static int Burn(int oldPx, int newPx, float s, float height, float flicker)
        {
            int px = s < 0 ? oldPx : newPx;
            float r = (px >> 16) & 255, g = (px >> 8) & 255, b = px & 255;

            if (s < 0)
            {
                // scorching grows toward the front; contour lines of the terrain glow in the char
                float k = Smooth(-1.2f, -0.15f, s);
                float dark = 1 - 0.92f * k;
                r *= dark; g *= dark; b *= dark;
                float q = s * 8f;
                float line = 1 - Smooth(0.03f, 0.14f, MathF.Abs(q - MathF.Round(q)));
                float ember = k * k * (0.3f + 0.7f * line);
                r += 255 * ember; g += 80 * ember; b += 10 * ember;
            }
            else
            {
                float glow = 1 - s;
                glow = glow * glow * glow * 0.8f;
                r += 255 * glow; g += 110 * glow; b += 20 * glow;
            }

            // flame core along the crossing, white-hot in the middle; the terrain height gives it a stable grain
            float c = 1 - MathF.Abs(s + 0.05f) / 0.4f;
            if (c > 0)
            {
                c = c * c * (3 - 2 * c);
                float grain = (0.85f + 0.3f * (((int)(height * 8191f) & 255) / 255f)) * flicker;
                float hot = c * c;
                float fr = 255, fg = 90 + 150 * hot, fb = 10 + 140 * hot * hot;
                r += (fr * grain - r) * c; g += (fg * grain - g) * c; b += (fb * grain - b) * c;
            }

            return unchecked((int)0xFF000000) | (Clamp(r) << 16) | (Clamp(g) << 8) | Clamp(b);
        }

        private static int Clamp(float v) => v < 0 ? 0 : v > 255 ? 255 : (int)v;

        private static float Smooth(float e0, float e1, float x)
        {
            float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
            return t * t * (3 - 2 * t);
        }

        // ---------- Drop: the new page falls in from above like a heavy box, bounces and rocks before it settles ----------

        private void RenderDrop(double t)
        {
            if (t >= 1) { Array.Copy(_b, Output, Output.Length); return; }
            double s = t * DurationMs(TabEffect.Drop) / 1000;
            const double fall = 0.42, e = 0.3, tilt = 0.05;
            double y0 = 1.06, g = 2 * y0 / (fall * fall); // heights are in page heights
            double h;                                      // height above the floor
            if (s < fall) h = y0 - 0.5 * g * s * s;
            else
            {
                h = 0;
                double v = g * fall, tt = s - fall;
                for (int i = 0; i < 4; i++)
                {
                    v *= e;
                    double air = 2 * v / g;
                    if (tt < air) { h = v * tt - 0.5 * g * tt * tt; break; }
                    tt -= air;
                }
            }
            double tau = Math.Max(0, s - fall);
            double phi = _dir * tilt * Math.Exp(-tau / 0.16) * Math.Cos(2 * Math.PI * tau / 0.55);
            // pivot: bottom centre of the page, which sits on the screen's bottom edge when landed
            RenderRigid(_b, _a, _w / 2f, _h, _w / 2f, (float)(_h - h * _h), (float)phi, false, false);
        }

        // ---------- Slip: the old page hangs on two nails, one gives, it slips, swings, then the other gives and it falls ----------

        private void RenderSlip(double t)
        {
            if (t >= 1) { Array.Copy(_b, Output, Output.Length); return; }
            double s = t * DurationMs(TabEffect.Slip) / 1000;
            const double swing = 0.80, hold = 0.07, peak = 0.42;
            const float inset = 36;
            float nx = _dir > 0 ? _w - inset : inset; // the nail that holds
            double slip = 0.03 * _h * (1 - Math.Pow(1 - Math.Min(1, s / 0.2), 2));
            double phi, px = nx, py = inset + slip;
            bool holds = true;
            if (s < swing) phi = _dir * peak * Math.Pow(Math.Sin(s / swing * Math.PI / 2), 1.6);
            else if (s < swing + hold) phi = _dir * (peak + 0.004 * Math.Sin(s * 90));
            else
            {
                double tau = s - swing - hold, g = 11.3 * _h;
                phi = _dir * (peak + 0.9 * tau);
                px -= _dir * 30 * tau;
                py += 0.5 * g * tau * tau;
                holds = false;
            }
            RenderRigid(_a, _b, nx, inset, (float)px, (float)py, (float)phi, true, holds);
        }

        // Draws `top` as a rigid page (local pivot -> screen pivot, rotated by phi) over `under`, with a soft shadow around its edge.
        private void RenderRigid(int[] top, int[] under, float pivotU, float pivotV, float screenX, float screenY, float phi, bool nails, bool nailHolds)
        {
            float c = MathF.Cos(phi), sn = MathF.Sin(phi);
            float ce = pivotU - screenX * c + screenY * sn, fe = pivotV - screenX * sn - screenY * c;
            int w = _w, h = _h;
            int[] o = Output;
            const float reach = 36f;
            float lx = 36, rx = w - 36, ny = 36;
            bool leftHolds = nailHolds && _dir < 0, rightHolds = nailHolds && _dir > 0;
            Parallel.For(0, h, y =>
            {
                for (int x = 0, i = y * w; x < w; x++, i++)
                {
                    float u = c * x - sn * y + ce, v = sn * x + c * y + fe;
                    float sd = MathF.Max(MathF.Max(-u, u - (w - 1)), MathF.Max(-v, v - (h - 1)));
                    int below = under[i];
                    if (sd > 0 && sd < reach) { float k = 1 - sd / reach; below = Scale(below, 1 - 0.55f * k * k); }
                    if (sd > 0.5f) { o[i] = below; continue; }
                    int px = top[(int)Math.Clamp(v + 0.5f, 0, h - 1) * w + (int)Math.Clamp(u + 0.5f, 0, w - 1)];
                    if (nails)
                    {
                        // the nail that holds: a metal head; the one that gave: a dark hole
                        float dv = v - ny, du = u - lx, d2 = du * du + dv * dv;
                        if (d2 < 49) px = NailPx(d2, leftHolds);
                        else
                        {
                            du = u - rx; d2 = du * du + dv * dv;
                            if (d2 < 49) px = NailPx(d2, rightHolds);
                        }
                    }
                    float cov = sd <= -0.5f ? 1 : 0.5f - sd;
                    o[i] = cov >= 1 ? px : Mix(below, px, cov);
                }
            });
        }

        private static int NailPx(float d2, bool metal)
        {
            float k = d2 / 49f;
            return metal ? Mix(unchecked((int)0xFFE4E4E4), unchecked((int)0xFF6A6A6A), k) : Mix(unchecked((int)0xFF0C0C0C), unchecked((int)0xFF353535), k);
        }

        private static int Scale(int p, float f)
        {
            int r = (int)(((p >> 16) & 255) * f), g = (int)(((p >> 8) & 255) * f), b = (int)((p & 255) * f);
            return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
        }

        /// <summary>a at k = 0, b at k = 1.</summary>
        private static int Mix(int a, int b, float k)
        {
            float ik = 1 - k;
            int r = (int)(((a >> 16) & 255) * ik + ((b >> 16) & 255) * k);
            int g = (int)(((a >> 8) & 255) * ik + ((b >> 8) & 255) * k);
            int bl = (int)((a & 255) * ik + (b & 255) * k);
            return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | bl;
        }

        // ---------- Curl: the old page is turned from a lower corner like a leaf of a book, the new page lies beneath ----------

        public int Width => _w;
        public int Height => _h;
        public bool Mirrored => _mirror;
        /// <summary>The curl radius limit in pixels.</summary>
        public float MaxRadius => _rMax;

        private void RenderCurlAuto(double t)
        {
            if (t >= 1) { Array.Copy(_b, Output, Output.Length); return; }
            float e = (float)(t * t * (3 - 2 * t));
            float mx = _w - e * (2 * _w + 40); // ends past the far edge, where the fold has left the page
            float my = _h - 0.16f * _h * MathF.Sin(MathF.PI * e);
            RenderCurl(mx, my);
        }

        /// <summary>
        /// Draws the page with its lower right corner dragged to (mx, my); for a left corner the picture is mirrored, so callers
        /// pass x already mirrored (<c>width - x</c>). The page is wrapped around a cylinder whose axis is the fold line.
        /// </summary>
        public void RenderCurl(float mx, float my)
        {
            int w = _w, h = _h;
            float cx = w, cy = h;
            float dx = cx - mx, dy = cy - my, dist = MathF.Sqrt(dx * dx + dy * dy);
            bool mirror = _mirror;
            int[] a = _a, b = _b, o = Output;
            if (dist < 1.5f) { Array.Copy(a, o, o.Length); return; }

            float nx = dx / dist, ny = dy / dist;
            // a page bends wider the further it is pulled; a tight, fixed radius reads as a sharp fold that stretches out
            float r = MathF.Min(_rMax * (1 + 1.5f * dist / w), dist / MathF.PI), pr = MathF.PI * r;
            float line = ((mx + cx) * nx + (my + cy) * ny) / 2 - pr / 2;
            float reach = r * 1.4f + 18f;
            int paper = unchecked((int)0xFFEAEAE4);

            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + (mirror ? w - 1 - x : x);
                    float t = x * nx + y * ny - line;
                    if (t > r + reach) { o[i] = b[i]; continue; }

                    if (t <= r)
                    {
                        // underside of the turned part: flat beyond the fold, around the cylinder inside it
                        float ang = t <= 0 ? pr : r * (MathF.PI - MathF.Asin(t / r));
                        float k = ang - t;
                        float sx = x + k * nx, sy = y + k * ny;
                        if (sx >= 0 && sx < w && sy >= 0 && sy < h)
                        {
                            int src = (int)sy * w + (mirror ? w - 1 - (int)sx : (int)sx);
                            float lit = 0.78f - 0.22f * MathF.Cos(ang / r);
                            o[i] = Scale(Mix(a[src], paper, 0.62f), lit);
                            continue;
                        }
                        // the page's top side
                        float af = t <= 0 ? t : r * MathF.Asin(t / r);
                        float k2 = af - t;
                        float fx = x + k2 * nx, fy = y + k2 * ny;
                        if (fx >= 0 && fx < w && fy >= 0 && fy < h)
                        {
                            int src = (int)fy * w + (mirror ? w - 1 - (int)fx : (int)fx);
                            float shade = t <= 0 ? 1 - 0.3f * Sq(MathF.Max(0, 1 + t / (1.6f * r))) : 1 - 0.3f * MathF.Sin(af / r);
                            o[i] = Scale(a[src], shade);
                            continue;
                        }
                    }
                    // the new page, shaded where the curl shadows it
                    float sh = 1 - (t - r) / reach;
                    o[i] = sh > 0 ? Scale(b[i], 1 - 0.5f * sh * sh) : b[i];
                }
            });
        }

        private static float Sq(float v) => v * v;

        // ---------- terrain ----------

        private static float Fbm(float x, float y, int seed)
        {
            float sum = 0, amp = 1, norm = 0;
            for (int o = 0; o < 5; o++)
            {
                sum += amp * Noise(x, y, seed + o * 31);
                norm += amp;
                amp *= 0.5f; x = x * 2 + 17.3f; y = y * 2 + 5.1f;
            }
            return sum / norm;
        }

        private static float Noise(float x, float y, int seed)
        {
            int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y);
            float fx = x - ix, fy = y - iy;
            fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
            float a = Hash(ix, iy, seed), b = Hash(ix + 1, iy, seed), c = Hash(ix, iy + 1, seed), d = Hash(ix + 1, iy + 1, seed);
            float top = a + (b - a) * fx, bottom = c + (d - c) * fx;
            return top + (bottom - top) * fy;
        }

        private static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1274126177);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / 16777216f;
            }
        }

        // ---------- capture ----------

        /// <summary>PNG of what the view shows right now, or null when it can't be captured.</summary>
        public static async Task<byte[]?> CaptureAsync(WebView2 view)
        {
            try
            {
                if (view.CoreWebView2 == null) return null;
                using var ms = new MemoryStream();
                var capture = view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                if (await Task.WhenAny(capture, Task.Delay(500)) != capture) return null;
                await capture;
                return ms.ToArray();
            }
            catch { return null; } // the view is being closed or disposed: just skip the effect
        }

        public static Task<PageFrame?> DecodeAsync(byte[] png) => Task.Run(() =>
        {
            try
            {
                using var ms = new MemoryStream(png);
                var frame = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var bmp = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
                var px = new int[bmp.PixelWidth * bmp.PixelHeight];
                bmp.CopyPixels(px, bmp.PixelWidth * 4, 0);
                return new PageFrame(bmp.PixelWidth, bmp.PixelHeight, px);
            }
            catch { return null; }
        });
    }
}
