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

        private static RefreshFrame CreateReleaseProbeFrame()
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
                new RectInt32(-20000, -20000, width, height), width / 2, height / 2,
                new RefreshTimingProfile(new RefreshAnimationSettings { DurationMs = 1400 }, instantStart: true),
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
