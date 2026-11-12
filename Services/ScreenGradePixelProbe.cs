using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Action_Wheel.Overlay;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;

namespace Action_Wheel.Services
{
    /// <summary>
    /// Explicit, visible pixel diagnostic. Only a 640x360 synthetic rectangle is displayed and
    /// read back; no hooks, configuration, model, sound or fullscreen colour matrix participate.
    /// Core HWNDs opt out of capture exclusion so the screen readback sees the final stack;
    /// a separate synthetic reclaim-source experiment compares WDA_NONE with WDA_EXCLUDEFROMCAPTURE.
    /// The caller must keep its own hidden lifetime window alive until it has written the report.
    /// </summary>
    internal static class ScreenGradePixelProbe
    {
        private const int Width = 640, Height = 360;
        private const int RgbTolerance = 2;
        private static readonly RectInt32 Foreground = new(496, 280, 96, 48);
        private static readonly RectInt32 OnePixel = new(24, 144, 256, 48);
        private static readonly RectInt32 FourPixel = new(320, 144, 256, 48);
        private static readonly RectInt32 StepEdge = new(24, 216, 256, 48);
        private static readonly RectInt32 Dynamic = new(24, 288, 192, 48);
        private static readonly RectInt32 LiveMarker = new(240, 288, 192, 48);
        private static readonly RectInt32 Reclaim = new(464, 112, 128, 24);
        private static readonly (string Name, Rgb Colour)[] Colours =
        {
            ("red", new(255, 0, 0)), ("green", new(0, 255, 0)), ("blue", new(0, 0, 255)),
            ("cyan", new(0, 255, 255)), ("magenta", new(255, 0, 255)), ("yellow", new(255, 255, 0)),
        };

        /// <summary>Checks explicit grading of the real glass material above a capture-excluded native grade.</summary>
        internal static async Task<object> RunGlassSourceProbeAsync()
        {
            var failures = new List<string>();
            var stages = new Dictionary<string, object>();
            PatternWindow? background = null, glassWindow = null;
            ScreenGradeSurface? grade = null;
            RectInt32 bounds = default;
            var sample = new RectInt32(Width / 2 - 8, Height / 2 - 8, 16, 16);
            var originalColour = new Rgb(230, 40, 90);
            byte[] original = new byte[Width * Height * 4];
            Fill(original, Width, new RectInt32(0, 0, Width, Height), originalColour);
            byte[] physicalGrey = Grey(original);
            bool affinityVerified = false, glassConnected = false;
            int captures = 0;
            void Require(bool condition, string message) { if (!condition) failures.Add(message); }
            try
            {
                bounds = ChooseBounds();
                background = new PatternWindow(bounds, original);
                await background.ShowAsync();
                glassWindow = new PatternWindow(bounds, original);
                await glassWindow.ShowAsync();
                WheelGlassSurface glass = glassWindow.AttachProbeGlass();
                glass.ApplySaturation(1);
                async Task<byte[]> CaptureAsync()
                {
                    await Task.Delay(160);
                    DwmFlush();
                    captures++;
                    return CaptureRegion(bounds);
                }
                byte[] colour = await CaptureAsync();
                glass.RefreshDiagnostics();
                glassConnected = glass.IsConnected && glass.Diagnostics.BrushChainConnected;
                Require(glassConnected && glass.MaterialOpacity == 1 && glass.Diagnostics.MaskOpacity > .999,
                    "The real glass probe must connect its production brush chain with a fully opaque centre mask.");
                Require(Math.Abs(WheelGlassSurface.FrostedMaterialOpacity - .60) < .000001,
                    "The fully opaque test mask must not change the accepted 60% production glass opacity.");
                Require(MaximumError(colour, original, sample) <= RgbTolerance
                    && Pixel(colour, Width / 2, Height / 2).Maximum - Pixel(colour, Width / 2, Height / 2).Minimum > 100,
                    "The glass source did not reproduce the known saturated uniform colour before grading.");
                stages["uniformColour"] = new
                {
                    Actual = Pixel(colour, Width / 2, Height / 2), Expected = originalColour,
                    MaximumRgbError = MaximumError(colour, original, sample),
                };

                background.UpdatePixels(physicalGrey);
                byte[] greyReference = await CaptureAsync();
                Require(MaximumError(greyReference, physicalGrey, sample) <= RgbTolerance,
                    "The same glass brush did not reproduce the known physical-grey source reference.");
                stages["physicalGreyReference"] = new
                {
                    Actual = Pixel(greyReference, Width / 2, Height / 2), Expected = Pixel(physicalGrey, Width / 2, Height / 2),
                    MaximumRgbError = MaximumError(greyReference, physicalGrey, sample),
                };

                background.UpdatePixels(original);
                grade = ScreenGradeSurface.CreateForProbe(bounds, glassWindow.Handle,
                    show: true, captureExcluded: true);
                Require(grade.Apply(0), "The capture-excluded native grey layer could not be applied below the glass.");
                affinityVerified = background.HasCaptureAffinity(0) && glassWindow.HasCaptureAffinity(0)
                    && grade.WindowHandles.Count == 1
                    && GetWindowDisplayAffinity(grade.WindowHandles[0], out uint affinity) && affinity == 0x11;
                Require(affinityVerified, "Glass/background must use WDA_NONE and the native grey layer WDA_EXCLUDEFROMCAPTURE.");
                // The host backdrop can omit a capture-excluded HWND. Preserve that observation
                // as a source diagnostic; the production material now has its own explicit grade.
                byte[] uncompensated = await CaptureAsync();
                stages["colourWithCaptureExcludedGradeUncompensated"] = new
                {
                    Actual = Pixel(uncompensated, Width / 2, Height / 2),
                    MaximumGreyReferenceRgbError = MaximumError(uncompensated, greyReference, sample),
                    MaximumColourReferenceRgbError = MaximumError(uncompensated, colour, sample),
                    DiagnosticOnly = true, GlassSaturation = 1,
                };
                glass.ApplySaturation(0);
                byte[] graded = await CaptureAsync();
                int greyError = MaximumError(graded, greyReference, sample);
                int colourError = MaximumError(graded, colour, sample);
                Require(greyError <= RgbTolerance && colourError > 50,
                    "The explicitly desaturated real glass material does not match its physical-grey reference.");
                var outside = new RectInt32(16, 16, 16, 16);
                Require(MaximumError(graded, original, outside) <= RgbTolerance,
                    "The grade was not excluded from screen readback outside the opaque glass mask; source isolation is unverified.");
                stages["glassSaturationZeroWithCaptureExcludedGrade"] = new
                {
                    GlassSaturation = 0,
                    Actual = Pixel(graded, Width / 2, Height / 2),
                    ExpectedPhysicalGreyReference = Pixel(greyReference, Width / 2, Height / 2),
                    MaximumGreyReferenceRgbError = greyError, MaximumColourReferenceRgbError = colourError,
                    OutsideGlassRgb = Pixel(graded, outside.X + 8, outside.Y + 8),
                    OutsideGlassExpectedRawColour = originalColour,
                    OutsideGlassMaximumRgbError = MaximumError(graded, original, outside),
                };
            }
            catch (Exception exception) { failures.Add(exception.GetType().Name + ": " + exception.Message); }
            finally
            {
                if (grade != null) foreach (nint hwnd in grade.WindowHandles) ShowWindow(hwnd, 0);
                glassWindow?.Hide(); background?.Hide();
                try { grade?.Dispose(); } catch (Exception exception) { failures.Add("Grade cleanup: " + exception.Message); }
                try { glassWindow?.Dispose(); } catch (Exception exception) { failures.Add("Glass cleanup: " + exception.Message); }
                try { background?.Dispose(); } catch (Exception exception) { failures.Add("Background cleanup: " + exception.Message); }
            }
            return new
            {
                Passed = failures.Count == 0, Failures = failures, Stages = stages,
                Bounds = new { bounds.X, bounds.Y, bounds.Width, bounds.Height },
                SampleBounds = new { sample.X, sample.Y, sample.Width, sample.Height },
                ActualWheelGlassSurface = true, ProbeMaterialOpacity = 1, ProductionMaterialOpacity = WheelGlassSurface.FrostedMaterialOpacity,
                ProbeTintCleared = true, GlassConnected = glassConnected, CaptureAffinityVerified = affinityVerified,
                CapturedOnlySyntheticRegion = true, ScreenCaptureCount = captures,
                UsedFullscreenColourMatrix = false, RegisteredHooks = false, UsedConfiguration = false, AudioPlayed = false,
                RgbTolerance,
                Interpretation = "An opaque probe mask removes transparent bypass. The real glass material at explicit saturation 0 must match the physical-grey reference while the native desktop grade is excluded from capture. Uncompensated source sampling is reported separately.",
                Limitations = "Only an unobscured synthetic 640x360 region is read back. Uniform-colour and physical-grey references must self-calibrate before the excluded-grade sample can pass. Production opacity and tint remain unchanged; no screenshot is saved.",
            };
        }

        internal static async Task<object> RunAsync()
        {
            var failures = new List<string>();
            var stages = new Dictionary<string, object>();
            var reclaimSourceExperiments = new List<object>();
            PatternWindow? background = null, foreground = null, reclaim = null;
            ScreenGradeSurface? grade = null;
            RectInt32 bounds = default;
            bool displayed = false, captureAffinityVerified = false;
            int captures = 0;
            DateTimeOffset started = DateTimeOffset.UtcNow;
            void Require(bool condition, string message)
            {
                if (!condition) failures.Add(message);
            }
            try
            {
                bounds = ChooseBounds();
                byte[] original = BuildPattern(updated: false);
                byte[] foregroundPixels = BuildForeground();
                background = new PatternWindow(bounds, original);
                await background.ShowAsync();
                displayed = true;
                var foregroundBounds = new RectInt32(bounds.X + Foreground.X, bounds.Y + Foreground.Y,
                    Foreground.Width, Foreground.Height);
                foreground = new PatternWindow(foregroundBounds, foregroundPixels);
                await foreground.ShowAsync();
                byte[] expectedOriginal = CompositeForeground(original, foregroundPixels);

                // This is a screen DC, not a capture of the background HWND. CAPTUREBLT includes
                // the independent grade/foreground windows in their real DWM presentation order.
                async Task<byte[]> CaptureAsync()
                {
                    await Task.Delay(65);
                    byte[]? previous = null;
                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        DwmFlush();
                        byte[] current = CaptureRegion(bounds);
                        captures++;
                        if (previous != null && RgbEquals(previous, current)) return current;
                        previous = current;
                        await Task.Delay(30);
                    }
                    return previous!;
                }

                byte[] baseline = await CaptureAsync();
                StageResult baselineResult = Analyse(baseline, expectedOriginal);
                stages["baseline"] = baselineResult;
                Require(baselineResult.MaximumExpectedRgbError <= RgbTolerance,
                    "Screen readback did not reproduce the known native-pixel synthetic baseline; capture is not calibrated.");
                Require(baselineResult.OnePixelStripes.Contrast >= 250
                    && baselineResult.FourPixelStripes.Contrast >= 250
                    && baselineResult.StepTransitionPixels <= 1,
                    "The synthetic baseline itself is blurred or scaled; live grade quality is unverified.");

                grade = ScreenGradeSurface.CreateForProbe(bounds, foreground.Handle,
                    show: true, captureExcluded: false);
                captureAffinityVerified = CaptureAllowed(background.Handle) && CaptureAllowed(foreground.Handle)
                    && grade.WindowHandles.Count == 1 && CaptureAllowed(grade.WindowHandles[0]);
                Require(captureAffinityVerified,
                    "Every synthetic HWND must use WDA_NONE; capture exclusion would make the screen comparison invalid.");
                Require(grade.Bounds.Count == 1 && grade.Bounds[0].X == bounds.X && grade.Bounds[0].Y == bounds.Y
                    && grade.Bounds[0].Width == Width && grade.Bounds[0].Height == Height,
                    "The grade probe did not remain inside its synthetic 640x360 rectangle.");
                Require(grade.Apply(1), "The identity grade could not be applied.");

                byte[] identity = await CaptureAsync();
                StageResult identityResult = Analyse(identity, baseline);
                stages["identity"] = identityResult;
                Require(identityResult.MaximumExpectedRgbError <= RgbTolerance,
                    "The identity live grade changes the known screen pixels (possibly blur, scaling or unsupported capture).");
                RequireUnblurred(identityResult, baselineResult, "identity", Require);

                Require(grade.Apply(0), "The grey grade could not be applied.");
                byte[] expectedGrey = Grey(baseline);
                CopyRectangle(baseline, expectedGrey, Foreground);
                byte[] grey = await CaptureAsync();
                StageResult greyResult = Analyse(grey, expectedGrey);
                stages["grey"] = greyResult;
                Require(greyResult.MaximumExpectedRgbError <= RgbTolerance,
                    "The live grey layer differs from the Rec.601 reference or alters foreground/geometry.");
                Require(greyResult.ColourPatches.All(patch => patch.MaximumChannelDifference <= 1
                    && patch.MaximumExpectedRgbError <= RgbTolerance),
                    "Background colour patches were not converted to the expected Rec.601 grey.");
                Require(greyResult.ForegroundMaximumRgbError <= RgbTolerance
                    && greyResult.ForegroundSamples.All(sample => sample.Colour.Maximum - sample.Colour.Minimum >= 250),
                    "A coloured foreground above the grey layer lost its original colour.");
                RequireUnblurred(greyResult, baselineResult, "grey", Require);

                byte[] updatedSource = BuildPattern(updated: true);
                background.UpdatePixels(updatedSource);
                byte[] expectedUpdated = Grey(CompositeForeground(updatedSource, foregroundPixels));
                CopyRectangle(baseline, expectedUpdated, Foreground);
                byte[] updated = await CaptureAsync();
                StageResult updatedResult = Analyse(updated, expectedUpdated);
                stages["updatedGrey"] = updatedResult;
                Require(updatedResult.MaximumExpectedRgbError <= RgbTolerance,
                    "Changing the synthetic source did not produce the current Rec.601 image through the live grade.");
                Require(Math.Abs(updatedResult.LiveMarker.R - greyResult.LiveMarker.R) >= 150,
                    "The live grade did not reflect a changed source; it may be a stale image.");
                Require(updatedResult.ForegroundMaximumRgbError <= RgbTolerance,
                    "The live-source update changed the independent coloured foreground.");

                // Keep the original core phases unchanged. A capture-excluded decorative HWND
                // may be absent from the grade's source even though it is visible on screen.
                // Record that fact against both possible images, without accepting blurred or
                // incorrectly graded pixels anywhere outside this intentional source experiment.
                byte[] reclaimPixels = BuildReclaim();
                foreach (uint affinity in new uint[] { 0, 0x11 })
                {
                    grade.Dispose();
                    grade = null;
                    reclaim?.Dispose();
                    reclaim = null;
                    background.UpdatePixels(original);
                    var reclaimBounds = new RectInt32(bounds.X + Reclaim.X, bounds.Y + Reclaim.Y,
                        Reclaim.Width, Reclaim.Height);
                    reclaim = new PatternWindow(reclaimBounds, reclaimPixels);
                    reclaim.SetCaptureAffinity(affinity);
                    await reclaim.ShowAsync();
                    foreground.BringToTop();

                    byte[] expectedSource = CompositeReclaim(expectedOriginal, reclaimPixels);
                    byte[] reclaimBaseline = await CaptureAsync();
                    int baselineSourceError = MaximumError(reclaimBaseline, expectedSource, Reclaim);
                    int baselineUnderlyingError = MaximumError(reclaimBaseline, expectedOriginal, Reclaim);
                    Require(MaximumErrorOutside(reclaimBaseline, expectedOriginal, Reclaim) <= RgbTolerance,
                        "The reclaim baseline changed pixels outside its own synthetic source rectangle.");
                    if (affinity == 0)
                        Require(baselineSourceError <= RgbTolerance,
                            "The WDA_NONE reclaim source did not reproduce its known raw colours before grading.");

                    grade = ScreenGradeSurface.CreateForProbe(bounds, foreground.Handle,
                        show: true, captureExcluded: false);
                    Require(grade.Apply(0), "The reclaim source experiment could not apply grey.");
                    Require(grade.WindowHandles.Count == 1 && CaptureAllowed(grade.WindowHandles[0]),
                        "The reclaim experiment's grading HWND must remain capturable.");
                    byte[] reclaimGrey = await CaptureAsync();
                    byte[] expectedSourceGrey = Grey(expectedSource);
                    byte[] expectedUnderlyingGrey = Grey(expectedOriginal);
                    CopyRectangle(expectedOriginal, expectedSourceGrey, Foreground);
                    CopyRectangle(expectedOriginal, expectedUnderlyingGrey, Foreground);
                    int gradedSourceError = MaximumError(reclaimGrey, expectedSourceGrey, Reclaim);
                    int gradedUnderlyingError = MaximumError(reclaimGrey, expectedUnderlyingGrey, Reclaim);
                    int outsideError = MaximumErrorOutside(reclaimGrey, expectedUnderlyingGrey, Reclaim);
                    StageResult reclaimCore = Analyse(reclaimGrey, expectedUnderlyingGrey);
                    Require(reclaim.HasCaptureAffinity(affinity),
                        "The synthetic reclaim source lost its requested capture affinity during grading.");
                    Require(outsideError <= RgbTolerance,
                        "The reclaim experiment changed geometry/Rec.601 output outside its source rectangle.");
                    Require(reclaimCore.ForegroundMaximumRgbError <= RgbTolerance,
                        "The reclaim experiment desaturated the independent top foreground.");
                    RequireUnblurred(reclaimCore, baselineResult, "reclaim source experiment", Require);
                    bool sourceIncluded = gradedSourceError <= RgbTolerance;
                    bool underlyingExposed = gradedUnderlyingError <= RgbTolerance;
                    reclaimSourceExperiments.Add(new
                    {
                        RequestedAffinity = affinity, AffinityVerified = reclaim.HasCaptureAffinity(affinity),
                        CaptureExcluded = affinity == 0x11,
                        SourceBounds = new { Reclaim.X, Reclaim.Y, Reclaim.Width, Reclaim.Height },
                        BaselineSourceMaximumRgbError = baselineSourceError,
                        BaselineUnderlyingMaximumRgbError = baselineUnderlyingError,
                        BaselineShowsRawSource = baselineSourceError <= RgbTolerance,
                        BaselineShowsUnderlying = baselineUnderlyingError <= RgbTolerance,
                        GradedSourceMaximumRgbError = gradedSourceError,
                        GradedUnderlyingMaximumRgbError = gradedUnderlyingError,
                        SourceIncludedInGradedImage = sourceIncluded, UnderlyingExposed = underlyingExposed,
                        Classification = sourceIncluded ? "source included and graded"
                            : underlyingExposed ? "source absent; underlying pattern exposed" : "neither reference matches",
                        OutsideSourceMaximumRgbError = outsideError,
                        ForegroundMaximumRgbError = reclaimCore.ForegroundMaximumRgbError,
                        CoreOutsideSourcePassed = outsideError <= RgbTolerance
                            && reclaimCore.ForegroundMaximumRgbError <= RgbTolerance,
                        SourceInclusionDiagnosticOnly = true,
                        reclaimCore.OnePixelStripes, reclaimCore.FourPixelStripes,
                        Samples = new[]
                        {
                            ReclaimSample("red", Reclaim.X + 32, Reclaim.Y + 12, reclaimBaseline,
                                reclaimGrey, expectedSource, expectedSourceGrey, expectedUnderlyingGrey),
                            ReclaimSample("cyan", Reclaim.X + 96, Reclaim.Y + 12, reclaimBaseline,
                                reclaimGrey, expectedSource, expectedSourceGrey, expectedUnderlyingGrey),
                        },
                        Interpretation = "Source inclusion is reported independently. The core grey/clarity/foreground thresholds outside this experimental rectangle are unchanged.",
                    });
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception.GetType().Name + ": " + exception.Message);
            }
            finally
            {
                // Hide synchronously before releasing graphics resources so no intermediate
                // native clear becomes visible between surface teardown and HWND close.
                if (grade != null)
                    foreach (nint handle in grade.WindowHandles) ShowWindow(handle, 0);
                reclaim?.Hide();
                foreground?.Hide();
                background?.Hide();
                try { grade?.Dispose(); }
                catch (Exception exception) { failures.Add("Grade cleanup: " + exception.Message); }
                try { foreground?.Dispose(); }
                catch (Exception exception) { failures.Add("Foreground cleanup: " + exception.Message); }
                try { reclaim?.Dispose(); }
                catch (Exception exception) { failures.Add("Reclaim source cleanup: " + exception.Message); }
                try { background?.Dispose(); }
                catch (Exception exception) { failures.Add("Background cleanup: " + exception.Message); }
            }
            return new
            {
                Passed = failures.Count == 0, Failures = failures, Stages = stages,
                ReclaimSourceExperiments = reclaimSourceExperiments,
                Bounds = new { bounds.X, bounds.Y, bounds.Width, bounds.Height },
                DisplayedSyntheticRegion = displayed, CaptureAffinityVerified = captureAffinityVerified,
                CaptureMethod = "Screen DC BitBlt SRCCOPY | CAPTUREBLT. Core foreground/background/grade HWNDs use WDA_NONE; the separate reclaim source explicitly compares WDA_NONE and WDA_EXCLUDEFROMCAPTURE. Known-pattern and identity calibration required.",
                CapturedOnlySyntheticRegion = true, ScreenCaptureCount = captures,
                ReadbackBytesPerCapture = Width * Height * 4,
                UsedFullscreenColourMatrix = false, RegisteredHooks = false,
                UsedConfiguration = false, AudioPlayed = false, MovedMouse = false,
                RgbTolerance, ElapsedMs = (DateTimeOffset.UtcNow - started).TotalMilliseconds,
                Limitations = "Requires a visible, unobscured synthetic ROI and a capture path reproducing its known baseline. A failed/blank identity stage is a failure, not evidence of correct live grade pixels. No screenshot is saved.",
            };
        }

        private static void RequireUnblurred(StageResult actual, StageResult baseline, string stage,
            Action<bool, string> require)
        {
            require(actual.OnePixelStripes.Contrast >= baseline.OnePixelStripes.Contrast - 3
                && actual.FourPixelStripes.Contrast >= baseline.FourPixelStripes.Contrast - 3
                && actual.StepTransitionPixels <= baseline.StepTransitionPixels + 1,
                "The " + stage + " live grade blurred 1px/4px stripes or broadened a black/white edge.");
        }

        private readonly record struct Rgb(int R, int G, int B)
        {
            public int Maximum => Math.Max(R, Math.Max(G, B));
            public int Minimum => Math.Min(R, Math.Min(G, B));
        }
        private sealed record StripeResult(double WhiteMean, double BlackMean, double Contrast,
            Rgb WhiteSample, Rgb BlackSample);
        private sealed record PatchResult(string Name, Rgb Actual, Rgb Expected,
            int MaximumChannelDifference, int MaximumExpectedRgbError);
        private sealed record ForegroundResult(string Name, Rgb Colour);
        private sealed record StageResult(int MaximumExpectedRgbError, PatchResult[] ColourPatches,
            StripeResult OnePixelStripes, StripeResult FourPixelStripes, int StepTransitionPixels,
            int ForegroundMaximumRgbError, ForegroundResult[] ForegroundSamples, Rgb DynamicPatch, Rgb LiveMarker);

        private static StageResult Analyse(byte[] actual, byte[] expected)
        {
            var patches = new PatchResult[Colours.Length];
            for (int index = 0; index < patches.Length; index++)
            {
                var area = new RectInt32(24 + index * 96 + 16, 40, 48, 48);
                Rgb colour = Pixel(actual, area.X + 24, area.Y + 24);
                patches[index] = new(Colours[index].Name, colour, Pixel(expected, area.X + 24, area.Y + 24),
                    colour.Maximum - colour.Minimum, MaximumError(actual, expected, area));
            }
            int transitionPixels = 0;
            for (int x = StepEdge.X + 16; x < StepEdge.X + StepEdge.Width - 16; x++)
            {
                Rgb value = Pixel(actual, x, StepEdge.Y + StepEdge.Height / 2);
                if (value.R > 10 && value.R < 245) transitionPixels++;
            }
            var foregroundSamples = new ForegroundResult[3];
            for (int index = 0; index < foregroundSamples.Length; index++)
                foregroundSamples[index] = new(Colours[index].Name,
                    Pixel(actual, Foreground.X + index * 32 + 16, Foreground.Y + 24));
            return new(MaximumError(actual, expected, new RectInt32(0, 0, Width, Height)), patches,
                AnalyseStripes(actual, OnePixel, 1), AnalyseStripes(actual, FourPixel, 4), transitionPixels,
                MaximumError(actual, expected, Foreground), foregroundSamples,
                Pixel(actual, Dynamic.X + Dynamic.Width / 2, Dynamic.Y + Dynamic.Height / 2),
                Pixel(actual, LiveMarker.X + LiveMarker.Width / 2, LiveMarker.Y + LiveMarker.Height / 2));
        }

        private static StripeResult AnalyseStripes(byte[] pixels, RectInt32 area, int stripeWidth)
        {
            double white = 0, black = 0;
            int whiteCount = 0, blackCount = 0;
            for (int y = area.Y + 8; y < area.Y + area.Height - 8; y++)
            for (int x = area.X + 16; x < area.X + area.Width - 16; x++)
            {
                Rgb colour = Pixel(pixels, x, y);
                double value = (colour.R + colour.G + colour.B) / 3.0;
                if (((x - area.X) / stripeWidth & 1) == 0) { white += value; whiteCount++; }
                else { black += value; blackCount++; }
            }
            double whiteMean = white / whiteCount, blackMean = black / blackCount;
            return new(whiteMean, blackMean, whiteMean - blackMean,
                Pixel(pixels, area.X + 16, area.Y + 24),
                Pixel(pixels, area.X + 16 + stripeWidth, area.Y + 24));
        }

        private static byte[] BuildPattern(bool updated)
        {
            byte[] pixels = new byte[Width * Height * 4];
            Fill(pixels, Width, new RectInt32(0, 0, Width, Height), new Rgb(48, 64, 80));
            for (int index = 0; index < Colours.Length; index++)
                Fill(pixels, Width, new RectInt32(24 + index * 96, 24, 80, 80), Colours[index].Colour);
            foreach (var item in new[] { (Area: OnePixel, StripeWidth: 1), (Area: FourPixel, StripeWidth: 4) })
                for (int x = 0; x < item.Area.Width; x++)
                {
                    int value = (x / item.StripeWidth & 1) == 0 ? 255 : 0;
                    Fill(pixels, Width, new RectInt32(item.Area.X + x, item.Area.Y, 1, item.Area.Height), new(value, value, value));
                }
            Fill(pixels, Width, StepEdge, new(0, 0, 0));
            Fill(pixels, Width, new RectInt32(StepEdge.X + 128, StepEdge.Y, 128, StepEdge.Height), new(255, 255, 255));
            for (int value = 0; value < 256; value++)
                Fill(pixels, Width, new RectInt32(320 + value, 216, 1, 48), new(value, value, value));
            Fill(pixels, Width, Dynamic, updated ? new(210, 35, 100) : new(24, 190, 60));
            int marker = updated ? 210 : 45;
            Fill(pixels, Width, LiveMarker, new(marker, marker, marker));
            // Physical-pixel corner marks make a shifted/cropped/zoomed readback fail calibration.
            foreach ((int x, int y) in new[] { (4, 4), (Width - 12, 4), (4, Height - 12), (Width - 12, Height - 12) })
            {
                Fill(pixels, Width, new RectInt32(x, y, 8, 8), new(255, 255, 255));
                Fill(pixels, Width, new RectInt32(x, y, 4, 4), new(0, 0, 0));
            }
            return pixels;
        }

        private static byte[] BuildForeground()
        {
            byte[] pixels = new byte[Foreground.Width * Foreground.Height * 4];
            for (int index = 0; index < 3; index++)
                Fill(pixels, Foreground.Width, new RectInt32(index * 32, 0, 32, Foreground.Height), Colours[index].Colour);
            return pixels;
        }

        private static byte[] BuildReclaim()
        {
            byte[] pixels = new byte[Reclaim.Width * Reclaim.Height * 4];
            Fill(pixels, Reclaim.Width, new RectInt32(0, 0, Reclaim.Width / 2, Reclaim.Height), new(255, 0, 0));
            Fill(pixels, Reclaim.Width, new RectInt32(Reclaim.Width / 2, 0, Reclaim.Width / 2, Reclaim.Height), new(0, 255, 255));
            return pixels;
        }

        private static byte[] CompositeReclaim(byte[] background, byte[] reclaim)
        {
            byte[] result = (byte[])background.Clone();
            for (int y = 0; y < Reclaim.Height; y++)
                Buffer.BlockCopy(reclaim, y * Reclaim.Width * 4, result,
                    ((y + Reclaim.Y) * Width + Reclaim.X) * 4, Reclaim.Width * 4);
            return result;
        }

        private static object ReclaimSample(string name, int x, int y, byte[] baseline, byte[] grey,
            byte[] rawSource, byte[] sourceGrey, byte[] underlyingGrey)
            => new
            {
                Name = name, OriginalSourceRgb = Pixel(rawSource, x, y),
                BaselineObservedRgb = Pixel(baseline, x, y), GradedObservedRgb = Pixel(grey, x, y),
                ExpectedSourceGreyRgb = Pixel(sourceGrey, x, y),
                ExpectedUnderlyingGreyRgb = Pixel(underlyingGrey, x, y),
            };

        private static byte[] CompositeForeground(byte[] background, byte[] foreground)
        {
            byte[] result = (byte[])background.Clone();
            for (int y = 0; y < Foreground.Height; y++)
                Buffer.BlockCopy(foreground, y * Foreground.Width * 4, result,
                    ((y + Foreground.Y) * Width + Foreground.X) * 4, Foreground.Width * 4);
            return result;
        }

        private static void Fill(byte[] pixels, int stride, RectInt32 area, Rgb colour)
        {
            for (int y = area.Y; y < area.Y + area.Height; y++)
            for (int x = area.X; x < area.X + area.Width; x++)
            {
                int p = (y * stride + x) * 4;
                pixels[p] = (byte)colour.B; pixels[p + 1] = (byte)colour.G;
                pixels[p + 2] = (byte)colour.R; pixels[p + 3] = 255;
            }
        }

        private static byte[] Grey(byte[] source)
        {
            byte[] result = new byte[source.Length];
            for (int p = 0; p < source.Length; p += 4)
            {
                byte value = (byte)Math.Clamp((int)(ScreenGrade.LumR * source[p + 2]
                    + ScreenGrade.LumG * source[p + 1] + ScreenGrade.LumB * source[p] + .5f), 0, 255);
                result[p] = result[p + 1] = result[p + 2] = value;
                result[p + 3] = 255;
            }
            return result;
        }

        private static void CopyRectangle(byte[] source, byte[] destination, RectInt32 area)
        {
            for (int y = area.Y; y < area.Y + area.Height; y++)
                Buffer.BlockCopy(source, (y * Width + area.X) * 4, destination,
                    (y * Width + area.X) * 4, area.Width * 4);
        }

        private static Rgb Pixel(byte[] pixels, int x, int y)
        {
            int p = (y * Width + x) * 4;
            return new(pixels[p + 2], pixels[p + 1], pixels[p]);
        }

        private static int MaximumError(byte[] actual, byte[] expected, RectInt32 area)
        {
            int maximum = 0;
            for (int y = area.Y; y < area.Y + area.Height; y++)
            for (int x = area.X; x < area.X + area.Width; x++)
            {
                int p = (y * Width + x) * 4;
                for (int channel = 0; channel < 3; channel++)
                    maximum = Math.Max(maximum, Math.Abs(actual[p + channel] - expected[p + channel]));
            }
            return maximum;
        }

        private static int MaximumErrorOutside(byte[] actual, byte[] expected, RectInt32 excluded)
        {
            int maximum = 0;
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                if (x >= excluded.X && x < excluded.X + excluded.Width
                    && y >= excluded.Y && y < excluded.Y + excluded.Height) continue;
                int p = (y * Width + x) * 4;
                for (int channel = 0; channel < 3; channel++)
                    maximum = Math.Max(maximum, Math.Abs(actual[p + channel] - expected[p + channel]));
            }
            return maximum;
        }

        private static bool RgbEquals(byte[] left, byte[] right)
        {
            for (int p = 0; p < left.Length; p += 4)
                if (left[p] != right[p] || left[p + 1] != right[p + 1] || left[p + 2] != right[p + 2]) return false;
            return true;
        }

        private static RectInt32 ChooseBounds()
        {
            GetCursorPos(out NativePoint point);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromPoint(point, 2), ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (info.Work.Right - info.Work.Left < Width + 32 || info.Work.Bottom - info.Work.Top < Height + 32)
                throw new InvalidOperationException("The selected monitor work area cannot contain the synthetic 640x360 test.");
            return new RectInt32(info.Work.Right - Width - 16, info.Work.Top + 16, Width, Height);
        }

        private static bool CaptureAllowed(nint hwnd)
            => GetWindowDisplayAffinity(hwnd, out uint affinity) && affinity == 0;

        private sealed class PatternWindow : IDisposable
        {
            private readonly Window _window;
            private readonly Grid _root;
            private readonly Image _image;
            private readonly WriteableBitmap _bitmap;
            private readonly RectInt32 _bounds;
            private WheelGlassSurface? _glass;
            private bool _backgroundSubclassInstalled;
            internal nint Handle { get; }

            internal PatternWindow(RectInt32 bounds, byte[] pixels)
            {
                _bounds = bounds;
                _window = new Window();
                try
                {
                    Handle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
                    _bitmap = new WriteableBitmap(bounds.Width, bounds.Height);
                    _image = new Image { Source = _bitmap, Stretch = Stretch.Fill };
                    _root = new Grid { Background = new SolidColorBrush(Colors.Black) };
                    _root.Children.Add(_image);
                    _window.Content = _root;
                    _window.AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                    RemoveFrame();
                    if (!SetWindowDisplayAffinity(Handle, 0) || !CaptureAllowed(Handle))
                        throw new InvalidOperationException("The synthetic pattern window could not opt into screen capture.");
                    UpdatePixels(pixels);
                    Place();
                }
                catch { Dispose(); throw; }
            }

            internal async Task ShowAsync()
            {
                _window.AppWindow.Show(false);
                RemoveFrame();
                Place();
                await Task.Delay(60);
                Place();
                double scale = _root.XamlRoot?.RasterizationScale
                    ?? throw new InvalidOperationException("The synthetic pattern has no XamlRoot.");
                _root.Width = _image.Width = _bounds.Width / scale;
                _root.Height = _image.Height = _bounds.Height / scale;
                _root.Measure(new Windows.Foundation.Size(_root.Width, _root.Height));
                _root.Arrange(new Windows.Foundation.Rect(0, 0, _root.Width, _root.Height));
                _root.UpdateLayout();
                if (!GetClientRect(Handle, out NativeRect client) || client.Right != _bounds.Width || client.Bottom != _bounds.Height)
                    throw new InvalidOperationException("The synthetic window client does not match its physical-pixel bounds.");
                if (!GetWindowRect(Handle, out NativeRect actual) || actual.Left != _bounds.X || actual.Top != _bounds.Y
                    || actual.Right - actual.Left != _bounds.Width || actual.Bottom - actual.Top != _bounds.Height)
                    throw new InvalidOperationException("The synthetic window moved outside its requested physical bounds.");
            }

            internal void UpdatePixels(byte[] pixels)
            {
                if (pixels.Length != _bounds.Width * _bounds.Height * 4) throw new ArgumentException("Incorrect synthetic bitmap size.");
                using var stream = _bitmap.PixelBuffer.AsStream();
                stream.Write(pixels, 0, pixels.Length);
                _bitmap.Invalidate();
            }

            internal bool HasCaptureAffinity(uint affinity)
                => GetWindowDisplayAffinity(Handle, out uint actual) && actual == affinity;

            internal void SetCaptureAffinity(uint affinity)
            {
                if (!SetWindowDisplayAffinity(Handle, affinity) || !HasCaptureAffinity(affinity))
                    throw new InvalidOperationException("The synthetic reclaim source could not apply its requested capture affinity.");
            }

            internal void BringToTop() => Place();

            internal WheelGlassSurface AttachProbeGlass()
            {
                var backdrop = new RadialMenu.TransparentSystemBackdrop();
                _window.SystemBackdrop = backdrop;
                var blurBehind = new BlurBehind { Flags = 1, Enable = true };
                if (DwmEnableBlurBehindWindow(Handle, ref blurBehind) < 0
                    || !SetWindowSubclass(Handle, ClearProbeBackground, new UIntPtr(97), UIntPtr.Zero))
                    throw new InvalidOperationException("The synthetic glass window could not enable transparent composition.");
                _backgroundSubclassInstalled = true;
                _root.Background = new SolidColorBrush(Colors.Transparent);
                _image.Visibility = Visibility.Collapsed;
                double scale = _root.XamlRoot?.RasterizationScale
                    ?? throw new InvalidOperationException("The synthetic glass host has no XamlRoot.");
                var host = new Border
                {
                    Width = 192 / scale, Height = 192 / scale, CornerRadius = new CornerRadius(96 / scale),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                _root.Children.Add(host);
                _root.UpdateLayout();
                _glass = WheelGlassSurface.Attach(host, Handle, true, backdrop.Compositor,
                    backdrop.SetBrush, materialOpacity: 1d)
                    ?? throw new InvalidOperationException("The real glass brush could not attach: " + WheelGlassSurface.LastAttachFailure);
                // Tint belongs to XAML above the source and would otherwise introduce its own
                // colour cast. Only this disposable test host removes it; production is unchanged.
                host.Background = new SolidColorBrush(Colors.Transparent);
                nint dc = GetDC(Handle);
                if (dc != 0)
                {
                    try { if (GetClientRect(Handle, out NativeRect rect)) FillRect(dc, ref rect, GetStockObject(4)); }
                    finally { ReleaseDC(Handle, dc); }
                }
                _glass.RefreshDiagnostics();
                return _glass;
            }

            private void Place()
            {
                if (!SetWindowPos(Handle, new nint(-1), _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, 0x30))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Synthetic window positioning failed.");
            }

            private void RemoveFrame()
            {
                SetWindowLong(Handle, -16, (GetWindowLong(Handle, -16) & ~0x00C40000) | unchecked((int)0x80000000));
                SetWindowLong(Handle, -20, (GetWindowLong(Handle, -20) & ~0x00020301) | 0x08000080);
                int corners = 1, noBorder = unchecked((int)0xFFFFFFFE);
                DwmSetWindowAttribute(Handle, 33, ref corners, sizeof(int));
                DwmSetWindowAttribute(Handle, 34, ref noBorder, sizeof(int));
            }

            internal void Hide() => ShowWindow(Handle, 0);
            public void Dispose()
            {
                Hide();
                _glass?.Dispose(); _glass = null;
                if (_backgroundSubclassInstalled)
                {
                    RemoveWindowSubclass(Handle, ClearProbeBackground, new UIntPtr(97));
                    _backgroundSubclassInstalled = false;
                }
                _window.Close();
            }
        }

        private delegate nint NativeSubclass(nint hwnd, uint message, nint wParam, nint lParam, UIntPtr id, UIntPtr data);
        private static readonly NativeSubclass ClearProbeBackground = ClearProbeBackgroundProc;
        private static nint ClearProbeBackgroundProc(nint hwnd, uint message, nint wParam, nint lParam, UIntPtr id, UIntPtr data)
        {
            if (message == 0x14 && wParam != 0 && GetClientRect(hwnd, out NativeRect rect))
            {
                FillRect(wParam, ref rect, GetStockObject(4));
                return 1;
            }
            return DefSubclassProc(hwnd, message, wParam, lParam);
        }

        private static byte[] CaptureRegion(RectInt32 bounds)
        {
            if (bounds.Width != Width || bounds.Height != Height)
                throw new InvalidOperationException("The probe can only read its exact synthetic 640x360 rectangle.");
            nint desktop = 0, memory = 0, bitmap = 0, previous = 0;
            try
            {
                desktop = GetDC(0);
                if (desktop == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                memory = CreateCompatibleDC(desktop);
                if (memory == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                var info = new BitmapInfo
                {
                    Header = new BitmapHeader { Size = (uint)Marshal.SizeOf<BitmapHeader>(), Width = Width,
                        Height = -Height, Planes = 1, BitCount = 32 },
                };
                bitmap = CreateDIBSection(desktop, ref info, 0, out nint bits, 0, 0);
                if (bitmap == 0 || bits == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                previous = SelectObject(memory, bitmap);
                if (previous == 0 || previous == new nint(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!BitBlt(memory, 0, 0, Width, Height, desktop, bounds.X, bounds.Y, 0x40CC0020))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Synthetic desktop ROI capture failed.");
                GdiFlush();
                byte[] pixels = new byte[Width * Height * 4];
                Marshal.Copy(bits, pixels, 0, pixels.Length);
                return pixels;
            }
            finally
            {
                if (previous != 0 && previous != new nint(-1) && memory != 0) SelectObject(memory, previous);
                if (bitmap != 0) DeleteObject(bitmap);
                if (memory != 0) DeleteDC(memory);
                if (desktop != 0) ReleaseDC(0, desktop);
            }
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
        [StructLayout(LayoutKind.Sequential)] private struct BitmapHeader
        { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant; }
        [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapHeader Header; public uint Colour; }
        [StructLayout(LayoutKind.Sequential)] private struct BlurBehind { public uint Flags; [MarshalAs(UnmanagedType.Bool)] public bool Enable; public nint Region; [MarshalAs(UnmanagedType.Bool)] public bool Transition; }
        [DllImport("user32.dll", SetLastError = true)] private static extern nint GetDC(nint hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
        [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint hwnd, int index, int value);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
        [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();
        [DllImport("dwmapi.dll")] private static extern int DwmEnableBlurBehindWindow(nint hwnd, ref BlurBehind blur);
        [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, NativeSubclass callback, UIntPtr id, UIntPtr data);
        [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, NativeSubclass callback, UIntPtr id);
        [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
        [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref NativeRect rect, nint brush);
        [DllImport("gdi32.dll")] private static extern nint GetStockObject(int item);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern nint SelectObject(nint dc, nint value);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
        [DllImport("gdi32.dll")] private static extern bool GdiFlush();
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    }
}
