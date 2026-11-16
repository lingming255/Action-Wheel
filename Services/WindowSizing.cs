using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Action_Wheel.Services
{
    public static class WindowSizing
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct CursorPoint { internal int X; internal int Y; }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out CursorPoint point);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr window);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRectangle { internal int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeWindowPlacement
        {
            internal int Length, Flags, ShowCommand;
            internal CursorPoint MinPosition, MaxPosition;
            internal NativeRectangle NormalPosition;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowPlacement(IntPtr window, ref NativeWindowPlacement placement);

        /// <summary>Capture once at a tool entry point, before dispatching or awaiting UI work.</summary>
        public static WindowPlacementTarget? CaptureTarget()
        {
            try
            {
                return GetCursorPos(out var point) ? new WindowPlacementTarget(point.X, point.Y) : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Move an existing tool while preserving its current size in logical pixels.</summary>
        public static void CentreOnTarget(Window window, FrameworkElement content, WindowPlacementTarget target)
        {
            OverlappedPresenter? presenter = null;
            bool restoreMaximized = false;
            nint foreground = WindowInputFocus.Foreground;
            try
            {
                restoreMaximized = RestoreForPlacement(window, out presenter);
                double scale = WindowScale(window, content);
                var size = window.AppWindow.Size;
                SizeAndCentre(window, content, size.Width / scale, size.Height / scale,
                    "an existing tool window", target);
            }
            catch (Exception) { }
            finally { FinishPlacement(window, presenter, restoreMaximized, foreground); }
        }

        public static void SizeAndCentre(Window window, FrameworkElement content,
            double widthDip, double heightDip, string context, WindowPlacementTarget? target = null)
        {
            OverlappedPresenter? presenter = null;
            bool restoreMaximized = false;
            nint foreground = WindowInputFocus.Foreground;
            try
            {
                if (target != null) restoreMaximized = RestoreForPlacement(window, out presenter);
                var display = target == null
                    ? DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Nearest)
                    : DisplayArea.GetFromPoint(new PointInt32(target.X, target.Y), DisplayAreaFallback.Nearest);
                if (target != null && display != null)
                {
                    var current = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Nearest);
                    if (current == null || current.DisplayId.Value != display.DisplayId.Value)
                    {
                        // Migrate without activation before asking for DPI. XamlRoot can still
                        // describe the old monitor during the first synchronous opening turn.
                        var size = window.AppWindow.Size;
                        int stagedWidth = Math.Clamp(size.Width, 1, display.WorkArea.Width);
                        int stagedHeight = Math.Clamp(size.Height, 1, display.WorkArea.Height);
                        window.AppWindow.MoveAndResize(new RectInt32(
                            display.WorkArea.X + (display.WorkArea.Width - stagedWidth) / 2,
                            display.WorkArea.Y + (display.WorkArea.Height - stagedHeight) / 2,
                            stagedWidth, stagedHeight));
                    }
                }
                // WM_DPICHANGED can update the native DPI as the HWND migrates. Reconcile once
                // more if resizing/centering itself crossed that boundary; never chase the cursor.
                for (int pass = 0; pass < 2; pass++)
                {
                    double scale = WindowScale(window, content);
                    int width = Math.Max(1, (int)Math.Round(widthDip * scale));
                    int height = Math.Max(1, (int)Math.Round(heightDip * scale));
                    if (display != null)
                    {
                        width = Math.Min(width, display.WorkArea.Width);
                        height = Math.Min(height, display.WorkArea.Height);
                    }
                    window.AppWindow.Resize(new SizeInt32(width, height));
                    if (display != null)
                        window.AppWindow.Move(new PointInt32(
                            display.WorkArea.X + Math.Max(0, (display.WorkArea.Width - width) / 2),
                            display.WorkArea.Y + Math.Max(0, (display.WorkArea.Height - height) / 2)));
                    if (Math.Abs(WindowScale(window, content) - scale) < .001) break;
                }
            }
            catch (Exception) { }
            finally { FinishPlacement(window, presenter, restoreMaximized, foreground); }
        }

        private static bool RestoreForPlacement(Window window, out OverlappedPresenter? presenter)
        {
            presenter = window.AppWindow.Presenter as OverlappedPresenter;
            if (presenter == null) return false;
            bool maximized = presenter.State == OverlappedPresenterState.Maximized;
            bool minimized = presenter.State == OverlappedPresenterState.Minimized;
            if (minimized)
            {
                var placement = new NativeWindowPlacement { Length = Marshal.SizeOf<NativeWindowPlacement>() };
                // Windows remembers whether a minimized window should return maximized. Its
                // live Size is not its restored geometry and must not become new restore bounds.
                if (GetWindowPlacement(WinRT.Interop.WindowNative.GetWindowHandle(window), ref placement))
                    maximized = (placement.Flags & 2) != 0; // WPF_RESTORETOMAXIMIZED
            }
            if (maximized || minimized)
            {
                presenter.Restore(false);
                if (presenter.State == OverlappedPresenterState.Maximized) presenter.Restore(false);
            }
            return maximized;
        }

        private static void FinishPlacement(Window window, OverlappedPresenter? presenter,
            bool restoreMaximized, nint foreground)
        {
            try
            {
                // Maximize has no nonactivating overload. Keep the documented presenter state
                // transition and restore prior foreground if that transition activated this HWND;
                // the caller separately activates the explicitly requested tool after placement.
                if (restoreMaximized) presenter?.Maximize();
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                if (foreground != 0 && foreground != hwnd && WindowInputFocus.Foreground == hwnd)
                    WindowInputFocus.TryActivate(foreground);
            }
            catch (Exception) { }
        }

        private static double WindowScale(Window window, FrameworkElement content)
        {
            uint dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window));
            return dpi > 0 ? dpi / 96.0 : content.XamlRoot?.RasterizationScale ?? 1.0;
        }
    }
}
