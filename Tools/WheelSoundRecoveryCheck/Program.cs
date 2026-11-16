using System.Text.Json;
using Action_Wheel.Services;

int checks = 0;
void Require(bool result, string label)
{
    checks++;
    if (!result) throw new Exception(label);
}

using var recovery = new WheelSoundRecovery<FakeVoice>(2);
int created = 0, played = 0;
FakeVoice Create() { created++; return new FakeVoice(); }
bool Play(FakeVoice voice) { played++; return true; }
recovery.SynchronizeEndpoint(10);
Require(recovery.TryPlay(0, Create, Play, () => true), "First cue plays");
var initial = recovery.Voices[0]!;
Require(recovery.TryPlay(0, Create, Play, () => true) && created == 1, "Healthy voice is reused");
recovery.SynchronizeEndpoint(10);
Require(!initial.Disposed, "Unchanged endpoint keeps voices");
recovery.SynchronizeEndpoint(11);
Require(initial.Disposed && recovery.Voices[0] == null, "Endpoint generation invalidates cached voices");

int playedBefore = played;
Require(!recovery.TryPlay(0, Create, Play, () => false) && played == playedBefore && created == 1,
    "Expired request neither opens nor plays");
bool fresh = true;
Require(!recovery.TryPlay(0, () => { fresh = false; return Create(); }, Play, () => fresh)
    && played == playedBefore, "A slow device open cannot emit a late cue");
var slowOpened = recovery.Voices[0]!;
recovery.Dispose();
Require(slowOpened.Disposed, "Opened but unused voice remains owned and is released");

int attempts = 0;
Require(recovery.TryPlay(0, Create, voice => { attempts++; return attempts > 1; }, () => true)
    && attempts == 2, "Native failure reopens and retries the same fresh cue once");
var recovered = recovery.Voices[0]!;
Require(!recovered.Disposed, "Successful retry remains cached");
recovery.TryPlay(1, Create, Play, () => true);
var other = recovery.Voices[1]!;
attempts = 0;
Require(!recovery.TryPlay(0, Create, voice => { attempts++; return false; }, () => true)
    && attempts == 2, "Persistent native failure has a bounded retry");
Require(recovered.Disposed && other.Disposed && recovery.Voices.All(v => v == null),
    "Native failure releases every cached endpoint handle");

fresh = true;
attempts = 0;
Require(!recovery.TryPlay(0, Create, voice => { attempts++; fresh = false; return false; }, () => fresh)
    && attempts == 1, "An expired failed cue is not reopened for retry");
fresh = true;
attempts = 0;
int factoryCalls = 0;
Require(!recovery.TryPlay(0, () => { factoryCalls++; if (factoryCalls == 2) fresh = false; return Create(); },
    voice => { attempts++; return false; }, () => fresh) && attempts == 1 && factoryCalls == 2,
    "A slow retry open is checked again before submission");
recovery.Dispose();

recovery.TryPlay(0, Create, Play, () => true);
recovery.TryPlay(1, Create, Play, () => true);
var stopped = recovery.Voices.ToArray();
recovery.StopAll(voice => false);
Require(stopped.All(v => v!.Disposed) && recovery.Voices.All(v => v == null),
    "Reset failure invalidates the endpoint voices");
int beforeMissing = created;
Require(!recovery.TryPlay(0, () => null, Play, () => true) && created == beforeMissing,
    "Unavailable devices do not spin or submit");

var native = new WaveOutReleaseState { DeviceOpen = true, Prepared = true };
int closes = 0;
Require(!native.TryRelease(() => 1, () => 33, () => { closes++; return 0; }, () => true)
    && native.Prepared && native.DeviceOpen && closes == 0,
    "A failed reset with a queued buffer must not close or free native ownership");
Require(native.TryRelease(() => 0, () => 0, () => { closes++; return 0; }, () => false)
    && !native.Prepared && !native.DeviceOpen && closes == 1,
    "A returned buffer can be unprepared and then closed");
native = new WaveOutReleaseState { DeviceOpen = true, Prepared = true };
Require(!native.TryRelease(() => 5, () => throw new Exception("Invalid handle must not unprepare"),
    () => throw new Exception("Invalid handle must not close"), () => true) && native.Prepared,
    "Invalid handles still retain buffers that WinMM marks queued");
Require(native.TryRelease(() => throw new Exception("Invalid handle must not reset"),
    () => throw new Exception("Invalid handle must not unprepare"),
    () => throw new Exception("Invalid handle must not close"), () => false),
    "Once an invalid stream's buffer returns it does not occupy retirement forever");
native = new WaveOutReleaseState { DeviceOpen = true, Prepared = true };
Require(native.TryRelease(() => 1, () => 5, () => throw new Exception("Invalid handle must not close"), () => false),
    "An invalid handle with no queued data releases safely");
native = new WaveOutReleaseState { DeviceOpen = true, Prepared = true };
Require(!native.TryRelease(() => 0, () => 0, () => 1, () => false)
    && !native.Prepared && native.DeviceOpen, "A close failure retains its native lease");
Require(native.TryRelease(() => 0, () => throw new Exception("Do not unprepare twice"), () => 5, () => false),
    "An invalid close handle needs no permanent retry");

var retirement = new WheelSoundRetirement<FakeVoice>(2);
Require(retirement.TryAcquire() && retirement.TryAcquire() && !retirement.TryAcquire(),
    "All live and retired native reservations share one strict limit");
var pending = new FakeVoice();
retirement.Retire(pending);
retirement.Retire(pending);
int drained = 0;
retirement.Drain(voice => { drained++; return false; });
Require(drained == 1 && !retirement.TryAcquire(), "Duplicate retirement cannot multiply callbacks or bypass the limit");
retirement.Drain(voice => { retirement.Release(); return true; });
Require(retirement.TryAcquire() && !retirement.TryAcquire(), "Successful retirement cleanup restores exactly one reservation");
drained = 0;
retirement.Drain(voice => { drained++; return true; });
Require(drained == 0, "Released native owners leave the retry list");
Console.WriteLine(JsonSerializer.Serialize(new { Passed = true, Checks = checks,
    LinkedActualRecovery = true, OpenedAudioDevice = false, PlayedAudio = false }));

sealed class FakeVoice : IDisposable
{
    public bool Disposed { get; private set; }
    public void Dispose() => Disposed = true;
}
