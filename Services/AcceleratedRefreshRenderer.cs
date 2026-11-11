using System.Threading;

namespace Action_Wheel.Services
{
    /// <summary>CPU fallback follows the same per-gesture timing snapshot as GPU playback.</summary>
    internal sealed class AcceleratedRefreshRenderer(IRefreshRenderer reference, RefreshTimingProfile? timing = null) : IRefreshRenderer
    {
        private readonly RefreshTimingProfile _timing = timing ?? RefreshTimingProfile.Default;
        public double DurationMs => _timing.PlaybackDurationMs;
        // Preserve the CPU's bounded display budget on devices without hardware rendering.
        public double FrameIntervalMs => reference.FrameIntervalMs;
        public int BloomWidth => reference.BloomWidth;
        public int BloomHeight => reference.BloomHeight;
        public void Render(double elapsedMs, byte[] pixels, byte[] light,
            CancellationToken cancellation = default)
            => reference.Render(elapsedMs * _timing.ReferenceTimeRatio, pixels, light, cancellation);
    }
}
