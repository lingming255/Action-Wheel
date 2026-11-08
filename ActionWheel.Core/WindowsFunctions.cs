using System.Collections.Generic;
using System;

namespace Action_Wheel.Core
{
    /// <summary>A built-in Windows operation exposed by a Function action.</summary>
    public sealed class WindowsFunction
    {
        public WindowsFunction(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Id { get; }
        public string Name { get; }
        public string Category => Name.Split('·')[0].Trim();

        public string Description => Id switch
        {
            WindowsFunctions.ShowDesktop => "Hide or restore all application windows.",
            WindowsFunctions.MinimizeWindow => "Minimize the window that was active before the ring opened.",
            WindowsFunctions.MaximizeWindow => "Maximize the original foreground window.",
            WindowsFunctions.CloseWindow => "Request the original foreground window to close.",
            WindowsFunctions.SnapLeft or WindowsFunctions.SnapRight => "Snap the original foreground window to one side.",
            WindowsFunctions.Lock => "Lock the current Windows session securely.",
            WindowsFunctions.Sleep or WindowsFunctions.SignOut or WindowsFunctions.Restart or WindowsFunctions.Shutdown
                => "System action with a confirmation dialog.",
            WindowsFunctions.ActionSettings => "Open Action Wheel button and trigger settings.",
            WindowsFunctions.ActionReload => "Reload actions.json without restarting Action Wheel.",
            WindowsFunctions.QuickInfo => "Open the local QuickInfo folder for schedules, reference images and notes.",
            WindowsFunctions.ActionSwitchProfile => "Switch to the next saved profile.",
            _ when WindowsFunctions.TryGetProfileName(Id, out string profileName)
                => $"Load the saved profile '{profileName}' and make it active immediately.",
            _ => "Open or invoke this built-in Windows feature.",
        };

        /// <summary>
        /// Category marker for the function picker. Code points of the icon font the app ships,
        /// not the system's - see Services/IconFont.cs.
        /// </summary>
        public string Glyph => Category switch
        {
            "Window" => "\uEAAE",       // window
            "System" => "\uF011",       // power
            "Media" => "\uF04B",        // play
            "Action Wheel" => "\uF013",  // settings
            _ => "\uF114",              // folder
        };
    }

    public static class WindowsFunctions
    {
        public const string Lock = "windows.lock";
        public const string FileExplorer = "windows.file-explorer";
        public const string TaskManager = "windows.task-manager";
        public const string TaskView = "windows.task-view";
        public const string ShowDesktop = "window.show-desktop";
        public const string MinimizeWindow = "window.minimize";
        public const string MaximizeWindow = "window.maximize";
        public const string CloseWindow = "window.close";
        public const string SnapLeft = "window.snap-left";
        public const string SnapRight = "window.snap-right";
        public const string WindowsSettings = "windows.settings";
        public const string WindowsTerminal = "windows.terminal";
        public const string SnippingTool = "windows.snipping-tool";
        public const string VolumeMixer = "windows.volume-mixer";
        public const string ClipboardHistory = "windows.clipboard-history";
        public const string MediaPlayPause = "media.play-pause";
        public const string Sleep = "system.sleep";
        public const string SignOut = "system.sign-out";
        public const string Restart = "system.restart";
        public const string Shutdown = "system.shutdown";
        public const string ActionSettings = "action-wheel.settings";
        public const string ActionReload = "action-wheel.reload";
        public const string ActionProfiles = "action-wheel.profiles";
        public const string QuickInfo = "action-wheel.quick-info";
        public const string ActionSwitchProfile = "action-wheel.switch-profile";
        public const string CommandPrompt = "developer.command-prompt";
        public const string PowerShell = "developer.powershell";
        public const string RunDialog = "developer.run";
        public const string EnvironmentVariables = "developer.environment-variables";
        public const string Services = "developer.services";
        public const string DeviceManager = "developer.device-manager";
        public const string RegistryEditor = "developer.registry-editor";
        public const string EventViewer = "developer.event-viewer";
        public const string Notepad = "tools.notepad";
        public const string Calculator = "tools.calculator";

        private const string ProfilePrefix = "action-wheel.profile:";

        public static IReadOnlyList<WindowsFunction> Choices { get; } = new[]
        {
            new WindowsFunction(ShowDesktop, "Window · Show Desktop"),
            new WindowsFunction(MinimizeWindow, "Window · Minimize Current Window"),
            new WindowsFunction(MaximizeWindow, "Window · Maximize Current Window"),
            new WindowsFunction(CloseWindow, "Window · Close Current Window"),
            new WindowsFunction(SnapLeft, "Window · Snap Left"),
            new WindowsFunction(SnapRight, "Window · Snap Right"),
            new WindowsFunction(Lock, "System · Windows Lock"),
            new WindowsFunction(Sleep, "System · Sleep (confirmation)"),
            new WindowsFunction(SignOut, "System · Sign Out (confirmation)"),
            new WindowsFunction(Restart, "System · Restart (confirmation)"),
            new WindowsFunction(Shutdown, "System · Shut Down (confirmation)"),
            new WindowsFunction(FileExplorer, "Tools · Windows File Explorer"),
            new WindowsFunction(TaskManager, "Tools · Task Manager"),
            new WindowsFunction(TaskView, "Tools · Task View"),
            new WindowsFunction(WindowsSettings, "Tools · Windows Settings"),
            new WindowsFunction(WindowsTerminal, "Tools · Windows Terminal"),
            new WindowsFunction(SnippingTool, "Tools · Snipping Tool"),
            new WindowsFunction(VolumeMixer, "Tools · Volume Mixer"),
            new WindowsFunction(ClipboardHistory, "Tools · Clipboard History"),
            new WindowsFunction(Notepad, "Tools · Notepad"),
            new WindowsFunction(Calculator, "Tools · Calculator"),
            new WindowsFunction(CommandPrompt, "Developer · Command Prompt"),
            new WindowsFunction(PowerShell, "Developer · PowerShell"),
            new WindowsFunction(RunDialog, "Developer · Run Dialog"),
            new WindowsFunction(EnvironmentVariables, "Developer · Environment Variables"),
            new WindowsFunction(Services, "Developer · Services"),
            new WindowsFunction(DeviceManager, "Developer · Device Manager"),
            new WindowsFunction(RegistryEditor, "Developer · Registry Editor"),
            new WindowsFunction(EventViewer, "Developer · Event Viewer"),
            new WindowsFunction(MediaPlayPause, "Media · Play / Pause"),
            new WindowsFunction(ActionSettings, "Action Wheel · Settings"),
            new WindowsFunction(ActionReload, "Action Wheel · Reload Configuration"),
            new WindowsFunction(ActionSwitchProfile, "Action Wheel · Switch Profile"),
            new WindowsFunction(ActionProfiles, "Action Wheel · Open Profiles Folder"),
            new WindowsFunction(QuickInfo, "Action Wheel · Open QuickInfo Folder"),
        };

        public static WindowsFunction ProfileChoice(string profileName) =>
            new(ProfilePrefix + Uri.EscapeDataString(profileName), $"Action Wheel · Switch Profile · {profileName}");

        public static bool TryGetProfileName(string? id, out string profileName)
        {
            profileName = string.Empty;
            if (string.IsNullOrWhiteSpace(id) || !id.StartsWith(ProfilePrefix, StringComparison.OrdinalIgnoreCase))
                return false;

            try
            {
                profileName = Uri.UnescapeDataString(id[ProfilePrefix.Length..]).Trim();
                return profileName.Length > 0;
            }
            catch (UriFormatException)
            {
                return false;
            }
        }

        public static bool IsKnown(string id)
        {
            if (TryGetProfileName(id, out _))
                return true;
            foreach (var function in Choices)
            {
                if (string.Equals(function.Id, id, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
