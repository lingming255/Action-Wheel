using System.Text.Json;
using Action_Wheel.Services;

string temporaryRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"ActionWheel-SmokeReportCheck-{Guid.NewGuid():N}"));
Directory.CreateDirectory(temporaryRoot);
int checks = 0;
void Require(bool passed, string label)
{
    checks++;
    if (!passed)
        throw new Exception(label);
}

try
{
    string reportPath = Path.Combine(temporaryRoot, "report.json");
    Require(SmokeReportStatus.GetExitCode(reportPath) != 0, "Missing reports must fail");
    Require(SmokeReportStatus.Prepare(reportPath), "Preparing a missing report must succeed");
    string missingParent = Path.Combine(temporaryRoot, "missing-parent");
    Require(SmokeReportStatus.Prepare(Path.Combine(missingParent, "report.json")), "A missing parent directory must not block preparation");
    Require(!Directory.Exists(missingParent), "Preparation must leave parent creation to the probe");
    foreach (var (contents, expected, label) in new[]
    {
        ("{\"Passed\":true}", 0, "Explicit success must pass"),
        ("{\"Passed\":false}", 1, "Explicit failure must fail"),
        ("{\"Passed\":false,\"Nested\":{\"Passed\":true}}", 1, "Root failure overrides a nested success"),
        ("{\"Nested\":{\"Passed\":true}}", 1, "Nested success cannot replace a missing root result"),
        ("{\"Passed\":\"true\"}", 1, "String results must fail"),
        ("{\"Passed\":1}", 1, "Numeric results must fail"),
        ("{\"Passed\":null}", 1, "Null results must fail"),
        ("{\"passed\":true}", 1, "Root property names are case sensitive"),
        ("[{\"Passed\":true}]", 1, "Array reports must fail"),
        ("true", 1, "Scalar reports must fail"),
        ("null", 1, "Null reports must fail"),
        ("{\"Passed\":", 1, "Truncated JSON must fail"),
        ("", 1, "Empty reports must fail"),
    })
    {
        File.WriteAllText(reportPath, contents);
        Require(SmokeReportStatus.GetExitCode(reportPath) == expected, label);
    }

    File.WriteAllText(reportPath, "{\"Passed\":true}");
    using (var locked = new FileStream(reportPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        Require(SmokeReportStatus.GetExitCode(reportPath) != 0, "Unreadable reports must fail");
        Require(!SmokeReportStatus.Prepare(reportPath), "A locked previous report must fail preparation");
    }
    Require(SmokeReportStatus.Prepare(reportPath), "An old successful report must be removable");
    Require(!File.Exists(reportPath), "Preparation must remove an old successful report");
    Require(SmokeReportStatus.GetExitCode(reportPath) != 0, "A run that never writes a new report must fail after preparation");
    Require(!SmokeReportStatus.Prepare(temporaryRoot), "A directory must fail preparation");
    Require(!SmokeReportStatus.Prepare("relative-report.json"), "Relative paths must fail preparation");
    Require(!SmokeReportStatus.Prepare(Path.Combine(temporaryRoot, "invalid\0.json")), "Invalid paths must fail preparation");
    Require(SmokeReportStatus.GetExitCode(temporaryRoot) != 0, "Directory paths must fail");
    Require(SmokeReportStatus.GetExitCode("relative-report.json") != 0, "Relative report paths must fail");
    Require(SmokeReportStatus.GetExitCode("\0") != 0, "Invalid report paths must fail");

    Console.WriteLine(JsonSerializer.Serialize(new { Passed = true, Checks = checks, LinkedActualReportReader = true, UsedTemporaryFilesOnly = true }));
}
finally
{
    string resolvedRoot = Path.GetFullPath(temporaryRoot);
    string temporaryParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (resolvedRoot.StartsWith(temporaryParent, StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(resolvedRoot).StartsWith("ActionWheel-SmokeReportCheck-", StringComparison.Ordinal))
        Directory.Delete(resolvedRoot, recursive: true);
}
