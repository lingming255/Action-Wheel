using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Action_Wheel.Services
{
    /// <summary>
    /// A notification-area (system tray) icon with a right-click menu.
    /// </summary>
    /// <remarks>
    /// WinUI 3 has no tray API, so this goes straight to Shell_NotifyIcon. That requires an HWND to
    /// deliver callbacks to, and it must not be one of the app's visible windows (they come and go),
    /// so a message-only window is created for the purpose. It lives on the UI thread, whose message
    /// pump dispatches the callbacks.
    /// </remarks>
    public sealed class TrayIcon : IDisposable
    {
        #region Win32 API

        private const int WM_APP = 0x8000;
        private const int WM_TRAYCALLBACK = WM_APP + 1;
        private const int WM_DESTROY = 0x0002;
        private const int WM_COMMAND = 0x0111;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_NULL = 0x0000;

        private const int NIM_ADD = 0x0000;
        private const int NIM_DELETE = 0x0002;
        private const int NIF_MESSAGE = 0x0001;
        private const int NIF_ICON = 0x0002;
        private const int NIF_TIP = 0x0004;

        private const int MF_STRING = 0x0000;
        private const int MF_GRAYED = 0x0001;
        private const int MF_CHECKED = 0x0008;
        private const int MF_POPUP = 0x0010;
        private const int MF_SEPARATOR = 0x0800;
        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const uint TPM_RETURNCMD = 0x0100;

        private static readonly IntPtr HWND_MESSAGE = new(-3);

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public int cbSize;
            public int style;
            public WndProcDelegate lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public int uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x, y; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(int exStyle, string className, string? windowName,
            int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr hMenu, int uFlags, int uIDNewItem, string? lpNewItem);

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        private static extern int TrackPopupMenuEx(IntPtr hMenu, uint fuFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        #endregion

        private const int MenuIdShow = 1;
        private const int MenuIdSettings = 2;
        private const int MenuIdEditActions = 3;
        private const int MenuIdReload = 4;
        private const int MenuIdStartup = 5;
        private const int MenuIdExit = 6;
        private const int MenuIdQuickAdd = 7;
        private const int MenuIdNoteBoard = 8;

        /// <summary>
        /// First command ID handed to a profile row; the rest follow in <see cref="_menuProfiles"/>
        /// order. Clear of every fixed MenuId* above with room to spare.
        /// </summary>
        private const int MenuIdProfileBase = 100;

        /// <summary>Most profiles a tray click can pick from before "see all in Settings" applies.</summary>
        private const int MaxProfilesInMenu = 10;

        // Held in a field so the GC cannot collect the delegate the window class points at.
        private readonly WndProcDelegate _wndProc;
        private IntPtr _hwnd;
        private IntPtr _icon;
        private bool _added;

        /// <summary>
        /// The profiles the currently-open menu offered, in the same order their command IDs
        /// (MenuIdProfileBase + index) were assigned. Rebuilt on every ShowContextMenu call, read
        /// back in HandleCommand once TrackPopupMenuEx returns which one was clicked.
        /// </summary>
        private IReadOnlyList<string> _menuProfiles = Array.Empty<string>();

        public event EventHandler? ShowRequested;
        public event EventHandler? SettingsRequested;
        public event EventHandler? QuickAddRequested;
        public event EventHandler? NoteBoardRequested;
        public event EventHandler? ReloadRequested;

        /// <summary>Raised after the tray menu toggled the startup entry, so open windows can refresh.</summary>
        public event EventHandler? StartupChanged;
        public event EventHandler? ExitRequested;

        /// <summary>Raised when the user picks a profile from the tray's Profiles submenu.</summary>
        public event EventHandler<string>? ProfileSelected;

        public TrayIcon()
        {
            _wndProc = WindowProc;
        }

        /// <summary>Creates the hidden window and adds the icon. Returns false if it could not be created.</summary>
        public bool Create(string tooltip)
        {
            var instance = GetModuleHandle(null);

            // Shared with SingleInstance, which finds this window by class name to hand a second
            // launch's intent over to the instance already running.
            var className = SingleInstance.MessageWindowClass;

            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = _wndProc,
                hInstance = instance,
                lpszClassName = className,
            };

            // A zero return can also mean "already registered" from a previous run in the same
            // process, which is fine - CreateWindowEx below is the real test.
            RegisterClassEx(ref wc);

            _hwnd = CreateWindowEx(0, className, null, 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, instance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                return false;
            }

            _icon = AppIcon.Small;

            var data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_TRAYCALLBACK,
                hIcon = _icon,
                szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip,
                szInfo = string.Empty,
                szInfoTitle = string.Empty,
            };

            _added = Shell_NotifyIcon(NIM_ADD, ref data);
            return _added;
        }

        private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            // A second launch of the app posts this instead of starting its own copy. The validity
            // check is load-bearing: a failed registration yields zero, and ShowContextMenu posts
            // WM_NULL - also zero - to this window after every menu dismissal.
            if (SingleInstance.IsShowSettingsMessageValid && msg == SingleInstance.ShowSettingsMessage)
            {
                SettingsRequested?.Invoke(this, EventArgs.Empty);
                return IntPtr.Zero;
            }

            switch (msg)
            {
                case WM_TRAYCALLBACK:
                    int mouseMsg = (int)(lParam.ToInt64() & 0xFFFF);
                    if (mouseMsg == WM_RBUTTONUP)
                        ShowContextMenu();
                    else if (mouseMsg == WM_LBUTTONDBLCLK)
                        ShowRequested?.Invoke(this, EventArgs.Empty);
                    return IntPtr.Zero;

                case WM_COMMAND:
                    HandleCommand((int)(wParam.ToInt64() & 0xFFFF));
                    return IntPtr.Zero;

                case WM_DESTROY:
                    return IntPtr.Zero;
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void ShowContextMenu()
        {
            var menu = CreatePopupMenu();
            if (menu == IntPtr.Zero)
                return;

            // Owned by the popup menu DestroyMenu(menu) tears down below - a submenu attached with
            // MF_POPUP is destroyed recursively along with its parent, so there is nothing extra to
            // clean up here even if AppendProfilesSubmenu throws before that point.
            IntPtr profilesMenu = IntPtr.Zero;

            try
            {
                AppendMenu(menu, MF_STRING, MenuIdShow, "Open Action Wheel");
                AppendMenu(menu, MF_STRING, MenuIdQuickAdd, "快捷添加网页 / 笔记…");
                AppendMenu(menu, MF_STRING, MenuIdNoteBoard, "笔记板");
                AppendMenu(menu, MF_STRING, MenuIdSettings, "Settings…");
                AppendMenu(menu, MF_SEPARATOR, 0, null);

                profilesMenu = BuildProfilesSubmenu();
                AppendMenu(menu, MF_POPUP, (int)profilesMenu, "Profiles");
                AppendMenu(menu, MF_SEPARATOR, 0, null);

                AppendMenu(menu, MF_STRING, MenuIdEditActions, "Open actions.json");
                AppendMenu(menu, MF_STRING, MenuIdReload, "Reload configuration");
                AppendMenu(menu, MF_SEPARATOR, 0, null);

                // Read fresh every time the menu opens: Windows' own Startup Apps page can turn the
                // entry off behind the app's back, and a cached flag would then show a stale tick.
                AppendMenu(menu, MF_STRING | (StartupManager.IsEnabled ? MF_CHECKED : 0),
                    MenuIdStartup, "Start with Windows");

                AppendMenu(menu, MF_SEPARATOR, 0, null);
                AppendMenu(menu, MF_STRING, MenuIdExit, "Exit");

                GetCursorPos(out var pt);

                // Required dance from the Shell docs: the owner window must be foreground before
                // TrackPopupMenu, and needs a dummy message afterwards, or the menu refuses to
                // close when the user clicks elsewhere.
                SetForegroundWindow(_hwnd);
                int command = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.x, pt.y, _hwnd, IntPtr.Zero);
                PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

                if (command != 0)
                    HandleCommand(command);
            }
            finally
            {
                DestroyMenu(menu);
            }
        }

        /// <summary>
        /// Builds the Profiles submenu, ordered most-recently-activated first (see
        /// <see cref="ProfileLibrary.TryTouch"/>), with the active one checked. Grayed out with a
        /// single explanatory row when there is nothing to switch to yet, rather than omitted -
        /// an absent submenu reads as "this build has no such feature" instead of "you have not
        /// saved a profile yet".
        /// </summary>
        private IntPtr BuildProfilesSubmenu()
        {
            var submenu = CreatePopupMenu();
            if (submenu == IntPtr.Zero)
            {
                _menuProfiles = Array.Empty<string>();
                return submenu;
            }

            var library = new ProfileLibrary();
            if (!library.TryListByRecency(MaxProfilesInMenu, out var names, out _) || names.Count == 0)
            {
                _menuProfiles = Array.Empty<string>();
                AppendMenu(submenu, MF_STRING | MF_GRAYED, 0, "No saved profiles yet");
                return submenu;
            }

            _menuProfiles = names;
            string active = ActiveProfileSettings.Load();

            for (int i = 0; i < names.Count; i++)
            {
                int flags = MF_STRING | (string.Equals(names[i], active, StringComparison.OrdinalIgnoreCase) ? MF_CHECKED : 0);
                AppendMenu(submenu, flags, MenuIdProfileBase + i, names[i]);
            }

            return submenu;
        }

        private void HandleCommand(int id)
        {
            if (id >= MenuIdProfileBase && id - MenuIdProfileBase < _menuProfiles.Count)
            {
                ProfileSelected?.Invoke(this, _menuProfiles[id - MenuIdProfileBase]);
                return;
            }

            switch (id)
            {
                case MenuIdShow:
                    ShowRequested?.Invoke(this, EventArgs.Empty);
                    break;

                case MenuIdSettings:
                    SettingsRequested?.Invoke(this, EventArgs.Empty);
                    break;

                case MenuIdQuickAdd:
                    QuickAddRequested?.Invoke(this, EventArgs.Empty);
                    break;

                case MenuIdNoteBoard:
                    NoteBoardRequested?.Invoke(this, EventArgs.Empty);
                    break;

                case MenuIdEditActions:
                    OpenActionsFile();
                    break;

                case MenuIdReload:
                    ReloadRequested?.Invoke(this, EventArgs.Empty);
                    break;

                case MenuIdStartup:
                    // The result used to be discarded, so a locked-down Run key made the tick
                    // silently refuse to move with nothing said anywhere. StartupChanged still
                    // fires either way - the main window has to re-read the registry regardless,
                    // because the tick it is showing is now wrong.
                    StartupManager.SetEnabled(!StartupManager.IsEnabled, out _);

                    StartupChanged?.Invoke(this, EventArgs.Empty);
                    break;

                case MenuIdExit:
                    ExitRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }

        private static void OpenActionsFile()
        {
            try
            {
                // Loading the config writes the defaults out if nothing exists yet, so by the time
                // this returns there is always a file to open.
                ActionConfig.Load();

                ShellCommands.OpenPath(ActionConfig.ActiveConfigPath, out _);
            }
            catch (Exception) { }
        }

        public void Dispose()
        {
            if (_added)
            {
                var data = new NOTIFYICONDATA
                {
                    cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                    hWnd = _hwnd,
                    uID = 1,
                    szTip = string.Empty,
                    szInfo = string.Empty,
                    szInfoTitle = string.Empty,
                };
                Shell_NotifyIcon(NIM_DELETE, ref data);
                _added = false;
            }

            // _icon is not destroyed here: it belongs to AppIcon, which shares it with the window
            // title bars and keeps it for the lifetime of the process.
            _icon = IntPtr.Zero;

            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }

            GC.SuppressFinalize(this);
        }
    }
}
