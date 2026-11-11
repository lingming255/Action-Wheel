using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Action_Wheel.Overlay
{
    /// <summary>
    /// Makes a decorative Composition window transparent to input across applications.
    /// WS_EX_TRANSPARENT on its own only changes painting order; layered windows explicitly
    /// combine it with hit-test transparency. Composition retains ownership of opacity;
    /// SetLayeredWindowAttributes would introduce a legacy redirection surface and whiten
    /// its premultiplied output.
    /// </summary>
    internal static class RefreshInputTransparency
    {
        internal static void Enable(nint hwnd)
        {
            if (hwnd == 0) throw new ArgumentException("A refresh window handle is required.", nameof(hwnd));
            const int extendedStyle = -20;
            const int transparent = 0x20, layered = 0x80000, noActivate = 0x08000000;
            int style = GetWindowLong(hwnd, extendedStyle);
            int previous = SetWindowLong(hwnd, extendedStyle, style | transparent | layered | noActivate);
            if (previous == 0 && Marshal.GetLastWin32Error() != 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Refresh input transparency could not be enabled.");
            if (!SetWindowPos(hwnd, 0, 0, 0, 0, 0, 0x37)) // FRAMECHANGED | NOACTIVATE | NOZORDER | NOMOVE | NOSIZE
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Refresh input style could not be applied.");
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(nint hwnd, int index);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(nint hwnd, int index, int style);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    }
}
