using System;
using Action_Wheel.Services;

namespace Action_Wheel.ViewModels
{
    /// <summary>
    /// The ring's size and opening animation: the part of the settings screen that is not about the
    /// nine buttons.
    /// </summary>
    /// <remarks>
    /// A partial of <see cref="SettingsViewModel"/> rather than a class of its own, following
    /// SettingsViewModel.Profiles.cs. These properties are bound directly by the window and share
    /// the base class's Set/Raise and the preview timer, so extracting them into a separate object
    /// would mean either exposing that plumbing or re-forwarding every notification through it. The
    /// split here is for reading, not for ownership - which is why it is a file and not a type.
    /// </remarks>
    public sealed partial class SettingsViewModel
    {
        #region Ring appearance

        private RingAppearance _appearance = RingAppearance.Default;

        /// <summary>The size and animation the preview draws and a save will write.</summary>
        public RingAppearance Appearance => _appearance;

        /// <summary>
        /// The order the animations are offered in, which is deliberately not the enum's order.
        /// </summary>
        /// <remarks>
        /// The enum's numbers go in preferences.json and are append-only, so the two sweeps ended up
        /// at opposite ends of it - and a picker that separates "Clockwise sweep" from
        /// "Counter-clockwise sweep" is a bad picker. Going through this array is what lets the list
        /// be grouped sensibly without renumbering anyone's saved setting. It must stay in step with
        /// the ComboBoxItems in SettingsWindow.xaml.
        /// </remarks>
        private static readonly RingAnimation[] AnimationOrder =
        {
            RingAnimation.None,
            RingAnimation.Fade,
            RingAnimation.Pop,
            RingAnimation.Radiate,
            RingAnimation.Converge,
            RingAnimation.Sweep,
            RingAnimation.CounterSweep,
        };

        /// <summary>Bound to the ComboBox's SelectedIndex, mapped through <see cref="AnimationOrder"/>.</summary>
        public int RingAnimationIndex
        {
            get => Math.Max(0, Array.IndexOf(AnimationOrder, _appearance.Animation));
            set => ApplyAppearance(
                _appearance with
                {
                    Animation = value >= 0 && value < AnimationOrder.Length
                        ? AnimationOrder[value] : RingAnimation.Fade,
                },
                nameof(RingAnimationIndex),
                // The only edit on this card that replays the preview by itself. Picking from a
                // list is a single deliberate act with an answer the user is waiting to see; the
                // two size boxes are not, and replaying on each click of a spinner - or on each
                // digit of a typed number - would be a card that will not sit still. The play
                // button beside the picker is how a size change is reviewed.
                animate: true);
        }

        public double ButtonSize
        {
            get => _appearance.ButtonSize;
            set => ApplyAppearance(_appearance with { ButtonSize = value }, nameof(ButtonSize));
        }

        public double OrbitRadius
        {
            get => _appearance.OrbitRadius;
            set => ApplyAppearance(_appearance with { OrbitRadius = value }, nameof(OrbitRadius));
        }

        /// <summary>What each Slider's own Value would otherwise show as a bare, unlabelled number.</summary>
        public string ButtonSizeText => $"{_appearance.ButtonSize:0} px";
        public string OrbitRadiusText => $"{_appearance.OrbitRadius:0} px";

        /// <summary>
        /// How long the opening animation takes to finish, in milliseconds. Not on the
        /// animation-picker card's replay path: like <see cref="ButtonSize"/> and
        /// <see cref="OrbitRadius"/> it is a value dragged rather than a single deliberate pick, so
        /// it does not replay the preview by itself - the Play button is how it is reviewed.
        /// </summary>
        public double AnimationDurationMs
        {
            get => _appearance.AnimationDurationMs;
            set => ApplyAppearance(
                _appearance with { AnimationDurationMs = value }, nameof(AnimationDurationMs));
        }

        /// <summary>What the Slider's own Value would otherwise show as a bare, unlabelled number.</summary>
        public string AnimationDurationMsText =>
            $"{_appearance.AnimationDurationMs:0} ms";

        public double MinAnimationDurationMs => RingAppearance.MinAnimationDurationMs;
        public double MaxAnimationDurationMs => RingAppearance.MaxAnimationDurationMs;

        public double MinButtonSize => RingGeometry.MinButtonSizeDip;
        public double MaxButtonSize => RingGeometry.MaxButtonSizeDip;

        /// <summary>
        /// The orbit's limits for the button size currently chosen. Bound to the NumberBox rather
        /// than written into the XAML as constants, because they are not constants: bigger buttons
        /// need a wider orbit before they stop touching each other, and leave less room before the
        /// ring runs out of window. A fixed range would let the user pick a pair that overlaps.
        /// </summary>
        public double MinOrbitRadius => RingGeometry.MinOrbitFor(_appearance.ButtonSize);

        public double MaxOrbitRadius => RingGeometry.MaxOrbitFor(_appearance.ButtonSize);

        public string RingAnimationDescription => _appearance.Animation switch
        {
            RingAnimation.None => "无动画，首帧完整显示，速度最快。/ No animation; the ring is fully drawn on its first frame — the fastest option.",
            RingAnimation.Pop => "按钮从四分之一大小弹出，带回弹。/ Each button springs up from a quarter of its size with an overshoot.",
            RingAnimation.Radiate => "按钮由中心飞出，从上方顺时针依次到位。/ Buttons fly out from the centre, clockwise from the top.",
            RingAnimation.Converge => "按钮从外围聚拢，中心最后出现。/ Buttons converge from outside; the centre appears last.",
            RingAnimation.Sweep => "轮盘顺时针转过一个按钮位置，图标保持正立。/ The ring sweeps one slot clockwise; icons stay upright.",
            RingAnimation.CounterSweep => "轮盘逆时针转过一个按钮位置，图标保持正立。/ The ring sweeps one slot anticlockwise; icons stay upright.",
            _ => "轮盘淡入并放大，按钮依次出现。/ The ring fades and scales up as buttons arrive in sequence.",
        };

        /// <summary>
        /// Stores a normalised appearance and tells the UI what moved.
        /// </summary>
        /// <remarks>
        /// The else branch is the one that is easy to leave out. Normalised refuses an out-of-range
        /// or non-finite value and keeps the old number, so the record compares equal, so nothing is
        /// raised - and the control then stays showing the rejected value for the rest of the
        /// session while the model quietly holds a value the user cannot see. Raising the property
        /// on a refused edit is what puts the real number back in the control.
        /// </remarks>
        private void ApplyAppearance(RingAppearance requested, string propertyName, bool animate = false)
        {
            var next = requested.Normalised();
            if (next == _appearance)
            {
                Raise(propertyName);
                return;
            }

            _appearance = next;

            // All of them, not just the one that was edited: clamping the button size moves the
            // orbit's limits, which can in turn move the orbit itself.
            //
            // The limits go FIRST, and that order is load-bearing. The Slider coerces its Value
            // into its own Minimum/Maximum and writes the coerced number back through the two-way
            // binding. Raising OrbitRadius while the control still holds the previous size's Minimum
            // therefore does not just display the wrong number - it overwrites the model with it.
            // Restore defaults with a 64 DIP ring showed exactly that: the size went back to 44 and
            // the orbit stuck at 92, the old minimum, instead of 88.
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
            ScheduleRefresh(redraw: true, animate: animate);
        }

        #endregion
    }
}
