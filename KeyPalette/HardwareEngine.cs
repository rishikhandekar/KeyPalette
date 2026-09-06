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

        public event Action<string>? StatusChanged;
        public event Action<byte, byte, byte>? ColorChanged;

        private IntPtr _hDll = IntPtr.Zero;
        private SetDCHUDataDelegate? _setDchuData;
        private WriteAppSettingsDelegate? _writeAppSettings;
        private CancellationTokenSource? _animCts;
        private Task? _animTask;
        private readonly object _sync = new();

        public bool IsConnected => _hDll != IntPtr.Zero && _setDchuData != null && _writeAppSettings != null;
        public string? LoadedFrom { get; private set; }

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
                LoadedFrom = path;
                StatusChanged?.Invoke($"Connected via {path}");
                return true;
            }

            StatusChanged?.Invoke(
                "InsydeDCHU.dll not found automatically. Click 'Locate InsydeDCHU.dll' and browse to it - " +
                "it's the same DLL your Acer keyboard/control-center software already has installed.");
            return false;
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
            if (!IsConnected) return;

            double br = System.Math.Clamp(Brightness, 0.0, 1.0);
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
            _animCts = new CancellationTokenSource();
            _animTask = Task.Run(() => AnimationLoop(effect, speedMs, _animCts.Token), _animCts.Token);
        }

        public void StopEffect()
        {
            _animCts?.Cancel();
            try { _animTask?.Wait(200); } catch { /* task already exiting */ }
            _animCts?.Dispose();
            _animCts = null;
            _animTask = null;
        }

        private async Task AnimationLoop(EffectType effect, int speedMs, CancellationToken token)
        {
            const int frameIntervalMs = 20; // ~50 FPS logical tick
            double t = 0;
            double cycleSeconds = Math.Max(0.05, speedMs / 100.0);

            // Breathing/Blink/Heartbeat pulse the brightness of BaseColor - convert once,
            // keep hue+saturation fixed, and vary V (brightness) each frame.
            var (baseHue, baseSat, _) = RgbToHsv(BaseColor.r, BaseColor.g, BaseColor.b);

            while (!token.IsCancellationRequested)
            {
                switch (effect)
                {
                    case EffectType.Breathing:
                        {
                            double phase = (t % cycleSeconds) / cycleSeconds;
                            double brightness = (Math.Sin(phase * 2 * Math.PI - Math.PI / 2) + 1) / 2;
                            var (r, g, b) = HsvToRgb(baseHue, baseSat, brightness);
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
                            var (r, g, b) = HsvToRgb(baseHue, baseSat, phase < 0.5 ? 1.0 : 0.0);
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
                            var (r, g, b) = HsvToRgb(baseHue, baseSat, Math.Clamp(brightness, 0, 1));
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

        public void Dispose()
        {
            StopEffect();
            Disconnect();
        }
    }
}