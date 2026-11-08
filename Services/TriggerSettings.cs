using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Action_Wheel.Services
{
    public enum MouseTrigger
    {
        MiddleButton,
        HoldRightPressLeft,
        HoldLeftPressRight,
        MouseButton4,
        MouseButton5,
    }

    /// <summary>Persists the global mouse gesture independently from the button profile.</summary>
    public static class TriggerSettings
    {
        private static string FilePath => Path.Combine(AppDataPaths.DirectoryPath, "preferences.json");

        public static TriggerPreferences LoadPreferences()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return new TriggerPreferences();

                using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
                var root = document.RootElement;
                int trigger = ReadInt(root, "trigger", (int)MouseTrigger.HoldLeftPressRight);
                return new TriggerPreferences
                {
                    Trigger = Enum.IsDefined(typeof(MouseTrigger), trigger)
                        ? (MouseTrigger)trigger : MouseTrigger.HoldLeftPressRight,
                    ChordTimeoutMs = Math.Clamp(ReadInt(root, "chordTimeoutMs", 800), 200, 2000),
                    MovementThreshold = Math.Clamp(ReadInt(root, "movementThreshold", 12), 2, 100),

                    // Normalised rather than trusted: this file is plain JSON next to the config the
                    // app tells people to hand-edit, and a button size of 4000 would put the whole
                    // ring outside its own window with no way back except deleting the file.
                    Appearance = new RingAppearance
                    {
                        Animation = (RingAnimation)ReadInt(root, "animation", (int)RingAnimation.Fade),
                        ButtonSize = ReadInt(root, "buttonSize", (int)RingGeometry.OuterButtonSizeDip),
                        OrbitRadius = ReadInt(root, "orbitRadius", (int)RingGeometry.OrbitRadiusDip),
                        AnimationDurationMs = ReadAnimationDurationMs(root),
                    }.Normalised(),
                };
            }
            catch (Exception)
            {
                return new TriggerPreferences();
            }
        }

        public static MouseTrigger Load() => LoadPreferences().Trigger;

        public static bool Save(TriggerPreferences preferences, out string error)
        {
            try
            {
                var appearance = (preferences.Appearance ?? RingAppearance.Default).Normalised();

                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("trigger", (int)preferences.Trigger);
                    writer.WriteNumber("chordTimeoutMs", Math.Clamp(preferences.ChordTimeoutMs, 200, 2000));
                    writer.WriteNumber("movementThreshold", Math.Clamp(preferences.MovementThreshold, 2, 100));
                    writer.WriteNumber("animation", (int)appearance.Animation);
                    writer.WriteNumber("buttonSize", (int)appearance.ButtonSize);
                    writer.WriteNumber("orbitRadius", (int)appearance.OrbitRadius);
                    writer.WriteNumber("animationDurationMs", (int)appearance.AnimationDurationMs);
                    writer.WriteEndObject();
                }

                return AtomicFile.TryWriteText(FilePath, Encoding.UTF8.GetString(stream.ToArray()), out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static int ReadInt(JsonElement root, string name, int fallback)
        {
            if (root.TryGetProperty(name, out var value) && value.TryGetInt32(out int result))
                return result;

            // Read files written by the previous JsonSerializer implementation.
            string legacy = char.ToUpperInvariant(name[0]) + name[1..];
            return root.TryGetProperty(legacy, out value) && value.TryGetInt32(out result)
                ? result : fallback;
        }

        /// <summary>
        /// Reads the effect duration in its current unit (milliseconds), or converts a percentage
        /// written by a version before the slider - <c>ms = percent * DefaultAnimationDurationMs /
        /// 100</c> is the exact scale those percentages always meant, just now expressed in a unit
        /// people can picture directly instead of one they had to multiply out themselves.
        /// </summary>
        private static int ReadAnimationDurationMs(JsonElement root)
        {
            if (root.TryGetProperty("animationDurationMs", out var value) && value.TryGetInt32(out int ms))
                return ms;

            int percent = ReadInt(root, "animationDuration", 100);
            return (int)Math.Round(percent * RingAppearance.DefaultAnimationDurationMs / 100.0);
        }
    }

    public sealed class TriggerPreferences
    {
        public MouseTrigger Trigger { get; init; } = MouseTrigger.HoldLeftPressRight;
        public int ChordTimeoutMs { get; init; } = 800;
        public int MovementThreshold { get; init; } = 12;

        /// <summary>
        /// Ring size and opening animation. Lives here because Save rewrites the whole file: a
        /// settings screen that saved only the trigger would erase whatever this was set to.
        /// </summary>
        public RingAppearance Appearance { get; init; } = RingAppearance.Default;
    }
}
