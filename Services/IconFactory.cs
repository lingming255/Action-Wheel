using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace Action_Wheel.Services
{
    /// <summary>
    /// Turns the icon and colour fields of an <see cref="ActionItem"/> into something XAML can show.
    /// Shared by the radial menu and the settings window so both render a button the same way.
    /// </summary>
    public static class IconFactory
    {
        // Keyed by everything that changes the decoded result, so a row whose colour is being
        // dragged around in the picker re-tints instead of showing a stale image.
        private static readonly Dictionary<string, ImageCacheEntry> ImageCache = new(StringComparer.Ordinal);
        private sealed record ImageCacheEntry(DateTime WriteTimeUtc, ImageSource Source);

        /// <summary>Comfortably more than nine buttons at two sizes; small enough to be free.</summary>
        private const int MaxCachedImages = 64;

        /// <summary>
        /// Decode once above the largest on-screen icon and resize the element while the slider
        /// moves. Keying the cache by every intermediate size made one drag decode dozens of SVGs
        /// or bitmaps and was the main source of the Scale control's stutter.
        /// </summary>
        private const double CachedIconPixels = 192;

        /// <summary>
        /// How much larger a vector icon is drawn than a glyph asked for at the same size.
        /// </summary>
        /// <remarks>
        /// A glyph and an image are not measured the same way. A glyph is drawn at an em of
        /// <c>size</c> and the icon fonts push their artwork out to the edge of it: across the
        /// glyphs this app ships by default, the inked part spans 0.89 to 1.00 of the size asked
        /// for, averaging 0.91. An image is laid out by its canvas instead, and an SVG canvas is
        /// padded by convention - the common icon sets draw about 20 units inside a 24-unit
        /// viewBox, or 0.83. Fitted into the same box the SVG therefore came out visibly smaller,
        /// and 1.15 puts it back on the glyphs' average.
        ///
        /// Raster icons get no such correction: measured, an icon taken from an application already
        /// spans 0.96 to 1.00 of its bitmap across, so scaling it up would overshoot the glyphs
        /// rather than match them. Icons this app extracts are cropped to their artwork when they
        /// are written, which is what makes that true of every application and not just the tidy
        /// ones - see <see cref="AppIconExtractor"/>.
        ///
        /// Both are applied on top of the per-action scale, so an icon that still looks wrong can be
        /// corrected in the settings window without moving everything else.
        /// </remarks>
        private const double VectorIconScale = 1.15;

        /// <summary>
        /// Builds the icon element for an action: the SVG file if one is configured and usable,
        /// otherwise the glyph, drawn from the font the app ships (see <see cref="IconFont"/>).
        /// </summary>
        /// <param name="tint">
        /// Fixed colour for a glyph icon, for somewhere the colour never changes. Leave it null when
        /// the caller drives the colour itself: a Foreground set here is a local value that beats
        /// anything assigned to the button later, which is what made hovering recolour the button
        /// but leave the glyph behind.
        /// It does not reach a file icon either way - an SVG is recoloured by rewriting the document
        /// (see <see cref="ActionItem.IconTint"/>) and a raster icon keeps its own pixels.
        /// </param>
        public static FrameworkElement CreateIcon(ActionItem action, double size, Brush? tint = null)
        {
            // IconScale is a presentation setting shared by every icon source. Previously it was
            // only applied inside TryCreateFileIcon, so the Scale control appeared to work for an
            // SVG/PNG/ICO but did nothing when the same button used a font glyph.
            double scale = double.IsFinite(action.IconScale)
                ? Math.Clamp(action.IconScale, 0.25, 3)
                : ActionItem.DefaultIconScale;
            double scaledSize = Math.Max(1, size * scale);

            if (TryCreateFileIcon(action, scaledSize, out var svg))
                return svg!;

            if (!string.IsNullOrWhiteSpace(action.IconText))
                return CreateTextIcon(action, scaledSize, tint);

            // A code the shipped font has no icon for draws nothing at all. There is no fallback
            // picture on purpose: one would have to be a guess at what the user meant, and the
            // notdef box the font would otherwise draw reads as a broken app rather than as a
            // setting to change. The button still works - only its picture is missing.
            if (!IconFont.TryResolve(action.Glyph, out string text))
                return new Border { Width = scaledSize, Height = scaledSize };

            var icon = new FontIcon
            {
                // Explicit, because FontIcon otherwise defaults to the system icon font, and the
                // same code means a different picture there - see IconFont.
                FontFamily = IconFont.Family,
                Glyph = text,
                FontSize = scaledSize,
                // FontIcon defaults to Stretch. In a Button/Border that makes its layout slot
                // depend on the host template, so the same glyph can sit slightly to the right in
                // the ring while looking centred in the picker. The Propo font's ink is centred in
                // its own advance; explicitly centring that advance keeps every rendering path the
                // same.
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                UseLayoutRounding = true,
            };

            // Assigning null would still be a local value - and a null brush draws nothing.
            if (tint != null)
                icon.Foreground = tint;

            return icon;
        }

        /// <summary>
        /// Draws short text - a letter, an emoji, a couple of characters - as the icon instead of a
        /// glyph or a file. The font shrinks as the text lengthens so two or three characters still
        /// fit inside the same circle a single glyph would.
        /// </summary>
        private static FrameworkElement CreateTextIcon(ActionItem action, double scaledSize, Brush? tint)
        {
            string text = action.IconText.Trim();

            // String.Length counts UTF-16 code units, not what the user typed - a single emoji is
            // often a surrogate pair, sometimes several code points joined by a zero-width joiner.
            // GetTextElementEnumerator counts what actually looks like one character on screen,
            // which is what the sizing below needs to shrink correctly.
            int elements = 0;
            var enumerator = StringInfo.GetTextElementEnumerator(text);
            while (enumerator.MoveNext())
                elements++;
            elements = Math.Max(1, elements);

            double factor = elements switch
            {
                1 => 0.62,
                2 => 0.48,
                3 => 0.36,
                _ => 0.30,
            };

            var block = new TextBlock
            {
                Text = text,
                FontSize = Math.Max(1, scaledSize * factor),
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                Width = scaledSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                UseLayoutRounding = true,
                RenderTransform = new TranslateTransform
                {
                    X = action.IconOffsetX,
                    Y = action.IconOffsetY,
                },
            };

            // Same rule as the glyph path above: a caller-supplied tint is a fixed colour for
            // somewhere it never changes; leaving Foreground unset lets it inherit the button's own
            // TemplateBinding instead, which is what keeps hover recolouring the text with it.
            if (tint != null)
                block.Foreground = tint;

            return block;
        }

        private static bool TryCreateFileIcon(ActionItem action, double scaledSize, out FrameworkElement? element)
        {
            element = null;
            string path = action.IconPath;

            if (!IconFile.IsUsable(path))
                return false;

            try
            {
                bool vector = IconFile.IsVector(path);

                // What makes a vector icon read at the same weight as a glyph asked for at this
                // already user-scaled size. Raster artwork needs no format-specific correction.
                double drawn = scaledSize * (vector ? VectorIconScale : 1);

                bool tint = action.IconTint && vector;
                var colour = ColorOr(action.Foreground, DefaultForeground(action.Tag));

                string fullPath = Path.GetFullPath(path);

                // Yes, this is a disk stat on the menu-open path, and no, it is not worth caching
                // away. Measured warm, nine file-backed icons cost 0.118 ms per open - 0.65% of the
                // ~18 ms it takes to build and show the ring, and zero for the default buttons,
                // which use glyphs and never reach this branch. What it buys is the only thing that
                // makes the cache below correct: an icon edited on disk is picked up on the next
                // open instead of never.
                DateTime writeTime = File.GetLastWriteTimeUtc(fullPath);
                string key = tint
                    ? $"{fullPath.ToLowerInvariant()}|{ToHex(colour)}"
                    : fullPath.ToLowerInvariant();

                if (!ImageCache.TryGetValue(key, out var cached) || cached.WriteTimeUtc != writeTime)
                {
                    // Dragging the colour picker over a tinted SVG produces a new entry per frame.
                    // Nothing here is worth keeping across that, so start over rather than grow
                    // without limit; the visible cost is one re-decode of icons still on screen.
                    if (ImageCache.Count > MaxCachedImages)
                        ImageCache.Clear();

                    var source = vector
                        ? CreateVectorSource(fullPath, CachedIconPixels, tint, colour)
                        : CreateRasterSource(fullPath, CachedIconPixels);

                    cached = new ImageCacheEntry(writeTime, source);
                    ImageCache[key] = cached;
                }

                element = new Image
                {
                    Source = cached.Source,
                    Width = drawn,
                    Height = drawn,
                    // SvgTint writes RGB into the document. Carry the configured alpha on the
                    // hosting element so #80RRGGBB behaves the same for an SVG and a glyph.
                    Opacity = tint ? colour.A / 255.0 : 1,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    UseLayoutRounding = true,
                    RenderTransform = new TranslateTransform
                    {
                        X = action.IconOffsetX,
                        Y = action.IconOffsetY,
                    },
                };

                return true;
            }
            catch (Exception)
            {
                // An unreadable icon falls back to the glyph. The settings row says so through
                // ActionsValidator, which is where a broken path is meant to be noticed.
                return false;
            }
        }

        /// <summary>
        /// An untinted SVG is handed to the decoder as a file, which is the cheapest path. A tinted
        /// one has to be rewritten first (<see cref="SvgTint"/>), so it arrives as a stream instead.
        /// </summary>
        /// <remarks>
        /// SetSourceAsync is deliberately not awaited: this runs from a binding converter and from
        /// the ring's ApplyActions, neither of which can go async without delaying the frame. The
        /// Image redraws itself when the decode completes, and the result is cached, so only the
        /// very first draw of a given colour can miss a frame.
        /// </remarks>
        private static ImageSource CreateVectorSource(string path, double pixels, bool tint, Color colour)
        {
            var source = new SvgImageSource
            {
                RasterizePixelWidth = pixels,
                RasterizePixelHeight = pixels,
            };

            if (!tint)
            {
                source.UriSource = new Uri(path);
                return source;
            }

            string tinted = SvgTint.Apply(File.ReadAllText(path), ToValue(colour));
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(tinted));

            // The stream has to outlive this method; the closure below is what keeps it alive until
            // the decoder is done with it.
            var load = source.SetSourceAsync(stream.AsRandomAccessStream());
            load.Completed = (_, _) => stream.Dispose();

            return source;
        }

        private static ImageSource CreateRasterSource(string path, double pixels) =>
            new BitmapImage(new Uri(path))
            {
                DecodePixelWidth = (int)Math.Round(pixels),
                DecodePixelType = DecodePixelType.Logical,
            };

        /// <summary>
        /// Built-in colours for a button, used when actions.json specifies none. Shared so the
        /// settings preview and the real menu cannot drift apart.
        /// </summary>
        public static Color DefaultBackground(int tag) => tag == 0
            ? Color.FromArgb(0xF0, 0x02, 0x06, 0x17)
            : Color.FromArgb(0xE6, 0x0F, 0x17, 0x2A);

        public static Color DefaultForeground(int tag) => tag == 0
            ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0xFF, 0xE0, 0xF2, 0xFE);

        // The colour helpers below are a thin XAML-facing skin over ColorValue in the core library.
        // The arithmetic lives there so it is one implementation rather than one per UI type.

        /// <summary>Parses "#RRGGBB" or "#AARRGGBB". Returns false for empty or malformed input.</summary>
        public static bool TryParseColor(string value, out Color color)
        {
            if (ColorValue.TryParse(value, out var parsed))
            {
                color = ToColor(parsed);
                return true;
            }

            color = default;
            return false;
        }

        /// <summary>The configured colour, or <paramref name="fallback"/> when it is unset or invalid.</summary>
        public static Color ColorOr(string value, Color fallback) =>
            TryParseColor(value, out var color) ? color : fallback;

        public static string ToHex(Color color) => ToValue(color).ToHex();

        /// <summary>Moves a colour towards white. Used for the hover state of a filled button.</summary>
        public static Color Lighten(Color color, double amount) => ToColor(ToValue(color).Lighten(amount));

        /// <summary>Moves a colour towards black. Used for the pressed state.</summary>
        public static Color Darken(Color color, double amount) => ToColor(ToValue(color).Darken(amount));

        private static Color ToColor(ColorValue value) => Color.FromArgb(value.A, value.R, value.G, value.B);

        private static ColorValue ToValue(Color color) => new(color.A, color.R, color.G, color.B);

    }
}
