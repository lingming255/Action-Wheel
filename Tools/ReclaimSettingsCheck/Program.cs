using System.Text.Json;
using Action_Wheel.Core;
using Action_Wheel.Services;

int assertions = 0;
void Require(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

string temporaryRoot = Action_Wheel.Services.AppDataPaths.DirectoryPath;
Directory.CreateDirectory(temporaryRoot);
try
{
    string preferencesPath = Path.Combine(temporaryRoot, "preferences.json");
    Require(TriggerSettings.LoadPreferences().Reclaim == ReclaimAnimationSettings.Default,
        "A new install must retain the accepted opening.");
    File.WriteAllText(preferencesPath,
        """{"trigger":2,"chordTimeoutMs":800,"movementThreshold":30,"animation":1,"buttonSize":52,"orbitRadius":116,"animationDurationMs":120,"refresh":{"durationMs":1800,"distanceDelayPercent":100,"pixelDurationPercent":100}}""");
    var legacy = TriggerSettings.LoadPreferences();
    Require(legacy.Reclaim == ReclaimAnimationSettings.Default && legacy.Refresh.DurationMs == 1800,
        "An existing config must keep outward timing and get default opening controls.");
    var custom = new ReclaimAnimationSettings
    {
        DurationMs = 420, PixelDurationMs = 140, DensityPercent = 175, GlowPercent = 35,
    };
    Require(TriggerSettings.Save(new TriggerPreferences
    {
        Trigger = legacy.Trigger, ChordTimeoutMs = legacy.ChordTimeoutMs,
        MovementThreshold = legacy.MovementThreshold, Appearance = legacy.Appearance,
        Refresh = legacy.Refresh, Reclaim = custom,
    }, out string error), "Saving the isolated config failed: " + error);
    var roundTrip = TriggerSettings.LoadPreferences();
    Require(roundTrip.Reclaim == custom, "All four controls must survive restart.");
    Require(roundTrip.Refresh == legacy.Refresh && roundTrip.Appearance == legacy.Appearance
        && roundTrip.Trigger == legacy.Trigger && roundTrip.ChordTimeoutMs == legacy.ChordTimeoutMs
        && roundTrip.MovementThreshold == legacy.MovementThreshold,
        "Changing opening settings must preserve the existing gesture, ring and outward settings.");
    using (var json = JsonDocument.Parse(File.ReadAllText(preferencesPath)))
        Require(json.RootElement.GetProperty("reclaim").EnumerateObject().Count() == 4,
            "All opening preferences must be written to their own object.");
    File.WriteAllText(preferencesPath,
        """{"refresh":{"durationMs":1800},"reclaim":{"durationMs":100,"pixelDurationMs":500,"densityPercent":9000,"glowPercent":-10}}""");
    var bounded = TriggerSettings.LoadPreferences();
    Require(bounded.Reclaim.DurationMs == 100 && bounded.Reclaim.PixelDurationMs == 100
        && bounded.Reclaim.DensityPercent == 200 && bounded.Reclaim.GlowPercent == 0,
        "Hand-edited settings must not produce negative scan delay, excessive blocks or invalid opacity.");
    File.WriteAllText(preferencesPath,
        """{"reclaim":{"durationMs":"wrong","pixelDurationMs":null,"densityPercent":1e9999,"glowPercent":false}}""");
    Require(TriggerSettings.LoadPreferences().Reclaim == ReclaimAnimationSettings.Default,
        "Malformed optional fields must fall back without preventing the opening.");
    foreach (double duration in new[] { double.NaN, double.PositiveInfinity, -10, 100, 240, 1500, 8000 })
    foreach (double pulse in new[] { double.NaN, -1, 20, 60, 500, 9000 })
    {
        var settings = new ReclaimAnimationSettings { DurationMs = duration, PixelDurationMs = pulse }.Normalised();
        Require(settings.DurationMs >= 100 && settings.DurationMs <= 1500
            && settings.PixelDurationMs >= 20 && settings.PixelDurationMs <= 500
            && settings.DurationMs >= settings.PixelDurationMs,
            "Every supported config must produce a finite positive pulse fitting the opening.");
    }
    Console.WriteLine(JsonSerializer.Serialize(new { Passed = true, Assertions = assertions,
        LegacyDefaults = true, AllControlsRoundTripped = true, ExistingSettingsPreserved = true,
        InvalidSettingsBounded = true, UsedUserConfiguration = false }));
}
finally
{
    string resolved = Path.GetFullPath(temporaryRoot);
    if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(resolved).StartsWith("ActionWheel-ReclaimSettingsCheck-", StringComparison.Ordinal))
        throw new InvalidOperationException("Refusing to clean a path outside the isolated check directory.");
    Directory.Delete(resolved, recursive: true);
}

namespace Action_Wheel.Services
{
    // The linked production codec resolves this local type before the Core namespace import.
    // No test reads or writes the user's actual ActionWheel configuration.
    internal static class AppDataPaths
    {
        internal static string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(),
            "ActionWheel-ReclaimSettingsCheck-" + Guid.NewGuid().ToString("N"));
    }
}
