using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KeyPalette
{
    /// <summary>
    /// Wraps a low-level, system-wide keyboard hook (WH_KEYBOARD_LL) so KeyPalette can react to
    /// keystrokes typed anywhere on the machine - including while KeyPalette is minimized or a
    /// completely different window has focus. A normal WPF PreviewKeyDown handler only ever sees
    /// keys typed while THIS window specifically has focus, which isn't good enough for a
    /// "reactive" lighting effect that should respond to actual typing.
    ///
    /// WH_KEYBOARD_LL is special-cased by Windows: it runs its callback on the thread that
    /// installed it (no DLL injection into other processes needed), but that thread MUST be
    /// pumping Windows messages for the callback to ever fire. The WPF UI thread already does
    /// that via its Dispatcher, so this hook is installed directly on the UI thread rather than
    /// spinning up a separate one.
    /// </summary>
    public sealed class GlobalKeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104; // Alt+key combinations

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        /// <summary>Raised on every physical key-down, system-wide - fires with the Win32
        /// virtual-key code of the key that was pressed. Handlers should return fast; heavy work
        /// (like the hardware writes here) should be offloaded rather than done inline, since
        /// Windows can silently disable a hook that blocks its message pump for too long.</summary>
        public event Action<int>? KeyDown;

        // Kept alive for the hook's lifetime - if this delegate is garbage collected while the
        // hook is still installed, Windows ends up calling into freed native memory.
        private readonly LowLevelKeyboardProc _proc;
        private IntPtr _hookHandle = IntPtr.Zero;

        public bool IsInstalled => _hookHandle != IntPtr.Zero;

        public GlobalKeyboardHook()
        {
            _proc = HookCallback;
        }

        /// <summary>Installs the hook. Safe to call more than once - a no-op if already installed.
        /// Throws if Windows refuses to set the hook (e.g. blocked by policy/AV).</summary>
        public void Install()
        {
            if (_hookHandle != IntPtr.Zero) return;

            using Process curProcess = Process.GetCurrentProcess();
            using ProcessModule? curModule = curProcess.MainModule;
            IntPtr hMod = curModule != null ? GetModuleHandle(curModule.ModuleName) : IntPtr.Zero;

            _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, hMod, 0 /* 0 = system-wide */);
            if (_hookHandle == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"SetWindowsHookEx(WH_KEYBOARD_LL) failed (Win32 error {error}).");
            }
        }

        public void Uninstall()
        {
            if (_hookHandle == IntPtr.Zero) return;
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                KeyDown?.Invoke((int)data.vkCode);
            }

            // Always chain to the next hook - this is a passive observer. Returning anything
            // other than CallNextHookEx's result here would swallow the keystroke system-wide,
            // breaking typing in every other application on the machine.
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        public void Dispose() => Uninstall();
    }
}
