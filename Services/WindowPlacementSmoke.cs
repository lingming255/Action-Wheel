using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Action_Wheel.Settings;
using Action_Wheel.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace Action_Wheel.Services
{
    /// <summary>Opt-in placement checks using only this process's DWM-cloaked test windows.</summary>
    internal static class WindowPlacementSmoke
    {
        internal static async void Run(string reportPath, Action exit)
        {
            Window? owner = null;
            Window? external = null;
            var windows = new List<Window>();
            var rounds = new List<object>();
            var failures = new List<string>();
            object? selection = null, dispatch = null;
            nint originalForeground = WindowInputFocus.Foreground;
            void Require(bool value, string message)
            {
                if (!value) throw new InvalidOperationException(message);
            }
            void RestoreForeground()
            {
                if (originalForeground != 0) WindowInputFocus.TryActivate(originalForeground);
            }
            try
            {
                if (!Path.IsPathFullyQualified(reportPath))
                    throw new ArgumentException("Placement report path must be absolute.");
                WheelSounds.Configure(WheelSoundSettings.Default with { Enabled = false });
                owner = new Window();
                owner.AppWindow.Hide();
                // This SDK's WinRT vector supports indexed access but its IEnumerable adapter
                // can query an unavailable generic IID. Copy by index before using LINQ.
                var found = DisplayArea.FindAll();
                var displayList = new List<DisplayArea>();
                for (int index = 0; index < found.Count; index++) displayList.Add(found[index]);
                var displays = displayList.OrderBy(display => display.WorkArea.X).ToArray();
                Require(displays.Length > 0, "No displays are available.");
                WindowPlacementTarget Target(DisplayArea display) => new(
                    display.WorkArea.X + display.WorkArea.Width / 2,
                    display.WorkArea.Y + display.WorkArea.Height / 2);
                var first = Target(displays[^1]);
                string notePath = Path.Combine(Path.GetTempPath(), "wheel-placement-" + Guid.NewGuid().ToString("N") + ".txt");
                var settings = new SettingsWindow(new SettingsViewModel(), first);
                windows.Add(settings);
                var board = new NoteBoardWindow(notePath, first);
                windows.Add(board);
                var add = new QuickAddWindow(preview: true, placementTarget: first);
                windows.Add(add);
                var tools = new (string Name, Window Window, Action<WindowPlacementTarget?> Place)[]
                {
                    ("settings", settings, settings.PlaceOnTarget),
                    ("noteboard", board, board.PlaceOnTarget),
                    ("quick-add", add, add.PlaceOnTarget),
                };
                foreach (var tool in tools)
                {
                    Cloak(tool.Window);
                    tool.Window.AppWindow.Show(false);
                    await Task.Delay(180);
                    CheckPosition(tool.Window, displays[^1], tool.Name + " initial Loaded");
                    foreach (var display in displays.Concat(displays.Reverse()))
                    {
                        tool.Place(Target(display));
                        await Task.Delay(70);
                        CheckPosition(tool.Window, display, tool.Name + " reused");
                    }
                    // Resizing a tool is a user choice. A later invocation must preserve that
                    // logical size while moving to another DPI, rather than restoring defaults.
                    tool.Window.AppWindow.Resize(new SizeInt32(480, 360));
                    double oldScale = Dpi(tool.Window) / 96.0;
                    double widthDip = tool.Window.AppWindow.Size.Width / oldScale;
                    double heightDip = tool.Window.AppWindow.Size.Height / oldScale;
                    tool.Place(first);
                    await Task.Delay(70);
                    var expected = displays[^1].WorkArea;
                    double newScale = Dpi(tool.Window) / 96.0;
                    Require(Math.Abs(tool.Window.AppWindow.Size.Width - Math.Min(expected.Width, Math.Round(widthDip * newScale))) <= 2
                        && Math.Abs(tool.Window.AppWindow.Size.Height - Math.Min(expected.Height, Math.Round(heightDip * newScale))) <= 2,
                        tool.Name + " lost its existing logical size.");
                    CheckPosition(tool.Window, displays[^1], tool.Name + " resized");
                }

                if (board.AppWindow.Presenter is OverlappedPresenter boardPresenter)
                {
                    boardPresenter.Maximize();
                    RestoreForeground();
                    foreach (var display in displays)
                    {
                        board.PlaceOnTarget(Target(display));
                        await Task.Delay(80);
                        Require(boardPresenter.State == OverlappedPresenterState.Maximized,
                            "Moving the note board lost its maximized state.");
                        CheckMonitor(board, display, "maximized note board");
                    }
                    boardPresenter.Restore(false);
                    RestoreForeground();
                    boardPresenter.Minimize();
                    board.PlaceOnTarget(first);
                    await Task.Delay(80);
                    Require(boardPresenter.State != OverlappedPresenterState.Minimized,
                        "An existing minimized note board did not restore for invocation.");
                    CheckMonitor(board, displays[^1], "restored note board");
                }

                // The external-window mover is checked only against a disposable local HWND.
                // This exercises its real native placement without opening a browser/program.
                external = new Window { Content = new Grid() };
                Cloak(external);
                external.AppWindow.Resize(new SizeInt32(620, 440));
                external.AppWindow.Show(false);
                await Task.Delay(80);
                double nativeWidthDip = external.AppWindow.Size.Width / (Dpi(external) / 96.0);
                double nativeHeightDip = external.AppWindow.Size.Height / (Dpi(external) / 96.0);
                foreach (var display in displays)
                {
                    Require(await LaunchWindowFollower.MoveWindowAsync(WindowNative.GetWindowHandle(external), Target(display)),
                        "Native launch-window mover rejected the local test window.");
                    await Task.Delay(70);
                    CheckMonitor(external, display, "native normal");
                    double nativeScale = Dpi(external) / 96.0;
                    Require(Math.Abs(external.AppWindow.Size.Width - Math.Min(display.WorkArea.Width, Math.Round(nativeWidthDip * nativeScale))) <= 3
                        && Math.Abs(external.AppWindow.Size.Height - Math.Min(display.WorkArea.Height, Math.Round(nativeHeightDip * nativeScale))) <= 3,
                        "Native move scaled a per-monitor window more than once.");
                }
                if (external.AppWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.Maximize();
                    RestoreForeground();
                    foreach (var display in displays.Reverse())
                    {
                        Require(await LaunchWindowFollower.MoveWindowAsync(WindowNative.GetWindowHandle(external), Target(display)),
                            "Native launch-window mover rejected a maximized test window.");
                        await Task.Delay(90);
                        Require(IsZoomed(WindowNative.GetWindowHandle(external)), "Native move lost maximized state.");
                        CheckMonitor(external, display, "native maximized");
                        RestoreForeground();
                    }
                    presenter.Restore(false);
                    RestoreForeground();
                    foreach (var display in displays)
                    {
                        presenter.Minimize();
                        await Task.Delay(50);
                        Require(await LaunchWindowFollower.MoveWindowAsync(WindowNative.GetWindowHandle(external), Target(display)),
                            "Native launch-window mover rejected a minimized test window.");
                        presenter.Restore(false);
                        await Task.Delay(80);
                        CheckMonitor(external, display, "native minimized restore");
                    }
                }
                Require(WindowInputFocus.Foreground == originalForeground, "Placement unexpectedly changed foreground.");
                nint protectedWheel = WindowNative.GetWindowHandle(external);
                WindowInputFocus.EnterWheel(protectedWheel);
                try
                {
                    Require(!WindowInputFocus.CanActivateActionTarget,
                        "A newer interactive wheel did not block an older action's activation.");
                    WindowInputFocus.LeaveWheel(WindowNative.GetWindowHandle(board));
                    Require(!WindowInputFocus.CanActivateActionTarget,
                        "Closing an older wheel cleared a newer wheel's activation ownership.");
                }
                finally { WindowInputFocus.LeaveWheel(protectedWheel); }
                Require(WindowInputFocus.CanActivateActionTarget, "Ending the wheel left action activation blocked.");
                selection = LaunchWindowFollower.RunSelectionProbe();
                Require(JsonSerializer.SerializeToElement(selection).GetProperty("Passed").GetBoolean(),
                    "External candidate-selection checks failed.");
                dispatch = await LauncherService.RunPlacementDispatchProbeAsync();
                Require(JsonSerializer.SerializeToElement(dispatch).GetProperty("Passed").GetBoolean(),
                    "Reused wheel dispatch lost its original screen.");
                Require(!File.Exists(notePath), "Opening an unchanged diagnostic board created a note file.");

                void CheckMonitor(Window window, DisplayArea expected, string label)
                {
                    var actual = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Nearest);
                    Require(actual?.DisplayId.Value == expected.DisplayId.Value, label + " is on the wrong monitor.");
                    rounds.Add(new { Case = label, Passed = true, WorkArea = expected.WorkArea,
                        Position = window.AppWindow.Position, Size = window.AppWindow.Size, Dpi = Dpi(window) });
                }
                void CheckPosition(Window window, DisplayArea expected, string label)
                {
                    CheckMonitor(window, expected, label);
                    var point = window.AppWindow.Position;
                    var size = window.AppWindow.Size;
                    var work = expected.WorkArea;
                    Require(Math.Abs(point.X - (work.X + (work.Width - size.Width) / 2)) <= 2
                        && Math.Abs(point.Y - (work.Y + (work.Height - size.Height) / 2)) <= 2
                        && size.Width <= work.Width && size.Height <= work.Height,
                        label + " is not centered inside its target work area.");
                }
            }
            catch (Exception exception) { failures.Add(exception.ToString()); }
            finally
            {
                foreach (var window in windows)
                {
                    try { if (window is SettingsWindow settings) settings.ForceClose(); else window.Close(); }
                    catch (Exception exception) { failures.Add("Tool cleanup: " + exception.Message); }
                }
                try { external?.Close(); }
                catch (Exception exception) { failures.Add("Native cleanup: " + exception.Message); }
                RestoreForeground();
                try
                {
                    if (Path.IsPathFullyQualified(reportPath))
                        File.WriteAllText(reportPath, JsonSerializer.Serialize(new
                        {
                            Passed = failures.Count == 0, Rounds = rounds, Selection = selection, Dispatch = dispatch,
                            Failures = failures, RegisteredInputHooks = false, SavedUserConfiguration = false,
                            DisplayedWindows = false, OpenedExternalApplications = false, CapturedScreenPixels = false,
                            Boundary = "Only disposable DWM-cloaked local windows move. Candidate policy is tested with synthetic identities. This does not prove association and activation behavior of every real external application.",
                        }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
                }
                finally { owner?.Close(); exit(); }
            }
        }

        private static uint Dpi(Window window) => GetDpiForWindow(WindowNative.GetWindowHandle(window));

        private static void Cloak(Window window)
        {
            int cloak = 1;
            if (DwmSetWindowAttribute(WindowNative.GetWindowHandle(window), 13, ref cloak, sizeof(int)) != 0)
                throw new InvalidOperationException("Diagnostic window could not be cloaked.");
        }

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
        [DllImport("user32.dll")] private static extern bool IsZoomed(nint hwnd);
    }
}
