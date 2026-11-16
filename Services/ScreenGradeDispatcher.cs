using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Action_Wheel.Overlay;
using Microsoft.UI.Dispatching;

namespace Action_Wheel.Services
{
    /// <summary>
    /// Owns all production magnifier HWNDs on one dedicated dispatcher. UI callers only publish
    /// the latest state; they never wait synchronously for source sampling or native window calls.
    /// </summary>
    internal sealed class ScreenGradeDispatcher
    {
        private enum Operation { Restore, Warm, Apply, Release, Shutdown }
        private sealed record Request(long Generation, Operation Operation, nint Menu = 0,
            nint Decoration = 0, float Saturation = 1, Action? Failed = null);

        private readonly bool _hiddenProbe, _measureTimings;
        private readonly ConcurrentQueue<(long Generation, TaskCompletionSource<bool> Completion)> _restoreAcks = new();
        private DispatcherQueueController? _controller;
        private DispatcherQueue? _queue;
        private Request _desired = new(0, Operation.Restore);
        private ScreenGradeSurface? _surface;
        private long _generation, _surfaceGeneration;
        private int _scheduled, _workerThread;
        private bool _shutdown;
        private Task _lastRestore = Task.CompletedTask;
        private Task? _shutdownTask;
        private object? _lastTimings, _shutdownSnapshot;
        private nint[] _knownHosts = Array.Empty<nint>();
        private string? _failure;

        internal ScreenGradeDispatcher(bool hiddenProbe = false, bool measureTimings = false)
        {
            _hiddenProbe = hiddenProbe;
            _measureTimings = measureTimings;
        }

        internal Task LastRestoreTask => Volatile.Read(ref _lastRestore);

        internal long BeginGesture()
        {
            Restore();
            return Volatile.Read(ref _generation);
        }

        internal bool Apply(long generation, nint menu, nint decoration, float saturation, Action? failed = null)
        {
            if (_shutdown || generation != Volatile.Read(ref _generation) || menu == 0) return false;
            Volatile.Write(ref _desired, new Request(generation, Operation.Apply, menu, decoration, saturation, failed));
            return Schedule();
        }

        internal void Warm()
        {
            if (_shutdown || Volatile.Read(ref _desired).Operation == Operation.Apply) return;
            Volatile.Write(ref _desired, new Request(Volatile.Read(ref _generation), Operation.Warm));
            Schedule();
        }

        internal void Restore() => PublishRestore(Operation.Restore);
        internal void ReleaseCache() => PublishRestore(Operation.Release);

        private void PublishRestore(Operation operation)
        {
            if (_shutdown) return;
            long generation = Interlocked.Increment(ref _generation);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _lastRestore, completion.Task);
            _restoreAcks.Enqueue((generation, completion));
            Volatile.Write(ref _desired, new Request(generation, operation));
            // No native thread has ever been created, so no HWND can need hiding.
            if (_queue == null && operation == Operation.Restore)
            {
                CompleteRestores(generation);
                return;
            }
            if (!Schedule()) FailRestores(new InvalidOperationException("The grading dispatcher is unavailable."));
        }

        private bool EnsureQueue()
        {
            if (_queue != null) return true;
            if (_shutdown) return false;
            try
            {
                _controller = DispatcherQueueController.CreateOnDedicatedThread();
                _queue = _controller.DispatcherQueue;
                return true;
            }
            catch (Exception error) { _failure = error.ToString(); return false; }
        }

        private bool Schedule()
        {
            if (!EnsureQueue()) return false;
            if (Interlocked.Exchange(ref _scheduled, 1) != 0) return true;
            if (_queue!.TryEnqueue(Drain)) return true;
            Interlocked.Exchange(ref _scheduled, 0);
            return false;
        }

        private bool IsCurrent(long generation)
        {
            Request desired = Volatile.Read(ref _desired);
            return desired.Generation == generation && desired.Operation == Operation.Apply;
        }

        private void Drain()
        {
            _workerThread = Environment.CurrentManagedThreadId;
            while (true)
            {
                Request request = Volatile.Read(ref _desired);
                try { Process(request); }
                catch (Exception error)
                {
                    _failure = error.ToString();
                    RetireSurface();
                    FailRestores(error);
                    if (IsCurrent(request.Generation)) request.Failed?.Invoke();
                }
                if (!ReferenceEquals(request, Volatile.Read(ref _desired))) continue;
                Interlocked.Exchange(ref _scheduled, 0);
                if (ReferenceEquals(request, Volatile.Read(ref _desired))) return;
                if (Interlocked.Exchange(ref _scheduled, 1) != 0) return;
            }
        }

        private void Process(Request request)
        {
            if (_surface != null && (_surfaceGeneration != request.Generation || request.Operation != Operation.Apply))
                RetireSurface();
            // Hide the older owner before acknowledging original-colour capture for this generation.
            if (_surface == null) CompleteRestores(request.Generation);
            switch (request.Operation)
            {
                case Operation.Warm:
                    if (!ScreenGradeSurface.Warm()) throw new InvalidOperationException(ScreenGradeSurface.LastFailure);
                    return;
                case Operation.Release:
                case Operation.Shutdown:
                    ScreenGradeSurface.ShutdownCache();
                    _shutdownSnapshot = Snapshot();
                    return;
                case Operation.Restore:
                    return;
            }
            if (!IsCurrent(request.Generation)) return;
            if (_surface == null)
            {
                _surface = ScreenGradeSurface.TryCreate(request.Menu)
                    ?? throw new InvalidOperationException(ScreenGradeSurface.LastFailure);
                _surfaceGeneration = request.Generation;
                _surface.SetPresentationGuard(() => IsCurrent(request.Generation));
                _surface.MeasureTimings = _measureTimings;
                _surface.ResetTimingDiagnostics();
                _knownHosts = new List<nint>(_surface.WindowHandles).ToArray();
                if (_hiddenProbe)
                    foreach (nint host in _knownHosts)
                    {
                        int cloak = 1;
                        if (DwmSetWindowAttribute(host, 13, ref cloak, sizeof(int)) != 0)
                            throw new InvalidOperationException("A worker diagnostic host could not be cloaked.");
                    }
            }
            _surface.SetDecorationAnchor(request.Decoration);
            if (!_surface.Apply(request.Saturation))
            {
                RetireSurface();
                if (IsCurrent(request.Generation)) throw new InvalidOperationException(ScreenGradeSurface.LastFailure ?? "Native grading failed.");
            }
        }

        private void RetireSurface()
        {
            ScreenGradeSurface? surface = _surface;
            _surface = null;
            _surfaceGeneration = 0;
            if (surface == null) return;
            if (_measureTimings) _lastTimings = surface.GetTimingDiagnostics();
            surface.MeasureTimings = false;
            surface.ReturnToCache();
        }

        private void CompleteRestores(long generation)
        {
            while (_restoreAcks.TryPeek(out var pending) && pending.Generation <= generation)
                if (_restoreAcks.TryDequeue(out pending)) pending.Completion.TrySetResult(true);
        }

        private void FailRestores(Exception error)
        {
            while (_restoreAcks.TryDequeue(out var pending)) pending.Completion.TrySetException(error);
        }

        private object Snapshot() => new
        {
            WorkerThread = _workerThread, Generation = Volatile.Read(ref _generation),
            ActiveGeneration = _surfaceGeneration, HasActiveSurface = _surface != null,
            Native = ScreenGradeSurface.GetNativeCallDiagnostics(),
            Timings = _surface?.GetTimingDiagnostics() ?? _lastTimings,
            KnownHosts = Array.ConvertAll(_knownHosts, host => host.ToInt64()),
            HostsHidden = Array.TrueForAll(_knownHosts, host => !IsWindowVisible(host)),
            HostsCloaked = Array.TrueForAll(_knownHosts, host =>
                DwmGetWindowAttribute(host, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0),
            HostsDestroyed = Array.TrueForAll(_knownHosts, host => !IsWindow(host)),
            Failure = _failure,
        };

        internal Task<object> SnapshotAsync()
        {
            if (_shutdownSnapshot != null && _shutdownTask?.IsCompleted == true) return Task.FromResult(_shutdownSnapshot);
            var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!EnsureQueue() || !_queue!.TryEnqueue(() => completion.TrySetResult(Snapshot())))
                completion.TrySetException(new InvalidOperationException("The grading dispatcher is unavailable."));
            return completion.Task;
        }

        internal Task DrainAsync()
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_queue == null) { completion.TrySetResult(); return completion.Task; }
            if (!_queue.TryEnqueue(() => completion.TrySetResult()))
                completion.TrySetException(new InvalidOperationException("The grading dispatcher is unavailable."));
            return completion.Task;
        }

        internal Task ShutdownAsync()
        {
            if (_shutdownTask != null) return _shutdownTask;
            PublishRestore(Operation.Shutdown);
            _shutdown = true;
            return _shutdownTask = FinishShutdownAsync(LastRestoreTask, _controller);
        }

        private async Task FinishShutdownAsync(Task restored, DispatcherQueueController? controller)
        {
            await restored.ConfigureAwait(false);
            await DrainAsync().ConfigureAwait(false);
            if (controller != null) await controller.ShutdownQueueAsync();
        }

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
        [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    }
}
