using System;
using System.Collections.Generic;

namespace Action_Wheel.Services
{
    /// <summary>Tracks the WinMM buffer ownership contract, independently of native allocation.</summary>
    internal sealed class WaveOutReleaseState
    {
        private const uint InvalidHandle = 5;
        internal bool DeviceOpen { get; set; }
        internal bool Prepared { get; set; }

        internal bool TryRelease(Func<uint> reset, Func<uint> unprepare, Func<uint> close, Func<bool> isQueued)
        {
            if (DeviceOpen && reset() == InvalidHandle) DeviceOpen = false;
            if (Prepared)
            {
                if (!DeviceOpen)
                {
                    // An invalid stream needs no Close retry, but an outstanding native buffer
                    // must still be retained until WinMM returns it (WHDR_INQUEUE clears).
                    if (isQueued()) return false;
                    Prepared = false;
                }
                else
                {
                    uint result = unprepare();
                    if (result == 0) Prepared = false;
                    else if (result == InvalidHandle)
                    {
                        DeviceOpen = false;
                        if (isQueued()) return false;
                        Prepared = false;
                    }
                    else return false; // Includes WAVERR_STILLPLAYING: the driver owns the data.
                }
            }
            if (DeviceOpen)
            {
                uint result = close();
                if (result != 0 && result != InvalidHandle) return false;
                DeviceOpen = false;
            }
            return true;
        }
    }

    /// <summary>Bounds all live native leases, including ones awaiting safe driver release.</summary>
    internal sealed class WheelSoundRetirement<T> where T : class
    {
        private readonly int _capacity;
        private readonly List<T> _retired = new();
        private readonly object _gate = new();
        private int _leases;
        internal WheelSoundRetirement(int capacity) => _capacity = capacity;
        internal bool TryAcquire()
        {
            lock (_gate)
            {
                if (_leases >= _capacity) return false;
                _leases++;
                return true;
            }
        }
        internal void Release() { lock (_gate) _leases--; }
        internal void Retire(T item)
        {
            lock (_gate) if (!_retired.Contains(item)) _retired.Add(item);
        }
        internal void Drain(Func<T, bool> tryRelease)
        {
            lock (_gate)
                for (int i = _retired.Count - 1; i >= 0; i--)
                    if (tryRelease(_retired[i])) _retired.RemoveAt(i);
        }
    }
}
