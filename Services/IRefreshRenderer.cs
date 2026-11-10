namespace Action_Wheel.Services
{
    internal interface IRefreshRenderer
    {
        int BloomWidth { get; }
        int BloomHeight { get; }
        double DurationMs { get; }
        double FrameIntervalMs => 1000.0 / 60;
        void Render(double elapsedMs, byte[] fullBuffer, byte[] bloomBuffer,
            System.Threading.CancellationToken cancellation = default);
    }
}
