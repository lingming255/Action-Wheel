using System;
using System.IO;
using System.Text.Json;
using Action_Wheel.Overlay;
using Microsoft.UI.Xaml;

namespace Action_Wheel.Services
{
    internal static class WheelInputSmoke
    {
        internal static async void Run(string reportPath, Action exit)
        {
            Window? lifetime = null;
            try
            {
                if (!Path.IsPathFullyQualified(reportPath))
                    throw new ArgumentException("Input smoke report path must be absolute.");
                lifetime = new Window();
                lifetime.AppWindow.Hide();
                object report = await RadialMenu.RunInputProbeAsync();
                File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception error)
            {
                if (Path.IsPathFullyQualified(reportPath))
                    File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Passed = false, Error = error.ToString() },
                        new JsonSerializerOptions { WriteIndented = true }));
            }
            finally
            {
                lifetime?.Close();
                exit();
            }
        }
    }
}
