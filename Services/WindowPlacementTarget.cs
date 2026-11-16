namespace Action_Wheel.Services
{
    /// <summary>
    /// Physical screen point captured for one invocation. Carry the value across asynchronous
    /// dispatch rather than consulting a cursor which may already have moved to another screen.
    /// </summary>
    public sealed record WindowPlacementTarget(int X, int Y)
    {
        private static long _nextSequence;
        internal long RequestSequence { get; } = System.Threading.Interlocked.Increment(ref _nextSequence);
    }
}
