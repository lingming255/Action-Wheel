using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Color = Windows.UI.Color;

namespace Action_Wheel.Overlay
{
    public sealed partial class RadialMenu
    {
        private readonly Storyboard?[] _selectionMotions = new Storyboard?[9];
        private readonly bool[] _selectedGlass = new bool[9];
        private Storyboard? _detailMotion;
        private Storyboard? _infoMotion;
        private Ellipse? _motionHalo;
        private Path? _openingSweep;
        private int? _soundSelection;

        private void AnnounceGlassSelection(int? tag)
        {
            if (_soundSelection == tag) return;
            _soundSelection = tag;
            if (tag is > 0) Services.WheelSounds.Play(Services.WheelSound.Switch);
        }

        private void BuildGlassMotionAccents(double centre, double outer)
        {
            var accent = new SolidColorBrush(GlassIsDark
                ? Color.FromArgb(140, 161, 214, 206) : Color.FromArgb(120, 39, 117, 114));
            _motionHalo = new Ellipse
            {
                Width = outer * 2, Height = outer * 2, Stroke = accent, StrokeThickness = 1,
                Opacity = 0, IsHitTestVisible = false, RenderTransformOrigin = new Point(.5, .5),
                RenderTransform = new CompositeTransform(),
            };
            Canvas.SetLeft(_motionHalo, centre - outer);
            Canvas.SetTop(_motionHalo, centre - outer);
            ButtonsCanvas.Children.Add(_motionHalo);
            var figure = new PathFigure { StartPoint = GlassPolar(centre, outer - 3, -125) };
            figure.Segments.Add(new ArcSegment
            {
                Point = GlassPolar(centre, outer - 3, -55), Size = new Size(outer - 3, outer - 3),
                SweepDirection = SweepDirection.Clockwise,
            });
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            _openingSweep = new Path
            {
                Width = MenuSize, Height = MenuSize, Data = geometry, Stroke = accent,
                StrokeThickness = 1.5, Opacity = 0, IsHitTestVisible = false,
                RenderTransformOrigin = new Point(.5, .5), RenderTransform = new CompositeTransform(),
            };
            ButtonsCanvas.Children.Add(_openingSweep);
        }

        private void StartGlassAccents(bool confirmed = false)
        {
            StopGlassAccents();
            if (_appearance.Animation == RingAnimation.None || _motionHalo == null || _openingSweep == null) return;
            double duration = confirmed ? Math.Clamp(_appearance.AnimationDurationMs * .65, 70, 160)
                : Math.Clamp(_appearance.AnimationDurationMs * 2, 180, 400);
            var motion = new Storyboard();
            AddGlassMotion(motion, _motionHalo, "Opacity", confirmed ? .55 : .28, 0, duration);
            foreach (string axis in new[] { "ScaleX", "ScaleY" })
                AddGlassMotion(motion, _motionHalo, $"(UIElement.RenderTransform).(CompositeTransform.{axis})",
                    confirmed ? .99 : .965, confirmed ? 1.055 : 1.075, duration);
            if (!confirmed)
            {
                AddGlassMotion(motion, _openingSweep, "Opacity", .8, 0, duration);
                AddGlassMotion(motion, _openingSweep, "(UIElement.RenderTransform).(CompositeTransform.Rotation)",
                    -30, 75, duration);
            }
            _detailMotion = motion;
            motion.Begin();
        }

        private void StopGlassAccents()
        {
            _detailMotion?.Stop();
            _detailMotion = null;
            if (_motionHalo != null) _motionHalo.Opacity = 0;
            if (_openingSweep != null) _openingSweep.Opacity = 0;
        }

        private void UpdateGlassSelection(int tag, bool active, Path wedge)
        {
            if (_selectedGlass[tag] == active) return;
            _selectedGlass[tag] = active;
            var arc = _directionArcs[tag];
            double opacity = wedge.Opacity, arcOpacity = arc?.Opacity ?? 0;
            _selectionMotions[tag]?.Stop();
            wedge.Opacity = opacity;
            if (arc != null) arc.Opacity = arcOpacity;
            if (_appearance.Animation == RingAnimation.None)
            {
                wedge.Opacity = active ? 1 : 0;
                if (arc != null) arc.Opacity = active ? 1 : 0;
                return;
            }
            var motion = new Storyboard();
            AddGlassMotion(motion, wedge, "Opacity", opacity, active ? 1 : 0, active ? 85 : 110);
            if (arc != null) AddGlassMotion(motion, arc, "Opacity", arcOpacity, active ? 1 : 0, active ? 100 : 110);
            _selectionMotions[tag] = motion;
            motion.Begin();
        }

        private void AnimateGlassInfo()
        {
            _infoMotion?.Stop();
            if (_appearance.Animation == RingAnimation.None) return;
            CenterInfo.RenderTransform = new CompositeTransform();
            var motion = new Storyboard();
            AddGlassMotion(motion, CenterInfo, "Opacity", .65, 1, 100);
            AddGlassMotion(motion, CenterInfo, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)", 3, 0, 100);
            _infoMotion = motion;
            motion.Begin();
        }

        private void StopGlassMotion()
        {
            StopGlassAccents();
            _infoMotion?.Stop();
            foreach (var motion in _selectionMotions) motion?.Stop();
        }

        private static void AddGlassMotion(Storyboard storyboard, DependencyObject target, string property,
            double from, double to, double duration)
        {
            var animation = new DoubleAnimation
            {
                From = from, To = to, Duration = new Duration(TimeSpan.FromMilliseconds(duration)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            storyboard.Children.Add(animation);
        }
    }
}
