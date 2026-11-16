using System;
using System.Runtime.InteropServices;

namespace Action_Wheel.Services
{
    /// <summary>Reads the target display's nominal mode once, outside the rendering loop.</summary>
    internal static class DisplayRefreshRate
    {
        internal static int ForWindow(nint window)
        {
            if (window == 0) return 60;
            nint monitor = MonitorFromWindow(window, 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = string.Empty };
            if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) return 60;
            var mode = new DisplayMode { Size = (ushort)Marshal.SizeOf<DisplayMode>() };
            if (!EnumDisplaySettings(info.Device, -1, ref mode)
                || (mode.Fields & 0x00400000) == 0
                || mode.Frequency <= 1 || mode.Frequency > 1000) return 60;
            return (int)mode.Frequency;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { internal int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfo
        {
            internal int Size;
            internal Rect Monitor, Work;
            internal uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Device;
        }

        // DEVMODEW is 220 bytes; only these fields are needed from the current display mode.
        [StructLayout(LayoutKind.Explicit, Size = 220)]
        private struct DisplayMode
        {
            [FieldOffset(68)] internal ushort Size;
            [FieldOffset(72)] internal uint Fields;
            [FieldOffset(184)] internal uint Frequency;
        }

        [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
        [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplaySettings(string device, int modeNumber, ref DisplayMode mode);
    }
}
