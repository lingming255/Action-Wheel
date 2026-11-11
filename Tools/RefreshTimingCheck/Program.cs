using System.Numerics;
using Action_Wheel.Core;
using Action_Wheel.Services;

int assertions = 0;
void Require(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}
void Near(double actual, double expected, string message, double tolerance = 1e-9)
    => Require(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance,
        $"{message}: expected {expected:R}, got {actual:R}");

var defaults = RefreshTimingProfile.Default;
Near(defaults.PlaybackDurationMs, 2500, "Accepted default wall duration");
Near(defaults.ReferenceDurationMs, 3000, "Accepted default reference duration");
Near(defaults.ReferenceTimeRatio, 1.2, "Accepted default clock ratio");
Near(defaults.StartAt(0, 0, 0), 119, "Accepted initial delay");
Near(defaults.StartAt(1, 0, 1), 2409, "Accepted latest arrival");
Near(defaults.LifetimeMs, 510, "Accepted pixel lifetime");
Near(defaults.RiseMs, 85, "Accepted rise");
Near(defaults.FadeStartMs, 187, "Accepted fade start");
Near(defaults.BackgroundRestoreMs, 340, "Accepted background restoration");

var nonFinite = new RefreshAnimationSettings
{
    DurationMs = double.NaN,
    DistanceDelayPercent = double.PositiveInfinity,
    PixelDurationPercent = double.NegativeInfinity,
}.Normalised();
Require(nonFinite == RefreshAnimationSettings.Default, "Nonfinite preferences recover independently to defaults");
var clamp = new RefreshAnimationSettings
{
    DurationMs = -20,
    DistanceDelayPercent = 400,
    PixelDurationPercent = 1,
}.Normalised();
Require(clamp == new RefreshAnimationSettings
{
    DurationMs = 600,
    DistanceDelayPercent = 200,
    PixelDurationPercent = 25,
}, "Out-of-range preferences are clamped");
var rounded = new RefreshAnimationSettings
{
    DurationMs = 1234.7,
    DistanceDelayPercent = 53.2,
    PixelDurationPercent = 63.8,
}.Normalised();
Require(rounded.DurationMs == 1235 && rounded.DistanceDelayPercent == 53 && rounded.PixelDurationPercent == 64,
    "All persisted controls use whole values");
Require(rounded.Normalised() == rounded, "Preference normalization is idempotent");
var untrusted = new RefreshTimingProfile(new RefreshAnimationSettings
{
    DurationMs = double.PositiveInfinity,
    DistanceDelayPercent = -1,
    PixelDurationPercent = 400,
});
Require(untrusted.Settings.DurationMs == 2500 && untrusted.Settings.DistanceDelayPercent == 0
    && untrusted.Settings.PixelDurationPercent == 200, "Profile always normalizes its input snapshot");

var instantDefault = new RefreshTimingProfile(RefreshAnimationSettings.Default, instantStart: true);
Near(instantDefault.StartDelayMs, 0, "Instant scan has no initial lead-in");
Near(instantDefault.StartAt(.5f, .5f, 0), 0, "The clicked depth begins at time zero");
Near(instantDefault.ReferenceDurationMs, 2881, "Instant scan removes only the 119ms reference lead-in");
Near(instantDefault.PlaybackDurationMs, defaults.PlaybackDurationMs, "Instant scan preserves requested wall duration");
var profiles = new List<RefreshTimingProfile> { defaults, instantDefault };
foreach (double duration in new[] { 600d, 6000d })
foreach (double distance in new[] { 0d, 200d })
foreach (double pulse in new[] { 25d, 200d })
foreach (bool instantStart in new[] { false, true })
    profiles.Add(new(new RefreshAnimationSettings
    {
        DurationMs = duration,
        DistanceDelayPercent = distance,
        PixelDurationPercent = pulse,
    }, instantStart));
foreach (bool instantStart in new[] { false, true })
    profiles.Add(new(new RefreshAnimationSettings
{
    DurationMs = 1873,
    DistanceDelayPercent = 37,
    PixelDurationPercent = 63,
}, instantStart));

int arrivals = 0;
double smallestWallTail = double.PositiveInfinity;
foreach (var profile in profiles)
{
    double ratio = profile.ReferenceTimeRatio;
    var archived = new RefreshTimingProfile(profile.Settings);
    Require(ratio > 0 && double.IsFinite(ratio), "All legal controls produce a positive finite clock");
    Near(profile.ReferenceDurationMs / ratio, profile.Settings.DurationMs, "Wall duration stays fixed");
    Near(archived.ReferenceDurationMs - profile.ReferenceDurationMs, 119 - profile.StartDelayMs,
        "Instant start changes the reference lead-in without trimming propagation or pulse duration");
    Near(profile.LifetimeMs, archived.LifetimeMs, "Instant start retains the selected full pixel lifetime");
    foreach (float origin in new[] { 0f, .1f, .3f, .5f, .9f, 1f })
    {
        Near(profile.StartAt(origin, origin, 0), profile.StartDelayMs, "Every depth origin begins at the click");
        foreach (float depth in new[] { 0f, .1f, .3f, .5f, .9f, 1f })
        foreach (double distance in new[] { 0d, .01d, .25d, .5d, 1d })
        {
            double start = profile.StartAt(depth, origin, distance);
            Near(archived.StartAt(depth, origin, distance) - start, 119 - profile.StartDelayMs,
                "Removing the lead-in preserves every depth and distance arrival offset", 1e-8);
            double endWall = (start + profile.LifetimeMs) / ratio;
            Require(endWall < profile.PlaybackDurationMs, "Every allowed pulse completes before wall deadline");
            Require((start + profile.BackgroundRestoreMs) / ratio < profile.PlaybackDurationMs,
                "Every background cell restores before wall deadline");
            Near(UniformBlockRefreshRenderer.OpacityAt(profile.ReferenceDurationMs - start, profile), 0,
                "No pixel remains visible at requested completion");
            smallestWallTail = Math.Min(smallestWallTail, profile.PlaybackDurationMs - endWall);
            arrivals++;
        }
        double sameDepthNear = profile.StartAt(origin, origin, .2);
        double sameDepthFar = profile.StartAt(origin, origin, .8);
        Require(profile.DistanceDelayMs == 0 ? sameDepthNear == sameDepthFar : sameDepthNear < sameDepthFar,
            "Same-depth pixels propagate away from click unless distance delay is disabled");
        if (origin > .1f && origin < .9f)
        {
            double left = profile.StartAt(origin - .05f, origin, 0);
            double right = profile.StartAt(origin + .05f, origin, 0);
            Near(left, right, "Equal depth differences propagate in both directions", .001);
            Require(profile.StartAt(origin - .1f, origin, 0) > left
                && profile.StartAt(origin + .1f, origin, 0) > right,
                "Increasing depth differences arrive later on both sides");
        }
    }

    var block = new UniformBlockRefreshRenderer.Block(0, 0, 4, 4, 0, 0, 4, 4,
        new Vector3(.7f, .3f, .1f), .5f, 100);
    if (profile.StartDelayMs == 0)
    {
        var clickedBlock = block with { StartMs = profile.StartAt(.5f, .5f, 0) };
        double earlyReferenceMs = profile.RiseMs / 4;
        Require(earlyReferenceMs / ratio < 119 / ratio,
            "An instant pixel reaches visible rise inside the archived lead-in");
        Require(UniformBlockRefreshRenderer.OpacityAt(earlyReferenceMs, profile) > 0
            && UniformBlockRefreshRenderer.CoreAt(clickedBlock, earlyReferenceMs, profile) != 0,
            "The clicked instant pixel emits during the first rise without waiting for the old lead-in");
    }
    foreach (double invalidAge in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1d, 0d,
        profile.LifetimeMs, profile.LifetimeMs + 1 })
        Near(UniformBlockRefreshRenderer.OpacityAt(invalidAge, profile), 0,
            "Pulse envelope is zero before start, at completion and for invalid ages");
    float previous = -1;
    for (int i = 0; i <= 40; i++)
    {
        double age = profile.RiseMs * i / 40;
        float opacity = UniformBlockRefreshRenderer.OpacityAt(age, profile);
        Require(opacity >= previous, "Rise envelope is monotonic");
        previous = opacity;
    }
    previous = 1;
    for (int i = 0; i <= 40; i++)
    {
        double age = profile.FadeStartMs + (profile.LifetimeMs - profile.FadeStartMs) * i / 40;
        float opacity = UniformBlockRefreshRenderer.OpacityAt(age, profile);
        Require(opacity <= previous, "Fade envelope is monotonic");
        previous = opacity;
    }
    foreach (double fraction in new[] { 0d, .001d, .1d, .25d, .5d, .75d, .99d, 1d })
    {
        double age = fraction * profile.LifetimeMs;
        Near(UniformBlockRefreshRenderer.OpacityAt(age, profile),
            UniformBlockRefreshRenderer.OpacityAt(fraction * defaults.LifetimeMs),
            "Pulse percentages preserve fractional opacity curve", 1e-6);
        Near(UniformBlockRefreshRenderer.SaturationAt(age, profile),
            UniformBlockRefreshRenderer.SaturationAt(fraction * defaults.LifetimeMs),
            "Pulse percentages preserve fractional saturation curve", 1e-6);
    }
    Near(UniformBlockRefreshRenderer.SaturationAt(profile.RiseMs, profile), 1,
        "Pigment is fully saturated at the completed rise", 1e-6);
    Require(UniformBlockRefreshRenderer.BackgroundAmountAt(block, 100, profile) == 256
        && UniformBlockRefreshRenderer.BackgroundAmountAt(block, 100 + profile.BackgroundRestoreMs, profile) == 0,
        "Background restores from fully grey to transparent at scaled endpoints");
    Require(UniformBlockRefreshRenderer.CoreAt(block, 100, profile) == 0
        && UniformBlockRefreshRenderer.CoreAt(block, 100 + profile.LifetimeMs, profile) == 0,
        "Core pixels start and finish fully transparent");

    var spy = new ClockSpy();
    var accelerated = new AcceleratedRefreshRenderer(spy, profile);
    Near(accelerated.DurationMs, profile.PlaybackDurationMs, "CPU fallback advertises selected wall duration");
    foreach (double wallFraction in new[] { 0d, .17d, .5d, .999d, 1d })
    {
        accelerated.Render(profile.PlaybackDurationMs * wallFraction, [], []);
        Near(spy.LastElapsedMs, profile.ReferenceDurationMs * wallFraction,
            "CPU fallback maps wall clock to the entire reference span");
    }
}
Console.WriteLine($"Refresh timing checks passed: {assertions} assertions, {profiles.Count} timing profiles, "
    + $"{arrivals} depth/distance arrivals; smallest guaranteed wall tail={smallestWallTail:F3}ms. "
    + "Archived 3000ms/1.2 clock, instant-start lead-in removal, normalized settings, complete pulses, bidirectional depth, distance order, "
    + "fractional envelopes and CPU clock verified without desktop or model inference.");

internal sealed class ClockSpy : IRefreshRenderer
{
    public int BloomWidth => 1;
    public int BloomHeight => 1;
    public double DurationMs => 0;
    internal double LastElapsedMs { get; private set; }
    public void Render(double elapsedMs, byte[] fullBuffer, byte[] bloomBuffer,
        CancellationToken cancellation = default) => LastElapsedMs = elapsedMs;
}
