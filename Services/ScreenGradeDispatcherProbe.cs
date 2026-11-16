using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Action_Wheel.Overlay;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Action_Wheel.Services
{
    /// <summary>Real all-monitor worker sampling with every host cloaked; no desktop pixels are read.</summary>
    internal static class ScreenGradeDispatcherProbe
    {
        internal static async Task<object> RunAsync()
        {
            var initialNative = ScreenGradeSurface.GetNativeCallDiagnostics();
            if (initialNative.ProcessSessionUsers != 0)
                throw new InvalidOperationException("Worker diagnostics require the previous native owner to release its cache.");
            var worker = new ScreenGradeDispatcher(hiddenProbe: true, measureTimings: true);
            var rounds = new List<object>();
            var intervals = new List<double>();
            var queue = DispatcherQueue.GetForCurrentThread()
                ?? throw new InvalidOperationException("Worker diagnostics need the UI dispatcher.");
            var timer = queue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(16);
            timer.IsRepeating = true;
            var clock = Stopwatch.StartNew();
            double previous = 0;
            timer.Tick += (_, _) =>
            {
                double now = clock.Elapsed.TotalMilliseconds;
                if (previous != 0 && intervals.Count < 1024) intervals.Add(now - previous);
                previous = now;
            };
            int uiThread = Environment.CurrentManagedThreadId;
            Window? anchor = null;
            object? shutdown = null;
            int assertions = 0;
            void Require(bool condition, string message)
            {
                assertions++;
                if (!condition) throw new InvalidOperationException(message);
            }
            try
            {
                anchor = new Window();
                anchor.AppWindow.Hide();
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(anchor);
                timer.Start();
                worker.Warm();
                await worker.DrainAsync();
                long[]? hosts = null;
                for (int round = 0; round < 3; round++)
                {
                    long generation = worker.BeginGesture();
                    Require(worker.Apply(generation, hwnd, 0, .5f), "The worker rejected its current generation.");
                    await worker.DrainAsync();
                    await Task.Delay(300);
                    object active = await worker.SnapshotAsync();
                    JsonElement activeJson = JsonSerializer.SerializeToElement(active);
                    Require(activeJson.GetProperty("WorkerThread").GetInt32() != uiThread,
                        "Magnifier sampling still belongs to the UI thread.");
                    Require(activeJson.GetProperty("HasActiveSurface").GetBoolean()
                        && activeJson.GetProperty("HostsCloaked").GetBoolean(),
                        "The worker diagnostic did not remain cloaked: " + activeJson.GetRawText());
                    long[] currentHosts = activeJson.GetProperty("KnownHosts").EnumerateArray().Select(item => item.GetInt64()).ToArray();
                    Require(currentHosts.Length > 0 && (hosts == null || hosts.SequenceEqual(currentHosts)),
                        "The worker did not retain one bounded native host group.");
                    hosts = currentHosts;
                    Require(activeJson.GetProperty("Timings").GetProperty("Tick").GetProperty("Count").GetInt64() > 0,
                        "The native worker source timer never sampled.");

                    var restoreClock = Stopwatch.StartNew();
                    worker.Restore();
                    await worker.LastRestoreTask;
                    double restoreMs = restoreClock.Elapsed.TotalMilliseconds;
                    Require(!worker.Apply(generation, hwnd, 0, 0), "An older generation could reapply grey after restore.");
                    await worker.DrainAsync();
                    await Task.Delay(50);
                    object restored = await worker.SnapshotAsync();
                    JsonElement restoredJson = JsonSerializer.SerializeToElement(restored);
                    Require(restoredJson.GetProperty("HostsHidden").GetBoolean()
                        && !restoredJson.GetProperty("HasActiveSurface").GetBoolean(), "An acknowledged restore left a visible grade host.");
                    rounds.Add(new { Round = round + 1, Active = active, Restored = restored, RestoreAcknowledgedMs = restoreMs });
                }
                // Coalesced, superseded work must not resurrect a retired generation.
                for (int request = 0; request < 20; request++)
                {
                    long generation = worker.BeginGesture();
                    worker.Apply(generation, hwnd, 0, 0);
                    worker.Restore();
                }
                await worker.LastRestoreTask;
                await worker.DrainAsync();
                JsonElement finalRestore = JsonSerializer.SerializeToElement(await worker.SnapshotAsync());
                Require(finalRestore.GetProperty("HostsHidden").GetBoolean()
                    && !finalRestore.GetProperty("HasActiveSurface").GetBoolean(), "Superseded applies survived the final restore.");
            }
            finally
            {
                timer.Stop();
                try
                {
                    await worker.ShutdownAsync();
                    shutdown = await worker.SnapshotAsync();
                }
                finally { anchor?.Close(); }
            }
            JsonElement cleanup = JsonSerializer.SerializeToElement(shutdown);
            Require(cleanup.GetProperty("HostsDestroyed").GetBoolean()
                && cleanup.GetProperty("Native").GetProperty("SessionUsers").GetInt32() == 0
                && cleanup.GetProperty("Native").GetProperty("ProcessSessionUsers").GetInt32() == 0
                && !cleanup.GetProperty("Native").GetProperty("HasIdleGroup").GetBoolean(), "Worker shutdown retained native windows or a magnifier session.");
            object uiOwnerTransfer = RunUiOwnerTransferProbe();
            intervals.Sort();
            return new
            {
                Passed = true, Assertions = assertions, UiThread = uiThread, Rounds = rounds, Shutdown = shutdown,
                UiOwnerTransferAfterWorkerShutdown = uiOwnerTransfer,
                UiDispatcher = new
                {
                    TargetIntervalMs = 16, Count = intervals.Count,
                    MedianMs = intervals.Count == 0 ? 0 : intervals[intervals.Count / 2],
                    P95Ms = intervals.Count == 0 ? 0 : intervals[Math.Clamp((int)Math.Ceiling(intervals.Count * .95) - 1, 0, intervals.Count - 1)],
                    MaximumMs = intervals.Count == 0 ? 0 : intervals[^1],
                    IntervalsOver25Ms = intervals.Count(value => value > 25),
                },
                UsedDesktopCapture = false, DisplayedEffect = false, RegisteredHooks = false,
                Boundary = "All production native APIs and 33-ms source sampling run on a separate dispatcher while the caller samples its UI queue. One process uses only one native owner thread at a time. The baseline owner is destroyed before the worker; a fresh UI rental is verified after worker shutdown. Hosts remain DWM-cloaked. Restore acknowledgement waits for any in-flight source call and hiding; captures must await that task. No input-to-pixel or desktop frame-rate claim is made.",
            };
        }

        private static object RunUiOwnerTransferProbe()
        {
            Window? anchor = null;
            ScreenGradeSurface? grade = null;
            nint[] hosts = Array.Empty<nint>();
            var before = ScreenGradeSurface.GetNativeCallDiagnostics();
            object? rented = null;
            try
            {
                anchor = new Window();
                anchor.AppWindow.Hide();
                if (!ScreenGradeSurface.Warm())
                    throw new InvalidOperationException("UI rewarm after worker shutdown failed: " + ScreenGradeSurface.LastFailure);
                grade = ScreenGradeSurface.TryCreate(WinRT.Interop.WindowNative.GetWindowHandle(anchor))
                    ?? throw new InvalidOperationException("UI rerent after worker shutdown failed: " + ScreenGradeSurface.LastFailure);
                hosts = grade.WindowHandles.ToArray();
                foreach (nint host in hosts)
                {
                    int cloak = 1;
                    if (DwmSetWindowAttribute(host, 13, ref cloak, sizeof(int)) != 0)
                        throw new InvalidOperationException("A UI owner-transfer diagnostic host could not be cloaked.");
                }
                if (!grade.Apply(.5f) || !Array.TrueForAll(hosts, host =>
                    DwmGetWindowAttribute(host, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0))
                    throw new InvalidOperationException("UI native apply after worker shutdown failed or was not cloaked: " + ScreenGradeSurface.LastFailure);
                rented = ScreenGradeSurface.GetNativeCallDiagnostics();
                grade.ReturnToCache();
                grade = null;
            }
            finally
            {
                grade?.Dispose();
                ScreenGradeSurface.ShutdownCache();
                anchor?.Close();
            }
            var after = ScreenGradeSurface.GetNativeCallDiagnostics();
            if (after.SessionUsers != 0 || after.ProcessSessionUsers != 0 || after.HasIdleGroup
                || Array.Exists(hosts, IsWindow))
                throw new InvalidOperationException("UI owner-transfer diagnostics retained native resources.");
            return new { Passed = true, Before = before, Rented = rented, After = after,
                HostsDestroyed = true, DisplayedEffect = false, ScreenPixelsReadBack = false };
        }

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
        [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    }
}
