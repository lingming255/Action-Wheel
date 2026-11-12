using System;
using System.IO;
using System.Text.Json;
using Action_Wheel.Settings;
using Microsoft.UI.Xaml;

namespace Action_Wheel.Services
{
    /// <summary>Opt-in hidden binding checks; reads settings without saving or starting audio.</summary>
    internal static class SettingsSmoke
    {
        internal static async void Run(string reportPath, Action exit)
        {
            Window? lifetimeWindow = null;
            try
            {
                if (!Path.IsPathFullyQualified(reportPath))
                    throw new ArgumentException("Settings smoke report path must be absolute.");
                // Closing the settings probe must not end WinUI before its report is written.
                lifetimeWindow = new Window();
                lifetimeWindow.AppWindow.Hide();
                object report = await SettingsWindow.RunBilingualSettingsProbeAsync();
                File.WriteAllText(reportPath, JsonSerializer.Serialize(report,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception error)
            {
                if (Path.IsPathFullyQualified(reportPath))
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
