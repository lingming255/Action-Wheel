using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Action_Wheel.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Action_Wheel.Overlay
{
    public sealed partial class RadialMenu
    {
        internal static async Task<object> RunOpeningProbeRoundAsync(int round,
            RingAppearance appearance, ReclaimAnimationSettings reclaimSettings,
            IReadOnlyList<long>? previousNativeHostHandles = null)
        {
            var monitors = PositionProbeReadMonitors();
            var monitor = monitors[round % monitors.Count];
            var bounds = new RectInt32(monitor.Bounds.left, monitor.Bounds.top,
                monitor.Bounds.right - monitor.Bounds.left, monitor.Bounds.bottom - monitor.Bounds.top);
            var origin = new PointInt32(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            RadialMenu? menu = null;
            RefreshReclaimIntro? intro = null;
            ScreenGradeSurface? grade = null;
            var stages = new Dictionary<string, double>();
            var samples = new List<(string Phase, double Milliseconds)>();
            var failures = new List<string>();
            var nativeSnapshots = new Dictionary<string, ScreenGradeSurface.NativeCallDiagnostics>();
            var nativeDeltas = new Dictionary<string, object>();
            object? nativeTimings = null;
            long[] nativeHosts = Array.Empty<long>();
            long[] initialOrder = Array.Empty<long>();
            bool? reusedPreviousHosts = null;
            bool hostsRetainedHiddenAfterReturn = false;
            var clock = Stopwatch.StartNew();
            string phase = "baseline";
            double previousTick = clock.Elapsed.TotalMilliseconds;
            int applyCount = 0, manualLoadedCalls = 0;
            bool gradeCloaked = false, menuCloaked = false;
            DispatcherQueue queue = DispatcherQueue.GetForCurrentThread()
                ?? throw new InvalidOperationException("Opening diagnostics require a UI dispatcher.");
            var sampler = queue.CreateTimer();
            sampler.Interval = TimeSpan.FromMilliseconds(16);
            sampler.IsRepeating = true;
            void OnSample(DispatcherQueueTimer sender, object args)
            {
                double now = clock.Elapsed.TotalMilliseconds;
                samples.Add((phase, now - previousTick));
                previousTick = now;
            }
            sampler.Tick += OnSample;

            async Task MeasureAsync(string name, Action action)
            {
                phase = name;
                var stage = Stopwatch.StartNew();
                action();
                stages[name] = stage.Elapsed.TotalMilliseconds;
                // Let the queued sampler observe this stage's blocking interval before relabeling.
                await Task.Delay(24);
            }
            void Cloak(nint hwnd)
            {
                int cloak = 1;
                if (DwmSetWindowAttribute(hwnd, PositionProbeCloakAttribute, ref cloak, sizeof(int)) != 0)
                    throw new InvalidOperationException("A diagnostic HWND could not be cloaked before show.");
                PositionProbeRequireCloaked(hwnd);
            }
            try
            {
                // This is idle-time preallocation, deliberately outside the opening dispatcher
                // sample window. Later rounds still call Warm so its cache-hit cost is visible.
                nativeSnapshots["BeforeWarm"] = ScreenGradeSurface.GetNativeCallDiagnostics();
                var warmClock = Stopwatch.StartNew();
                bool warmed = ScreenGradeSurface.Warm();
                stages["WarmPreallocationMs"] = warmClock.Elapsed.TotalMilliseconds;
                nativeSnapshots["AfterWarm"] = ScreenGradeSurface.GetNativeCallDiagnostics();
                nativeDeltas["Warm"] = OpeningNativeDelta(nativeSnapshots["BeforeWarm"], nativeSnapshots["AfterWarm"]);
                if (!warmed) throw new InvalidOperationException("Native cache warm failed: " + ScreenGradeSurface.LastFailure);
                clock.Restart();
                previousTick = 0;
                sampler.Start();
                await Task.Delay(55);
                await MeasureAsync("MenuConstructionMs", () =>
                {
                    menu = new RadialMenu(ActionConfig.Defaults(), appearance);
                    // No await can let the normal Loaded callback run before it is removed.
                    // Invoke it exactly once below so its component cost has a clear boundary.
                    menu.RootGrid.Loaded -= menu.RadialMenu_Loaded;
                    Cloak(menu._windowHandle);
                    menuCloaked = true;
                });
                await MeasureAsync("ShowAtPositionMs", () =>
                {
                    menu!.ShowAtPosition(origin);
                    PositionProbeRequireCloaked(menu._windowHandle);
                });
                await MeasureAsync("ManualLoadedAndLayoutMs", () =>
                {
                    menu!.RootGrid.Measure(new Windows.Foundation.Size(menu.MenuSize, menu.MenuSize));
                    menu.RootGrid.Arrange(new Windows.Foundation.Rect(0, 0, menu.MenuSize, menu.MenuSize));
                    menu.RootGrid.UpdateLayout();
                    menu.RadialMenu_Loaded(null, new RoutedEventArgs());
                    manualLoadedCalls++;
                    menu.RootGrid.UpdateLayout();
                    PositionProbeRequireCloaked(menu._windowHandle);
                });
                await MeasureAsync("ActualMonitorHiddenReclaimConstructionMs", () =>
                {
                    intro = RefreshReclaimIntro.CreateHiddenForProbe(bounds, origin, menu!._windowHandle, reclaimSettings);
                });
                await MeasureAsync("NativeGradeTryCreateMs", () =>
                {
                    nativeSnapshots["BeforeRent"] = ScreenGradeSurface.GetNativeCallDiagnostics();
                    grade = ScreenGradeSurface.TryCreate(menu!._windowHandle)
                        ?? throw new InvalidOperationException("Native grade creation failed: " + ScreenGradeSurface.LastFailure);
                    grade.ResetTimingDiagnostics();
                    grade.MeasureTimings = true;
                    nativeSnapshots["AfterRent"] = ScreenGradeSurface.GetNativeCallDiagnostics();
                    nativeDeltas["Rent"] = OpeningNativeDelta(nativeSnapshots["BeforeRent"], nativeSnapshots["AfterRent"]);
                    nativeHosts = grade.WindowHandles.Select(hwnd => hwnd.ToInt64()).ToArray();
                    var before = nativeSnapshots["BeforeRent"];
                    var after = nativeSnapshots["AfterRent"];
                    if (after.CacheRents - before.CacheRents != 1 || after.CreateHost != before.CreateHost
                        || after.CreateMagnifier != before.CreateMagnifier || after.Initialize != before.Initialize)
                        failures.Add("The gesture did not rent its preallocated native hosts/session.");
                    if (previousNativeHostHandles != null)
                    {
                        reusedPreviousHosts = nativeHosts.SequenceEqual(previousNativeHostHandles);
                        if (reusedPreviousHosts != true) failures.Add("The next gesture did not reuse the returned native host handles.");
                    }
                    // TryCreate does not reveal hosts. Cloak the whole group before the first Apply.
                    foreach (nint hwnd in grade.WindowHandles) Cloak(hwnd);
                    gradeCloaked = true;
                });
                await MeasureAsync("NativeGradeFirstIdentityApplyMs", () =>
                {
                    nativeSnapshots["BeforeFirstApply"] = ScreenGradeSurface.GetNativeCallDiagnostics();
                    if (!grade!.Apply(1)) throw new InvalidOperationException("Native identity apply failed.");
                    foreach (nint hwnd in grade.WindowHandles) PositionProbeRequireCloaked(hwnd);
                    nativeSnapshots["AfterFirstApply"] = ScreenGradeSurface.GetNativeCallDiagnostics();
                    nativeDeltas["FirstApply"] = OpeningNativeDelta(nativeSnapshots["BeforeFirstApply"], nativeSnapshots["AfterFirstApply"]);
                    if (nativeSnapshots["AfterFirstApply"].ShowWindow - nativeSnapshots["BeforeFirstApply"].ShowWindow != nativeHosts.Length)
                        failures.Add("The gesture did not show each native host exactly once.");
                    initialOrder = grade.WindowHandles.Select(hwnd => OpeningProbeGetWindow(hwnd, 3).ToInt64()).ToArray();
                });
                phase = "gradual-grey";
                var transitionClock = Stopwatch.StartNew();
                double duration = reclaimSettings.DurationMs;
                double totalApplyMs = 0, maximumApplyMs = 0;
                while (transitionClock.Elapsed.TotalMilliseconds < duration)
                {
                    float saturation = ScreenGrade.GradeSaturationAt(transitionClock.Elapsed.TotalMilliseconds, duration);
                    var applyClock = Stopwatch.StartNew();
                    menu!.ApplyGlassSaturation(saturation);
                    intro?.ApplySaturation(saturation);
                    if (!grade!.Apply(saturation)) throw new InvalidOperationException("Native gradual apply failed.");
                    double elapsed = applyClock.Elapsed.TotalMilliseconds;
                    totalApplyMs += elapsed;
                    maximumApplyMs = Math.Max(maximumApplyMs, elapsed);
                    applyCount++;
                    await Task.Delay(16);
                }
                menu!.ApplyGlassSaturation(0);
                intro?.ApplySaturation(0);
                if (!grade!.Apply(0)) throw new InvalidOperationException("Native endpoint apply failed.");
                stages["GradualGreyWallMs"] = transitionClock.Elapsed.TotalMilliseconds;
                stages["GradualGreyTotalApplyMs"] = totalApplyMs;
                stages["GradualGreyMaximumApplyMs"] = maximumApplyMs;
                nativeSnapshots["AfterGradual"] = ScreenGradeSurface.GetNativeCallDiagnostics();
                nativeDeltas["RepeatedApplyAndLiveTicks"] = OpeningNativeDelta(nativeSnapshots["AfterFirstApply"], nativeSnapshots["AfterGradual"]);
                phase = "held-grey-live-refresh";
                var holdClock = Stopwatch.StartNew();
                await Task.Delay(300);
                stages["HeldGreyWallMs"] = holdClock.Elapsed.TotalMilliseconds;
                nativeSnapshots["AfterHeld"] = ScreenGradeSurface.GetNativeCallDiagnostics();
                nativeDeltas["HeldGreyLiveTicks"] = OpeningNativeDelta(nativeSnapshots["AfterGradual"], nativeSnapshots["AfterHeld"]);
                if (nativeSnapshots["AfterHeld"].ShowWindow != nativeSnapshots["AfterFirstApply"].ShowWindow
                    || nativeSnapshots["AfterHeld"].SetWindowPos != nativeSnapshots["AfterFirstApply"].SetWindowPos)
                    failures.Add("Repeated Apply or steady live ticks redundantly showed/reordered native hosts.");
                if (!initialOrder.SequenceEqual(grade.WindowHandles.Select(hwnd => OpeningProbeGetWindow(hwnd, 3).ToInt64())))
                    failures.Add("Native host z-order changed while its layer anchor was unchanged.");
                if (nativeSnapshots["AfterHeld"].SetWindowSource <= nativeSnapshots["AfterGradual"].SetWindowSource)
                    failures.Add("The native held-grey source did not continue refreshing.");
                PositionProbeRequireCloaked(menu._windowHandle);
                foreach (nint hwnd in grade.WindowHandles) PositionProbeRequireCloaked(hwnd);
            }
            catch (Exception exception) { failures.Add(exception.GetType().Name + ": " + exception.Message); }
            finally
            {
                phase = "cleanup";
                var cleanup = Stopwatch.StartNew();
                // Production cleanup returns the hidden native hosts/session for the next round;
                // only the wrapper's final cleanup destroys the cache. Reclaim stays cold/hidden.
                try
                {
                    nativeTimings = grade?.GetTimingDiagnostics();
                    if (grade != null) grade.MeasureTimings = false;
                    nativeSnapshots["BeforeReturn"] = ScreenGradeSurface.GetNativeCallDiagnostics();
                    grade?.ReturnToCache();
                    nativeSnapshots["AfterReturn"] = ScreenGradeSurface.GetNativeCallDiagnostics();
                    nativeDeltas["Return"] = OpeningNativeDelta(nativeSnapshots["BeforeReturn"], nativeSnapshots["AfterReturn"]);
                    if (grade != null)
                    {
                        hostsRetainedHiddenAfterReturn = nativeHosts.Length > 0
                            && nativeHosts.All(hwnd => OpeningProbeIsWindow(new nint(hwnd)) && !OpeningProbeIsWindowVisible(new nint(hwnd)))
                            && grade.WindowHandles.Select(hwnd => hwnd.ToInt64()).SequenceEqual(nativeHosts);
                        var before = nativeSnapshots["BeforeReturn"];
                        var after = nativeSnapshots["AfterReturn"];
                        if (!hostsRetainedHiddenAfterReturn || !after.HasIdleGroup
                            || after.CacheReturns - before.CacheReturns != 1
                            || after.Uninitialize != before.Uninitialize || after.Initialize != before.Initialize
                            || after.CreateHost != before.CreateHost || after.CreateMagnifier != before.CreateMagnifier)
                            failures.Add("Native return destroyed/recreated its cached hosts/session or left a host visible.");
                        foreach (nint hwnd in grade.WindowHandles) PositionProbeRequireCloaked(hwnd);
                    }
                    if (nativeSnapshots.TryGetValue("AfterWarm", out var gestureStart))
                        nativeDeltas["GestureExcludingWarm"] = OpeningNativeDelta(gestureStart, nativeSnapshots["AfterReturn"]);
                }
                catch (Exception exception) { failures.Add("Grade cache return: " + exception.Message); }
                try { intro?.Dispose(); } catch (Exception exception) { failures.Add("Reclaim cleanup: " + exception.Message); }
                try { if (menu is { _closed: false }) menu.CloseMenu(); }
                catch (Exception exception) { failures.Add("Menu cleanup: " + exception.Message); }
                stages["CleanupMs"] = cleanup.Elapsed.TotalMilliseconds;
                await Task.Delay(24);
                sampler.Stop();
                sampler.Tick -= OnSample;
            }
            return new
            {
                Passed = failures.Count == 0, Round = round + 1, Failures = failures, Stages = stages,
                Monitor = new { monitor.Index, monitor.Primary, monitor.Dpi, bounds.X, bounds.Y, bounds.Width, bounds.Height },
                ActualReclaimSettings = reclaimSettings, ManualLoadedCalls = manualLoadedCalls,
                ReclaimPath = "Cold hidden construction; reclaim cache is not exercised by this revision.",
                MenuCloaked = menuCloaked, GradeCloaked = gradeCloaked, GradualApplyCount = applyCount,
                NativeCache = new
                {
                    HostsAfterRent = nativeHosts, ReusedPreviousRoundHosts = reusedPreviousHosts,
                    HostsRetainedHiddenAfterReturn = hostsRetainedHiddenAfterReturn,
                    WarmExcludedFromOpeningDispatcherSamples = true,
                    Snapshots = nativeSnapshots, Deltas = nativeDeltas,
                    Timings = nativeTimings,
                },
                Dispatcher = SummariseOpeningSamples(samples.Select(sample => sample.Milliseconds)),
                DispatcherByPhase = samples.GroupBy(sample => sample.Phase).Select(group => new
                {
                    Phase = group.Key, Timing = SummariseOpeningSamples(group.Select(sample => sample.Milliseconds)),
                }).ToArray(),
                ScreenPixelsReadBack = false, DisplayedEffect = false,
                Boundary = "Menu constructor/ShowAtPosition are production methods. Loaded is unsubscribed before show and called once manually with forced layout. Reclaim uses real monitor geometry/settings but remains hidden and cold. Native all-monitor Warm runs before opening dispatcher sampling; production TryCreate rents the cache and ReturnToCache preserves it between rounds. Every native host is cloaked before show. These are component/UI timings, not real input-to-presentation latency or on-screen animation frame times.",
            };
        }

        private static object OpeningNativeDelta(ScreenGradeSurface.NativeCallDiagnostics before,
            ScreenGradeSurface.NativeCallDiagnostics after) => new
        {
            Initialize = after.Initialize - before.Initialize,
            Uninitialize = after.Uninitialize - before.Uninitialize,
            CreateHost = after.CreateHost - before.CreateHost,
            CreateMagnifier = after.CreateMagnifier - before.CreateMagnifier,
            SetColorEffect = after.SetColorEffect - before.SetColorEffect,
            SetWindowSource = after.SetWindowSource - before.SetWindowSource,
            Invalidate = after.Invalidate - before.Invalidate,
            ShowWindow = after.ShowWindow - before.ShowWindow,
            HideWindow = after.HideWindow - before.HideWindow,
            SetWindowPos = after.SetWindowPos - before.SetWindowPos,
            SetFilterList = after.SetFilterList - before.SetFilterList,
            CacheRents = after.CacheRents - before.CacheRents,
            CacheReturns = after.CacheReturns - before.CacheReturns,
            CacheWarms = after.CacheWarms - before.CacheWarms,
            SessionUsersBefore = before.SessionUsers, SessionUsersAfter = after.SessionUsers,
            HadIdleGroupBefore = before.HasIdleGroup, HasIdleGroupAfter = after.HasIdleGroup,
        };

        [DllImport("user32.dll", EntryPoint = "IsWindow")]
        private static extern bool OpeningProbeIsWindow(nint hwnd);
        [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
        private static extern bool OpeningProbeIsWindowVisible(nint hwnd);
        [DllImport("user32.dll", EntryPoint = "GetWindow")]
        private static extern nint OpeningProbeGetWindow(nint hwnd, uint command);

        private static object SummariseOpeningSamples(IEnumerable<double> samples)
        {
            double[] sorted = samples.OrderBy(value => value).ToArray();
            return new
            {
                TargetIntervalMs = 16, Count = sorted.Length,
                MedianMs = sorted.Length == 0 ? 0 : sorted[sorted.Length / 2],
                P95Ms = sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * .95) - 1)],
                MaximumMs = sorted.Length == 0 ? 0 : sorted[^1],
                IntervalsOver25Ms = sorted.Count(value => value > 25),
                IntervalsOver33Ms = sorted.Count(value => value > 33),
            };
        }
    }
}
