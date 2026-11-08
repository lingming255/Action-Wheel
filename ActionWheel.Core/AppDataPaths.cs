using System;
using System.IO;

namespace Action_Wheel.Core
{
    /// <summary>Locations used for user-owned configuration. No telemetry or activity history is stored.</summary>
    public static class AppDataPaths
    {
        public static string DirectoryPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ActionWheel");

        /// <summary>
        /// Where icons extracted from an application are kept.
        /// </summary>
        /// <remarks>
        /// A copy rather than a reference to the original executable: the button has to keep drawing
        /// after the app is updated, moved or uninstalled, and re-reading the icon out of an .exe on
        /// every menu open would put a shell call on the path that has to stay at a few milliseconds.
        /// </remarks>
        public static string IconsDirectoryPath => Path.Combine(DirectoryPath, "Icons");

        /// <summary>Local files the user keeps for quick access from the radial menu.</summary>
        public static string QuickInfoDirectoryPath => Path.Combine(DirectoryPath, "QuickInfo");

        /// <summary>Removes activity logs left by versions released before logging was retired.</summary>
        public static void DeleteLegacyLogs()
        {
            DeleteIfPresent(Path.Combine(DirectoryPath, "log.txt"));
            DeleteIfPresent(Path.Combine(DirectoryPath, "log.previous.txt"));
        }

        private static void DeleteIfPresent(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
