using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Action_Wheel.Overlay;
using Microsoft.UI.Xaml;

namespace Action_Wheel.Services
{
    /// <summary>Explicit opening-cost diagnostic. Reads preferences; never starts launcher hooks.</summary>
    internal static class WheelOpeningSmoke
    {
        internal static async void Run(string reportPath, Action exit)
        {
            Window? owner = null;
            var rounds = new List<object>();
            var report = new Dictionary<string, object?>
            {
                ["Passed"] = false, ["Rounds"] = rounds,
                ["RegisteredHooks"] = false, ["AudioPlayed"] = false,
                ["SavedUserConfiguration"] = false, ["ScreenPixelsReadBack"] = false,
                ["DisplayedEffect"] = false,
            };
            var initialNative = ScreenGradeSurface.GetNativeCallDiagnostics();
            try
            {
                if (!Path.IsPathFullyQualified(reportPath))
                    throw new ArgumentException("Opening report path must be absolute.");
                // A dedicated CLI process can mute its own worker without persisting a setting.
                WheelSounds.Configure(WheelSoundSettings.Default with { Enabled = false });
                TriggerPreferences preferences = TriggerSettings.LoadPreferences();
                var appearance = (preferences.Appearance ?? RingAppearance.Default).Normalised();
                var reclaim = (preferences.Reclaim ?? ReclaimAnimationSettings.Default).Normalised();
                owner = new Window();
                owner.AppWindow.Hide();
                long[]? previousNativeHosts = null;
                for (int round = 0; round < 2; round++)
                {
                    object result = await RadialMenu.RunOpeningProbeRoundAsync(round, appearance, reclaim, previousNativeHosts);
                    rounds.Add(result);
                    previousNativeHosts = JsonSerializer.SerializeToElement(result)
                        .GetProperty("NativeCache").GetProperty("HostsAfterRent")
                        .EnumerateArray().Select(value => value.GetInt64()).ToArray();
                }
                bool passed = true;
                foreach (object round in rounds)
                    passed &= JsonSerializer.SerializeToElement(round).GetProperty("Passed").GetBoolean();
                report["Passed"] = passed;
                report["Kind"] = "Opt-in cached native opening component and UI dispatcher timing";
                report["Appearance"] = appearance;
                report["ReclaimSettings"] = reclaim;
                report["PreferencesReadOnly"] = true;
                report["UsedSyntheticDefaultActions"] = true;
                report["Boundary"] = "Cloaked real-monitor geometry; synthetic actions and cold hidden reclaim. Native Warm cost is reported separately and excluded from opening dispatcher samples; native hosts/session are reused across rounds and destroyed in final cleanup. Stage wall times and dispatcher intervals do not measure input-to-first-pixel or establish perceived on-screen smoothness. Native magnifier source refresh runs while cloaked; no screenshot is read back or saved.";
            }
            catch (Exception exception)
            {
                report["Passed"] = false;
                report["Error"] = exception.ToString();
            }
            finally
            {
                try
                {
                    var beforeShutdown = ScreenGradeSurface.GetNativeCallDiagnostics();
                    ScreenGradeSurface.ShutdownCache();
                    var afterShutdown = ScreenGradeSurface.GetNativeCallDiagnostics();
                    bool released = afterShutdown.SessionUsers == initialNative.SessionUsers && !afterShutdown.HasIdleGroup;
                    report["NativeFinalCleanup"] = new { Initial = initialNative, Before = beforeShutdown, After = afterShutdown, Released = released };
                    if (!released) report["Passed"] = false;
                }
                catch (Exception exception)
                {
                    report["Passed"] = false;
                    report["NativeFinalCleanupError"] = exception.ToString();
                }
                try
                {
                    if (Path.IsPathFullyQualified(reportPath))
                        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                }
                finally
                {
                    owner?.Close();
                    exit();
                }
            }
        }
    }
}
