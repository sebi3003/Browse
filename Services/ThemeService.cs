using System.Windows;
using System.Windows.Media;

namespace Browse.Services
{
    public sealed record ThemeColor(string Key, string Label, string Default);

    /// <summary>
    /// The editable colours. Every key is an application-level brush; the XAML uses DynamicResource and the editor changes the
    /// brush's colour in place, so a change shows up at once. Overrides are kept in the store (Roaming\Browse\browse.json, "Theme").
    /// </summary>
    public static class ThemeService
    {
        public static readonly ThemeColor[] Colors =
        [
            new("Bg", "Window background", "#0A0C10"),
            new("ThemeTool", "Toolbar tiles (back, forward, address icon)", "#0B2A5B"),
            new("ThemeDownloads", "Downloads tile", "#E0E07B16"),
            new("ThemeMenu", "Menu tile", "#802E9E4F"),
            new("ThemeTab", "Tab", "#3E6B52"),
            new("ThemeTabHover", "Tab under the mouse", "#4A7D61"),
            new("ThemeTabActive", "Selected tab", "#5A9472"),
            new("ThemeTabText", "Tab text", "#E7EDF5"),
            new("ThemeTabActiveText", "Selected tab text", "#FFFFFF"),
            new("ThemeTabIcon", "Tab icon tile", "#0B2A5B"),
            new("ThemeChip", "Bookmark", "#1A2332"),
            new("ThemeChipHover", "Bookmark under the mouse", "#232F42"),
            new("ThemeChipText", "Bookmark text", "#E7EDF5"),
            new("ThemeChipIcon", "Bookmark icon tile", "#1A2332"),
            new("ThemeFolder", "Bookmark folder", "#0B2A5B"),
            new("ThemeFolderHover", "Folder under the mouse", "#123A7A"),
            new("ThemeFolderText", "Folder text", "#FFFFFF"),
        ];

        // The names of the colours in the sync file (docs/SYNC-FORMAT.md, "Theme") for each local key.
        private static readonly Dictionary<string, string> SyncNames = new()
        {
            ["Bg"] = "Window", ["ThemeTool"] = "ToolbarTile", ["ThemeDownloads"] = "Downloads", ["ThemeMenu"] = "Menu",
            ["ThemeTab"] = "Tab", ["ThemeTabHover"] = "TabHover", ["ThemeTabActive"] = "TabSelected", ["ThemeTabText"] = "TabText",
            ["ThemeTabActiveText"] = "TabSelectedText", ["ThemeTabIcon"] = "TabIcon", ["ThemeChip"] = "Bookmark",
            ["ThemeChipHover"] = "BookmarkHover", ["ThemeChipText"] = "BookmarkText", ["ThemeChipIcon"] = "BookmarkIcon",
            ["ThemeFolder"] = "Folder", ["ThemeFolderHover"] = "FolderHover", ["ThemeFolderText"] = "FolderText",
        };

        private static BrowserStore? _store;

        /// <summary>Raised when the user changes a colour (not when a sync applies one).</summary>
        public static event Action? Changed;

        private static Color DefaultOf(string key) => Parse(Colors.First(c => c.Key == key).Default)!.Value;

        /// <summary>Creates the brushes (defaults, then the saved overrides). Call once before the first window.</summary>
        public static void Init(BrowserStore store)
        {
            _store = store;
            foreach (var c in Colors)
                Set(c.Key, Parse(store.Theme.GetValueOrDefault(c.Key)) ?? DefaultOf(c.Key), save: false);
        }

        public static Color Get(string key) => Application.Current.Resources[key] is SolidColorBrush b ? b.Color : DefaultOf(key);

        /// <summary>A fresh brush with the current colour, for UI built in code.</summary>
        public static SolidColorBrush Brush(string key)
        {
            var b = new SolidColorBrush(Get(key));
            b.Freeze();
            return b;
        }

        public static void Set(string key, Color color, bool save = true)
        {
            var res = Application.Current.Resources;
            if (res[key] is SolidColorBrush b && !b.IsFrozen) b.Color = color; else res[key] = new SolidColorBrush(color);
            if (key == "Bg") Set("Surface", color, save: false);
            if (save) { Save(); Changed?.Invoke(); }
        }

        public static void Reset(string key) => Set(key, DefaultOf(key));

        public static void ResetAll()
        {
            foreach (var c in Colors) Set(c.Key, DefaultOf(c.Key), save: false);
            Save();
            Changed?.Invoke();
        }

        /// <summary>#RRGGBB or #AARRGGBB, the # optional, either case; anything else is null.</summary>
        public static Color? Parse(string? hex)
        {
            if (hex == null || !System.Text.RegularExpressions.Regex.IsMatch(hex, "^#?([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$")) return null;
            try { return (Color)ColorConverter.ConvertFromString(hex.StartsWith('#') ? hex : "#" + hex); } catch { return null; }
        }

        /// <summary>The changed colours under their sync-file names, as the sync file stores them.</summary>
        public static Dictionary<string, string> ToSync() =>
            Colors.Where(c => Get(c.Key) != DefaultOf(c.Key)).ToDictionary(c => SyncNames[c.Key], c => ToHex(Get(c.Key)), StringComparer.OrdinalIgnoreCase);

        /// <summary>A sync file's Theme cleaned up: known keys only, values that parse and differ from the default, normalised.</summary>
        public static Dictionary<string, string> NormalizeSync(IReadOnlyDictionary<string, string?> theme)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in Colors)
            {
                var name = SyncNames[c.Key];
                var hit = theme.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase));
                if (hit.Key != null && Parse(hit.Value) is { } col && col != DefaultOf(c.Key)) result[name] = ToHex(col);
            }
            return result;
        }

        /// <summary>Makes the overrides exactly these (a sync file's Theme) and repaints. UI thread. Not a local edit: <see cref="Changed"/> is not raised.</summary>
        public static void ApplyFromSync(IReadOnlyDictionary<string, string?> theme)
        {
            var clean = NormalizeSync(theme);
            foreach (var c in Colors)
                Set(c.Key, clean.TryGetValue(SyncNames[c.Key], out var hex) ? Parse(hex)!.Value : DefaultOf(c.Key), save: false);
            Save();
        }

        /// <summary>First connection: adds the file's colours for the parts this PC has not changed (a local change wins). UI thread.</summary>
        public static void MergeFromSync(IReadOnlyDictionary<string, string?> theme)
        {
            var local = ToSync();
            foreach (var (name, hex) in NormalizeSync(theme))
                if (!local.ContainsKey(name))
                    Set(SyncNames.First(kv => kv.Value == name).Key, Parse(hex)!.Value, save: false);
            Save();
        }

        public static string ToHex(Color c) => c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

        private static void Save()
        {
            if (_store == null) return;
            _store.Theme = Colors.Where(c => Get(c.Key) != DefaultOf(c.Key)).ToDictionary(c => c.Key, c => ToHex(Get(c.Key)));
            _store.SaveSoon();
        }
    }
}
