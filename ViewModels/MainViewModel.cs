using System;
using Action_Wheel.Services;
using Microsoft.UI.Xaml;
using InfoBarSeverity = Action_Wheel.ViewModels.StatusLevel;

namespace Action_Wheel.ViewModels
{
    /// <summary>
    /// Everything the main window shows and does. The window itself is left with nothing but its
    /// own sizing and the title bar: the state on show here - whether the hooks are installed,
    /// whether actions.json was usable, whether the startup entry exists - all belongs to services
    /// that outlive the window, and putting it in the code-behind meant reaching back into them
    /// from event handlers.
    /// </summary>
    public sealed class MainViewModel : ObservableObject, IDisposable
    {
        private readonly LauncherService? _launcher;
        private bool _disposed;

        public MainViewModel(LauncherService? launcher)
        {
            _launcher = launcher;

            OpenSettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
            CopyDiagnosticsCommand = new RelayCommand(CopyDiagnostics);
            RestoreBackupCommand = new RelayCommand(RestoreBackup, () => CanRestoreBackup);
            OpenUpdateCommand = new RelayCommand(OpenUpdate, () => HasUpdateAvailable);

            if (_launcher != null)
            {
                _launcher.ConfigurationChanged += OnConfigurationChanged;
                _launcher.StatusChanged += OnStatusChanged;
                _launcher.ActionFailed += OnActionFailed;
            }

            RefreshStartup();
            RefreshConfiguration();
        }

        /// <summary>Raised when the user asks for the settings window; App owns that window.</summary>
        public event EventHandler? SettingsRequested;

        public RelayCommand OpenSettingsCommand { get; }
        public RelayCommand CopyDiagnosticsCommand { get; }
        public RelayCommand RestoreBackupCommand { get; }
        public RelayCommand OpenUpdateCommand { get; }

        /// <summary>The product version embedded in Action Wheel.exe, formatted for display.</summary>
        public string VersionText
        {
            get
            {
                var version = typeof(MainViewModel).Assembly.GetName().Version;
                return version == null ? string.Empty : $"v{version.Major}.{version.Minor}.{version.Build}";
            }
        }

        private void OnConfigurationChanged(object? sender, EventArgs e) => RefreshConfiguration();
        private void OnStatusChanged(object? sender, EventArgs e) => RefreshHookStatus();

        private void OnActionFailed(object? sender, string message) => ActionError = message;
        public string QuickInfoStatus => _launcher?.QuickInfoStatus ?? "Quick Info unavailable";
        public string ActiveProfileText => $"Active profile: {_launcher?.ActiveProfileLabel ?? ActiveProfileSettings.Load()}";

        #region Hook status

        /// <summary>
        /// The service's view of itself, re-read rather than cached: the window can be shown long
        /// after startup, and a null launcher means the service could not even be constructed.
        /// </summary>
        private LauncherStatus Status => _launcher?.Status ?? LauncherStatus.NotStarted;

        public InfoBarSeverity HookSeverity =>
            Status.IsRunning ? InfoBarSeverity.Success : InfoBarSeverity.Error;

        public string HookTitle => Status.IsRunning
            ? "Running in the background"
            : "The global hooks are not installed";

        public string HookMessage
        {
            get
            {
                if (Status.IsRunning)
                    return "The service has started. Hold left and press right, or use Ctrl+Alt+Space for Quick Info.";

                var reason = string.IsNullOrWhiteSpace(Status.Error)
                    ? "The service did not start."
                    : Status.Error;

                // Both hooks are always in the same state - Start rolls back a half-installed pair -
                // so the message can speak about the feature rather than about the two hooks.
                return $"{reason} The radial menu will not open. This is usually security software "
                    + "blocking SetWindowsHookEx.";
            }
        }

        public void RefreshHookStatus() => Raise(nameof(HookSeverity), nameof(HookTitle), nameof(HookMessage));

        #endregion

        #region Configuration status

        private ConfigLoadResult Config => _launcher?.Configuration ?? ActionConfig.LoadDetailed();

        /// <summary>
        /// Only shown when something is wrong. A configuration that loaded is not news, and an
        /// InfoBar that is always on screen is one nobody reads when it finally says something.
        /// </summary>
        public Visibility ConfigProblemVisibility =>
            Config.Status == ConfigStatus.Rejected ? Visibility.Visible : Visibility.Collapsed;

        public string ConfigProblemMessage
        {
            get
            {
                var config = Config;
                if (config.Status != ConfigStatus.Rejected)
                    return string.Empty;

                return $"{config.Path} could not be used ({config.Error}), so the built-in defaults "
                    + "are in effect. Your file has been left exactly as it is - fix it by hand, or "
                    + (config.BackupAvailable
                        ? "restore the copy saved before the last change."
                        : "open the settings window and save to write a fresh one.");
            }
        }

        public bool CanRestoreBackup => Config.Status == ConfigStatus.Rejected && Config.BackupAvailable;

        public Visibility RestoreBackupVisibility =>
            CanRestoreBackup ? Visibility.Visible : Visibility.Collapsed;

        public void RefreshConfiguration()
        {
            Raise(nameof(ConfigProblemVisibility), nameof(ConfigProblemMessage),
                  nameof(CanRestoreBackup), nameof(RestoreBackupVisibility), nameof(ActiveProfileText), nameof(QuickInfoStatus));
            RestoreBackupCommand.RaiseCanExecuteChanged();
        }

        private void RestoreBackup()
        {
            if (_launcher == null)
                return;

            if (_launcher.RestoreConfigBackup(out string error))
            {
                ActionError = string.Empty;
                RefreshConfiguration();
                return;
            }

            ActionError = $"The backup could not be restored: {error}";
        }

        #endregion

        #region Start with Windows

        private bool _startWithWindows;

        /// <summary>
        /// Bound two-way to the toggle. The setter writes to the registry and, if that is refused,
        /// puts the switch back where it was rather than leaving it showing a state nobody applied.
        /// </summary>
        public bool StartWithWindows
        {
            get => _startWithWindows;
            set
            {
                if (_startWithWindows == value)
                    return;

                if (_suppressStartupWrite)
                {
                    Set(ref _startWithWindows, value);
                    return;
                }

                if (StartupManager.SetEnabled(value, out string error))
                {
                    StartupError = string.Empty;
                    Set(ref _startWithWindows, value);
                    return;
                }

                StartupError = error;

                // Re-read the registry into the field directly, then raise unconditionally. Going
                // back through the property - as this used to - is silent: the write failed, so the
                // registry still holds the old value, the setter's own "no change" guard returns
                // early, and PropertyChanged never fires. The ToggleSwitch then sits where the user
                // just dragged it, showing a state nobody applied. The bare Raise is what makes the
                // binding re-read and snap the switch back.
                _startWithWindows = StartupManager.IsEnabled;
                Raise(nameof(StartWithWindows));
            }
        }

        private bool _suppressStartupWrite;

        private string _startupError = string.Empty;

        public string StartupError
        {
            get => _startupError;
            private set
            {
                if (Set(ref _startupError, value))
                    Raise(nameof(HasStartupError));
            }
        }

        public bool HasStartupError => _startupError.Length > 0;

        /// <summary>
        /// Brings the toggle in line with the registry. Called whenever the window is shown, not
        /// just once: Windows' own Startup Apps page - and this app's tray menu - can change the
        /// entry while the window sits hidden in the tray.
        /// </summary>
        public void RefreshStartup()
        {
            // Guard the setter: assigning the property is what writes to the registry, and a read
            // that writes the value straight back is not a read.
            _suppressStartupWrite = true;
            StartWithWindows = StartupManager.IsEnabled;
            _suppressStartupWrite = false;
        }

        #endregion

        #region Update check

        private UpdateInfo? _availableUpdate;

        /// <summary>
        /// Set once, from <see cref="Action_Wheel.App"/>'s background check after launch - there is
        /// no re-check while the window is open, so this only ever moves from false to true.
        /// </summary>
        public void ApplyUpdateCheck(UpdateInfo update)
        {
            _availableUpdate = update;
            Raise(nameof(HasUpdateAvailable), nameof(UpdateTitle), nameof(UpdateMessage));
            OpenUpdateCommand.RaiseCanExecuteChanged();
        }

        public bool HasUpdateAvailable => _availableUpdate != null;

        public string UpdateTitle => _availableUpdate == null
            ? string.Empty
            : $"Version {_availableUpdate.Version} is available";

        public string UpdateMessage => "You're running an older release. Open the GitHub release page to download it.";

        private void OpenUpdate()
        {
            if (_availableUpdate == null)
                return;

            ShellCommands.OpenPath(_availableUpdate.ReleaseUrl, out _);
        }

        #endregion

        #region Errors and diagnostics

        private string _actionError = string.Empty;

        /// <summary>The most recent failure worth showing. Empty hides the bar.</summary>
        public string ActionError
        {
            get => _actionError;
            set
            {
                if (Set(ref _actionError, value))
                    Raise(nameof(HasActionError));
            }
        }

        public bool HasActionError => _actionError.Length > 0;

        private void CopyDiagnostics()
        {
            var report = new DiagnosticsReport()
                .AddEnvironment()
                .Add("Mouse hook", Status.MouseHookInstalled)
                .Add("Keyboard hook", Status.KeyboardHookInstalled)
                .Add("Hook error", Status.Error)
                .Add("Start with Windows", StartupManager.IsEnabled)
                .Add("Quick Info", QuickInfoStatus)
                .Build();

            if (ShellCommands.CopyText(report, out string error))
            {
                ActionError = string.Empty;
                return;
            }

            ActionError = $"The diagnostics could not be copied: {error}";
        }

        #endregion

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            if (_launcher != null)
            {
                _launcher.ConfigurationChanged -= OnConfigurationChanged;
                _launcher.StatusChanged -= OnStatusChanged;
                _launcher.ActionFailed -= OnActionFailed;
            }

            GC.SuppressFinalize(this);
        }
    }
}
