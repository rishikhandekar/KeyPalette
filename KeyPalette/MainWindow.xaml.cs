using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KeyPalette
{
    public enum UiMode { Static, Presets, Custom }

    public partial class MainWindow : Window
    {
        private readonly HardwareEngine _engine = new();
        private readonly GlobalKeyboardHook _keyboardHook = new();
        private UiMode _mode = UiMode.Static;

        /// <summary>True = showing the effect gallery grid; False = showing the config controls
        /// for whichever effect was picked. Only meaningful while _mode == UiMode.Presets.</summary>
        private bool _presetShowingGallery = true;

        /// <summary>The effect chosen from the gallery ("Breathing", "Rainbow", "Blink",
        /// "Heartbeat", "Fire", "Reactive", "AmbientReactive").</summary>
        private string _selectedPresetEffect = "Breathing";

        private string _lastHwStatus = "Not connected yet.";
        private TextBlock? _settingsHwLabel;

        /// <summary>Guards SleepTimeSlider/SleepTimeTextBox against re-triggering each other
        /// while one of them is programmatically updating the other.</summary>
        private bool _updatingSleepControls;

        private static readonly SolidColorBrush InactiveTabForeground = new(Color.FromRgb(0xAA, 0xAA, 0xAA));
        private static readonly SolidColorBrush ActiveTabForeground = Brushes.White;
        private static readonly SolidColorBrush ActiveTabBorder = new(Color.FromRgb(0xFF, 0x00, 0x3C));
        private static readonly SolidColorBrush ActiveTabBackground = new(Color.FromRgb(0x18, 0x18, 0x18));

        public MainWindow()
        {
            InitializeComponent();

            // The card-preview brushes (BreathingKeyBrush, BlinkKeyBrush, HeartbeatKeyBrush,
            // RainbowKeyBrush) are declared with x:Name inside <Window.Resources>. WPF does NOT
            // automatically add resource-dictionary entries to the owning element's NameScope
            // just because they have x:Name - so Storyboard.TargetName in <Window.Triggers>
            // can't resolve them unless we register them here explicitly.
            RegisterName("BreathingKeyBrush", FindResource("BreathingKeyBrush"));
            RegisterName("BlinkKeyBrush", FindResource("BlinkKeyBrush"));
            RegisterName("HeartbeatKeyBrush", FindResource("HeartbeatKeyBrush"));
            RegisterName("RainbowKeyBrush", FindResource("RainbowKeyBrush"));

            // FireStopMid and FireStopBottom aren't keyed resources themselves - they're the
            // nested, x:Named GradientStops inside FireKeyBrush - so they have to be reached
            // through the brush's GradientStops collection rather than via FindResource.
            var fireBrush = (LinearGradientBrush)FindResource("FireKeyBrush");
            RegisterName("FireKeyBrush", fireBrush);
            RegisterName("FireStopMid", fireBrush.GradientStops[1]);
            RegisterName("FireStopBottom", fireBrush.GradientStops[2]);
            RegisterName("ReactiveKeyBrush", FindResource("ReactiveKeyBrush"));
            RegisterName("AmbientReactiveKeyBrush", FindResource("AmbientReactiveKeyBrush"));

            _engine.StatusChanged += OnHardwareStatus;
            _engine.ColorChanged += OnColorChanged;

            // Reactive needs to see keystrokes even when KeyPalette isn't the focused window, so
            // it listens on a system-wide low-level hook rather than a WPF KeyDown handler. The
            // hook itself just reports vkCodes; HardwareEngine.ReactiveStrike() is a no-op unless
            // Reactive is actually the running effect, so it's safe to leave installed for the
            // whole app lifetime instead of installing/uninstalling per effect switch.
            _keyboardHook.KeyDown += OnGlobalKeyDown;
            try
            {
                _keyboardHook.Install();
            }
            catch (InvalidOperationException ex)
            {
                // Non-fatal: every other effect still works fine without the hook, only
                // Reactive would silently never flash. Surface it via the same status line the
                // hardware connection uses rather than a blocking MessageBox on startup.
                _lastHwStatus = $"Reactive effect unavailable - keyboard hook failed: {ex.Message}";
            }

            Loaded += (_, _) =>
            {
                TryConnect();
                RefreshSequencePanel();
                RefreshPresetList();
                ApplySleepMinutes(0, updateSlider: true);
                SetMode(UiMode.Static);
            };
            Closed += (_, _) =>
            {
                _keyboardHook.Dispose();
                _engine.Dispose();
            };
        }

        /// <summary>
        /// Fired from GlobalKeyboardHook's hook callback thread on every system-wide keystroke.
        /// Offloaded via Task.Run rather than handled inline - the hook callback runs on the UI
        /// thread's message pump, and Windows can silently disable a low-level hook that blocks
        /// its callback for too long, so the actual (blocking, native) hardware write must not
        /// happen synchronously here.
        /// </summary>
        private void OnGlobalKeyDown(int vkCode)
        {
            _ = Task.Run(() => _engine.ReactiveStrike());
        }

        // ---------------- Sharp ComboBox click-to-toggle ----------------

        /// <summary>
        /// SharpCombo's template has no ToggleButton, so opening the dropdown is done manually
        /// here. The critical part: while the dropdown is open, WPF still routes the initial
        /// mouse-down for a click on a popup item THROUGH the ComboBox first (it holds mouse
        /// capture while open, to support click-outside-to-close). If we unconditionally toggled
        /// IsDropDownOpen and marked the event Handled on every click, that would close the
        /// dropdown and swallow the event before the ComboBoxItem underneath ever got a chance
        /// to register the click as a selection - which was exactly the "selection never
        /// updates" bug. So this only opens the dropdown when the click did NOT land on an item
        /// inside the already-open popup; clicks on items are left completely alone.
        /// </summary>
        private void Combo_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ComboBox comboBox) return;

            if (comboBox.IsDropDownOpen && IsClickOnComboBoxItem(e.OriginalSource as DependencyObject))
            {
                // Let the click reach the ComboBoxItem normally - don't touch IsDropDownOpen,
                // don't mark Handled.
                return;
            }

            comboBox.IsDropDownOpen = !comboBox.IsDropDownOpen;
            e.Handled = true;
        }

        /// <summary>
        /// Walks up from the click's original source looking for a ComboBoxItem ancestor.
        /// Note: this deliberately does NOT look for a Popup ancestor. A Popup's content sits
        /// under a separate PopupRoot (its own visual tree layer, backed by a different HWND),
        /// so VisualTreeHelper.GetParent hits a dead end at PopupRoot and never reaches the
        /// Popup control itself - that was the bug in the previous version. A ComboBoxItem,
        /// however, is fully contained WITHIN that same popup-content visual tree (it's just a
        /// couple of hops up from whatever was actually clicked - a TextBlock, a Border, etc.),
        /// so VisualTreeHelper reaches it without ever needing to cross that boundary.
        /// </summary>
        private static bool IsClickOnComboBoxItem(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is ComboBoxItem) return true;

                // VisualTreeHelper only walks Visual/Visual3D nodes; fall back to the logical
                // tree for anything else (defensive - in practice everything clickable here is
                // a Visual, but this keeps the walk from throwing if that's ever not true).
                source = source is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(source)
                    : LogicalTreeHelper.GetParent(source);
            }
            return false;
        }

        // ---------------- Sidebar mode navigation (3 tabs) ----------------

        private Button[] AllTabs => new[] { TabStatic, TabPresets, TabCustom };

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

            if (mode == UiMode.Presets)
            {
                _presetShowingGallery = true; // always land on the gallery when entering Presets
            }

            foreach (var tab in AllTabs)
            {
                bool active = tab.Tag as string == mode.ToString();
                tab.Foreground = active ? ActiveTabForeground : InactiveTabForeground;
                tab.BorderBrush = active ? ActiveTabBorder : Brushes.Transparent;
                tab.Background = active ? ActiveTabBackground : Brushes.Transparent;
            }

            RefreshPresetSubState();

            _engine.StopEffect();
            StatusLabel.Text = "STATUS: IDLE";
        }

        /// <summary>
        /// Recomputes every section's visibility from (_mode, _presetShowingGallery). Called
        /// whenever the mode changes, a gallery card is clicked, or Back is clicked.
        /// </summary>
        private void RefreshPresetSubState()
        {
            bool isPresetsGallery = _mode == UiMode.Presets && _presetShowingGallery;
            bool isPresetsConfig = _mode == UiMode.Presets && !_presetShowingGallery;

            // Center panel: gallery grid replaces the visualizer only during Presets gallery state.
            VisualizerHost.Visibility = isPresetsGallery ? Visibility.Collapsed : Visibility.Visible;
            EffectGalleryPanel.Visibility = isPresetsGallery ? Visibility.Visible : Visibility.Collapsed;

            // Right panel sections.
            QuickColorsSection.Visibility = _mode == UiMode.Static ? Visibility.Visible : Visibility.Collapsed;
            PresetsPlaceholderSection.Visibility = isPresetsGallery ? Visibility.Visible : Visibility.Collapsed;
            BackToEffectsButton.Visibility = isPresetsConfig ? Visibility.Visible : Visibility.Collapsed;
            // Speed drives a continuous cycle (breathing/blink/rainbow/fire/etc.) that the
            // Reactive-family effects don't have - they fire from keystrokes, not a timer - so
            // hide it there.
            bool speedApplies = _mode == UiMode.Custom || (isPresetsConfig && _selectedPresetEffect is not ("Reactive" or "AmbientReactive"));
            SpeedSection.Visibility = speedApplies ? Visibility.Visible : Visibility.Collapsed;
            CustomSequenceSection.Visibility = _mode == UiMode.Custom ? Visibility.Visible : Visibility.Collapsed;
            BrightnessRow.Visibility = (_mode == UiMode.Static || _mode == UiMode.Custom || isPresetsConfig)
                ? Visibility.Visible : Visibility.Collapsed;

            // Nothing to Apply/Stop while just browsing the gallery.
            ApplyStopBar.Visibility = isPresetsGallery ? Visibility.Collapsed : Visibility.Visible;

            UpdateEffectColorVisibility();

            PropertiesHeader.Text = isPresetsGallery ? "PRESETS" : _mode.ToString().ToUpperInvariant();
            UpdateModeLabel();
        }

        private void EffectCard_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string effectName)
            {
                _selectedPresetEffect = effectName;
                _presetShowingGallery = false;
                RefreshPresetSubState();
            }
        }

        private void BtnBackToEffects_Click(object sender, RoutedEventArgs e)
        {
            _engine.StopEffect();
            _presetShowingGallery = true;
            RefreshPresetSubState();
        }

        /// <summary>Effect Color only makes sense once an effect is actually picked (Presets
        /// config state), and for the four effects that use a single flash/pulse color
        /// (Rainbow sweeps every hue and Fire always randomizes its own ember shades, so both
        /// are excluded). Those same four effects also get the Multi Colors checkbox, and hide
        /// the swatch itself while that's checked since a fixed color is meaningless then.</summary>
        private void UpdateEffectColorVisibility()
        {
            if (EffectColorSection == null) return;

            bool isPresetsConfig = _mode == UiMode.Presets && !_presetShowingGallery;
            bool effectHasColor = _selectedPresetEffect is "Breathing" or "Blink" or "Heartbeat" or "Reactive";
            EffectColorSection.Visibility = isPresetsConfig && effectHasColor ? Visibility.Visible : Visibility.Collapsed;

            MultiColorsCheckBox.Visibility = effectHasColor ? Visibility.Visible : Visibility.Collapsed;

            bool hideSwatchForMultiColor = effectHasColor && MultiColorsCheckBox.IsChecked == true;
            EffectColorSwatch.Visibility = hideSwatchForMultiColor ? Visibility.Collapsed : Visibility.Visible;
        }

        private void MultiColorsCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            UpdateEffectColorVisibility();
        }

        private void UpdateModeLabel()
        {
            if (ModeLabel == null) return;

            if (_mode == UiMode.Presets && !_presetShowingGallery)
            {
                ModeLabel.Text = $"PRESETS: {_selectedPresetEffect.ToUpperInvariant()}";
            }
            else
            {
                ModeLabel.Text = _mode.ToString().ToUpperInvariant();
            }
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

        private (byte r, byte g, byte b)? PickColor((byte r, byte g, byte b)? startColor = null)
        {
            var picker = new ColorPickerWindow(startColor ?? (0x00, 0xE5, 0xFF))
            {
                Owner = this
            };

            return picker.ShowDialog() == true ? picker.SelectedColor : null;
        }

        private void BtnPickCustomStatic_Click(object sender, RoutedEventArgs e)
        {
            var picked = PickColor(_engine.CurrentColor);
            if (picked is { } c)
            {
                _engine.StopEffect();
                _engine.SetColor(c.r, c.g, c.b);
                StatusLabel.Text = $"STATUS: STATIC #{c.r:X2}{c.g:X2}{c.b:X2} APPLIED";
            }
        }

        private void BtnPickEffectColor_Click(object sender, RoutedEventArgs e)
        {
            var picked = PickColor(_engine.BaseColor);
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
            // A logical starting point: whatever color was added last (so building up a
            // sequence of similar shades doesn't mean re-navigating the picker from cyan every
            // time), falling back to the current effect's base color if the sequence is empty.
            var startColor = _engine.CustomSequence.Count > 0
                ? _engine.CustomSequence[^1]
                : _engine.BaseColor;

            var picked = PickColor(startColor);
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

        // ---------------- Saved custom presets ----------------

        private void RefreshPresetList()
        {
            var presets = PresetStore.LoadAll();
            SavedPresetCombo.ItemsSource = null;
            SavedPresetCombo.ItemsSource = presets.Select(p => p.Name).ToList();
            if (SavedPresetCombo.Items.Count > 0) SavedPresetCombo.SelectedIndex = 0;
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
            if (SavedPresetCombo.SelectedItem is not string name) return;

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
            if (SavedPresetCombo.SelectedItem is not string name) return;

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

        // ---------------- Sleep Time (Slider <-> TextBox, both drive _engine.SleepMinutes) ----------------

        private void SleepTimeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingSleepControls) return;
            ApplySleepMinutes((int)Math.Round(SleepTimeSlider.Value), updateSlider: false);
        }

        private void SleepTimeTextBox_Committed(object sender, RoutedEventArgs e) => CommitSleepTimeText();

        private void SleepTimeTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            CommitSleepTimeText();
            Keyboard.ClearFocus(); // triggers LostFocus too, but this makes Enter feel immediate
        }

        /// <summary>Parses whatever's currently typed in the box and applies it. Committing on
        /// Enter/LostFocus (rather than every keystroke in TextChanged) means a half-typed value
        /// like "" or "6" while backspacing to type "60" is never fought over mid-edit.</summary>
        private void CommitSleepTimeText()
        {
            if (int.TryParse(SleepTimeTextBox.Text.Trim(), out int minutes))
            {
                ApplySleepMinutes(Math.Clamp(minutes, 0, 60), updateSlider: true);
            }
            else
            {
                // Not a valid number - just redisplay whatever the setting actually still is,
                // rather than leaving garbled text sitting in the box.
                ApplySleepMinutes((int)Math.Round(SleepTimeSlider.Value), updateSlider: true);
            }
        }

        /// <summary>Single source of truth for pushing a Sleep Time value out to the slider, the
        /// text box, and the engine together, so the three can never drift out of sync.</summary>
        private void ApplySleepMinutes(int minutes, bool updateSlider)
        {
            _updatingSleepControls = true;
            try
            {
                if (updateSlider) SleepTimeSlider.Value = minutes;
                SleepTimeTextBox.Text = minutes == 0 ? "Never Off" : minutes.ToString();
                _engine.SleepMinutes = minutes;
            }
            finally
            {
                _updatingSleepControls = false;
            }
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

                case UiMode.Presets:
                    {
                        EffectType effect = _selectedPresetEffect switch
                        {
                            "Breathing" => EffectType.Breathing,
                            "Rainbow" => EffectType.Rainbow,
                            "Blink" => EffectType.Blink,
                            "Heartbeat" => EffectType.Heartbeat,
                            "Fire" => EffectType.Fire,
                            "Reactive" => EffectType.Reactive,
                            "AmbientReactive" => EffectType.AmbientReactive,
                            _ => EffectType.Breathing
                        };
                        _engine.UseMultiColorMode = MultiColorsCheckBox.IsChecked == true;
                        _engine.StartEffect(effect, speed);
                        StatusLabel.Text = $"STATUS: RUNNING {_selectedPresetEffect.ToUpperInvariant()}";
                        break;
                    }

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