using System;
using System.IO;
using System.Text.Json;
using Action_Wheel.Overlay;
using Microsoft.UI.Xaml;

namespace Action_Wheel.Services
{
    /// <summary>Opt-in native placement checks; cloaked windows do not capture or alter the desktop.</summary>
    internal static class WheelPositionSmoke
    {
        internal static async void Run(string reportPath, Action exit)
        {
            Window? lifetimeWindow = null;
            try
            {
                if (!Path.IsPathFullyQualified(reportPath))
                    throw new ArgumentException("Wheel placement report path must be absolute.");
                // A hidden owner keeps WinUI alive while the individual test menus close.
                lifetimeWindow = new Window();
                lifetimeWindow.AppWindow.Hide();
                object report = await RadialMenu.RunPositionProbeAsync();
                File.WriteAllText(reportPath, JsonSerializer.Serialize(report,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception error)
            {
                File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Passed = false, Error = error.ToString() },
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            finally
            {
                GpuRefreshDevice.ShutdownPool();
                lifetimeWindow?.Close();
                exit();
            }
        }
    }
}
