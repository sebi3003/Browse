using System.IO;

namespace Browse.Services
{
    /// <summary>
    /// Size of the browser engine's caches, for the cache chip on the toolbar. Cookies, logins, local storage, history and the
    /// page icons (the engine's Favicons database and Browse's own favicons folder) are not cache and are never counted or removed.
    /// </summary>
    public static class CacheService
    {
        private static readonly string EngineDir = Path.Combine(BrowserStore.ProfileDir, "EBWebView");

        // Everything that is only a speed-up and gets rebuilt on demand.
        private static readonly string[] CacheDirs =
        [
            @"Default\Cache", @"Default\Code Cache", @"Default\GPUCache", @"Default\DawnWebGPUCache", @"Default\DawnGraphiteCache",
            @"Default\Service Worker\CacheStorage", @"Default\Service Worker\ScriptCache", "GrShaderCache", "ShaderCache",
        ];

        public static long Measure()
        {
            long total = 0;
            foreach (var rel in CacheDirs)
            {
                var dir = Path.Combine(EngineDir, rel);
                if (!Directory.Exists(dir)) continue;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        try { total += new FileInfo(file).Length; } catch { /* the engine may delete it while we count */ }
                    }
                }
                catch { /* folder vanished or is locked: count what we got */ }
            }
            return total;
        }

        /// <summary>After the engine's own clear: removes whatever cache files it left that are not locked right now.</summary>
        public static void DeleteLeftovers()
        {
            foreach (var rel in CacheDirs)
            {
                var dir = Path.Combine(EngineDir, rel);
                if (!Directory.Exists(dir)) continue;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        try { File.Delete(file); } catch { /* in use: left for the engine */ }
                    }
                }
                catch { }
            }
        }

        /// <summary>The unit follows the size: 0 B, 512 KB, 93.7 MB, 1.2 GB.</summary>
        public static string Format(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return u == 0 ? $"{bytes} B" : v >= 100 ? $"{v:0} {units[u]}" : $"{v:0.#} {units[u]}";
        }
    }
}
