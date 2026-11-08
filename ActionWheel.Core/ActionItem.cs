using System;
using System.Collections.Generic;

namespace Action_Wheel.Core
{
    /// <summary>What an <see cref="ActionItem"/> does when its button is clicked.</summary>
    public enum ActionKind
    {
        /// <summary>Do nothing beyond closing the menu.</summary>
        None,
        /// <summary>Send a keyboard shortcut to whatever window is in the foreground.</summary>
        Keys,
        /// <summary>Start a program, open a file/folder, or follow a URI.</summary>
        Launch,
        /// <summary>Run one of Action Wheel's built-in Windows functions.</summary>
        Function,
        /// <summary>
        /// Reveals up to 5 child actions on a second ring, concentric with and outside the main one,
        /// instead of dispatching anything itself. Selecting it never closes the menu.
        /// </summary>
        Group,
        /// <summary>Copy literal text to the Windows clipboard.</summary>
        CopyText,
    }

    /// <summary>One button of the radial menu.</summary>
    public sealed record ActionItem
    {
        public const double DefaultIconScale = 1;
        public const bool DefaultIconTint = true;
        public const bool DefaultShadowEnabled = true;
        public const double DefaultShadowOpacity = 0.45;
        public const double DefaultShadowBlur = 11;
        public const double DefaultShadowOffsetX = 0;
        public const double DefaultShadowOffsetY = 3;
        /// <summary>0 = the center button, 1-8 = the outer buttons clockwise from the top.</summary>
        public int Tag { get; init; }

        /// <summary>Tooltip text.</summary>
        public string Label { get; init; } = string.Empty;

        public ActionKind Kind { get; init; } = ActionKind.None;

        /// <summary>Shortcut ("Ctrl+Shift+S") for Keys, or a path/URI for Launch.</summary>
        public string Value { get; init; } = string.Empty;

        /// <summary>Command-line arguments passed to a Launch target. Ignored for other kinds.</summary>
        public string Arguments { get; init; } = string.Empty;

        /// <summary>
        /// Icon-font code point in hex, e.g. "F0C5". Five digits are allowed: two thirds of the
        /// font's icons live above U+FFFF.
        /// </summary>
        /// <remarks>
        /// The code is only meaningful against the font the app ships. The same value draws
        /// something else entirely in the system icon font, so a config moved between versions that
        /// use different fonts will not survive - which is why the font travels with the app.
        /// </remarks>
        public string Glyph { get; init; } = string.Empty;

        /// <summary>
        /// Path to an image file to draw instead of <see cref="Glyph"/>. Takes precedence when set.
        /// See <see cref="IconFile"/> for the formats that can be drawn.
        /// </summary>
        public string IconPath { get; init; } = string.Empty;

        /// <summary>
        /// Short text (a letter, a couple of characters, an emoji) to draw instead of
        /// <see cref="Glyph"/>. Takes precedence over the glyph but not over <see cref="IconPath"/> -
        /// a picked-out file is a stronger choice than either.
        /// </summary>
        public string IconText { get; init; } = string.Empty;

        /// <summary>Display-size multiplier shared by glyph, SVG, PNG, ICO and text icons.</summary>
        public double IconScale { get; init; } = DefaultIconScale;
        public double IconOffsetX { get; init; }
        public double IconOffsetY { get; init; }

        /// <summary>
        /// Recolours a vector icon to <see cref="Foreground"/> instead of drawing it in the colours
        /// it was authored with.
        /// </summary>
        /// <remarks>
        /// On by default, because the common case is a single-colour pictogram that should match the
        /// rest of the ring. Turn it off for a logo whose colours carry meaning. It has no effect on
        /// a raster icon - a PNG extracted from an application keeps that application's colours.
        /// </remarks>
        public bool IconTint { get; init; } = DefaultIconTint;

        /// <summary>Icon colour as "#RRGGBB"/"#AARRGGBB". Empty means the built-in colour.</summary>
        /// <remarks>Applies to a glyph, and to a vector icon when <see cref="IconTint"/> is on.</remarks>
        public string Foreground { get; init; } = string.Empty;

        /// <summary>Button fill colour as "#RRGGBB"/"#AARRGGBB". Empty means the built-in colour.</summary>
        public string Background { get; init; } = string.Empty;

        /// <summary>Drop-shadow colour. Empty means the built-in black shadow.</summary>
        public string Shadow { get; init; } = string.Empty;
        public bool ShadowEnabled { get; init; } = DefaultShadowEnabled;
        public double ShadowOpacity { get; init; } = DefaultShadowOpacity;
        public double ShadowBlur { get; init; } = DefaultShadowBlur;
        public double ShadowOffsetX { get; init; } = DefaultShadowOffsetX;
        public double ShadowOffsetY { get; init; } = DefaultShadowOffsetY;

        /// <summary>
        /// What this button does when pressed and held past the threshold instead of released
        /// quickly. <see cref="ActionKind.None"/> - the default - means there is no hold action and
        /// the button behaves exactly as it always has: click only, nothing armed or measured.
        /// </summary>
        /// <remarks>
        /// Deliberately not a double-click. Disambiguating a click from a double-click needs holding
        /// every ordinary click back for the system double-click interval before it counts as a
        /// single one, and this app is tuned around a click landing in the ~15 ms
        /// click-to-first-pixel it already takes - see RingOpenAnimation's remarks. A hold costs
        /// nothing on the common path: it only ever fires while the button is still down, past the
        /// threshold, which a quick click or flick never reaches.
        /// </remarks>
        public ActionKind HoldKind { get; init; } = ActionKind.None;

        /// <summary>Shortcut, path/URI, or built-in function id for <see cref="HoldKind"/>.</summary>
        public string HoldValue { get; init; } = string.Empty;

        /// <summary>Command-line arguments for a <see cref="ActionKind.Launch"/> hold action.</summary>
        public string HoldArguments { get; init; } = string.Empty;

        /// <summary>
        /// At the default button size/orbit radius the fan's angular step between neighbours is
        /// ~13 degrees (see <c>RadialMenu.BuildGroupButtons</c>), so 8 keeps the fan under 100
        /// degrees total - comfortably short of wrapping into the opposite side of the ring while
        /// still giving each satellite room to be hit accurately.
        /// </summary>
        public const int MaxGroupChildren = 8;

        /// <summary>
        /// The buttons a <see cref="ActionKind.Group"/> button reveals. Ignored for every other
        /// <see cref="Kind"/>. Each child is a full <see cref="ActionItem"/> so the exact same
        /// icon/dispatch code the main ring uses draws and runs it - only <see cref="Tag"/> means
        /// something different here: it is the child's position among its siblings (0 first,
        /// clockwise from the top), not a slot on the main ring, and a child's own
        /// <see cref="Kind"/> may not be <see cref="ActionKind.Group"/> - groups do not nest.
        /// </summary>
        public IReadOnlyList<ActionItem> GroupChildren { get; init; } = Array.Empty<ActionItem>();
    }
}
