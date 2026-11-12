using System;
using System.IO;
using System.Text.Json;
using Action_Wheel.Overlay;
using Microsoft.UI.Xaml;

namespace Action_Wheel.Services
{
    /// <summary>Opt-in glass/grade checks without hooks, user configuration writes or audio.</summary>
    internal static class WheelGlassSmoke
    {
        internal static async void Run(string reportPath, bool verifyPixels, Action exit)
        {
            Window? owner = null;
            try
            {
                if (!Path.IsPathFullyQualified(reportPath))
                    throw new ArgumentException("Glass report path must be absolute.");
                owner = new Window();
                owner.AppWindow.Hide();
                object glass = await RadialMenu.RunGlassProbeAsync();
                object transition = await ScreenGrade.RunTransitionProbeAsync();
                object surface = await ScreenGradeSurface.RunHiddenProbeAsync();
                object intro = await RefreshReclaimIntro.RunHiddenProbeAsync(verifyOffscreenShow: true);
                object introCache = await RefreshReclaimIntro.RunPreparedCacheProbeAsync();
                object parallel = await RadialMenu.RunParallelIntroProbeAsync();
                object? pixels = verifyPixels ? await ScreenGradePixelProbe.RunAsync() : null;
                object? glassPixels = verifyPixels ? await ScreenGradePixelProbe.RunGlassSourceProbeAsync() : null;
                bool passed = pixels == null || JsonSerializer.SerializeToElement(pixels)
                    .TryGetProperty("Passed", out var result) && result.GetBoolean();
                passed &= glassPixels == null || JsonSerializer.SerializeToElement(glassPixels)
                    .TryGetProperty("Passed", out var glassResult) && glassResult.GetBoolean();
                File.WriteAllText(reportPath, JsonSerializer.Serialize(new
                {
                    Passed = passed, Glass = glass, GradeTransition = transition, GradeSurface = surface,
                    Reclaim = intro, ReclaimCache = introCache, ParallelIntro = parallel,
                    Pixels = pixels, GlassPixels = glassPixels, DisplayedSyntheticTest = verifyPixels, AudioPlayed = false,
                    SavedUserConfiguration = false,
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception exception)
            {
                if (Path.IsPathFullyQualified(reportPath))
                    File.WriteAllText(reportPath, JsonSerializer.Serialize(new
                    {
                        Passed = false, Error = exception.ToString(), DisplayedSyntheticTest = verifyPixels,
                    }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally
            {
                owner?.Close();
                exit();
            }
        }
    }
}
