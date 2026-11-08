using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Action_Wheel.Services
{
    /// <summary>
    /// Low-level global keyboard hook used to catch Escape and the hold-to-open information wheel.
    /// The menu window carries WS_EX_NOACTIVATE, so it never takes keyboard focus and cannot
    /// receive a KeyDown of its own - the key has to be caught system-wide instead.
    /// </summary>
    public class KeyboardHook : IDisposable
    {
        #region Win32 API Imports

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYUP = 0x0105;
        private const int VK_ESCAPE = 0x1B;
        private const int VK_SPACE = 0x20;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        #endregion

        /// <summary>Any non-zero return from a hook procedure swallows the event.</summary>
        private static readonly IntPtr Handled = (IntPtr)1;

        private IntPtr _hookID = IntPtr.Zero;
        private readonly LowLevelKeyboardProc _proc;
        public long LastCallbackTick { get; private set; }

        /// <summary>
        /// Called when Escape is pressed. Return true if the key was consumed (i.e. a menu was
        /// open and got dismissed), which stops Escape from also reaching the foreground app.
        /// </summary>
        public Func<bool>? EscapePressed { get; set; }
        public Func<bool>? EmergencyTriggerPressed { get; set; }
        public Func<bool>? EmergencyTriggerReleased { get; set; }
        private bool _swallowEmergencyKeyUp;

        public KeyboardHook()
        {
            // Keep a reference to prevent garbage collection
            _proc = HookCallback;
        }

        public void Start()
        {
            if (_hookID != IntPtr.Zero)
                return;

            // The current process's own module, asked for directly rather than looked up again by
            // name via Process.MainModule.ModuleName - that lookup can fail for a self-contained
            // single-file publish even though the module is already loaded, since the running exe
            // is a temporary extraction rather than a normally-loaded module on disk.
            IntPtr module = GetModuleHandle(null);
            int moduleError = module == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;

            _hookID = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, module, 0);

            if (_hookID == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 0)
                    error = moduleError;
                throw new Win32Exception(error, "Windows could not install the keyboard hook");
            }

            LastCallbackTick = Environment.TickCount64;
        }

        public void Stop()
        {
            _swallowEmergencyKeyUp = false;
            if (_hookID != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookID);
                _hookID = IntPtr.Zero;
            }
        }

        /// <summary>
        /// The raw hook entry point. Nothing may throw out of here: this is a reverse P/Invoke from
        /// user32, and a managed exception crossing back into a native frame is not recoverable -
        /// the CLR fails fast and the process dies. The catch turns a handler bug into a key that
        /// simply passes through.
        /// </summary>
        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            LastCallbackTick = Environment.TickCount64;
            try
            {
                if (Dispatch(nCode, wParam, lParam))
                    return Handled;
            }
            catch (Exception) { }

            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        /// <summary>
        /// Reads the event structure out of the pointer user32 supplied, without allocating.
        /// Same reasoning as MouseHook.ReadEvent, at a fraction of the volume: once per key press
        /// rather than once per mouse movement.
        /// </summary>
        private static unsafe KBDLLHOOKSTRUCT ReadEvent(IntPtr lParam) => *(KBDLLHOOKSTRUCT*)lParam;

        /// <summary>Returns true if the key was consumed and must not reach the foreground app.</summary>
        private bool Dispatch(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    var data = ReadEvent(lParam);
                    if (data.dwExtraInfo == InputInjection.Marker)
                        return false;

                    if (data.vkCode == VK_ESCAPE && EscapePressed?.Invoke() == true)
                    {
                        return true;
                    }

                    if (data.vkCode == VK_SPACE
                        && _swallowEmergencyKeyUp)
                    {
                        // Space repeats while held. Once the first press was consumed, repeats must
                        // not leak into the foreground text field.
                        return true;
                    }

                    if (data.vkCode == VK_SPACE
                        && IsDown(VK_CONTROL) && IsDown(VK_MENU)
                        && EmergencyTriggerPressed?.Invoke() == true)
                    {
                        _swallowEmergencyKeyUp = true;
                        return true;
                    }
                }
                else if ((msg == WM_KEYUP || msg == WM_SYSKEYUP) && _swallowEmergencyKeyUp)
                {
                    var data = ReadEvent(lParam);
                    if (data.dwExtraInfo == InputInjection.Marker)
                        return false;

                    if (data.vkCode == VK_SPACE)
                    {
                        try
                        {
                            EmergencyTriggerReleased?.Invoke();
                        }
                        finally
                        {
                            _swallowEmergencyKeyUp = false;
                        }
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

        public void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }
    }
}
