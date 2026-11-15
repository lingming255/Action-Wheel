using System;
using System.Runtime.InteropServices;

namespace Action_Wheel.Services
{
    /// <summary>Short-lived focus handoff between the interactive wheel and its original app.</summary>
    internal static class WindowInputFocus
    {
        internal static nint Foreground => GetForegroundWindow();

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
