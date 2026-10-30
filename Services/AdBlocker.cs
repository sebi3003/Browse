using System.IO;
using System.Net.Http;
using Microsoft.Web.WebView2.Core;

namespace Browse.Services
{
    /// <summary>
    /// Blocks ad/tracker/consent-manager requests by domain (HaGeZi "Pro" list, cached and refreshed in the
    /// background), hides cookie banners, and denies notification prompts and unrequested popups.
    /// </summary>
    public static class AdBlocker
    {
        private const string ListUrl = "https://raw.githubusercontent.com/hagezi/dns-blocklists/main/wildcard/pro-onlydomains.txt";
        private static readonly TimeSpan MaxAge = TimeSpan.FromDays(3);
        private static readonly string CachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Browse", "blocklist.txt");

        // Cookie-consent platforms: with their scripts blocked the banner never gets built.
        private static readonly string[] ConsentDomains =
        [
            "cookiebot.com", "cookielaw.org", "onetrust.com", "usercentrics.eu", "usercentrics.com", "trustarc.com", "truste.com",
            "didomi.io", "sourcepoint.com", "quantcast.mgr.consensu.org", "consensu.org", "iubenda.com", "cookiepro.com",
            "cookiescript.com", "termly.io", "osano.com", "civiccomputing.com", "cookieyes.com", "complianz.io", "cookiefirst.com",
            "consentmanager.net", "cmp.osano.com", "privacy-mgmt.com", "evidon.com", "cookie-script.com", "borlabs.io",
        ];

        private static HashSet<string> _blocked = new(ConsentDomains, StringComparer.OrdinalIgnoreCase);

        public static void Start()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (File.Exists(CachePath)) Load(File.ReadLines(CachePath));
                    if (File.Exists(CachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath) < MaxAge) return;
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                    var text = await http.GetStringAsync(ListUrl);
                    var lines = text.Split('\n');
                    if (lines.Length < 10000) return; // not a real list
                    Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                    await File.WriteAllTextAsync(CachePath, text);
                    Load(lines);
                }
                catch { /* offline: keep whatever is loaded; retried on next launch */ }
            });
        }

        private static void Load(IEnumerable<string> lines)
        {
            var set = new HashSet<string>(ConsentDomains, StringComparer.OrdinalIgnoreCase);
            foreach (var raw in lines)
            {
                var l = raw.Trim();
                if (l.Length > 0 && l[0] != '#' && l[0] != '!') set.Add(l);
            }
            _blocked = set;
        }

        /// <summary>True for a listed domain or any of its subdomains.</summary>
        public static bool IsBlocked(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
            var set = _blocked;
            var host = uri.IdnHost;
            while (true)
            {
                if (set.Contains(host)) return true;
                int dot = host.IndexOf('.');
                if (dot < 0 || host.IndexOf('.', dot + 1) < 0) return false; // never test a bare TLD
                host = host[(dot + 1)..];
            }
        }

        public static async Task AttachAsync(CoreWebView2 c)
        {
            c.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Strict;

            c.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            c.WebResourceRequested += (_, e) =>
            {
                if (IsBlocked(e.Request.Uri))
                    e.Response = c.Environment.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain");
            };

            // No "show notifications" prompts: always denied, never asked.
            c.PermissionRequested += (_, e) =>
            {
                if (e.PermissionKind == CoreWebView2PermissionKind.Notifications) e.State = CoreWebView2PermissionState.Deny;
            };

            await c.AddScriptToExecuteOnDocumentCreatedAsync(CosmeticScript.Replace("__SEL__", Selectors));
            await c.AddScriptToExecuteOnDocumentCreatedAsync(YouTubeScript);
            await c.AddScriptToExecuteOnDocumentCreatedAsync(ClickJackScript);
        }

        // Popup guard, runs in every frame (ad iframes included). Ad scripts open tabs in three ways, all covered:
        // click-jacking (a transparent layer over a player), popunders (a click listener on window/document/body that opens
        // an ad on any click), and scripted opens with no gesture at all (timers, page load). A new tab/window is only
        // allowed when a real click or key press happened just before, was not on a click-jacking layer, is handled by
        // something other than a page-wide capture listener or a full-view overlay, and is the first open of that gesture.
        // Covers window.open, a.click() / dispatched clicks on target=_blank links, and form.submit() to _blank.
        // A refused open returns a stub window (so page scripts don't throw), and the layers that stole the click are
        // switched off and the click is passed on to the element underneath, so the first click still plays the video.
        private const string ClickJackScript = """
            (() => {
              if (window.__bhj) return;
              window.__bhj = true;

              const now = () => performance.now();
              const GESTURE_MS = 1500;
              const gesture = { t: -1e9, x: null, y: null, hijack: false, opened: false, neutralized: false };

              const hasContent = el => {
                if (el.matches('video,audio,img,svg,canvas,input,button,select,textarea,iframe')) return true;
                if (el.querySelector('video,audio,img,svg,canvas,input,button,select,textarea')) return true;
                return (el.textContent || '').trim().length > 0;
              };
              const isClear = cs =>
                (parseFloat(cs.opacity) < 0.1 || /rgba\(\s*0,\s*0,\s*0,\s*0\s*\)|transparent/.test(cs.backgroundColor))
                && !cs.backgroundImage.includes('url(');
              const covers = r => {
                const tw = innerWidth >> 4, th = innerHeight >> 4;
                return r.left <= tw && r.top <= th && r.right >= innerWidth - tw && r.bottom >= innerHeight - th;
              };
              const highZ = el => {
                for (; el && el.nodeType === 1; el = el.parentElement) {
                  const cs = getComputedStyle(el);
                  if (cs.position !== 'static' && parseInt(cs.zIndex, 10) > 1000) return true;
                }
                return false;
              };

              // A large, empty, invisible positioned element: the click-jacking layer.
              const isOverlay = el => {
                const cs = getComputedStyle(el);
                if (cs.position !== 'fixed' && cs.position !== 'absolute') return false;
                if (cs.visibility === 'hidden' || cs.display === 'none') return false;
                const r = el.getBoundingClientRect();
                if (r.width < Math.min(300, innerWidth * 0.5) || r.height < Math.min(150, innerHeight * 0.4)) return false;
                return !hasContent(el) && isClear(cs);
              };
              // Anything that may be hiding the real target: empty, positioned, and invisible or full-view with a huge z-index.
              const isMask = el => {
                const cs = getComputedStyle(el);
                if (cs.position === 'static' || hasContent(el)) return false;
                return isClear(cs) || (highZ(el) && covers(el.getBoundingClientRect()));
              };
              const hijacked = e => {
                let el = e.target;
                for (let i = 0; el && el.nodeType === 1 && i < 4; i++, el = el.parentElement)
                  if (isOverlay(el)) return el;
                return null;
              };

              // Track the last real click / key press.
              const startEvents = ['pointerdown', 'mousedown', 'touchstart', 'keydown'];
              const gestureEvent = e => {
                if (!e.isTrusted) return;
                const pt = e.changedTouches ? e.changedTouches[0] : e;
                if (startEvents.includes(e.type)) { gesture.opened = false; gesture.neutralized = false; gesture.hijack = false; }
                gesture.t = now();
                if (pt && pt.clientX !== undefined) { gesture.x = pt.clientX; gesture.y = pt.clientY; }
                const el = e.target && e.target.nodeType === 1 ? hijacked(e) : null;
                if (el) {
                  gesture.hijack = true;
                  if (e.type === 'click') {
                    const a = el.closest('a[href]');
                    if (a && (a.target === '_blank' || a.origin !== location.origin)) { e.preventDefault(); e.stopImmediatePropagation(); }
                  }
                }
              };
              for (const t of ['pointerdown', 'mousedown', 'mouseup', 'touchstart', 'touchend', 'click', 'keydown'])
                window.addEventListener(t, gestureEvent, true);

              // May a new window open right now?
              const allowed = () => {
                if (now() - gesture.t > GESTURE_MS) return false;   // no click/key just before: timers, page load, ...
                if (gesture.hijack || gesture.opened) return false; // click-jacked, or a second open for one click
                const ev = window.event;
                if (ev) {
                  const ct = ev.currentTarget;
                  // a page-wide listener in the capture/target phase fires on every click: the popunder pattern
                  if (ct && (ct === window || ct === document || ct === document.documentElement || ct === document.body) && ev.eventPhase !== 3) return false;
                  if (ct && ct.nodeType === 1 && covers(ct.getBoundingClientRect()) && highZ(ct)) return false; // full-view overlay handler
                }
                gesture.opened = true;
                return true;
              };

              // The click was taken by an ad: switch off the layers above the real target and click that instead.
              const neutralize = url => {
                if (gesture.neutralized || gesture.x === null) return;
                gesture.neutralized = true;
                const { x, y } = gesture;
                setTimeout(() => {
                  const masks = [];
                  let real = null;
                  for (const el of document.elementsFromPoint(x, y)) {
                    if (isMask(el)) masks.push(el); else { real = el; break; }
                  }
                  if (!masks.length) return;
                  for (const m of masks) {
                    m.style.setProperty('display', 'none', 'important');
                    m.style.setProperty('pointer-events', 'none', 'important');
                  }
                  if (!real || real.tagName === 'IFRAME') return; // an iframe player gets the next click directly
                  const a = real.closest('a[href]');
                  try { if (a && url && a.href === new URL(url, location.href).href) return; } catch {} // would reopen the same ad
                  real.click();
                }, 0);
              };

              const stubWindow = () => ({
                closed: true, opener: null, name: '', focus() {}, blur() {}, close() {}, postMessage() {},
                location: { href: '', assign() {}, replace() {}, reload() {} },
                document: { write() {}, writeln() {}, open() {}, close() {} },
              });
              const newTab = t => String(t || '').toLowerCase() === '_blank';

              // Proxies keep toString() native-looking.
              const wrap = (obj, name, fn) => {
                const real = obj[name];
                if (typeof real === 'function') obj[name] = new Proxy(real, { apply: (t, self, args) => fn(t, self, args) });
              };

              wrap(window, 'open', (t, self, a) => {
                if (/^_(self|top|parent)$/i.test(a[1] || '') || allowed()) return Reflect.apply(t, self, a);
                neutralize(a[0] ? String(a[0]) : '');
                return stubWindow();
              });
              wrap(HTMLElement.prototype, 'click', (t, self, a) => {
                if (self instanceof HTMLAnchorElement && self.href && newTab(self.target) && !allowed()) { neutralize(self.href); return; }
                return Reflect.apply(t, self, a);
              });
              wrap(EventTarget.prototype, 'dispatchEvent', (t, self, a) => {
                const ev = a[0];
                if (ev && ev.type === 'click' && ev.isTrusted === false && self instanceof HTMLAnchorElement && newTab(self.target) && !allowed()) {
                  neutralize(self.href);
                  return false;
                }
                return Reflect.apply(t, self, a);
              });
              wrap(HTMLFormElement.prototype, 'submit', (t, self, a) => {
                if (newTab(self.target) && !allowed()) { neutralize(''); return; }
                return Reflect.apply(t, self, a);
              });
            })();
            """;

        // YouTube ads come from youtube.com itself, so a domain list can't catch them. Three layers, YouTube only:
        // 1) drop the ad fields from the player JSON before the player reads them (like uBlock's json-prune),
        // 2) hide ad slots in the page, 3) if an ad still plays, mute it, jump to its end and click "Skip".
        private const string YouTubeScript = """
            (() => {
              if (!/(^|\.)youtube(-nocookie)?\.com$/.test(location.hostname)) return;

              const KEYS = ['adPlacements', 'adSlots', 'playerAds', 'adBreakHeartbeatParams'];
              const prune = o => {
                if (o && typeof o === 'object') {
                  for (const k of KEYS) if (k in o) { try { delete o[k]; } catch {} }
                  if (o.playerResponse) prune(o.playerResponse);
                }
                return o;
              };

              const parse = JSON.parse;
              JSON.parse = function (text, reviver) {
                return prune(parse.call(this, text, reviver));
              };
              const json = Response.prototype.json;
              Response.prototype.json = function () { return json.call(this).then(prune); };

              let initial;
              try {
                Object.defineProperty(window, 'ytInitialPlayerResponse', {
                  configurable: true,
                  get: () => initial,
                  set: v => { initial = prune(v); },
                });
              } catch {}

              const css = [
                '#masthead-ad', '#player-ads', '.ytd-ad-slot-renderer', 'ytd-ad-slot-renderer', 'ytd-in-feed-ad-layout-renderer',
                'ytd-display-ad-renderer', 'ytd-promoted-video-renderer', 'ytd-promoted-sparkles-web-renderer', 'ytd-banner-promo-renderer',
                'ytd-statement-banner-renderer', 'ytd-companion-slot-renderer', '.ytp-ad-overlay-container', '.ytp-ad-module',
                'ytd-rich-item-renderer:has(ytd-ad-slot-renderer)', 'ytd-rich-section-renderer:has(ytd-statement-banner-renderer)',
              ].join(',') + '{display:none !important}';
              const inject = () => {
                const s = document.createElement('style');
                s.textContent = css;
                document.documentElement.appendChild(s);
              };
              if (document.documentElement) inject();
              else new MutationObserver((_, o) => { if (document.documentElement) { o.disconnect(); inject(); } }).observe(document, { childList: true });

              let inAd = false, wasMuted = false, rate = 1;
              setInterval(() => {
                const player = document.getElementById('movie_player');
                const video = document.querySelector('video.html5-main-video');
                if (!player || !video) return;
                if (player.classList.contains('ad-showing')) {
                  if (!inAd) { inAd = true; wasMuted = video.muted; rate = video.playbackRate || 1; }
                  video.muted = true;
                  video.playbackRate = 16;
                  if (isFinite(video.duration) && video.duration > 0) video.currentTime = video.duration;
                  document.querySelector('.ytp-ad-skip-button, .ytp-ad-skip-button-modern, .ytp-skip-ad-button')?.click();
                  document.querySelector('.ytp-ad-overlay-close-button')?.click();
                } else if (inAd) {
                  inAd = false;
                  video.muted = wasMuted;
                  video.playbackRate = rate;
                }
              }, 250);
            })();
            """;

        // Hides the common cookie banners and gives back page scrolling if a banner had locked it.
        private const string Selectors =
            "#onetrust-consent-sdk,#onetrust-banner-sdk,.onetrust-pc-dark-filter,#CybotCookiebotDialog,#CybotCookiebotDialogBodyUnderlay," +
            "#cookiebanner,#cookie-banner,#cookie-notice,#cookie-law-info-bar,#cookieconsent,.cc-window,.cc-banner,#cmpbox,#cmpbox2," +
            "#usercentrics-root,#truste-consent-track,#didomi-host,#didomi-popup,[id^=sp_message_container],iframe[id^=sp_message_iframe]," +
            ".qc-cmp2-container,#qc-cmp2-container,#consent-banner,.consent-banner,#gdpr-cookie-notice,.gdpr-banner,#cookie-consent," +
            ".cookie-consent,#cookiescript_injected,#cookie-bar,.cookie-bar,.cookie-notice,#CookieConsent,.cookiebanner,#hs-eu-cookie-confirmation," +
            ".fc-consent-root,#_evidon_banner,#ccc,#ccc-overlay,.cky-consent-container,.cky-overlay,#iubenda-cs-banner,.iubenda-cs-overlay," +
            "[id*=cookie i][id*=banner i],[class*=cookie i][class*=banner i],[id*=consent i][id*=banner i],[class*=cookie-consent i]," +
            "[id*=cookie-consent i],[role=dialog][aria-label*=cookie i],[role=dialog][aria-label*=consent i],[role=dialog][aria-label*=privacy i]";

        private const string CosmeticScript = """
            (() => {
              const SEL = `__SEL__`;
              const inject = () => {
                const s = document.createElement('style');
                s.textContent = SEL + '{display:none !important}';
                document.documentElement.appendChild(s);
              };
              if (document.documentElement) inject();
              else new MutationObserver((_, o) => { if (document.documentElement) { o.disconnect(); inject(); } }).observe(document, { childList: true });
              const unlock = () => {
                if (!document.querySelector(SEL)) return;
                for (const el of [document.documentElement, document.body])
                  if (el && getComputedStyle(el).overflow === 'hidden') el.style.setProperty('overflow', 'auto', 'important');
              };
              addEventListener('load', () => { unlock(); setTimeout(unlock, 1500); });
            })();
            """;

    }
}
