using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Action_Wheel.Overlay;
using WinRT.Interop;

namespace Action_Wheel.Services
{
    public partial class LauncherService
    {
        /// <summary>
        /// Observe the real delayed dispatch path with one unshown wheel reused as two senders.
        /// This fixture never starts hooks, connects App, launches a process or opens a tool.
        /// </summary>
        internal static async Task<object> RunPlacementDispatchProbeAsync()
        {
            var failures = new List<string>();
            var received = new List<object?>();
            var receivedGate = new object();
            var bothReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = new WindowPlacementTarget(-1280, 360);
            var second = new WindowPlacementTarget(1920, 540);
            var afterDispatch = new WindowPlacementTarget(0, 0);
            LauncherService? service = null;
            RadialMenu? menu = null;
            nint hwnd = 0;
            nint originalForeground = WindowInputFocus.Foreground;
            bool bothCompleted = false, exactSnapshots = false, stableAfterReuse = false;
            bool foregroundUnchanged = false, nativeDestroyed = false;
            int firstCount = 0, secondCount = 0, receivedCount = 0;
            void OnNoteBoardRequested(object? sender, EventArgs args)
            {
                lock (receivedGate)
                {
                    received.Add(sender);
                    if (received.Count >= 2) bothReceived.TrySetResult();
                }
            }
            try
            {
                WheelSounds.Configure(WheelSoundSettings.Default with { Enabled = false });
                var appearance = RingAppearance.Default with { Animation = RingAnimation.None };
                var synthetic = Enumerable.Range(0, 9).Select(tag => new ActionItem
                {
                    Tag = tag, Label = $"placement probe {tag}", Kind = ActionKind.None, Glyph = "F00D",
                }).ToArray();
                service = new LauncherService(appearance) { _actions = synthetic };
                menu = new RadialMenu(synthetic, appearance);
                hwnd = WindowNative.GetWindowHandle(menu);
                int cloak = 1;
                if (ReusePoolSetDwmAttribute(hwnd, 13, ref cloak, sizeof(int)) != 0)
                    throw new InvalidOperationException("Placement dispatch fixture could not cloak its unshown window.");
                var action = new ActionItem
                {
                    Label = "observe note board placement", Kind = ActionKind.Function,
                    Value = WindowsFunctions.NoteBoard,
                };
                ActionDispatcher.NoteBoardRequested += OnNoteBoardRequested;

                // Each call must capture before its first await. Changing the same menu's
                // property immediately models renting that HWND for another wheel session.
                menu.ActionPlacementTarget = first;
                service.OnActionInvoked(menu, action);
                menu.ActionPlacementTarget = second;
                service.OnActionInvoked(menu, action);
                bothCompleted = await Task.WhenAny(bothReceived.Task, Task.Delay(1500)) == bothReceived.Task;
                lock (receivedGate)
                {
                    firstCount = received.Count(value => ReferenceEquals(value, first));
                    secondCount = received.Count(value => ReferenceEquals(value, second));
                    exactSnapshots = bothCompleted && received.Count == 2 && firstCount == 1 && secondCount == 1;
                }
                if (!exactSnapshots)
                    failures.Add("Delayed dispatch did not preserve each invocation's original placement identity exactly once.");

                // A later reuse cannot alter an event already published or emit another one.
                menu.ActionPlacementTarget = afterDispatch;
                await Task.Delay(100);
                lock (receivedGate)
                {
                    receivedCount = received.Count;
                    stableAfterReuse = receivedCount == 2
                        && received.Count(value => ReferenceEquals(value, first)) == 1
                        && received.Count(value => ReferenceEquals(value, second)) == 1
                        && received.All(value => !ReferenceEquals(value, afterDispatch));
                }
                if (!stableAfterReuse) failures.Add("A later wheel reuse changed or repeated an earlier placement dispatch.");
                if (service.Status.IsRunning) failures.Add("Placement dispatch fixture unexpectedly started global hooks.");
            }
            catch (Exception exception) { failures.Add(exception.ToString()); }
            finally
            {
                ActionDispatcher.NoteBoardRequested -= OnNoteBoardRequested;
                try { menu?.DestroyMenu(); }
                catch (Exception exception) { failures.Add("Placement wheel cleanup: " + exception.Message); }
                try
                {
                    // The diagnostic constructor subscribes no static dispatcher events. Release
                    // its owned resources without resetting global sound settings for later probes.
                    service?.Stop();
                    service?._configWatcher.Dispose();
                    service?._hookHealthTimer.Stop();
                    service?._mouseHook.Dispose();
                    service?._keyboardHook.Dispose();
                }
                catch (Exception exception) { failures.Add("Placement launcher cleanup: " + exception.Message); }
                await Task.Delay(50);
                nativeDestroyed = hwnd == 0 || !ReusePoolIsWindow(hwnd);
                if (!nativeDestroyed) failures.Add("Placement dispatch fixture retained its native window.");
                foregroundUnchanged = WindowInputFocus.Foreground == originalForeground;
                if (!foregroundUnchanged) failures.Add("Unshown placement dispatch fixture changed the foreground window.");
            }
            return new
            {
                Passed = failures.Count == 0, BothDispatchesCompleted = bothCompleted,
                FirstPlacementCount = firstCount, SecondPlacementCount = secondCount,
                ReceivedEventCount = receivedCount, ExactSnapshotIdentities = exactSnapshots,
                StableAfterLaterReuse = stableAfterReuse, ForegroundUnchanged = foregroundUnchanged,
                NativeWindowDestroyed = nativeDestroyed, InstalledHooks = false,
                ExecutedExternalActions = false, OpenedToolWindows = false,
                SavedUserConfiguration = false, Failures = failures,
            };
        }
    }
}
