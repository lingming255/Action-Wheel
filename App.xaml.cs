using System;
using Action_Wheel.Services;
using Action_Wheel.Settings;
using Action_Wheel.ViewModels;
using Microsoft.UI.Xaml;

namespace Action_Wheel
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// Manages the global mouse hook and radial menu launcher service.
    /// </summary>
    public partial class App : Application
    {
        private MainWindow? _window;
        private SettingsWindow? _settingsWindow;
        private QuickAddWindow? _quickAddWindow;
        private NoteBoardWindow? _noteBoardWindow;
        private LauncherService? _launcherService;
        private TrayIcon? _trayIcon;
        private bool _exiting;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();


            // Note for anyone tempted to add DebugSettings.BindingFailed here to catch the silent
            // failures classic {Binding} produces: it does not fire in WinUI 3. Measured - the flag
            // reads back true, the handler attaches, and a deliberately misspelled binding path
            // still raises nothing. Verify-Bindings.ps1 checks the paths statically instead.
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            // Explicit diagnostics run before single-instance registration and hooks. UI previews
            // save only isolated copies; the input/pixel probes briefly show disposable windows.
            string[] command = Environment.GetCommandLineArgs();
            if (command.Length == 3 && command[1] == "--input-smoke-report")
            {
                WheelInputSmoke.Run(command[2], Exit);
                return;
            }
            if (command.Length == 2 && command[1] == "--quick-add-preview")
            {
                // Explicit UI verification uses an isolated config and installs no global hooks.
                _quickAddWindow = new QuickAddWindow(preview: true);
                _quickAddWindow.Closed += (s, e) => Exit();
                ActivateToolWindow(_quickAddWindow);
                return;
            }
            if (command.Length == 3 && command[1] == "--note-board-preview")
            {
                if (!System.IO.Path.IsPathFullyQualified(command[2])) { Exit(); return; }
                _noteBoardWindow = new NoteBoardWindow(command[2]);
                _noteBoardWindow.Closed += (s, e) => Exit();
                ActivateToolWindow(_noteBoardWindow);
                return;
            }
            if (command.Length == 3 && command[1] == "--wheel-opening-smoke")
            {
                WheelOpeningSmoke.Run(command[2], Exit);
                return;
            }
            if (command.Length == 3 && command[1] is "--wheel-glass-smoke" or "--wheel-glass-pixel-probe")
            {
                WheelGlassSmoke.Run(command[2], command[1] == "--wheel-glass-pixel-probe", Exit);
                return;
            }
            if (command.Length == 3 && command[1] == "--settings-smoke")
            {
                SettingsSmoke.Run(command[2], Exit);
                return;
            }
            if (command.Length == 3 && command[1] == "--wheel-position-smoke")
            {
                WheelPositionSmoke.Run(command[2], Exit);
                return;
            }
            if (command.Length == 3 && command[1] == "--refresh-gpu-smoke")
            {
                GpuRefreshSmoke.Run(command[2], Exit);
                return;
            }

            // Logging was removed for privacy. Clear the two exact files written by older builds;
            // settings, profiles and all other user data in this directory remain untouched.
            AppDataPaths.DeleteLegacyLogs();

            // Two instances would install two global hooks (one middle click, two overlays), fight
            // over the tray icon, and overwrite each other's actions.json. Hand this launch's intent
            // to the copy already running instead - opening Settings is the useful interpretation of
            // "the user started the app again".
            if (!SingleInstance.TryAcquire())
            {
                // If the running instance cannot be reached, this launch does nothing visible at
                // all - the user double-clicked and got no window.
                SingleInstance.AskRunningInstanceToShowSettings();

                Exit();
                return;
            }

            // The launcher comes first so the window can show its real state on the very first
            // frame. LauncherService.Start does not throw - it reports a hook it could not install
            // through its Status, which is what the main window puts on screen.
            try
            {
                _launcherService = new LauncherService();
                _launcherService.SettingsRequested += (s, e) => ShowSettings();
                _launcherService.QuickAddRequested += OnQuickAddRequested;
                _launcherService.NoteBoardRequested += OnNoteBoardRequested;
                _launcherService.Start();
            }
            catch (Exception)
            {
                // Only a failure to construct the service reaches here; the window then shows the
                // "not installed" state rather than the app starting up looking healthy.
            }

            // Only the primary instance performs icon-store maintenance. Starting it after the
            // launcher has read actions.json also avoids racing first-run config creation.
            _ = AppIconExtractor.CleanupUnusedAsync();

            var viewModel = new MainViewModel(_launcherService);
            viewModel.SettingsRequested += (s, e) => ShowSettings();

            _window = new MainWindow(viewModel);
            ActionDispatcher.QuickAddRequested += OnQuickAddRequested;
            ActionDispatcher.NoteBoardRequested += OnNoteBoardRequested;
            _window.Activate();

            SetUpTrayIcon();

            // Exercise the model and complete renderer offscreen while the app is idle. A gesture
            // started immediately after launch awaits this same one-shot task before capturing.
            RefreshRenderWarmup.TryDepthWarmup = RefreshDepthEstimator.Warmup;
            RefreshRenderWarmup.TryGpuWarmup = (reference, cancellation) =>
            {
                using var renderer = new GpuRefreshRenderer(new GpuRefreshScene(reference));
                for (int frame = 0; frame < 8; frame++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    renderer.Render(500 + frame * 170);
                }
                // Only a startup diagnostic fence; playback never reads its pixels back.
                renderer.Device.WaitIdle();
                return true;
            };
            _ = RefreshRenderWarmup.EnsureAsync();

            _ = CheckForUpdatesAsync(viewModel);
        }

        /// <summary>
        /// Fire-and-forget, same as <see cref="AppIconExtractor.CleanupUnusedAsync"/> above: a
        /// network round-trip has no business delaying <see cref="OnLaunched"/>, which is already
        /// measured at 185 ms. <see cref="UpdateChecker.CheckAsync"/> never throws, so nothing here
        /// needs a try/catch of its own - only the explicit hop back onto the UI thread, the same
        /// discipline <c>LauncherService</c> applies to its own background-thread callbacks.
        /// </summary>
        private async System.Threading.Tasks.Task CheckForUpdatesAsync(MainViewModel viewModel)
        {
            var currentVersion = typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0);
            var update = await UpdateChecker.CheckAsync(currentVersion).ConfigureAwait(false);
            if (update == null)
                return;

            _window?.DispatcherQueue.TryEnqueue(() => viewModel.ApplyUpdateCheck(update));
        }

        /// <summary>
        /// Puts an icon in the notification area and turns the main window's close button into
        /// "hide", so the app keeps running (and the global hook keeps working) in the background.
        /// If the tray icon cannot be created, closing the window exits as before - otherwise the
        /// app would become impossible to quit.
        /// </summary>
        private void SetUpTrayIcon()
        {
            _trayIcon = new TrayIcon();
            _trayIcon.ShowRequested += (s, e) => ShowMainWindow();
            _trayIcon.SettingsRequested += (s, e) => ShowSettings();
            _trayIcon.QuickAddRequested += (s, e) => ShowQuickAdd();
            _trayIcon.NoteBoardRequested += (s, e) => ShowNoteBoard();
            _trayIcon.ReloadRequested += (s, e) => _launcherService?.ReloadActions();
            _trayIcon.StartupChanged += (s, e) => _window?.ViewModel.RefreshStartup();
            _trayIcon.ExitRequested += (s, e) => ExitApp();
            _trayIcon.ProfileSelected += (s, name) => _launcherService?.RequestProfileSwitch(name);

            if (!_trayIcon.Create("Action Wheel - hold left and press right, or hold Ctrl+Alt+Space for Quick Info"))
            {
                _trayIcon.Dispose();
                _trayIcon = null;

                if (_window != null)
                    _window.Closed += (s, e) => ExitApp();

                return;
            }

            if (_window != null)
            {
                _window.AppWindow.Closing += (s, e) =>
                {
                    if (_exiting)
                        return;

                    e.Cancel = true;
                    _window.AppWindow.Hide();
                };
            }
        }

        private void ShowMainWindow()
        {
            if (_window == null)
                return;

            _window.AppWindow.Show();
            _window.Activate();

            // Both may have been changed elsewhere while the window sat hidden in the tray.
            _window.ViewModel.RefreshStartup();
            _window.ViewModel.RefreshHookStatus();
        }

        /// <summary>
        /// Opens the settings window, or brings the existing one forward. Only one is allowed:
        /// two windows editing the same file would silently overwrite each other on save.
        /// </summary>
        private void ShowSettings()
        {
            // Only one action editor may exist: its in-memory snapshot would otherwise replace
            // changes just saved by the quick adder.
            if (_quickAddWindow != null)
            {
                try { ActivateToolWindow(_quickAddWindow); return; }
                catch (Exception) { _quickAddWindow = null; }
            }
            // Reusing the existing window can throw if it has already been torn down - the Closed
            // event is not guaranteed to have cleared the field by the time this runs. Falling back
            // to a fresh window is the difference between "Settings stops working after you close
            // it once" and it just working.
            if (_settingsWindow != null)
            {
                try
                {
                    _settingsWindow.AppWindow.Show();
                    _settingsWindow.Activate();
                    return;
                }
                catch (Exception)
                {
                    _settingsWindow = null;
                }
            }

            try
            {
                // App is the composition root: it owns construction and lifetime, while the Window
                // only owns WinUI concerns and receives the state/commands it presents.
                var settings = new SettingsWindow(new SettingsViewModel());
                _settingsWindow = settings;

                settings.ActionsSaved += (s, e) =>
                {
                    // The save is about to trip the file watcher. Suppressing it keeps the reload
                    // to the one this handler does, instead of a second one a moment later.
                    _launcherService?.SuppressConfigWatcher();
                    _launcherService?.ReloadActions();
                };
                settings.Closed += (s, e) =>
                {
                    if (ReferenceEquals(_settingsWindow, settings))
                        _settingsWindow = null;
                };

                settings.Activate();
            }
            catch (Exception)
            {
                // Without this the exception escapes the click handler and the app dies on what
                // should be a recoverable failure.
                _settingsWindow = null;
            }
        }

        private void OnQuickAddRequested(object? sender, IntPtr targetWindow) =>
            _window?.DispatcherQueue.TryEnqueue(() => ShowQuickAdd(targetWindow));

        private void OnNoteBoardRequested(object? sender, EventArgs e) =>
            _window?.DispatcherQueue.TryEnqueue(ShowNoteBoard);

        private async void ShowQuickAdd(IntPtr targetWindow = default)
        {
            try
            {
                if (_quickAddWindow != null)
                {
                    try { ActivateToolWindow(_quickAddWindow); return; }
                    catch (Exception) { _quickAddWindow = null; }
                }
                if (_settingsWindow != null)
                {
                    if (_settingsWindow.ViewModel.HasUnsavedChanges)
                    {
                        _settingsWindow.Activate();
                        var root = (_settingsWindow.Content as FrameworkElement)?.XamlRoot;
                        if (root != null)
                        {
                            await new Microsoft.UI.Xaml.Controls.ContentDialog
                            {
                                XamlRoot = root,
                                Title = "请先保存设置中的修改",
                                Content = "保存或撤销当前修改后，再打开快捷添加，避免覆盖你正在编辑的内容。",
                                CloseButtonText = "返回设置",
                            }.ShowAsync();
                        }
                        return;
                    }
                    _settingsWindow.ForceClose();
                    _settingsWindow = null;
                }
                var quickAdd = new QuickAddWindow(targetWindow);
                _quickAddWindow = quickAdd;
                quickAdd.ActionsSaved += (s, e) =>
                {
                    _launcherService?.SuppressConfigWatcher();
                    _launcherService?.ReloadActions();
                };
                quickAdd.Closed += (s, e) =>
                {
                    if (ReferenceEquals(_quickAddWindow, quickAdd)) _quickAddWindow = null;
                };
                ActivateToolWindow(quickAdd);
            }
            catch (Exception error)
            {
                ReportFeatureError("无法打开快捷添加", error);
            }
        }

        private void ShowNoteBoard()
        {
            try
            {
                if (_noteBoardWindow == null)
                {
                    var board = new NoteBoardWindow();
                    _noteBoardWindow = board;
                    board.Closed += (s, e) =>
                    {
                        if (ReferenceEquals(_noteBoardWindow, board)) _noteBoardWindow = null;
                    };
                }
                ActivateToolWindow(_noteBoardWindow);
                _noteBoardWindow.FocusEditor();
            }
            catch (Exception error)
            {
                ReportFeatureError("无法打开笔记板", error);
            }
        }

        private static void ActivateToolWindow(Window window)
        {
            window.Activate();
            window.AppWindow.Show();
            WindowInputFocus.TryActivate(WinRT.Interop.WindowNative.GetWindowHandle(window));
        }

        private void ReportFeatureError(string title, Exception error)
        {
            if (_window == null) return;
            _window.ViewModel.ActionError = $"{title}：{error.Message}";
            ShowMainWindow();
        }

        private void ExitApp()
        {
            if (_exiting)
                return;

            if (_noteBoardWindow?.TrySave() == false)
            {
                ShowNoteBoard();
                return;
            }

            _exiting = true;
            ActionDispatcher.QuickAddRequested -= OnQuickAddRequested;
            ActionDispatcher.NoteBoardRequested -= OnNoteBoardRequested;
            _quickAddWindow?.Close();
            _noteBoardWindow?.Close();
            RefreshRenderWarmup.Cancel();
            // Close the idle device cache before asynchronous capture disposal can return to it.
            GpuRefreshDevice.ShutdownPool();

            // ForceClose, not Close: the settings window would otherwise put an unsaved-changes
            // dialog on screen, and this method cannot wait for an answer - it runs synchronously
            // and ends in Exit(). Choosing Exit from the tray is taken as meaning it.
            _settingsWindow?.ForceClose();
            _settingsWindow = null;
            _window?.ViewModel.Dispose();

            _launcherService?.Dispose();
            _launcherService = null;

            _trayIcon?.Dispose();
            _trayIcon = null;

            SingleInstance.Release();

            Exit();
        }
    }
}
