using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Action_Wheel.Services
{
    internal sealed partial class LaunchWindowFollower
    {
        private sealed record LaunchIdentity(string? Executable, string Affinity, bool Settings,
            string? PackageFamily = null, bool ReusablePlainExe = false);
        private readonly record struct CandidateEvidence(bool Associated, bool Direct, bool NewWindow,
            bool Foreground, bool WasForeground, bool ForegroundEvent, bool NameChanged,
            bool Shown, bool UserMoved, bool RectangleChanged, long ElapsedMs);

        private static bool MayRegister(long sequence, long latest, long retiredFloor) =>
            sequence > retiredFloor && sequence >= latest;

        private static int CandidateScore(CandidateEvidence candidate)
        {
            if ((!candidate.Associated && !candidate.Direct) || candidate.UserMoved
                || !candidate.NewWindow && candidate.RectangleChanged) return 0;
            if (candidate.NewWindow) return (candidate.Direct ? 500 : 300) + (candidate.Foreground ? 50 : 0);
            // Existing single-instance windows need evidence tied to this launch. A current
            // foreground HWND alone could simply be the app the user was already working in.
            if (!candidate.Foreground || candidate.ElapsedMs > 3000) return 0;
            if (candidate.ForegroundEvent || !candidate.WasForeground) return candidate.Direct ? 250 : 200;
            if (candidate.NameChanged && candidate.WasForeground) return 180;
            return 0;
        }

        private static WindowSnapshot? SelectUnique(List<(WindowSnapshot Window, int Score)> candidates)
        {
            if (candidates.Count == 0) return null;
            int best = candidates.Max(candidate => candidate.Score);
            var matches = candidates.Where(candidate => candidate.Score == best).ToArray();
            return matches.Length == 1 ? matches[0].Window : null;
        }

        private static LaunchIdentity ResolveIdentity(string target, string arguments)
        {
            string value = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));
            string? executable = null;
            bool settings = false;
            bool plainExecutable = false;
            string association = string.Empty;
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !uri.IsFile)
            {
                association = uri.Scheme.ToLowerInvariant();
                settings = association == "ms-settings";
                executable = settings
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                        "ImmersiveControlPanel", "SystemSettings.exe")
                    : AssociationExecutable(association, protocol: true);
            }
            else
            {
                if (uri?.IsFile == true) value = uri.LocalPath;
                if (Directory.Exists(value)) executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                else if (string.Equals(Path.GetExtension(value), ".exe", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetExtension(value), ".com", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(value).Length == 0)
                {
                    executable = ResolveProgram(value);
                    plainExecutable = executable != null && string.IsNullOrWhiteSpace(arguments);
                }
                else
                {
                    association = Path.GetExtension(value);
                    if (!string.Equals(association, ".lnk", StringComparison.OrdinalIgnoreCase))
                        executable = AssociationExecutable(association, protocol: false);
                }
            }
            executable = NormaliseExecutable(executable);
            string? packageFamily = TrustedStubPackage(executable);
            // A generic UWP/shell host is insufficient identity. Settings above is deliberately
            // resolved to SystemSettings, with a child-process check for its native frame host.
            if (executable != null && new[] { "ApplicationFrameHost.exe", "rundll32.exe", "dllhost.exe" }
                    .Contains(Path.GetFileName(executable), StringComparer.OrdinalIgnoreCase)) executable = null;
            string affinity = packageFamily != null ? "package:" + packageFamily
                : executable != null ? "exe:" + executable
                : "target:" + (association.Length != 0 ? association : value);
            return new LaunchIdentity(executable, affinity, settings, packageFamily, plainExecutable);
        }

        private static string? TrustedStubPackage(string? executable)
        {
            if (executable == null) return null;
            string file = Path.GetFileName(executable);
            string? directory = Path.GetDirectoryName(executable);
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            bool windowsStub = new[] { windows, Path.Combine(windows, "System32"), Path.Combine(windows, "SysWOW64") }
                .Any(path => string.Equals(directory, path, StringComparison.OrdinalIgnoreCase));
            if (windowsStub && string.Equals(file, "calc.exe", StringComparison.OrdinalIgnoreCase))
                return "Microsoft.WindowsCalculator_8wekyb3d8bbwe";
            if (windowsStub && string.Equals(file, "notepad.exe", StringComparison.OrdinalIgnoreCase))
                return "Microsoft.WindowsNotepad_8wekyb3d8bbwe";
            string aliases = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps");
            if (string.Equals(directory, aliases, StringComparison.OrdinalIgnoreCase)
                && string.Equals(file, "wt.exe", StringComparison.OrdinalIgnoreCase))
                return "Microsoft.WindowsTerminal_8wekyb3d8bbwe";
            return null;
        }

        private static string? ResolveProgram(string value)
        {
            try
            {
                if (File.Exists(value)) return Path.GetFullPath(value);
                var buffer = new StringBuilder(32768);
                uint length = SearchPath(null, value, Path.GetExtension(value).Length == 0 ? ".exe" : null,
                    (uint)buffer.Capacity, buffer, 0);
                return length > 0 && length < buffer.Capacity ? buffer.ToString() : null;
            }
            catch (Exception) { return null; }
        }

        private static string? AssociationExecutable(string association, bool protocol)
        {
            if (association.Length == 0) return null;
            uint length = 32768;
            var buffer = new StringBuilder((int)length);
            return AssocQueryString(protocol ? 0x1000u : 0u, 2, association, null, buffer, ref length) == 0
                ? buffer.ToString() : null;
        }

        private static string? NormaliseExecutable(string? executable)
        {
            if (string.IsNullOrWhiteSpace(executable)) return null;
            try
            {
                string path = Environment.ExpandEnvironmentVariables(executable.Trim().Trim('"'));
                if (path.StartsWith("\\\\?\\", StringComparison.Ordinal)) path = path[4..];
                return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path).ToLowerInvariant() : null;
            }
            catch (Exception) { return null; }
        }

        internal static object RunSelectionProbe()
        {
            var failures = new List<string>();
            int checks = 0;
            void Check(bool condition, string message) { checks++; if (!condition) failures.Add(message); }
            var none = new CandidateEvidence(false, false, false, true, true, false, false, false, false, false, 80);
            Check(CandidateScore(none) == 0, "An unrelated foreground window was eligible.");
            var handler = none with { Associated = true };
            Check(CandidateScore(handler) == 0, "An unchanged existing handler window was eligible.");
            Check(CandidateScore(handler with { NewWindow = true }) > 0, "A new handler window was rejected.");
            Check(CandidateScore(handler with { NameChanged = true }) > 0, "A foreground single-instance title event was rejected.");
            Check(CandidateScore(handler with { NameChanged = true, Foreground = false }) == 0,
                "A background page title change was eligible.");
            Check(CandidateScore(handler with { ForegroundEvent = true }) > 0, "An activated existing handler was rejected.");
            Check(CandidateScore(handler with { WasForeground = false }) > 0, "A matching foreground transition was rejected.");
            Check(CandidateScore(handler with { NewWindow = true, UserMoved = true }) == 0,
                "A user-dragged window was eligible.");
            Check(CandidateScore(handler with { NameChanged = true, RectangleChanged = true }) == 0,
                "An existing window moved since launch was eligible.");
            Check(CandidateScore(handler with { ForegroundEvent = true, ElapsedMs = 4000 }) == 0,
                "A late existing activation was eligible.");
            Check(CandidateScore(handler with { NewWindow = true, Direct = true })
                > CandidateScore(handler with { NewWindow = true }), "Direct process evidence did not take priority.");
            var one = new WindowSnapshot(1, new ProcessIdentity(10, "c:\\browser.exe", 1), new NativeRect(0, 0, 900, 600));
            var two = one with { Handle = 2 };
            Check(SelectUnique(new() { (one, 300), (two, 300) }) == null, "Ambiguous equal candidates were selected.");
            Check(SelectUnique(new() { (one, 350), (two, 300) })?.Handle == 1, "The uniquely evidenced window was rejected.");
            Check(string.Equals(new LaunchIdentity("c:\\browser.exe", "exe:c:\\browser.exe", false).Affinity,
                new LaunchIdentity("C:\\BROWSER.EXE", "exe:C:\\BROWSER.EXE", false).Affinity, StringComparison.OrdinalIgnoreCase),
                "The same handler had case-sensitive request affinity.");
            var negativeWork = new NativeRect(-1920, -40, 0, 1040);
            var centered = CenteredRectangle(1000, 800, negativeWork);
            Check(centered == new NativeRect(-1460, 100, -460, 900), "Negative work-area coordinates were centered incorrectly.");
            Check(CenteredRectangle(4000, 2000, negativeWork) == negativeWork, "An oversized window was not bounded to its target work area.");
            Check(TrustedStubPackage("c:\\untrusted\\calc.exe") == null,
                "An arbitrary executable basename acquired the Calculator package identity.");
            string calculator = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "calc.exe");
            Check(TrustedStubPackage(calculator) == "Microsoft.WindowsCalculator_8wekyb3d8bbwe",
                "The trusted Calculator launcher did not resolve its package family.");
            Check(!MayRegister(10, 11, 0) && MayRegister(12, 11, 0),
                "A slow older Begin could replace a newer request for the same handler.");
            Check(!MayRegister(10, 0, 11), "An already-retired request could return after pending cleanup.");
            return new { Passed = failures.Count == 0, Checks = checks, Failures = failures,
                ReadsWindowTitles = false, InstallsInputHooks = false, MovesUserWindows = false };
        }
    }
}
