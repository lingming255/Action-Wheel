namespace Action_Wheel.Services
{
    internal interface IRefreshRenderer
    {
        int BloomWidth { get; }
        int BloomHeight { get; }
        double DurationMs { get; }
        void Render(double elapsedMs, byte[] fullBuffer, byte[] bloomBuffer,
            System.Threading.CancellationToken cancellation = default);
    }
}
