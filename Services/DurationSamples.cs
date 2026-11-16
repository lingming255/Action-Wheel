namespace Action_Wheel.Services
{
    /// <summary>Bounded in-memory timing totals for explicitly enabled diagnostic probes.</summary>
    internal struct DurationSamples
    {
        private long _count;
        private double _totalMilliseconds, _maximumMilliseconds;

        internal void Add(double milliseconds)
        {
            _count++;
            _totalMilliseconds += milliseconds;
            _maximumMilliseconds = System.Math.Max(_maximumMilliseconds, milliseconds);
        }

        internal readonly object Snapshot() => new
        {
            Count = _count,
            MeanMs = _count == 0 ? 0 : _totalMilliseconds / _count,
            MaximumMs = _maximumMilliseconds,
            TotalMs = _totalMilliseconds,
        };
    }
}
