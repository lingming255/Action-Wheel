using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Action_Wheel.Overlay
{
    /// <summary>
    /// The ring geometry an opening animation needs, in the coordinate space it will be drawn in.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="RingAppearance"/> itself. The settings preview plays the same
    /// presets over a ring it has scaled down to fit its card, on a surface that is the card rather
    /// than the overlay window; handing the appearance straight through would have animated real
    /// overlay DIPs on top of that scaled layout, so the buttons would fly in from several times too
    /// far away and Radiate would start well outside the ring instead of on its centre.
    /// </remarks>
    /// <param name="SurfaceSize">
    /// The side of the square the ring is drawn on. Only Converge reads it, to work out how far
    /// outside the orbit a button can start without being clipped by the edge.
    /// </param>
    /// <param name="DurationScale">
    /// <see cref="RingAppearance.AnimationDurationMs"/> divided by <see cref="BudgetMs"/> - multiplies
    /// every begin and duration value a preset builds. 1 plays a preset exactly as tuned.
    /// </param>
    internal readonly record struct RingMetrics(
        double OrbitRadius, double ButtonSize, double SurfaceSize, double DurationScale)
    {
        /// <summary>The live overlay: the ring at its own size, on the window derived from it.</summary>
        public static RingMetrics For(RingAppearance appearance) =>
            new(appearance.OrbitRadius, appearance.ButtonSize, appearance.MenuSize,
                appearance.AnimationDurationMs / RingOpenAnimation.BudgetMs);
    }

    /// <summary>
    /// Builds the ring's opening storyboard for the animation the user picked.
    /// </summary>
    /// <remarks>
    /// In code rather than XAML because there are now seven of these and each one needs a line per
    /// button: as markup that is the 165-line storyboard this replaced, seven times over, with the
    /// eight button names and their stagger offsets retyped in every copy. Radiate and Converge
    /// could not be written in XAML at all - each button travels to or from the ring's centre, so
    /// its start offset is the orbit radius, which is a user setting and not known until the menu
    /// opens.
    ///
    /// Every animation here drives only Opacity and RenderTransform. Both are *independent*
    /// animations in XAML, meaning composition runs them off the UI thread; the same movement
    /// expressed as Canvas.Left/Top would be a dependent animation, run on the UI thread, competing
    /// with the config parse and the SVG decoding that also happen while the menu is opening.
    ///
    /// **Fade fast, move slowly.** This is the rule that separates the presets, and getting it
    /// wrong is what made the first version of Pop and Sweep indistinguishable from Fade: their
    /// motion ran over the same 70-90 ms window as their 0 -> 1 opacity, so the button was still
    /// nearly transparent while it was small or rotated, and by the time it could be seen it had
    /// already arrived. Each preset below finishes its fade in 30-50 ms and spends the rest of the
    /// budget on movement that is actually visible.
    ///
    /// The timings are bounded, not free. Click-to-first-pixel is around 15 ms, so the animation is
    /// almost the entire delay the user actually feels; the original storyboard was retuned from
    /// 400 ms down to 186 ms for exactly that reason. Every preset finishes inside <see cref="BudgetMs"/>
    /// at <see cref="RingAppearance.DefaultAnimationDurationMs"/> - the user's own duration setting is
    /// the one deliberate way past that budget, traded off against the ratios above still holding at
    /// any scale since every value in a preset is multiplied by the same factor.
    /// </remarks>
    internal static class RingOpenAnimation
    {
        /// <summary>
        /// Total wall-clock budget for an opening animation, in milliseconds, at the default
        /// <see cref="RingAppearance.AnimationDurationMs"/>. Must match
        /// <see cref="RingAppearance.DefaultAnimationDurationMs"/>.
        /// </summary>
        public const double BudgetMs = 200.0;

        /// <summary>
        /// How far the ring turns in a sweep. One eighth of a turn, so it reads as the ring rotating
        /// by exactly one button position rather than by an arbitrary amount.
        /// </summary>
        private const double SweepDegrees = 45.0;

        /// <summary>Longest distance a Converge button may start from outside its orbit, in DIPs.</summary>
        private const double MaxConvergeTravelDip = 72.0;

        /// <summary>Shortest distance that still reads as converging rather than as a plain fade.</summary>
        /// <remarks>
        /// Never binds in the overlay, whose window is derived from the ring and always leaves at
        /// least 85 DIP of room. It exists for the settings preview, which draws the ring on a card
        /// tighter than the overlay window: without a floor the largest rings had no room left at
        /// all, so the one preset the user was trying to look at played as a fade. The cost is that
        /// the first frames of the outermost buttons can be drawn under the card's edge.
        /// </remarks>
        private const double MinConvergeTravelDip = 18.0;

        /// <summary>
        /// Prepares <paramref name="container"/> and <paramref name="buttons"/> for animation and
        /// returns the storyboard to run, or null for <see cref="RingAnimation.None"/> - in which
        /// case the caller has nothing to start and the ring is simply already there.
        /// </summary>
        /// <param name="buttons">
        /// Indexed by tag: 0 is the centre button, 1-8 clockwise from the top. Typed as
        /// <see cref="UIElement"/> rather than <c>Button</c> so the settings preview, whose ring is
        /// made of Borders, plays the identical storyboard rather than a second copy of it that
        /// could drift.
        /// </param>
        public static Storyboard? Build(RingAnimation kind, FrameworkElement container,
            IReadOnlyList<UIElement> buttons, RingMetrics metrics)
        {
            // The buttons start invisible in XAML so nothing flashes at full opacity before the
            // storyboard's first frame. "None" is the one case that has to undo that by hand.
            if (kind == RingAnimation.None)
            {
                container.Opacity = 1;
                foreach (var button in buttons)
                    button.Opacity = 1;
                return null;
            }

            var storyboard = new Storyboard();

            // Before the switch, so every preset scales and rotates the ring about its own centre
            // even when it never touches the container itself.
            EnsureTransform(container);

            switch (kind)
            {
                case RingAnimation.Pop:
                    BuildPop(storyboard, container, buttons, metrics);
                    break;
                case RingAnimation.Radiate:
                    BuildRadiate(storyboard, container, buttons, metrics);
                    break;
                case RingAnimation.Converge:
                    BuildConverge(storyboard, container, buttons, metrics);
                    break;
                case RingAnimation.Sweep:
                    BuildSweep(storyboard, container, buttons, clockwise: true, metrics);
                    break;
                case RingAnimation.CounterSweep:
                    BuildSweep(storyboard, container, buttons, clockwise: false, metrics);
                    break;
                default:
                    BuildFade(storyboard, container, buttons, metrics);
                    break;
            }

            return storyboard;
        }

        /// <summary>
        /// The original animation, timings unchanged: the ring fades and scales up as a whole while
        /// the buttons come in one after another, the last landing 186 ms in. Deliberately the plain
        /// one - it is the reference the other six are variations on.
        /// </summary>
        private static void BuildFade(Storyboard storyboard, FrameworkElement container,
            IReadOnlyList<UIElement> buttons, RingMetrics metrics)
        {
            double scale = metrics.DurationScale;

            Fade(storyboard, container, 0, 0, 90, scale);
            Scale(storyboard, container, 0.86, 0, 120, scale);

            Fade(storyboard, buttons[0], 0, 0, 80, scale);
            for (int tag = 1; tag < buttons.Count; tag++)
                Fade(storyboard, buttons[tag], 0, 12 * tag, 90, scale);
        }

        /// <summary>Each button springs up from a quarter of its size, overshooting on the way.</summary>
        private static void BuildPop(Storyboard storyboard, FrameworkElement container,
            IReadOnlyList<UIElement> buttons, RingMetrics metrics)
        {
            double scale = metrics.DurationScale;

            Fade(storyboard, container, 0, 0, 50, scale);

            for (int tag = 0; tag < buttons.Count; tag++)
            {
                double begin = 7 * tag;

                // 35 ms against 140 ms of growth: the button is solid for three quarters of its
                // journey, which is the whole point of the preset. Matching the two durations is
                // what made this look like a plain fade.
                Fade(storyboard, buttons[tag], 0, begin, 35, scale);
                Scale(storyboard, buttons[tag], 0.25, begin, 140, scale,
                    new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.9 });
            }
        }

        /// <summary>
        /// The buttons start stacked on the centre and fly out to their orbit, clockwise from the
        /// top - the ring drawing itself rather than appearing already drawn.
        /// </summary>
        private static void BuildRadiate(Storyboard storyboard, FrameworkElement container,
            IReadOnlyList<UIElement> buttons, RingMetrics metrics)
        {
            double scale = metrics.DurationScale;

            Fade(storyboard, container, 0, 0, 45, scale);
            Fade(storyboard, buttons[0], 0, 0, 45, scale);
            Scale(storyboard, buttons[0], 0.5, 0, 110, scale, new CubicEase { EasingMode = EasingMode.EaseOut });

            for (int tag = 1; tag < buttons.Count; tag++)
            {
                var (offsetX, offsetY) = Offset(tag, metrics.OrbitRadius);
                double begin = 6 * tag;
                var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

                Fade(storyboard, buttons[tag], 0, begin, 45, scale);
                Scale(storyboard, buttons[tag], 0.65, begin, 150, scale, easing);

                // Negated: the button is laid out at its final position, so travelling *out* from
                // the centre means starting at minus its own offset and animating back to zero.
                Translate(storyboard, buttons[tag], -offsetX, -offsetY, begin, 150, scale, easing);
            }
        }

        /// <summary>
        /// The mirror of Radiate: the buttons start beyond the ring and close inward onto it.
        /// </summary>
        /// <remarks>
        /// How far outside they start is not a free choice. The overlay window clips hard at its
        /// edge, so a button that began past that edge would be sliced in half on the way in - and
        /// the window is transparent, so the cut would be plainly visible rather than hidden by a
        /// frame. The travel is therefore whatever room is left between the orbit and the window,
        /// capped at <see cref="MaxConvergeTravelDip"/>. That used to run out at the widest rings,
        /// because the window was a fixed 400 DIP square however big the ring got; now that the
        /// window is derived from the ring the room is constant and this preset behaves the same at
        /// every size. The settings preview is the one caller whose surface is still tight - it draws
        /// the ring on a card, not on the overlay window - which is what
        /// <see cref="MinConvergeTravelDip"/> is there for.
        /// </remarks>
        private static void BuildConverge(Storyboard storyboard, FrameworkElement container,
            IReadOnlyList<UIElement> buttons, RingMetrics metrics)
        {
            double scale = metrics.DurationScale;
            double room = metrics.SurfaceSize / 2.0
                - metrics.OrbitRadius - metrics.ButtonSize * 0.56 - 2.0;
            double travel = Math.Clamp(room, MinConvergeTravelDip, MaxConvergeTravelDip);

            Fade(storyboard, container, 0, 0, 45, scale);
            Fade(storyboard, buttons[0], 0, 110, 60, scale);
            Scale(storyboard, buttons[0], 0.4, 110, 90, scale,
                new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.7 });

            for (int tag = 1; tag < buttons.Count; tag++)
            {
                var (offsetX, offsetY) = Offset(tag, travel);
                double begin = 5 * tag;
                var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

                Fade(storyboard, buttons[tag], 0, begin, 40, scale);
                // Positive, unlike Radiate: outside the orbit rather than inside it.
                Translate(storyboard, buttons[tag], offsetX, offsetY, begin, 150, scale, easing);
                Scale(storyboard, buttons[tag], 1.25, begin, 150, scale, easing);
            }
        }

        /// <summary>
        /// The ring turns one button-slot into place, revealing the buttons in the direction it
        /// turns.
        /// </summary>
        /// <remarks>
        /// The container rotates and every button counter-rotates by exactly the same amount over
        /// exactly the same duration and easing. The container's rotation is about the ring's
        /// centre and each button's is about its own, so the two cancel for the glyph while leaving
        /// the orbital movement intact: the buttons slide around the circle with their icons
        /// upright. Stagger the counter-rotation and the cancellation stops being exact, which is
        /// why only the opacity and scale are staggered here.
        /// </remarks>
        private static void BuildSweep(Storyboard storyboard, FrameworkElement container,
            IReadOnlyList<UIElement> buttons, bool clockwise, RingMetrics metrics)
        {
            double scale = metrics.DurationScale;
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

            // A clockwise arrival means starting turned anticlockwise. XAML's Rotation is positive
            // clockwise, so the wind-up is negative for a clockwise sweep.
            double windUp = clockwise ? -SweepDegrees : SweepDegrees;

            Fade(storyboard, container, 0, 0, 50, scale);
            Rotate(storyboard, container, windUp, 0, 190, scale, easing);

            Fade(storyboard, buttons[0], 0, 0, 45, scale);
            Scale(storyboard, buttons[0], 0.7, 0, 110, scale, easing);

            for (int tag = 1; tag < buttons.Count; tag++)
            {
                // Reveal order follows the direction of travel: tag 1 first going clockwise, tag 8
                // first going the other way. Without this the buttons appear against the rotation
                // and the sweep reads as two different movements at once.
                int order = clockwise ? tag : buttons.Count - tag;
                double begin = 12 * order;

                Fade(storyboard, buttons[tag], 0, begin, 30, scale);
                Scale(storyboard, buttons[tag], 0.85, begin, 90, scale, easing);
                Rotate(storyboard, buttons[tag], -windUp, 0, 190, scale, easing);
            }
        }

        /// <summary>
        /// A button's offset from the ring's centre at <paramref name="radius"/>, using the same
        /// angles as UpdateButtonPositions: tag 1 straight up, 45 degrees per step clockwise.
        /// </summary>
        private static (double X, double Y) Offset(int tag, double radius)
        {
            double radians = (tag - 1) * Math.PI / 4.0;
            return (radius * Math.Sin(radians), -radius * Math.Cos(radians));
        }

        /// <summary>
        /// Gives an element a <see cref="CompositeTransform"/> that scales and rotates about its own
        /// middle, replacing whatever transform it had.
        /// </summary>
        /// <remarks>
        /// RenderTransformOrigin rather than the transform's CenterX/CenterY: the origin is a
        /// fraction, so it stays correct when the button's diameter is a user setting, while
        /// CenterX/CenterY are absolute DIPs that would have to be rewritten on every size change.
        /// </remarks>
        private static CompositeTransform EnsureTransform(UIElement element)
        {
            if (element.RenderTransform is CompositeTransform existing)
                return existing;

            var transform = new CompositeTransform();
            element.RenderTransform = transform;
            element.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            return transform;
        }

        private static void Fade(Storyboard storyboard, UIElement element,
            double from, double beginMs, double durationMs, double scale) =>
            Add(storyboard, element, "Opacity", from, 1, beginMs, durationMs, scale,
                new QuadraticEase { EasingMode = EasingMode.EaseOut });

        private static void Scale(Storyboard storyboard, UIElement element,
            double from, double beginMs, double durationMs, double scale, EasingFunctionBase? easing = null)
        {
            EnsureTransform(element);
            easing ??= new QuadraticEase { EasingMode = EasingMode.EaseOut };
            Add(storyboard, element, "(UIElement.RenderTransform).(CompositeTransform.ScaleX)",
                from, 1, beginMs, durationMs, scale, easing);
            Add(storyboard, element, "(UIElement.RenderTransform).(CompositeTransform.ScaleY)",
                from, 1, beginMs, durationMs, scale, easing);
        }

        private static void Translate(Storyboard storyboard, UIElement element,
            double fromX, double fromY, double beginMs, double durationMs, double scale,
            EasingFunctionBase easing)
        {
            EnsureTransform(element);
            Add(storyboard, element, "(UIElement.RenderTransform).(CompositeTransform.TranslateX)",
                fromX, 0, beginMs, durationMs, scale, easing);
            Add(storyboard, element, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)",
                fromY, 0, beginMs, durationMs, scale, easing);
        }

        private static void Rotate(Storyboard storyboard, UIElement element,
            double fromDegrees, double beginMs, double durationMs, double scale, EasingFunctionBase easing)
        {
            EnsureTransform(element);
            Add(storyboard, element, "(UIElement.RenderTransform).(CompositeTransform.Rotation)",
                fromDegrees, 0, beginMs, durationMs, scale, easing);
        }

        private static void Add(Storyboard storyboard, DependencyObject target, string path,
            double from, double to, double beginMs, double durationMs, double scale,
            EasingFunctionBase easing)
        {
            var animation = new DoubleAnimation
            {
                From = from,
                To = to,
                BeginTime = TimeSpan.FromMilliseconds(beginMs * scale),
                Duration = new Duration(TimeSpan.FromMilliseconds(durationMs * scale)),
                EasingFunction = easing,
            };

            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, path);
            storyboard.Children.Add(animation);
        }
    }
}
