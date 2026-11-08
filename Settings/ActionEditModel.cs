using System;
using System.Windows.Input;
using System.Collections.ObjectModel;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using Action_Wheel.Services;
using Action_Wheel.ViewModels;
using Color = Windows.UI.Color;

namespace Action_Wheel.Settings
{
    /// <summary>
    /// One editable row of the settings window: an <see cref="ActionItem"/> plus the extra state the
    /// UI needs (live icon preview, which fields are relevant, whether the target actually resolves).
    /// </summary>
    /// <remarks>
    /// <see cref="ActionItem"/> itself is immutable (init-only) on purpose, so it cannot be edited
    /// in place; this is the mutable, observable counterpart used only while the window is open.
    /// </remarks>
    public sealed class ActionEditModel : ObservableObject
    {
        private int _tag;
        private string _label;
        private string _value;
        private string _arguments;
        private string _glyph;
        private string _iconPath;
        private string _iconText;
        private double _iconScale;
        private double _iconOffsetX;
        private double _iconOffsetY;
        private bool _iconTint;
        private string _foreground;
        private string _background;
        private string _shadow;
        private bool _shadowEnabled;
        private double _shadowOpacity;
        private double _shadowBlur;
        private double _shadowOffsetX;
        private double _shadowOffsetY;
        private int _kindIndex;
        private int _holdKindIndex;
        private string _holdValue = string.Empty;
        private string _holdArguments = string.Empty;
        private IReadOnlyList<ActionItem> _groupChildren = Array.Empty<ActionItem>();
        private bool _isDuplicateShortcut;
        private string _functionQuery = string.Empty;
        private LaunchTargetStatus _status;

        private IReadOnlyList<WindowsFunction> _functionChoices;
        public ObservableCollection<WindowsFunction> FunctionSuggestions { get; }

        public ActionEditModel(ActionItem source, IEnumerable<WindowsFunction>? functionChoices = null)
        {
            _functionChoices = (functionChoices ?? WindowsFunctions.Choices).ToArray();
            FunctionSuggestions = new ObservableCollection<WindowsFunction>(_functionChoices);
            _tag = source.Tag;
            _label = source.Label;
            _value = source.Value;
            _arguments = source.Arguments;
            _glyph = source.Glyph;
            _iconPath = source.IconPath;
            _iconText = source.IconText;
            _iconScale = Math.Round(source.IconScale, 2, MidpointRounding.AwayFromZero);
            _iconOffsetX = Math.Round(source.IconOffsetX, 2, MidpointRounding.AwayFromZero);
            _iconOffsetY = Math.Round(source.IconOffsetY, 2, MidpointRounding.AwayFromZero);
            _iconTint = source.IconTint;
            _foreground = source.Foreground;
            _background = source.Background;
            _shadow = source.Shadow;
            _shadowEnabled = source.ShadowEnabled;
            _shadowOpacity = Math.Round(source.ShadowOpacity, 2, MidpointRounding.AwayFromZero);
            _shadowBlur = Math.Round(source.ShadowBlur, 1, MidpointRounding.AwayFromZero);
            _shadowOffsetX = Math.Round(source.ShadowOffsetX, 1, MidpointRounding.AwayFromZero);
            _shadowOffsetY = Math.Round(source.ShadowOffsetY, 1, MidpointRounding.AwayFromZero);
            _kindIndex = ActionValueCodec.KindToIndex(source.Kind);
            _holdKindIndex = ActionValueCodec.KindToIndex(source.HoldKind);
            _holdValue = source.HoldValue;
            _holdArguments = source.HoldArguments;
            _groupChildren = source.GroupChildren;
            _status = ComputeStatus();
        }

        /// <summary>
        /// Restores this one button to its built-in default. Assigned by
        /// <see cref="ViewModels.SettingsViewModel"/> when the row is created: the row can describe
        /// the command, but only the window's view model knows how to record it for undo.
        /// </summary>
        public ICommand? ResetCommand { get; internal set; }

        /// <summary>Drops the custom SVG and goes back to the glyph icon.</summary>
        public ICommand? ClearIconCommand { get; internal set; }

        /// <summary>Drops the custom text and goes back to the glyph icon.</summary>
        public ICommand? ClearIconTextCommand { get; internal set; }

        public void RefreshFunctionChoices(IEnumerable<WindowsFunction> choices)
        {
            _functionChoices = choices.ToArray();
            FunctionSuggestions.Clear();
            foreach (var function in _functionChoices.Where(function =>
                string.IsNullOrWhiteSpace(_functionQuery)
                || function.Name.Contains(_functionQuery, StringComparison.OrdinalIgnoreCase)))
            {
                FunctionSuggestions.Add(function);
            }

            _functionQuery = _functionChoices.FirstOrDefault(item =>
                string.Equals(item.Id, _value, StringComparison.OrdinalIgnoreCase))?.Name ?? _functionQuery;
            Raise(nameof(FunctionQuery));
            Raise(nameof(HoldFunctionChoices));
        }

        public void ShowAllFunctionChoices()
        {
            FunctionSuggestions.Clear();
            foreach (var function in _functionChoices)
                FunctionSuggestions.Add(function);
        }

        /// <summary>
        /// 0 = the centre button, 1-8 = the outer buttons clockwise from the top. Settable because
        /// drag-and-drop reordering assigns tags from the new row order.
        /// </summary>
        public int Tag
        {
            get => _tag;
            set
            {
                if (!Set(ref _tag, value))
                    return;

                Raise(nameof(PositionName));
                Raise(nameof(TagCaption));
                Raise(nameof(CanBeGroup));
                RaiseIconChanged();
            }
        }

        /// <summary>
        /// Whether this row is allowed to be a <see cref="ActionKind.Group"/> button - every position
        /// except the centre. Drives the "Group" combo box item's IsEnabled, so it reads as
        /// unavailable there rather than silently doing nothing if picked.
        /// </summary>
        /// <remarks>
        /// A dragged reorder that moves a group into position 0 demotes it back to "Do nothing" -
        /// see SettingsViewModel.OnItemsCollectionChanged - and ActionsValidator rejects a tag-0
        /// group outright for a file that set one up some other way (hand-edited, or a profile
        /// imported from a version that allowed it). Three checks for the same rule because each
        /// guards a different way the rule could otherwise be bypassed: a live pick in this window,
        /// a live reorder in this window, and a file this window never touched at all.
        /// </remarks>
        public bool CanBeGroup => _tag != 0;

        /// <summary>Where this button sits on the ring, in the user's terms.</summary>
        public string PositionName => _tag switch
        {
            0 => "Centre",
            1 => "Top",
            2 => "Top right",
            3 => "Right",
            4 => "Bottom right",
            5 => "Bottom",
            6 => "Bottom left",
            7 => "Left",
            8 => "Top left",
            _ => $"Button {_tag}",
        };

        public string TagCaption => $"tag {_tag}";

        public string Label
        {
            get => _label;
            set => Set(ref _label, value);
        }

        public string Value
        {
            get => _value;
            set
            {
                if (Set(ref _value, value))
                {
                    if (_kindIndex != 2)
                        RefreshStatus();
                    if (IsFunction)
                    {
                        _functionQuery = _functionChoices.FirstOrDefault(
                            item => string.Equals(item.Id, _value, StringComparison.OrdinalIgnoreCase))?.Name ?? _value;
                        Raise(nameof(FunctionQuery));
                    }
                }
            }
        }

        public string Arguments
        {
            get => _arguments;
            set => Set(ref _arguments, value);
        }

        public string Glyph
        {
            get => _glyph;
            set
            {
                if (Set(ref _glyph, value))
                    RaiseIconChanged();
            }
        }

        /// <summary>Path to an icon file; when set it replaces the glyph.</summary>
        public string IconPath
        {
            get => _iconPath;
            set
            {
                if (!Set(ref _iconPath, value))
                    return;

                Raise(nameof(HasIconFile));
                Raise(nameof(CanTintIcon));
                RaiseIconChanged();
            }
        }

        public bool HasIconFile => !string.IsNullOrWhiteSpace(_iconPath);

        /// <summary>
        /// Short text (a letter, an emoji, a couple of characters) drawn instead of the glyph. An
        /// icon file set on the same row still wins - see <see cref="ActionItem.IconText"/>.
        /// </summary>
        public string IconText
        {
            get => _iconText;
            set
            {
                if (!Set(ref _iconText, value))
                    return;

                Raise(nameof(HasIconText));
                RaiseIconChanged();
            }
        }

        public bool HasIconText => !string.IsNullOrWhiteSpace(_iconText);

        /// <summary>
        /// Only a vector icon can be recoloured, so the toggle is dead weight on any other file and
        /// says so by being disabled rather than by quietly doing nothing.
        /// </summary>
        public bool CanTintIcon => IconFile.IsVector(_iconPath);

        public bool IconTint { get => _iconTint; set { if (Set(ref _iconTint, value)) RaiseIconChanged(); } }

        public double IconScale
        {
            get => _iconScale;
            set
            {
                // NumberBox reports NaN (and temporarily out-of-range values such as 0) while the
                // user replaces its text. Ignore those intermediate editor states instead of
                // pushing the old value back into the control, which would interrupt manual input.
                if (!double.IsFinite(value) || value is < 0.25 or > 3)
                    return;

                // Slider arithmetic can leave binary floating-point tails. Store the same
                // precision presented by the exact editor so its default formatter stays compact.
                double normalised = Math.Round(value, 2, MidpointRounding.AwayFromZero);

                if (Set(ref _iconScale, normalised))
                {
                    RaiseIconChanged();
                    Raise(nameof(IconScaleText));
                }
            }
        }
        public string IconScaleText => _iconScale.ToString("0.##", CultureInfo.CurrentCulture);

        public double IconOffsetX
        {
            get => _iconOffsetX;
            set
            {
                double normalised = Normalise(value, -50, 50, 2, _iconOffsetX);
                if (Set(ref _iconOffsetX, normalised))
                {
                    RaiseIconChanged();
                    Raise(nameof(IconOffsetXText));
                }
            }
        }

        public double IconOffsetY
        {
            get => _iconOffsetY;
            set
            {
                double normalised = Normalise(value, -50, 50, 2, _iconOffsetY);
                if (Set(ref _iconOffsetY, normalised))
                {
                    RaiseIconChanged();
                    Raise(nameof(IconOffsetYText));
                }
            }
        }

        public string IconOffsetXText => _iconOffsetX.ToString("0.##", CultureInfo.CurrentCulture);
        public string IconOffsetYText => _iconOffsetY.ToString("0.##", CultureInfo.CurrentCulture);

        public string Foreground
        {
            get => _foreground;
            set
            {
                if (Set(ref _foreground, value))
                    RaiseIconChanged();
            }
        }

        public string Background
        {
            get => _background;
            set
            {
                if (Set(ref _background, value))
                    RaiseIconChanged();
            }
        }

        public string Shadow
        {
            get => _shadow;
            set
            {
                if (Set(ref _shadow, value))
                    RaiseIconChanged();
            }
        }

        // ShadowEnabled toggles whether a shadow is drawn at all in the big canvas preview, which
        // SettingsViewModel.PreviewProperties already listens for directly by property name - but it
        // does not change what IconFactory.CreateIcon draws for this row's own small icon swatch, so
        // unlike the properties above it must not call RaiseIconChanged.
        public bool ShadowEnabled { get => _shadowEnabled; set => Set(ref _shadowEnabled, value); }

        // None of the four below feed IconFactory.CreateIcon (only ShadowColor - driven by the
        // Shadow hex string, not these - would), so RaiseIconChanged has nothing to do here. It used
        // to be called anyway, which meant every tick of these NumberBoxes rebuilt this row's icon
        // element - a disk stat (File.GetLastWriteTimeUtc) on every tick for a file-based icon - for
        // an update whose result was always identical to what was already on screen. Plain Set still
        // raises the property itself, which is all PreviewProperties needs for the big canvas redraw.
        public double ShadowOpacity
        {
            get => _shadowOpacity;
            set { if (SetNormalised(ref _shadowOpacity, value, 0, 1, 2)) Raise(nameof(ShadowOpacityText)); }
        }
        public double ShadowBlur
        {
            get => _shadowBlur;
            set { if (SetNormalised(ref _shadowBlur, value, 0, 64, 1)) Raise(nameof(ShadowBlurText)); }
        }
        public double ShadowOffsetX
        {
            get => _shadowOffsetX;
            set { if (SetNormalised(ref _shadowOffsetX, value, -50, 50, 1)) Raise(nameof(ShadowOffsetXText)); }
        }
        public double ShadowOffsetY
        {
            get => _shadowOffsetY;
            set { if (SetNormalised(ref _shadowOffsetY, value, -50, 50, 1)) Raise(nameof(ShadowOffsetYText)); }
        }

        public string ShadowOpacityText => _shadowOpacity.ToString("0.##", CultureInfo.CurrentCulture);
        public string ShadowBlurText => _shadowBlur.ToString("0.#", CultureInfo.CurrentCulture);
        public string ShadowOffsetXText => _shadowOffsetX.ToString("0.#", CultureInfo.CurrentCulture);
        public string ShadowOffsetYText => _shadowOffsetY.ToString("0.#", CultureInfo.CurrentCulture);

        /// <summary>Stores a rounded, in-range value and keeps the editor showing what was stored.</summary>
        /// <remarks>
        /// The second Raise is the part that is easy to leave out, and the one the NumberBoxes need.
        /// A NumberBox reports NaN the moment its text is empty; that is rejected here, so the field
        /// keeps its old value, so <see cref="ObservableObject.Set"/> sees no change and raises
        /// nothing - and the box then stays blank for the rest of the session while the model
        /// quietly holds the old number and saves it without a word. Raising the property when the
        /// input was refused is what makes the control read the kept value back.
        /// </remarks>
        private bool SetNormalised(ref double field, double value, double minimum, double maximum, int decimals,
            [CallerMemberName] string? propertyName = null)
        {
            double normalised = Normalise(value, minimum, maximum, decimals, field);
            if (Set(ref field, normalised, propertyName))
                return true;

            // Set compared against the coerced value, so a refused edit is indistinguishable from
            // "the user typed what was already there". Only the original value tells them apart -
            // and NaN differs from every number, including itself, so an emptied box lands here.
            if (value != normalised)
                Raise(propertyName);

            return false;
        }

        private static double Normalise(double value, double minimum, double maximum, int decimals, double fallback) =>
            double.IsFinite(value) && value >= minimum && value <= maximum
                ? Math.Round(value, decimals, MidpointRounding.AwayFromZero)
                : fallback;

        /// <summary>Index into the type combo box: 0 = none, 1 = keys, 2 = launch.</summary>
        public int KindIndex
        {
            get => _kindIndex;
            set
            {
                if (!Set(ref _kindIndex, value))
                    return;

                Raise(nameof(IsValueEnabled));
                Raise(nameof(CanBrowse));
                Raise(nameof(CanRecord));
                Raise(nameof(ValuePlaceholder));
                Raise(nameof(IsFunction));
                Raise(nameof(IsGroup));
                RefreshStatus();
            }
        }

        /// <summary>"Do nothing" and "Group" both need no value, so the box is greyed out rather than misleading.</summary>
        public bool IsValueEnabled => _kindIndex != ActionValueCodec.NoneIndex && _kindIndex != ActionValueCodec.GroupIndex;

        /// <summary>Only a launch target can be picked from disk, and take arguments.</summary>
        public bool CanBrowse => _kindIndex == 2;

        /// <summary>Recording a key press only makes sense for a shortcut action.</summary>
        public bool CanRecord => _kindIndex == 1;

        public bool IsFunction => _kindIndex == 3;

        /// <summary>Whether this button reveals a satellite ring instead of dispatching anything.</summary>
        public bool IsGroup => _kindIndex == ActionValueCodec.GroupIndex;

        /// <summary>
        /// The buttons a Group action reveals. A plain settable list rather than an
        /// ObservableCollection: nothing binds to its individual entries live - the whole thing is
        /// replaced at once when "Edit group…" is saved (see SettingsWindow.xaml.cs's
        /// EditGroup_Click), the same all-at-once approach <see cref="CopyFrom"/> already uses for
        /// every other field.
        /// </summary>
        public IReadOnlyList<ActionItem> GroupChildren
        {
            get => _groupChildren;
            set
            {
                if (Set(ref _groupChildren, value))
                    Raise(nameof(EditGroupText));
            }
        }

        public string EditGroupText => $"Edit group ({_groupChildren.Count}/{ActionItem.MaxGroupChildren})…";

        /// <summary>Index into the hold-action combo box. 0 (no hold action) is the default.</summary>
        public int HoldKindIndex
        {
            get => _holdKindIndex;
            set
            {
                if (!Set(ref _holdKindIndex, value))
                    return;

                Raise(nameof(IsHoldValueEnabled));
                Raise(nameof(CanBrowseHold));
                Raise(nameof(HoldValuePlaceholder));
                Raise(nameof(IsHoldFunction));
            }
        }

        public bool IsHoldValueEnabled => _holdKindIndex != 0;
        public bool CanBrowseHold => _holdKindIndex == 2;
        public bool IsHoldFunction => _holdKindIndex == 3;

        public string HoldValuePlaceholder => _holdKindIndex switch
        {
            1 => "For example: Ctrl+Shift+S",
            2 => "For example: notepad.exe or https://…",
            3 => "Choose a built-in function",
            5 => "Text to copy to the clipboard",
            _ => "No hold action",
        };

        public string HoldValue { get => _holdValue; set => Set(ref _holdValue, value); }
        public string HoldArguments { get => _holdArguments; set => Set(ref _holdArguments, value); }

        /// <summary>
        /// The complete function list (built-ins plus profiles), for the hold action's own picker.
        /// Deliberately not <see cref="FunctionSuggestions"/> - that collection is filtered live by
        /// the primary action's search box, and sharing it would make the hold picker's contents
        /// depend on what someone happened to be typing in an unrelated box.
        /// </summary>
        public IReadOnlyList<WindowsFunction> HoldFunctionChoices => _functionChoices;

        public string FunctionQuery
        {
            get
            {
                if (string.IsNullOrEmpty(_functionQuery))
                    _functionQuery = _functionChoices.FirstOrDefault(item => item.Id == _value)?.Name ?? string.Empty;
                return _functionQuery;
            }
            set
            {
                if (!Set(ref _functionQuery, value))
                    return;

                FunctionSuggestions.Clear();
                foreach (var function in _functionChoices.Where(function =>
                    string.IsNullOrWhiteSpace(value) || function.Name.Contains(value, StringComparison.OrdinalIgnoreCase)))
                {
                    FunctionSuggestions.Add(function);
                }
            }
        }

        public string ValuePlaceholder => _kindIndex switch
        {
            1 => "For example: Ctrl+Shift+S",
            2 => "For example: notepad.exe or https://…",
            3 => "Choose a built-in function",
            4 => "Use \"Edit group\" below to configure",
            5 => "Text to copy to the clipboard",
            _ => "This button only closes the menu",
        };

        /// <summary>Set by the window when another row uses the same shortcut.</summary>
        public bool IsDuplicateShortcut
        {
            get => _isDuplicateShortcut;
            set
            {
                if (Set(ref _isDuplicateShortcut, value))
                    RefreshStatus();
            }
        }

        #region Live status of the value

        /// <summary>What the row's status indicator says about the current value.</summary>
        private LaunchTargetStatus ComputeStatus() => _kindIndex switch
        {
            1 when _isDuplicateShortcut => LaunchTargetStatus.Duplicate,
            1 when !string.IsNullOrWhiteSpace(_value) && !ShortcutKeys.IsValid(_value)
                => LaunchTargetStatus.Invalid,
            2 => LaunchTarget.Check(_value),
            _ => LaunchTargetStatus.None,
        };

        public bool HasStatus => _status != LaunchTargetStatus.None;

        // Code points of the shipped icon font, not the system's. They render as blank boxes in a
        // plain text editor - the trailing comment on each line is what identifies them.
        public string StatusGlyph => _status switch
        {
            LaunchTargetStatus.Resolved => "",                          // Tick
            LaunchTargetStatus.Duplicate or LaunchTargetStatus.Unresolved => "", // Warning
            _ => "",                                                    // Error
        };

        public string StatusTooltip => _status switch
        {
            LaunchTargetStatus.Resolved => "This target was found.",
            LaunchTargetStatus.Unresolved => "Nothing found at this path, and it is not a URL or a program on PATH.",
            LaunchTargetStatus.Duplicate => "Another button is bound to this same shortcut.",
            LaunchTargetStatus.Invalid => "This shortcut contains a key name the app cannot send.",
            _ => string.Empty,
        };

        public Color StatusColor => _status switch
        {
            LaunchTargetStatus.Resolved => Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50),
            LaunchTargetStatus.Duplicate or LaunchTargetStatus.Unresolved
                => Color.FromArgb(0xFF, 0xE8, 0xA3, 0x3D),
            _ => Color.FromArgb(0xFF, 0xE0, 0x5A, 0x5A),
        };

        #endregion

        /// <summary>The colours actually in effect, falling back to the built-in ones.</summary>
        public Color ForegroundColor => IconFactory.ColorOr(_foreground, IconFactory.DefaultForeground(_tag));

        public Color BackgroundColor => IconFactory.ColorOr(_background, IconFactory.DefaultBackground(_tag));

        public Color ShadowColor => IconFactory.ColorOr(_shadow, Color.FromArgb(255, 0, 0, 0));

        /// <summary>An SVG replaces the glyph entirely, so the glyph code stops mattering then.</summary>
        /// <summary>
        /// False when this row would draw no picture: a code the shipped icon font has no icon for,
        /// which is what a configuration written against the old icon font mostly contains.
        /// </summary>
        public bool HasValidGlyph => HasIconFile || HasIconText || IconFont.TryResolve(_glyph, out _);

        /// <summary>
        /// The icon exactly as the ring will draw it - SVG or glyph, in the configured colour - so
        /// the settings row is a real preview rather than an approximation.
        /// </summary>
        public ActionEditModel IconSource => this;

        public ActionItem ToActionItem() => new()
        {
            Tag = _tag,
            Label = (_label ?? string.Empty).Trim(),
            Kind = ActionValueCodec.IndexToKind(_kindIndex),
            Value = (_value ?? string.Empty).Trim(),
            Arguments = (_arguments ?? string.Empty).Trim(),
            Glyph = (_glyph ?? string.Empty).Trim().ToUpperInvariant(),
            IconPath = (_iconPath ?? string.Empty).Trim(),
            IconText = (_iconText ?? string.Empty).Trim(),
            IconScale = _iconScale,
            IconOffsetX = _iconOffsetX,
            IconOffsetY = _iconOffsetY,
            IconTint = _iconTint,
            Foreground = (_foreground ?? string.Empty).Trim(),
            Background = (_background ?? string.Empty).Trim(),
            Shadow = (_shadow ?? string.Empty).Trim(),
            ShadowEnabled = _shadowEnabled,
            ShadowOpacity = _shadowOpacity,
            ShadowBlur = _shadowBlur,
            ShadowOffsetX = _shadowOffsetX,
            ShadowOffsetY = _shadowOffsetY,
            HoldKind = ActionValueCodec.IndexToKind(_holdKindIndex),
            HoldValue = (_holdValue ?? string.Empty).Trim(),
            HoldArguments = (_holdArguments ?? string.Empty).Trim(),
            GroupChildren = _groupChildren,
        };

        /// <summary>
        /// Overwrites every editable field from <paramref name="source"/>, keeping this instance so
        /// the bindings stay attached. Used by undo/redo and by the per-row reset.
        /// </summary>
        public void CopyFrom(ActionItem source)
        {
            _tag = source.Tag;
            _label = source.Label;
            _value = source.Value;
            _arguments = source.Arguments;
            _glyph = source.Glyph;
            _iconPath = source.IconPath;
            _iconText = source.IconText;
            _iconScale = Math.Round(source.IconScale, 2, MidpointRounding.AwayFromZero);
            _iconOffsetX = Math.Round(source.IconOffsetX, 2, MidpointRounding.AwayFromZero);
            _iconOffsetY = Math.Round(source.IconOffsetY, 2, MidpointRounding.AwayFromZero);
            _iconTint = source.IconTint;
            _foreground = source.Foreground;
            _background = source.Background;
            _shadow = source.Shadow;
            _shadowEnabled = source.ShadowEnabled;
            _shadowOpacity = Math.Round(source.ShadowOpacity, 2, MidpointRounding.AwayFromZero);
            _shadowBlur = Math.Round(source.ShadowBlur, 1, MidpointRounding.AwayFromZero);
            _shadowOffsetX = Math.Round(source.ShadowOffsetX, 1, MidpointRounding.AwayFromZero);
            _shadowOffsetY = Math.Round(source.ShadowOffsetY, 1, MidpointRounding.AwayFromZero);
            _kindIndex = ActionValueCodec.KindToIndex(source.Kind);
            _holdKindIndex = ActionValueCodec.KindToIndex(source.HoldKind);
            _holdValue = source.HoldValue;
            _holdArguments = source.HoldArguments;
            _groupChildren = source.GroupChildren;
            _status = ComputeStatus();

            // Everything at once: individual setters were bypassed to avoid nine separate change
            // notifications and nine undo entries for what is one operation.
            foreach (var name in AllPropertyNames)
                Raise(name);
        }

        private static readonly string[] AllPropertyNames =
        {
            nameof(Tag), nameof(PositionName), nameof(TagCaption), nameof(Label), nameof(Value),
            nameof(Arguments), nameof(Glyph), nameof(IconPath), nameof(HasIconFile),
            nameof(IconText), nameof(HasIconText),
            nameof(IconScale), nameof(IconScaleText), nameof(IconOffsetX), nameof(IconOffsetXText),
            nameof(IconOffsetY), nameof(IconOffsetYText),
            nameof(IconTint), nameof(CanTintIcon),
            nameof(Foreground), nameof(Background), nameof(Shadow), nameof(KindIndex), nameof(IsValueEnabled),
            nameof(ShadowEnabled), nameof(ShadowOpacity), nameof(ShadowOpacityText), nameof(ShadowBlur), nameof(ShadowBlurText),
            nameof(ShadowOffsetX), nameof(ShadowOffsetXText), nameof(ShadowOffsetY), nameof(ShadowOffsetYText),
            nameof(CanBrowse), nameof(CanRecord), nameof(ValuePlaceholder),
            nameof(IsFunction),
            nameof(HoldKindIndex), nameof(HoldValue), nameof(HoldArguments), nameof(IsHoldValueEnabled),
            nameof(CanBrowseHold), nameof(HoldValuePlaceholder), nameof(IsHoldFunction), nameof(HoldFunctionChoices),
            nameof(IsGroup), nameof(GroupChildren), nameof(EditGroupText), nameof(CanBeGroup),
            nameof(FunctionQuery), nameof(FunctionSuggestions),
            nameof(IconSource), nameof(ForegroundColor), nameof(BackgroundColor), nameof(ShadowColor),
            nameof(HasStatus), nameof(StatusGlyph), nameof(StatusTooltip), nameof(StatusColor),
        };

        private void RaiseIconChanged()
        {
            Raise(nameof(IconSource));
            Raise(nameof(ForegroundColor));
            Raise(nameof(BackgroundColor));
            Raise(nameof(ShadowColor));
        }

        public void RefreshStatus()
        {
            var status = ComputeStatus();
            if (_status == status)
                return;

            _status = status;
            Raise(nameof(HasStatus));
            Raise(nameof(StatusGlyph));
            Raise(nameof(StatusTooltip));
            Raise(nameof(StatusColor));
        }

    }
}
