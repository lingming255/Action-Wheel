using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Action_Wheel.Services
{
    /// <summary>
    /// Bounded, one-shot placement after ShellExecute. A matching executable alone never moves an
    /// old window: it must also be newly shown/foregrounded, or receive a foreground title-change
    /// event. No titles, document content, input hooks or application preferences are read.
    /// </summary>
    internal sealed partial class LaunchWindowFollower : IDisposable
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<string, LaunchWindowFollower> Pending = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, long> LatestRequests = new(StringComparer.OrdinalIgnoreCase);
        private static long _retiredSequenceFloor;
        private readonly long _sequence;
        private readonly LaunchIdentity _identity;
        private readonly WindowPlacementTarget _placement;
        private readonly Dictionary<nint, WindowSnapshot> _baseline;
        private readonly Dictionary<uint, ProcessIdentity> _processes = new();
        private readonly ConcurrentDictionary<nint, WindowActivity> _activity = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly ManualResetEventSlim _eventsReady = new();
        private readonly WinEventCallback _eventCallback;
        private readonly nint _originalForeground;
        private uint _eventThreadId;
        private ProcessIdentity? _launched;
        private int _followed, _disposed;

        private LaunchWindowFollower(LaunchIdentity identity, WindowPlacementTarget placement, long sequence)
        {
            _identity = identity;
            _placement = placement;
            _sequence = sequence;
            _originalForeground = GetForegroundWindow();
            _baseline = ReadWindows();
            _eventCallback = OnWindowEvent;
        }

        internal static LaunchWindowFollower Begin(string target, string arguments, WindowPlacementTarget placement)
        {
            // Resolve/enumeration can take different amounts of time on concurrent launches. Order
            // requests at entry, so an older slow Begin cannot replace a newer completed one.
            long sequence = placement.RequestSequence;
            var follower = new LaunchWindowFollower(ResolveIdentity(target, arguments), placement, sequence);
            lock (Gate)
            {
                LatestRequests.TryGetValue(follower._identity.Affinity, out long latest);
                if (!MayRegister(sequence, latest, _retiredSequenceFloor))
                {
                    follower.Dispose();
                    return follower;
                }
                LatestRequests[follower._identity.Affinity] = sequence;
                if (LatestRequests.Count > 256)
                {
                    var oldest = LatestRequests.MinBy(request => request.Value);
                    LatestRequests.Remove(oldest.Key);
                    _retiredSequenceFloor = Math.Max(_retiredSequenceFloor, oldest.Value);
                }
                if (Pending.TryGetValue(follower._identity.Affinity, out var previous)) previous.Dispose();
                Pending[follower._identity.Affinity] = follower;
            }
            try
            {
                var thread = new Thread(follower.ObserveWindowEvents)
                {
                    IsBackground = true, Name = "ActionWheel launch placement",
                };
                thread.Start();
                _ = follower.ExpireAsync();
                // Install before ShellExecute can synchronously reuse an existing browser. A slow
                // listener is optional; the baseline/new-window/foreground comparison still works.
                follower._eventsReady.Wait(120);
                return follower;
            }
            catch { follower.Dispose(); throw; }
        }

        /// <summary>
        /// Plain executables with no content arguments can reuse one unambiguous existing main
        /// window. URLs, documents, protocols and argument-bearing commands must still execute.
        /// </summary>
        internal bool TryReuseExisting()
        {
            if (!_identity.ReusablePlainExe || Volatile.Read(ref _disposed) != 0) return false;
            var candidates = _baseline.Values.Where(MatchesHandler).ToArray();
            WindowSnapshot? chosen = candidates.Length == 1 ? candidates[0]
                : candidates.Where(window => window.Handle == _originalForeground)
                    .Select(window => (WindowSnapshot?)window).SingleOrDefault();
            if (chosen == null) return false;
            lock (Gate)
            {
                if (!IsCurrent() || !IsSameWindow(chosen.Value)) return false;
            }
            _ = Task.Run(async () =>
            {
                try { await CompleteCandidateAsync(chosen.Value).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception) { }
                finally { Dispose(); }
            });
            return true;
        }

        private async Task ExpireAsync()
        {
            try { await Task.Delay(8000, _cancellation.Token).ConfigureAwait(false); Dispose(); }
            catch (OperationCanceledException) { }
        }

        /// <summary>Takes ownership of the returned Process, including a null single-instance result.</summary>
        internal void Follow(Process? launched)
        {
            try
            {
                if (launched != null)
                {
                    try { _launched = ReadProcess((uint)launched.Id, refresh: true); }
                    catch (InvalidOperationException) { }
                }
            }
            finally { launched?.Dispose(); }
            if (Interlocked.Exchange(ref _followed, 1) != 0 || Volatile.Read(ref _disposed) != 0) return;
            _ = Task.Run(FollowAsync);
        }

        private async Task FollowAsync()
        {
            var clock = Stopwatch.StartNew();
            var stable = new Dictionary<nint, WindowSnapshot>();
            try
            {
                while (!_cancellation.IsCancellationRequested && clock.ElapsedMilliseconds < 6000)
                {
                    nint foreground = GetForegroundWindow();
                    var windows = ReadWindows();
                    var eligible = new List<(WindowSnapshot Window, int Score)>();
                    foreach (var window in windows.Values)
                    {
                        bool isNew = !_baseline.TryGetValue(window.Handle, out var old)
                            || old.Process != window.Process;
                        _activity.TryGetValue(window.Handle, out var activity);
                        bool associated = MatchesHandler(window);
                        bool direct = _launched is { } process && process == window.Process;
                        bool stayedPut = isNew || old.Rectangle == window.Rectangle;
                        var evidence = new CandidateEvidence(associated, direct, isNew,
                            window.Handle == foreground, window.Handle == _originalForeground,
                            activity.Foreground, activity.NameChanged, activity.Shown,
                            activity.UserMoved, !stayedPut, clock.ElapsedMilliseconds);
                        int score = CandidateScore(evidence);
                        // Wait for two stable samples so a splash, initial resize or per-monitor
                        // DPI transition can finish before choosing the application surface.
                        if (score > 0 && stable.TryGetValue(window.Handle, out var previous)
                            && previous.Rectangle == window.Rectangle && previous.Process == window.Process)
                            eligible.Add((window, score));
                    }
                    stable = windows;
                    var chosen = SelectUnique(eligible);
                    if (chosen == null && clock.ElapsedMilliseconds >= 1000)
                    {
                        // ShellExecute still ran, so the requested URL/document/note was delivered.
                        // Reopening the same page may emit neither a foreground nor title event.
                        // With exactly one known handler main window before AND after launch, its
                        // unchanged process/rectangle identifies that unambiguous reused target.
                        var before = _baseline.Values.Where(MatchesHandler).ToArray();
                        var after = windows.Values.Where(MatchesHandler).ToArray();
                        if (before.Length == 1 && after.Length == 1 && before[0].Handle == after[0].Handle
                            && before[0].Process == after[0].Process && before[0].Rectangle == after[0].Rectangle
                            && !(_activity.TryGetValue(after[0].Handle, out var activity) && activity.UserMoved))
                            chosen = after[0];
                    }
                    if (chosen != null)
                    {
                        await CompleteCandidateAsync(chosen.Value).ConfigureAwait(false);
                        return; // A request never chases a later navigation or user drag.
                    }
                    await Task.Delay(80, _cancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* Optional placement must not affect an already-successful launch. */ }
            finally { Dispose(); }
        }

        private bool IsCurrent() => !_cancellation.IsCancellationRequested
            && Pending.TryGetValue(_identity.Affinity, out var current) && ReferenceEquals(current, this)
            && LatestRequests.TryGetValue(_identity.Affinity, out long latest) && latest == _sequence;

        private async Task CompleteCandidateAsync(WindowSnapshot window)
        {
            bool restoring;
            bool StillCurrent()
            {
                lock (Gate)
                    return IsCurrent() && IsSameWindow(window)
                        && !(_activity.TryGetValue(window.Handle, out var activity) && activity.UserMoved);
            }
            lock (Gate)
            {
                if (!IsCurrent() || !IsSameWindow(window)
                    || _activity.TryGetValue(window.Handle, out var activity) && activity.UserMoved) return;
                if (!ReadComparableRectangle(window.Handle, out var rectangle) || rectangle != window.Rectangle) return;
            }
            await MoveWindowAsync(window.Handle, _placement, StillCurrent).ConfigureAwait(false);
            lock (Gate)
            {
                if (!StillCurrent()) return;
                restoring = IsIconic(window.Handle) && WindowInputFocus.CanActivateActionTarget;
                if (restoring) RestoreForActivation(window.Handle);
            }
            if (restoring) await Task.Delay(80, _cancellation.Token).ConfigureAwait(false);
            lock (Gate)
            {
                if (IsCurrent() && IsSameWindow(window)
                    && !(_activity.TryGetValue(window.Handle, out var activity) && activity.UserMoved))
                    WindowInputFocus.TryActivateActionTarget(window.Handle); // One handoff; never reclaim focus in a loop.
            }
        }

        private bool IsSameWindow(WindowSnapshot window)
        {
            GetWindowThreadProcessId(window.Handle, out uint pid);
            // HWND and PID values can be recycled. Re-read creation time/path before moving or
            // activating, including the child identity of a generic UWP frame host.
            return pid == window.Process.Id && ReadProcess(pid, refresh: true) == window.Process
                && (_launched is { } launched && launched == window.Process || MatchesHandler(window));
        }

        internal static void CancelPending()
        {
            lock (Gate)
            {
                if (LatestRequests.Count != 0)
                    _retiredSequenceFloor = Math.Max(_retiredSequenceFloor, LatestRequests.Values.Max());
                foreach (var follower in Pending.Values.ToArray()) follower.Dispose();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _cancellation.Cancel();
            uint thread = Volatile.Read(ref _eventThreadId);
            if (thread != 0) PostThreadMessage(thread, 0x0012, 0, 0); // WM_QUIT, not application input.
            lock (Gate)
                if (Pending.TryGetValue(_identity.Affinity, out var current) && ReferenceEquals(current, this))
                    Pending.Remove(_identity.Affinity);
        }

        private void ObserveWindowEvents()
        {
            nint foreground = 0, objects = 0, moving = 0;
            try
            {
                // OUTOFCONTEXT callbacks require a pumping thread. They observe native window
                // metadata only, and are not keyboard/mouse hooks or input injection.
                PeekMessage(out _, 0, 0, 0, 0);
                Volatile.Write(ref _eventThreadId, GetCurrentThreadId());
                if (_cancellation.IsCancellationRequested) return;
                const uint outOfContextSkipOwnProcess = 2;
                foreground = SetWinEventHook(3, 3, 0, _eventCallback, 0, 0, outOfContextSkipOwnProcess);
                objects = SetWinEventHook(0x8002, 0x800C, 0, _eventCallback, 0, 0, outOfContextSkipOwnProcess);
                moving = SetWinEventHook(0x000A, 0x000B, 0, _eventCallback, 0, 0, outOfContextSkipOwnProcess);
                _eventsReady.Set();
                while (!_cancellation.IsCancellationRequested && GetMessage(out _, 0, 0, 0) > 0) { }
            }
            catch (Exception) { /* Candidate discovery also works without the optional event evidence. */ }
            finally
            {
                if (foreground != 0) UnhookWinEvent(foreground);
                if (objects != 0) UnhookWinEvent(objects);
                if (moving != 0) UnhookWinEvent(moving);
                _eventsReady.Set();
                Volatile.Write(ref _eventThreadId, 0);
            }
        }

        private void OnWindowEvent(nint hook, uint eventType, nint hwnd, int objectId, int childId, uint threadId, uint time)
        {
            if (_cancellation.IsCancellationRequested || hwnd == 0 || GetAncestor(hwnd, 2) != hwnd) return;
            if ((eventType is 0x8002 or 0x800C) && (objectId != 0 || childId != 0)) return;
            if (eventType is not (3 or 0x8002 or 0x800C or 0x000A)) return;
            if (eventType == 0x800C && hwnd != GetForegroundWindow()) return;
            // Never retain names, titles, URLs or child-document text. Foreground is rechecked at
            // selection time, so a background page's automatic title change cannot move its window.
            _activity.AddOrUpdate(hwnd,
                new WindowActivity(eventType == 3, eventType == 0x800C, eventType == 0x8002, eventType == 0x000A),
                (_, old) => new WindowActivity(old.Foreground || eventType == 3,
                    old.NameChanged || eventType == 0x800C, old.Shown || eventType == 0x8002,
                    old.UserMoved || eventType == 0x000A));
        }

        private Dictionary<nint, WindowSnapshot> ReadWindows()
        {
            var result = new Dictionary<nint, WindowSnapshot>();
            EnumWindows((hwnd, _) =>
            {
                try
                {
                    if (!IsWindowVisible(hwnd) || GetWindow(hwnd, 4) != 0
                        || GetAncestor(hwnd, 2) != hwnd || (GetWindowLong(hwnd, -20) & (0x80 | 0x08000000)) != 0)
                        return true;
                    // Unowned splash/pop-up surfaces can share the correct process but disappear
                    // before its real main window arrives. A caption or resizing frame is a useful
                    // conservative application-surface boundary; ambiguous frameless popups stay put.
                    if ((GetWindowLong(hwnd, -16) & (0x00C00000 | 0x00040000)) == 0) return true;
                    if (DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                    if (!ReadComparableRectangle(hwnd, out var rectangle) || rectangle.Width < 80 || rectangle.Height < 60) return true;
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid == 0 || pid == Environment.ProcessId) return true;
                    var process = ReadProcess(pid);
                    if (process != null) result[hwnd] = new WindowSnapshot(hwnd, process.Value, rectangle);
                }
                catch (Exception) { /* One inaccessible/retired window cannot fail enumeration. */ }
                return true;
            }, 0);
            return result;
        }

        private ProcessIdentity? ReadProcess(uint pid, bool refresh = false)
        {
            if (!refresh && _processes.TryGetValue(pid, out var cached)) return cached;
            var process = QueryProcess(pid);
            if (process != null) _processes[pid] = process.Value;
            return process;
        }

        private bool MatchesHandler(WindowSnapshot window)
        {
            if (MatchesProcess(window.Process)) return true;
            // Windows 10 may host Settings' CoreWindow in ApplicationFrameHost. Match the real
            // child process, never ApplicationFrameHost itself (which hosts unrelated UWP apps).
            if ((!_identity.Settings && _identity.PackageFamily == null) || !string.Equals(Path.GetFileName(window.Process.Executable),
                    "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)) return false;
            bool found = false;
            EnumChildWindows(window.Handle, (child, _) =>
            {
                GetWindowThreadProcessId(child, out uint pid);
                var process = ReadProcess(pid);
                found = process != null && MatchesProcess(process.Value);
                return !found;
            }, 0);
            return found;
        }

        private bool MatchesProcess(ProcessIdentity process) =>
            _identity.Executable != null && string.Equals(process.Executable, _identity.Executable, StringComparison.OrdinalIgnoreCase)
            || _identity.PackageFamily != null && string.Equals(process.PackageFamily, _identity.PackageFamily, StringComparison.OrdinalIgnoreCase);

        private readonly record struct ProcessIdentity(uint Id, string Executable, long Started, string? PackageFamily = null);
        private readonly record struct WindowSnapshot(nint Handle, ProcessIdentity Process, NativeRect Rectangle);
        private readonly record struct WindowActivity(bool Foreground, bool NameChanged, bool Shown, bool UserMoved);
    }
}
