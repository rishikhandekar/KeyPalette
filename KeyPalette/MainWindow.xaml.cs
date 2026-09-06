using System;
using System.Linq;
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

            Loaded += (_, _) =>
            {
                TryConnect();
                RefreshSequencePanel();
                RefreshPresetList();
            };
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

        // ---------------- Color picking helpers ----------------

        /// <summary>Opens the native Windows color picker. Returns null if the user cancelled.</summary>
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
                StatusLabel.Text = $"Status: Static #{c.r:X2}{c.g:X2}{c.b:X2} Applied";
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
                StatusLabel.Text = $"Status: Effect color set to #{c.r:X2}{c.g:X2}{c.b:X2}";
            }
        }

        // ---------------- Custom sequence (click + to add, click a swatch to remove) ----------------

        private void RefreshSequencePanel()
        {
            SequencePanel.Children.Clear();

            for (int i = 0; i < _engine.CustomSequence.Count; i++)
            {
                var (r, g, b) = _engine.CustomSequence[i];
                var box = new Button
                {
                    Width = 36,
                    Height = 36,
                    Margin = new Thickness(4),
                    Background = new SolidColorBrush(Color.FromRgb(r, g, b)),
                    Tag = i,
                    ToolTip = $"#{r:X2}{g:X2}{b:X2} - click to remove"
                };
                box.Click += SequenceBox_Click;
                SequencePanel.Children.Add(box);
            }

            var addButton = new Button
            {
                Width = 36,
                Height = 36,
                Margin = new Thickness(4),
                Content = "+",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
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
                MessageBox.Show("Add at least one color to the Custom Sequence before saving a preset.",
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
            StatusLabel.Text = $"Status: Preset '{name}' saved";
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

            EffectSelector.SelectedIndex = 4; // "Custom Sequence"
            StatusLabel.Text = $"Status: Preset '{name}' loaded";
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
            string selected = ((ComboBoxItem)EffectSelector.SelectedItem).Content.ToString() ?? "";

            EffectType effect = selected switch
            {
                var s when s.Contains("Breathing") => EffectType.Breathing,
                var s when s.Contains("Rainbow") => EffectType.Rainbow,
                var s when s.Contains("Blink") => EffectType.Blink,
                var s when s.Contains("Heartbeat") => EffectType.Heartbeat,
                var s when s.Contains("Custom") => EffectType.Custom,
                _ => EffectType.Static
            };

            if (effect == EffectType.Static)
            {
                _engine.SetColor(_engine.BaseColor.r, _engine.BaseColor.g, _engine.BaseColor.b);
            }
            else
            {
                _engine.StartEffect(effect, speed);
            }

            StatusLabel.Text = $"Status: Running {selected.Split('/')[0].Trim()}";
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
                StatusLabel.Text = $"Status: Static #{hex} Applied";
            }
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _engine.StopEffect();
            StatusLabel.Text = "Status: Stopped";
        }
    }
}