using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Action_Wheel.Overlay;
using Microsoft.UI.Dispatching;
using Windows.Graphics;

namespace Action_Wheel.Services
{
    /// <summary>
    /// Service that manages the lifecycle of the radial menu overlay.
    /// Coordinates between the mouse hook and menu window creation.
    /// </summary>
    public class LauncherService : IDisposable
    {
        private readonly MouseHook _mouseHook;
        private readonly KeyboardHook _keyboardHook;
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly ConfigWatcher _configWatcher;
        private readonly DispatcherQueueTimer _hookHealthTimer;
        private RadialMenu? _currentMenu;
        private IReadOnlyList<ActionItem> _actions;
        private IReadOnlyList<ActionItem> _quickInfoActions = Array.Empty<ActionItem>();
        private int _quickInfoPage;
        public string QuickInfoStatus { get; private set; } = string.Empty;

        private void ReloadQuickInfo()
        {
            _quickInfoActions = QuickInfoCatalog.Load(out string error, page: _quickInfoPage);
            QuickInfoStatus = $"Quick Info: {_quickInfoActions.Count - 1} sectors\n{QuickInfoCatalog.FilePath}\n"
                + (System.IO.File.Exists(QuickInfoCatalog.FilePath) ? "Catalog" : "Folder discovery")
                + (error.Length > 0 ? $"\n{error}" : string.Empty);
        }

        // Drag gesture origins are in physical screen pixels. Selection belongs to the overlay,
        // which hit-tests every movement, including moves within the same main-ring sector.
        private PointInt32 _gestureOrigin;
        private bool _gestureActive;
        private PointInt32 _keyboardGestureOrigin;
        private bool _keyboardGestureActive;
        private IntPtr _targetWindow;

        private PointInt32? _lastHealthCursor;
        private long _lastHealthMouseCallback;

        /// <summary>Whether the global hooks are installed. Drives the status shown on the main window.</summary>
        public LauncherStatus Status { get; private set; } = LauncherStatus.NotStarted;

        /// <summary>How the configuration currently in effect was arrived at.</summary>
        public ConfigLoadResult Configuration { get; private set; }

        /// <summary>Raised after <see cref="Configuration"/> changes, on the UI thread.</summary>
        public event EventHandler? ConfigurationChanged;
        public event EventHandler? StatusChanged;
        public string ActiveProfileLabel { get; private set; } = "Main configuration";

        /// <summary>
        /// Raised when an action could not be carried out, on the UI thread. Forwarded from
        /// <see cref="ActionDispatcher"/> so the windows only ever have to know about this service.
        /// </summary>
        public event EventHandler<string>? ActionFailed;

        /// <summary>Raised on the UI thread when a menu button requests Action Settings.</summary>
        public event EventHandler? SettingsRequested;

        public LauncherService()
        {
            Configuration = ActionConfig.LoadDetailed();
            _actions = Configuration.Actions;
            QuickInfoCatalog.EnsureDirectory();
            ReloadQuickInfo();
            ActiveProfileLabel = ActiveProfileSettings.Load();

            _mouseHook = new MouseHook();
            ApplyTriggerSettings();
            _mouseHook.MiddleClick += OnMiddleClick;
            _mouseHook.AnyButtonDown += OnAnyButtonDown;
            _mouseHook.MouseMoved += OnMouseMoved;
            _mouseHook.MiddleUp += OnMiddleUp;
            _mouseHook.GestureCanceled += OnGestureCanceled;

            _keyboardHook = new KeyboardHook { EscapePressed = OnEscapePressed };
            _keyboardHook.EmergencyTriggerPressed = OnEmergencyTrigger;
            _keyboardHook.EmergencyTriggerReleased = OnEmergencyTriggerReleased;

            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _hookHealthTimer = _dispatcherQueue.CreateTimer();
            _hookHealthTimer.Interval = TimeSpan.FromSeconds(5);
            _hookHealthTimer.IsRepeating = true;
            _hookHealthTimer.Tick += CheckHookHealth;

            ActionDispatcher.ActionFailed += OnActionFailed;
            ActionDispatcher.SettingsRequested += OnSettingsRequested;
            ActionDispatcher.ReloadRequested += OnReloadRequested;
            ActionDispatcher.ProfileRequested += OnProfileRequested;

            // Picks up edits made with a text editor. The watcher fires on a thread-pool thread.
            _configWatcher = new ConfigWatcher();
            _configWatcher.Changed += (s, e) => _dispatcherQueue.TryEnqueue(ReloadActions);
        }

        /// <summary>
        /// Installs the global hooks and returns what happened. Never throws: a hook that will not
        /// install is a condition to report, not a reason to fail startup with no window to say so.
        /// </summary>
        public LauncherStatus Start()
        {
            string error = string.Empty;
            bool mouse = TryStart(_mouseHook.Start, "mouse", ref error);
            bool keyboard = TryStart(_keyboardHook.Start, "keyboard", ref error);

            // All or nothing. One hook alone is a trap: with only the mouse hook the menu opens but
            // Esc silently will not close it, and with only the keyboard hook nothing opens at all
            // while Esc is quietly swallowed from every other application on the system.
            if (mouse != keyboard)
            {
                _mouseHook.Stop();
                _keyboardHook.Stop();
                mouse = keyboard = false;
            }

            Status = new LauncherStatus(mouse, keyboard, error);
            if (Status.IsRunning)
            {
                _lastHealthMouseCallback = _mouseHook.LastCallbackTick;
                _hookHealthTimer.Start();
            }
            StatusChanged?.Invoke(this, EventArgs.Empty);

            return Status;
        }

        private static bool TryStart(Action start, string which, ref string error)
        {
            try
            {
                start();
                return true;
            }
            catch (Exception ex)
            {
                // Keep the first failure. It is the one that explains the second when both fail for
                // the same underlying reason.
                if (error.Length == 0)
                    error = $"The {which} hook could not be installed: {ex.Message}";

                return false;
            }
        }

        /// <summary>
        /// Stops the launcher service.
        /// </summary>
        public void Stop()
        {
            _gestureActive = false;
            _keyboardGestureActive = false;
            _mouseHook.TrackMovement = false;
            _mouseHook.Stop();
            _keyboardHook.Stop();
            _hookHealthTimer.Stop();
            Status = LauncherStatus.NotStarted;
            StatusChanged?.Invoke(this, EventArgs.Empty);
            CloseCurrentMenu();
        }

        private void CheckHookHealth(DispatcherQueueTimer sender, object args)
        {
            if (!Status.IsRunning || !MouseHook.TryGetCursorPosition(out var cursor))
                return;

            long callback = _mouseHook.LastCallbackTick;
            bool cursorMoved = _lastHealthCursor is PointInt32 previous
                && (previous.X != cursor.X || previous.Y != cursor.Y);
            bool callbackAdvanced = callback != _lastHealthMouseCallback;
            _lastHealthCursor = cursor;
            _lastHealthMouseCallback = callback;

            // Windows exposes no query for an LL hook's installed state. Cursor movement is a safe
            // probe: a live mouse hook must observe it. If Windows removed the hook after a timeout,
            // the cursor moves while LastCallbackTick stays unchanged. Reinstall both hooks because
            // they share the same UI-thread timeout exposure.
            // The reason - cursor input the mouse hook did not observe - is not recorded anywhere.
            // It used to go to the log, which this project no longer has, and ReinstallHooks kept a
            // parameter for it long after there was anything to do with it. What the user can see is
            // Status, which only changes if the reinstall itself fails.
            if (cursorMoved && !callbackAdvanced)
                ReinstallHooks();
        }

        private void ReinstallHooks()
        {
            _mouseHook.Stop();
            _keyboardHook.Stop();

            string error = string.Empty;
            bool mouse = TryStart(_mouseHook.Start, "mouse", ref error);
            bool keyboard = TryStart(_keyboardHook.Start, "keyboard", ref error);
            if (mouse != keyboard)
            {
                _mouseHook.Stop();
                _keyboardHook.Stop();
                mouse = keyboard = false;
            }

            Status = new LauncherStatus(mouse, keyboard, error);
            _lastHealthMouseCallback = _mouseHook.LastCallbackTick;
            StatusChanged?.Invoke(this, EventArgs.Empty);
            if (!Status.IsRunning)
                _hookHealthTimer.Stop();
        }

        /// <summary>Re-reads actions.json. The next menu that opens picks up the changes.</summary>
        public void ReloadActions()
        {
            ApplyTriggerSettings();
            Configuration = ActionConfig.LoadDetailed();
            _actions = Configuration.Actions;
            ReloadQuickInfo();
            ActiveProfileLabel = ActiveProfileSettings.Load();
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Called around this app's own saves, so the watcher does not report them back as an
        /// external edit.
        /// </summary>
        public void SuppressConfigWatcher() => _configWatcher.SuppressFor(TimeSpan.FromSeconds(2));

        /// <summary>Puts the .bak back and reloads. Returns false with the reason on failure.</summary>
        public bool RestoreConfigBackup(out string error)
        {
            if (!ActionConfig.RestoreBackup(Configuration.Path, out error))
                return false;

            SuppressConfigWatcher();
            ReloadActions();
            return true;
        }

        private void OnActionFailed(object? sender, string message) =>
            _dispatcherQueue.TryEnqueue(() => ActionFailed?.Invoke(this, message));

        private void OnSettingsRequested(object? sender, EventArgs e) =>
            _dispatcherQueue.TryEnqueue(() => SettingsRequested?.Invoke(this, EventArgs.Empty));

        private void OnReloadRequested(object? sender, EventArgs e) =>
            _dispatcherQueue.TryEnqueue(ReloadActions);

        private void OnProfileRequested(object? sender, string profileName) =>
            _dispatcherQueue.TryEnqueue(() => SwitchProfile(profileName));

        /// <summary>
        /// Switches to a saved profile by name, the same way a ring button's "switch profile"
        /// function does. The tray icon's Profiles submenu calls this directly rather than going
        /// through <see cref="ActionDispatcher.ProfileRequested"/>: that event exists for dispatching
        /// an <see cref="ActionItem"/>, and a tray menu click is not one - there is only a name the
        /// user picked.
        /// </summary>
        public void RequestProfileSwitch(string profileName) =>
            _dispatcherQueue.TryEnqueue(() => SwitchProfile(profileName));

        private void SwitchProfile(string profileName)
        {
            var library = new ProfileLibrary();
            if (string.IsNullOrWhiteSpace(profileName))
            {
                if (!library.TryList(out var profileNames, out string listError))
                {
                    ReportProfileFailure("next profile", listError);
                    return;
                }

                if (profileNames.Count == 0)
                {
                    ReportProfileFailure("next profile", "No saved profiles are available. Create one in Button Settings first.");
                    return;
                }

                int currentIndex = -1;
                for (int i = 0; i < profileNames.Count; i++)
                {
                    if (string.Equals(profileNames[i], ActiveProfileLabel, StringComparison.OrdinalIgnoreCase))
                    {
                        currentIndex = i;
                        break;
                    }
                }
                profileName = profileNames[(currentIndex + 1) % profileNames.Count];
            }

            if (!library.TryLoad(profileName, out var actions, out string loadError))
            {
                ReportProfileFailure(profileName, loadError);
                return;
            }

            SuppressConfigWatcher();
            if (!ActionConfig.Save(actions, out string saveError))
            {
                ReportProfileFailure(profileName, saveError);
                return;
            }

            if (!ActiveProfileSettings.Save(profileName, out string labelError))
            {
                ReportProfileFailure(profileName, $"The actions were loaded, but the active profile label could not be saved: {labelError}");
                ReloadActions();
                return;
            }

            library.TryTouch(profileName);
            ReloadActions();
        }

        private void ReportProfileFailure(string profileName, string error)
        {
            string message = $"Could not switch to profile '{profileName}': {error}";
            ActionFailed?.Invoke(this, message);
        }

        /// <summary>
        /// Called when middle mouse button is clicked.
        /// Creates and positions the radial menu at the cursor location.
        /// </summary>
        private void OnMiddleClick(object? sender, PointInt32 position)
        {
            // Arm the drag gesture. Movement tracking stays off outside this window so the hook does
            // no work on ordinary mouse movement. Only the mouse path arms it, because only the
            // mouse path produces the button-up that disarms it again - see OnEmergencyTrigger.
            _gestureOrigin = position;
            _gestureActive = true;
            _mouseHook.TrackMovement = true;

            OpenMenuAt(position);
        }

        /// <summary>
        /// Shows the ring centred on <paramref name="position"/>. Carries no gesture state, so it is
        /// safe to call from the keyboard trigger, which has no button release to close the gesture
        /// with.
        /// </summary>
        private void OpenMenuAt(PointInt32 position)
            => OpenMenuAt(position, _actions);

        private void OpenMenuAt(PointInt32 position, IReadOnlyList<ActionItem> actions, bool quickInfo = false)
        {
            _targetWindow = ActionDispatcher.CaptureTargetWindow();

            // Closing and opening both have to happen in the same UI-thread turn. Closing the old
            // menu right here (on the hook's callback) would touch the window from off the UI thread.
            _dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    // Close any existing menu first
                    CloseCurrentMenu();

                    // Read after the hook returns. Each opening sees the latest Markdown snapshot.
                    if (quickInfo)
                    {
                        ReloadQuickInfo();
                        actions = _quickInfoActions;
                        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
                    }

                    var menu = new RadialMenu(actions, _appearance);
                    _currentMenu = menu;

                    // Only clear the reference if it still points at this exact menu. The old menu's
                    // Closed event arrives after the new one has already been assigned, so a
                    // hardcoded "_currentMenu = null" here would wipe out the new menu and it would
                    // never close on the next middle-click.
                    menu.Closed += (s, e) =>
                    {
                        if (ReferenceEquals(_currentMenu, menu))
                            _currentMenu = null;
                        // Every way out of a gesture closes its menu, so this is the one place a
                        // cancel, Esc, a fired hold or an action can never leave the screen grey.
                        if (ReferenceEquals(_gradedMenu, menu))
                            EndGrade();
                    };

                    menu.ActionInvoked += OnActionInvoked;

                    // Position the menu centered at cursor
                    menu.ShowAtPosition(position);
                    BeginGrade(menu, position);
                }
                catch (Exception ex)
                {
                    ActionFailed?.Invoke(this, $"The radial menu could not be shown: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// While the middle button is still held, highlights whichever button the cursor is pointing
        /// away towards. This is the feedback half of the drag gesture.
        /// </summary>
        private void OnMouseMoved(object? sender, PointInt32 position)
        {
            if (_gestureActive)
            {
                var mouseTag = RingGeometry.TagForDirection(
                    position.X - _gestureOrigin.X,
                    position.Y - _gestureOrigin.Y);

                _dispatcherQueue.TryEnqueue(() =>
                {
                    // Radius and child choice can change without crossing a main-ring sector.
                    _currentMenu?.UpdateGesture(position, mouseTag, allowHold: true);
                });
                return;
            }

            if (!_keyboardGestureActive)
                return;

            var keyboardTag = RingGeometry.TagForDirection(
                position.X - _keyboardGestureOrigin.X,
                position.Y - _keyboardGestureOrigin.Y);

            _dispatcherQueue.TryEnqueue(() =>
            {
                _currentMenu?.UpdateGesture(position, keyboardTag, allowHold: false);
            });
        }

        /// <summary>
        /// Completes the drag gesture. Released past the dead zone, the highlighted button runs;
        /// released in the centre plays the brief visual refresh.
        /// </summary>
        private void OnMiddleUp(object? sender, PointInt32 position)
        {
            if (!_gestureActive)
                return;

            _gestureActive = false;
            _mouseHook.TrackMovement = false;

            var tag = RingGeometry.TagForDirection(
                position.X - _gestureOrigin.X,
                position.Y - _gestureOrigin.Y);

            _dispatcherQueue.TryEnqueue(() =>
            {
                // Ends whatever countdown ShowHoldProgress started, regardless of whether a tag was
                // resolved - released too close to the centre to choose a direction still ends it.
                // If the hold already fired on its own (RadialMenu.FireHold runs preemptively, while
                // still pressed - see its remarks), the menu and _currentMenu are already gone by the
                // time this runs, and both calls below are then safe no-ops.
                _currentMenu?.HideHoldProgress();
                if (_currentMenu?.TryInvokeGroupChildAt(position) == true)
                    return;
                if (tag != null)
                    _currentMenu?.InvokeTag(tag.Value);
                else
                    PlayRefresh();
            });
        }

        private void OnGestureCanceled(object? sender, EventArgs e)
        {
            _gestureActive = false;
            _mouseHook.TrackMovement = false;
            _dispatcherQueue.TryEnqueue(CloseCurrentMenu);
        }

        /// <summary>
        /// Dismisses the menu when the user clicks anywhere outside of it. The overlay is
        /// WS_EX_NOACTIVATE and never gets focus, so it receives no deactivation event of its own -
        /// without this, a click past the window edge would leave the menu hanging on screen until
        /// the next middle click.
        /// </summary>
        private void OnAnyButtonDown(object? sender, PointInt32 position)
        {
            var menu = _currentMenu;
            if (menu == null || menu.ContainsScreenPoint(position))
                return;

            _gestureActive = false;
            _keyboardGestureActive = false;
            _mouseHook.TrackMovement = false;
            _dispatcherQueue.TryEnqueue(CloseCurrentMenu);
        }

        /// <summary>
        /// Returns true if Escape actually dismissed a menu, so the hook can swallow the key.
        /// </summary>
        private bool OnEscapePressed()
        {
            if (_currentMenu == null)
                return false;

            _gestureActive = false;
            _keyboardGestureActive = false;
            _mouseHook.TrackMovement = false;
            _dispatcherQueue.TryEnqueue(CloseCurrentMenu);
            return true;
        }

        private async void OnActionInvoked(object? sender, ActionItem action)
        {
            if (action.Kind == ActionKind.Function && action.Value == QuickInfoCatalog.NextPage)
            {
                _quickInfoPage++;
                if (MouseHook.TryGetCursorPosition(out var cursor))
                {
                    _keyboardGestureOrigin = cursor;
                    OpenMenuAt(cursor, _quickInfoActions, quickInfo: true);
                }
                return;
            }
            // The menu has already closed itself, but the window teardown is still in flight.
            // Give composition/focus a short, deterministic window to settle. Dispatcher priority
            // alone is not enough: teardown can continue in the compositor after the UI queue is
            // empty, causing occasional shortcuts to land while the overlay is disappearing.
            await Task.Delay(40).ConfigureAwait(false);
            if (action.Kind == ActionKind.CopyText)
                _dispatcherQueue.TryEnqueue(() => ActionDispatcher.Execute(action));
            else
                ActionDispatcher.Execute(action, _targetWindow);
        }

        /// <summary>
        /// Ring size and opening animation, re-read whenever the trigger settings are.
        /// </summary>
        /// <remarks>
        /// Cached rather than read in OpenMenuAt: that path runs from the mouse hook, on the input
        /// path of every application on the system, and reading preferences.json there would put a
        /// file open on it. Both callers of ApplyTriggerSettings already cover the two moments this
        /// can change - startup and a save from the settings window.
        /// </remarks>
        private RingAppearance _appearance = RingAppearance.Default;

        private void ApplyTriggerSettings()
        {
            var preferences = TriggerSettings.LoadPreferences();
            _mouseHook.Trigger = preferences.Trigger;
            _mouseHook.ChordTimeoutMs = preferences.ChordTimeoutMs;
            _mouseHook.ChordMovementThreshold = preferences.MovementThreshold;
            _appearance = preferences.Appearance ?? RingAppearance.Default;
        }

        /// <summary>
        /// Ctrl+Alt+Space opens the information wheel as a hold-to-open gesture. The resources are
        /// already the expanded layer, so the pointer only has to travel to the desired item.
        /// </summary>
        /// <remarks>
        /// Returns true only when the chord actually did something, so Ctrl+Alt+Space still reaches
        /// the foreground app if the wheel is already occupied or the cursor cannot be read.
        /// </remarks>
        private bool OnEmergencyTrigger()
        {
            if (_currentMenu != null || _gestureActive || _keyboardGestureActive)
                return false;

            if (!MouseHook.TryGetCursorPosition(out var position))
            {
                return false;
            }

            _quickInfoPage = 0;
            _keyboardGestureOrigin = position;
            _keyboardGestureActive = true;
            _mouseHook.TrackMovement = true;
            OpenMenuAt(position, _quickInfoActions, quickInfo: true);
            return true;
        }

        private bool OnEmergencyTriggerReleased()
        {
            if (!_keyboardGestureActive)
                return false;

            _keyboardGestureActive = false;
            if (!_gestureActive)
                _mouseHook.TrackMovement = false;

            var position = MouseHook.TryGetCursorPosition(out var current)
                ? current : _keyboardGestureOrigin;
            var tag = RingGeometry.TagForDirection(
                position.X - _keyboardGestureOrigin.X,
                position.Y - _keyboardGestureOrigin.Y);

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (_currentMenu?.TryInvokeGroupChildAt(position) == true)
                    return;
                if (tag is int selected && selected < _quickInfoActions.Count
                    && _quickInfoActions[selected].Kind != ActionKind.Group)
                    _currentMenu?.InvokeTag(selected);
                else if (tag == null)
                    PlayRefresh();
                else
                    CloseCurrentMenu();
            });

            return true;
        }

        /// <summary>The menu whose gesture currently holds the screen grey, if any.</summary>
        private RadialMenu? _gradedMenu;
        private Task<RefreshFrame?>? _refreshFrame;

        /// <summary>
        /// Starts the hold half of the refresh: capture the monitor for the mosaic, then grey the
        /// screen once the capture has the colour it needs. UI thread.
        /// </summary>
        private void BeginGrade(RadialMenu menu, PointInt32 position)
        {
            _gradedMenu = menu;
            _refreshFrame = RefreshFrame.CaptureAsync(position, () => _dispatcherQueue.TryEnqueue(() =>
            {
                // The gesture may already have ended while the capture ran.
                if (ReferenceEquals(_gradedMenu, menu))
                    ScreenGrade.Lower();
            }));
        }

        /// <summary>Restores colour and hands back the gesture's mosaic. UI thread.</summary>
        private Task<RefreshFrame?>? EndGrade()
        {
            var frame = _refreshFrame;
            _gradedMenu = null;
            _refreshFrame = null;
            ScreenGrade.Restore();
            return frame;
        }

        /// <summary>
        /// A centre release starts the click-centred block wave. Capture and learned
        /// depth usually prepare during the hold; a faster release waits for them.
        /// </summary>
        /// <remarks>
        /// The screen stays grey until the menu has its grey still of the monitor on screen and
        /// calls back. The colour matrix can only change the whole desktop at once; the cover
        /// handoff hides that transition before block playback begins.
        /// If the menu closes first, its Closed handler restores colour instead.
        /// </remarks>
        private async void PlayRefresh()
        {
            var menu = _currentMenu;
            if (menu == null)
                return;

            var pending = ReferenceEquals(_gradedMenu, menu) ? _refreshFrame : null;
            RefreshFrame? frame = pending == null ? null : await pending;
            menu.PlayRefresh(frame, () =>
            {
                if (ReferenceEquals(_gradedMenu, menu))
                    EndGrade();
            });
        }

        /// <summary>
        /// Closes the currently open menu if one exists.
        /// </summary>
        /// <remarks>
        /// The field is cleared before the CloseMenu() call, not after. If CloseMenu() throws - WinUI
        /// window teardown races, or a COMException on a window whose HWND is already gone -
        /// clearing it afterwards would leave _currentMenu pointing at a dead window forever. That
        /// is not a cosmetic leak: OnEscapePressed would then answer "yes, I dismissed a menu" to
        /// every Escape from then on, and the keyboard hook would swallow Escape in every
        /// application on the machine.
        /// </remarks>
        private void CloseCurrentMenu()
        {
            var menu = _currentMenu;
            if (menu == null)
                return;

            _currentMenu = null;

            try
            {
                menu.CloseMenu();
            }
            catch (Exception ex)
            {
                ActionFailed?.Invoke(this, $"The radial menu could not be closed cleanly: {ex.Message}");
            }
        }

        public void Dispose()
        {
            // ActionDispatcher.ActionFailed is static; leaving this subscribed would keep the whole
            // service - and the window it reports into - alive past shutdown.
            ActionDispatcher.ActionFailed -= OnActionFailed;
            ActionDispatcher.SettingsRequested -= OnSettingsRequested;
            ActionDispatcher.ReloadRequested -= OnReloadRequested;
            ActionDispatcher.ProfileRequested -= OnProfileRequested;

            Stop();
            _configWatcher.Dispose();
            _hookHealthTimer.Stop();
            _mouseHook.Dispose();
            _keyboardHook.Dispose();
            ScreenGrade.Shutdown();
            GC.SuppressFinalize(this);
        }
    }
}
