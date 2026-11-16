using System.Collections.Generic;
using System.Diagnostics;

namespace Action_Wheel.Overlay
{
    public sealed partial class RadialMenu
    {
        // The explicit hidden reuse probe opts in. Normal wheel sessions do not allocate a
        // collector, read a timing clock or write any diagnostic/activity files.
        internal bool MeasureShowTimings { get; set; }
        internal ShowTimingCapture? LastShowTimings { get; private set; }
        private ShowTimingCapture? _showTimings;

        internal sealed class ShowTimingCapture
        {
            private readonly long _started = Stopwatch.GetTimestamp();
            private long _last;
            private long _openingLast;

            internal ShowTimingCapture(bool prepared)
            {
                Prepared = prepared;
                _last = _started;
            }

            public bool Prepared { get; }
            public Dictionary<string, double> StagesMs { get; } = new();
            public Dictionary<string, double> PreparedOpeningStagesMs { get; } = new();
            public double TotalSynchronousMs { get; private set; }
            public bool MonitorStagingMoved { get; private set; }
            public bool FrameChanged { get; private set; }
            public int FrameStyleWrites { get; private set; }
            public int CornerPreferenceWrites { get; private set; }

            internal void RecordMonitorStagingMove() => MonitorStagingMoved = true;
            internal void RecordFrameChange() => FrameChanged = true;
            internal void RecordFrameStyleWrite() => FrameStyleWrites++;
            internal void RecordCornerPreferenceWrite() => CornerPreferenceWrites++;

            internal void Mark(string stage)
            {
                long now = Stopwatch.GetTimestamp();
                StagesMs[stage] = Stopwatch.GetElapsedTime(_last, now).TotalMilliseconds;
                _last = now;
            }

            internal void BeginPreparedOpening() => _openingLast = Stopwatch.GetTimestamp();

            internal void MarkPreparedOpening(string stage)
            {
                long now = Stopwatch.GetTimestamp();
                PreparedOpeningStagesMs[stage] = Stopwatch.GetElapsedTime(_openingLast, now).TotalMilliseconds;
                _openingLast = now;
            }

            internal void Complete()
                => TotalSynchronousMs = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
        }
    }
}
