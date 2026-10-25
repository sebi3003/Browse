using System.IO;
using System.Windows;
using Velopack;

namespace Browse
{
    public partial class App : Application
    {
        // App.xaml's ApplicationDefinition was removed (see .csproj) so Main can run
        // VelopackApp first: it handles install/update/uninstall callbacks and exits before any UI.
        [STAThread]
        private static void Main(string[] args)
        {
            VelopackApp.Build().Run();

            var app = new App();
            app.InitializeComponent();
            app.DispatcherUnhandledException += (_, e) => { LogCrash("UI thread", e.Exception); e.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) LogCrash("AppDomain", ex); };
            TaskScheduler.UnobservedTaskException += (_, e) => { LogCrash("Task", e.Exception); e.SetObserved(); };
            app.Run(new MainWindow(args.FirstOrDefault()));
        }

        // Roaming, not Local: Velopack owns %LocalAppData%\Browse and can wipe unknown files there.
        internal static void LogCrash(string source, Exception ex)
        {
            try
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Browse", "crash.log");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\n{ex}\n\n");
            }
            catch { }
        }
    }
}
