using System;
using System.IO;
using System.Text.Json;
using Action_Wheel.Core;

namespace Action_Wheel.Services
{
    /// <summary>The board's chosen working file, rather than a one-time export destination.</summary>
    public static class NoteBoardLocationSettings
    {
        public static string FilePath => Path.Combine(AppDataPaths.DirectoryPath, "noteboard-location.json");
        public static string DefaultNotePath => Path.Combine(AppDataPaths.DirectoryPath, "noteboard.txt");

        public static bool TryLoad(out string notePath, out string error, string? settingsPath = null)
        {
            notePath = DefaultNotePath;
            error = string.Empty;
            try
            {
                string json;
                try { json = File.ReadAllText(settingsPath ?? FilePath); }
                catch (FileNotFoundException) { return true; }
                catch (DirectoryNotFoundException) { return true; }

                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("path", out var property)
                    || property.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(property.GetString()))
                    throw new FormatException("笔记位置设置缺少有效路径。");
                notePath = Path.GetFullPath(property.GetString()!);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool TrySave(string notePath, out string error, string? settingsPath = null)
        {
            try
            {
                string normalized = Path.GetFullPath(notePath);
                string json = JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string, string>
                {
                    ["path"] = normalized,
                });
                return AtomicFile.TryWriteText(settingsPath ?? FilePath, json, out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
