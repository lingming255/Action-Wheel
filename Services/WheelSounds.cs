using System;
using System.Buffers.Binary;
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
        private readonly record struct Request(WheelSound? Sound, long Timestamp);

        private static readonly Channel<Request> Requests = Channel.CreateBounded<Request>(
            new BoundedChannelOptions(16) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
        private static WheelSoundSettings _settings = WheelSoundSettings.Default;
        private static int _started;
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
            Requests.Writer.TryWrite(new Request(null, Stopwatch.GetTimestamp()));
        }

        public static void Play(WheelSound sound)
        {
            var settings = Volatile.Read(ref _settings);
            if (!settings.Enabled || settings.VolumePercent <= 0) return;
            if (sound == WheelSound.Switch && settings.HoverVolumePercent <= 0) return;
            Requests.Writer.TryWrite(new Request(sound, Stopwatch.GetTimestamp()));
        }

        /// <summary>Stops active cues and releases devices asynchronously. A later Configure can resume.</summary>
        public static void Shutdown() => Configure(WheelSoundSettings.Default);

        private static async Task ProcessAsync()
        {
            Voice?[] voices = new Voice?[SoundForSlot.Length];
            int nextSwitch = 0;
            await foreach (var request in Requests.Reader.ReadAllAsync())
            {
                var settings = Volatile.Read(ref _settings);
                if (!settings.Enabled || settings.VolumePercent <= 0)
                {
                    ReleaseVoices(voices);
                    continue;
                }
                try
                {
                    if (request.Sound == null)
                    {
                        // Prepare without writing any audio. Enabling the option cannot emit a cue.
                        for (int i = 0; i < voices.Length; i++) voices[i] ??= Voice.TryCreate(SoundForSlot[i]);
                        continue;
                    }
                    // An unavailable/slow device must never play an old gesture after it has ended.
                    if (Stopwatch.GetElapsedTime(request.Timestamp).TotalMilliseconds > 250) continue;
                    int index = request.Sound.Value switch
                    {
                        WheelSound.Open => 0,
                        WheelSound.Switch => 1 + nextSwitch++ % 3,
                        WheelSound.Confirm => 4,
                        WheelSound.Cancel => 5,
                        _ => -1,
                    };
                    if (index < 0) continue;
                    var voice = voices[index] ??= Voice.TryCreate(request.Sound.Value);
                    if (voice == null) continue;
                    // A confirmation or cancellation supersedes the opening/switch cues.
                    if (request.Sound is WheelSound.Confirm or WheelSound.Cancel)
                        foreach (var other in voices) other?.Stop();
                    // Settings can change while a driver is opening. Recheck before writing audio.
                    var current = Volatile.Read(ref _settings);
                    if (!current.Enabled || current.VolumePercent <= 0) continue;
                    if (request.Sound == WheelSound.Switch && current.HoverVolumePercent <= 0) continue;
                    // 45% is the reference level. The relay opening cue is independently stronger;
                    // hover scales independently, preserving the cue's baked 70 ms fade.
                    double gain = current.VolumePercent / WheelSoundSettings.DefaultVolumePercent * (request.Sound.Value switch
                    {
                        WheelSound.Open => 0.45,
                        WheelSound.Switch => 0.08 * current.HoverVolumePercent / 100,
                        WheelSound.Confirm => 0.375,
                        _ => 0.08,
                    });
                    LimitVoices(voices, voice);
                    if (!voice.Play(gain))
                    {
                        voice.Dispose();
                        voices[index] = null;
                    }
                }
                catch (Exception)
                {
                    // Optional decoration cannot interrupt a gesture when audio is unavailable.
                    ReleaseVoices(voices);
                }
            }
        }

        private static void LimitVoices(Voice?[] voices, Voice upcoming)
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
            if (active >= 3) oldest?.Stop();
        }

        private static void ReleaseVoices(Voice?[] voices)
        {
            for (int i = 0; i < voices.Length; i++)
            {
                voices[i]?.Dispose();
                voices[i] = null;
            }
        }

        private sealed class Voice : IDisposable
        {
            private readonly byte[] _original;
            private readonly byte[] _scaled;
            private nint _device;
            private nint _samples;
            private nint _header;
            private bool _prepared;
            internal long StartedAt { get; private set; }
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
                    voice = new Voice(pcm);
                    var format = new WaveFormat { FormatTag = 1, Channels = 1, SamplesPerSecond = 48000,
                        AverageBytesPerSecond = 96000, BlockAlign = 2, BitsPerSample = 16 };
                    if (waveOutOpen(out voice._device, uint.MaxValue, ref format, 0, 0, 0) != 0) return null;
                    voice._samples = Marshal.AllocHGlobal(pcm.Length);
                    voice._header = Marshal.AllocHGlobal((int)HeaderSize);
                    Marshal.StructureToPtr(new WaveHeader { Data = voice._samples, BufferLength = (uint)pcm.Length },
                        voice._header, false);
                    if (waveOutPrepareHeader(voice._device, voice._header, HeaderSize) != 0) return null;
                    voice._prepared = true;
                    return voice;
                }
                catch (Exception) { return null; }
                finally
                {
                    if (voice != null && !voice._prepared) voice.Dispose();
                }
            }

            internal void Stop()
            {
                if (_device != 0) waveOutReset(_device);
                StartedAt = 0;
            }

            internal bool IsPlaying(long now) => StartedAt != 0
                && Stopwatch.GetElapsedTime(StartedAt, now).TotalSeconds < _original.Length / 96000.0;

            internal bool Play(double gain)
            {
                Stop();
                for (int i = 0; i < _original.Length; i += 2)
                {
                    short sample = BinaryPrimitives.ReadInt16LittleEndian(_original.AsSpan(i, 2));
                    BinaryPrimitives.WriteInt16LittleEndian(_scaled.AsSpan(i, 2), (short)Math.Round(sample * gain));
                }
                Marshal.Copy(_scaled, 0, _samples, _scaled.Length);
                bool started = waveOutWrite(_device, _header, HeaderSize) == 0;
                if (started) StartedAt = Stopwatch.GetTimestamp();
                return started;
            }

            public void Dispose()
            {
                Stop();
                if (_prepared) waveOutUnprepareHeader(_device, _header, HeaderSize);
                if (_device != 0) waveOutClose(_device);
                if (_header != 0) Marshal.FreeHGlobal(_header);
                if (_samples != 0) Marshal.FreeHGlobal(_samples);
                _device = _header = _samples = 0;
                _prepared = false;
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
    }
}
