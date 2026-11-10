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
            _window.Activate();

            SetUpTrayIcon();

            // Exercise the complete CPU renderer offscreen while the app is idle. A gesture
            // started immediately after launch awaits this same one-shot task before capturing.
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

        private void ExitApp()
        {
            if (_exiting)
                return;

            _exiting = true;
            RefreshRenderWarmup.Cancel();

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
