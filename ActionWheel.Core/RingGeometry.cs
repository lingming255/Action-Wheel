using System;

namespace Action_Wheel.Core
{
    /// <summary>A rectangle in physical screen pixels - a monitor's work area.</summary>
    public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    /// <summary>A point in physical screen pixels.</summary>
    public readonly record struct ScreenPoint(int X, int Y);

    /// <summary>
    /// Where the overlay goes and which button a gesture is pointing at. Pure arithmetic, so the
    /// awkward cases - a cursor in the bottom-right corner, a 150% monitor, a work area that does
    /// not start at (0,0) because the taskbar is on the left - can be reasoned about without a
    /// second monitor plugged in.
    /// </summary>
    public static class RingGeometry
    {
        /// <summary>Child centre relative to the ring centre, in DIPs; shared by drawing and gestures.</summary>
        public static (double X, double Y) GroupChildOffset(int parentTag, int childIndex,
            double orbitRadius, double buttonSize)
        {
            double step = 2 * Math.Asin(Math.Clamp((buttonSize + 6) / (2 * orbitRadius), -1, 1));
            int fanOffset = childIndex % 2 == 0 ? -(childIndex / 2) : childIndex / 2 + 1;
            double angle = (parentTag - 1) * Math.PI / 4 - Math.PI / 2 + fanOffset * step;
            return (Math.Cos(angle) * orbitRadius, Math.Sin(angle) * orbitRadius);
        }

        /// <summary>Hit-tests the visible child circles using a screen displacement and monitor scale.</summary>
        public static int? GroupChildAt(double screenDx, double screenDy, double scale,
            int parentTag, int count, double orbitRadius, double buttonSize)
        {
            double x = screenDx / scale;
            double y = screenDy / scale;
            for (int i = 0; i < count; i++)
            {
                var centre = GroupChildOffset(parentTag, i, orbitRadius, buttonSize);
                double dx = x - centre.X, dy = y - centre.Y;
                if (dx * dx + dy * dy <= buttonSize * buttonSize / 4)
                    return i;
            }
            return null;
        }

        /// <summary>
        /// Menu size in DIPs at the default ring, and the smallest the overlay window ever gets.
        /// </summary>
        /// <remarks>
        /// No longer the only size. <see cref="MenuSizeFor"/> grows the window for larger rings;
        /// this is what it returns for the default 44/88, so the default overlay is byte-identical
        /// to the one that was hard-coded here before.
        /// </remarks>
        public const int MenuSizeDip = 420;

        /// <summary>Shared visual geometry used by both the live overlay and Settings preview.</summary>
        public const double OuterButtonSizeDip = 52.0;
        public const double CenterButtonSizeDip = 104.0;
        public const double OrbitRadiusDip = 116.0;

        public const double InnerRadiusDip = 58.0;
        public const double OuterRadiusDip = 174.0;
        public const double SelectedOffsetDip = 8.0;

        /// <summary>
        /// The centre button's diameter as a fraction of an outer one. Kept as a ratio rather than a
        /// second setting so one "button size" control moves the whole ring in proportion.
        /// </summary>
        public const double CenterButtonRatio = CenterButtonSizeDip / OuterButtonSizeDip;

        /// <summary>Glyph size as a fraction of the button it sits in - 14 DIP inside 44.</summary>
        public const double IconSizeRatio = 30.0 / OuterButtonSizeDip;

        /// <summary>How small and how large a user is allowed to make the buttons, in DIPs.</summary>
        public const double MinButtonSizeDip = 28.0;
        public const double MaxButtonSizeDip = 72.0;

        /// <summary>Clear space left between two neighbouring buttons at the tightest orbit.</summary>
        private const double NeighbourGapDip = 6.0;

        /// <summary>Clear space left between the centre button and an outer one at the tightest orbit.</summary>
        private const double CenterGapDip = 8.0;

        /// <summary>The PointerOver scale in RadialMenu.xaml. A hovered button must still fit.</summary>
        private const double HoverScale = 1.12;

        /// <summary>
        /// Clear space kept between the outermost hovered button and the window edge, in DIPs.
        /// </summary>
        /// <remarks>
        /// Not cosmetic slack - this is where the drop shadows are drawn, and the overlay window
        /// clips hard at its edge. The value is what the old fixed 400 DIP window happened to leave
        /// at the default ring (400/2 - 88 - 44*1.12/2), so <see cref="MenuSizeFor"/> returns
        /// exactly 400 there and nothing about the default overlay changes. Before the window could
        /// grow, this room shrank as the orbit widened, and at the largest settings the shadows were
        /// being cut off by the window edge.
        /// </remarks>
        private const double EdgeMarginDip = 64.88;

        /// <summary>
        /// The widest orbit offered, in DIPs. A flat cap rather than something derived from the
        /// window, because the window follows the ring now instead of bounding it.
        /// </summary>
        /// <remarks>
        /// Chosen for the screen rather than the geometry: 180 puts the overlay at roughly 615 DIP
        /// square with the largest buttons, which still fits the work area of a mainstream display
        /// at 100% and 125% scaling. At 150% on a short laptop screen the very largest combinations
        /// can exceed the work area, and <see cref="ClampOrigin"/> then pins the ring to the
        /// top-left and lets the bottom-right overflow - the deliberate failure documented there,
        /// which keeps the centre button under the cursor.
        /// </remarks>
        public const double MaxOrbitRadiusDip = 180.0;

        /// <summary>
        /// The tightest orbit that still leaves every button visibly separate, for a given button
        /// size. The two limits are independent, so both are checked and the larger wins.
        /// </summary>
        /// <remarks>
        /// Neighbours sit 45 degrees apart, so the straight-line distance between two of them is the
        /// chord 2*r*sin(22.5 degrees) - about 0.765*r, not r. Using r itself (the obvious guess)
        /// under-estimates by 30% and lets the ring overlap while the arithmetic says it does not.
        /// </remarks>
        public static double MinOrbitFor(double buttonSize)
        {
            double neighbours = (buttonSize + NeighbourGapDip) / (2.0 * Math.Sin(Math.PI / 8.0));
            double centre = (buttonSize + buttonSize * CenterButtonRatio) / 2.0 + CenterGapDip;
            return Math.Ceiling(Math.Max(neighbours, centre));
        }

        /// <summary>
        /// The widest orbit offered for a given button size.
        /// </summary>
        /// <remarks>
        /// The button size no longer enters into it: this used to be whatever was left inside a
        /// fixed 400 DIP window after the buttons had taken their share, so bigger buttons meant a
        /// tighter ceiling and, at the ceiling, clipped shadows. The window is derived from the ring
        /// now, so the only remaining limit is how large a ring is still usable - a flat
        /// <see cref="MaxOrbitRadiusDip"/>. The parameter stays so callers and the settings binding
        /// do not have to change, and so the pair of limits still reads as a pair.
        /// </remarks>
        public static double MaxOrbitFor(double buttonSize) => MaxOrbitRadiusDip;

        /// <summary>
        /// The overlay window's size in DIPs for a given ring: large enough that a hovered button at
        /// full orbit still has <see cref="EdgeMarginDip"/> of room for its shadow, and never
        /// smaller than <see cref="MenuSizeDip"/>.
        /// </summary>
        /// <remarks>
        /// The floor is not just nostalgia for 400. The window is also the region that counts as
        /// "inside the menu" for hit-testing, and shrinking it around a small ring would move the
        /// boundary between "clicked the menu's empty space" and "clicked another application"
        /// inward, changing what a click just outside the buttons does.
        /// </remarks>
        public static int MenuSizeFor(double buttonSize, double orbitRadius) => Math.Max(
            MenuSizeDip,
            (int)Math.Ceiling(2.0 * (orbitRadius + buttonSize * HoverScale / 2.0 + EdgeMarginDip)));


        /// <summary>
        /// How far the cursor must travel, in physical pixels, before a middle-button drag counts as
        /// choosing a direction. Below this the release is treated as a plain click that opened the
        /// menu, and the menu stays up to be clicked normally.
        /// </summary>
        public const double GestureDeadZone = InnerRadiusDip;

        /// <summary>
        /// How long a button must stay pressed - or stay the flick gesture's chosen direction -
        /// before its hold action fires instead of its click action. Shared by the direct-click path
        /// (<c>RadialMenu</c>) and the drag/flick path (<c>LauncherService</c>) so holding feels the
        /// same regardless of which gesture opened the ring. Comfortably clears a quick click or a
        /// decisive flick - this app is tuned around a ~15 ms click-to-first-pixel and even a
        /// deliberate flick rarely takes this long - while still firing promptly once crossed.
        /// </summary>
        public const int HoldThresholdMs = 450;

        /// <summary>Scale factor for a monitor's DPI. 96 DPI is 100%.</summary>
        /// <remarks>Takes a double so GetDpiForWindow's uint passes straight in.</remarks>
        public static double ScaleFromDpi(double dpi) => dpi > 0 ? dpi / 96.0 : 1.0;

        /// <summary>
        /// The overlay's size in physical pixels. AppWindow.Resize/Move work in physical pixels
        /// while the XAML layout is in DIPs; at 125% or 150% a window sized in DIPs is smaller than
        /// its own content and the ring gets clipped at the edges.
        /// </summary>
        public static int PhysicalMenuSize(double scale, int dipSize = MenuSizeDip) =>
            (int)Math.Round(dipSize * scale);

        /// <summary>
        /// Top-left corner for a menu of <paramref name="size"/> physical pixels centred on
        /// <paramref name="cursor"/>, pulled back inside <paramref name="work"/> so no part of the
        /// ring lands off-screen or under the taskbar.
        /// </summary>
        /// <remarks>
        /// The clamps run bottom/right first and top/left second, which matters when the menu is
        /// larger than the work area: the second pair wins, so the menu is pinned to the top-left of
        /// the monitor and the part that overflows is the bottom-right. That is the better failure -
        /// the centre button, the one under the cursor, stays reachable.
        /// </remarks>
        public static ScreenPoint ClampOrigin(ScreenPoint cursor, int size, ScreenRect work)
        {
            double half = size / 2.0;
            int left = (int)Math.Round(cursor.X - half);
            int top = (int)Math.Round(cursor.Y - half);

            if (left + size > work.Right) left = work.Right - size;
            if (top + size > work.Bottom) top = work.Bottom - size;
            if (left < work.Left) left = work.Left;
            if (top < work.Top) top = work.Top;

            return new ScreenPoint(left, top);
        }

        /// <summary>True when a screen point falls inside a menu placed at <paramref name="origin"/>.</summary>
        public static bool Contains(ScreenPoint origin, int size, ScreenPoint point) =>
            point.X >= origin.X && point.X < origin.X + size
            && point.Y >= origin.Y && point.Y < origin.Y + size;

        /// <summary>
        /// Which button a drag away from the menu centre is pointing at, or null inside the dead
        /// zone. Angles are measured from straight up and increase clockwise, matching the layout of
        /// Btn1..Btn8; screen Y grows downwards, hence the negated dy.
        /// </summary>
        public static int? TagForDirection(double dx, double dy)
        {
            if (Math.Sqrt(dx * dx + dy * dy) < GestureDeadZone)
                return null;

            double degrees = Math.Atan2(dx, -dy) * 180.0 / Math.PI;
            if (degrees < 0)
                degrees += 360.0;

            // Round to the nearest eighth; 8 wraps back to straight up, which is tag 1.
            return (int)Math.Round(degrees / 45.0) % 8 + 1;
        }
    }
}
