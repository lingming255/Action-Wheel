using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;

namespace Action_Wheel.Overlay
{
    public sealed partial class RadialMenu
    {
        private bool _refreshHandoffCloaked;
        private bool _refreshHandoffHidden;
        private DispatcherQueueTimer? _refreshHandoffTimer;

        /// <summary>
        /// Native geometry changes immediately; collapsing XAML does not. Conceal the old
        /// swapchain before moving it to the monitor origin so its last wheel frame cannot flash
        /// in the corner while WinUI lays out the fullscreen cover.
        /// </summary>
        private void ConcealRefreshHandoff()
        {
            if (RefreshHandoffGetDwmAttribute(_windowHandle, 14, out int existing, sizeof(int)) == 0
                && existing != 0) return; // Preserve a diagnostic/system cloak we do not own.
            int cloak = 1;
            if (DwmSetWindowAttribute(_windowHandle, 13, ref cloak, sizeof(int)) == 0)
            {
                _refreshHandoffCloaked = true;
                return;
            }
            // Cloaking is optional on older/unsupported desktop compositions. Keep an already
            // hidden diagnostic hidden, and never activate the fallback when it is revealed.
            if (RefreshHandoffIsWindowVisible(_windowHandle))
            {
                _refreshHandoffHidden = true;
                _appWindow?.Hide();
                ShowWindow(_windowHandle, SW_HIDE);
            }
        }

        private bool HasRefreshHandoffBarrier => _refreshHandoffCloaked || _refreshHandoffHidden;

        private void RevealRefreshHandoff()
        {
            if (_refreshHandoffCloaked)
            {
                int cloak = 0;
                if (DwmSetWindowAttribute(_windowHandle, 13, ref cloak, sizeof(int)) != 0)
                    throw new InvalidOperationException("The refresh cover could not be revealed.");
                _refreshHandoffCloaked = false;
            }
            if (_refreshHandoffHidden)
            {
                _refreshHandoffHidden = false;
                _appWindow?.Show(false);
            }
        }

        private void WatchRefreshHandoff(Action restoreColour)
        {
            var timer = DispatcherQueue.CreateTimer();
            timer.IsRepeating = false;
            timer.Interval = TimeSpan.FromMilliseconds(1000);
            timer.Tick += (_, _) =>
            {
                StopRefreshHandoffWatch();
                if (_closed || _refreshOnRendering == null) return;
                CompositionTarget.Rendering -= _refreshOnRendering;
                _refreshOnRendering = null;
                // An occluded/stalled compositor may not tick. Abandon this decoration safely;
                // a timeout must never reveal the old wheel frame or strand desktop grading.
                try { restoreColour(); }
                finally { CloseMenu(); }
            };
            _refreshHandoffTimer = timer;
            timer.Start();
        }

        private void StopRefreshHandoffWatch()
        {
            _refreshHandoffTimer?.Stop();
            _refreshHandoffTimer = null;
        }

        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
        private static extern int RefreshHandoffGetDwmAttribute(nint window, int attribute, out int value, int size);

        [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
        private static extern bool RefreshHandoffIsWindowVisible(nint window);
    }
}
