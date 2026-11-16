using System.Reflection;
using System.Runtime.InteropServices;
using Action_Wheel.Services;

var dispatch = typeof(MouseHook).GetMethod("Dispatch", BindingFlags.Instance | BindingFlags.NonPublic)!;
// Feed the actual hook dispatcher synthetic Win32 event structures. No Windows hook is
// installed and no input is sent; only the WinUI coordinate type and settings enum are
// supplied locally so this tool needs no UI runtime or configuration files.
var nativeEventType = typeof(MouseHook).GetNestedType("MSLLHOOKSTRUCT", BindingFlags.NonPublic)!;
if (Marshal.SizeOf(nativeEventType) != Marshal.SizeOf<TestMouseData>())
    throw new Exception("Synthetic event layout no longer matches the actual Win32 hook structure.");
int checks = 0;
void Require(bool passed, string label) { checks++; if (!passed) throw new Exception(label); }
bool Send(MouseHook hook, int message, int button = 0)
{
    var data = new TestMouseData { MouseData = (uint)button << 16 };
    nint memory = Marshal.AllocHGlobal(Marshal.SizeOf<TestMouseData>());
    try { Marshal.StructureToPtr(data, memory, false); return (bool)dispatch.Invoke(hook, new object[] { 0, (nint)message, memory })!; }
    finally { Marshal.FreeHGlobal(memory); }
}
foreach (var (trigger, down, up, button) in new[] {
    (MouseTrigger.MiddleButton, 0x207, 0x208, 0),
    (MouseTrigger.MouseButton4, 0x20b, 0x20c, 1),
    (MouseTrigger.MouseButton5, 0x20b, 0x20c, 2) })
{
    bool allowed = false;
    using var hook = new MouseHook { Trigger = trigger, CanTrigger = () => allowed };
    int opened = 0, released = 0;
    hook.MiddleClick += (_, _) => opened++;
    hook.MiddleUp += (_, _) => released++;
    Require(!Send(hook, down, button), $"{trigger}: declined DOWN must pass");
    allowed = true;
    Require(!Send(hook, up, button), $"{trigger}: declined DOWN's UP must pass after handoff");
    Require(opened == 0 && released == 0, $"{trigger}: declined pair must not open/release a gesture");
    Require(Send(hook, down, button), $"{trigger}: normal DOWN must be swallowed");
    allowed = false;
    Require(Send(hook, up, button), $"{trigger}: accepted DOWN's UP must stay swallowed when trigger becomes unavailable");
    Require(opened == 1 && released == 1, $"{trigger}: normal complete pair must fire once");
    Require(!Send(hook, up, button), $"{trigger}: duplicate UP must pass");
}
foreach (var (trigger, firstDown, firstUp, secondDown, secondUp) in new[] {
    (MouseTrigger.HoldLeftPressRight, 0x201, 0x202, 0x204, 0x205),
    (MouseTrigger.HoldRightPressLeft, 0x204, 0x205, 0x201, 0x202) })
{
    bool allowed = false;
    using var hook = new MouseHook { Trigger = trigger, CanTrigger = () => allowed };
    int opened = 0, released = 0, canceled = 0;
    hook.MiddleClick += (_, _) => opened++;
    hook.MiddleUp += (_, _) => released++;
    hook.GestureCanceled += (_, _) => canceled++;
    Require(!Send(hook, firstDown), $"{trigger}: first DOWN must pass");
    Require(!Send(hook, secondDown), $"{trigger}: declined second DOWN must pass");
    allowed = true;
    Require(!Send(hook, secondUp), $"{trigger}: declined second UP must pass after handoff");
    Require(!Send(hook, firstUp), $"{trigger}: first UP must stay balanced");
    Require(opened == 0 && released == 0 && canceled == 0, $"{trigger}: declined chord must not generate gesture callbacks");
    Require(!Send(hook, firstDown), $"{trigger}: normal first DOWN must pass");
    Require(Send(hook, secondDown), $"{trigger}: normal second DOWN must be swallowed");
    allowed = false;
    Require(Send(hook, secondUp), $"{trigger}: accepted second UP must stay swallowed");
    Require(!Send(hook, firstUp), $"{trigger}: normal first UP must pass");
    Require(opened == 1 && released == 1 && canceled == 0, $"{trigger}: normal complete chord must fire once");
}
// Ownership ends independently from button/key release. A dismissal DOWN must never expose
// its UP to the underlying application after the menu has returned the foreground.
{
    bool owns = true;
    using var hook = new MouseHook { OwnsInput = () => owns, InterceptButtonDown = _ => owns };
    Require(Send(hook, 0x20a) && Send(hook, 0x20e), "Both scroll axes stay in the interactive wheel");
    Require(Send(hook, 0x201), "Outside left DOWN is consumed");
    owns = false;
    Require(Send(hook, 0x202), "Outside left UP stays consumed after dismissal");
    Require(!Send(hook, 0x202), "Duplicate outside UP passes");
    Require(!Send(hook, 0x20a), "Decoration restores normal scrolling");
    Require(!Send(hook, 0x201) && !Send(hook, 0x202), "Normal left pair resumes after dismissal");
    owns = true;
    hook.InterceptButtonDown = _ => false;
    Require(!Send(hook, 0x201) && !Send(hook, 0x202), "Inside clicks remain available to XAML buttons");
}
{
    bool owns = true;
    using var hook = new MouseHook { Trigger = MouseTrigger.HoldLeftPressRight, InterceptButtonDown = _ => owns };
    int opened = 0;
    hook.MiddleClick += (_, _) => opened++;
    Require(Send(hook, 0x201), "A dismissal first DOWN is isolated");
    owns = false;
    Require(!Send(hook, 0x204) && opened == 0, "An isolated dismissal DOWN cannot seed a new chord");
    Send(hook, 0x205);
    Require(Send(hook, 0x202), "The isolated dismissal UP remains consumed after another button pair");
}
foreach (var (trigger, firstDown, firstUp, secondDown, secondUp) in new[] {
    (MouseTrigger.HoldLeftPressRight, 0x201, 0x202, 0x204, 0x205),
    (MouseTrigger.HoldRightPressLeft, 0x204, 0x205, 0x201, 0x202) })
foreach (bool cancel in new[] { false, true })
{
    using var hook = new MouseHook { Trigger = trigger, RedirectChordFirstButtonUp = true };
    int delivered = 0;
    hook.ButtonTargetAt = _ => (nint)42;
    hook.SendButtonRelease = (target, message, _) =>
    {
        Require(target == 42 && message == firstUp, "Only the original first button is returned to its original HWND");
        delivered++;
        return true;
    };
    Require(!Send(hook, firstDown) && Send(hook, secondDown), "Chord preserves first DOWN and consumes trigger DOWN");
    Require(Send(hook, cancel ? firstUp : secondUp), "First physical release is consumed or redirected");
    Require(Send(hook, cancel ? secondUp : firstUp), "Second physical release balances its destination");
    Require(delivered == 1, "Original target receives exactly one matching UP");
    Require(!Send(hook, firstUp) && delivered == 1, "Duplicate UP is not redirected");
}
{
    using var hook = new MouseHook { Trigger = MouseTrigger.HoldLeftPressRight, RedirectChordFirstButtonUp = true };
    hook.ButtonTargetAt = _ => (nint)42;
    hook.SendButtonRelease = (_, _, _) => false;
    Send(hook, 0x201); Send(hook, 0x204); Send(hook, 0x205);
    Require(!Send(hook, 0x202), "Failed targeted delivery falls back to the physical first UP");
}

var keyDispatch = typeof(KeyboardHook).GetMethod("Dispatch", BindingFlags.Instance | BindingFlags.NonPublic)!;
bool SendKey(KeyboardHook hook, int message, uint key, nint marker = default)
{
    var data = new TestKeyData { Key = key, ExtraInfo = marker };
    nint memory = Marshal.AllocHGlobal(Marshal.SizeOf<TestKeyData>());
    try { Marshal.StructureToPtr(data, memory, false); return (bool)keyDispatch.Invoke(hook, new object[] { 0, (nint)message, memory })!; }
    finally { Marshal.FreeHGlobal(memory); }
}
{
    bool owns = true;
    int callbacks = 0;
    using var hook = new KeyboardHook { IsKeyDown = _ => false };
    hook.MenuKeyPressed = _ => { callbacks++; return owns; };
    Require(SendKey(hook, 0x100, 0x41), "Menu key DOWN is consumed");
    owns = false;
    Require(SendKey(hook, 0x100, 0x41) && callbacks == 1, "Held key repeats never trigger a second command or leak");
    Require(SendKey(hook, 0x101, 0x41), "Menu key UP stays consumed after dismissal");
    Require(!SendKey(hook, 0x101, 0x41), "Duplicate key UP passes");
    Require(!SendKey(hook, 0x100, 0x42) && !SendKey(hook, 0x101, 0x42), "Normal typing resumes after ownership ends");
    owns = true;
    Require(SendKey(hook, 0x104, 0x5b) && SendKey(hook, 0x105, 0x5b), "System-key pair is isolated too");
    Require(!SendKey(hook, 0x101, 0x11), "Modifier DOWN predating the wheel has an unconsumed UP");
    Require(!SendKey(hook, 0x100, 0x56, InputInjection.Marker), "Own injected shortcut bypasses isolation");
}
{
    bool open = true;
    using var hook = new KeyboardHook { IsKeyDown = _ => false };
    hook.EscapePressed = () => { bool wasOpen = open; open = false; return wasOpen; };
    Require(SendKey(hook, 0x100, 0x1b), "Esc DOWN dismisses the menu");
    Require(SendKey(hook, 0x100, 0x1b) && SendKey(hook, 0x101, 0x1b), "Esc repeat and UP cannot reach the original app");
    Require(!SendKey(hook, 0x100, 0x1b), "Esc works normally after its paired release");
}
{
    int opened = 0, released = 0;
    using var hook = new KeyboardHook { IsKeyDown = _ => true };
    hook.EmergencyTriggerPressed = () => { opened++; return true; };
    hook.EmergencyTriggerReleased = () => { released++; return true; };
    Require(SendKey(hook, 0x100, 0x20) && SendKey(hook, 0x100, 0x20), "QuickInfo Space and its repeats stay consumed");
    Require(SendKey(hook, 0x101, 0x20) && opened == 1 && released == 1, "QuickInfo still completes once on Space UP");
}
// Exercise the production scheduler with a busy dispatcher, without sending real pointer input.
{
    var queued = new Queue<Action>();
    var applied = new List<(long Gesture, int Position)>();
    var interrupted = new List<bool>();
    var latest = new Action_Wheel.Core.LatestGestureUpdate<int>(
        callback => { queued.Enqueue(callback); return true; },
        (gesture, point, resetHold) => { applied.Add((gesture, point)); interrupted.Add(resetHold); },
        (before, after) => before != after);
    for (int point = 0; point < 10000; point++) latest.Submit(1, point);
    Require(queued.Count == 1, "A busy dispatcher must keep only one pointer turn per gesture");
    queued.Dequeue()();
    Require(applied.SequenceEqual(new[] { (1L, 9999) }), "Pointer feedback must use the latest position");
    latest.Submit(1, 10);
    latest.Submit(1, 11);
    latest.Submit(1, 10);
    queued.Dequeue()();
    Require(interrupted.Last(), "A leave-and-return must restart hold even when the final selection is unchanged");
    latest.Submit(1, 20);
    queued.Enqueue(() => applied.Add((1, -1))); // Exact release remains behind its pending movement.
    queued.Dequeue()();
    queued.Dequeue()();
    Require(applied.TakeLast(2).SequenceEqual(new[] { (1L, 20), (1L, -1) }), "Coalescing must preserve release order");
    latest.Submit(1, 21);
    queued.Enqueue(() => applied.Add((2, -2))); // Opening the next gesture.
    latest.Submit(2, 30);
    queued.Dequeue()();
    queued.Dequeue()();
    queued.Dequeue()();
    Require(applied.TakeLast(2).SequenceEqual(new[] { (2L, -2), (2L, 30) }),
        "A stale queued update must not update the new wheel before its opening turn");
    bool accepting = false;
    var recovered = new Action_Wheel.Core.LatestGestureUpdate<int>(
        callback => { if (!accepting) return false; queued.Enqueue(callback); return true; },
        (gesture, point, _) => applied.Add((gesture, point)), (before, after) => before != after);
    recovered.Submit(3, 1);
    accepting = true;
    recovered.Submit(3, 2);
    Require(queued.Count == 1, "A rejected dispatcher post must not strand later pointer updates");
    queued.Dequeue()();
    Require(applied.Last() == (3L, 2), "Recovered dispatch must use the fresh value");
}
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Passed = true, Checks = checks, LinkedActualMouseHook = true, LinkedActualKeyboardHook = true, LinkedPointerScheduler = true, InstalledHooks = false, UsedSendInput = false }));

[StructLayout(LayoutKind.Sequential)] struct TestMouseData { public int X, Y; public uint MouseData, Flags, Time; public nuint ExtraInfo; }
[StructLayout(LayoutKind.Sequential)] struct TestKeyData { public uint Key, Scan, Flags, Time; public nint ExtraInfo; }
namespace Windows.Graphics { public readonly record struct PointInt32(int X, int Y); }
namespace Action_Wheel.Services { public enum MouseTrigger { MiddleButton, HoldRightPressLeft, HoldLeftPressRight, MouseButton4, MouseButton5 } }

