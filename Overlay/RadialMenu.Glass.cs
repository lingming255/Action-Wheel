using System;
using Action_Wheel.Services;
using Microsoft.UI;
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
        private WheelGlassSurface? _glassSurface;
        private Border? _glassSheet;
        private bool _glassStartedHidden;
        private float _glassSaturation = 1;
        private Storyboard? _ringMotion;
        private bool _closing;
        internal bool IsClosing => _closing;
        internal IntPtr ActionTargetWindow { get; set; }
        private readonly Path?[] _directionArcs = new Path?[9];
        private bool GlassIsDark => RootGrid.ActualTheme == ElementTheme.Dark;
        private Color GlassInk => GlassIsDark
            ? Color.FromArgb(255, 233, 242, 243) : Color.FromArgb(255, 23, 49, 57);
        private double GlassOuterRadius => Math.Max(RingGeometry.InnerRadiusDip + 48,
            _appearance.OrbitRadius + _appearance.ButtonSize * 58 / 52);

        internal void ApplyGlassSaturation(float saturation)
        {
            _glassSaturation = saturation;
            _glassSurface?.ApplySaturation(saturation);
        }

        private static Point GlassPolar(double centre, double radius, double degrees)
        {
            double radians = degrees * Math.PI / 180;
            return new Point(centre + radius * Math.Cos(radians), centre + radius * Math.Sin(radians));
        }

        private void BuildGlassSheet()
        {
            double centre = MenuSize / 2.0;
            double outer = GlassOuterRadius;
            var sheet = new Border
            {
                Width = outer * 2, Height = outer * 2,
                CornerRadius = new CornerRadius(outer), IsHitTestVisible = false,
            };
            Canvas.SetLeft(sheet, centre - outer);
            Canvas.SetTop(sheet, centre - outer);
            Canvas.SetZIndex(sheet, -10);
            ButtonsCanvas.Children.Add(sheet);
            _glassSheet = sheet;

            var edge = new SolidColorBrush(GlassIsDark
                ? Color.FromArgb(64, 222, 239, 255) : Color.FromArgb(160, 255, 255, 255));
            foreach (double radius in new[] { outer - .5, RingGeometry.InnerRadiusDip })
            {
                var circle = new Ellipse
                {
                    Width = radius * 2, Height = radius * 2, Stroke = edge,
                    StrokeThickness = .7, IsHitTestVisible = false,
                };
                Canvas.SetLeft(circle, centre - radius);
                Canvas.SetTop(circle, centre - radius);
                ButtonsCanvas.Children.Add(circle);
            }
            for (int tag = 1; tag <= 8; tag++)
            {
                double boundary = -112.5 + (tag - 1) * 45;
                var start = GlassPolar(centre, RingGeometry.InnerRadiusDip, boundary);
                var end = GlassPolar(centre, outer - 1, boundary);
                ButtonsCanvas.Children.Add(new Line
                {
                    X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y,
                    Stroke = edge, StrokeThickness = .7, Opacity = .72, IsHitTestVisible = false,
                });
                double middle = -90 + (tag - 1) * 45;
                var figure = new PathFigure { StartPoint = GlassPolar(centre, outer - 4, middle - 12) };
                figure.Segments.Add(new ArcSegment
                {
                    Point = GlassPolar(centre, outer - 4, middle + 12),
                    Size = new Size(outer - 4, outer - 4), SweepDirection = SweepDirection.Clockwise,
                });
                var geometry = new PathGeometry();
                geometry.Figures.Add(figure);
                var arc = new Path
                {
                    Data = geometry, StrokeThickness = 2, Stroke = new SolidColorBrush(GlassIsDark
                        ? Color.FromArgb(225, 161, 214, 206) : Color.FromArgb(225, 39, 117, 114)),
                    Opacity = 0, IsHitTestVisible = false,
                };
                _directionArcs[tag] = arc;
                ButtonsCanvas.Children.Add(arc);
            }
            BuildGlassMotionAccents(centre, outer);
        }

        private void ConnectGlassMaterial()
        {
            if (_glassSheet != null && _transparentBackdrop != null)
                _glassSurface = WheelGlassSurface.Attach(_glassSheet, _windowHandle, GlassIsDark,
                    _transparentBackdrop.Compositor, _transparentBackdrop.SetBrush);
            _glassSurface?.ApplySaturation(_glassSaturation);
            _glassStartedHidden = _glassSurface?.Diagnostics.MaskOpacity == 0;
        }

        private void BeginGlassOpening()
        {
            foreach (var button in AllButtons) button.Opacity = 1;
            if (_appearance.Animation == RingAnimation.None)
            {
                MenuContainer.Opacity = 1;
                SettleAfterOpening();
                return;
            }
            _ringMotion = RingOpenAnimation.BuildUnified(MenuContainer, _appearance.AnimationDurationMs,
                kind: _appearance.Animation);
            _ringMotion.Completed += (_, _) => { if (!_closing) SettleAfterOpening(); };
            _ringMotion.Begin();
            StartGlassAccents();
        }

        /// <summary>Keep the visual closing tail while immediately returning input to the desktop.</summary>
        public void DismissMenu(bool confirmed = false, Action? completed = null)
        {
            if (_closed || _closing) return;
            _closing = true;
            WheelSounds.Play(confirmed ? WheelSound.Confirm : WheelSound.Cancel);
            DisarmHold();
            // Stop() restores base values. Preserve the current animated frame so a quick
            // release/Esc during opening does not jump to a fully opaque, full-size wheel.
            double opacity = MenuContainer.Opacity;
            var current = MenuContainer.RenderTransform as CompositeTransform;
            double scaleX = current?.ScaleX ?? 1, scaleY = current?.ScaleY ?? 1;
            double rotation = current?.Rotation ?? 0;
            _ringMotion?.Stop();
            MenuContainer.Opacity = opacity;
            MenuContainer.RenderTransform = new CompositeTransform
            {
                ScaleX = scaleX, ScaleY = scaleY, Rotation = rotation,
            };
            if (confirmed) StartGlassAccents(confirmed: true);
            else StopGlassAccents();
            RootGrid.IsHitTestVisible = false;
            RefreshInputTransparency.Enable(_windowHandle);
            if (_appearance.Animation == RingAnimation.None || IsRefreshPlaying)
            {
                CloseMenu();
                completed?.Invoke();
                return;
            }
            _ringMotion = RingOpenAnimation.BuildUnified(MenuContainer, _appearance.AnimationDurationMs,
                closing: true, confirmed: confirmed);
            _ringMotion.Completed += (_, _) =>
            {
                if (!_closed) CloseMenu();
                completed?.Invoke();
            };
            _ringMotion.Begin();
        }

        private void ConfirmGlassAction(ActionItem? action)
        {
            DismissMenu(confirmed: true, completed: () =>
            {
                if (action != null) ActionInvoked?.Invoke(this, action);
            });
        }
    }
}
