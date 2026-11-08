using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Action_Wheel.Services
{
    /// <summary>
    /// Carries out an <see cref="ActionItem"/>: synthesises a keyboard shortcut, or starts a
    /// program / opens a path or URI.
    /// </summary>
    /// <remarks>
    /// Deciding which keys a shortcut string means lives in <see cref="ShortcutKeys"/>; this class
    /// is only the part that needs user32.
    /// </remarks>
    public static class ActionDispatcher
    {
        // Two actions can arrive close together (a gesture and a click, for example). Interleaving
        // their modifiers produces a different chord and can leave a modifier logically pressed.
        private static readonly object InputGate = new();

        #region Win32 API

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        // The Win32 INPUT struct is a union of MOUSEINPUT (the largest arm), KEYBDINPUT and
        // HARDWAREINPUT. Only the keyboard arm is used here, but MOUSEINPUT has to stay in the
        // union so sizeof(INPUT) matches what SendInput expects - and it differs between x86 and
        // x64, so it cannot just be hardcoded with StructLayout.Size.
#pragma warning disable CS0649 // mi is never assigned - it exists purely to size the union
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public MOUSEINPUT mi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }
#pragma warning restore CS0649

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LockWorkStation();

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ExitWindowsEx(uint flags, uint reason);

        [DllImport("powrprof.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges,
            ref TOKEN_PRIVILEGES newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint LowPart; public int HighPart; }
        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID Luid;
            public uint Attributes;
        }

        private const int SW_MINIMIZE = 6;
        private const int SW_MAXIMIZE = 3;
        private const uint WM_CLOSE = 0x0010;
        private const uint MB_YESNO_WARNING_DEFBUTTON2 = 0x00000004 | 0x00000030 | 0x00000100;
        private const int IDYES = 6;
        private const uint EWX_LOGOFF = 0x00000000;
        private const uint EWX_SHUTDOWN = 0x00000001;
        private const uint EWX_REBOOT = 0x00000002;
        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint SE_PRIVILEGE_ENABLED = 0x00000002;
        private const int ERROR_NOT_ALL_ASSIGNED = 1300;

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

        #endregion

        /// <summary>
        /// Raised when an action could not be carried out, with a message fit to show the user.
        /// The menu has already closed by this point, so there is no window left to report into -
        /// the app surfaces it on the main window and in the log instead of it vanishing.
        /// </summary>
        public static event EventHandler<string>? ActionFailed;

        /// <summary>Raised when the built-in Action Settings function is selected.</summary>
        public static event EventHandler? SettingsRequested;

        /// <summary>Raised when the active configuration should be read again.</summary>
        public static event EventHandler? ReloadRequested;

        /// <summary>Raised when a dynamic Action Wheel profile function is selected.</summary>
        public static event EventHandler<string>? ProfileRequested;

        /// <summary>
        /// Runs the action. Never throws - a bad entry in actions.json must not take the app down.
        /// </summary>
        public static IntPtr CaptureTargetWindow() => GetForegroundWindow();

        public static void Execute(ActionItem action, IntPtr targetWindow = default)
        {
            try
            {
                switch (action.Kind)
                {
                    case ActionKind.Keys:
                        SendShortcut(action);
                        break;

                    case ActionKind.Launch:
                        Launch(action);
                        break;

                    case ActionKind.CopyText:
                        // Clipboard requires the UI thread; both dispatch callers marshal copies.
                        if (!ShellCommands.CopyText(action.Value, out string copyError))
                            Fail(action, copyError);
                        break;

                    case ActionKind.Function:
                        RunFunction(action, targetWindow);
                        break;
                }
            }
            catch (Exception ex)
            {
                Fail(action, ex.Message, ex);
            }
        }

        private static void Fail(ActionItem action, string reason, Exception? ex = null)
        {
            var message = $"'{action.Label}' failed: {reason}";

            ActionFailed?.Invoke(null, message);
        }

        private static void Launch(ActionItem action)
        {
            if (string.IsNullOrWhiteSpace(action.Value))
            {
                Fail(action, "there is nothing to launch.");
                return;
            }

            // UseShellExecute lets a single "value" cover executables, documents, folders and URIs.
            // ShellExecute passes Arguments on to the verb, so parameters work for programs; they
            // are meaningless for a URL or a document and are simply ignored there.
            var info = new ProcessStartInfo(action.Value) { UseShellExecute = true };

            if (!string.IsNullOrWhiteSpace(action.Arguments))
                info.Arguments = action.Arguments;

            try
            {
                Process.Start(info)?.Dispose();
            }
            catch (Exception ex)
            {
                // The overwhelmingly common cases are a program that has been uninstalled or moved,
                // and a UAC prompt the user dismissed. Both used to be completely silent.
                Fail(action, $"could not start '{action.Value}' ({ex.Message})");
            }
        }

        /// <summary>
        /// Presses a shortcut such as "Ctrl+Shift+S" on the foreground window. The radial menu is
        /// WS_EX_NOACTIVATE, so it never steals focus and the keystrokes land in whatever app the
        /// user was already working in.
        /// </summary>
        private static void SendShortcut(ActionItem action)
        {
            var keys = ShortcutKeys.Parse(action.Value, out string unknownKey);
            if (keys.Count == 0)
            {
                // Half a chord is never sent, so an unrecognised key name means the button does
                // nothing at all. Naming the key is the difference between "this button is broken"
                // and "I typed Cmd instead of Ctrl".
                Fail(action, unknownKey.Length > 0
                    ? $"'{unknownKey}' is not a key name this app knows (shortcut '{action.Value}')."
                    : $"the shortcut '{action.Value}' is empty.");
                return;
            }

            // Win+L is a secure shell command, not an ordinary application shortcut. Depending on
            // the Windows version and shell state, injected Win+L events may deliberately be
            // ignored. Use the documented workstation API for this exact chord instead.
            if (IsLockShortcut(keys))
            {
                if (!LockWorkStation())
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not lock the workstation");

                return;
            }

            // Keep key-down and key-up as separate batches. Some applications inspect modifier
            // state while processing the main key and intermittently miss a chord whose complete
            // lifetime was inserted into the queue in a single SendInput call.
            var keyDowns = new INPUT[keys.Count];
            var keyUps = new INPUT[keys.Count];
            for (int i = 0; i < keys.Count; i++)
            {
                keyDowns[i] = MakeInput(keys[i], keyUp: false);
                keyUps[keys.Count - 1 - i] = MakeInput(keys[i], keyUp: true);
            }

            lock (InputGate)
            {
                uint pressed = SendInput((uint)keyDowns.Length, keyDowns, Marshal.SizeOf<INPUT>());
                if (pressed != keyDowns.Length)
                {
                    int error = Marshal.GetLastWin32Error();
                    ReleaseAll(keyUps);
                    ReportSendFailure(action, pressed, keyDowns.Length, error, "key-down");
                    return;
                }

                // A tiny hold time mirrors a physical chord without adding perceptible latency.
                Thread.Sleep(12);

                uint released = SendInput((uint)keyUps.Length, keyUps, Marshal.SizeOf<INPUT>());
                if (released == keyUps.Length)
                    return;

                int releaseError = Marshal.GetLastWin32Error();
                // Send every key-up again after a short write. Duplicate key-up events are benign;
                // a modifier left down is not, because it corrupts everything the user types next.
                ReleaseAll(keyUps);
                ReportSendFailure(action, released, keyUps.Length, releaseError, "key-up");
            }
        }

        private static void RunFunction(ActionItem action, IntPtr targetWindow)
        {
            if (WindowsFunctions.TryGetProfileName(action.Value, out string profileName))
            {
                ProfileRequested?.Invoke(null, profileName);
                return;
            }

            switch (action.Value.ToLowerInvariant())
            {
                case WindowsFunctions.Lock:
                    if (!LockWorkStation())
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not lock the workstation");
                    break;

                case WindowsFunctions.FileExplorer:
                    Launch(new ActionItem { Label = action.Label, Kind = ActionKind.Launch, Value = "explorer.exe" });
                    break;

                case WindowsFunctions.TaskManager:
                    Launch(new ActionItem { Label = action.Label, Kind = ActionKind.Launch, Value = "taskmgr.exe" });
                    break;

                case WindowsFunctions.TaskView:
                    SendShortcut(new ActionItem { Label = action.Label, Kind = ActionKind.Keys, Value = "Win+Tab" });
                    break;

                case WindowsFunctions.ShowDesktop:
                    SendBuiltInShortcut(action, "Win+D");
                    break;

                case WindowsFunctions.MinimizeWindow:
                    RunWindowCommand(action, targetWindow, SW_MINIMIZE);
                    break;

                case WindowsFunctions.MaximizeWindow:
                    RunWindowCommand(action, targetWindow, SW_MAXIMIZE);
                    break;

                case WindowsFunctions.CloseWindow:
                    if (targetWindow == IntPtr.Zero || !PostMessage(targetWindow, WM_CLOSE, IntPtr.Zero, IntPtr.Zero))
                        Fail(action, "the original foreground window is no longer available.");
                    break;

                case WindowsFunctions.SnapLeft:
                    SendBuiltInShortcut(action, "Win+Left");
                    break;

                case WindowsFunctions.SnapRight:
                    SendBuiltInShortcut(action, "Win+Right");
                    break;

                case WindowsFunctions.WindowsSettings:
                    LaunchBuiltIn(action, "ms-settings:");
                    break;

                case WindowsFunctions.WindowsTerminal:
                    LaunchBuiltIn(action, "wt.exe");
                    break;

                case WindowsFunctions.CommandPrompt:
                    LaunchBuiltIn(action, "cmd.exe");
                    break;

                case WindowsFunctions.PowerShell:
                    LaunchBuiltIn(action, "powershell.exe");
                    break;

                case WindowsFunctions.RunDialog:
                    SendBuiltInShortcut(action, "Win+R");
                    break;

                case WindowsFunctions.EnvironmentVariables:
                    LaunchBuiltIn(action, "rundll32.exe", "sysdm.cpl,EditEnvironmentVariables");
                    break;

                case WindowsFunctions.Services:
                    LaunchBuiltIn(action, "services.msc");
                    break;

                case WindowsFunctions.DeviceManager:
                    LaunchBuiltIn(action, "devmgmt.msc");
                    break;

                case WindowsFunctions.RegistryEditor:
                    LaunchBuiltIn(action, "regedit.exe");
                    break;

                case WindowsFunctions.EventViewer:
                    LaunchBuiltIn(action, "eventvwr.msc");
                    break;

                case WindowsFunctions.Notepad:
                    LaunchBuiltIn(action, "notepad.exe");
                    break;

                case WindowsFunctions.Calculator:
                    LaunchBuiltIn(action, "calc.exe");
                    break;

                case WindowsFunctions.SnippingTool:
                    LaunchBuiltIn(action, "ms-screenclip:");
                    break;

                case WindowsFunctions.VolumeMixer:
                    LaunchBuiltIn(action, "sndvol.exe");
                    break;

                case WindowsFunctions.ClipboardHistory:
                    SendBuiltInShortcut(action, "Win+V");
                    break;

                case WindowsFunctions.MediaPlayPause:
                    SendBuiltInShortcut(action, "PlayPause");
                    break;

                case WindowsFunctions.Sleep:
                    if (ConfirmPowerAction(targetWindow, "Put this computer to sleep?")
                        && !SetSuspendState(false, false, false))
                        ReportSystemFailure(action, "put the computer to sleep");
                    break;

                case WindowsFunctions.SignOut:
                    if (ConfirmPowerAction(targetWindow, "Sign out of Windows now? Unsaved work may be lost.")
                        && !ExitWindowsEx(EWX_LOGOFF, 0))
                        ReportSystemFailure(action, "sign out of Windows");
                    break;

                case WindowsFunctions.Restart:
                    if (ConfirmPowerAction(targetWindow, "Restart this computer now? Unsaved work may be lost.")
                        && TryPrepareShutdown(action)
                        && !ExitWindowsEx(EWX_REBOOT, 0)) ReportSystemFailure(action, "restart Windows");
                    break;

                case WindowsFunctions.Shutdown:
                    if (ConfirmPowerAction(targetWindow, "Shut down this computer now? Unsaved work may be lost.")
                        && TryPrepareShutdown(action)
                        && !ExitWindowsEx(EWX_SHUTDOWN, 0)) ReportSystemFailure(action, "shut down Windows");
                    break;

                case WindowsFunctions.ActionSettings:
                    SettingsRequested?.Invoke(null, EventArgs.Empty);
                    break;

                case WindowsFunctions.ActionReload:
                    ReloadRequested?.Invoke(null, EventArgs.Empty);
                    break;

                case WindowsFunctions.ActionSwitchProfile:
                    ProfileRequested?.Invoke(null, string.Empty);
                    break;

                case WindowsFunctions.ActionProfiles:
                    LaunchBuiltIn(action, new ProfileLibrary().DirectoryPath);
                    break;

                case WindowsFunctions.QuickInfo:
                    Directory.CreateDirectory(AppDataPaths.QuickInfoDirectoryPath);
                    LaunchBuiltIn(action, AppDataPaths.QuickInfoDirectoryPath);
                    break;

                default:
                    Fail(action, $"the Windows function '{action.Value}' is not supported.");
                    break;
            }
        }

        private static void SendBuiltInShortcut(ActionItem source, string shortcut) =>
            SendShortcut(new ActionItem { Label = source.Label, Kind = ActionKind.Keys, Value = shortcut });

        private static void LaunchBuiltIn(ActionItem source, string target) =>
            Launch(new ActionItem { Label = source.Label, Kind = ActionKind.Launch, Value = target });

        private static void LaunchBuiltIn(ActionItem source, string target, string arguments) =>
            Launch(new ActionItem { Label = source.Label, Kind = ActionKind.Launch, Value = target, Arguments = arguments });

        private static void RunWindowCommand(ActionItem action, IntPtr targetWindow, int command)
        {
            if (targetWindow == IntPtr.Zero)
            {
                Fail(action, "the original foreground window is no longer available.");
                return;
            }

            _ = ShowWindow(targetWindow, command);
        }

        private static bool ConfirmPowerAction(IntPtr owner, string message) =>
            MessageBox(owner, message, "Action Wheel — Confirm system action",
                MB_YESNO_WARNING_DEFBUTTON2) == IDYES;

        private static void ReportSystemFailure(ActionItem action, string operation)
        {
            int error = Marshal.GetLastWin32Error();
            Fail(action, $"Windows could not {operation} (Win32 error {error}: {new Win32Exception(error).Message}).");
        }

        private static bool TryPrepareShutdown(ActionItem action)
        {
            using var currentProcess = Process.GetCurrentProcess();
            if (!OpenProcessToken(currentProcess.Handle,
                TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token))
            {
                ReportSystemFailure(action, "open the process token for shutdown");
                return false;
            }

            try
            {
                if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid))
                {
                    ReportSystemFailure(action, "locate the shutdown privilege");
                    return false;
                }

                var privileges = new TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Luid = luid,
                    Attributes = SE_PRIVILEGE_ENABLED,
                };
                if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)
                    || Marshal.GetLastWin32Error() == ERROR_NOT_ALL_ASSIGNED)
                {
                    ReportSystemFailure(action, "enable the shutdown privilege");
                    return false;
                }
                return true;
            }
            finally { CloseHandle(token); }
        }

        private static void ReportSendFailure(ActionItem action, uint sent, int expected, int error, string phase)
        {
            // SendInput is blocked outright by UIPI when the foreground window belongs to a process
            // at a higher integrity level - an elevated console, or the secure desktop. It reports
            // this by returning a short count, which the old code discarded.
            Fail(action, sent == 0
                ? $"SendInput was blocked (Win32 error {error}). The foreground window may be running elevated."
                : $"only {sent} of {expected} {phase} events were accepted (Win32 error {error}).");
        }

        private static void ReleaseAll(INPUT[] keyUps)
        {
            Thread.Sleep(2);
            _ = SendInput((uint)keyUps.Length, keyUps, Marshal.SizeOf<INPUT>());
        }

        private static bool IsLockShortcut(System.Collections.Generic.IReadOnlyList<ushort> keys) =>
            keys.Count == 2
            && ((keys[0] is 0x5B or 0x5C && keys[1] == 0x4C)
                || (keys[1] is 0x5B or 0x5C && keys[0] == 0x4C));

        private static INPUT MakeInput(ushort vk, bool keyUp)
        {
            uint flags = keyUp ? KEYEVENTF_KEYUP : 0;
            if (ShortcutKeys.IsExtendedKey(vk))
                flags |= KEYEVENTF_EXTENDEDKEY;

            return new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = vk,
                        dwFlags = flags,
                        dwExtraInfo = InputInjection.Marker,
                    }
                },
            };
        }
    }
}
