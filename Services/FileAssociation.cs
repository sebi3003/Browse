using System.IO;
using Microsoft.Win32;

namespace Browse.Services
{
    // Registers Browse as an "Open with" candidate for local web files (per-user, no admin).
    // Idempotent and cheap, so it runs on every start and keeps the path right after Velopack updates.
    internal static class FileAssociation
    {
        private const string ProgId = "BrowseHTML";
        private const string AppName = "Browse";
        private static readonly string[] Extensions = { ".html", ".htm", ".xhtml", ".svg" };

        public static void Register()
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;
                var command = $"\"{exe}\" \"%1\"";

                using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
                {
                    prog.SetValue(null, "Browse HTML Document");
                    using var icon = prog.CreateSubKey("DefaultIcon");
                    icon.SetValue(null, $"\"{exe}\",0");
                    using var cmd = prog.CreateSubKey(@"shell\open\command");
                    cmd.SetValue(null, command);
                }

                foreach (var ext in Extensions)
                {
                    using var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\OpenWithProgids");
                    k.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
                }

                // Makes Browse appear under Settings > Default apps.
                using (var cap = Registry.CurrentUser.CreateSubKey($@"Software\{AppName}\Capabilities"))
                {
                    cap.SetValue("ApplicationName", AppName);
                    cap.SetValue("ApplicationDescription", "Browse web browser");
                    using var assoc = cap.CreateSubKey("FileAssociations");
                    foreach (var ext in Extensions) assoc.SetValue(ext, ProgId);
                }
                using (var reg = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
                    reg.SetValue(AppName, $@"Software\{AppName}\Capabilities");
            }
            catch { /* best effort: a locked-down registry must not block startup */ }
        }
    }
}
