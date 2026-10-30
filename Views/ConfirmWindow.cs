using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Browse.Views
{
    /// <summary>A dark OK / Cancel confirmation, standing in for the white system MessageBox. Fixed dark colours like the theme window.</summary>
    public sealed class ConfirmWindow : Window
    {
        private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

        private ConfirmWindow(string message, string okText)
        {
            Title = "Browse";
            Width = 400;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Brush("#0A0C10");
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 14;

            var ok = MakeButton(okText, "#2B4F94", "#3665BD", true);
            ok.Click += (_, _) => DialogResult = true;
            var cancel = MakeButton("Cancel", "#1A2332", "#232F42", false);
            cancel.IsCancel = true;
            cancel.Click += (_, _) => DialogResult = false;

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = message, Foreground = Brush("#E7EDF5"), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(buttons);
            Content = panel;

            Loaded += (_, _) => cancel.Focus(); // like the MessageBox it replaces: Cancel is the default
        }

        private static Button MakeButton(string text, string bg, string hover, bool primary)
        {
            var b = new Button
            {
                Content = text, Width = 84, Height = 30, Cursor = Cursors.Hand, IsDefault = false,
                Margin = primary ? new Thickness(0, 0, 8, 0) : new Thickness(0),
                Foreground = Brush("#E7EDF5"), Background = Brush(bg),
            };
            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "B";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var trigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            trigger.Setters.Add(new Setter(Border.BackgroundProperty, Brush(hover), "B"));
            template.Triggers.Add(trigger);
            b.Template = template;
            return b;
        }

        /// <summary>True when the user chose OK.</summary>
        public static bool Ask(Window? owner, string message, string okText = "Delete")
        {
            var w = new ConfirmWindow(message, okText);
            if (owner != null && owner.IsLoaded) w.Owner = owner;
            return w.ShowDialog() == true;
        }
    }
}
