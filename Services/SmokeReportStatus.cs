using System;
using System.IO;
using System.Text.Json;

namespace Action_Wheel.Services
{
    /// <summary>Only a readable report with an explicit successful root result passes diagnostics.</summary>
    internal static class SmokeReportStatus
    {
        internal static bool Prepare(string reportPath)
        {
            try
            {
                if (!Path.IsPathFullyQualified(reportPath))
                    return false;

                // Never let an earlier successful run mask a failure to write this run's report.
                File.Delete(reportPath);
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                // Some probes create their own output directory; preparation need not create it.
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
            {
                return false;
            }
        }

        internal static int GetExitCode(string reportPath)
        {
            try
            {
                if (!Path.IsPathFullyQualified(reportPath))
                    return 1;

                using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
                return report.RootElement.ValueKind == JsonValueKind.Object
                    && report.RootElement.TryGetProperty("Passed", out JsonElement passed)
                    && passed.ValueKind == JsonValueKind.True ? 0 : 1;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or JsonException)
            {
                return 1;
            }
        }
    }
}
