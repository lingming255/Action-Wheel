using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Action_Wheel.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace Action_Wheel.Overlay
{
    public sealed partial class RadialMenu
    {
        /// <summary>Exercise real HWND ownership with only disposable windows owned by this probe.</summary>
        internal static async Task<object> RunInputProbeAsync()
        {
            nint previous = WindowInputFocus.Foreground;
            Window? owner = null, replacement = null;
            RadialMenu? menu = null;
            int checks = 0;
            void Require(bool value, string message)
            {
                checks++;
                if (!value) throw new InvalidOperationException(message + " "
                    + "Owner[" + DescribeInputWindow(owner == null ? 0 : WindowNative.GetWindowHandle(owner)) + "] "
                    + "Menu[" + DescribeInputWindow(menu?._windowHandle ?? 0) + "] "
                    + "Replacement[" + DescribeInputWindow(replacement == null ? 0 : WindowNative.GetWindowHandle(replacement)) + "] "
                    + "Foreground[" + DescribeInputWindow(WindowInputFocus.Foreground) + "]");
            }
            try
            {
                owner = new Window
                {
                    Title = "Action Wheel input check",
                    Content = new TextBox { Text = "Disposable input target", Margin = new Thickness(24) },
                };
                owner.AppWindow.Resize(new SizeInt32(420, 220));
                owner.Activate();
                // STARTUPINFO may specify SW_HIDE for a diagnostic process. Activate alone can
                // leave its first HWND hidden; make visibility explicit before testing focus.
                owner.AppWindow.Show();
                nint target = WindowNative.GetWindowHandle(owner);
                Require(WindowInputFocus.TryActivate(target), "The disposable original target could not be activated.");
                var position = new PointInt32(owner.AppWindow.Position.X + 210, owner.AppWindow.Position.Y + 110);

                menu = new RadialMenu { ActionTargetWindow = target };
                menu.ShowAtPosition(position);
                await Task.Delay(100);
                Require(menu.OwnsInput && WindowInputFocus.Foreground == menu._windowHandle,
                    "The interactive wheel did not own the foreground.");
                Require((GetWindowLong(menu._windowHandle, GWL_EXSTYLE) & (WS_EX_NOACTIVATE | WS_EX_TRANSPARENT)) == 0,
                    "The interactive wheel retained a no-activate or click-through style.");
                menu.DismissMenu();
                Require(!menu.OwnsInput && WindowInputFocus.Foreground == target,
                    "Dismissing the wheel did not return the foreground immediately.");
                await Task.Delay(250);
                if (!menu._closed) menu.CloseMenu();
                menu = null;

                menu = new RadialMenu { ActionTargetWindow = target };
                menu.ShowAtPosition(position);
                await Task.Delay(80);
                Require(WindowInputFocus.Foreground == menu._windowHandle, "The second wheel did not acquire focus.");
                menu.BeginRefreshFeedback(position);
                Require(!menu.OwnsInput && WindowInputFocus.Foreground == target,
                    "The released refresh decoration retained foreground ownership.");
                int decorative = GetWindowLong(menu._windowHandle, GWL_EXSTYLE);
                Require((decorative & (WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | 0x80000))
                    == (WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | 0x80000),
                    "The refresh decoration did not become layered, transparent and non-activating.");
                menu.CloseMenu();
                menu = null;

                menu = new RadialMenu { ActionTargetWindow = target };
                menu.ShowAtPosition(position);
                await Task.Delay(80);
                replacement = new Window { Title = "Action Wheel explicit focus change", Content = new Grid() };
                replacement.AppWindow.Resize(new SizeInt32(240, 120));
                replacement.Activate();
                replacement.AppWindow.Show();
                nint replacementTarget = WindowNative.GetWindowHandle(replacement);
                Require(WindowInputFocus.TryActivate(replacementTarget), "The replacement foreground could not be activated.");
                menu.CloseMenu();
                menu = null;
                Require(WindowInputFocus.Foreground == replacementTarget,
                    "A closing wheel overrode an explicit intervening focus change.");
                return new
                {
                    Passed = true, Checks = checks, ActualHwndFocus = true,
                    InteractiveNoClickThrough = true, DismissReturnsFocus = true,
                    RefreshReturnsFocus = true, RefreshClickThrough = true,
                    ExplicitFocusChangePreserved = true, InstalledHooks = false, UsedSendInput = false,
                };
            }
            finally
            {
                menu?.CloseMenu();
                replacement?.Close();
                owner?.Close();
                WindowInputFocus.TryActivate(previous);
            }
        }

        private static string DescribeInputWindow(nint hwnd)
        {
            if (hwnd == 0) return "HWND=0";
            uint thread = InputProbeGetWindowThreadProcessId(hwnd, out uint process);
            return $"HWND=0x{hwnd:X}, visible={InputProbeIsWindowVisible(hwnd)}, "
                + $"iconic={InputProbeIsIconic(hwnd)}, style=0x{GetWindowLong(hwnd, GWL_STYLE):X8}, "
                + $"exStyle=0x{GetWindowLong(hwnd, GWL_EXSTYLE):X8}, thread={thread}, process={process}";
        }

        [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
        private static extern bool InputProbeIsWindowVisible(nint hwnd);
        [DllImport("user32.dll", EntryPoint = "IsIconic")]
        private static extern bool InputProbeIsIconic(nint hwnd);
        [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
        private static extern uint InputProbeGetWindowThreadProcessId(nint hwnd, out uint process);
    }
}
