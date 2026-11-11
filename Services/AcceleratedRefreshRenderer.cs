using System.Threading;

namespace Action_Wheel.Services
{
    /// <summary>CPU fallback follows the same 2.5-second clock as GPU playback.</summary>
    internal sealed class AcceleratedRefreshRenderer(IRefreshRenderer reference) : IRefreshRenderer
    {
        internal const double Speed = 1.2;
        internal const double PlaybackDurationMs = 2500;
        public double DurationMs => PlaybackDurationMs;
        // Preserve the CPU's bounded display budget on devices without hardware rendering.
        public double FrameIntervalMs => reference.FrameIntervalMs;
        public int BloomWidth => reference.BloomWidth;
        public int BloomHeight => reference.BloomHeight;
        public void Render(double elapsedMs, byte[] pixels, byte[] light,
            CancellationToken cancellation = default)
            => reference.Render(elapsedMs * Speed, pixels, light, cancellation);
    }
}
