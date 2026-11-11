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
        // Display names are bilingual. Keep these category keys stable for the existing icon
        // mapping, rather than parsing translated labels or a user's saved profile name.
        public string Category => Id switch
        {
            _ when Id.StartsWith("window.", StringComparison.OrdinalIgnoreCase) => "Window",
            _ when string.Equals(Id, WindowsFunctions.Lock, StringComparison.OrdinalIgnoreCase) => "System",
            _ when Id.StartsWith("system.", StringComparison.OrdinalIgnoreCase) => "System",
            _ when Id.StartsWith("media.", StringComparison.OrdinalIgnoreCase) => "Media",
            _ when Id.StartsWith("action-wheel.", StringComparison.OrdinalIgnoreCase) => "Action Wheel",
            _ when Id.StartsWith("developer.", StringComparison.OrdinalIgnoreCase) => "Developer",
            _ when Id.StartsWith("windows.", StringComparison.OrdinalIgnoreCase)
                || Id.StartsWith("tools.", StringComparison.OrdinalIgnoreCase) => "Tools",
            _ => Name.Split('·')[0].Trim(),
        };

        public string Description => Id switch
        {
            WindowsFunctions.ShowDesktop => "隐藏或恢复所有窗口 / Hide or restore all application windows.",
            WindowsFunctions.MinimizeWindow => "最小化打开轮盘前的活动窗口 / Minimize the window that was active before the ring opened.",
            WindowsFunctions.MaximizeWindow => "最大化原前台窗口 / Maximize the original foreground window.",
            WindowsFunctions.CloseWindow => "请求关闭原前台窗口 / Request the original foreground window to close.",
            WindowsFunctions.SnapLeft or WindowsFunctions.SnapRight => "将原前台窗口贴靠到一侧 / Snap the original foreground window to one side.",
            WindowsFunctions.Lock => "安全锁定当前 Windows 会话 / Lock the current Windows session securely.",
            WindowsFunctions.Sleep or WindowsFunctions.SignOut or WindowsFunctions.Restart or WindowsFunctions.Shutdown
                => "执行前会弹出确认框 / System action with a confirmation dialog.",
            WindowsFunctions.ActionSettings => "打开按钮和触发设置 / Open Action Wheel button and trigger settings.",
            WindowsFunctions.ActionReload => "无需重启即可重载 actions.json / Reload actions.json without restarting Action Wheel.",
            WindowsFunctions.QuickInfo => "打开存放日程、参考图和笔记的本地 QuickInfo 文件夹 / Open the local QuickInfo folder for schedules, reference images and notes.",
            WindowsFunctions.ActionSwitchProfile => "切换到下一个已保存配置 / Switch to the next saved profile.",
            _ when WindowsFunctions.TryGetProfileName(Id, out string profileName)
                => $"立即启用已保存配置“{profileName}” / Load the saved profile '{profileName}' and make it active immediately.",
            _ => "打开或执行此 Windows 内置功能 / Open or invoke this built-in Windows feature.",
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
            new WindowsFunction(ShowDesktop, "窗口 · 显示桌面 / Window · Show Desktop"),
            new WindowsFunction(MinimizeWindow, "窗口 · 最小化当前窗口 / Window · Minimize Current Window"),
            new WindowsFunction(MaximizeWindow, "窗口 · 最大化当前窗口 / Window · Maximize Current Window"),
            new WindowsFunction(CloseWindow, "窗口 · 关闭当前窗口 / Window · Close Current Window"),
            new WindowsFunction(SnapLeft, "窗口 · 向左贴靠 / Window · Snap Left"),
            new WindowsFunction(SnapRight, "窗口 · 向右贴靠 / Window · Snap Right"),
            new WindowsFunction(Lock, "系统 · 锁屏 / System · Windows Lock"),
            new WindowsFunction(Sleep, "系统 · 睡眠（需确认） / System · Sleep (confirmation)"),
            new WindowsFunction(SignOut, "系统 · 注销（需确认） / System · Sign Out (confirmation)"),
            new WindowsFunction(Restart, "系统 · 重启（需确认） / System · Restart (confirmation)"),
            new WindowsFunction(Shutdown, "系统 · 关机（需确认） / System · Shut Down (confirmation)"),
            new WindowsFunction(FileExplorer, "工具 · 文件资源管理器 / Tools · Windows File Explorer"),
            new WindowsFunction(TaskManager, "工具 · 任务管理器 / Tools · Task Manager"),
            new WindowsFunction(TaskView, "工具 · 任务视图 / Tools · Task View"),
            new WindowsFunction(WindowsSettings, "工具 · Windows 设置 / Tools · Windows Settings"),
            new WindowsFunction(WindowsTerminal, "工具 · Windows 终端 / Tools · Windows Terminal"),
            new WindowsFunction(SnippingTool, "工具 · 截图工具 / Tools · Snipping Tool"),
            new WindowsFunction(VolumeMixer, "工具 · 音量混合器 / Tools · Volume Mixer"),
            new WindowsFunction(ClipboardHistory, "工具 · 剪贴板历史 / Tools · Clipboard History"),
            new WindowsFunction(Notepad, "工具 · 记事本 / Tools · Notepad"),
            new WindowsFunction(Calculator, "工具 · 计算器 / Tools · Calculator"),
            new WindowsFunction(CommandPrompt, "开发 · 命令提示符 / Developer · Command Prompt"),
            new WindowsFunction(PowerShell, "开发 · PowerShell / Developer · PowerShell"),
            new WindowsFunction(RunDialog, "开发 · 运行窗口 / Developer · Run Dialog"),
            new WindowsFunction(EnvironmentVariables, "开发 · 环境变量 / Developer · Environment Variables"),
            new WindowsFunction(Services, "开发 · 系统服务 / Developer · Services"),
            new WindowsFunction(DeviceManager, "开发 · 设备管理器 / Developer · Device Manager"),
            new WindowsFunction(RegistryEditor, "开发 · 注册表编辑器 / Developer · Registry Editor"),
            new WindowsFunction(EventViewer, "开发 · 事件查看器 / Developer · Event Viewer"),
            new WindowsFunction(MediaPlayPause, "媒体 · 播放／暂停 / Media · Play / Pause"),
            new WindowsFunction(ActionSettings, "Action Wheel · 设置 / Action Wheel · Settings"),
            new WindowsFunction(ActionReload, "Action Wheel · 重载配置 / Action Wheel · Reload Configuration"),
            new WindowsFunction(ActionSwitchProfile, "Action Wheel · 切换配置 / Action Wheel · Switch Profile"),
            new WindowsFunction(ActionProfiles, "Action Wheel · 打开配置文件夹 / Action Wheel · Open Profiles Folder"),
            new WindowsFunction(QuickInfo, "Action Wheel · 打开 QuickInfo 文件夹 / Action Wheel · Open QuickInfo Folder"),
        };

        public static WindowsFunction ProfileChoice(string profileName) =>
            new(ProfilePrefix + Uri.EscapeDataString(profileName), $"切换配置 / Action Wheel · Switch Profile · {profileName}");

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
