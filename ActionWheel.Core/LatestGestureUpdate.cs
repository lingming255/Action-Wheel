using System;

namespace Action_Wheel.Core
{
    /// <summary>
    /// Keeps one pending pointer update per gesture. A new gesture gets a new queued turn,
    /// preserving the opening/release order while older pointer positions are discarded.
    /// </summary>
    public sealed class LatestGestureUpdate<T> where T : struct
    {
        private sealed class Pending(long generation, T value)
        {
            internal readonly long Generation = generation;
            internal T Value = value;
            internal bool SelectionInterrupted;
        }

        private readonly object _gate = new();
        private readonly Func<Action, bool> _post;
        private readonly Action<long, T, bool> _apply;
        private readonly Func<T, T, bool> _selectionChanged;
        private Pending? _pending;

        public LatestGestureUpdate(Func<Action, bool> post, Action<long, T, bool> apply,
            Func<T, T, bool> selectionChanged)
        {
            _post = post;
            _apply = apply;
            _selectionChanged = selectionChanged;
        }

        public void Submit(long generation, T value)
        {
            Pending pending;
            lock (_gate)
            {
                if (_pending is { } current && current.Generation == generation)
                {
                    current.SelectionInterrupted |= _selectionChanged(current.Value, value);
                    current.Value = value;
                    return;
                }
                pending = new Pending(generation, value);
                _pending = pending;
            }
            if (!_post(() => Drain(pending)))
            {
                lock (_gate)
                    if (ReferenceEquals(_pending, pending)) _pending = null;
            }
        }

        private void Drain(Pending pending)
        {
            T value;
            lock (_gate)
            {
                if (!ReferenceEquals(_pending, pending)) return;
                value = pending.Value;
                _pending = null;
            }
            _apply(pending.Generation, value, pending.SelectionInterrupted);
        }
    }
}
