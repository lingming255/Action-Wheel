using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Action_Wheel.Services
{
    public enum WheelSound { Open, Switch, Confirm, Cancel }

    /// <summary>
    /// Optional short interaction cues. Callers only enqueue: resource reads, device opening and
    /// waveOut operations all happen on the single background consumer, never in a hook or UI tick.
    /// </summary>
    public static class WheelSounds
    {
        private readonly record struct Request(WheelSound? Sound, long Timestamp, bool Silent = false,
            TaskCompletionSource<QueueProbeResult>? Completion = null);
        private sealed record QueueProbeResult(string Stage, bool Passed, bool SubmittedSilence,
            bool Completed, uint? NativeResult = null, uint? NativeDeviceId = null, string? Reason = null);
        private sealed class SilentQueueProbe
        {
            private TaskCompletionSource<QueueProbeResult>? _next;
            internal Task<QueueProbeResult> ExpectNext()
            {
                var completion = new TaskCompletionSource<QueueProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref _next, completion);
                return completion.Task;
            }
            internal TaskCompletionSource<QueueProbeResult>? TakeCompletion() => Interlocked.Exchange(ref _next, null);
        }

        private static readonly Channel<Request> Requests = Channel.CreateBounded<Request>(
            new BoundedChannelOptions(16) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
        private static WheelSoundSettings _settings = WheelSoundSettings.Default;
        private static int _started;
        // Installed only by the opt-in diagnostic before hooks/windows are started.
        private static SilentQueueProbe? _silentQueueProbe;
        // Point Duel's hover cue has no time limit. Three reusable switch voices let every real
        // direction entry sound even when the pointer crosses several sectors within 70 ms.
        private static readonly WheelSound[] SoundForSlot =
            { WheelSound.Open, WheelSound.Switch, WheelSound.Switch, WheelSound.Switch, WheelSound.Confirm, WheelSound.Cancel };

        public static void Configure(WheelSoundSettings? settings)
        {
            var next = (settings ?? WheelSoundSettings.Default).Normalised();
            Volatile.Write(ref _settings, next);
            // Default-off installs neither open an audio device nor start a worker.
            if (!next.Enabled && Volatile.Read(ref _started) == 0) return;
            if (Interlocked.Exchange(ref _started, 1) == 0) _ = Task.Run(ProcessAsync);
            var probe = Volatile.Read(ref _silentQueueProbe);
            Requests.Writer.TryWrite(new Request(null, Stopwatch.GetTimestamp(), probe != null, probe?.TakeCompletion()));
        }

        public static void Play(WheelSound sound)
        {
            var settings = Volatile.Read(ref _settings);
            if (!settings.Enabled || settings.VolumePercent <= 0) return;
            if (sound == WheelSound.Switch && settings.HoverVolumePercent <= 0) return;
            var probe = Volatile.Read(ref _silentQueueProbe);
            Requests.Writer.TryWrite(new Request(sound, Stopwatch.GetTimestamp(), probe != null, probe?.TakeCompletion()));
        }

        /// <summary>Stops active cues and releases devices asynchronously. A later Configure can resume.</summary>
        public static void Shutdown() => Configure(WheelSoundSettings.Default);

        private static async Task ProcessAsync()
        {
            using var endpoint = new WheelAudioEndpointMonitor();
            using var recovery = new WheelSoundRecovery<Voice>(SoundForSlot.Length);
            Voice?[] voices = recovery.Voices;
            int nextSwitch = 0;
            await foreach (var request in Requests.Reader.ReadAllAsync())
            {
                Voice.RetryRetired();
                var settings = Volatile.Read(ref _settings);
                if (!settings.Enabled || settings.VolumePercent <= 0)
                {
                    recovery.Dispose();
                    request.Completion?.TrySetResult(new QueueProbeResult("Stopped", true, false, false));
                    continue;
                }
                try
                {
                    endpoint.RefreshDefaultEndpoint();
                    long endpointGeneration = endpoint.Generation;
                    recovery.SynchronizeEndpoint(endpointGeneration);
                    if (request.Sound == null)
                    {
                        // Prepare without writing any audio. Enabling the option cannot emit a cue.
                        for (int i = 0; i < voices.Length; i++) voices[i] ??= Voice.TryCreate(SoundForSlot[i]);
                        if (request.Completion != null)
                            request.Completion.TrySetResult(new QueueProbeResult("Prepared",
                                Array.TrueForAll(voices, voice => voice != null), false, false));
                        continue;
                    }
                    // An unavailable/slow device must never play an old gesture after it has ended.
                    if (Stopwatch.GetElapsedTime(request.Timestamp).TotalMilliseconds > 250)
                    {
                        request.Completion?.TrySetResult(new QueueProbeResult("Dropped", false, false, false,
                            Reason: "Request expired before dispatch."));
                        continue;
                    }
                    int index = request.Sound.Value switch
                    {
                        WheelSound.Open => 0,
                        WheelSound.Switch => 1 + nextSwitch++ % 3,
                        WheelSound.Confirm => 4,
                        WheelSound.Cancel => 5,
                        _ => -1,
                    };
                    if (index < 0)
                    {
                        request.Completion?.TrySetResult(new QueueProbeResult("Dropped", false, false, false,
                            Reason: "Unknown cue."));
                        continue;
                    }
                    // A confirmation or cancellation supersedes the opening/switch cues.
                    if (request.Sound is WheelSound.Confirm or WheelSound.Cancel)
                        recovery.StopAll(voice => voice.Stop());
                    bool IsFresh()
                    {
                        var current = Volatile.Read(ref _settings);
                        return Stopwatch.GetElapsedTime(request.Timestamp).TotalMilliseconds <= 250
                            && endpoint.Generation == endpointGeneration
                            && current.Enabled && current.VolumePercent > 0
                            && (request.Sound != WheelSound.Switch || current.HoverVolumePercent > 0);
                    }
                    Voice? submittedVoice = null;
                    recovery.TryPlay(index, () => Voice.TryCreate(request.Sound.Value), voice =>
                    {
                        // Settings and request age were rechecked after any driver open/retry.
                        var current = Volatile.Read(ref _settings);
                        double gain = current.VolumePercent / WheelSoundSettings.DefaultVolumePercent * (request.Sound.Value switch
                        {
                            WheelSound.Open => 0.45,
                            WheelSound.Switch => 0.08 * current.HoverVolumePercent / 100,
                            WheelSound.Confirm => 0.375,
                            _ => 0.08,
                        });
                        bool played = LimitVoices(voices, voice) && voice.Play(request.Silent ? 0 : gain, IsFresh);
                        if (played && voice.StartedAt != 0) submittedVoice = voice;
                        return played;
                    }, IsFresh);
                    if (request.Completion != null)
                    {
                        bool completed = submittedVoice != null && await WaitForCompletionAsync(submittedVoice);
                        request.Completion.TrySetResult(new QueueProbeResult(request.Sound.Value.ToString(), completed,
                            submittedVoice != null, completed, submittedVoice?.LastResult, submittedVoice?.GetDeviceId(),
                            submittedVoice == null ? "Expired, disabled, changed endpoint or unavailable device." : null));
                    }
                }
                catch (Exception)
                {
                    // Optional decoration cannot interrupt a gesture when audio is unavailable.
                    recovery.Dispose();
                    request.Completion?.TrySetResult(new QueueProbeResult("Failed", false, false, false,
                        Reason: "Audio worker rejected the diagnostic request."));
                }
            }
        }

        private static bool LimitVoices(Voice?[] voices, Voice upcoming)
        {
            // Match the game's three concurrent sources, replacing the oldest when full.
            long now = Stopwatch.GetTimestamp();
            int active = 0;
            Voice? oldest = null;
            foreach (var voice in voices)
            {
                if (voice == null || ReferenceEquals(voice, upcoming) || !voice.IsPlaying(now)) continue;
                active++;
                if (oldest == null || voice.StartedAt < oldest.StartedAt) oldest = voice;
            }
            return active < 3 || oldest == null || oldest.Stop();
        }

        /// <summary>Opt-in diagnostic: parses real cues and submits silence through real waveOut.</summary>
        internal static async Task<object> RunSilentProbeAsync()
        {
            using var endpoint = new WheelAudioEndpointMonitor();
            var results = new List<object>();
            bool passed = endpoint.DefaultEndpointId != null && endpoint.NotificationRegistrationResult == 0;
            foreach (WheelSound sound in Enum.GetValues<WheelSound>())
            {
                using Voice? voice = Voice.TryCreate(sound);
                bool submitted = voice?.Play(0) == true;
                bool completed = submitted && await WaitForCompletionAsync(voice!);
                passed &= completed;
                results.Add(new { Sound = sound.ToString(), Loaded = voice != null,
                    SubmittedSilence = submitted, Completed = completed,
                    NativeResult = voice?.LastResult, NativeDeviceId = voice?.GetDeviceId() });
            }
            var queueResults = new List<QueueProbeResult>();
            var queueProbe = new SilentQueueProbe();
            if (Interlocked.CompareExchange(ref _silentQueueProbe, queueProbe, null) != null)
                throw new InvalidOperationException("An audio queue diagnostic is already running.");
            bool stopped = false;
            try
            {
                Task<QueueProbeResult> preparation = queueProbe.ExpectNext();
                Configure(WheelSoundSettings.Default with { Enabled = true });
                QueueProbeResult prepared = await preparation.WaitAsync(TimeSpan.FromSeconds(10));
                passed &= prepared.Passed && prepared.Stage == "Prepared";
                queueResults.Add(prepared);
                foreach (WheelSound sound in Enum.GetValues<WheelSound>())
                {
                    Task<QueueProbeResult> completion = queueProbe.ExpectNext();
                    Play(sound);
                    QueueProbeResult result = await completion.WaitAsync(TimeSpan.FromSeconds(5));
                    passed &= result.Passed && result.SubmittedSilence && result.Completed;
                    queueResults.Add(result);
                }
            }
            finally
            {
                try
                {
                    Task<QueueProbeResult> completion = queueProbe.ExpectNext();
                    Shutdown();
                    QueueProbeResult result = await completion.WaitAsync(TimeSpan.FromSeconds(5));
                    stopped = result.Passed && result.Stage == "Stopped";
                }
                finally { Volatile.Write(ref _silentQueueProbe, null); }
            }
            return new { Passed = passed && stopped, DefaultEndpointId = endpoint.DefaultEndpointId,
                NotificationRegistrationResult = endpoint.NotificationRegistrationResult,
                Cues = results, QueuedCues = queueResults, QueueShutdownCompleted = stopped,
                Gain = 0, AudibleAudioPlayed = false, SavedUserConfiguration = false };
        }

        private static async Task<bool> WaitForCompletionAsync(Voice voice)
        {
            long started = Stopwatch.GetTimestamp();
            while (!voice.HasCompleted && Stopwatch.GetElapsedTime(started).TotalSeconds < 2)
                await Task.Delay(10).ConfigureAwait(false);
            return voice.HasCompleted;
        }

        private sealed class Voice : IDisposable
        {
            private static readonly WheelSoundRetirement<Voice> NativeLeases = new(SoundForSlot.Length);
            private readonly byte[] _original;
            private readonly byte[] _scaled;
            private nint _device;
            private nint _samples;
            private nint _header;
            private readonly WaveOutReleaseState _release = new();
            private bool _reserved;
            internal long StartedAt { get; private set; }
            internal uint LastResult { get; private set; }
            internal bool HasCompleted => _header != 0
                && (Marshal.PtrToStructure<WaveHeader>(_header).Flags & 1) != 0; // WHDR_DONE
            private bool IsQueued => _header != 0
                && (Marshal.PtrToStructure<WaveHeader>(_header).Flags & 16) != 0; // WHDR_INQUEUE
            private static readonly uint HeaderSize = (uint)Marshal.SizeOf<WaveHeader>();

            private Voice(byte[] pcm) { _original = pcm; _scaled = new byte[pcm.Length]; }

            internal static Voice? TryCreate(WheelSound sound)
            {
                Voice? voice = null;
                try
                {
                    string name = sound.ToString().ToLowerInvariant();
                    using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                        "ActionWheel.WheelSounds." + name + ".wav")
                        ?? throw new InvalidDataException("Missing embedded wheel sound.");
                    using var reader = new BinaryReader(stream);
                    if (reader.ReadUInt32() != 0x46464952) return null; // RIFF
                    reader.ReadUInt32();
                    if (reader.ReadUInt32() != 0x45564157) return null; // WAVE
                    bool validFormat = false;
                    byte[]? pcm = null;
                    while (stream.Position + 8 <= stream.Length)
                    {
                        uint id = reader.ReadUInt32(), length = reader.ReadUInt32();
                        long end = stream.Position + length;
                        if (end > stream.Length) return null;
                        if (id == 0x20746d66 && length >= 16) // fmt
                        {
                            validFormat = reader.ReadUInt16() == 1 && reader.ReadUInt16() == 1
                                && reader.ReadUInt32() == 48000;
                            reader.ReadUInt32();
                            validFormat &= reader.ReadUInt16() == 2 && reader.ReadUInt16() == 16;
                        }
                        else if (id == 0x61746164 && length <= 96000) pcm = reader.ReadBytes((int)length);
                        stream.Position = end + (length & 1);
                    }
                    if (!validFormat || pcm == null || pcm.Length == 0 || (pcm.Length & 1) != 0) return null;
                    RetryRetired();
                    voice = new Voice(pcm);
                    if (!NativeLeases.TryAcquire()) return null;
                    voice._reserved = true;
                    var format = new WaveFormat { FormatTag = 1, Channels = 1, SamplesPerSecond = 48000,
                        AverageBytesPerSecond = 96000, BlockAlign = 2, BitsPerSample = 16 };
                    if (waveOutOpen(out voice._device, uint.MaxValue, ref format, 0, 0, 0) != 0) return null;
                    voice._release.DeviceOpen = true;
                    voice._samples = Marshal.AllocHGlobal(pcm.Length);
                    voice._header = Marshal.AllocHGlobal((int)HeaderSize);
                    Marshal.StructureToPtr(new WaveHeader { Data = voice._samples, BufferLength = (uint)pcm.Length },
                        voice._header, false);
                    if (waveOutPrepareHeader(voice._device, voice._header, HeaderSize) != 0) return null;
                    voice._release.Prepared = true;
                    return voice;
                }
                catch (Exception) { return null; }
                finally
                {
                    if (voice != null && !voice._release.Prepared) voice.Dispose();
                }
            }

            internal bool Stop()
            {
                LastResult = _device == 0 ? 0 : waveOutReset(_device);
                StartedAt = 0;
                return LastResult == 0;
            }

            internal bool IsPlaying(long now) => StartedAt != 0
                && Stopwatch.GetElapsedTime(StartedAt, now).TotalSeconds < _original.Length / 96000.0;

            internal uint? GetDeviceId() => _device != 0 && waveOutGetID(_device, out uint id) == 0 ? id : null;

            internal bool Play(double gain, Func<bool>? canSubmit = null)
            {
                if (!Stop()) return false;
                for (int i = 0; i < _original.Length; i += 2)
                {
                    short sample = BinaryPrimitives.ReadInt16LittleEndian(_original.AsSpan(i, 2));
                    BinaryPrimitives.WriteInt16LittleEndian(_scaled.AsSpan(i, 2), (short)Math.Round(sample * gain));
                }
                Marshal.Copy(_scaled, 0, _samples, _scaled.Length);
                // Reset/copy may itself be delayed by a driver; never submit an expired gesture.
                if (canSubmit != null && !canSubmit()) return true;
                LastResult = waveOutWrite(_device, _header, HeaderSize);
                bool started = LastResult == 0;
                if (started) StartedAt = Stopwatch.GetTimestamp();
                return started;
            }

            public void Dispose()
            {
                if (!TryReleaseNative()) NativeLeases.Retire(this);
            }

            internal static void RetryRetired() => NativeLeases.Drain(voice => voice.TryReleaseNative());

            private bool TryReleaseNative()
            {
                if (!_reserved) return true;
                StartedAt = 0;
                if (!_release.TryRelease(() => waveOutReset(_device),
                    () => waveOutUnprepareHeader(_device, _header, HeaderSize),
                    () => waveOutClose(_device), () => IsQueued)) return false;
                // Only an unprepared/returned buffer can leave native ownership. A driver that
                // never returns it holds one of six leases instead of causing a dangling pointer.
                if (_header != 0) Marshal.FreeHGlobal(_header);
                if (_samples != 0) Marshal.FreeHGlobal(_samples);
                _device = _header = _samples = 0;
                _reserved = false;
                NativeLeases.Release();
                return true;
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WaveFormat
        {
            public ushort FormatTag, Channels;
            public uint SamplesPerSecond, AverageBytesPerSecond;
            public ushort BlockAlign, BitsPerSample, ExtraSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveHeader
        {
            public nint Data;
            public uint BufferLength, BytesRecorded;
            public nuint User;
            public uint Flags, Loops;
            public nint Next;
            public nuint Reserved;
        }

        [DllImport("winmm.dll")] private static extern uint waveOutOpen(out nint device, uint deviceId,
            ref WaveFormat format, nint callback, nint instance, uint flags);
        [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(nint device, nint header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveOutWrite(nint device, nint header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveOutReset(nint device);
        [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(nint device, nint header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveOutClose(nint device);
        [DllImport("winmm.dll")] private static extern uint waveOutGetID(nint device, out uint id);
    }
}
