using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Action_Wheel.Services
{
    internal sealed partial class LaunchWindowFollower
    {
        /// <summary>
        /// Native geometry only; production callers first establish window ownership/evidence.
        /// Same-monitor windows keep the user's position. Cross-monitor moves preserve logical
        /// size where it fits, use the work area (including negative coordinates), and keep state.
        /// </summary>
        internal static async Task<bool> MoveWindowAsync(nint hwnd, WindowPlacementTarget target,
            Func<bool>? stillCurrent = null)
        {
            try
            {
                bool Allowed() => IsWindow(hwnd) && (stillCurrent?.Invoke() ?? true);
                if (!Allowed()) return false;
                nint destination = MonitorFromPoint(new NativePoint(target.X, target.Y), 2);
                nint source = MonitorFromWindow(hwnd, 2);
                if (destination == 0 || source == 0) return false;
                if (source == destination) return true;
                var targetMonitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                var sourceMonitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(destination, ref targetMonitor) || !GetMonitorInfo(source, ref sourceMonitor)
                    || !GetWindowRect(hwnd, out var current)) return false;
                var placement = new NativePlacement { Length = Marshal.SizeOf<NativePlacement>() };
                bool hasPlacement = GetWindowPlacement(hwnd, ref placement);
                bool maximized = IsZoomed(hwnd), minimized = IsIconic(hwnd);
                NativeRect size = (maximized || minimized) && hasPlacement ? placement.Normal : current;
                // Compare monitor DPI on both sides. GetDpiForWindow returns 96/system DPI for
                // unaware/system-aware apps even though GetWindowRect here is physical, which
                // would incorrectly scale an already DWM-scaled legacy window a second time.
                uint sourceDpi = 96, targetDpi = 96;
                try
                {
                    if (GetDpiForMonitor(source, 0, out uint sourceX, out _) == 0 && sourceX > 0) sourceDpi = sourceX;
                    if (GetDpiForMonitor(destination, 0, out uint dpiX, out _) == 0 && dpiX > 0) targetDpi = dpiX;
                }
                catch (EntryPointNotFoundException) { }
                double ratio = sourceDpi > 0 && targetDpi > 0 ? (double)targetDpi / sourceDpi : 1;
                var centered = CenteredRectangle(Math.Max(1, (int)Math.Round(size.Width * ratio)),
                    Math.Max(1, (int)Math.Round(size.Height * ratio)), targetMonitor.Work);
                const uint noActivateNoZOrderAsync = 0x0010 | 0x0004 | 0x4000;
                if ((maximized || minimized) && hasPlacement)
                {
                    // Keeping showCmd can itself activate a maximized foreign window. A newer
                    // interactive wheel owns focus until it releases; do not bypass its guard.
                    if (!WindowInputFocus.CanActivateActionTarget || !Allowed()) return false;
                    // WINDOWPLACEMENT uses workspace coordinates; SetWindowPos uses screen
                    // coordinates. Retain showCmd instead of restore/maximize/activate cycling.
                    int offsetX = targetMonitor.Work.Left - targetMonitor.Monitor.Left;
                    int offsetY = targetMonitor.Work.Top - targetMonitor.Monitor.Top;
                    placement.Normal = new NativeRect(centered.Left - offsetX, centered.Top - offsetY,
                        centered.Right - offsetX, centered.Bottom - offsetY);
                    placement.Flags |= 4; // WPF_ASYNCWINDOWPLACEMENT: never wait on a foreign UI thread.
                    if (!SetWindowPlacement(hwnd, ref placement)) return false;
                    // SetWindowPlacement updates a maximized window's restore rectangle without
                    // moving its current HWND on some Windows/WinUI versions. Move that surface
                    // separately while retaining WS_MAXIMIZE and its newly stored normal bounds.
                    // No restore/maximize cycle is needed, and both calls target the same UI queue.
                    return !maximized || Allowed() && SetWindowPos(hwnd, 0, targetMonitor.Work.Left, targetMonitor.Work.Top,
                        targetMonitor.Work.Width, targetMonitor.Work.Height, noActivateNoZOrderAsync);
                }
                // Moving and resizing in one call lets WinUI apply its old-monitor scale to the
                // requested new-monitor physical size a second time. Migrate at the existing size,
                // let WM_DPICHANGED settle, then set the final physical bounds on that monitor.
                if (!Allowed() || !SetWindowPos(hwnd, 0, centered.Left, centered.Top, 0, 0,
                    noActivateNoZOrderAsync | 0x0001)) return false; // SWP_NOSIZE
                bool arrived = false;
                for (int sample = 0; sample < 6; sample++)
                {
                    await Task.Delay(40).ConfigureAwait(false);
                    if (!Allowed()) return false;
                    if (MonitorFromWindow(hwnd, 2) == destination)
                    {
                        // An additional queue turn lets XAML process its DPI callback before the
                        // physical sizing operation; no foreign UI thread is synchronously waited on.
                        await Task.Delay(40).ConfigureAwait(false);
                        arrived = true;
                        break;
                    }
                }
                return arrived && Allowed() && SetWindowPos(hwnd, 0, centered.Left, centered.Top,
                    centered.Width, centered.Height, noActivateNoZOrderAsync);
            }
            catch (Exception) { return false; }
        }

        private static bool ReadComparableRectangle(nint hwnd, out NativeRect rectangle)
        {
            if (IsIconic(hwnd))
            {
                var placement = new NativePlacement { Length = Marshal.SizeOf<NativePlacement>() };
                if (GetWindowPlacement(hwnd, ref placement)) { rectangle = placement.Normal; return true; }
            }
            return GetWindowRect(hwnd, out rectangle);
        }

        private static void RestoreForActivation(nint hwnd)
        {
            var placement = new NativePlacement { Length = Marshal.SizeOf<NativePlacement>() };
            bool restoreMaximized = GetWindowPlacement(hwnd, ref placement) && (placement.Flags & 2) != 0;
            ShowWindowAsync(hwnd, restoreMaximized ? 3 : 9); // Original maximize restore state, otherwise SW_RESTORE.
        }

        private static NativeRect CenteredRectangle(int width, int height, NativeRect work)
        {
            width = Math.Clamp(width, 1, Math.Max(1, work.Width));
            height = Math.Clamp(height, 1, Math.Max(1, work.Height));
            int left = work.Left + (work.Width - width) / 2;
            int top = work.Top + (work.Height - height) / 2;
            return new NativeRect(left, top, left + width, top + height);
        }

        private static ProcessIdentity? QueryProcess(uint pid)
        {
            nint handle = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
            if (handle == 0) return null;
            try
            {
                var path = new StringBuilder(32768);
                uint length = (uint)path.Capacity;
                if (!QueryFullProcessImageName(handle, 0, path, ref length)
                    || !GetProcessTimes(handle, out long started, out _, out _, out _)) return null;
                string? executable = NormaliseExecutable(path.ToString());
                uint familyLength = 256;
                var family = new StringBuilder((int)familyLength);
                string? packageFamily = GetPackageFamilyName(handle, ref familyLength, family) == 0 ? family.ToString() : null;
                return executable == null ? null : new ProcessIdentity(pid, executable, started, packageFamily);
            }
            finally { CloseHandle(handle); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly record struct NativeRect(int Left, int Top, int Right, int Bottom)
        {
            internal int Width => Right - Left;
            internal int Height => Bottom - Top;
        }
        [StructLayout(LayoutKind.Sequential)]
        private readonly record struct NativePoint(int X, int Y);
        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            internal int Size;
            internal NativeRect Monitor, Work;
            internal uint Flags;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativePlacement
        {
            internal int Length;
            internal uint Flags, ShowCommand;
            internal NativePoint Minimum, Maximum;
            internal NativeRect Normal;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMessage
        {
            internal nint Window;
            internal uint Message;
            internal nuint WParam;
            internal nint LParam;
            internal uint Time;
            internal NativePoint Point;
            internal uint Private;
        }
        private delegate bool EnumWindowCallback(nint hwnd, nint parameter);
        private delegate void WinEventCallback(nint hook, uint eventType, nint hwnd,
            int objectId, int childId, uint threadId, uint time);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, nint parameter);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumWindowCallback callback, nint parameter);
        [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
        [DllImport("user32.dll")] private static extern bool IsZoomed(nint hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint hwnd, int command);
        [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
        [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint hwnd, int index);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rectangle);
        [DllImport("user32.dll")] private static extern bool GetWindowPlacement(nint hwnd, ref NativePlacement placement);
        [DllImport("user32.dll")] private static extern bool SetWindowPlacement(nint hwnd, ref NativePlacement placement);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
        [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
        [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder name, ref uint length);
        [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(nint process, out long started, out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetPackageFamilyName(nint process, ref uint length, StringBuilder familyName);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint SearchPath(string? path, string file, string? extension, uint length, StringBuilder buffer, nint filePart);
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int AssocQueryString(uint flags, uint type, string association, string? extra, StringBuilder result, ref uint length);
        [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint first, uint last, nint module, WinEventCallback callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PeekMessage(out NativeMessage message, nint hwnd, uint first, uint last, uint remove);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessage(out NativeMessage message, nint hwnd, uint first, uint last);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostThreadMessage(uint thread, uint message, nuint wparam, nint lparam);
    }
}
