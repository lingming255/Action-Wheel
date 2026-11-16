using System;
using System.Runtime.InteropServices;

namespace Action_Wheel.Services
{
    /// <summary>Short-lived focus handoff between the interactive wheel and its original app.</summary>
    internal static class WindowInputFocus
    {
        private static nint _interactiveWheel;
        internal static nint Foreground => GetForegroundWindow();

        internal static void EnterWheel(nint window) => System.Threading.Interlocked.Exchange(ref _interactiveWheel, window);
        internal static void LeaveWheel(nint window) => System.Threading.Interlocked.CompareExchange(ref _interactiveWheel, 0, window);

        internal static bool CanActivateActionTarget
        {
            get
            {
                nint wheel = System.Threading.Interlocked.CompareExchange(ref _interactiveWheel, 0, 0);
                return wheel == 0 || !IsWindow(wheel);
            }
        }

        // A slow launch from an earlier gesture must not take focus from a newer open wheel.
        // Its window may still be placed, and a later user invocation can explicitly raise it.
        internal static bool TryActivateActionTarget(nint target)
        {
            return CanActivateActionTarget && TryActivate(target);
        }

        internal static bool TryActivate(nint target)
        {
            if (target == 0 || !IsWindow(target)) return false;
            if (GetForegroundWindow() == target) return true;
            if (SetForegroundWindow(target) && GetForegroundWindow() == target) return true;

            // A queued UI turn can follow the triggering input. Temporarily share its foreground
            // queue for this one handoff; never leave threads attached or poll/reclaim focus.
            nint previous = GetForegroundWindow();
            uint foregroundThread = GetWindowThreadProcessId(previous, out _);
            uint ownThread = GetCurrentThreadId();
            bool attached = foregroundThread != 0 && foregroundThread != ownThread
                && AttachThreadInput(ownThread, foregroundThread, true);
            try { return SetForegroundWindow(target) && GetForegroundWindow() == target; }
            finally { if (attached) AttachThreadInput(ownThread, foregroundThread, false); }
        }

        [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint target);
        [DllImport("user32.dll")] private static extern bool IsWindow(nint target);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint first, uint second, bool attach);
    }
}
