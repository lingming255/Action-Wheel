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
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Passed = true, Checks = checks, LinkedActualMouseHook = true, InstalledHooks = false, UsedSendInput = false }));

[StructLayout(LayoutKind.Sequential)] struct TestMouseData { public int X, Y; public uint MouseData, Flags, Time; public nuint ExtraInfo; }
namespace Windows.Graphics { public readonly record struct PointInt32(int X, int Y); }
namespace Action_Wheel.Services { public enum MouseTrigger { MiddleButton, HoldRightPressLeft, HoldLeftPressRight, MouseButton4, MouseButton5 } }

