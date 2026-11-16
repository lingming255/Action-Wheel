using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Action_Wheel.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Action_Wheel.Overlay
{
    public sealed partial class RadialMenu
    {
        // A never-shown cold window cannot catch reuse-specific layout or opening callbacks.
        // Exercise the full centre-release transition on a rented, previously used wheel instead.
        // DWM cloaking stays enabled throughout; no input, desktop capture or screen grading runs.
        private static async Task<object> RunReusedReleaseProbeAsync()
        {
            WheelSounds.Configure(WheelSoundSettings.Default with { Enabled = false });
            var rounds = new List<object>();
            var monitor = PositionProbeReadMonitors().Last();
            nint originalForeground = WindowInputFocus.Foreground;
            void RestoreForeground()
            {
                if (originalForeground != 0) WindowInputFocus.TryActivate(originalForeground);
            }
            void Cloak(RadialMenu value)
            {
                int cloak = 1;
                RequireReleaseProbe(DwmSetWindowAttribute(value._windowHandle, 13, ref cloak, sizeof(int)) == 0,
                    "The reused release window could not be cloaked.");
                PositionProbeRequireCloaked(value._windowHandle);
            }
            foreach (bool quickRelease in new[] { true, false })
            {
                RadialMenu? menu = null, spare = null;
                RefreshFrame? frame = null;
                var samples = new List<object>();
                int handoffs = 0;
                try
                {
                    var appearance = RingAppearance.Default with
                    {
                        Animation = RingAnimation.Fade, AnimationDurationMs = 120,
                    };
                    menu = new RadialMenu(ActionConfig.Defaults(), appearance)
                    {
                        ActionTargetWindow = originalForeground,
                    };
                    Cloak(menu);
                    menu.PrepareForReuse();
                    Cloak(menu);
                    var release = new PointInt32(monitor.Bounds.left + 320, monitor.Bounds.top + 180);
                    menu.ShowAtPosition(release);
                    RestoreForeground();
                    await Task.Delay(180);
                    menu.CloseMenu();
                    await Task.Delay(50);
                    RequireReleaseProbe(menu.IsParked && menu.CanReuse && !IsReleaseProbeWindowVisible(menu._windowHandle),
                        "A reused release setup did not park its first ordinary session.");
                    menu.ShowAtPosition(release);
                    RestoreForeground();
                    if (!quickRelease) await Task.Delay(180);
                    frame = CreateReleaseProbeFrame(600,
                        new RectInt32(monitor.Bounds.left, monitor.Bounds.top, 640, 360));
                    menu.PrepareRefreshPresentation(frame);
                    menu.BeginRefreshFeedback(release);
                    Sample("feedback");
                    await menu.WaitForRefreshFeedbackAsync();
                    frame.PrepareRelease(release);
                    var ready = frame;
                    menu.PlayRefresh(ready, () =>
                    {
                        menu.DetachBorrowedRefreshPresentation();
                        ready.Dispose();
                        handoffs++;
                    });
                    Sample("immediate full scene");
                    // Hidden/occluded targets do not reliably receive Rendering. These are the
                    // same three explicit cover commits used by the existing release fixture.
                    for (int callback = 0; callback < 3; callback++)
                        menu._refreshOnRendering?.Invoke(null, null!);
                    RequireReleaseProbe(handoffs == 1, "A reused centre release did not hand off exactly once.");
                    Sample("cover committed");

                    spare = new RadialMenu(ActionConfig.Defaults(), appearance);
                    Cloak(spare);
                    spare.PrepareForReuse();
                    Cloak(spare);
                    RequireReleaseProbe(spare.IsParked && !IsReleaseProbeWindowVisible(spare._windowHandle),
                        "A spare prepared during outward playback is natively visible.");
                    foreach (int delay in new[] { 35, 100, 180 })
                    {
                        await Task.Delay(delay);
                        Sample("after spare " + delay);
                    }
                    RequireReleaseProbe(menu._gpuRefreshPlayback != null || menu._refreshPlayback != null,
                        "The reused centre release lost both playback backends before its deadline.");
                    nint owner = menu._windowHandle;
                    await Task.Delay(500);
                    RequireReleaseProbe(menu._closed && !PositionProbeIsWindow(owner) && !menu.IsParked,
                        "The reused fullscreen owner survived its refresh deadline or returned to the pool.");
                    rounds.Add(new
                    {
                        QuickRelease = quickRelease, Passed = true, Handoffs = handoffs,
                        Samples = samples, OwnerDestroyed = true, SpareStayedHidden = true,
                    });

                    void Sample(string phase)
                    {
                        RadialMenu current = menu!;
                        PositionProbeGetWindowRect(current._windowHandle, out var rect);
                        PositionProbeGetDwmAttribute(current._windowHandle, 14, out int cloaked, sizeof(int));
                        bool collapsed = current.MenuContainer.Visibility == Visibility.Collapsed;
                        bool glassDetached = current._glassSurface == null;
                        samples.Add(new
                        {
                            Phase = phase, MenuCollapsed = collapsed, MenuOpacity = current.MenuContainer.Opacity,
                            GlassDetached = glassDetached, current.IsRefreshPlaying, current.IsParked,
                            NativeVisible = IsReleaseProbeWindowVisible(current._windowHandle), Cloaked = cloaked,
                            Left = rect.left, Top = rect.top, Width = rect.right - rect.left, Height = rect.bottom - rect.top,
                        });
                        RequireReleaseProbe(collapsed && glassDetached && current.IsRefreshPlaying && !current.IsParked,
                            "A reused centre-release owner exposed wheel visuals during " + phase + ".");
                        PositionProbeRequireCloaked(current._windowHandle);
                    }
                }
                finally
                {
                    try { if (menu is { _closed: false }) menu.DestroyMenu(); }
                    finally
                    {
                        if (spare is { _closed: false }) spare.DestroyMenu();
                        frame?.Dispose();
                        RestoreForeground();
                    }
                }
            }
            return new { Passed = true, Rounds = rounds, UsedDesktopCapture = false, SimulatedInput = false };
        }

        private sealed record ReleaseHandoffProbeResult(bool Passed, List<object> Stages, List<string> Failures);

        // Remove only our diagnostic cloak while the native HWND is already hidden. The real
        // handoff must then supply its own compositor barrier before moving the previous wheel
        // surface to the monitor origin. No window is shown during this independent check.
        private static async Task<ReleaseHandoffProbeResult> RunReusedReleaseHandoffProbeAsync()
        {
            WheelSounds.Configure(WheelSoundSettings.Default with { Enabled = false });
            var stages = new List<object>();
            var failures = new List<string>();
            RadialMenu? menu = null;
            RefreshFrame? frame = null;
            nint originalForeground = WindowInputFocus.Foreground;
            int handoffs = 0;
            void Check(bool value, string message)
            {
                if (!value) failures.Add(message);
            }
            void RestoreForeground()
            {
                if (originalForeground != 0) WindowInputFocus.TryActivate(originalForeground);
            }
            try
            {
                var monitor = PositionProbeReadMonitors().Last();
                var point = new PointInt32(monitor.Bounds.left + 320, monitor.Bounds.top + 180);
                var appearance = RingAppearance.Default with { Animation = RingAnimation.Fade, AnimationDurationMs = 120 };
                menu = new RadialMenu(ActionConfig.Defaults(), appearance) { ActionTargetWindow = originalForeground };
                int cloak = 1;
                RequireReleaseProbe(DwmSetWindowAttribute(menu._windowHandle, 13, ref cloak, sizeof(int)) == 0,
                    "The native handoff fixture could not cloak its window.");
                menu.PrepareForReuse();
                cloak = 1;
                DwmSetWindowAttribute(menu._windowHandle, 13, ref cloak, sizeof(int));
                menu.ShowAtPosition(point);
                RestoreForeground();
                await Task.Delay(150);
                menu.CloseMenu();
                menu.ShowAtPosition(point);
                RestoreForeground();
                ShowWindow(menu._windowHandle, SW_HIDE);
                RequireReleaseProbe(!IsReleaseProbeWindowVisible(menu._windowHandle),
                    "The native handoff fixture is not hidden before releasing the diagnostic cloak.");
                cloak = 0;
                RequireReleaseProbe(DwmSetWindowAttribute(menu._windowHandle, 13, ref cloak, sizeof(int)) == 0,
                    "The native handoff fixture could not remove its diagnostic cloak.");
                await Task.Delay(40);
                RequireReleaseProbe(PositionProbeGetDwmAttribute(menu._windowHandle, 14, out int originalCloaked, sizeof(int)) == 0
                    && originalCloaked == 0 && !IsReleaseProbeWindowVisible(menu._windowHandle),
                    "The independent handoff fixture did not begin hidden and uncloaked.");
                frame = CreateReleaseProbeFrame(600,
                    new RectInt32(monitor.Bounds.left, monitor.Bounds.top, 640, 360));
                menu.BeginRefreshFeedback(point);
                frame.PrepareRelease(point);
                var ready = frame;
                menu.PlayRefresh(ready, () => { ready.Dispose(); handoffs++; });
                int afterMutation = Sample("after native move, before Rendering");
                Check(afterMutation != 0,
                    "Fullscreen geometry changed without a native compositor cloak before the first Rendering callback.");
                for (int callback = 0; callback < 3; callback++) menu._refreshOnRendering?.Invoke(null, null!);
                int afterCover = Sample("after three cover commits");
                Check(afterCover == originalCloaked,
                    "The native handoff did not restore the window's original cloak after its prepared cover committed.");
                Check(handoffs == 0,
                    "Colour restored before the newly visible cover had its own commit interval.");
                for (int callback = 0; callback < 3; callback++) menu._refreshOnRendering?.Invoke(null, null!);
                int afterVisibleCover = Sample("after three visible cover commits");
                Check(afterVisibleCover == originalCloaked && handoffs == 1,
                    "The uncloaked cover did not hand off exactly once after its visible commit interval.");
                Check(menu.MenuContainer.Visibility == Visibility.Collapsed && menu._glassSurface == null,
                    "The native handoff restored the wheel's menu or glass during fullscreen playback.");

                int Sample(string phase)
                {
                    RadialMenu current = menu!;
                    PositionProbeGetDwmAttribute(current._windowHandle, 14, out int flags, sizeof(int));
                    PositionProbeGetWindowRect(current._windowHandle, out var rect);
                    bool visible = IsReleaseProbeWindowVisible(current._windowHandle);
                    stages.Add(new
                    {
                        Phase = phase, Cloaked = flags, NativeVisible = visible,
                        MenuCollapsed = current.MenuContainer.Visibility == Visibility.Collapsed,
                        GlassDetached = current._glassSurface == null, Handoffs = handoffs,
                        Left = rect.left, Top = rect.top, Width = rect.right - rect.left, Height = rect.bottom - rect.top,
                    });
                    Check(!visible, "The native handoff fixture showed its hidden window during " + phase + ".");
                    return flags;
                }
            }
            catch (Exception error) { failures.Add(error.ToString()); }
            finally
            {
                if (menu is { _closed: false }) menu.DestroyMenu();
                frame?.Dispose();
                RestoreForeground();
            }
            await RunReleaseHandoffEdgeCasesAsync(stages, failures);
            return new ReleaseHandoffProbeResult(failures.Count == 0, stages, failures);
        }

        private static async Task RunReleaseHandoffEdgeCasesAsync(List<object> stages, List<string> failures)
        {
            foreach (bool stallRendering in new[] { false, true })
            {
                RadialMenu? menu = null;
                RefreshFrame? frame = null;
                int restores = 0;
                string scenario = stallRendering ? "stalled Rendering timeout" : "close during concealed handoff";
                void Check(bool value, string message)
                {
                    if (!value) failures.Add(scenario + ": " + message);
                }
                try
                {
                    menu = new RadialMenu();
                    menu.AppWindow.Hide();
                    menu.AppWindow.Move(new PointInt32(-20000, -20000));
                    menu._screenOrigin = new PointInt32(-20000, -20000);
                    await Task.Delay(120);
                    RequireReleaseProbe(!IsReleaseProbeWindowVisible(menu._windowHandle),
                        "A handoff edge-case fixture became visible before playback.");
                    frame = CreateReleaseProbeFrame(600);
                    var point = new PointInt32(-19680, -19820);
                    menu.BeginRefreshFeedback(point);
                    frame.PrepareRelease(point);
                    menu.PlayRefresh(frame, () => restores++);
                    nint owner = menu._windowHandle;
                    var oldRendering = menu._refreshOnRendering;
                    Check(oldRendering != null && menu._refreshHandoffTimer?.IsRunning == true,
                        "Playback did not install its Rendering callback and handoff watchdog.");
                    Check(menu._refreshHandoffCloaked && !IsReleaseProbeWindowVisible(owner),
                        "The hidden playback owner did not start with an owned native cloak.");
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    if (stallRendering)
                    {
                        // Retain the field, exactly as a compositor that stops producing events
                        // would. Only remove the actual event subscription; the watchdog must
                        // independently converge on restoring colour and destroying this owner.
                        CompositionTarget.Rendering -= oldRendering;
                        while (!menu._closed && clock.ElapsedMilliseconds < 1500)
                            await Task.Delay(25);
                        Check(restores == 1, "The watchdog did not restore colour exactly once.");
                    }
                    else menu.CloseMenu();

                    // A native event already queued when Closed/timeout ran may still call the
                    // captured delegate. It must not reveal the retired HWND or repeat restore.
                    for (int callback = 0; callback < 6; callback++) oldRendering?.Invoke(null, null!);
                    await Task.Delay(35);
                    Check(menu._closed && !PositionProbeIsWindow(owner), "The concealed native owner survived closure.");
                    Check(menu._refreshHandoffTimer == null && menu._refreshOnRendering == null,
                        "Closure retained a handoff watchdog or Rendering callback.");
                    Check(menu._refreshHandoffCloaked && !menu._refreshHandoffHidden,
                        "A late Rendering callback revealed the retired handoff instead of leaving it destroyed.");
                    Check(restores == (stallRendering ? 1 : 0),
                        "A late Rendering callback repeated colour restoration.");
                    stages.Add(new
                    {
                        Phase = scenario, Passed = menu._closed && !PositionProbeIsWindow(owner)
                            && menu._refreshHandoffTimer == null && menu._refreshOnRendering == null
                            && restores == (stallRendering ? 1 : 0) && menu._refreshHandoffCloaked,
                        NativeDestroyed = !PositionProbeIsWindow(owner), Restores = restores,
                        TimerCleared = menu._refreshHandoffTimer == null,
                        RenderingCleared = menu._refreshOnRendering == null,
                        RetiredCloakNeverRevealed = menu._refreshHandoffCloaked,
                        ElapsedMs = clock.Elapsed.TotalMilliseconds,
                    });
                }
                catch (Exception error) { failures.Add(scenario + ": " + error); }
                finally
                {
                    if (menu is { _closed: false }) menu.DestroyMenu();
                    frame?.Dispose();
                }
            }
        }
    }
}
