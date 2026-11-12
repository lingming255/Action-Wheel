using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Action_Wheel.Overlay;
using Microsoft.UI.Dispatching;

namespace Action_Wheel.Services
{
    /// <summary>
    /// Drains the colour out of every monitor below the interactive wheel, through live
    /// 1:1 native magnifier windows. The wheel's own icons and hover accents keep their colour.
    /// </summary>
    /// <remarks>
    /// The effect lives in input-transparent windows below the wheel, rather than a final-screen
    /// matrix. Its glass still samples the grey scene beneath. No cached screenshot or global colour
    /// setting is installed; closing these windows restores the desktop, including after a crash.
    ///
    /// UI thread only: LauncherService starts this only after the raw colour capture succeeds.
    /// Its dispatcher orders the transition against cancellation and the grey-cover handoff.
    /// </remarks>
    internal static class ScreenGrade
    {
        /// <summary>Saturation left while held: 0 is pure grey, 1 is untouched.</summary>
        public const float Saturation = 0.0f;

        // Rec. 601 luma.
        public const float LumR = 0.30f, LumG = 0.59f, LumB = 0.11f;

        private static readonly float[] Identity =
        {
            1, 0, 0, 0, 0,
            0, 1, 0, 0, 0,
            0, 0, 1, 0, 0,
            0, 0, 0, 1, 0,
            0, 0, 0, 0, 1,
        };

        private static nint _menuHwnd;
        private static RefreshReclaimIntro? _intro;
        private static Action<float>? _applyGlassSaturation;
        private static ScreenGradeSurface? _surface;
        private static readonly Transition Grade = new(ApplySaturation);
        private static readonly Stopwatch TransitionClock = new();
        private static DispatcherQueueTimer? _transitionTimer;

        /// <summary>Orders this gesture's live grey layer below its interactive menu.</summary>
        internal static void BindMenu(nint menuHwnd, RefreshReclaimIntro? intro, Action<float> applyGlassSaturation)
        {
            Restore();
            _menuHwnd = menuHwnd;
            _intro = intro;
            _applyGlassSaturation = applyGlassSaturation;
        }

        /// <summary>Desaturates every monitor.</summary>
        public static void Lower()
        {
            StopTransitionTimer();
            Grade.Lower();
        }

        /// <summary>
        /// Smoothly desaturates every monitor after a successful raw-colour capture. Repeated
        /// calls share the active transition. True means the exact grey endpoint was reached;
        /// cancellation, shutdown or an unavailable colour effect completes with false.
        /// </summary>
        public static Task<bool> LowerGraduallyAsync(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(duration));
            if (Grade.IsTransitioning || Grade.IsLowered)
                return Grade.Begin(duration.TotalMilliseconds);

            // Keep updates ordered with cancellation and the outward-cover handoff. Elapsed
            // time avoids accumulated drift when a busy UI tick arrives late.
            DispatcherQueue? queue = DispatcherQueue.GetForCurrentThread();
            if (queue == null)
                return Task.FromResult(false);

            Task<bool> completion = Grade.Begin(duration.TotalMilliseconds);
            if (!Grade.IsTransitioning)
                return completion;
            try
            {
                var timer = queue.CreateTimer();
                timer.Interval = TimeSpan.FromMilliseconds(16);
                timer.IsRepeating = true;
                timer.Tick += OnTransitionTick;
                _transitionTimer = timer;
                TransitionClock.Restart();
                timer.Start();
            }
            catch (Exception)
            {
                StopTransitionTimer();
                Grade.Restore();
            }
            return completion;
        }

        /// <summary>Restores colour. A no-op when it was never lowered.</summary>
        public static void Restore()
        {
            StopTransitionTimer();
            Grade.Restore();
            _surface?.Dispose();
            _surface = null;
            _menuHwnd = 0;
            ApplyIntroSaturation(1);
            _intro = null;
            ApplyGlassSaturation(1);
            _applyGlassSaturation = null;
        }

        public static void Shutdown()
        {
            StopTransitionTimer();
            Grade.Shutdown();
            _surface?.Dispose();
            _surface = null;
            _menuHwnd = 0;
            ApplyIntroSaturation(1);
            _intro = null;
            ApplyGlassSaturation(1);
            _applyGlassSaturation = null;
        }

        private static void OnTransitionTick(DispatcherQueueTimer sender, object args)
        {
            // A queued tick from a stopped or replaced timer must never reapply grey.
            if (!ReferenceEquals(sender, _transitionTimer))
                return;
            Grade.Advance(TransitionClock.Elapsed.TotalMilliseconds);
            if (!Grade.IsTransitioning)
                StopTransitionTimer();
        }

        private static void StopTransitionTimer()
        {
            DispatcherQueueTimer? timer = _transitionTimer;
            _transitionTimer = null;
            if (timer != null)
            {
                timer.Stop();
                timer.Tick -= OnTransitionTick;
            }
            TransitionClock.Reset();
        }

        private static bool ApplySaturation(float saturation)
        {
            ApplyIntroSaturation(saturation);
            ApplyGlassSaturation(saturation);
            if (saturation == 1)
            {
                _surface?.Dispose();
                _surface = null;
                return true;
            }
            if (_menuHwnd == 0) return false;
            _surface ??= ScreenGradeSurface.TryCreate(_menuHwnd);
            _surface?.SetDecorationAnchor(_intro is { AlreadyCompleted: false } ? _intro.WindowHandle : 0);
            if (_surface?.Apply(saturation) == true) return true;
            _surface?.Dispose();
            _surface = null;
            return false;
        }

        private static void ApplyIntroSaturation(float saturation)
        {
            try { _intro?.ApplySaturation(saturation); }
            catch (Exception)
            {
                // A lost particle effect must never interrupt desktop colour restoration.
                try { _intro?.Dispose(); } catch (Exception) { }
                _intro = null;
            }
        }

        private static void ApplyGlassSaturation(float saturation)
        {
            try { _applyGlassSaturation?.Invoke(saturation); }
            catch (Exception)
            {
                // A lost material must not prevent removing the desktop grade windows.
                _applyGlassSaturation = null;
            }
        }

        /// <summary>Pure smoothstep curve from colour to the exact held-screen saturation.</summary>
        internal static float GradeSaturationAt(double elapsedMilliseconds, double durationMilliseconds)
        {
            if (double.IsNaN(elapsedMilliseconds))
                throw new ArgumentOutOfRangeException(nameof(elapsedMilliseconds));
            if (!double.IsFinite(durationMilliseconds))
                throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));
            if (durationMilliseconds <= 0)
                return Saturation;
            double progress = Math.Clamp(elapsedMilliseconds / durationMilliseconds, 0, 1);
            double eased = progress * progress * (3 - 2 * progress);
            return (float)(1 + (Saturation - 1) * eased);
        }

        /// <summary>
        /// Row-major 5x5 for a row vector [r g b a 1]: row i is how much input channel i feeds each
        /// output. Blends identity with the luma projection by <paramref name="saturation"/>.
        /// </summary>
        internal static float[] BuildMatrix(float saturation)
        {
            float k = 1 - saturation;
            float[] luma = { LumR, LumG, LumB };
            var m = (float[])Identity.Clone();
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++)
                    m[row * 5 + col] = k * luma[row] + (row == col ? saturation : 0);
            return m;
        }

        /// <summary>
        /// Timer-independent lifecycle, also exercised with an injected no-op native effect.
        /// Production owns its only instance on the UI thread.
        /// </summary>
        private sealed class Transition
        {
            private readonly Func<float, bool> _apply;
            private TaskCompletionSource<bool>? _completion;
            private double _durationMilliseconds;
            private float _saturation = 1;
            private bool _effectApplied;
            private bool _shutdown;

            internal Transition(Func<float, bool> apply) => _apply = apply;
            internal bool IsTransitioning => _completion != null;
            internal bool IsLowered => _effectApplied && _saturation == Saturation;

            internal Task<bool> Begin(double durationMilliseconds)
            {
                if (_shutdown)
                    return Task.FromResult(false);
                if (_completion != null)
                    return _completion.Task;
                if (IsLowered)
                    return Task.FromResult(true);
                if (!_apply(1))
                    return Task.FromResult(false);

                _saturation = 1;
                _effectApplied = false;
                _durationMilliseconds = durationMilliseconds;
                _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Task<bool> completion = _completion.Task;
                if (durationMilliseconds <= 0)
                    Advance(0);
                return completion;
            }

            internal void Advance(double elapsedMilliseconds)
            {
                if (_shutdown || _completion == null)
                    return;
                float saturation = GradeSaturationAt(elapsedMilliseconds, _durationMilliseconds);
                if (!_apply(saturation))
                {
                    Restore();
                    return;
                }
                _saturation = saturation;
                _effectApplied = saturation != 1;
                if (saturation == Saturation)
                    Complete(true);
            }

            internal void Lower()
            {
                Complete(false);
                if (_shutdown || IsLowered)
                    return;
                if (_apply(Saturation))
                {
                    _saturation = Saturation;
                    _effectApplied = true;
                }
                else
                    Restore();
            }

            internal void Restore()
            {
                Complete(false);
                if (!_effectApplied || _apply(1))
                {
                    _saturation = 1;
                    _effectApplied = false;
                }
            }

            internal void Shutdown()
            {
                Restore();
                _shutdown = true;
            }

            private void Complete(bool result)
            {
                TaskCompletionSource<bool>? completion = _completion;
                _completion = null;
                completion?.TrySetResult(result);
            }
        }

        /// <summary>Lifecycle and curve diagnostics; never calls the Magnification API.</summary>
        internal static Task<object> RunTransitionProbeAsync()
        {
            int assertions = 0;
            void Require(bool condition, string message)
            {
                assertions++;
                if (!condition) throw new InvalidOperationException(message);
            }
            var applied = new List<float>();
            var transition = new Transition(saturation => { applied.Add(saturation); return true; });
            Task<bool> complete = transition.Begin(420);
            Require(!complete.IsCompleted, "Transition completed before its first tick.");
            Require(ReferenceEquals(complete, transition.Begin(100)), "Repeated begin restarted the transition.");
            transition.Advance(210);
            Require(Math.Abs(applied[^1] - 0.5f) < 0.00001f, "Transition midpoint is incorrect.");
            transition.Advance(420);
            Require(complete.IsCompletedSuccessfully && complete.Result && transition.IsLowered,
                "Transition did not finish at exact grey.");
            transition.Restore();
            Require(applied[^1] == 1 && !transition.IsLowered, "Completed transition failed to restore colour.");

            Task<bool> cancelled = transition.Begin(420);
            transition.Advance(120);
            transition.Restore();
            int afterRestore = applied.Count;
            transition.Advance(420);
            Require(cancelled.IsCompletedSuccessfully && !cancelled.Result, "Restore did not cancel the pending task.");
            Require(applied.Count == afterRestore && applied[^1] == 1, "A late tick reapplied grey after restore.");

            Task<bool> interrupted = transition.Begin(420);
            transition.Advance(70);
            transition.Lower();
            Require(interrupted.IsCompletedSuccessfully && !interrupted.Result && transition.IsLowered,
                "Immediate Lower did not replace the pending transition.");
            transition.Restore();
            Task<bool> stopped = transition.Begin(420);
            transition.Advance(160);
            transition.Shutdown();
            int afterShutdown = applied.Count;
            transition.Advance(420);
            transition.Lower();
            Require(stopped.IsCompletedSuccessfully && !stopped.Result && applied[^1] == 1,
                "Shutdown did not cancel and restore colour.");
            Require(applied.Count == afterShutdown && !transition.Begin(420).Result,
                "Shutdown allowed a later transition to reapply grey.");

            bool failNext = false;
            var failureApplied = new List<float>();
            var failure = new Transition(saturation =>
            {
                failureApplied.Add(saturation);
                if (!failNext) return true;
                failNext = false;
                return false;
            });
            Task<bool> failed = failure.Begin(420);
            failure.Advance(120);
            failNext = true;
            failure.Advance(300);
            Require(failed.IsCompletedSuccessfully && !failed.Result && failureApplied[^1] == 1,
                "Native effect failure left a partial grade active.");
            var unavailable = new Transition(_ => false);
            Require(!unavailable.Begin(420).Result && !unavailable.IsTransitioning,
                "Unavailable native effect left a transition pending.");
            var immediate = new Transition(_ => true);
            Require(immediate.Begin(0).Result && immediate.IsLowered,
                "Zero-duration transition missed its endpoint.");

            Require(GradeSaturationAt(0, 420) == 1 && GradeSaturationAt(420, 420) == Saturation,
                "Curve endpoints are incorrect.");
            float previous = 1;
            for (int i = 0; i <= 20; i++)
            {
                float current = GradeSaturationAt(i * 21, 420);
                Require(current <= previous && current >= Saturation, "Curve is not monotonic.");
                previous = current;
            }
            float[] identity = BuildMatrix(1), grey = BuildMatrix(Saturation);
            Require(identity.AsSpan().SequenceEqual(Identity), "Full-colour matrix differs from identity.");
            Require(grey[0] == LumR && grey[1] == LumR && grey[5] == LumG && grey[10] == LumB,
                "Grey matrix no longer matches the capture cover.");
            return Task.FromResult<object>(new
            {
                Passed = true, Assertions = assertions, UsedScreenGrade = false,
                UsedDesktopCapture = false, DisplayedEffect = false,
                Checked = new[] { "curve", "matrix", "completion", "duplicate start", "restore", "late ticks",
                    "immediate lower", "shutdown", "native failure", "unavailable API", "zero duration" },
            });
        }
    }
}
