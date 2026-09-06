using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KeyPalette
{
    public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
    {
        private readonly HardwareEngine _engine = new();

        public MainWindow()
        {
            InitializeComponent();

            _engine.StatusChanged += OnHardwareStatus;
            _engine.ColorChanged += OnColorChanged;

            Loaded += (_, _) => TryConnect();
            Closed += (_, _) => _engine.Dispose();
        }

        private void TryConnect(string? explicitDllPath = null)
        {
            bool ok = _engine.Connect(explicitDllPath);
            HwStatusLabel.Text = ok
                ? $"Connected via {_engine.LoadedFrom}"
                : "Not connected - see status below, or click Locate InsydeDCHU.dll.";
        }

        private void OnHardwareStatus(string message)
        {
            Dispatcher.Invoke(() => HwStatusLabel.Text = message);
        }

        private void OnColorChanged(byte r, byte g, byte b)
        {
            Dispatcher.Invoke(() =>
            {
                var color = Color.FromRgb(r, g, b);
                ColorGlow.Background = new SolidColorBrush(color);
                if (ColorGlow.Effect is System.Windows.Media.Effects.DropShadowEffect glow)
                {
                    glow.Color = color;
                }
            });
        }

        private void BtnConnect_Click(object sender, RoutedEventArgs e) => TryConnect();

        private void BtnLocateDll_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Locate InsydeDCHU.dll",
                Filter = "InsydeDCHU.dll|InsydeDCHU.dll|All DLL files (*.dll)|*.dll",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                TryConnect(dialog.FileName);
            }
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            int speed = (int)SpeedSlider.Value;
            string selected = ((ComboBoxItem)EffectSelector.SelectedItem).Content.ToString() ?? "";

            EffectType effect = selected switch
            {
                var s when s.Contains("Breathing") => EffectType.Breathing,
                var s when s.Contains("Rainbow") => EffectType.Rainbow,
                var s when s.Contains("Blink") => EffectType.Blink,
                var s when s.Contains("Heartbeat") => EffectType.Heartbeat,
                _ => EffectType.Static
            };

            if (effect == EffectType.Static)
            {
                ApplyHex(HexInput.Text);
            }
            else
            {
                _engine.StartEffect(effect, speed);
            }

            StatusLabel.Text = $"Status: Running {selected.Split('/')[0].Trim()}";
        }

        private void Swatch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string hex)
            {
                _engine.StopEffect();
                ApplyHex(hex);
                StatusLabel.Text = $"Status: Static #{hex} Applied";
            }
        }

        private void BtnSetHex_Click(object sender, RoutedEventArgs e)
        {
            _engine.StopEffect();
            ApplyHex(HexInput.Text);
            StatusLabel.Text = $"Status: Static #{HexInput.Text} Applied";
        }

        private void ApplyHex(string hex)
        {
            hex = hex.Trim().TrimStart('#');
            if (hex.Length != 6 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint parsed))
            {
                MessageBox.Show("Enter a valid 6-digit hex color, e.g. 00E5FF.", "Invalid Color", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            byte r = (byte)((parsed >> 16) & 0xFF);
            byte g = (byte)((parsed >> 8) & 0xFF);
            byte b = (byte)(parsed & 0xFF);
            _engine.SetColor(r, g, b);
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _engine.StopEffect();
            StatusLabel.Text = "Status: Stopped";
        }
    }
}