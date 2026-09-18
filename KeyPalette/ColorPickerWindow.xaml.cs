using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KeyPalette
{
    /// <summary>
    /// Self-contained replacement for System.Windows.Forms.ColorDialog, styled to match
    /// KeyPalette's own dark/sharp look instead of the OS-native picker. Centerpiece is a
    /// circular hue/saturation wheel rendered once into a WriteableBitmap (BuildWheelBitmap);
    /// brightness is a separate vertical slider layered next to it rather than baked into the
    /// wheel itself - the wheel always shows fully-saturated colors at full value, and the
    /// slider darkens the final result, same split most native wheel pickers use.
    /// </summary>
    public partial class ColorPickerWindow : Window
    {
        private const int WheelSize = 240;
        private const double WheelRadius = WheelSize / 2.0;       // outer radius of the ring (~120px)
        private const double WheelInnerRadius = 70;                 // inner radius - hollow center starts here
        private const double WheelCenterlineRadius = (WheelRadius + WheelInnerRadius) / 2.0; // ~95px - where the thumb always sits
        private const double ThumbRadius = 8;

        private double _hue;        // 0-360
        private double _saturation; // 0-1. Locked to 1.0 by every ring interaction (see
                                     // UpdateHueSaturationFromPoint) - can still end up lower than
                                     // 1.0 if the user types a desaturated RGB/hex value directly
                                     // into the text boxes, since that path intentionally isn't
                                     // restricted (see SetFromRgb).
        private double _value;      // 0-1 (brightness)

        // Guards RefreshAllFromHsv's own writes to the Slider/TextBoxes from re-triggering their
        // ValueChanged/TextChanged-driven handlers - without this, programmatically setting
        // RTextBox.Text from a wheel drag would immediately fire RgbTextBox_Committed... except
        // that's LostFocus-driven, so the real danger is BrightnessSlider.Value, whose
        // ValueChanged fires on every programmatic set too.
        private bool _suppressEvents;

        private bool _isDraggingWheel;

        /// <summary>The color the user had dialed in when they clicked APPLY. Null if they
        /// cancelled - callers should treat that exactly like the old ColorDialog's
        /// DialogResult != OK, i.e. leave whatever color was already set alone.</summary>
        public (byte r, byte g, byte b)? SelectedColor { get; private set; }

        public ColorPickerWindow((byte r, byte g, byte b) startColor)
        {
            // InitializeComponent() runs the XAML parser, which assigns BrightnessSlider's
            // Minimum/Maximum/Value in document order - setting Value="100" fires ValueChanged
            // immediately, which calls RefreshAllFromHsv. At that point InitializeComponent()
            // hasn't returned yet, so later-declared fields like PreviewSwatch are still null,
            // and RefreshAllFromHsv's very first line (PreviewSwatch.Background = ...) throws a
            // NullReferenceException. Suppressing events for the duration of InitializeComponent()
            // means that spurious ValueChanged is a no-op; the real, fully-populated refresh
            // still happens explicitly right after, once every named element actually exists.
            _suppressEvents = true;
            InitializeComponent();
            _suppressEvents = false;

            BuildWheelBitmap();

            var (h, s, v) = RgbToHsv(startColor.r, startColor.g, startColor.b);
            _hue = h;
            _saturation = s;
            _value = v;

            RefreshAllFromHsv(updateWheelThumb: true, updateBrightnessSlider: true, updateTextBoxes: true);
        }

        // ---------------- Wheel rendering ----------------

        /// <summary>
        /// Painted once - the wheel's own pixels never change after this (only the thumb
        /// position and the brightness slider move). This is a HOLLOW RING, not a solid disc:
        /// for every pixel between WheelInnerRadius and WheelRadius, the angle from center gives
        /// hue (0-360) - saturation is locked to 1.0 for every one of those pixels regardless of
        /// how far from center they sit, so there are no pastel/washed-out colors anywhere on
        /// the ring (unlike a solid saturation-by-distance wheel, where dragging toward the
        /// center produces near-white colors that read poorly on a single-zone RGB backlight).
        /// Value is fixed at 1.0 here too; brightness is the separate slider, applied afterward.
        /// Pixels outside the ring (either past the outer edge, or inside the hollow center) are
        /// left fully transparent.
        /// </summary>
        private void BuildWheelBitmap()
        {
            var bitmap = new WriteableBitmap(WheelSize, WheelSize, 96, 96, PixelFormats.Bgra32, null);
            int stride = WheelSize * 4;
            byte[] pixels = new byte[stride * WheelSize];

            for (int y = 0; y < WheelSize; y++)
            {
                double dy = y + 0.5 - WheelRadius;
                for (int x = 0; x < WheelSize; x++)
                {
                    double dx = x + 0.5 - WheelRadius;
                    double r = Math.Sqrt(dx * dx + dy * dy);
                    int idx = y * stride + x * 4;

                    if (r >= WheelInnerRadius && r <= WheelRadius)
                    {
                        double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                        if (angle < 0) angle += 360.0;

                        // Saturation and value both fixed - only hue (angle) varies across the ring.
                        var (rr, gg, bb) = HsvToRgb(angle, 1.0, 1.0);

                        // WriteableBitmap with Bgra32 wants byte order Blue, Green, Red, Alpha.
                        pixels[idx + 0] = bb;
                        pixels[idx + 1] = gg;
                        pixels[idx + 2] = rr;
                        pixels[idx + 3] = 255;
                    }
                    else
                    {
                        pixels[idx + 3] = 0; // transparent: outside the outer edge, or inside the hollow center
                    }
                }
            }

            bitmap.WritePixels(new Int32Rect(0, 0, WheelSize, WheelSize), pixels, stride, 0);
            WheelImage.Source = bitmap;
        }

        // ---------------- Wheel drag handling ----------------

        private void WheelCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingWheel = true;
            WheelCanvas.CaptureMouse();
            UpdateHueSaturationFromPoint(e.GetPosition(WheelCanvas));
        }

        private void WheelCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingWheel) return;
            UpdateHueSaturationFromPoint(e.GetPosition(WheelCanvas));
        }

        private void WheelCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingWheel = false;
            WheelCanvas.ReleaseMouseCapture();
        }

        /// <summary>Converts a click/drag point (in WheelCanvas coordinates) into a hue angle.
        /// Saturation is no longer derived from the click's distance from center - it's locked
        /// to 1.0 everywhere on the ring (see BuildWheelBitmap) - so only the angle matters here,
        /// and the thumb always snaps to the ring's centerline regardless of how far in or out
        /// the actual click/drag point was.</summary>
        private void UpdateHueSaturationFromPoint(Point p)
        {
            double dx = p.X - WheelRadius;
            double dy = p.Y - WheelRadius;

            double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            if (angle < 0) angle += 360.0;

            _hue = angle;
            _saturation = 1.0; // locked - see BuildWheelBitmap

            double clampedX = WheelRadius + WheelCenterlineRadius * Math.Cos(angle * Math.PI / 180.0);
            double clampedY = WheelRadius + WheelCenterlineRadius * Math.Sin(angle * Math.PI / 180.0);
            Canvas.SetLeft(WheelThumb, clampedX - ThumbRadius);
            Canvas.SetTop(WheelThumb, clampedY - ThumbRadius);

            RefreshAllFromHsv(updateWheelThumb: false, updateBrightnessSlider: false, updateTextBoxes: true);
        }

        /// <summary>Repositions the thumb from _hue - used whenever the color changed via
        /// something other than dragging the ring itself (typing hex/RGB, or the initial
        /// startColor), since those don't already have a screen point to work from. Always
        /// places the thumb on the ring's centerline at that hue's angle; saturation isn't a
        /// factor since it's locked to 1.0 everywhere on the ring.</summary>
        private void UpdateThumbPositionFromHsv()
        {
            double angleRad = _hue * Math.PI / 180.0;
            double x = WheelRadius + WheelCenterlineRadius * Math.Cos(angleRad);
            double y = WheelRadius + WheelCenterlineRadius * Math.Sin(angleRad);
            Canvas.SetLeft(WheelThumb, x - ThumbRadius);
            Canvas.SetTop(WheelThumb, y - ThumbRadius);
        }

        // ---------------- Brightness slider ----------------

        private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            _value = BrightnessSlider.Value / 100.0;
            RefreshAllFromHsv(updateWheelThumb: false, updateBrightnessSlider: false, updateTextBoxes: true);
        }

        // ---------------- Hex text box ----------------

        private void HexTextBox_Committed(object sender, RoutedEventArgs e) => CommitHexText();

        private void HexTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            CommitHexText();
            Keyboard.ClearFocus();
        }

        /// <summary>Committed on Enter/LostFocus rather than every keystroke, so a half-typed
        /// value while backspacing to retype isn't fought over mid-edit.</summary>
        private void CommitHexText()
        {
            string hex = HexTextBox.Text.Trim().TrimStart('#');
            if (hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed))
            {
                byte r = (byte)((parsed >> 16) & 0xFF);
                byte g = (byte)((parsed >> 8) & 0xFF);
                byte b = (byte)(parsed & 0xFF);
                SetFromRgb(r, g, b);
            }
            else
            {
                // Not valid hex - redisplay whatever the color actually still is, rather than
                // leaving garbled text sitting in the box.
                RefreshAllFromHsv(updateWheelThumb: false, updateBrightnessSlider: false, updateTextBoxes: true);
            }
        }

        // ---------------- R / G / B text boxes ----------------

        private void RgbTextBox_Committed(object sender, RoutedEventArgs e) => CommitRgbText();

        private void RgbTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            CommitRgbText();
            Keyboard.ClearFocus();
        }

        private void CommitRgbText()
        {
            if (TryParseByte(RTextBox.Text, out byte r) &&
                TryParseByte(GTextBox.Text, out byte g) &&
                TryParseByte(BTextBox.Text, out byte b))
            {
                SetFromRgb(r, g, b);
            }
            else
            {
                RefreshAllFromHsv(updateWheelThumb: false, updateBrightnessSlider: false, updateTextBoxes: true);
            }
        }

        private static bool TryParseByte(string text, out byte value)
        {
            if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 0 && n <= 255)
            {
                value = (byte)n;
                return true;
            }
            value = 0;
            return false;
        }

        private void SetFromRgb(byte r, byte g, byte b)
        {
            var (h, s, v) = RgbToHsv(r, g, b);
            _hue = h;
            _saturation = s;
            _value = v;

            // Came from the text boxes rather than the wheel/slider, so both of those need to
            // catch up this time.
            RefreshAllFromHsv(updateWheelThumb: true, updateBrightnessSlider: true, updateTextBoxes: true);
        }

        // ---------------- Shared refresh ----------------

        /// <summary>
        /// Single source of truth for pushing the current (_hue, _saturation, _value) out to
        /// whichever controls didn't already know about the change. The preview swatch always
        /// updates; everything else is opt-in per caller so the control the user is actually
        /// mid-interaction with (dragging the wheel, typing a text box) is never fought over by
        /// this same method re-writing it out from under them.
        /// </summary>
        private void RefreshAllFromHsv(bool updateWheelThumb, bool updateBrightnessSlider, bool updateTextBoxes)
        {
            var (r, g, b) = HsvToRgb(_hue, _saturation, _value);

            PreviewSwatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));

            if (updateWheelThumb) UpdateThumbPositionFromHsv();

            _suppressEvents = true;
            try
            {
                if (updateBrightnessSlider) BrightnessSlider.Value = _value * 100.0;

                if (updateTextBoxes)
                {
                    HexTextBox.Text = $"{r:X2}{g:X2}{b:X2}";
                    RTextBox.Text = r.ToString(CultureInfo.InvariantCulture);
                    GTextBox.Text = g.ToString(CultureInfo.InvariantCulture);
                    BTextBox.Text = b.ToString(CultureInfo.InvariantCulture);
                }
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        // ---------------- Buttons ----------------

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            var (r, g, b) = HsvToRgb(_hue, _saturation, _value);
            SelectedColor = (r, g, b);
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        // ---------------- HSV <-> RGB ----------------
        // Same math HardwareEngine uses internally for its own effects - duplicated here rather
        // than shared, since HardwareEngine's versions are private to that class.

        private static (byte r, byte g, byte b) HsvToRgb(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;

            double r1, g1, b1;
            if (h < 60) { r1 = c; g1 = x; b1 = 0; }
            else if (h < 120) { r1 = x; g1 = c; b1 = 0; }
            else if (h < 180) { r1 = 0; g1 = c; b1 = x; }
            else if (h < 240) { r1 = 0; g1 = x; b1 = c; }
            else if (h < 300) { r1 = x; g1 = 0; b1 = c; }
            else { r1 = c; g1 = 0; b1 = x; }

            return (
                (byte)Math.Round((r1 + m) * 255),
                (byte)Math.Round((g1 + m) * 255),
                (byte)Math.Round((b1 + m) * 255)
            );
        }

        private static (double h, double s, double v) RgbToHsv(byte r, byte g, byte b)
        {
            double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
            double max = Math.Max(rd, Math.Max(gd, bd));
            double min = Math.Min(rd, Math.Min(gd, bd));
            double delta = max - min;

            double h;
            if (delta < 1e-9) h = 0;
            else if (max == rd) h = 60 * (((gd - bd) / delta) % 6);
            else if (max == gd) h = 60 * (((bd - rd) / delta) + 2);
            else h = 60 * (((rd - gd) / delta) + 4);
            if (h < 0) h += 360;

            double s = max < 1e-9 ? 0 : delta / max;
            double vv = max;

            return (h, s, vv);
        }
    }
}
