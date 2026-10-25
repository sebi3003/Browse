using System.IO;
using System.Text.Json;

namespace Browse.Services
{
    /// <summary>Reads bookmarks from the local Microsoft Edge profiles (read-only).</summary>
    public static class EdgeImport
    {
        private static readonly string Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "User Data");

        public sealed record EdgeProfile(string Dir, string Name, int Count);

        public static List<EdgeProfile> Profiles()
        {
            var list = new List<EdgeProfile>();
            if (!Directory.Exists(Root)) return list;
            foreach (var dir in Directory.GetDirectories(Root))
            {
                if (!File.Exists(Path.Combine(dir, "Bookmarks"))) continue;
                try { list.Add(new(dir, ProfileName(dir), BrowserStore.Flatten(Read(dir)).Count())); } catch { }
            }
            return list;
        }

        /// <summary>The Edge bookmark bar as top-level items; "other"/"synced" bookmarks become folders of their own.</summary>
        public static List<Bookmark> Read(string profileDir)
        {
            using var doc = JsonDocument.Parse(ReadShared(Path.Combine(profileDir, "Bookmarks")));
            var roots = doc.RootElement.GetProperty("roots");
            var result = new List<Bookmark>();
            if (roots.TryGetProperty("bookmark_bar", out var bar)) result.AddRange(Children(bar));
            foreach (var (key, title) in new[] { ("other", "Other bookmarks"), ("synced", "Mobile bookmarks") })
                if (roots.TryGetProperty(key, out var node) && Children(node) is { Count: > 0 } items)
                    result.Add(new(title, "", items));
            return result;
        }

        private static List<Bookmark> Children(JsonElement folder)
        {
            var items = new List<Bookmark>();
            if (!folder.TryGetProperty("children", out var children)) return items;
            foreach (var n in children.EnumerateArray())
            {
                string title = n.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                if (n.GetProperty("type").GetString() == "folder")
                {
                    if (Children(n) is { Count: > 0 } sub) items.Add(new(title, "", sub));
                }
                else if (n.TryGetProperty("url", out var u) && u.GetString() is { Length: > 0 } url)
                    items.Add(new(title.Length > 0 ? title : url, url));
            }
            return items;
        }

        private static string ProfileName(string dir)
        {
            try
            {
                using var doc = JsonDocument.Parse(ReadShared(Path.Combine(dir, "Preferences")));
                if (doc.RootElement.GetProperty("profile").GetProperty("name").GetString() is { Length: > 0 } name) return name;
            }
            catch { }
            return Path.GetFileName(dir);
        }

        // Edge may be running and holding the file open.
        private static string ReadShared(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }
    }
}
