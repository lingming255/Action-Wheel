using System;

namespace Action_Wheel.Core
{
    /// <summary>Optional interaction sounds; existing installations stay silent.</summary>
    public sealed record WheelSoundSettings
    {
        public static readonly WheelSoundSettings Default = new();
        public const double DefaultVolumePercent = 45;

        public bool Enabled { get; init; }
        public double VolumePercent { get; init; } = DefaultVolumePercent;

        public WheelSoundSettings Normalised() => this with
        {
            VolumePercent = Math.Clamp(double.IsFinite(VolumePercent)
                ? Math.Round(VolumePercent) : DefaultVolumePercent, 0, 100),
        };
    }
}
