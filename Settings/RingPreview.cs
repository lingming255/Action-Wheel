using System;
using System.Collections.Generic;
using System.Linq;
using Action_Wheel.Overlay;
using Action_Wheel.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Action_Wheel.Settings
{
    /// <summary>
    /// A scaled preview of the continuous glass sheet, its dividers and the actions being edited.
    /// Clicking a sector selects its row without dispatching the action or expanding a group.
    /// </summary>
    public static class RingPreview
    {
        private static Storyboard? _playing;
        private static Canvas? _playingOn;

        public static void Render(Canvas canvas, IEnumerable<ActionEditModel> items,
            RingAppearance appearance, Action<ActionEditModel>? selected = null, bool animate = false)
        {
            if (ReferenceEquals(_playingOn, canvas))
                _playing?.Stop();

            _playing = null;
            _playingOn = null;
            canvas.Children.Clear();

            double size = canvas.Width;
            double outerRadius = Math.Max(RingGeometry.InnerRadiusDip + 48,
                appearance.OrbitRadius + appearance.ButtonSize * 58 / 52);
            double scale = Math.Min(1.0, Math.Max(1.0, size - 8.0) / (2 * outerRadius));
            double radius = outerRadius * scale;
            double innerRadius = RingGeometry.InnerRadiusDip * scale;
            double diameter = 2 * radius;
            double orbit = appearance.OrbitRadius * scale;
            double iconSize = appearance.IconSize * .73 * scale;
            bool dark = canvas.ActualTheme == ElementTheme.Dark;
            Color foreground = dark ? Color.FromArgb(255, 233, 242, 243) : Color.FromArgb(255, 23, 49, 57);
            Color edge = dark ? Color.FromArgb(69, 222, 239, 255) : Color.FromArgb(163, 255, 255, 255);
            Color hover = dark ? Color.FromArgb(35, 159, 207, 200) : Color.FromArgb(55, 138, 187, 181);
            var foregroundBrush = new SolidColorBrush(foreground);
            var edgeBrush = new SolidColorBrush(edge);

            // One circular material, including the centre. Only transparent hit regions divide it.
            var ring = new Canvas { Width = diameter, Height = diameter };
            Canvas.SetLeft(ring, (size - diameter) / 2);
            Canvas.SetTop(ring, (size - diameter) / 2);
            canvas.Children.Add(ring);
            ring.Children.Add(new Border
            {
                Width = diameter,
                Height = diameter,
                CornerRadius = new CornerRadius(radius),
                IsHitTestVisible = false,
                Background = new AcrylicBrush
                {
                    TintColor = dark ? Color.FromArgb(255, 35, 51, 63) : Color.FromArgb(255, 232, 241, 246),
                    TintOpacity = .46,
                    FallbackColor = dark ? Color.FromArgb(240, 35, 51, 63) : Color.FromArgb(240, 232, 241, 246),
                },
            });

            var rows = items.Where(item => item.Tag >= 0 && item.Tag <= 8).OrderBy(item => item.Tag).ToArray();
            foreach (var item in rows)
            {
                Shape region;
                if (item.Tag == 0)
                {
                    region = new Ellipse { Width = 2 * innerRadius, Height = 2 * innerRadius };
                    Canvas.SetLeft(region, radius - innerRadius);
                    Canvas.SetTop(region, radius - innerRadius);
                }
                else
                {
                    double angle = (item.Tag - 1) * Math.PI / 4 - Math.PI / 2;
                    region = new Path
                    {
                        Width = diameter,
                        Height = diameter,
                        Data = Sector(radius, innerRadius, angle),
                    };
                }

                region.Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                region.PointerEntered += (s, e) => region.Fill = new SolidColorBrush(hover);
                region.PointerExited += (s, e) => region.Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                region.PointerPressed += (s, e) =>
                {
                    selected?.Invoke(item);
                    e.Handled = true;
                };
                ToolTipService.SetToolTip(region, $"{item.PositionName} — {item.Label}");
                ring.Children.Add(region);
            }

            AddCircle(ring, radius, radius, edgeBrush, .8 * scale);
            AddCircle(ring, radius, innerRadius, edgeBrush, .7 * scale);
            for (int i = 0; i < 8; i++)
            {
                double angle = -112.5 * Math.PI / 180 + i * Math.PI / 4;
                var inside = At(radius, innerRadius, angle);
                var outside = At(radius, radius, angle);
                ring.Children.Add(new Line
                {
                    X1 = inside.X,
                    Y1 = inside.Y,
                    X2 = outside.X,
                    Y2 = outside.Y,
                    Stroke = edgeBrush,
                    StrokeThickness = .7 * scale,
                    Opacity = .72,
                    IsHitTestVisible = false,
                });
            }

            foreach (var item in rows)
            {
                double angle = (item.Tag - 1) * Math.PI / 4 - Math.PI / 2;
                Point position = item.Tag == 0 ? new Point(radius, radius) : At(radius, orbit, angle);
                double hostSize = RingGeometry.OuterButtonSizeDip * scale;
                var host = new Grid { Width = hostSize, Height = hostSize, IsHitTestVisible = false };
                // Use the same theme tint for glyphs and tintable SVGs; keep untinted logos intact.
                var action = item.ToActionItem() with { Foreground = dark ? "#E9F2F3" : "#173139" };
                host.Children.Add(IconFactory.CreateIcon(action, iconSize, foregroundBrush));

                if (item.IsGroup)
                {
                    var badge = new FontIcon
                    {
                        FontFamily = IconFont.Family,
                        Glyph = "",
                        FontSize = Math.Max(8, 10 * scale),
                        Foreground = foregroundBrush,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Opacity = .78,
                    };
                    host.Children.Add(badge);
                }

                Canvas.SetLeft(host, position.X - hostSize / 2);
                Canvas.SetTop(host, position.Y - hostSize / 2);
                ring.Children.Add(host);
            }

            if (!animate || rows.Length == 0 || appearance.Animation == RingAnimation.None)
                return;

            // The exact same whole-sheet storyboard is used by the live overlay: no sector gaps,
            // staggered button reveals or individual outward travel can return through a preset.
            var storyboard = RingOpenAnimation.BuildUnified(ring, appearance.AnimationDurationMs,
                kind: appearance.Animation);
            _playing = storyboard;
            _playingOn = canvas;
            storyboard.Completed += (s, e) =>
            {
                if (!ReferenceEquals(_playing, storyboard))
                    return;

                _playing = null;
                _playingOn = null;
            };
            storyboard.Begin();
        }

        private static Point At(double centre, double radius, double angle) =>
            new(centre + radius * Math.Cos(angle), centre + radius * Math.Sin(angle));

        private static Geometry Sector(double radius, double innerRadius, double angle)
        {
            double first = angle - Math.PI / 8;
            double last = angle + Math.PI / 8;
            var figure = new PathFigure { StartPoint = At(radius, radius, first), IsClosed = true, IsFilled = true };
            figure.Segments.Add(new ArcSegment
            {
                Point = At(radius, radius, last),
                Size = new Size(radius, radius),
                SweepDirection = SweepDirection.Clockwise,
            });
            figure.Segments.Add(new LineSegment { Point = At(radius, innerRadius, last) });
            figure.Segments.Add(new ArcSegment
            {
                Point = At(radius, innerRadius, first),
                Size = new Size(innerRadius, innerRadius),
                SweepDirection = SweepDirection.Counterclockwise,
            });
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            return geometry;
        }

        private static void AddCircle(Canvas canvas, double centre, double radius, Brush stroke, double thickness)
        {
            var circle = new Ellipse
            {
                Width = 2 * radius,
                Height = 2 * radius,
                Stroke = stroke,
                StrokeThickness = thickness,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(circle, centre - radius);
            Canvas.SetTop(circle, centre - radius);
            canvas.Children.Add(circle);
        }
    }
}
