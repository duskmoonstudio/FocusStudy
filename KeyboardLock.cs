using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace FocusStudy
{
    /// <summary>
    /// Global low-level keyboard hook that swallows the shortcuts used to leave a fullscreen app:
    /// Win keys, Alt+Tab, Alt+Esc, Alt+Space, Ctrl+Esc and Ctrl+Shift+Esc.
    /// Alt+F4 is swallowed too, but raised as an event so the app can start its exit steps.
    /// Ctrl+Alt+Del is handled by Windows itself and can never be blocked (by design).
    /// The hook is removed automatically by Windows if the process exits or crashes.
    /// </summary>
    internal sealed class KeyboardLock : IDisposable
    {
        const uint VK_TAB = 0x09, VK_ESCAPE = 0x1B, VK_SPACE = 0x20, VK_F4 = 0x73;
        const uint VK_LWIN = 0x5B, VK_RWIN = 0x5C;
        const int VK_CONTROL = 0x11;
        const uint LLKHF_ALTDOWN = 0x20;

        static KeyboardLock _current;

        readonly NativeMethods.LowLevelKeyboardProc _proc;
        IntPtr _hook = IntPtr.Zero;
        bool _altF4Down;

        public bool Active { get; set; }
        public event Action AltF4;

        public KeyboardLock()
        {
            _proc = HookProc; // keep a reference so the delegate is not garbage collected
        }

        public void Install()
        {
            if (_hook != IntPtr.Zero) return;
            _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
            _current = this;
        }

        public void Dispose()
        {
            Active = false;
            if (_hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }

        public static void ReleaseAll() => _current?.Dispose();

        IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && Active)
            {
                var k = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                int msg = wParam.ToInt32();
                bool down = msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN;
                bool alt = (k.flags & LLKHF_ALTDOWN) != 0;
                bool ctrl = (NativeMethods.GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                uint vk = k.vkCode;

                if (vk == VK_F4)
                {
                    if (!down) _altF4Down = false;
                    if (alt)
                    {
                        if (down && !_altF4Down)
                        {
                            _altF4Down = true;
                            var handler = AltF4;
                            if (handler != null) Application.Current?.Dispatcher.BeginInvoke(handler);
                        }
                        return (IntPtr)1;
                    }
                }

                if (vk == VK_LWIN || vk == VK_RWIN) return (IntPtr)1;                       // Start menu, Win+D, Win+Tab ...
                if (alt && (vk == VK_TAB || vk == VK_ESCAPE || vk == VK_SPACE)) return (IntPtr)1; // Alt+Tab, Alt+Esc, Alt+Space
                if (ctrl && vk == VK_ESCAPE) return (IntPtr)1;                               // Ctrl+Esc, Ctrl+Shift+Esc
            }
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }
    }
}
