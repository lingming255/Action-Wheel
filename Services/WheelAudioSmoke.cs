using System;
using System.IO;
using System.Text.Json;
using Microsoft.UI.Xaml;

namespace Action_Wheel.Services
{
    /// <summary>Opt-in silent hardware audio probe; no settings, hooks or mixer changes.</summary>
    internal static class WheelAudioSmoke
    {
        internal static async void Run(string reportPath, Action exit)
        {
            Window? owner = null;
            try
            {
                if (!Path.IsPathFullyQualified(reportPath))
                    throw new ArgumentException("Audio report path must be absolute.");
                owner = new Window();
                owner.AppWindow.Hide();
                object report = await WheelSounds.RunSilentProbeAsync();
                File.WriteAllText(reportPath, JsonSerializer.Serialize(report,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception error)
            {
                if (Path.IsPathFullyQualified(reportPath))
                    File.WriteAllText(reportPath, JsonSerializer.Serialize(new
                    {
                        Passed = false, Error = error.ToString(), AudibleAudioPlayed = false,
                        SavedUserConfiguration = false,
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
