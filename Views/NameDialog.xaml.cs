using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Browse.Views
{
    /// <summary>Asks for a short name (a new Feeds folder). OK stays disabled until something is typed.</summary>
    public partial class NameDialog : Window
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private NameDialog(string title, string prompt)
        {
            InitializeComponent();
            Title = title;
            Prompt.Text = prompt;
            SourceInitialized += (_, _) => { int on = 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, sizeof(int)); }; // dark title bar
            Loaded += (_, _) => NameBox.Focus();
        }

        public string Result => NameBox.Text.Trim();

        /// <summary>Shows the dialog; returns the typed name, or null when cancelled.</summary>
        public static string? Ask(Window? owner, string title, string prompt)
        {
            var dlg = new NameDialog(title, prompt) { Owner = owner };
            return dlg.ShowDialog() == true ? dlg.Result : null;
        }

        private void NameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
            OkButton.IsEnabled = NameBox.Text.Trim().Length > 0;

        private void NameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && OkButton.IsEnabled) { DialogResult = true; e.Handled = true; }
        }

        private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
