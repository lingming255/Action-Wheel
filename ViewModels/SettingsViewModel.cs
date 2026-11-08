using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Action_Wheel.Services;
using Action_Wheel.Settings;
using Microsoft.UI.Dispatching;
using InfoBarSeverity = Action_Wheel.ViewModels.StatusLevel;

namespace Action_Wheel.ViewModels
{
    /// <summary>
    /// Everything the settings window decides: the nine rows, what is derived from them, undo/redo,
    /// validation, and saving.
    /// </summary>
    /// <remarks>
    /// The window keeps only what genuinely needs a window - sizing, the title bar, flyouts anchored
    /// to a particular button, the file picker's owner HWND, and the routed-key plumbing for
    /// recording a shortcut. Those handlers collect an input and hand it straight to a method here;
    /// none of them decides anything.
    ///
    /// This lives in the app rather than in ActionWheel.Core because it exposes commands and
    /// observable editor state. The rules it enforces do not: they are all in ActionConfig, ShortcutKeys and
    /// LaunchTarget, which is why the same validation can be checked without a window.
    /// </remarks>
    public sealed partial class SettingsViewModel : ObservableObject, IDisposable
    {
        /// <summary>Row order is button order: index 0 is the centre, 1-8 go clockwise from the top.</summary>
        public ObservableCollection<ActionEditModel> Items { get; } = new();
        public ObservableCollection<string> Profiles { get; } = new();

        /// <summary>
        /// The subset of <see cref="EditableProperties"/> the ring preview actually draws.
        /// </summary>
        /// <remarks>
        /// Everything missing from this set - the launch target, its arguments, the action type -
        /// changes what a button *does*, not what it looks like, so redrawing the preview for it
        /// repaints an identical ring. That is not free: measured, one redraw is 4.0 ms, of which
        /// 1.2 ms is <c>Canvas.Children.Clear()</c> tearing down nine hosts and their composition
        /// shadow visuals, and it lands on the UI thread 220 ms after every pause in typing. The
        /// launch-target box is the longest field in the window, so it was also the one paying most.
        ///
        /// Label is in the set even though it only reaches a tooltip. Leaving it out would make the
        /// preview quietly disagree with the rows, and "the preview is wrong but only if you hover"
        /// is a worse thing to own than 4 ms.
        /// </remarks>
        private static readonly HashSet<string> PreviewProperties = new()
        {
            nameof(ActionEditModel.Tag), nameof(ActionEditModel.Label),
            nameof(ActionEditModel.Glyph), nameof(ActionEditModel.IconPath),
            nameof(ActionEditModel.IconText),
            nameof(ActionEditModel.Foreground), nameof(ActionEditModel.Background),
            nameof(ActionEditModel.IconScale), nameof(ActionEditModel.IconOffsetX),
            nameof(ActionEditModel.IconOffsetY), nameof(ActionEditModel.IconTint),
            nameof(ActionEditModel.Shadow), nameof(ActionEditModel.ShadowEnabled),
            nameof(ActionEditModel.ShadowOpacity), nameof(ActionEditModel.ShadowBlur),
            nameof(ActionEditModel.ShadowOffsetX), nameof(ActionEditModel.ShadowOffsetY),
        };

        /// <summary>Editable fields. Changes to anything else are derived state, not user edits.</summary>
        private static readonly HashSet<string> EditableProperties = new()
        {
            nameof(ActionEditModel.Tag), nameof(ActionEditModel.Label), nameof(ActionEditModel.Value),
            nameof(ActionEditModel.Arguments), nameof(ActionEditModel.Glyph),
            nameof(ActionEditModel.IconPath), nameof(ActionEditModel.IconText), nameof(ActionEditModel.Foreground),
            nameof(ActionEditModel.IconScale), nameof(ActionEditModel.IconOffsetX), nameof(ActionEditModel.IconOffsetY),
            nameof(ActionEditModel.IconTint),
            nameof(ActionEditModel.Background), nameof(ActionEditModel.KindIndex),
            nameof(ActionEditModel.Shadow),
            nameof(ActionEditModel.ShadowEnabled), nameof(ActionEditModel.ShadowOpacity),
            nameof(ActionEditModel.ShadowBlur), nameof(ActionEditModel.ShadowOffsetX), nameof(ActionEditModel.ShadowOffsetY),
            nameof(ActionEditModel.HoldKindIndex), nameof(ActionEditModel.HoldValue), nameof(ActionEditModel.HoldArguments),
        };

        private readonly DispatcherQueue _dispatcher;
        private readonly DispatcherQueueTimer _captureTimer;
        private readonly DispatcherQueueTimer _previewTimer;
        private bool _disposed;
        private int _triggerIndex;
        private readonly ProfileLibrary _profileLibrary = new();

        public SettingsViewModel()
        {
            _dispatcher = DispatcherQueue.GetForCurrentThread();

            _captureTimer = _dispatcher.CreateTimer();
            _captureTimer.Interval = TimeSpan.FromMilliseconds(600);
            _captureTimer.IsRepeating = false;
            _captureTimer.Tick += (s, e) => CaptureUndoPoint();

            _previewTimer = _dispatcher.CreateTimer();
            // Composition shadows and SVG decoding are expensive. Coalesce slider drags and rapid
            // typing into one preview frame after the user pauses instead of rebuilding at 12 FPS.
            _previewTimer.Interval = TimeSpan.FromMilliseconds(220);
            _previewTimer.IsRepeating = false;
            _previewTimer.Tick += (s, e) => RefreshRowsAndPreview(_previewRedraws, _previewAnimates);

            UndoCommand = new RelayCommand(Undo, () => _history?.CanUndo == true);
            RedoCommand = new RelayCommand(Redo, () => _history?.CanRedo == true);
            SaveCommand = new RelayCommand(Save);
            RestoreDefaultsCommand = new RelayCommand(RestoreDefaults);
            OpenConfigFileCommand = new RelayCommand(OpenConfigFile);
            PlayAnimationCommand = new RelayCommand(PlayOpeningAnimation);
            LightPresetCommand = new RelayCommand(() => ApplyAppearancePreset("#FF202020", "#FFF7F7F7", "#66000000"));
            DarkPresetCommand = new RelayCommand(() => ApplyAppearancePreset("#FFF5F5F5", "#FF242424", "#B0000000"));
            ColorfulPresetCommand = new RelayCommand(ApplyColorfulPreset);
            SaveProfileCommand = new RelayCommand(SaveNamedProfile);
            LoadProfileCommand = new RelayCommand(LoadNamedProfile, () => !string.IsNullOrWhiteSpace(SelectedProfile));
            DeleteProfileCommand = new RelayCommand(DeleteNamedProfile, () => !string.IsNullOrWhiteSpace(SelectedProfile));
            RenameProfileCommand = new RelayCommand(RenameNamedProfile, () => !string.IsNullOrWhiteSpace(SelectedProfile));

            Items.CollectionChanged += OnItemsCollectionChanged;

            LoadFromDisk();
        }

        /// <summary>
        /// Reads actions, profiles, trigger settings, ring appearance and the active profile label
        /// from disk, and marks the result as saved. Shared by the constructor and
        /// <see cref="ReloadFromDisk"/> - the same read, just with nothing yet listening for the
        /// second one to raise change notifications for.
        /// </summary>
        private void LoadFromDisk()
        {
            RefreshProfiles();
            LoadItems(ActionConfig.Load());
            _history = new UndoHistory<ActionItem>(Snapshot());

            _configPath = ActionConfig.ActiveConfigPath;
            var triggerPreferences = TriggerSettings.LoadPreferences();
            _triggerIndex = (int)triggerPreferences.Trigger;
            _chordTimeoutMs = triggerPreferences.ChordTimeoutMs;
            _movementThreshold = triggerPreferences.MovementThreshold;
            _appearance = (triggerPreferences.Appearance ?? RingAppearance.Default).Normalised();
            _activeProfileName = ActiveProfileSettings.Load();
            _loadedProfileName = _activeProfileName;

            // Last, so the baseline is everything as it came off disk. Anything after this point is
            // the user changing something.
            MarkSaved();
        }

        /// <summary>
        /// Re-reads everything <see cref="LoadFromDisk"/> does and tells every bound control about
        /// it. Used after a full backup restore: that writes actions.json, preferences.json, the
        /// profiles directory and active-profile.txt directly, none of it through this view model, so
        /// without an explicit reload the window would keep showing whatever it had in memory before
        /// the restore until closed and reopened.
        /// </summary>
        public void ReloadFromDisk()
        {
            LoadFromDisk();

            Raise(nameof(ConfigPath));
            Raise(nameof(TriggerIndex));
            Raise(nameof(ChordTimeoutMs));
            Raise(nameof(ChordTimeoutMsText));
            Raise(nameof(MovementThreshold));
            Raise(nameof(MovementThresholdText));
            Raise(nameof(ActiveProfileText));

            // Mirrors ApplyAppearance's own Raise list - everything the ring size/animation card and
            // its preview read from _appearance.
            Raise(nameof(MinOrbitRadius));
            Raise(nameof(MaxOrbitRadius));
            Raise(nameof(RingAnimationIndex));
            Raise(nameof(RingAnimationDescription));
            Raise(nameof(ButtonSize));
            Raise(nameof(ButtonSizeText));
            Raise(nameof(OrbitRadius));
            Raise(nameof(OrbitRadiusText));
            Raise(nameof(AnimationDurationMs));
            Raise(nameof(AnimationDurationMsText));
            Raise(nameof(Appearance));

            UndoCommand.RaiseCanExecuteChanged();
            RedoCommand.RaiseCanExecuteChanged();

            ScheduleRefresh(redraw: true, animate: false);
        }

        /// <summary>Raised after actions.json has been written successfully.</summary>
        public event EventHandler? ActionsSaved;

        /// <summary>
        /// Raised whenever the ring preview is out of date. Drawing it needs a Canvas, so the window
        /// does that part; deciding when it changed is this class's job. The flag asks for the
        /// opening animation to be replayed over the redrawn ring.
        /// </summary>
        public event EventHandler<bool>? PreviewInvalidated;

        public RelayCommand UndoCommand { get; }
        public RelayCommand RedoCommand { get; }
        public RelayCommand SaveCommand { get; }
        public RelayCommand RestoreDefaultsCommand { get; }
        public RelayCommand OpenConfigFileCommand { get; }

        /// <summary>Plays the chosen opening animation in the preview again.</summary>
        /// <remarks>
        /// The picker replays it once on its own, but that is a single 200 ms showing of something
        /// the user is choosing between seven of - and half of them differ only in how they move, so
        /// one look is not a comparison. This is also the only way to see Radiate or Converge at a
        /// size that was typed rather than picked, since the size boxes deliberately do not replay.
        /// </remarks>
        public RelayCommand PlayAnimationCommand { get; }
        public RelayCommand LightPresetCommand { get; }
        public RelayCommand DarkPresetCommand { get; }
        public RelayCommand ColorfulPresetCommand { get; }
        public RelayCommand SaveProfileCommand { get; }
        public RelayCommand LoadProfileCommand { get; }
        public RelayCommand DeleteProfileCommand { get; }
        public RelayCommand RenameProfileCommand { get; }
        // Always overwritten by LoadFromDisk() before the constructor returns; the initializer only
        // exists because that assignment happens in a called method, which the nullable analyzer
        // does not trace into.
        private string _configPath = string.Empty;

        /// <summary>The file a save will write to - shown in the command bar.</summary>
        public string ConfigPath
        {
            get => _configPath;
            private set => Set(ref _configPath, value);
        }

        public int TriggerIndex
        {
            get => _triggerIndex;
            set
            {
                if (Set(ref _triggerIndex, value))
                    Raise(nameof(TriggerDescription));
            }
        }

        public string TriggerDescription => (MouseTrigger)_triggerIndex switch
        {
            MouseTrigger.HoldRightPressLeft =>
                "Hold the right mouse button, then press the left button. Move to choose; release the left button to run.",
            MouseTrigger.HoldLeftPressRight =>
                "Hold the left mouse button, then press the right button. Move to choose; release the right button to run.",
            MouseTrigger.MouseButton4 => "Press Mouse Button 4 (usually Back). It is consumed only when configured as the trigger.",
            MouseTrigger.MouseButton5 => "Press Mouse Button 5 (usually Forward). It is consumed only when configured as the trigger.",
            _ => "Hold left and press right. You can also drag toward an action and release to run it; hold Ctrl+Alt+Space for Quick Info.",
        };

        private double _chordTimeoutMs = 800;
        private double _movementThreshold = 12;

        public double ChordTimeoutMs
        {
            get => _chordTimeoutMs;
            set { if (Set(ref _chordTimeoutMs, value)) Raise(nameof(ChordTimeoutMsText)); }
        }

        public double MovementThreshold
        {
            get => _movementThreshold;
            set { if (Set(ref _movementThreshold, value)) Raise(nameof(MovementThresholdText)); }
        }

        public string ChordTimeoutMsText => $"{_chordTimeoutMs:0} ms";
        public string MovementThresholdText => $"{_movementThreshold:0} px";


        #region Rows

        /// <summary>
        /// Rebuilds the rows. Always produces nine of them, even if the config file left some out -
        /// an empty row is how the user adds a button back.
        /// </summary>
        private void LoadItems(IReadOnlyList<ActionItem> actions)
        {
            bool wasSuspended = _suspendCapture;
            _suspendCapture = true;

            try
            {
                foreach (var item in Items)
                    item.PropertyChanged -= OnItemPropertyChanged;

                Items.Clear();

                var byTag = actions
                    .Where(action => action.Tag is >= 0 and <= 8)
                    .GroupBy(action => action.Tag)
                    .ToDictionary(group => group.Key, group => group.First());

                for (int tag = 0; tag <= 8; tag++)
                {
                    var source = byTag.GetValueOrDefault(tag) ?? new ActionItem { Tag = tag };
                    var functionChoices = WindowsFunctions.Choices.Concat(
                        Profiles.Select(WindowsFunctions.ProfileChoice));
                    var model = new ActionEditModel(source, functionChoices) { Tag = tag };

                    // Closures over the row, so the template can bind a parameterless command and
                    // still act on the right button.
                    model.ResetCommand = new RelayCommand(() => ResetRow(model));
                    model.ClearIconCommand = new RelayCommand(() => ClearIcon(model));
                    model.ClearIconTextCommand = new RelayCommand(() => ClearIconText(model));

                    model.PropertyChanged += OnItemPropertyChanged;
                    Items.Add(model);
                }
            }
            finally
            {
                _suspendCapture = wasSuspended;
            }

            RefreshDerivedState();
        }

        /// <summary>
        /// Reordering is what a drag ends in, so the tags have to follow the new row order -
        /// position names, default colours and the preview all read from the tag.
        /// </summary>
        private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // The ListView reorder arrives as a remove followed by an insert. Retagging on the
            // remove would renumber a list that is one item short, so wait for the queue to drain.
            _dispatcher.TryEnqueue(() =>
            {
                if (_disposed || Items.Count != 9)
                    return;

                bool changed = false;

                for (int index = 0; index < Items.Count; index++)
                {
                    if (Items[index].Tag != index)
                    {
                        Items[index].Tag = index;
                        changed = true;
                    }

                    // The centre button cannot be a group (see ActionEditModel.CanBeGroup's
                    // remarks). A row dragged into position 0 while configured as one is demoted to
                    // "Do nothing" here rather than left to fail validation only once Save is
                    // pressed - by then the reorder that caused it is long past and the message
                    // would have nothing on screen to point at.
                    if (index == 0 && Items[index].KindIndex == ActionValueCodec.GroupIndex)
                    {
                        Items[index].KindIndex = ActionValueCodec.NoneIndex;
                        changed = true;
                    }
                }

                RefreshDerivedState();

                if (changed && !_suspendCapture)
                    CaptureUndoPoint();
            });
        }

        private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == null || !EditableProperties.Contains(e.PropertyName))
                return;

            MarkDuplicateShortcuts();
            // A row's status always has to be re-checked; the ring only has to be redrawn when
            // something it draws moved.
            ScheduleRefresh(redraw: PreviewProperties.Contains(e.PropertyName));
            ScheduleUndoPoint();
        }

        /// <summary>What the pending refresh owes the user by the time the timer fires.</summary>
        /// <remarks>
        /// Fields rather than parameters carried to the tick because the edits overlap: picking an
        /// animation and then typing in a launch target before the 220 ms is up collapses into one
        /// tick, and that tick still owes both the animation and, from the earlier edit, the redraw.
        /// Both are therefore only ever set here and only ever cleared by the tick itself - an edit
        /// that needs neither must not cancel one that does.
        /// </remarks>
        private bool _previewAnimates;
        private bool _previewRedraws;

        /// <summary>
        /// Re-checks the rows once the user pauses, rather than on every keystroke, and redraws the
        /// ring with them when <paramref name="redraw"/> says the ring itself changed.
        /// </summary>
        private void ScheduleRefresh(bool redraw = true, bool animate = false)
        {
            _previewRedraws |= redraw;
            _previewAnimates |= animate;
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        /// <summary>Replays the opening animation now, without waiting for the redraw timer.</summary>
        private void PlayOpeningAnimation()
        {
            // Stopping the timer loses nothing: this does everything its tick would have, so leaving
            // it to fire would only replay the animation a second time a moment later.
            _previewTimer.Stop();
            RefreshRowsAndPreview(redraw: true, animate: true);
        }

        private void RefreshRowsAndPreview(bool redraw, bool animate)
        {
            foreach (var item in Items)
                item.RefreshStatus();

            _previewRedraws = false;
            _previewAnimates = false;

            // animate implies redraw: there is nothing to animate over a ring that was not redrawn.
            if (redraw || animate)
                PreviewInvalidated?.Invoke(this, animate);
        }

        /// <summary>Everything that is computed from the rows rather than typed into them.</summary>
        public void RefreshDerivedState()
        {
            MarkDuplicateShortcuts();
            PreviewInvalidated?.Invoke(this, false);
        }

        /// <summary>
        /// Flags shortcuts bound to more than one button. Not an error - two buttons may legitimately
        /// send the same keys - so this only shows a marker and a warning, and never blocks saving.
        /// </summary>
        private void MarkDuplicateShortcuts()
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var key in Items.Select(ShortcutKeyOf).Where(k => k != null))
                counts[key!] = counts.GetValueOrDefault(key!) + 1;

            foreach (var item in Items)
            {
                var key = ShortcutKeyOf(item);
                item.IsDuplicateShortcut = key != null && counts[key] > 1;
            }
        }

        /// <summary>Normalised shortcut text, or null when the row does not send one.</summary>
        private static string? ShortcutKeyOf(ActionEditModel item)
        {
            if (item.KindIndex != ActionValueCodec.KeysIndex || string.IsNullOrWhiteSpace(item.Value))
                return null;

            return item.Value.Replace(" ", string.Empty);
        }

        private void ResetRow(ActionEditModel model)
        {
            var fallback = ActionConfig.Defaults().FirstOrDefault(a => a.Tag == model.Tag)
                ?? new ActionItem { Tag = model.Tag };

            model.CopyFrom(fallback);

            RefreshDerivedState();
            CaptureUndoPoint();

            ShowStatus(InfoBarSeverity.Informational, $"{model.PositionName} reset",
                "Press Save to write it to the file.");
        }

        private void ClearIcon(ActionEditModel model) => model.IconPath = string.Empty;

        private void ClearIconText(ActionEditModel model) => model.IconText = string.Empty;

        #endregion

        #region Edits driven from the view

        // Each of these is the tail end of something the window had to do itself - show a flyout on
        // a particular button, parent a file picker to an HWND, watch a routed key event. The window
        // gathers the input; what it means happens here.

        public void SetGlyph(ActionEditModel model, string code) => model.Glyph = code;

        public void SetIconPath(ActionEditModel model, string path)
        {
            model.IconPath = path;

            ShowStatus(InfoBarSeverity.Informational, "Icon updated",
                model.CanTintIcon
                    ? "Tint is on, so the icon follows the icon colour. Turn it off to keep the colours in the file."
                    : "This icon is drawn with its own colours. Only an .svg can follow the icon colour.");
        }

        /// <summary>
        /// Points the button at <paramref name="path"/> and takes that program's icon with it.
        /// </summary>
        /// <remarks>
        /// The icon comes along because picking a program is the moment the button stops being
        /// generic: keeping the previous glyph would leave a ring of identical squares that all say
        /// "document". A failure to read the icon is not a failure to set the target, so the value is
        /// assigned first and the icon is best-effort on top of it.
        /// </remarks>
        public async Task SetLaunchTargetAsync(ActionEditModel model, string path)
        {
            model.Value = path;
            await UseAppIconAsync(model, path);
        }

        /// <summary>
        /// Replaces the button's icon with the one Windows shows for <paramref name="target"/>.
        /// </summary>
        /// <remarks>
        /// Reported through the status bar rather than silently skipped: picking a program and
        /// getting the old icon back with no explanation reads as the feature being broken, when the
        /// real answer is usually that the target has no icon of its own.
        /// </remarks>
        public async Task UseAppIconAsync(ActionEditModel model, string target)
        {
            var (success, iconPath, error) = await AppIconExtractor.TryExtractAsync(target);

            // The shell work is asynchronous now. If the user selected another target while it
            // was running, the old result must not overwrite the icon of the new application.
            if (!string.Equals(model.Value.Trim().Trim('"'), target.Trim().Trim('"'),
                    StringComparison.OrdinalIgnoreCase))
                return;

            if (!success)
            {
                ShowStatus(InfoBarSeverity.Warning, "Could not use the app icon", error);
                return;
            }

            model.IconPath = iconPath;
            CaptureUndoPoint();

            ShowStatus(InfoBarSeverity.Success, "App icon applied",
                $"{model.PositionName} now uses the icon of {System.IO.Path.GetFileName(target.Trim().Trim('"'))}.");
        }

        public void SetColor(ActionEditModel model, bool isForeground, string hex)
        {
            if (isForeground)
                model.Foreground = hex;
            else
                model.Background = hex;
        }

        public void SetShadowColor(ActionEditModel model, string hex) => model.Shadow = hex;

        /// <summary>
        /// Back to the built-in colour. Stored as empty rather than the default's current hex value,
        /// so the button follows those colours if they ever change instead of being pinned to
        /// today's.
        /// </summary>
        public void ResetColor(ActionEditModel model, bool isForeground) =>
            SetColor(model, isForeground, string.Empty);

        public void ResetShadowColor(ActionEditModel model) => model.Shadow = string.Empty;

        private void ApplyAppearancePreset(string foreground, string background, string shadow)
        {
            foreach (var item in Items)
            {
                item.Foreground = foreground;
                item.Background = background;
                item.Shadow = shadow;
            }

            CaptureUndoPoint();
            ShowStatus(InfoBarSeverity.Informational, "Appearance preset applied",
                "The preset was applied to all buttons. Press Save to keep it, or Undo to restore the previous style.");
        }

        private void ApplyColorfulPreset()
        {
            string[] colours = { "#FF5B8DEF", "#FF8B5CF6", "#FFEC4899", "#FFF97316", "#FFEAB308", "#FF22C55E", "#FF14B8A6", "#FF06B6D4", "#FF3B82F6" };
            for (int i = 0; i < Items.Count; i++)
            {
                Items[i].Foreground = "#FFFFFFFF";
                Items[i].Background = colours[i % colours.Length];
                Items[i].Shadow = "#99000000";
            }

            CaptureUndoPoint();
            ShowStatus(InfoBarSeverity.Informational, "Colourful preset applied",
                "The preset was applied to all buttons. Press Save to keep it, or Undo to restore it.");
        }

        public void ApplyRecordedShortcut(ActionEditModel model, string shortcut)
        {
            model.Value = shortcut;
            ShowStatus(InfoBarSeverity.Success, "Shortcut recorded", shortcut);
        }

        #endregion

        #region Undo / redo

        // Snapshots of all nine rows. ActionItem is immutable, so a snapshot is just a list of
        // references and costs nothing to keep.
        private UndoHistory<ActionItem>? _history;
        private bool _suspendCapture;

        private List<ActionItem> Snapshot() => Items.Select(i => i.ToActionItem()).ToList();

        #region Unsaved changes

        /// <summary>
        /// Everything a Save would write, as it stood the last time one succeeded.
        /// </summary>
        /// <remarks>
        /// A snapshot rather than a boolean flag. A flag has to be set from every setter that can
        /// change anything, which means it is wrong the first time somebody adds a setting and
        /// forgets - and being wrong in the "no changes" direction is the expensive one, because it
        /// silently throws the user's work away. Comparing the state instead cannot drift: anything
        /// that is part of a save is part of the comparison by construction.
        /// </remarks>
        private List<ActionItem> _savedActions = new();

        private RingAppearance _savedAppearance = RingAppearance.Default;
        private int _savedTriggerIndex;
        private double _savedChordTimeoutMs;
        private double _savedMovementThreshold;

        /// <summary>True when closing now would lose something the user typed.</summary>
        public bool HasUnsavedChanges =>
            _appearance != _savedAppearance
            || _triggerIndex != _savedTriggerIndex
            || _chordTimeoutMs != _savedChordTimeoutMs
            || _movementThreshold != _savedMovementThreshold
            // ActionItem is a record, so this is a value comparison over all eighteen fields
            // rather than a reference one. That is the whole reason it was made a record.
            || !Snapshot().SequenceEqual(_savedActions);

        /// <summary>Marks the current state as the saved one. Called at load and after every save.</summary>
        private void MarkSaved()
        {
            _savedActions = Snapshot();
            _savedAppearance = _appearance;
            _savedTriggerIndex = _triggerIndex;
            _savedChordTimeoutMs = _chordTimeoutMs;
            _savedMovementThreshold = _movementThreshold;
        }

        #endregion

        /// <summary>
        /// Coalesces rapid edits. Without it, typing a label would push one undo entry per keystroke
        /// and undo would be useless.
        /// </summary>
        private void ScheduleUndoPoint()
        {
            if (_suspendCapture)
                return;

            _captureTimer.Stop();
            _captureTimer.Start();
        }

        private void CaptureUndoPoint()
        {
            _captureTimer.Stop();

            if (_suspendCapture)
                return;

            var current = Snapshot();
            if (_history == null || !_history.Capture(current))
                return;

            RaiseUndoRedoState();
        }

        private void Undo()
        {
            // Anything still sitting in the debounce timer is part of what the user wants undone.
            CaptureUndoPoint();

            if (_history == null || !_history.TryUndo(out var snapshot))
                return;
            Apply(snapshot);
        }

        private void Redo()
        {
            if (_history == null || !_history.TryRedo(out var snapshot))
                return;
            Apply(snapshot);
        }

        private void Apply(IReadOnlyList<ActionItem> snapshot)
        {
            _suspendCapture = true;

            try
            {
                LoadItems(snapshot);
            }
            finally
            {
                _suspendCapture = false;
            }

            RaiseUndoRedoState();
        }

        private void RaiseUndoRedoState()
        {
            UndoCommand.RaiseCanExecuteChanged();
            RedoCommand.RaiseCanExecuteChanged();
        }

        #endregion

        #region Saving

        /// <summary>
        /// Saves and reports whether it worked, for the close-confirmation dialog.
        /// </summary>
        /// <remarks>
        /// The answer is read back off <see cref="HasUnsavedChanges"/> rather than plumbed out of
        /// Save itself, which has seven separate failure exits. Each one already puts its own reason
        /// on the status bar and returns without calling MarkSaved, so "is there still anything
        /// unsaved" is the same question as "did the save go through", and asking it this way cannot
        /// miss an exit that gets added later.
        /// </remarks>
        public bool TrySave()
        {
            Save();
            return !HasUnsavedChanges;
        }

        private void Save()
        {
            var problems = Validate();
            if (problems.Count > 0)
            {
                ShowStatus(InfoBarSeverity.Error, "Not saved", string.Join("  •  ", problems));
                return;
            }

            var actions = Items.Select(i => i.ToActionItem()).ToList();

            string triggerError = string.Empty;
            if (!Enum.IsDefined(typeof(MouseTrigger), _triggerIndex)
                || !TriggerSettings.Save(new TriggerPreferences
                {
                    Trigger = (MouseTrigger)_triggerIndex,
                    ChordTimeoutMs = (int)_chordTimeoutMs,
                    MovementThreshold = (int)_movementThreshold,
                    Appearance = _appearance,
                }, out triggerError))
            {
                // preferences.json now carries the ring's size and animation as well as the trigger,
                // so the heading can no longer name only the trigger.
                ShowStatus(InfoBarSeverity.Error, "Could not save the trigger and ring preferences",
                    string.IsNullOrEmpty(triggerError) ? "The selected trigger is invalid." : triggerError);
                return;
            }

            bool hasActiveSavedProfile = Profiles.Any(name =>
                string.Equals(name, _activeProfileName, StringComparison.OrdinalIgnoreCase));
            if (hasActiveSavedProfile
                && !_profileLibrary.TrySave(_activeProfileName, actions, out string profileError))
            {
                ShowStatus(InfoBarSeverity.Error, "Could not update the active profile", profileError);
                return;
            }

            if (!ActionConfig.Save(actions, out string error))
            {
                ShowStatus(InfoBarSeverity.Error, "Could not write the configuration file", error);
                return;
            }

            bool profileLabelSaved = ActiveProfileSettings.Save(_activeProfileName, out string profileLabelError);
            if (profileLabelSaved)
            {
                Raise(nameof(ActiveProfileText));
            }

            ConfigPath = ActionConfig.ActiveConfigPath;

            // Both files are on disk by now. Every earlier failure above returns instead of falling
            // through, so reaching this line is the only definition of "saved" there is.
            MarkSaved();
            ActionsSaved?.Invoke(this, EventArgs.Empty);

            var warnings = Warnings();
            if (!profileLabelSaved)
                warnings.Add($"The profile label could not be saved: {profileLabelError}");
            if (warnings.Count > 0)
            {
                ShowStatus(InfoBarSeverity.Warning, "Saved, with something worth checking",
                    string.Join("  •  ", warnings));
                return;
            }

            ShowStatus(InfoBarSeverity.Success, "Saved",
                hasActiveSavedProfile
                    ? $"The active profile '{_activeProfileName}' and the radial menu were updated."
                    : "The main configuration and radial menu were updated.");
        }

        /// <summary>
        /// Catches the mistakes that would otherwise fail silently at click time: a typo in a
        /// shortcut, an empty target, or an icon code the font cannot render.
        /// </summary>
        private List<string> Validate()
            => ActionsValidator.Validate(Snapshot()).Select(issue => issue.Message).ToList();

        /// <summary>
        /// Things that are probably mistakes but are still legal, so they are reported after the
        /// save rather than instead of it.
        /// </summary>
        private List<string> Warnings()
        {
            var warnings = new List<string>();

            var duplicates = Items
                .Where(i => i.IsDuplicateShortcut)
                .Select(i => i.Value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var shortcut in duplicates)
                warnings.Add($"\"{shortcut}\" is bound to more than one button");

            foreach (var item in Items.Where(i => i.KindIndex == ActionValueCodec.LaunchIndex))
            {
                if (LaunchTarget.Check(item.Value) == LaunchTargetStatus.Unresolved)
                    warnings.Add($"{item.PositionName}: \"{item.Value}\" was not found on this machine");
            }

            // Reported rather than blocked: a button with no picture still works, and refusing the
            // save would strand anyone whose configuration predates the current icon font.
            foreach (var item in Items.Where(i => !i.HasValidGlyph))
                warnings.Add($"{item.PositionName}: icon code \"{item.Glyph}\" is not in the icon font, so that button shows no picture");

            return warnings;
        }

        private void RestoreDefaults()
        {
            LoadItems(ActionConfig.Defaults());
            SetLoadedProfile("Built-in defaults");
            CaptureUndoPoint();

            // The ring's size and animation are defaults too, and they were being left behind: a
            // user who had made the buttons huge got the default nine actions back inside a ring
            // that was still nothing like the default one.
            ApplyAppearance(RingAppearance.Default, nameof(Appearance));

            // Undo is a stack of ActionItem snapshots and does not carry the appearance, so the
            // message must not promise that it does.
            ShowStatus(InfoBarSeverity.Informational, "Defaults loaded",
                "The rows, the ring size and the animation are all back to their defaults. "
                + "Press Save to overwrite the current file; Undo restores the rows only.");
        }

        /// <summary>
        /// Runs one row's action right now, so the user can check a shortcut or a path without
        /// saving and opening the ring. Window-management functions are refused: Settings is the
        /// foreground window while this runs, so "minimise the active window" would act on the very
        /// window showing the button.
        /// </summary>
        public void TestAction(ActionEditModel model)
        {
            var action = model.ToActionItem();

            if (action.Kind == ActionKind.None)
            {
                ShowStatus(InfoBarSeverity.Informational, "Nothing to test", "This button only closes the menu.");
                return;
            }

            if (action.Kind == ActionKind.Function
                && action.Value.StartsWith("window.", StringComparison.OrdinalIgnoreCase))
            {
                ShowStatus(InfoBarSeverity.Warning, "Window action not tested",
                    "Open the radial menu over the target application so the command cannot affect Settings itself.");
                return;
            }

            // Off the UI thread because Execute can block on SendInput and on shell activation.
            // The catch matters: this is fire-and-forget, and an exception on a thread-pool thread
            // with nobody observing the task takes the process down.
            _ = Task.Run(() =>
            {
                try
                {
                    if (action.Kind == ActionKind.CopyText)
                        _dispatcher.TryEnqueue(() => ActionDispatcher.Execute(action));
                    else
                        ActionDispatcher.Execute(action);
                }
                catch (Exception)
                {
                }
            });

            ShowStatus(InfoBarSeverity.Informational, "Test action started", action.Label);
        }

        private void OpenConfigFile()
        {
            // Load() writes the defaults out when nothing exists yet, so there is always a file.
            ActionConfig.Load();

            if (!ShellCommands.OpenPath(ActionConfig.ActiveConfigPath, out string error))
                ShowStatus(InfoBarSeverity.Error, "Could not open the configuration file", error);
        }

        /// <summary>Loads an XML profile into the editor without overwriting the active config.</summary>
        public void ImportProfile(string path, string? profileName = null)
        {
            CaptureUndoPoint();

            if (!_profileLibrary.TryImport(path, out var actions, out string error))
            {
                ShowStatus(InfoBarSeverity.Error, "Could not import the profile", error);
                return;
            }

            ApplyImportedProfile(actions, profileName ?? ProfileLibrary.NameFromPath(path));
        }

        private void ApplyImportedProfile(IReadOnlyList<ActionItem> actions, string profileName)
        {
            LoadItems(actions);
            SetLoadedProfile(profileName);
            CaptureUndoPoint();
            ShowStatus(InfoBarSeverity.Success, "Profile imported",
                "Review the imported buttons, then press Save to apply them. You can also Undo this import.");
        }

        private void SetLoadedProfile(string name)
        {
            _loadedProfileName = string.IsNullOrWhiteSpace(name) ? "Main configuration" : name;
        }

        /// <summary>Exports the current editor state, including changes that have not been saved.</summary>
        /// <returns>False if the profile was not written; the reason is already on the status bar.</returns>
        public bool ExportProfile(string path)
        {
            var problems = Validate();
            if (problems.Count > 0)
            {
                ShowStatus(InfoBarSeverity.Error, "Could not export the profile", string.Join("  •  ", problems));
                return false;
            }

            if (!_profileLibrary.TryExport(path, Snapshot(), out string error))
            {
                ShowStatus(InfoBarSeverity.Error, "Could not export the profile", error);
                return false;
            }

            ShowStatus(InfoBarSeverity.Success, "Profile exported", path);
            return true;
        }

        #endregion

        #region Status bar

        private bool _isStatusOpen;
        private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;
        private string _statusTitle = string.Empty;
        private string _statusMessage = string.Empty;

        /// <summary>Two-way: the bar has a close button, and closing it must stick.</summary>
        public bool IsStatusOpen
        {
            get => _isStatusOpen;
            set => Set(ref _isStatusOpen, value);
        }

        public InfoBarSeverity StatusSeverity
        {
            get => _statusSeverity;
            private set => Set(ref _statusSeverity, value);
        }

        public string StatusTitle
        {
            get => _statusTitle;
            private set => Set(ref _statusTitle, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set => Set(ref _statusMessage, value);
        }

        public void ShowStatus(InfoBarSeverity severity, string title, string message)
        {
            StatusSeverity = severity;
            StatusTitle = title;
            StatusMessage = message;
            IsStatusOpen = true;
        }

        #endregion

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            // The timers hold a reference to this object and would otherwise keep firing into a
            // window that has already closed - the preview one straight into a Canvas that is gone.
            _captureTimer.Stop();
            _previewTimer.Stop();
            Items.CollectionChanged -= OnItemsCollectionChanged;

            foreach (var item in Items)
                item.PropertyChanged -= OnItemPropertyChanged;
        }
    }
}
