using System;

namespace Action_Wheel.Services
{
    /// <summary>Owns only worker-thread voices; recovery never queues an already-expired cue.</summary>
    internal sealed class WheelSoundRecovery<T> : IDisposable where T : class, IDisposable
    {
        private long _endpointGeneration = long.MinValue;
        internal T?[] Voices { get; }

        internal WheelSoundRecovery(int count) => Voices = new T?[count];

        internal void SynchronizeEndpoint(long generation)
        {
            if (generation == _endpointGeneration) return;
            Dispose();
            _endpointGeneration = generation;
        }

        internal bool TryPlay(int slot, Func<T?> create, Func<T, bool> play, Func<bool> isFresh)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!isFresh()) return false;
                T? voice = Voices[slot] ??= create();
                // Opening/reopening a driver can take longer than the cue's whole gesture.
                if (voice == null || !isFresh()) return false;
                if (play(voice)) return true;
                // Reset/write failures invalidate the endpoint's other cached handles as well.
                Dispose();
            }
            return false;
        }

        internal void StopAll(Func<T, bool> stop)
        {
            foreach (T? voice in Voices)
            {
                if (voice == null || stop(voice)) continue;
                Dispose();
                return;
            }
        }

        public void Dispose()
        {
            for (int i = 0; i < Voices.Length; i++)
            {
                Voices[i]?.Dispose();
                Voices[i] = null;
            }
        }
    }
}
