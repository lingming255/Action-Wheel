using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Action_Wheel.Overlay;
using Microsoft.UI.Xaml;

namespace Action_Wheel.Services
{
    /// <summary>Explicit hidden diagnostics; does not start launcher hooks or write user settings.</summary>
    internal static class WheelReuseSmoke
    {
        internal static async void Run(string reportPath, Action exit)
        {
            Window? owner = null;
            var report = new Dictionary<string, object?>
            {
                ["Passed"] = false,
                ["RegisteredHooks"] = false,
                ["AudioPlayed"] = false,
                ["SavedUserConfiguration"] = false,
                ["ScreenPixelsReadBack"] = false,
                ["DisplayedEffect"] = false,
                ["SimulatedInput"] = false,
                ["Boundary"] = "Opt-in DWM-cloaked real HWND lifecycle and identity checks. Construction, preparation and synchronous Show/close wall times are component measurements, not input-to-visible-first-frame or evidence of perceived on-screen smoothness. Synthetic actions never dispatch. Refresh retirement uses synthetic composition feedback only; no desktop capture, grading, model inference or pixel readback occurs. Show may briefly acquire foreground while cloaked; the original foreground is restored without simulated input.",
            };
            try
            {
                if (!Path.IsPathFullyQualified(reportPath))
                    throw new ArgumentException("Reuse report path must be absolute.");
                WheelSounds.Configure(WheelSoundSettings.Default with { Enabled = false });
                TriggerPreferences preferences = TriggerSettings.LoadPreferences();
                var appearance = (preferences.Appearance ?? RingAppearance.Default).Normalised();
                owner = new Window();
                owner.AppWindow.Hide();
                object result = await RadialMenu.RunReuseProbeAsync(appearance);
                report["Result"] = result;
                object launcherPool = await LauncherService.RunReusePoolProbeAsync(appearance);
                report["LauncherPool"] = launcherPool;
                report["Passed"] = JsonSerializer.SerializeToElement(result).GetProperty("Passed").GetBoolean()
                    && JsonSerializer.SerializeToElement(launcherPool).GetProperty("Passed").GetBoolean();
                report["Appearance"] = appearance;
                report["PreferencesReadOnly"] = true;
            }
            catch (Exception exception)
            {
                report["Error"] = exception.ToString();
            }
            finally
            {
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
