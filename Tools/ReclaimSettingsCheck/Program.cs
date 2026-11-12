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
    Require(TriggerSettings.LoadPreferences().Sounds == WheelSoundSettings.Default
        && WheelSoundSettings.Default.HoverVolumePercent == 100,
        "A new install must retain the existing sound defaults and use 100% hover volume.");
    File.WriteAllText(preferencesPath,
        """{"trigger":2,"chordTimeoutMs":800,"movementThreshold":30,"animation":1,"buttonSize":52,"orbitRadius":116,"animationDurationMs":120,"refresh":{"durationMs":1800,"distanceDelayPercent":100,"pixelDurationPercent":100}}""");
    var legacy = TriggerSettings.LoadPreferences();
    Require(legacy.Reclaim == ReclaimAnimationSettings.Default && legacy.Refresh.DurationMs == 1800,
        "An existing config must keep outward timing and get default opening controls.");
    Require(legacy.Sounds.HoverVolumePercent == 100,
        "An existing config without wheelSounds must use 100% hover volume.");
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

    // Exercise the linked production codec against this tool's temporary AppDataPaths shim.
    // A bad optional hover field must not reset valid sound or animation preferences.
    const string soundFixture = """{"trigger":3,"chordTimeoutMs":900,"movementThreshold":20,"animation":1,"buttonSize":52,"orbitRadius":116,"animationDurationMs":120,"refresh":{"durationMs":1800,"distanceDelayPercent":125,"pixelDurationPercent":90},"reclaim":{"durationMs":420,"pixelDurationMs":140,"densityPercent":175,"glowPercent":35},"wheelSounds":{"enabled":true,"volumePercent":37,"hoverVolumePercent":HOVER}}""";
    void WriteHoverFixture(string rawValue)
        => File.WriteAllText(preferencesPath, soundFixture.Replace("HOVER", rawValue, StringComparison.Ordinal));
    void RequireUnrelatedSettingsPreserved(TriggerPreferences actual, TriggerPreferences expected)
        => Require(actual.Trigger == expected.Trigger && actual.ChordTimeoutMs == expected.ChordTimeoutMs
            && actual.MovementThreshold == expected.MovementThreshold && actual.Appearance == expected.Appearance
            && actual.Refresh == expected.Refresh && actual.Reclaim == expected.Reclaim
            && actual.Sounds.Enabled == expected.Sounds.Enabled
            && actual.Sounds.VolumePercent == expected.Sounds.VolumePercent,
            "Hover volume must not alter master volume, enabled state, gesture, ring or animation preferences.");
    TriggerPreferences WithSounds(TriggerPreferences source, WheelSoundSettings sounds) => new()
    {
        Trigger = source.Trigger, ChordTimeoutMs = source.ChordTimeoutMs,
        MovementThreshold = source.MovementThreshold, Appearance = source.Appearance,
        Refresh = source.Refresh, Reclaim = source.Reclaim, Sounds = sounds,
    };

    WriteHoverFixture("150");
    var soundBaseline = TriggerSettings.LoadPreferences();
    Require(soundBaseline.Sounds.HoverVolumePercent == 150 && soundBaseline.Sounds.Enabled
        && soundBaseline.Sounds.VolumePercent == 37,
        "The isolated sound fixture must load its independent master and hover levels.");
    File.WriteAllText(preferencesPath,
        soundFixture.Replace(",\"hoverVolumePercent\":HOVER", string.Empty, StringComparison.Ordinal));
    var legacySounds = TriggerSettings.LoadPreferences();
    Require(legacySounds.Sounds.HoverVolumePercent == 100,
        "An existing wheelSounds object without hoverVolumePercent must retain 100% hover volume.");
    RequireUnrelatedSettingsPreserved(legacySounds, soundBaseline);

    foreach (double hover in new double[] { 0, 150, 200 })
    foreach (bool enabled in new[] { false, true })
    {
        var expected = WithSounds(soundBaseline, soundBaseline.Sounds with
        {
            HoverVolumePercent = hover, Enabled = enabled,
        });
        Require(TriggerSettings.Save(expected, out error), "Saving hover volume failed: " + error);
        var actual = TriggerSettings.LoadPreferences();
        Require(actual.Sounds.HoverVolumePercent == hover,
            $"Hover volume {hover}% must survive a save/load roundtrip.");
        RequireUnrelatedSettingsPreserved(actual, expected);
        using var json = JsonDocument.Parse(File.ReadAllText(preferencesPath));
        Require(json.RootElement.GetProperty("wheelSounds").GetProperty("hoverVolumePercent").GetDouble() == hover,
            "Hover volume must be persisted under wheelSounds.hoverVolumePercent.");
    }

    foreach (var sample in new[] { (Raw: "-500", Expected: 0d), (Raw: "-1", Expected: 0d),
        (Raw: "201", Expected: 200d), (Raw: "5000", Expected: 200d) })
    {
        WriteHoverFixture(sample.Raw);
        var actual = TriggerSettings.LoadPreferences();
        Require(actual.Sounds.HoverVolumePercent == sample.Expected,
            "Finite hand-edited hover volume must be bounded to 0–200%.");
        RequireUnrelatedSettingsPreserved(actual, soundBaseline);
    }

    foreach (string rawValue in new[] { "\"wrong\"", "null", "false", "{}", "[]", "1e9999", "-1e9999" })
    {
        WriteHoverFixture(rawValue);
        var actual = TriggerSettings.LoadPreferences();
        Require(actual.Sounds.HoverVolumePercent == 100,
            "Non-numeric or non-finite optional hover volume must fall back to 100%.");
        RequireUnrelatedSettingsPreserved(actual, soundBaseline);
    }

    foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
    {
        var expected = WithSounds(soundBaseline, soundBaseline.Sounds with { HoverVolumePercent = invalid });
        Require(expected.Sounds.Normalised().HoverVolumePercent == 100,
            "Non-finite hover volume in memory must normalize to 100%.");
        Require(TriggerSettings.Save(expected, out error), "Saving normalized hover volume failed: " + error);
        var actual = TriggerSettings.LoadPreferences();
        Require(actual.Sounds.HoverVolumePercent == 100,
            "Saving non-finite hover volume must produce a readable 100% fallback.");
        RequireUnrelatedSettingsPreserved(actual, soundBaseline);
    }
    Console.WriteLine(JsonSerializer.Serialize(new { Passed = true, Assertions = assertions,
        LegacyDefaults = true, AllControlsRoundTripped = true, ExistingSettingsPreserved = true,
        InvalidSettingsBounded = true, HoverVolumeDefaults = true, HoverVolumeRoundTripped = true,
        HoverVolumeInvalidValuesHandled = true, IndependentSoundSettingsPreserved = true,
        UsedUserConfiguration = false }));
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
