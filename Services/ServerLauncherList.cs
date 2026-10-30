using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Browse.Services
{
    /// <summary>One server chip on the Work bookmark bar: what it is called, where it opens and how to get its icon right now.</summary>
    public sealed record ServerChip(string Title, string Url, Func<ImageSource?> GetIcon)
    {
        public ImageSource? Icon => GetIcon();
    }

    /// <summary>
    /// The servers the ServerLauncher app manages (%LocalAppData%\ServerLauncher\servers.json: name, program, port).
    /// Each shows with the icon of its own program. Read-only: Browse never changes that file.
    /// </summary>
    public static class ServerLauncherList
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ServerLauncher", "servers.json");

        private sealed record Saved(string Name, string ExePath, int Port);

        private static readonly Dictionary<string, (DateTime Time, ImageSource? Icon)> Icons = new(StringComparer.OrdinalIgnoreCase);

        public static List<(string Name, string ExePath, int Port)> Load()
        {
            try
            {
                if (File.Exists(FilePath) && JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(FilePath)) is { } saved)
                    return saved.Where(s => s.Port > 0).Select(s => (s.Name, s.ExePath ?? "", s.Port)).ToList();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            return [];
        }

        /// <summary>The program's own icon (cached until the exe changes); null when the exe is missing or has none.</summary>
        public static ImageSource? IconOf(string exePath)
        {
            if (!File.Exists(exePath)) return null;
            var time = File.GetLastWriteTimeUtc(exePath);
            lock (Icons)
            {
                if (Icons.TryGetValue(exePath, out var c) && c.Time == time) return c.Icon;
                var icon = Extract(exePath, 48);
                Icons[exePath] = (time, icon);
                return icon;
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, uint[] ids, uint count, uint flags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);

        private static ImageSource? Extract(string exePath, int size)
        {
            var handles = new IntPtr[1];
            if (PrivateExtractIcons(exePath, 0, size, size, handles, new uint[1], 1, 0) != 1 || handles[0] == IntPtr.Zero) return null;
            try
            {
                var image = Imaging.CreateBitmapSourceFromHIcon(handles[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                return image;
            }
            finally { DestroyIcon(handles[0]); }
        }
    }
}
