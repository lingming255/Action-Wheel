using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Action_Wheel.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;

namespace Action_Wheel.Overlay
{
    public sealed partial class RadialMenu
    {
        /// <summary>
        /// Opt-in release integration check using only synthetic images and never-shown windows.
        /// The caller keeps a hidden lifetime window alive while individual test menus close.
        /// </summary>
        internal static async Task<object> RunReleasePreparationProbeAsync()
        {
            var rounds = new List<object>();
            foreach (bool prepareDuringHold in new[] { true, false })
            {
                RadialMenu? menu = null;
                RefreshFrame? frame = null;
                try
                {
                    menu = new RadialMenu();
                    menu.AppWindow.Hide();
                    menu.AppWindow.Move(new PointInt32(-20000, -20000));
                    menu._screenOrigin = new PointInt32(-20000, -20000);
                    await Task.Delay(120);
                    RequireReleaseProbe(menu.RootGrid.XamlRoot != null, "The hidden menu has no XamlRoot.");
                    var nativeSize = menu.AppWindow.Size;
                    double ringWidth = menu.MenuContainer.Width, ringHeight = menu.MenuContainer.Height;
                    var release = new PointInt32(-19850, -19850);
                    if (prepareDuringHold)
                    {
                        frame = CreateReleaseProbeFrame();
                        menu.PrepareRefreshPresentation(frame);
                        RequireReleaseProbe(menu._gpuRefreshPlayback != null, "The hold did not prepare a GPU surface.");
                        RequireReleaseProbe(ReferenceEquals(menu._gpuRefreshPlayback!.Renderer, frame.BorrowGpuRenderer()),
                            "Hidden preparation took renderer ownership before release.");
                        RequireReleaseProbe(menu.RefreshLayer.Visibility == Visibility.Collapsed
                            && menu.MenuContainer.Visibility == Visibility.Visible
                            && menu.MenuContainer.Width == ringWidth && menu.MenuContainer.Height == ringHeight
                            && menu.AppWindow.Size.Width == nativeSize.Width && menu.AppWindow.Size.Height == nativeSize.Height,
                            "Hidden preparation changed the ring or native window dimensions.");
                    }

                    var feedbackClock = Stopwatch.StartNew();
                    menu.BeginRefreshFeedback(release);
                    double synchronousFeedbackMs = feedbackClock.Elapsed.TotalMilliseconds;
                    var feedback = menu._refreshFeedbackVisual;
                    RequireReleaseProbe(feedback != null
                        && menu._refreshFeedbackResources.OfType<SpriteVisual>().Count() == 5,
                        "Release did not synchronously create five uniform feedback blocks.");
                    RequireReleaseProbe(menu._refreshFeedbackResources.OfType<ScalarKeyFrameAnimation>()
                            .Any(animation => animation.IterationBehavior == AnimationIterationBehavior.Forever
                                && animation.Duration == TimeSpan.FromMilliseconds(600)),
                        "Release feedback has no persistent native opacity animation.");
                    int extendedStyle = GetWindowLong(menu._windowHandle, GWL_EXSTYLE);
                    RequireReleaseProbe((extendedStyle & 0x08080020) == 0x08080020
                        && !menu.RootGrid.IsHitTestVisible && menu.IsRefreshPlaying
                        && menu.MenuContainer.Visibility == Visibility.Collapsed,
                        "Pending release feedback is not a click-through decoration.");
                    var feedbackCommitClock = Stopwatch.StartNew();
                    await menu.WaitForRefreshFeedbackAsync();
                    double feedbackCommitWaitMs = feedbackCommitClock.Elapsed.TotalMilliseconds;
                    await Task.Delay(150); // Simulate unfinished model/scene preparation without running a model.
                    RequireReleaseProbe(ReferenceEquals(feedback, menu._refreshFeedbackVisual)
                        && !menu._fullRefreshStarted && menu.RefreshFeedbackLayer.Visibility == Visibility.Visible,
                        "The local feedback disappeared while exact scene preparation was pending.");

                    frame ??= CreateReleaseProbeFrame();
                    RefreshFrame readyFrame = frame;
                    var gpu = readyFrame.BorrowGpuRenderer()
                        ?? throw new InvalidOperationException("Release probe requires the real hardware renderer.");
                    readyFrame.PrepareRelease(release);
                    var handedOff = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    menu.PlayRefresh(readyFrame, () =>
                    {
                        // Mirrors Launcher.EndGrade disposal after the presentation takes ownership.
                        readyFrame.Dispose();
                        handedOff.TrySetResult(true);
                    });
                    if (await Task.WhenAny(handedOff.Task, Task.Delay(1200)) != handedOff.Task)
                        throw new TimeoutException("The hidden grey cover did not finish its release handoff.");
                    await handedOff.Task;
                    RequireReleaseProbe(readyFrame.BorrowGpuRenderer() == null
                        && menu._gpuRefreshPlayback != null
                        && ReferenceEquals(menu._gpuRefreshPlayback!.Renderer, gpu),
                        "The prepared renderer did not transfer exactly once to playback.");
                    RequireReleaseProbe(menu._refreshFeedbackVisual == null
                        && menu._refreshFeedbackResources.Count == 0
                        && menu.RefreshFeedbackLayer.Visibility == Visibility.Collapsed,
                        "The local feedback resources survived their full-scene handoff.");
                    int initialFrames = menu._gpuRefreshPlayback!.PresentedFrames;
                    menu._gpuRefreshPlayback.RenderFrame(250);
                    await Task.Delay(160);
                    RequireReleaseProbe(menu._gpuRefreshPlayback.PresentedFrames > initialFrames,
                        "GPU playback stopped when the shared prepared frame was disposed.");
                    gpu.Device.WaitIdle();
                    RequireReleaseProbe(!IsReleaseProbeWindowVisible(menu._windowHandle),
                        "The release diagnostic unexpectedly showed its hidden window.");
                    menu.CloseMenu();
                    await Task.Delay(60);
                    RequireReleaseProbe(menu._closed && menu._gpuRefreshPlayback == null
                        && menu._preparedRefreshFrame == null && menu._refreshTimer == null
                        && menu._refreshFeedbackResources.Count == 0,
                        "Closing the release window left playback or feedback resources attached.");
                    bool gpuDisposed = false;
                    try { gpu.Render(0); }
                    catch (ObjectDisposedException) { gpuDisposed = true; }
                    RequireReleaseProbe(gpuDisposed, "The adopted renderer survived window teardown.");
                    rounds.Add(new
                    {
                        PreparedDuringHold = prepareDuringHold,
                        SynchronousFeedbackMs = synchronousFeedbackMs,
                        FeedbackCommitWaitMs = feedbackCommitWaitMs,
                        FeedbackBlockCount = 5,
                        FeedbackSurvivedPendingPreparation = true,
                        RendererSurvivedFrameDisposal = true,
                        HiddenPresentationHandoff = true,
                        ClosedResourcesReleased = true,
                    });
                }
                finally
                {
                    if (menu is { _closed: false }) menu.CloseMenu();
                    frame?.Dispose();
                }
            }

            // Cancelling a hold destroys only its borrowed presentation. The frame still owns
            // the renderer until the gesture's frame disposal, which may happen on another worker.
            RadialMenu? cancelledMenu = null;
            RefreshFrame? cancelledFrame = null;
            try
            {
                cancelledMenu = new RadialMenu();
                cancelledMenu.AppWindow.Hide();
                cancelledMenu.AppWindow.Move(new PointInt32(-20000, -20000));
                await Task.Delay(120);
                cancelledFrame = CreateReleaseProbeFrame();
                var gpu = cancelledFrame.BorrowGpuRenderer()
                    ?? throw new InvalidOperationException("Cancelled preparation probe requires a GPU renderer.");
                cancelledMenu.PrepareRefreshPresentation(cancelledFrame);
                RequireReleaseProbe(cancelledMenu._gpuRefreshPlayback != null,
                    "Cancelled preparation did not construct a borrowed GPU presentation.");
                // Model an exception after fullscreen preparation begins but before Take/Adopt.
                // EndGrade may precede Closed here; the stage flag must not conceal a borrower.
                cancelledMenu._fullRefreshStarted = true;
                cancelledMenu.DetachBorrowedRefreshPresentation();
                RequireReleaseProbe(cancelledMenu._gpuRefreshPlayback == null
                    && cancelledMenu._preparedRefreshFrame == null,
                    "Ending a failed grade retained a borrowed surface before frame disposal.");
                gpu.Render(0);
                gpu.Device.WaitIdle();
                await Task.Run(cancelledFrame.Dispose);
                cancelledMenu.CloseMenu();
                await Task.Delay(60);
                bool gpuDisposed = false;
                try { gpu.Render(0); }
                catch (ObjectDisposedException) { gpuDisposed = true; }
                RequireReleaseProbe(gpuDisposed, "The cancelled hold's frame failed to release its owned renderer.");
                rounds.Add(new { CancelledDuringHold = true, BorrowedSurfaceReleasedWithoutDisposingFrameRenderer = true,
                    FrameDisposalReleasedRenderer = true, GradeEndedBeforeCloseSafely = true });
            }
            finally
            {
                if (cancelledMenu is { _closed: false }) cancelledMenu.CloseMenu();
                cancelledFrame?.Dispose();
            }

            // A prepared device can disappear between the release timing upload and first
            // presentation. The still-owned CPU reference must survive that native failure.
            RadialMenu? failedGpuMenu = null;
            RefreshFrame? failedGpuFrame = null;
            try
            {
                failedGpuMenu = new RadialMenu();
                failedGpuMenu.AppWindow.Hide();
                failedGpuMenu.AppWindow.Move(new PointInt32(-20000, -20000));
                failedGpuMenu._screenOrigin = new PointInt32(-20000, -20000);
                await Task.Delay(120);
                failedGpuFrame = CreateReleaseProbeFrame();
                RefreshFrame readyFrame = failedGpuFrame;
                var gpu = readyFrame.BorrowGpuRenderer()
                    ?? throw new InvalidOperationException("GPU-loss preparation probe requires a GPU renderer.");
                failedGpuMenu.PrepareRefreshPresentation(readyFrame);
                RequireReleaseProbe(failedGpuMenu._gpuRefreshPlayback != null,
                    "GPU-loss preparation did not construct a borrowed GPU presentation.");
                var release = new PointInt32(-19850, -19850);
                failedGpuMenu.BeginRefreshFeedback(release);
                await failedGpuMenu.WaitForRefreshFeedbackAsync();
                readyFrame.PrepareRelease(release);
                gpu.Dispose();
                var handedOff = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                failedGpuMenu.PlayRefresh(readyFrame, () =>
                {
                    readyFrame.Dispose();
                    handedOff.TrySetResult(true);
                });
                RequireReleaseProbe(failedGpuMenu._gpuRefreshPlayback == null
                    && failedGpuMenu._refreshPlayback != null,
                    "A lost prepared GPU did not fall back to the CPU playback.");
                if (await Task.WhenAny(handedOff.Task, Task.Delay(1200)) != handedOff.Task)
                    throw new TimeoutException("The CPU fallback did not finish its grey-cover handoff.");
                await handedOff.Task;
                await Task.Delay(250);
                RequireReleaseProbe(!failedGpuMenu._closed && failedGpuMenu._refreshPlayback != null,
                    "CPU fallback closed after the shared frame was disposed.");
                var image = failedGpuMenu.RefreshLayer.Children.OfType<Image>().Single();
                var bitmap = image.Source as WriteableBitmap
                    ?? throw new InvalidOperationException("CPU fallback has no writable output bitmap.");
                byte[] uploaded = new byte[readyFrame.Grey.Length];
                using (var stream = bitmap.PixelBuffer.AsStream()) stream.ReadExactly(uploaded);
                RequireReleaseProbe(!uploaded.AsSpan().SequenceEqual(readyFrame.Grey),
                    "CPU fallback did not upload an animated frame after the initial grey cover.");
                RequireReleaseProbe(!IsReleaseProbeWindowVisible(failedGpuMenu._windowHandle),
                    "The GPU-loss diagnostic unexpectedly showed its hidden window.");
                failedGpuMenu.CloseMenu();
                await Task.Delay(60);
                RequireReleaseProbe(failedGpuMenu._closed && failedGpuMenu._refreshPlayback == null
                    && failedGpuMenu._gpuRefreshPlayback == null && failedGpuMenu._refreshTimer == null
                    && failedGpuMenu._refreshFeedbackResources.Count == 0,
                    "The GPU-loss fallback left playback resources attached after close.");
                rounds.Add(new { PreparedGpuLostBeforeFirstReleaseFrame = true,
                    CpuFallbackHandoff = true, CpuAnimatedUploadAfterFrameDisposal = true,
                    ClosedResourcesReleased = true });
            }
            finally
            {
                if (failedGpuMenu is { _closed: false }) failedGpuMenu.CloseMenu();
                failedGpuFrame?.Dispose();
            }

            return new { Passed = true, Rounds = rounds, UsedDesktopCapture = false,
                UsedModel = false, DisplayedEffect = false, UsedScreenGrade = false };
        }

        /// <summary>
        /// Exercises real inward-to-outward ownership with synthetic, never-shown windows.
        /// Manual cover callbacks separate lifecycle assertions from DWM's hidden-window rate.
        /// No model, desktop capture or native screen-grade matrix participates in this check.
        /// </summary>
        internal static async Task<object> RunParallelIntroProbeAsync()
        {
            var rounds = new List<object>();
            foreach (bool closeBeforeIntroEnds in new[] { false, true })
            {
                RadialMenu? menu = null;
                RefreshFrame? frame = null;
                RefreshReclaimIntro? intro = null;
                try
                {
                    menu = new RadialMenu();
                    menu.AppWindow.Hide();
                    menu.AppWindow.Move(new PointInt32(-20000, -20000));
                    menu._screenOrigin = new PointInt32(-20000, -20000);
                    await Task.Delay(120);
                    RequireReleaseProbe(menu.RootGrid.XamlRoot != null,
                        "The parallel inward/outward probe has no hidden XamlRoot.");

                    // All expensive reference/GPU/swapchain preparation precedes the inward
                    // clock, so a cold graphics backend cannot consume its 240 ms test window.
                    frame = CreateReleaseProbeFrame();
                    menu.PrepareRefreshPresentation(frame);
                    var gpu = frame.BorrowGpuRenderer()
                        ?? throw new InvalidOperationException("Parallel intro probe requires the real hardware renderer.");
                    RequireReleaseProbe(menu._gpuRefreshPlayback != null,
                        "Parallel intro preparation did not construct its hidden GPU surface.");
                    var release = new PointInt32(-19680, -19820);
                    frame.PrepareRelease(release);
                    menu.BeginRefreshFeedback(release);
                    intro = RefreshReclaimIntro.CreateHiddenForProbe(frame.Bounds, release, menu._windowHandle);
                    intro.UpdateCapture(frame.Renderer.CapturePixels, frame.Bounds);
                    Task introCompletion = intro.Completion;
                    double elapsedBeforeTransfer = intro.ElapsedMs;
                    double remainingBeforeTransfer = intro.RemainingMs;
                    RequireReleaseProbe(menu.AdoptRefreshReclaimIntro(intro)
                        && ReferenceEquals(menu._refreshReclaimIntro, intro),
                        "The ready outward window did not adopt its inward decoration.");

                    bool restored = false;
                    RefreshFrame readyFrame = frame;
                    var handoffClock = Stopwatch.StartNew();
                    menu.PlayRefresh(readyFrame, () =>
                    {
                        // Mirrors EndGrade after the renderer's ownership has moved, without
                        // applying any actual screen-wide colour effect in this diagnostic.
                        readyFrame.Dispose();
                        restored = true;
                    });
                    RequireReleaseProbe(menu._fullRefreshStarted && !restored
                        && !intro.AlreadyCompleted && !introCompletion.IsCompleted
                        && ReferenceEquals(menu._refreshReclaimIntro, intro),
                        "Outward preparation waited for or destroyed the unfinished inward pulse.");
                    EventHandler<object>? cover = menu._refreshOnRendering;
                    RequireReleaseProbe(cover != null,
                        "Parallel playback has no cover-commit handoff callback.");
                    // Commit both concealed layout and the revealed cover before any timer
                    // can complete the intro. Externally cloaked fixtures need only one phase.
                    for (int callback = 0; callback < 6; callback++)
                        menu._refreshOnRendering?.Invoke(null, null!);
                    double synchronousHandoffMs = handoffClock.Elapsed.TotalMilliseconds;
                    double introElapsedAtOutwardStart = intro.ElapsedMs;
                    RequireReleaseProbe(restored && menu._refreshOnRendering == null
                        && !intro.AlreadyCompleted && !introCompletion.IsCompleted
                        && menu._gpuRefreshPlayback != null
                        && ReferenceEquals(menu._gpuRefreshPlayback.Renderer, gpu)
                        && readyFrame.BorrowGpuRenderer() == null,
                        "Outward playback did not start while the same inward pulse remained unfinished.");
                    RequireReleaseProbe(ReferenceEquals(introCompletion, intro.Completion)
                        && intro.ElapsedMs >= elapsedBeforeTransfer
                        && intro.RemainingMs <= remainingBeforeTransfer,
                        "Transferring/promoting the inward pulse restarted its clock or completion.");
                    RequireReleaseProbe((GetWindowLong(menu._windowHandle, GWL_EXSTYLE) & 0x08080020) == 0x08080020
                        && !menu.RootGrid.IsHitTestVisible && menu.IsRefreshPlaying
                        && !IsReleaseProbeWindowVisible(menu._windowHandle),
                        "Parallel inward/outward playback intercepted input or showed the hidden menu.");

                    var activePlayback = menu._gpuRefreshPlayback
                        ?? throw new InvalidOperationException("Parallel playback lost its GPU surface.");
                    int framesBeforeManualRender = activePlayback.PresentedFrames;
                    activePlayback.RenderFrame(130);
                    RequireReleaseProbe(activePlayback.PresentedFrames > framesBeforeManualRender,
                        "The outward GPU renderer did not submit a frame during the inward pulse.");
                    if (closeBeforeIntroEnds)
                    {
                        RequireReleaseProbe(!introCompletion.IsCompleted,
                            "The cancellation round did not close an unfinished inward decoration.");
                        menu.CloseMenu();
                        await Task.Delay(60); // Window.Closed may be dispatched after Close returns.
                        RequireReleaseProbe(introCompletion.IsCompletedSuccessfully && intro.AlreadyCompleted,
                            "Closing outward playback did not release its unfinished inward decoration.");
                    }
                    else
                    {
                        RequireReleaseProbe(await Task.WhenAny(introCompletion, Task.Delay(1500)) == introCompletion,
                            "The transferred inward pulse never reached its natural end.");
                        await introCompletion;
                        RequireReleaseProbe(intro.AlreadyCompleted && intro.RemainingMs == 0
                            && !intro.PromoteForPlayback() && !menu._closed && menu._gpuRefreshPlayback != null,
                            "Natural inward completion interrupted or outlived its outward owner.");
                        int framesBeforeContinue = activePlayback.PresentedFrames;
                        activePlayback.RenderFrame(420);
                        RequireReleaseProbe(activePlayback.PresentedFrames > framesBeforeContinue,
                            "The outward scan stopped when the inward decoration completed.");
                        menu.CloseMenu();
                    }

                    await Task.Delay(60);
                    RequireReleaseProbe(menu._closed && menu._refreshReclaimIntro == null
                        && menu._gpuRefreshPlayback == null && menu._refreshPlayback == null
                        && menu._preparedRefreshFrame == null && menu._refreshTimer == null
                        && menu._refreshFeedbackResources.Count == 0 && introCompletion.IsCompletedSuccessfully,
                        "Closing parallel playback left an inward, outward or feedback resource attached.");
                    bool gpuDisposed = false;
                    try { gpu.Render(0); } catch (ObjectDisposedException) { gpuDisposed = true; }
                    RequireReleaseProbe(gpuDisposed, "The parallel playback renderer survived owner teardown.");
                    rounds.Add(new
                    {
                        CloseBeforeIntroEnds = closeBeforeIntroEnds,
                        IntroDurationMs = RefreshReclaimIntro.DurationMs,
                        SynchronousHandoffMs = synchronousHandoffMs,
                        IntroElapsedMsAtOutwardStart = introElapsedAtOutwardStart,
                        IntroIncompleteAtOutwardStart = true,
                        UsedManualCoverCommitCallbacks = true,
                        TransferRetainedClockAndCompletion = true,
                        OutwardFrameSubmittedDuringIntro = true,
                        NaturalCompletionKeptOutwardAlive = !closeBeforeIntroEnds,
                        ClosedResourcesReleased = true,
                        ClickThrough = true,
                    });
                }
                finally
                {
                    if (menu is { _closed: false }) menu.CloseMenu();
                    intro?.Dispose();
                    frame?.Dispose();
                }
            }
            // The longest configurable intro can outlast the shortest outward scan. Its owner
            // must retire that scan at the original deadline and retain only the inward pulse.
            RadialMenu? longIntroMenu = null;
            RefreshFrame? shortFrame = null;
            RefreshReclaimIntro? longIntro = null;
            try
            {
                longIntroMenu = new RadialMenu();
                longIntroMenu.AppWindow.Hide();
                longIntroMenu.AppWindow.Move(new PointInt32(-20000, -20000));
                longIntroMenu._screenOrigin = new PointInt32(-20000, -20000);
                await Task.Delay(120);
                shortFrame = CreateReleaseProbeFrame(600);
                longIntroMenu.PrepareRefreshPresentation(shortFrame);
                var release = new PointInt32(-19680, -19820);
                shortFrame.PrepareRelease(release);
                longIntro = RefreshReclaimIntro.CreateHiddenForProbe(shortFrame.Bounds, release,
                    longIntroMenu._windowHandle, new ReclaimAnimationSettings { DurationMs = 1500 });
                longIntroMenu.AdoptRefreshReclaimIntro(longIntro);
                var readyFrame = shortFrame;
                longIntroMenu.PlayRefresh(readyFrame, readyFrame.Dispose);
                for (int callback = 0; callback < 6; callback++)
                    longIntroMenu._refreshOnRendering?.Invoke(null, null!);
                await Task.Delay(820);
                RequireReleaseProbe(!longIntroMenu._closed && !longIntro.AlreadyCompleted
                    && longIntroMenu._gpuRefreshPlayback == null && longIntroMenu._refreshPlayback == null
                    && longIntroMenu.RefreshLayer.Visibility == Visibility.Collapsed,
                    "A short outward scan clipped the long intro or retained its final image.");
                RequireReleaseProbe(await Task.WhenAny(longIntro.Completion, Task.Delay(1800)) == longIntro.Completion,
                    "The long configurable intro never completed.");
                await Task.Delay(80);
                RequireReleaseProbe(longIntroMenu._closed && longIntroMenu._refreshReclaimIntro == null
                    && longIntroMenu._refreshTimer == null,
                    "The parallel owner survived the end of both independent animations.");
                rounds.Add(new { IntroDurationMs = 1500, OutwardDurationMs = 600,
                    IntroSurvivedOutwardEnd = true, OutwardLayerRetiredAtItsDeadline = true,
                    ClosedAfterBothAnimations = true });
            }
            finally
            {
                if (longIntroMenu is { _closed: false }) longIntroMenu.CloseMenu();
                longIntro?.Dispose();
                shortFrame?.Dispose();
            }
            object reusedRelease = await RunReusedReleaseProbeAsync();
            var nativeHandoff = await RunReusedReleaseHandoffProbeAsync();
            return new { Passed = nativeHandoff.Passed, Rounds = rounds, ReusedRelease = reusedRelease,
                NativeHandoff = nativeHandoff, UsedDesktopCapture = false, UsedModel = false,
                DisplayedEffect = false, UsedScreenGrade = false,
                TimingBoundary = "Prepared synthetic frames and manually committed hidden covers; this verifies independent lifetimes, not real input-to-visible latency." };
        }

        private static RefreshFrame CreateReleaseProbeFrame(double durationMs = 1400, RectInt32? bounds = null)
        {
            const int width = 640, height = 360;
            byte[] capture = new byte[width * height * 4];
            float[] depth = new float[width * height];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int pixel = y * width + x;
                capture[pixel * 4] = (byte)(40 + x * 100 / width);
                capture[pixel * 4 + 1] = (byte)(50 + y * 90 / height);
                capture[pixel * 4 + 2] = (byte)(80 + (x / 11 + y / 13) % 2 * 70);
                capture[pixel * 4 + 3] = 255;
                depth[pixel] = x / (float)(width - 1);
            }
            return RefreshFrame.CreatePrepared(capture, new RefreshDepthMap(width, height, depth),
                bounds ?? new RectInt32(-20000, -20000, width, height), width / 2, height / 2,
                new RefreshTimingProfile(new RefreshAnimationSettings { DurationMs = durationMs }, instantStart: true),
                CancellationToken.None);
        }

        private static void RequireReleaseProbe(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsReleaseProbeWindowVisible(nint hwnd);
    }
}
