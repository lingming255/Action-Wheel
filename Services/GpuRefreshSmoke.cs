using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Text.Json;
using Action_Wheel.Overlay;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace Action_Wheel.Services
{
    /// <summary>Opt-in hidden integration check for the deployed WinUI/DXGI surface ABI.</summary>
    internal static class GpuRefreshSmoke
    {
        internal static async void Run(string reportPath, Action exit)
        {
            var rounds = new List<object>();
            Window? lifetimeWindow = null;
            try
            {
                if (!Path.IsPathFullyQualified(reportPath))
                    throw new ArgumentException("GPU smoke report path must be absolute.");
                // Closing the last WinUI window terminates the application. Keep one hidden
                // owner alive while individual playback windows exercise repeated teardown.
                lifetimeWindow = new Window();
                lifetimeWindow.AppWindow.Hide();
                for (int round = 0; round < 2; round++)
                {
                    int width = round == 0 ? 1920 : 3840;
                    int height = round == 0 ? 1080 : 2160;
                    var window = new Window();
                    var host = new Canvas();
                    window.Content = host;
                    // XamlRoot is assigned asynchronously, even for a hidden window.
                    await Task.Delay(120);
                    double scale = host.XamlRoot?.RasterizationScale
                        ?? throw new InvalidOperationException("Hidden host has no XamlRoot.");
                    host.Width = width / scale; host.Height = height / scale;
                    window.AppWindow.Resize(new SizeInt32(width, height));
                    host.Measure(new Windows.Foundation.Size(host.Width, host.Height));
                    host.Arrange(new Windows.Foundation.Rect(0, 0, host.Width, host.Height));
                    host.UpdateLayout();
                    // The window is deliberately never activated or shown.
                    window.AppWindow.Hide();
                    GpuRefreshPlayback? playback = null;
                    GpuRefreshRenderer? renderer = null;
                    try
                    {
                        byte[] capture = new byte[width * height * 4];
                        float[] depths = new float[width * height];
                        for (int y = 0; y < height; y++)
                        for (int x = 0; x < width; x++)
                        {
                            int p = y * width + x;
                            capture[p * 4] = (byte)(x * 255 / (width - 1));
                            capture[p * 4 + 1] = (byte)(y * 255 / (height - 1));
                            capture[p * 4 + 2] = (byte)((x / 9 + y / 7) % 2 == 0 ? 200 : 30);
                            capture[p * 4 + 3] = 255;
                            depths[p] = x / (float)(width - 1);
                        }
                        var reference = new SpectrumOverlayRefreshRenderer(width, height, capture,
                            new RefreshDepthMap(width, height, depths), width / 2, height / 2);
                        renderer = new GpuRefreshRenderer(new GpuRefreshScene(reference));
                        playback = new GpuRefreshPlayback(renderer, host);
                        bool playbackFailed = false;
                        foreach (double elapsed in new double[] { 0, 300, 800, 1600, 2490, 2500 })
                        {
                            playback.RenderFrame(elapsed);
                            await Task.Delay(35);
                        }
                        int initialFrames = playback.PresentedFrames;
                        var clock = Stopwatch.StartNew();
                        playback.Start(() => playbackFailed = true);
                        await Task.Delay(2700);
                        double observedMs = clock.Elapsed.TotalMilliseconds;
                        int automaticFrames = playback.PresentedFrames - initialFrames;
                        int endingFrames = playback.PresentedFrames;
                        await Task.Delay(120);
                        if (playback.PresentedFrames != endingFrames)
                            throw new InvalidOperationException("GPU playback continued beyond its duration.");
                        renderer.Device.WaitIdle();
                        if (playbackFailed)
                            throw new InvalidOperationException("GPU smoke playback failed.");
                        if (automaticFrames < 2)
                            throw new InvalidOperationException("Composition rendering callbacks did not advance playback.");
                        double finalScale = host.XamlRoot?.RasterizationScale
                            ?? throw new InvalidOperationException("Playback lost its XamlRoot.");
                        if (Math.Abs(host.ActualWidth * finalScale - width) > .05
                            || Math.Abs(host.ActualHeight * finalScale - height) > .05)
                            throw new InvalidOperationException("Refresh cover does not match native capture pixels after the DPI change.");
                        rounds.Add(new { Width = width, Height = height, playback.AdapterName,
                            InitialRasterizationScale = scale, FinalRasterizationScale = finalScale,
                            playback.PresentedFrames, AutomaticFrames = automaticFrames,
                            ObservedMs = observedMs, PlaybackDurationMs = renderer.DurationMs,
                            StoppedAtEnd = true,
                            Mapping = playback.GetMappingDiagnostic(),
                            HiddenWindow = true, InteropSurfaceCreated = true });
                    }
                    finally
                    {
                        playback?.Dispose();
                        renderer?.Dispose();
                        window.Close();
                    }
                }
                object presentation = await GpuRefreshPresentationProbe.RunAsync();
                JsonElement presentationResult = JsonSerializer.SerializeToElement(presentation);
                bool pixelCaptureAvailable = presentationResult.TryGetProperty("PixelCaptureAvailable", out var available)
                    && available.GetBoolean();
                bool pixelMappingVerified = presentationResult.TryGetProperty("PixelMappingMatches", out var matches)
                    && matches.GetBoolean();
                if (pixelCaptureAvailable && !pixelMappingVerified)
                    throw new InvalidOperationException("Captured Composition pixels do not match the native coordinate pattern: "
                        + presentationResult.GetRawText());
                File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Passed = true, Rounds = rounds,
                    PixelMappingVerified = pixelMappingVerified,
                    Presentation = presentation,
                    UsedDesktopCapture = false, DisplayedEffect = false }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception error)
            {
                File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Passed = false, Error = error.ToString() },
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            finally
            {
                lifetimeWindow?.Close();
                exit();
            }
        }
    }
}
