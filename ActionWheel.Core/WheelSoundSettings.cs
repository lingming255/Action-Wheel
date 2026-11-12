using System;

namespace Action_Wheel.Core
{
    /// <summary>Optional interaction sounds; existing installations stay silent.</summary>
    public sealed record WheelSoundSettings
    {
        public static readonly WheelSoundSettings Default = new();
        public const double DefaultVolumePercent = 45;
        public const double DefaultHoverVolumePercent = 100;

        public bool Enabled { get; init; }
        public double VolumePercent { get; init; } = DefaultVolumePercent;
        public double HoverVolumePercent { get; init; } = DefaultHoverVolumePercent;

        public WheelSoundSettings Normalised() => this with
        {
            VolumePercent = Math.Clamp(double.IsFinite(VolumePercent)
                ? Math.Round(VolumePercent) : DefaultVolumePercent, 0, 100),
            HoverVolumePercent = Math.Clamp(double.IsFinite(HoverVolumePercent)
                ? Math.Round(HoverVolumePercent) : DefaultHoverVolumePercent, 0, 200),
        };
    }
}
