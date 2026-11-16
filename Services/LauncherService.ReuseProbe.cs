using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Action_Wheel.Overlay;
using Microsoft.UI.Dispatching;
using WinRT.Interop;
using Windows.Graphics;

namespace Action_Wheel.Services
{
    public partial class LauncherService
    {
        // The opt-in fixture uses the real pool methods without Start, static dispatcher
        // subscriptions, QuickInfo directory creation, live config notifications or audio setup.
        private LauncherService(RingAppearance probeAppearance)
        {
            RequireExistingProbeConfig();
            Configuration = ActionConfig.LoadDetailed();
            _actions = Configuration.Actions;
            _appearance = probeAppearance;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _hookHealthTimer = _dispatcherQueue.CreateTimer();
            _mouseHook = new MouseHook();
            _keyboardHook = new KeyboardHook();
            _configWatcher = new ConfigWatcher();
            _configWatcher.Dispose();
        }

        /// <summary>Exercise the actual bounded launcher pool without hooks, actions or capture.</summary>
        internal static async Task<object> RunReusePoolProbeAsync(RingAppearance appearance)
        {
            var failures = new List<string>();
            var rounds = new List<object>();
            int checks = 0;
            bool idleReloadDestroyed = false, activeSnapshotRetired = false, stopDestroyedAll = false;
            bool foregroundRestored = false;
            nint originalForeground = WindowInputFocus.Foreground;
            LauncherService? service = null;
            void Require(bool condition, string message)
            {
                checks++;
                if (!condition) throw new InvalidOperationException(message);
            }
            void RestoreForeground()
            {
                if (originalForeground != 0) WindowInputFocus.TryActivate(originalForeground);
            }
            void StopPreparation()
            {
                service?._openingPreparationTimer?.Stop();
                // Reload reads the user's preferences, but these synthetic sessions stay silent.
                WheelSounds.Configure(WheelSoundSettings.Default with { Enabled = false });
            }
            void ShowCloaked(RadialMenu menu)
            {
                nint hwnd = WindowNative.GetWindowHandle(menu);
                int cloak = 1;
                Require(ReusePoolSetDwmAttribute(hwnd, 13, ref cloak, sizeof(int)) == 0,
                    "Launcher pool window could not be cloaked before Show.");
                menu.ActionTargetWindow = originalForeground;
                service!._currentMenu = menu;
                menu.ShowAtPosition(new PointInt32(240, 240));
                service.CompleteMenuInitialization(menu);
                Require(ReusePoolGetDwmAttribute(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0,
                    "Launcher pool Show exposed an uncloaked window.");
                RestoreForeground();
            }
            try
            {
                WheelSounds.Configure(WheelSoundSettings.Default with { Enabled = false });
                var synthetic = Enumerable.Range(0, 9).Select(tag => new ActionItem
                {
                    Tag = tag, Label = $"pool probe {tag}", Kind = ActionKind.None, Glyph = "F00D",
                }).ToArray();
                var instantAppearance = appearance with { Animation = RingAnimation.None };
                service = new LauncherService(instantAppearance) { _actions = synthetic };
                service.PrepareMainMenu();
                Require(service._idleMenu is { IsParked: true, CanReuse: true },
                    "Production pool preparation did not publish a ready idle wheel.");
                Require(WindowInputFocus.Foreground == originalForeground,
                    "Launcher idle preparation changed the foreground.");
                var first = service._idleMenu!;
                nint firstHwnd = WindowNative.GetWindowHandle(first);
                int sessions = 0;
                first.SessionEnded += (_, _) => sessions++;
                for (int round = 0; round < 4; round++)
                {
                    var rented = service.RentMainMenu(synthetic);
                    Require(ReferenceEquals(rented, first) && service._idleMenu == null,
                        "Repeated RentMainMenu did not consume the same single idle instance.");
                    ShowCloaked(rented!);
                    Require(rented!.OwnsInput && !rented.IsParked, "Rented wheel did not start a new session.");
                    rented.CloseMenu();
                    StopPreparation();
                    Require(ReferenceEquals(service._idleMenu, first) && service._currentMenu == null
                        && service._menus.Count == 1 && first.IsParked && sessions == round + 1,
                        "SessionEnded did not return exactly the same wheel to the bounded pool.");
                    await Task.Delay(25);
                    Require(ReusePoolIsWindow(firstHwnd) && service._menus.Count == 1,
                        "A deferred callback destroyed or duplicated the idle wheel.");
                    rounds.Add(new { Round = round, Passed = true, Hwnd = firstHwnd.ToInt64(),
                        TrackedWindows = service._menus.Count, SessionEndedCount = sessions });
                }

                RequireExistingProbeConfig();
                service.ReloadActions();
                StopPreparation();
                await Task.Delay(35);
                idleReloadDestroyed = service._idleMenu == null && !ReusePoolIsWindow(firstHwnd)
                    && !service._menus.ContainsKey(first);
                Require(idleReloadDestroyed, "ReloadActions retained the old idle window or its tracked snapshot.");

                service._actions = synthetic;
                service._appearance = instantAppearance;
                service.PrepareMainMenu();
                var active = service.RentMainMenu(synthetic);
                Require(active != null && !ReferenceEquals(active, first),
                    "A new configuration did not receive a new prepared main wheel.");
                nint activeHwnd = WindowNative.GetWindowHandle(active!);
                ShowCloaked(active!);
                RequireExistingProbeConfig();
                service.ReloadActions();
                StopPreparation();
                Require(ReferenceEquals(service._currentMenu, active) && ReusePoolIsWindow(activeHwnd),
                    "ReloadActions interrupted the active window instead of letting its snapshot finish.");
                active!.CloseMenu();
                StopPreparation();
                await Task.Delay(35);
                activeSnapshotRetired = service._idleMenu == null && service._currentMenu == null
                    && !ReusePoolIsWindow(activeHwnd) && !service._menus.ContainsKey(active);
                Require(activeSnapshotRetired, "An active old configuration returned to the new idle pool.");

                service._actions = synthetic;
                service._appearance = appearance with { Animation = RingAnimation.Fade, AnimationDurationMs = 120 };
                service.PrepareMainMenu();
                var tail = service.RentMainMenu(synthetic);
                Require(tail != null, "Closing-tail setup did not obtain a prepared wheel.");
                nint tailHwnd = WindowNative.GetWindowHandle(tail!);
                ShowCloaked(tail!);
                service.CloseCurrentMenu();
                StopPreparation();
                Require(tail!.IsClosing && !tail.IsParked && service._currentMenu == null
                    && service._menus.ContainsKey(tail), "A normal closing tail stopped being tracked.");
                service.PrepareMainMenu();
                var idle = service._idleMenu;
                Require(idle != null && !ReferenceEquals(idle, tail) && service._menus.Count == 2,
                    "Stop setup did not retain both a closing tail and one idle wheel.");
                nint idleHwnd = WindowNative.GetWindowHandle(idle!);
                int tailClosed = 0, idleClosed = 0;
                tail.Closed += (_, _) => tailClosed++;
                idle!.Closed += (_, _) => idleClosed++;
                service.Stop();
                await Task.Delay(260);
                stopDestroyedAll = !ReusePoolIsWindow(tailHwnd) && !ReusePoolIsWindow(idleHwnd)
                    && service._idleMenu == null && service._currentMenu == null
                    && service._menus.Count == 0 && service._playingRefreshes.Count == 0
                    && service._openingPreparationTimer?.IsRunning != true
                    && tailClosed == 1 && idleClosed == 1;
                Require(stopDestroyedAll, "Stop leaked, recached or double-closed an idle/closing-tail window.");
                Require(!service.Status.IsRunning, "The pool fixture unexpectedly started global hooks.");
            }
            catch (Exception exception) { failures.Add(exception.ToString()); }
            finally
            {
                try { service?.Dispose(); }
                catch (Exception exception) { failures.Add("Launcher pool cleanup: " + exception.Message); }
                RestoreForeground();
                foregroundRestored = originalForeground == 0 || WindowInputFocus.Foreground == originalForeground;
                if (!foregroundRestored) failures.Add("Launcher pool probe could not restore the original foreground.");
            }
            return new
            {
                Passed = failures.Count == 0, Checks = checks, Rounds = rounds,
                IdleReloadDestroyed = idleReloadDestroyed, ActiveSnapshotRetired = activeSnapshotRetired,
                StopDestroyedIdleAndClosingTail = stopDestroyedAll, OriginalForegroundRestored = foregroundRestored,
                InstalledHooks = false, DispatchedActions = false, SavedUserConfiguration = false,
                ScreenPixelsReadBack = false, Failures = failures,
            };
        }

        private static void RequireExistingProbeConfig()
        {
            // LoadDetailed creates defaults on a first-ever install. This diagnostic only reads
            // an existing configuration, so decline that environment before invoking the loader.
            if (!File.Exists(ActionConfig.PortableConfigPath) && !File.Exists(ActionConfig.UserConfigPath))
                throw new InvalidOperationException("Pool reload diagnostics require an existing configuration; no defaults were written.");
        }

        [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
        private static extern int ReusePoolSetDwmAttribute(nint hwnd, int attribute, ref int value, int size);
        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
        private static extern int ReusePoolGetDwmAttribute(nint hwnd, int attribute, out int value, int size);
        [DllImport("user32.dll", EntryPoint = "IsWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReusePoolIsWindow(nint hwnd);
    }
}
