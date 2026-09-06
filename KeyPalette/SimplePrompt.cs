using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KeyPalette
{
    /// <summary>
    /// Tiny built-purely-in-code text input dialog (no separate .xaml needed),
    /// used to ask the user for a preset name.
    /// </summary>
    public static class SimplePrompt
    {
        public static string? ShowInputDialog(string title, string message, string defaultText = "")
        {
            var window = new Window
            {
                Title = title,
                Width = 380,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Owner = Application.Current.MainWindow,
                Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C))
            };

            var stack = new StackPanel { Margin = new Thickness(18) };

            stack.Children.Add(new TextBlock
            {
                Text = message,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap
            });

            var textBox = new TextBox { Text = defaultText, Margin = new Thickness(0, 0, 0, 16), Padding = new Thickness(4) };
            stack.Children.Add(textBox);

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okButton = new Button { Content = "Save", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancelButton = new Button { Content = "Cancel", Width = 80, IsCancel = true };
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            stack.Children.Add(buttonPanel);

            window.Content = stack;

            string? result = null;
            okButton.Click += (_, _) => { result = textBox.Text; window.DialogResult = true; };
            cancelButton.Click += (_, _) => { window.DialogResult = false; };

            textBox.Focus();
            textBox.SelectAll();

            return window.ShowDialog() == true ? result : null;
        }
    }
}
