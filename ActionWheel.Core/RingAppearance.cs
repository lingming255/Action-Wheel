using System;

namespace Action_Wheel.Core
{
    /// <summary>How the ring animates itself into view.</summary>
    /// <remarks>
    /// The numbers are written to preferences.json, so they are fixed: inserting a value in the
    /// middle would silently change what every existing installation is set to. Append only.
    /// </remarks>
    public enum RingAnimation
    {
        /// <summary>No animation. Every button is at full opacity on the first frame.</summary>
        None = 0,

        /// <summary>The original: the ring fades and scales up while the buttons stagger in.</summary>
        Fade = 1,

        /// <summary>Each button springs up from a fraction of its size, slightly overshooting.</summary>
        Pop = 2,

        /// <summary>The buttons start stacked on the centre and fly out to their orbit.</summary>
        Radiate = 3,

        /// <summary>The ring sweeps one button-slot clockwise into place.</summary>
        Sweep = 4,

        /// <summary>The buttons start outside the ring and fly inward to close around the centre.</summary>
        Converge = 5,

        /// <summary>As <see cref="Sweep"/>, anticlockwise.</summary>
        CounterSweep = 6,
    }

    /// <summary>
    /// The parts of the ring's look that the user controls but that are not per-button: the opening
    /// animation and the two numbers that decide how big the ring is.
    /// </summary>
    /// <remarks>
    /// Deliberately not part of <c>actions.json</c>. Those are the nine buttons and travel between
    /// machines as a profile; this is one person's preference about the pointer under their own hand,
    /// and it belongs with the trigger settings in preferences.json.
    /// </remarks>
    public sealed record RingAppearance
    {
        public static readonly RingAppearance Default = new();

        public RingAnimation Animation { get; init; } = RingAnimation.Fade;

        /// <summary>
        /// How long the opening animation takes to finish, in milliseconds.
        /// <see cref="DefaultAnimationDurationMs"/> plays every begin/duration value exactly as
        /// <see cref="RingOpenAnimation"/> defines them; below that plays faster, above that slower.
        /// </summary>
        /// <remarks>
        /// A single millisecond figure rather than one per preset, and a straight-line scale against
        /// <see cref="DefaultAnimationDurationMs"/> rather than each preset's own tuned total (which
        /// range 186-200 ms across the seven presets, not the same number for each) - because a
        /// 14 ms difference between presets is not something a person can feel, and a table mapping
        /// every preset to its own tuned total is a second place a future retune would have to
        /// remember to update. Internally this still multiplies every begin/duration value in
        /// lock-step the same way the old percentage did, preserving the ratios
        /// <see cref="RingOpenAnimation"/>'s remarks describe as "fade fast, move slowly" - see
        /// <see cref="RingOpenAnimation.RingMetrics.DurationScale"/>.
        /// </remarks>
        public double AnimationDurationMs { get; init; } = DefaultAnimationDurationMs;

        /// <summary>
        /// Must match <see cref="RingOpenAnimation.BudgetMs"/> - kept as its own literal rather than
        /// a reference to it because <c>RingOpenAnimation</c> lives in the app project and this one
        /// does not take a dependency on it.
        /// </summary>
        public const double DefaultAnimationDurationMs = 120.0;

        /// <summary>The fastest the opening animation can be played.</summary>
        public const double MinAnimationDurationMs = 60.0;

        /// <summary>The slowest the opening animation can be played.</summary>
        public const double MaxAnimationDurationMs = 1000.0;

        /// <summary>Diameter of one of the eight outer buttons, in DIPs.</summary>
        public double ButtonSize { get; init; } = RingGeometry.OuterButtonSizeDip;

        /// <summary>Distance from the ring's centre to each outer button's centre, in DIPs.</summary>
        public double OrbitRadius { get; init; } = RingGeometry.OrbitRadiusDip;

        /// <summary>Diameter of the centre button - a fixed fraction of an outer one.</summary>
        public double CenterButtonSize => Math.Round(ButtonSize * RingGeometry.CenterButtonRatio);

        /// <summary>
        /// Glyph size for a button of this diameter. Scaled rather than fixed: a 14 DIP glyph that
        /// looks right inside a 44 DIP button is lost inside a 72 DIP one.
        /// </summary>
        public double IconSize => Math.Round(ButtonSize * RingGeometry.IconSizeRatio);

        /// <summary>Side of the square overlay window this ring needs, in DIPs.</summary>
        public int MenuSize => RingGeometry.MenuSizeFor(ButtonSize, OrbitRadius);

        /// <summary>
        /// The same appearance with both numbers brought inside their legal range. The orbit is
        /// clamped <em>after</em> the button size, because its limits are derived from it - clamping
        /// in the other order can leave a pair that individually pass and together overlap.
        /// </summary>
        public RingAppearance Normalised()
        {
            double button = Math.Clamp(
                double.IsFinite(ButtonSize) ? Math.Round(ButtonSize) : RingGeometry.OuterButtonSizeDip,
                RingGeometry.MinButtonSizeDip, RingGeometry.MaxButtonSizeDip);

            double orbit = double.IsFinite(OrbitRadius)
                ? Math.Round(OrbitRadius) : RingGeometry.OrbitRadiusDip;
            orbit = Math.Clamp(orbit, RingGeometry.MinOrbitFor(button), RingGeometry.MaxOrbitFor(button));

            double duration = Math.Clamp(
                double.IsFinite(AnimationDurationMs) ? Math.Round(AnimationDurationMs) : DefaultAnimationDurationMs,
                MinAnimationDurationMs, MaxAnimationDurationMs);

            return this with
            {
                Animation = Enum.IsDefined(typeof(RingAnimation), Animation) ? Animation : RingAnimation.Fade,
                ButtonSize = button,
                OrbitRadius = orbit,
                AnimationDurationMs = duration,
            };
        }
    }
}
