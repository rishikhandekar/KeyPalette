using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.ToolTip;

namespace KeyPalette
{
    public enum UiMode { Static, Breathing, Rainbow, Blink, Heartbeat, Custom }

    public partial class MainWindow : Window
    {
        private readonly HardwareEngine _engine = new();
        private UiMode _mode = UiMode.Static;

        private string _lastHwStatus = "Not connected yet.";
        private TextBlock? _settingsHwLabel;

        private static readonly SolidColorBrush InactiveTabForeground = new(Color.FromRgb(0xAA, 0xAA, 0xAA));
        private static readonly SolidColorBrush ActiveTabForeground = Brushes.White;
        private static readonly SolidColorBrush ActiveTabBorder = new(Color.FromRgb(0xFF, 0x00, 0x3C));
        private static readonly SolidColorBrush ActiveTabBackground = new(Color.FromRgb(0x18, 0x18, 0x18));

        public MainWindow()
        {
            InitializeComponent();

            _engine.StatusChanged += OnHardwareStatus;
            _engine.ColorChanged += OnColorChanged;

            Loaded += (_, _) =>
            {
                TryConnect();
                RefreshSequencePanel();
                RefreshPresetList();
                SetMode(UiMode.Static);
            };
            Closed += (_, _) => _engine.Dispose();
        }

        // ---------------- Custom title bar chrome ----------------

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            }
            else
            {
                DragMove();
            }
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void BtnMaximizeRestore_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        // ---------------- Sidebar mode navigation ----------------

        private Button[] AllTabs => new[] { TabStatic, TabBreathing, TabRainbow, TabBlink, TabHeartbeat, TabCustom };

        private void SidebarTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag && Enum.TryParse<UiMode>(tag, out var mode))
            {
                SetMode(mode);
            }
        }

        private void SetMode(UiMode mode)
        {
            _mode = mode;

            foreach (var tab in AllTabs)
            {
                bool active = tab.Tag as string == mode.ToString();
                tab.Foreground = active ? ActiveTabForeground : InactiveTabForeground;
                tab.BorderBrush = active ? ActiveTabBorder : Brushes.Transparent;
                tab.Background = active ? ActiveTabBackground : Brushes.Transparent;
            }

            QuickColorsSection.Visibility = mode == UiMode.Static ? Visibility.Visible : Visibility.Collapsed;
            SpeedSection.Visibility = mode == UiMode.Static ? Visibility.Collapsed : Visibility.Visible;
            EffectColorSection.Visibility =
                (mode == UiMode.Breathing || mode == UiMode.Blink || mode == UiMode.Heartbeat)
                    ? Visibility.Visible : Visibility.Collapsed;
            CustomSequenceSection.Visibility = mode == UiMode.Custom ? Visibility.Visible : Visibility.Collapsed;

            PropertiesHeader.Text = mode.ToString().ToUpperInvariant();
            ModeLabel.Text = mode.ToString().ToUpperInvariant();

            _engine.StopEffect();
            StatusLabel.Text = "STATUS: IDLE";
        }

        // ---------------- Settings window (hardware / DLL setup) ----------------

        private void MenuSettings_Click(object sender, RoutedEventArgs e)
        {
            var window = new Window
            {
                Title = "KeyPalette Settings",
                Width = 440,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Owner = this,
                Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)),
                Foreground = Brushes.White
            };

            var stack = new StackPanel { Margin = new Thickness(22) };

            stack.Children.Add(new TextBlock
            {
                Text = "HARDWARE",
                FontSize = 13,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 12)
            });

            var hwLabel = new TextBlock
            {
                Text = _lastHwStatus,
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 16)
            };
            stack.Children.Add(hwLabel);
            _settingsHwLabel = hwLabel;

            var locateButton = new Button
            {
                Content = "LOCATE INSYDEDCHU.DLL",
                Style = (Style)FindResource("FlatButton"),
                Margin = new Thickness(0, 0, 0, 8)
            };
            locateButton.Click += (_, _) =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Locate InsydeDCHU.dll",
                    Filter = "InsydeDCHU.dll|InsydeDCHU.dll|All DLL files (*.dll)|*.dll",
                    CheckFileExists = true
                };
                if (dialog.ShowDialog() == true) TryConnect(dialog.FileName);
            };
            stack.Children.Add(locateButton);

            var reconnectButton = new Button { Content = "RECONNECT", Style = (Style)FindResource("FlatButton") };
            reconnectButton.Click += (_, _) => TryConnect();
            stack.Children.Add(reconnectButton);

            window.Content = stack;
            window.Closed += (_, _) => _settingsHwLabel = null;
            window.ShowDialog();
        }

        // ---------------- Hardware connection ----------------

        private void TryConnect(string? explicitDllPath = null)
        {
            bool ok = _engine.Connect(explicitDllPath);
            string msg = ok
                ? $"Connected via {_engine.LoadedFrom}"
                : "Not connected - see Settings to locate InsydeDCHU.dll.";
            _lastHwStatus = msg;
            if (_settingsHwLabel != null) _settingsHwLabel.Text = msg;
            HwStatusFooter.Text = msg;
        }

        private void OnHardwareStatus(string message)
        {
            Dispatcher.Invoke(() =>
            {
                _lastHwStatus = message;
                if (_settingsHwLabel != null) _settingsHwLabel.Text = message;
                HwStatusFooter.Text = message;
            });
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

        // ---------------- Color picking helpers ----------------

        private static (byte r, byte g, byte b)? PickColor(System.Drawing.Color? startColor = null)
        {
            using var dialog = new System.Windows.Forms.ColorDialog
            {
                FullOpen = true,
                AnyColor = true,
                Color = startColor ?? System.Drawing.Color.FromArgb(0x00, 0xE5, 0xFF)
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                return (dialog.Color.R, dialog.Color.G, dialog.Color.B);
            }

            return null;
        }

        private void BtnPickCustomStatic_Click(object sender, RoutedEventArgs e)
        {
            var picked = PickColor();
            if (picked is { } c)
            {
                _engine.StopEffect();
                _engine.SetColor(c.r, c.g, c.b);
                StatusLabel.Text = $"STATUS: STATIC #{c.r:X2}{c.g:X2}{c.b:X2} APPLIED";
            }
        }

        private void BtnPickEffectColor_Click(object sender, RoutedEventArgs e)
        {
            var current = System.Drawing.Color.FromArgb(_engine.BaseColor.r, _engine.BaseColor.g, _engine.BaseColor.b);
            var picked = PickColor(current);
            if (picked is { } c)
            {
                _engine.BaseColor = c;
                EffectColorSwatch.Background = new SolidColorBrush(Color.FromRgb(c.r, c.g, c.b));
                StatusLabel.Text = $"STATUS: EFFECT COLOR SET TO #{c.r:X2}{c.g:X2}{c.b:X2}";
            }
        }

        // ---------------- Custom sequence ----------------

        private void RefreshSequencePanel()
        {
            SequencePanel.Children.Clear();

            for (int i = 0; i < _engine.CustomSequence.Count; i++)
            {
                var (r, g, b) = _engine.CustomSequence[i];
                var box = new Button
                {
                    Width = 40,
                    Height = 40,
                    Margin = new Thickness(0, 0, 8, 8),
                    Background = new SolidColorBrush(Color.FromRgb(r, g, b)),
                    BorderThickness = new Thickness(0),
                    Tag = i,
                    ToolTip = $"#{r:X2}{g:X2}{b:X2} - click to remove"
                };
                box.Click += SequenceBox_Click;
                SequencePanel.Children.Add(box);
            }

            var addButton = new Button
            {
                Width = 40,
                Height = 40,
                Margin = new Thickness(0, 0, 8, 8),
                Content = "+",
                FontSize = 18,
                Style = (Style)FindResource("FlatButton"),
                ToolTip = "Add a color to the sequence"
            };
            addButton.Click += BtnAddSequenceColor_Click;
            SequencePanel.Children.Add(addButton);
        }

        private void BtnAddSequenceColor_Click(object sender, RoutedEventArgs e)
        {
            var picked = PickColor();
            if (picked is { } c)
            {
                _engine.CustomSequence.Add(c);
                RefreshSequencePanel();
            }
        }

        private void SequenceBox_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int index && index >= 0 && index < _engine.CustomSequence.Count)
            {
                _engine.CustomSequence.RemoveAt(index);
                RefreshSequencePanel();
            }
        }

        private void BtnClearSequence_Click(object sender, RoutedEventArgs e)
        {
            _engine.CustomSequence.Clear();
            RefreshSequencePanel();
        }

        // ---------------- Presets ----------------

        private void RefreshPresetList()
        {
            var presets = PresetStore.LoadAll();
            PresetCombo.ItemsSource = null;
            PresetCombo.ItemsSource = presets.Select(p => p.Name).ToList();
            if (PresetCombo.Items.Count > 0) PresetCombo.SelectedIndex = 0;
        }

        private void BtnSavePreset_Click(object sender, RoutedEventArgs e)
        {
            if (_engine.CustomSequence.Count == 0)
            {
                MessageBox.Show("Add at least one color to the sequence before saving a preset.",
                    "Nothing to Save", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string? name = SimplePrompt.ShowInputDialog("Save Preset", "Name this preset:");
            if (string.IsNullOrWhiteSpace(name)) return;

            var presets = PresetStore.LoadAll();
            presets.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            presets.Add(new ColorPreset
            {
                Name = name,
                HexColors = _engine.CustomSequence.Select(c => $"{c.r:X2}{c.g:X2}{c.b:X2}").ToList(),
                SpeedMs = SpeedSlider.Value,
                BrightnessPercent = BrightnessSlider.Value
            });
            PresetStore.SaveAll(presets);
            RefreshPresetList();
            StatusLabel.Text = $"STATUS: PRESET '{name}' SAVED";
        }

        private void BtnLoadPreset_Click(object sender, RoutedEventArgs e)
        {
            if (PresetCombo.SelectedItem is not string name) return;

            var preset = PresetStore.LoadAll().FirstOrDefault(p => p.Name == name);
            if (preset == null) return;

            _engine.CustomSequence.Clear();
            foreach (var hex in preset.HexColors)
            {
                if (hex.Length == 6 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint parsed))
                {
                    _engine.CustomSequence.Add((
                        (byte)((parsed >> 16) & 0xFF),
                        (byte)((parsed >> 8) & 0xFF),
                        (byte)(parsed & 0xFF)));
                }
            }
            RefreshSequencePanel();

            SpeedSlider.Value = preset.SpeedMs;
            BrightnessSlider.Value = preset.BrightnessPercent;

            StatusLabel.Text = $"STATUS: PRESET '{name}' LOADED";
        }

        private void BtnDeletePreset_Click(object sender, RoutedEventArgs e)
        {
            if (PresetCombo.SelectedItem is not string name) return;

            var presets = PresetStore.LoadAll();
            presets.RemoveAll(p => p.Name == name);
            PresetStore.SaveAll(presets);
            RefreshPresetList();
        }

        // ---------------- Effect controls ----------------

        private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _engine.Brightness = BrightnessSlider.Value / 100.0;
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            int speed = (int)SpeedSlider.Value;

            switch (_mode)
            {
                case UiMode.Static:
                    _engine.StopEffect();
                    _engine.SetColor(_engine.BaseColor.r, _engine.BaseColor.g, _engine.BaseColor.b);
                    StatusLabel.Text = "STATUS: STATIC COLOR APPLIED";
                    break;

                case UiMode.Breathing:
                    _engine.StartEffect(EffectType.Breathing, speed);
                    StatusLabel.Text = "STATUS: RUNNING BREATHING";
                    break;

                case UiMode.Rainbow:
                    _engine.StartEffect(EffectType.Rainbow, speed);
                    StatusLabel.Text = "STATUS: RUNNING RAINBOW";
                    break;

                case UiMode.Blink:
                    _engine.StartEffect(EffectType.Blink, speed);
                    StatusLabel.Text = "STATUS: RUNNING BLINK";
                    break;

                case UiMode.Heartbeat:
                    _engine.StartEffect(EffectType.Heartbeat, speed);
                    StatusLabel.Text = "STATUS: RUNNING HEARTBEAT";
                    break;

                case UiMode.Custom:
                    _engine.StartEffect(EffectType.Custom, speed);
                    StatusLabel.Text = "STATUS: RUNNING CUSTOM SEQUENCE";
                    break;
            }
        }

        private void Swatch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string hex &&
                hex.Length == 6 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint parsed))
            {
                byte r = (byte)((parsed >> 16) & 0xFF);
                byte g = (byte)((parsed >> 8) & 0xFF);
                byte b = (byte)(parsed & 0xFF);

                _engine.StopEffect();
                _engine.SetColor(r, g, b);
                StatusLabel.Text = $"STATUS: STATIC #{hex} APPLIED";
            }
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _engine.StopEffect();
            StatusLabel.Text = "STATUS: STOPPED";
        }
    }
}