using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace KeyPalette
{
    public enum EffectType
    {
        Static,
        Breathing,
        Rainbow,
        Blink,
        Heartbeat,
        Fire,
        Reactive,
        AmbientReactive,
        Custom
    }

    /// <summary>
    /// KeyPalette talks to the embedded controller the same way the reference
    /// "CLEVO_KeyboardColour" tool does for single-zone Insyde-based Acer/Clevo
    /// boards (device id family 0x0000A5xx, which includes the Aspire 7 A715
    /// series): by loading the vendor's own InsydeDCHU.dll and calling its two
    /// exported functions directly - no WMI is involved for this hardware family.
    ///
    /// InsydeDCHU.dll is not something we ship - it's the same DLL Acer's own
    /// keyboard/control-center software already uses on your machine. If it
    /// isn't found automatically, use "Locate InsydeDCHU.dll" to point at it.
    /// </summary>
    public sealed class HardwareEngine : IDisposable
    {
        // --- Native DLL loading plumbing ---

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string lpLibFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr hModule);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint SetDCHUDataDelegate(uint command, byte[] buffer, uint length);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint WriteAppSettingsDelegate(uint page, uint offset, uint length, byte[] buffer);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint ReadAppSettingsDelegate(uint page, uint offset, uint length, byte[] buffer);

        public event Action<string>? StatusChanged;
        public event Action<byte, byte, byte>? ColorChanged;

        private IntPtr _hDll = IntPtr.Zero;
        private SetDCHUDataDelegate? _setDchuData;
        private WriteAppSettingsDelegate? _writeAppSettings;
        private ReadAppSettingsDelegate? _readAppSettings;
        private CancellationTokenSource? _animCts;
        private Task? _animTask;
        private readonly object _sync = new();

        public bool IsConnected => _hDll != IntPtr.Zero && _setDchuData != null && _writeAppSettings != null;
        public string? LoadedFrom { get; private set; }

        /// <summary>The last color actually applied - whether from SetColor, an effect tick, or
        /// a hardware read-back on Connect(). Unlike BaseColor (which only Breathing/Blink/
        /// Heartbeat's pulse color reflects), this always matches whatever the physical
        /// keyboard is genuinely showing right now, which is what anything re-opening the
        /// color picker on an existing color - like a Quick Swatch pick, which never touches
        /// BaseColor - should actually start from.</summary>
        public (byte r, byte g, byte b) CurrentColor => _lastRawColor;

        /// <summary>
        /// The color used by Breathing, Blink, and Heartbeat (they pulse this one color's
        /// brightness). Defaults to cyan. Set this from the UI's color picker before
        /// starting one of those effects.
        /// </summary>
        public (byte r, byte g, byte b) BaseColor { get; set; } = (0x00, 0xE5, 0xFF);

        /// <summary>
        /// User-defined color stops for the "Custom" effect. The animation loop smoothly
        /// cycles through these in order, wrapping back to the first. Needs at least 2
        /// colors to animate; with 0 or 1 it just holds a single color.
        /// </summary>
        public List<(byte r, byte g, byte b)> CustomSequence { get; } = new();

        /// <summary>0.0 - 1.0 global brightness multiplier applied to every SetColor call.</summary>
        public double Brightness { get; set; } = 1.0;

        /// <summary>
        /// Multi Color mode: when true, Reactive, Breathing, Blink, and Heartbeat all cycle
        /// through <see cref="MultiColorPalette"/> instead of using the fixed
        /// <see cref="BaseColor"/>. Read fresh on each strike/frame, so it can be toggled live
        /// while an effect is already running.
        /// </summary>
        public bool UseMultiColorMode { get; set; }

        /// <summary>
        /// Custom color sequence used by Multi Color mode: Red, Orange, Yellow, Green,
        /// Light Blue, Dark Blue, Purple, Pink.
        /// </summary>
        private static readonly (byte r, byte g, byte b)[] MultiColorPalette =
        {
            (0xFF, 0x00, 0x00), // Red
            (0xFF, 0x53, 0x00), // Orange
            (0xFF, 0xFF, 0x00), // Yellow
            (0x00, 0xFF, 0x00), // Green
            (0x00, 0xE5, 0xFF), // Light Blue
            (0x00, 0x00, 0x8B), // Dark Blue
            (0x33, 0x00, 0xFF), // Purple
            (0xFE, 0x01, 0xC5), // Pink
        };

        /// <summary>Cursor into <see cref="MultiColorPalette"/>, shared by Reactive strikes and
        /// the Breathing/Blink/Heartbeat pulse loop so Multi Color mode always advances
        /// sequentially through the palette regardless of which effect is driving it.</summary>
        private int _multiColorIndex;

        /// <summary>Returns the color at the current palette cursor, then advances the cursor
        /// (wrapping back to 0 at the end of the palette).</summary>
        private (byte r, byte g, byte b) NextMultiColor()
        {
            var color = MultiColorPalette[_multiColorIndex];
            _multiColorIndex = (_multiColorIndex + 1) % MultiColorPalette.Length;
            return color;
        }

        private volatile bool _reactiveActive;
        private CancellationTokenSource? _reactiveFadeCts;

        /// <summary>True while the currently-armed Reactive-family effect is AmbientReactive
        /// rather than plain Reactive - changes what ReactiveStrike() fades back down to.</summary>
        private bool _reactiveAmbient;

        /// <summary>Rest brightness (as a fraction of full) that Ambient Reactive idles at
        /// between keystrokes. 20-30% is enough to see the keyboard without it looking "on".</summary>
        private const double AmbientReactiveDimLevel = 0.25;

        // --- Sleep Time idle monitor ---

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [DllImport("kernel32.dll")]
        private static extern uint GetTickCount();

        /// <summary>
        /// Minutes of true system idle time (mouse OR keyboard, anywhere on the machine - not
        /// just KeyPalette) after which the backlight fades to off. 0 = never sleep. Checked
        /// continuously by a background loop started for the lifetime of this engine, so it
        /// applies no matter which effect (or none) is currently running.
        /// </summary>
        public double SleepMinutes { get; set; }

        /// <summary>Last color actually requested via SetColor, BEFORE the Brightness/sleep
        /// multipliers are applied. The sleep monitor re-sends this at a shrinking multiplier to
        /// fade to black, and re-sends it once more at full multiplier to wake instantly -
        /// without needing to know or care which effect produced it.</summary>
        private (byte r, byte g, byte b) _lastRawColor = (0, 0, 0);

        /// <summary>0.0-1.0 multiplier layered on top of the user's own Brightness setting,
        /// driven purely by the sleep monitor. 1.0 = awake/no effect; ramps to 0.0 while
        /// fading asleep.</summary>
        private double _sleepMultiplier = 1.0;

        private volatile bool _isAsleep;
        private readonly CancellationTokenSource _sleepCts = new();
        private readonly Task _sleepMonitorTask;

        public HardwareEngine()
        {
            _sleepMonitorTask = Task.Run(() => SleepMonitorLoop(_sleepCts.Token));
        }

        /// <summary>
        /// Tries the app folder and the common Acer install locations first.
        /// Pass an explicit path (from a file picker) to try that first instead.
        /// </summary>
        public bool Connect(string? explicitDllPath = null)
        {
            Disconnect();

            foreach (var path in BuildCandidatePaths(explicitDllPath))
            {
                if (!File.Exists(path)) continue;

                var handle = LoadLibraryW(path);
                if (handle == IntPtr.Zero) continue;

                IntPtr pSet = GetProcAddress(handle, "SetDCHU_Data");
                IntPtr pWrite = GetProcAddress(handle, "WriteAppSettings");

                if (pSet == IntPtr.Zero || pWrite == IntPtr.Zero)
                {
                    FreeLibrary(handle);
                    continue;
                }

                _hDll = handle;
                _setDchuData = Marshal.GetDelegateForFunctionPointer<SetDCHUDataDelegate>(pSet);
                _writeAppSettings = Marshal.GetDelegateForFunctionPointer<WriteAppSettingsDelegate>(pWrite);

                // ReadAppSettings is treated as optional rather than a connection requirement:
                // some DLL builds may not export it, and losing the ability to read the current
                // hardware color back is a much smaller regression than refusing to connect at
                // all over it. If it's missing, TryReadCurrentColorFromHardware below just quietly
                // does nothing.
                IntPtr pRead = GetProcAddress(handle, "ReadAppSettings");
                _readAppSettings = pRead != IntPtr.Zero
                    ? Marshal.GetDelegateForFunctionPointer<ReadAppSettingsDelegate>(pRead)
                    : null;

                LoadedFrom = path;
                StatusChanged?.Invoke($"Connected via {path}");

                TryReadCurrentColorFromHardware();

                return true;
            }

            StatusChanged?.Invoke(
                "InsydeDCHU.dll not found automatically. Click 'Locate InsydeDCHU.dll' and browse to it - " +
                "it's the same DLL your Acer keyboard/control-center software already has installed.");
            return false;
        }

        /// <summary>
        /// Reads back whatever color the embedded controller is actually currently showing
        /// (page 2, offset 0x51, 3 bytes - the same page/offset SetColor writes R/G/B to) and
        /// pushes it out as though it had just been applied by this app: updates _lastRawColor
        /// and raises ColorChanged. Called once right after a successful Connect() so the UI's
        /// preview glow and CurrentColor start out matching the physical keyboard instead of
        /// defaulting to whatever this session's in-memory state happened to be (which, on a
        /// freshly launched app, is nothing at all).
        /// </summary>
        private void TryReadCurrentColorFromHardware()
        {
            if (_readAppSettings == null) return;

            try
            {
                byte[] buffer = new byte[3];
                _readAppSettings(2, 0x51, 3, buffer);

                byte r = buffer[0];
                byte g = buffer[1];
                byte b = buffer[2];

                _lastRawColor = (r, g, b);
                ColorChanged?.Invoke(r, g, b);
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Connected, but couldn't read the current keyboard color: {ex.Message}");
            }
        }

        private static IEnumerable<string> BuildCandidatePaths(string? explicitPath)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath))
                yield return explicitPath;

            yield return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "InsydeDCHU.dll");

            string[] roots =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            };
            string[] appFolders = { "Acer Control Center", "PredatorSense", "NitroSense", "Acer" };

            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                foreach (var folder in appFolders)
                {
                    yield return Path.Combine(root, "Acer", folder, "InsydeDCHU.dll");
                    yield return Path.Combine(root, folder, "InsydeDCHU.dll");
                }
            }
        }

        public void Disconnect()
        {
            _setDchuData = null;
            _writeAppSettings = null;
            _readAppSettings = null;
            if (_hDll != IntPtr.Zero)
            {
                FreeLibrary(_hDll);
                _hDll = IntPtr.Zero;
            }
            LoadedFrom = null;
        }

        /// <summary>
        /// Exactly the sequence the reference single-zone (Insyde) implementation uses:
        /// one SetDCHU_Data call with {G, R, B, 0xF0}, then two WriteAppSettings calls
        /// that write the raw colour bytes and the "mode 8" (custom colour) flag.
        /// </summary>
        public void SetColor(byte r, byte g, byte b)
        {
            // Recorded unconditionally (even if not connected) so the sleep monitor always knows
            // what color to fade from/restore to once a connection does exist.
            _lastRawColor = (r, g, b);

            if (!IsConnected) return;

            double br = System.Math.Clamp(Brightness, 0.0, 1.0) * System.Math.Clamp(_sleepMultiplier, 0.0, 1.0);
            byte sr = (byte)System.Math.Round(r * br);
            byte sg = (byte)System.Math.Round(g * br);
            byte sb = (byte)System.Math.Round(b * br);

            const byte mode = 8;
            byte[] dchuData = { sg, sr, sb, 0xF0 };
            byte[] colour = { sr, sg, sb };
            byte[] modeBuf = { mode };

            lock (_sync)
            {
                try
                {
                    _setDchuData!(0x67, dchuData, (uint)dchuData.Length);
                    _writeAppSettings!(2, 0x51, (uint)colour.Length, colour);
                    _writeAppSettings!(2, 0x20, (uint)modeBuf.Length, modeBuf);
                }
                catch (Exception ex)
                {
                    StatusChanged?.Invoke($"Write failed: {ex.Message}");
                    return;
                }
            }

            ColorChanged?.Invoke(sr, sg, sb);
        }

        // ---------------- Animation engine ----------------

        public void StartEffect(EffectType effect, int speedMs)
        {
            StopEffect();

            if (effect == EffectType.Reactive || effect == EffectType.AmbientReactive)
            {
                // Reactive-family effects don't run a continuous tick loop like the others -
                // they just sit armed until GlobalKeyboardHook reports a keystroke via
                // ReactiveStrike(). AmbientReactive additionally rests at a dim baseline instead
                // of fully off, rather than going completely dark between keystrokes.
                _reactiveActive = true;
                _reactiveAmbient = effect == EffectType.AmbientReactive;

                if (_reactiveAmbient)
                {
                    var (dr, dg, db) = ScaleColor(BaseColor, AmbientReactiveDimLevel);
                    SetColor(dr, dg, db);
                }
                else
                {
                    SetColor(0, 0, 0);
                }
                return;
            }

            _animCts = new CancellationTokenSource();
            _animTask = Task.Run(() => AnimationLoop(effect, speedMs, _animCts.Token), _animCts.Token);
        }

        public void StopEffect()
        {
            _reactiveActive = false;
            _reactiveAmbient = false;
            _reactiveFadeCts?.Cancel();
            _reactiveFadeCts?.Dispose();
            _reactiveFadeCts = null;

            _animCts?.Cancel();
            try { _animTask?.Wait(200); } catch { /* task already exiting */ }
            _animCts?.Dispose();
            _animCts = null;
            _animTask = null;
        }

        /// <summary>
        /// Called by the global keyboard hook on every keystroke while Reactive or
        /// AmbientReactive is running. Immediately flashes the target color (BaseColor, or the
        /// next color in <see cref="MultiColorPalette"/> if <see cref="UseMultiColorMode"/> is
        /// set) at full brightness, then smoothly fades back over ~180ms - to fully off for
        /// Reactive, or back down to the dim <see cref="AmbientReactiveDimLevel"/> baseline for
        /// AmbientReactive. A no-op unless one of those two effects is active, so the hook can
        /// call this unconditionally on every keystroke without checking UI state itself.
        /// </summary>
        public void ReactiveStrike()
        {
            if (!_reactiveActive) return;

            var (r, g, b) = UseMultiColorMode ? NextMultiColor() : BaseColor;

            // Fast typing re-triggers this far quicker than a single fade takes to finish -
            // cancel whatever fade is still in flight so the new flash always starts clean from
            // full brightness instead of blending in partway through the previous one.
            var previousCts = _reactiveFadeCts;
            var cts = new CancellationTokenSource();
            _reactiveFadeCts = cts;
            previousCts?.Cancel();
            previousCts?.Dispose();

            SetColor(r, g, b);
            double restingFraction = _reactiveAmbient ? AmbientReactiveDimLevel : 0.0;
            _ = FadeReactiveFlashAsync(r, g, b, restingFraction, cts.Token);
        }

        private async Task FadeReactiveFlashAsync(byte r, byte g, byte b, double restingFraction, CancellationToken token)
        {
            const int durationMs = 180;
            const int stepMs = 15;
            int steps = durationMs / stepMs;

            try
            {
                for (int i = 1; i <= steps; i++)
                {
                    await Task.Delay(stepMs, token);
                    // 1.0 -> restingFraction over the fade (restingFraction is 0.0 for plain
                    // Reactive, or the dim ambient baseline for AmbientReactive).
                    double factor = 1.0 - ((double)i / steps) * (1.0 - restingFraction);
                    SetColor((byte)Math.Round(r * factor), (byte)Math.Round(g * factor), (byte)Math.Round(b * factor));
                }
            }
            catch (TaskCanceledException)
            {
                // Superseded by a newer keystroke's flash - that one owns the backlight now.
            }
        }

        private async Task AnimationLoop(EffectType effect, int speedMs, CancellationToken token)
        {
            const int frameIntervalMs = 20; // ~50 FPS logical tick
            double t = 0;
            double cycleSeconds = Math.Max(0.05, speedMs / 100.0);

            // Breathing/Blink/Heartbeat pulse the brightness of BaseColor - convert once,
            // keep hue+saturation fixed, and vary V (brightness) each frame.
            var (baseHue, baseSat, _) = RgbToHsv(BaseColor.r, BaseColor.g, BaseColor.b);

            // Fire has no per-key addressing available on this hardware (whole-zone only), so
            // it fakes flicker by re-rolling a random ember color every "cycleSeconds" (reusing
            // the same Speed slider as the other effects) and smoothly blending the whole
            // backlight from the previous roll to the new one, rather than hard-cutting between
            // two fixed colors.
            var fireRandom = new Random();
            var fireTargetColor = RandomFireColor(fireRandom);
            var firePreviousColor = fireTargetColor;
            double firePrevPhase = 0;

            // Breathing/Blink/Heartbeat share this same "0..1 per pulse" phase shape. In Multi
            // Color mode we don't touch BaseColor at all - instead we watch for the phase
            // wrapping back to the start of a new pulse and advance the shared palette cursor
            // at that moment, so each full breath/blink/beat gets the next color in sequence.
            double pulsePrevPhase = 0;

            while (!token.IsCancellationRequested)
            {
                bool isPulseEffect = effect is EffectType.Breathing or EffectType.Blink or EffectType.Heartbeat;
                double effectiveHue = baseHue;
                double effectiveSat = baseSat;
                if (isPulseEffect)
                {
                    double pulsePhase = (t % cycleSeconds) / cycleSeconds;
                    if (UseMultiColorMode && pulsePhase < pulsePrevPhase)
                    {
                        _multiColorIndex = (_multiColorIndex + 1) % MultiColorPalette.Length;
                    }
                    pulsePrevPhase = pulsePhase;

                    if (UseMultiColorMode)
                    {
                        var mc = MultiColorPalette[_multiColorIndex];
                        (effectiveHue, effectiveSat, _) = RgbToHsv(mc.r, mc.g, mc.b);
                    }
                }

                switch (effect)
                {
                    case EffectType.Breathing:
                        {
                            double phase = (t % cycleSeconds) / cycleSeconds;
                            double brightness = (Math.Sin(phase * 2 * Math.PI - Math.PI / 2) + 1) / 2;
                            var (r, g, b) = HsvToRgb(effectiveHue, effectiveSat, brightness);
                            SetColor(r, g, b);
                            break;
                        }

                    case EffectType.Rainbow:
                        {
                            double phase = (t % cycleSeconds) / cycleSeconds;
                            var (r, g, b) = HsvToRgb(phase * 360.0, 1.0, 1.0);
                            SetColor(r, g, b);
                            break;
                        }

                    case EffectType.Blink:
                        {
                            double phase = (t % cycleSeconds) / cycleSeconds;
                            var (r, g, b) = HsvToRgb(effectiveHue, effectiveSat, phase < 0.5 ? 1.0 : 0.0);
                            SetColor(r, g, b);
                            break;
                        }

                    case EffectType.Heartbeat:
                        {
                            double phase = (t % cycleSeconds) / cycleSeconds;
                            double brightness = phase switch
                            {
                                < 0.15 => EaseOut(phase / 0.15),
                                < 0.25 => 1.0 - EaseOut((phase - 0.15) / 0.10),
                                < 0.35 => EaseOut((phase - 0.25) / 0.10) * 0.7,
                                < 0.50 => 0.7 - EaseOut((phase - 0.35) / 0.15) * 0.7,
                                _ => 0.0
                            };
                            var (r, g, b) = HsvToRgb(effectiveHue, effectiveSat, Math.Clamp(brightness, 0, 1));
                            SetColor(r, g, b);
                            break;
                        }

                    case EffectType.Fire:
                        {
                            double phase = (t % cycleSeconds) / cycleSeconds;

                            // Phase wrapped back to the start of a new cycle - lock in the color
                            // we were blending toward as the new starting point, and roll a
                            // fresh random target to blend toward next.
                            if (phase < firePrevPhase)
                            {
                                firePreviousColor = fireTargetColor;
                                fireTargetColor = RandomFireColor(fireRandom);
                            }
                            firePrevPhase = phase;

                            // EaseOut rather than linear blending front-loads the color change,
                            // so most of the cycle "sits" near the new ember shade instead of
                            // sliding through it evenly - reads more like a flicker than a fade.
                            var (r, g, b) = LerpColor(firePreviousColor, fireTargetColor, EaseOut(phase));
                            SetColor(r, g, b);
                            break;
                        }

                    case EffectType.Custom:
                        {
                            var seq = CustomSequence;
                            if (seq.Count == 0)
                            {
                                SetColor(BaseColor.r, BaseColor.g, BaseColor.b);
                            }
                            else if (seq.Count == 1)
                            {
                                SetColor(seq[0].r, seq[0].g, seq[0].b);
                            }
                            else
                            {
                                double phase = (t % cycleSeconds) / cycleSeconds; // 0..1 across the whole sequence
                                double segLen = 1.0 / seq.Count;
                                int segIndex = Math.Min(seq.Count - 1, (int)(phase / segLen));
                                int nextIndex = (segIndex + 1) % seq.Count;
                                double localT = (phase - segIndex * segLen) / segLen; // 0..1 within this segment

                                var from = seq[segIndex];
                                var to = seq[nextIndex];
                                byte r = (byte)Math.Round(from.r + (to.r - from.r) * localT);
                                byte g = (byte)Math.Round(from.g + (to.g - from.g) * localT);
                                byte b = (byte)Math.Round(from.b + (to.b - from.b) * localT);
                                SetColor(r, g, b);
                            }
                            break;
                        }

                    case EffectType.Static:
                    default:
                        return;
                }

                try { await Task.Delay(frameIntervalMs, token); }
                catch (TaskCanceledException) { break; }

                t += frameIntervalMs / 1000.0;
            }
        }

        private static double EaseOut(double x) => 1 - Math.Pow(1 - Math.Clamp(x, 0, 1), 2);

        /// <summary>
        /// A weighted-by-listing palette of ember shades - mostly deep red and bright orange,
        /// with a couple of brighter yellow entries for occasional "flare-up" moments - matching
        /// what a single whole-zone light can do to suggest fire without per-key control.
        /// </summary>
        private static readonly (byte r, byte g, byte b)[] FirePalette =
        {
            (0x8B, 0x00, 0x00), // deep red ember
            (0xB2, 0x22, 0x00), // ember red-orange
            (0xFF, 0x45, 0x00), // bright orange
            (0xFF, 0x6A, 0x00), // orange
            (0xFF, 0x8C, 0x00), // orange-yellow
            (0xFF, 0xB3, 0x00), // amber
            (0xFF, 0xD1, 0x00), // yellow flare
        };

        private static (byte r, byte g, byte b) RandomFireColor(Random rng) => FirePalette[rng.Next(FirePalette.Length)];

        private static (byte r, byte g, byte b) LerpColor((byte r, byte g, byte b) from, (byte r, byte g, byte b) to, double t)
        {
            t = Math.Clamp(t, 0, 1);
            return (
                (byte)Math.Round(from.r + (to.r - from.r) * t),
                (byte)Math.Round(from.g + (to.g - from.g) * t),
                (byte)Math.Round(from.b + (to.b - from.b) * t)
            );
        }

        private static (byte r, byte g, byte b) ScaleColor((byte r, byte g, byte b) color, double factor)
        {
            factor = Math.Clamp(factor, 0, 1);
            return (
                (byte)Math.Round(color.r * factor),
                (byte)Math.Round(color.g * factor),
                (byte)Math.Round(color.b * factor)
            );
        }

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

        /// <summary>Inverse of HsvToRgb: RGB (0-255 each) -> (hue 0-360, saturation 0-1, value 0-1).</summary>
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
            double v = max;

            return (h, s, v);
        }

        // ---------------- Sleep Time idle monitor ----------------

        /// <summary>
        /// Runs for the entire lifetime of the engine (not tied to any particular effect),
        /// polling true system-wide idle time and fading the backlight to black once it exceeds
        /// <see cref="SleepMinutes"/>, then instantly restoring it the moment new input arrives.
        /// Because this only ever adjusts <see cref="_sleepMultiplier"/> and re-sends
        /// <see cref="_lastRawColor"/> - rather than touching whatever effect is running - it
        /// works transparently underneath Static, an animated loop, or Reactive/AmbientReactive.
        /// </summary>
        private async Task SleepMonitorLoop(CancellationToken token)
        {
            const int pollIntervalMs = 500;

            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(pollIntervalMs, token); }
                catch (TaskCanceledException) { break; }

                double sleepMinutes = SleepMinutes;
                if (sleepMinutes <= 0)
                {
                    // Sleep disabled - if we'd already dimmed for a previous setting, wake back up.
                    if (_isAsleep) WakeInstantly();
                    continue;
                }

                double idleMinutes = GetIdleTimeMs() / 60000.0;
                if (idleMinutes >= sleepMinutes)
                {
                    if (!_isAsleep)
                    {
                        _isAsleep = true;
                        await FadeAsleepAsync(token);
                    }
                }
                else if (_isAsleep)
                {
                    // GetLastInputInfo's clock just reset - the user moved the mouse or typed.
                    WakeInstantly();
                }
            }
        }

        private async Task FadeAsleepAsync(CancellationToken token)
        {
            const int durationMs = 800;
            const int stepMs = 40;
            int steps = durationMs / stepMs;
            double start = _sleepMultiplier;

            try
            {
                for (int i = 1; i <= steps; i++)
                {
                    await Task.Delay(stepMs, token);
                    _sleepMultiplier = start + (0.0 - start) * i / steps;
                    var (r, g, b) = _lastRawColor;
                    SetColor(r, g, b);
                }
            }
            catch (TaskCanceledException)
            {
                // Engine is shutting down mid-fade - nothing further to do.
            }
        }

        /// <summary>Snaps straight back to full brightness - no fade-in, per spec: waking should
        /// feel instant, only falling asleep should be gradual.</summary>
        private void WakeInstantly()
        {
            _isAsleep = false;
            _sleepMultiplier = 1.0;
            var (r, g, b) = _lastRawColor;
            SetColor(r, g, b);
        }

        private static uint GetIdleTimeMs()
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info)) return 0;

            // GetTickCount wraps to 0 roughly every 49.7 days; unchecked subtraction still
            // yields the correct (small, positive) delta across that wraparound thanks to
            // unsigned modular arithmetic, so it's left unguarded rather than special-cased.
            return unchecked(GetTickCount() - info.dwTime);
        }

        public void Dispose()
        {
            StopEffect();

            _sleepCts.Cancel();
            try { _sleepMonitorTask.Wait(200); } catch { /* task already exiting */ }
            _sleepCts.Dispose();

            Disconnect();
        }
    }
}