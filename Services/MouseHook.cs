using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace Action_Wheel.Services
{
    /// <summary>
    /// Implements a low-level global mouse hook to detect middle mouse button clicks.
    /// Uses Win32 API WH_MOUSE_LL hook to monitor mouse events system-wide.
    /// </summary>
    public class MouseHook : IDisposable
    {
        #region Win32 API Imports

        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_MBUTTONUP = 0x0208;
        private const int WM_XBUTTONDOWN = 0x020B;
        private const int WM_XBUTTONUP = 0x020C;

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT Point;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public UIntPtr ExtraInfo;
        }

        #endregion

        /// <summary>Any non-zero return from a hook procedure swallows the event.</summary>
        private static readonly IntPtr Handled = (IntPtr)1;

        private IntPtr _hookID = IntPtr.Zero;
        private readonly LowLevelMouseProc _proc;
        private readonly ChordGate _chordGate = new();
        private ChordTrigger _activeChord;
        public long LastCallbackTick { get; private set; }

        public MouseTrigger Trigger { get; set; } = MouseTrigger.MiddleButton;

        public int ChordTimeoutMs
        {
            get => _chordGate.ChordTimeoutMs;
            set => _chordGate.ChordTimeoutMs = value;
        }

        public int ChordMovementThreshold
        {
            get => _chordGate.ChordMovementThreshold;
            set => _chordGate.ChordMovementThreshold = value;
        }

        /// <summary>
        /// Raised when the middle mouse button is pressed.
        /// </summary>
        public event EventHandler<PointInt32>? MiddleClick;

        /// <summary>
        /// Raised when any mouse button is pressed, with the cursor position. Used to dismiss an
        /// open menu when the user clicks somewhere else on the screen - the menu window is
        /// WS_EX_NOACTIVATE, so it never gets a "deactivated" notification of its own.
        /// </summary>
        public event EventHandler<PointInt32>? AnyButtonDown;

        /// <summary>Raised when the middle mouse button is released. Completes a drag gesture.</summary>
        public event EventHandler<PointInt32>? MiddleUp;

        /// <summary>Raised when the first button of an active chord is released before the trigger.</summary>
        public event EventHandler? GestureCanceled;

        /// <summary>
        /// Raised on cursor movement, but only while <see cref="TrackMovement"/> is set.
        /// </summary>
        public event EventHandler<PointInt32>? MouseMoved;

        /// <summary>
        /// Turns <see cref="MouseMoved"/> on. Off by default on purpose: this callback runs for every
        /// mouse move on the system, and raising an event from it when nothing is listening puts
        /// pointless work in the input path of every application.
        /// </summary>
        public bool TrackMovement { get; set; }

        public MouseHook()
        {
            // Keep a reference to prevent garbage collection
            _proc = HookCallback;
        }

        /// <summary>
        /// Starts the global mouse hook.
        /// </summary>
        public void Start()
        {
            if (_hookID != IntPtr.Zero)
                return; // Already hooked

            // The current process's own module, asked for directly rather than looked up again by
            // name via Process.MainModule.ModuleName - that lookup can fail for a self-contained
            // single-file publish even though the module is already loaded, since the running exe
            // is a temporary extraction rather than a normally-loaded module on disk.
            IntPtr module = GetModuleHandle(null);
            int moduleError = module == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;

            _hookID = SetWindowsHookEx(WH_MOUSE_LL, _proc, module, 0);

            if (_hookID == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 0)
                    error = moduleError;
                throw new Win32Exception(error, "Windows could not install the mouse hook");
            }

            LastCallbackTick = Environment.TickCount64;
        }

        /// <summary>
        /// Stops the global mouse hook.
        /// </summary>
        public void Stop()
        {
            _activeChord = ChordTrigger.None;
            if (_hookID != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookID);
                _hookID = IntPtr.Zero;
            }
        }

        /// <summary>
        /// Gets the current cursor position in screen coordinates. Returns false if Windows refused
        /// to answer - on a locked or switched-out session it does. The caller has to handle that:
        /// the old signature swallowed the failure and returned (0, 0), which put the ring in the
        /// top-left corner of the screen instead of under the cursor.
        /// </summary>
        public static bool TryGetCursorPosition(out PointInt32 position)
        {
            if (GetCursorPos(out POINT point))
            {
                position = new PointInt32(point.X, point.Y);
                return true;
            }

            position = default;
            return false;
        }

        /// <summary>
        /// Hook callback that processes mouse events.
        /// </summary>
        /// <remarks>
        /// Returning a non-zero value swallows the event so it never reaches the application under
        /// the cursor. Both the middle button's DOWN and UP are swallowed: the middle click is this
        /// app's trigger gesture, and letting it through would also open a browser tab / start
        /// autoscroll underneath the menu. Swallowing DOWN without UP would leave apps that track
        /// button state believing the button is still held - see <see cref="ChordGate"/> for how the
        /// two chord triggers keep left and right balanced individually rather than as a pair.
        /// </remarks>
        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            LastCallbackTick = Environment.TickCount64;
            try
            {
                if (Dispatch(nCode, wParam, lParam))
                    return Handled;
            }
            catch (Exception)
            {
                // Nothing may throw out of here: this is a reverse P/Invoke from user32, and a
                // managed exception crossing back into a native frame makes the CLR fail fast.
            }

            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        /// <summary>
        /// Reads the event structure out of the pointer user32 supplied, without allocating.
        /// </summary>
        /// <remarks>
        /// <c>Marshal.PtrToStructure&lt;T&gt;</c> is the obvious way to write this and it boxes:
        /// measured at 48 bytes and 20 ns per call against 0 and 0 for the dereference, over two
        /// million iterations. That looks negligible until you notice where this runs. The callback
        /// fires for every mouse event on the system, movement included, at the mouse's report rate
        /// - 125 Hz on an office mouse, 1000 Hz on a gaming one - and it fires before anything here
        /// has decided the event is uninteresting. So it was 6 to 48 KB per second of Gen0 garbage,
        /// produced from inside the one callback Windows times against LowLevelHooksTimeout and
        /// silently unhooks for overrunning. The bytes were never the problem; a collection pausing
        /// this thread was.
        ///
        /// The dereference is safe: MSLLHOOKSTRUCT is blittable and has no managed fields, and the
        /// pointer is one Windows just gave us and keeps valid for the duration of the callback.
        /// </remarks>
        private static unsafe MSLLHOOKSTRUCT ReadEvent(IntPtr lParam) => *(MSLLHOOKSTRUCT*)lParam;

        /// <summary>Returns true if the event was consumed and must not reach the app underneath.</summary>
        private bool Dispatch(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;

                // lParam already carries the cursor position for this event. Reading it is both
                // cheaper than GetCursorPos - which this used to call two or three times for a
                // single button press, on the input path of every application on the system - and
                // more correct: GetCursorPos answers "where is the pointer now", which during fast
                // movement is not where it was when the button went down.
                var data = ReadEvent(lParam);
                if (data.ExtraInfo == InputInjection.MouseMarker)
                    return false;

                var at = new PointInt32(data.Point.X, data.Point.Y);

                if (msg is WM_XBUTTONDOWN or WM_XBUTTONUP)
                {
                    int button = (int)((data.MouseData >> 16) & 0xFFFF);
                    bool isConfigured = (Trigger == MouseTrigger.MouseButton4 && button == 1)
                        || (Trigger == MouseTrigger.MouseButton5 && button == 2);

                    if (isConfigured)
                    {
                        if (msg == WM_XBUTTONDOWN)
                            MiddleClick?.Invoke(this, at);
                        else
                            MiddleUp?.Invoke(this, at);
                        return true;
                    }

                    if (msg == WM_XBUTTONDOWN)
                        AnyButtonDown?.Invoke(this, at);
                }

                if (msg == WM_MBUTTONDOWN && Trigger == MouseTrigger.MiddleButton)
                {
                    MiddleClick?.Invoke(this, at);
                    return true;
                }

                if (msg == WM_MBUTTONUP && Trigger == MouseTrigger.MiddleButton)
                {
                    MiddleUp?.Invoke(this, at);
                    return true;
                }

                if (msg == WM_MBUTTONDOWN && Trigger != MouseTrigger.MiddleButton)
                    AnyButtonDown?.Invoke(this, at);

                if (msg == WM_MOUSEMOVE && TrackMovement)
                {
                    // Not swallowed - the cursor must keep moving normally while the menu is open.
                    MouseMoved?.Invoke(this, at);
                }

                if (msg == WM_LBUTTONDOWN)
                {
                    var chordTrigger = Trigger == MouseTrigger.HoldRightPressLeft
                        ? ChordTrigger.HoldRightPressLeft : ChordTrigger.None;
                    bool swallow = _chordGate.OnLeftDown(chordTrigger, DateTime.UtcNow, at.X, at.Y, out bool chordFired);

                    if (chordFired)
                    {
                        _activeChord = chordTrigger;
                        MiddleClick?.Invoke(this, at);
                    }

                    if (swallow)
                        return true;

                    AnyButtonDown?.Invoke(this, at);
                }

                else if (msg == WM_RBUTTONDOWN)
                {
                    var chordTrigger = Trigger == MouseTrigger.HoldLeftPressRight
                        ? ChordTrigger.HoldLeftPressRight : ChordTrigger.None;
                    bool swallow = _chordGate.OnRightDown(chordTrigger, DateTime.UtcNow, at.X, at.Y, out bool chordFired);

                    if (chordFired)
                    {
                        _activeChord = chordTrigger;
                        MiddleClick?.Invoke(this, at);
                    }

                    if (swallow)
                        return true;

                    AnyButtonDown?.Invoke(this, at);
                }

                else if (msg == WM_LBUTTONUP)
                {
                    bool swallow = _chordGate.OnLeftUp();
                    if (_activeChord == ChordTrigger.HoldRightPressLeft)
                    {
                        _activeChord = ChordTrigger.None;
                        MiddleUp?.Invoke(this, at);
                    }
                    else if (_activeChord == ChordTrigger.HoldLeftPressRight)
                    {
                        _activeChord = ChordTrigger.None;
                        GestureCanceled?.Invoke(this, EventArgs.Empty);
                    }

                    if (swallow)
                        return true;
                }

                else if (msg == WM_RBUTTONUP)
                {
                    bool swallow = _chordGate.OnRightUp();
                    if (_activeChord == ChordTrigger.HoldLeftPressRight)
                    {
                        _activeChord = ChordTrigger.None;
                        MiddleUp?.Invoke(this, at);
                    }
                    else if (_activeChord == ChordTrigger.HoldRightPressLeft)
                    {
                        _activeChord = ChordTrigger.None;
                        GestureCanceled?.Invoke(this, EventArgs.Empty);
                    }

                    if (swallow)
                        return true;
                }
            }

            return false;
        }

        public void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }
    }
}
