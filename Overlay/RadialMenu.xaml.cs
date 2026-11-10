using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Action_Wheel.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;
using Windows.Graphics;
using Windows.Foundation;
using WinRT.Interop;
// Alias rather than "using Windows.UI": both Microsoft.UI and Windows.UI declare a Colors class,
// and importing the whole namespace makes every Colors.X reference ambiguous.
using Color = Windows.UI.Color;

namespace Action_Wheel.Overlay
{
    public sealed partial class RadialMenu : Window
    {
        #region Win32 API
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        // Frame bits. OverlappedPresenter.SetBorderAndTitleBar(false, false) leaves WS_DLGFRAME and
        // WS_EX_WINDOWEDGE behind, and those are what DWM draws the visible outline from.
        private const int WS_BORDER = 0x00800000;
        private const int WS_DLGFRAME = 0x00400000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_CAPTION = WS_BORDER | WS_DLGFRAME;
        private const int WS_EX_DLGMODALFRAME = 0x00000001;
        private const int WS_EX_CLIENTEDGE = 0x00000200;
        private const int WS_EX_STATICEDGE = 0x00020000;
        private const int WS_EX_WINDOWEDGE = 0x00000100;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
        private const int SW_HIDE = 0;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_DONOTROUND = 1;

        [DllImport("dwmapi.dll")]
        private static extern int DwmEnableBlurBehindWindow(IntPtr hWnd, ref DWM_BLURBEHIND blurBehind);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern int FillRect(IntPtr hDC, ref RECT rect, IntPtr brush);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateSolidBrush(uint colorRef);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        private delegate IntPtr WindowSubclassProc(
            IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr subclassId, UIntPtr referenceData);

        [DllImport("comctl32.dll")]
        private static extern bool SetWindowSubclass(
            IntPtr hWnd, WindowSubclassProc callback, UIntPtr subclassId, UIntPtr referenceData);

        [DllImport("comctl32.dll")]
        private static extern bool RemoveWindowSubclass(
            IntPtr hWnd, WindowSubclassProc callback, UIntPtr subclassId);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct DWM_BLURBEHIND
        {
            public uint dwFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fEnable;
            public IntPtr hRgnBlur;
            [MarshalAs(UnmanagedType.Bool)] public bool fTransitionOnMaximized;
        }
        private const uint DWM_BB_ENABLE = 0x1;
        private const uint WM_ERASEBKGND = 0x0014;
        private static readonly UIntPtr BackgroundSubclassId = new(1);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("Shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
        private const int MDT_EFFECTIVE_DPI = 0;

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_FRAMECHANGED = 0x0020;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; public POINT(int X, int Y) { x = X; y = Y; } }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        private const uint MONITOR_DEFAULTTONEAREST = 2;
        #endregion

        private AppWindow? _appWindow;
        private readonly WindowSubclassProc _backgroundSubclass;
        private IntPtr _backgroundBrush;
        private IntPtr _windowHandle;

        /// <summary>
        /// Menu size in DIPs. Derived from the ring rather than fixed at 400: a wide
        /// orbit needs a wider window, and the XAML's 400 is only the default that ApplySizes
        /// overwrites on MenuContainer and ButtonsCanvas when this differs.
        /// </summary>
        /// <remarks>
        /// When any button is a <see cref="ActionKind.Group"/> with children, the window has to be
        /// sized for <see cref="GroupOrbitRadius"/> - the outer, concentric ring those children
        /// appear on - from construction, not resized later when a group actually expands. Resizing
        /// an already-shown, already-positioned overlay mid-gesture is the kind of thing this
        /// project's own history (see RadialMenu's other remarks on window sizing) says to avoid;
        /// sizing once for the largest possible content and leaving the satellite buttons collapsed
        /// until needed costs nothing since <see cref="RingGeometry.MenuSizeFor"/> is only asked for
        /// this once, at most, per action set - not per frame.
        /// </remarks>
        private int MenuSize => HasGroups
            ? RingGeometry.MenuSizeFor(_appearance.ButtonSize, GroupOrbitRadius)
            : _appearance.MenuSize;

        /// <summary>True when at least one button reveals a satellite ring when selected.</summary>
        private bool HasGroups => _actions.Any(a => a.Kind == ActionKind.Group && a.GroupChildren.Count > 0);

        /// <summary>
        /// Distance from the ring's centre to a group's satellite buttons - outside the main orbit by
        /// a full button's width plus a gap, so the two rings never touch.
        /// </summary>
        private double GroupOrbitRadius => _appearance.OrbitRadius + _appearance.ButtonSize + 24.0;

        /// <summary>Diameter of a satellite button - the same as the main ring's own buttons.</summary>
        private double GroupButtonSize => _appearance.ButtonSize;

        /// <summary>
        /// Button diameters, orbit radius and opening animation, all of them user settings.
        /// </summary>
        /// <remarks>
        /// Already normalised by the time it arrives - <see cref="RingAppearance.Normalised"/> is the
        /// one place that knows an orbit's legal range depends on the button size, and it runs on
        /// load, on save and on the constructor's fallback. Nothing here re-checks the numbers.
        /// </remarks>
        private readonly RingAppearance _appearance;

        /// <summary>
        /// The DPI scale of the monitor the menu is on. AppWindow.Resize/Move work in physical
        /// pixels, while XAML layout (and every hit-test calculation below) works in DIPs. At 100%
        /// scale the two units coincide, but at 125%/150% failing to multiply by this factor leaves
        /// the physical window smaller than the XAML content, clipping the menu's edge.
        /// </summary>
        private double _rasterizationScale = 1.0;

        /// <summary>Window size in physical pixels.</summary>
        private int PhysicalMenuSize => RingGeometry.PhysicalMenuSize(_rasterizationScale, MenuSize);

        private readonly IReadOnlyList<ActionItem> _actions;

        /// <summary>The window's position on screen, used to tell whether a click landed on the menu.</summary>
        private PointInt32 _screenOrigin;

        /// <summary>
        /// Raised when one of the buttons is clicked. The menu closes itself first, so the handler
        /// runs with the previous foreground window already back on top.
        /// </summary>
        public event EventHandler<ActionItem>? ActionInvoked;

        public RadialMenu(IReadOnlyList<ActionItem>? actions = null, RingAppearance? appearance = null)
        {
            _actions = actions ?? ActionConfig.Defaults();
            _appearance = (appearance ?? RingAppearance.Default).Normalised();
            _backgroundSubclass = TransparentWindowProc;

            InitializeComponent();
            InitializeWindow();

            _holdTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _holdTimer.IsRepeating = false;
            _holdTimer.Tick += (s, e) => FireHold();

            RootGrid.PointerPressed += RootGrid_PointerPressed;
            RootGrid.Loaded += RadialMenu_Loaded;
            Closed += RadialMenu_Closed;
        }

        /// <summary>
        /// Set once the window has closed, so work queued while it was open stops before touching
        /// a visual tree that is no longer attached to anything.
        /// </summary>
        private bool _closed;
        private EventHandler<object>? _refreshOnRendering;
        private DispatcherQueueTimer? _refreshTimer;
        private RefreshPlayback? _refreshPlayback;

        /// <summary>
        /// The one path that ever closes this window - a button click, a click elsewhere on the
        /// menu, or <see cref="LauncherService"/> dismissing it (Esc, a click off the menu
        /// entirely, or a new menu replacing this one).
        /// </summary>
        /// <remarks>
        /// Hides the native window first and hands off to WinUI's own <see cref="Close"/> second,
        /// deliberately in that order. <see cref="TransparentSystemBackdrop.OnTargetDisconnected"/>
        /// sets SystemBackdrop back to null as part of that teardown, and for at least one frame
        /// before the HWND is actually destroyed the swap chain reverts to its opaque default -
        /// black, since RootGrid/MenuContainer are XAML "Transparent" with no backdrop behind them
        /// at that instant - stretched across the full 400x400 window. That is the flash: sharply
        /// visible against a light desktop, easy to miss against a dark one, and it landed on every
        /// close, which for most users is every single click of a button. ShowWindow(SW_HIDE) is
        /// synchronous at the OS level, so the window is off-screen before Close() ever starts
        /// tearing the backdrop down, and the repaint - if it still happens - happens on nothing
        /// anyone can see.
        /// </remarks>
        public void CloseMenu()
        {
            if (_windowHandle != IntPtr.Zero)
                ShowWindow(_windowHandle, SW_HIDE);

            Close();
        }

        private void RadialMenu_Closed(object sender, WindowEventArgs args)
        {
            _closed = true;
            _holdTimer.Stop();
            // Rendering is static. If a refresh is cancelled before its three-frame handoff,
            // there may be no next frame to detach it and release all its prepared bitmaps.
            if (_refreshOnRendering != null)
            {
                CompositionTarget.Rendering -= _refreshOnRendering;
                _refreshOnRendering = null;
            }
            _refreshPlayback?.Dispose();
            _refreshPlayback = null;
            _refreshTimer?.Stop();
            _refreshTimer = null;

            if (_windowHandle != IntPtr.Zero)
            {
                RemoveWindowSubclass(_windowHandle, _backgroundSubclass, BackgroundSubclassId);
                _windowHandle = IntPtr.Zero;
            }

            if (_backgroundBrush != IntPtr.Zero)
            {
                DeleteObject(_backgroundBrush);
                _backgroundBrush = IntPtr.Zero;
            }
        }

        private void RadialMenu_Loaded(object? sender, RoutedEventArgs e)
        {
            // Sizes before positions: UpdateButtonPositions places each button by its centre, so it
            // has to subtract a radius that is already the one the button will actually be drawn at.
            ApplySizes();
            UpdateButtonPositions(_appearance.OrbitRadius);
            BuildWedges();
            ApplyActions();
            ApplyShadows();

            var storyboard = RingOpenAnimation.Build(
                _appearance.Animation, MenuContainer, AllButtons, RingMetrics.For(_appearance));

            if (storyboard == null)
            {
                // "None" leaves nothing to wait for, so the settling pass runs straight away.
                SettleAfterOpening();
                return;
            }

            storyboard.Completed += (s, args) => SettleAfterOpening();
            storyboard.Begin();
        }

        /// <summary>
        /// Applies the user's button size to every button and to the icon inside it.
        /// </summary>
        /// <remarks>
        /// Written as local values on each Button rather than by rewriting the Style: a Style setter
        /// is shared by all eight outer buttons *and* is outranked by a local value anyway, so the
        /// XAML keeps 44/22 as the defaults and this overrides them. The ControlTemplate reads all
        /// three back through TemplateBinding, which is why the shadow host and the corner radius
        /// follow along without being touched here.
        ///
        /// Both halves are skipped at their default - the overwhelmingly common case - so the usual
        /// middle click does not pay for property writes that would set the values already there.
        /// ApplyShadows and the icons still read from the same appearance either way.
        /// </remarks>
        private void ApplySizes()
        {
            // The container and canvas are declared 400 square in XAML and the window is sized from
            // the same number, so these two have to move together or the ring is laid out inside a
            // surface that is the wrong size and gets clipped or floats off-centre.
            if (MenuSize != RingGeometry.MenuSizeDip)
            {
                MenuContainer.Width = MenuSize;
                MenuContainer.Height = MenuSize;
                ButtonsCanvas.Width = MenuSize;
                ButtonsCanvas.Height = MenuSize;
            }

            if (_appearance.ButtonSize == RingGeometry.OuterButtonSizeDip)
                return;

            var buttons = AllButtons;
            for (int tag = 0; tag < buttons.Length; tag++)
            {
                double diameter = tag == 0 ? _appearance.CenterButtonSize : _appearance.ButtonSize;
                buttons[tag].Width = diameter;
                buttons[tag].Height = diameter;
                buttons[tag].CornerRadius = new CornerRadius(diameter / 2.0);
            }
        }

        /// <summary>
        /// The final layout pass once the ring has finished arriving, whether it animated or not.
        /// </summary>
        /// <remarks>
        /// The guard is not theoretical. A drag gesture ends on the middle button coming back up,
        /// which is routinely under the ~190 ms the storyboard runs for, so Close() lands in the
        /// middle of the animation and Completed still fires afterwards - at which point this would
        /// be calling UpdateLayout on a tree that has been detached from its XamlRoot. Whether that
        /// throws is not worth finding out from a crash report: this app runs in the background, so
        /// an exception on the UI thread is a process that quietly vanishes with nothing on screen
        /// to say why.
        /// </remarks>
        private void SettleAfterOpening()
        {
            if (_closed)
                return;

            // Force one final layout pass after the animation so scaled buttons render sharply.
            MenuContainer.InvalidateArrange();
            MenuContainer.InvalidateMeasure();
            MenuContainer.UpdateLayout();

            // Pin every button to its final opacity in case composition rounded an animated value.
            foreach (var button in AllButtons)
                button.Opacity = 1.0;
        }

        /// <summary>
        /// Gives every button a drop shadow. Without it the white buttons all but disappear against
        /// a white window underneath - and the overlay is transparent, so whatever is underneath is
        /// exactly what the user sees.
        /// </summary>
        /// <remarks>
        /// Blur and offset are used exactly as configured, and deliberately do NOT scale with the
        /// button size, which was considered and rejected. A drop shadow describes how far the
        /// element sits above the surface and where the light is - neither of which changes because
        /// the element got bigger - and every mainstream design system keeps elevation shadows
        /// absolute for that reason. Scaling them would also mean the numbers in Settings no longer
        /// meant DIPs. The visible consequence is that a shadow reads as proportionally lighter on
        /// a 72 DIP button than on a 28 DIP one, which is what it should look like.
        /// </remarks>
        private void ApplyShadows()
        {
            var buttons = AllButtons;
            var byTag = ActionsByTag;

            for (int tag = 0; tag < buttons.Length; tag++)
            {
                // The template is applied by the time Loaded has run, so the part is there to find.
                var host = ButtonShadow.FindTemplatePart(buttons[tag], "ShadowHost");
                if (host == null)
                    continue;

                var action = byTag[tag];
                if (action?.ShadowEnabled == false)
                    continue;

                var color = IconFactory.ColorOr(action?.Shadow ?? string.Empty, Colors.Black);
                double diameter = tag == 0 ? _appearance.CenterButtonSize : _appearance.ButtonSize;
                ButtonShadow.Apply(host, diameter, color,
                    action?.ShadowOpacity ?? 0.45, action?.ShadowBlur ?? 11,
                    action?.ShadowOffsetX ?? 0, action?.ShadowOffsetY ?? 3);
            }
        }

        /// <summary>
        /// All the buttons, in Tag order 0-8 (0 is the centre button). Cached: this used to be an
        /// expression-bodied property, so every read - several per menu, and once per button inside
        /// ApplyShadows' own loop over it - allocated a fresh nine-element array.
        /// </summary>
        private Button[] AllButtons => _allButtons ??=
            new[] { CenterBtn, Btn1, Btn2, Btn3, Btn4, Btn5, Btn6, Btn7, Btn8 };

        private Button[]? _allButtons;

        private readonly XamlPath?[] _wedges = new XamlPath?[9];

        private void BuildWedges()
        {
            double centre = MenuSize / 2.0;

            for (int tag = 1; tag <= 8; tag++)
            {
                if (ActionsByTag[tag] == null)
                {
                    AllButtons[tag].Visibility = Visibility.Collapsed;
                    continue;
                }

                double middle = -90.0 + (tag - 1) * 45.0;
                var wedge = new XamlPath
                {
                    Data = CreateWedgeGeometry(centre, middle - 21.5, middle + 21.5),
                    StrokeThickness = 1,
                    Tag = tag.ToString(),
                };

                wedge.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler(OnButtonHover), true);
                wedge.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnButtonHover), true);
                wedge.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnButtonPressed), true);
                wedge.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler(OnButtonRest), true);
                wedge.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnButtonRest), true);
                wedge.Tapped += (_, _) => InvokeTag(tag);

                Canvas.SetZIndex(wedge, -1);
                ButtonsCanvas.Children.Insert(0, wedge);
                _wedges[tag] = wedge;
            }
        }

        private static PathGeometry CreateWedgeGeometry(double centre, double startDegrees, double endDegrees)
        {
            static Point Polar(double centre, double radius, double degrees)
            {
                double radians = degrees * Math.PI / 180.0;
                return new Point(centre + Math.Cos(radians) * radius, centre + Math.Sin(radians) * radius);
            }

            var figure = new PathFigure
            {
                StartPoint = Polar(centre, RingGeometry.OuterRadiusDip, startDegrees),
                IsClosed = true,
            };
            figure.Segments.Add(new ArcSegment
            {
                Point = Polar(centre, RingGeometry.OuterRadiusDip, endDegrees),
                Size = new Size(RingGeometry.OuterRadiusDip, RingGeometry.OuterRadiusDip),
                SweepDirection = SweepDirection.Clockwise,
            });
            figure.Segments.Add(new LineSegment
            {
                Point = Polar(centre, RingGeometry.InnerRadiusDip, endDegrees),
            });
            figure.Segments.Add(new ArcSegment
            {
                Point = Polar(centre, RingGeometry.InnerRadiusDip, startDegrees),
                Size = new Size(RingGeometry.InnerRadiusDip, RingGeometry.InnerRadiusDip),
                SweepDirection = SweepDirection.Counterclockwise,
            });

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            return geometry;
        }

        /// <summary>The action for a tag, or null. Indexed once instead of scanning the list per button.</summary>
        private ActionItem?[] ActionsByTag => _actionsByTag ??= BuildActionIndex();

        private ActionItem?[]? _actionsByTag;

        private ActionItem?[] BuildActionIndex()
        {
            var index = new ActionItem?[9];
            foreach (var action in _actions)
            {
                if (action.Tag is >= 0 and <= 8)
                    index[action.Tag] ??= action;
            }

            return index;
        }

        /// <summary>Draws a button's rest/highlighted state.</summary>
        private sealed record ButtonPainter(Action Rest, Action Hover, Action Pressed);

        /// <summary>Each button's painter, indexed by Tag.</summary>
        private readonly ButtonPainter?[] _buttonPainters = new ButtonPainter?[9];

        /// <summary>The button the drag gesture is currently highlighting, null if none.</summary>
        private int? _highlightedTag;

        /// <summary>
        /// Fires <see cref="ActionItem.HoldKind"/> for whichever tag is currently pressed, once it
        /// has been held past <see cref="RingGeometry.HoldThresholdMs"/>. One timer is enough - only
        /// one button can be under a pointer press at a time.
        /// </summary>
        private readonly DispatcherQueueTimer _holdTimer;


        /// <summary>
        /// Highlights the button the drag gesture is pointing at, or clears the highlight when
        /// <paramref name="tag"/> is null. Separate from the pointer-over path: during a gesture the
        /// cursor is out in empty space, nowhere near the button being chosen.
        /// </summary>
        public void HighlightTag(int? tag)
        {
            if (_highlightedTag == tag)
                return;

            if (_highlightedTag is int previous)
                _buttonPainters[previous]?.Rest();

            _highlightedTag = tag;

            if (tag is int current)
            {
                _buttonPainters[current]?.Hover();
                ShowSelectionInfo(current);
                OnMainRingHover(current);
            }
            else
            {
                HideSelectionInfo();
            }
        }

        private Button? _highlightedChild;
        private bool _trackingGesture;

        private (double X, double Y, double Scale) GestureOffset(PointInt32 point)
        {
            double scale = RootGrid.XamlRoot?.RasterizationScale ?? _rasterizationScale;
            return (point.X - _screenOrigin.X - MenuSize * scale / 2,
                point.Y - _screenOrigin.Y - MenuSize * scale / 2, scale);
        }

        private Button? GroupChildAt(PointInt32 point)
        {
            if (_expandedGroupTag is not int parent || !_groupButtons.TryGetValue(parent, out var buttons))
                return null;
            var offset = GestureOffset(point);
            int? child = RingGeometry.GroupChildAt(offset.X, offset.Y, offset.Scale,
                parent, buttons.Length, GroupOrbitRadius, GroupButtonSize);
            return child is int i ? buttons[i] : null;
        }

        private void HighlightChild(Button? button)
        {
            if (_highlightedChild == button) return;
            DisarmHold();
            if (_highlightedChild is Button previous)
            {
                previous.RenderTransform = null;
                previous.BorderThickness = new Thickness(0);
                VisualStateManager.GoToState(previous, "Normal", true);
            }
            _highlightedChild = button;
            if (button == null) return;
            // The trigger's events are swallowed by MouseHook; do not rely on WinUI hover routing.
            button.RenderTransformOrigin = new Point(0.5, 0.5);
            button.RenderTransform = new ScaleTransform { ScaleX = 1.1, ScaleY = 1.1 };
            button.BorderThickness = new Thickness(2);
            button.BorderBrush = new SolidColorBrush(Colors.White);
            if (button.Tag is string text && TryParseChildTag(text, out int parent, out int child))
                ShowSelectionInfo(ResolveGroupChild(parent, child));
        }

        public void UpdateGesture(PointInt32 point, int? mainTag, bool allowHold)
        {
            if (_closed) return;
            _trackingGesture = true;
            var child = GroupChildAt(point);
            bool changed = _highlightedChild != child;
            if (child != null)
            {
                HighlightTag(null);
                HighlightChild(child);
                if (changed && allowHold && child.Tag is string text
                    && TryParseChildTag(text, out int parent, out int index)
                    && ResolveGroupChild(parent, index) is ActionItem action)
                    ArmChildHold(parent, index, action, child);
                return;
            }
            HighlightChild(null);
            var offset = GestureOffset(point);
            double distance = Math.Sqrt(offset.X * offset.X + offset.Y * offset.Y) / offset.Scale;
            // Moving between satellites crosses main-ring sectors. Keep the group open until
            // the pointer returns to the main ring, including while passing through child gaps.
            if (_expandedGroupTag != null && distance > (_appearance.OrbitRadius + GroupOrbitRadius) / 2)
            {
                HighlightTag(null);
                HideSelectionInfo();
                DisarmHold();
                return;
            }
            bool mainChanged = _highlightedTag != mainTag || changed;
            HighlightTag(mainTag);
            if (mainChanged)
            {
                if (allowHold && mainTag is int tag) ArmHold(tag);
                else DisarmHold();
            }
        }

        public bool TryInvokeGroupChildAt(PointInt32 point)
        {
            _trackingGesture = false;
            DisarmHold();
            if (_closed) return false;
            if (GroupChildAt(point) is Button child)
            {
                GroupChildButton_Click(child, new RoutedEventArgs());
                return true;
            }
            var offset = GestureOffset(point);
            double distance = Math.Sqrt(offset.X * offset.X + offset.Y * offset.Y) / offset.Scale;
            if (_expandedGroupTag != null && distance > (_appearance.OrbitRadius + GroupOrbitRadius) / 2)
            {
                // Releasing in a gap cancels, instead of firing an unrelated underlying main sector.
                CloseMenu();
                return true;
            }
            return false;
        }

        private void ShowSelectionInfo(int tag)
        {
            ShowSelectionInfo(tag is >= 0 and <= 8 ? ActionsByTag[tag] : null);
        }

        private void ShowSelectionInfo(ActionItem? action)
        {
            if (action == null)
            {
                HideSelectionInfo();
                return;
            }

            bool copy = action.Kind == ActionKind.CopyText;
            SelectionTitle.MaxLines = copy ? 4 : 1;
            SelectionTitle.TextWrapping = copy ? TextWrapping.Wrap : TextWrapping.NoWrap;
            SelectionTitle.FontSize = copy ? 11 : 13;
            SelectionTitle.Text = copy ? action.Value : Truncate(action.Label, 18);
            SelectionMeta.Text = action.Kind switch
            {
                ActionKind.Keys => action.Value,
                ActionKind.Launch => "OPEN",
                ActionKind.Function => "SYSTEM",
                ActionKind.Group => "MORE",
                ActionKind.CopyText => "松手复制",
                _ => string.Empty,
            };
            CenterInfo.Visibility = Visibility.Visible;
        }

        private void HideSelectionInfo() => CenterInfo.Visibility = Visibility.Collapsed;

        private static string Truncate(string value, int max) =>
            value.Length <= max ? value : value[..(max - 1)] + "…";

        /// <summary>
        /// Opens <paramref name="tag"/>'s satellite ring the moment it is hovered, whether that hover
        /// came from a direct pointer entering the button (<see cref="OnButtonHover"/>) or the
        /// flick/drag gesture highlighting a direction (<see cref="HighlightTag"/>, driven by
        /// <c>LauncherService</c>). Hovering back over the same parent, or into the satellites it
        /// revealed, is a no-op (satellites carry a "parent:child" string <see cref="Tag"/>, not a
        /// plain 0-8 int, so <see cref="TagOf"/> never resolves one of them to a main-ring tag and
        /// this method is never even called for them). Hovering any *other* main-ring button - the
        /// centre included - closes whatever group is currently open before possibly opening a new
        /// one, since that is the one thing this method is always called for.
        /// </summary>
        private void OnMainRingHover(int tag)
        {
            if (_expandedGroupTag is int expanded && expanded != tag)
                CollapseGroup();

            var action = tag is >= 0 and <= 8 ? ActionsByTag[tag] : null;
            if (action?.Kind == ActionKind.Group)
                ExpandGroup(tag, action);
        }

        /// <summary>
        /// What <see cref="FireHold"/> runs when the countdown elapses. An <see cref="Action"/>
        /// rather than a bare tag: a main-ring hold and a group child's hold resolve completely
        /// differently (<see cref="InvokeTag"/> vs <see cref="InvokeGroupChildHold"/>), and unifying
        /// them behind one delegate is what let both share a single timer and a single visual
        /// indicator instead of duplicating the arm/disarm/fire plumbing per case.
        /// </summary>
        private Action? _heldAction;

        /// <summary>
        /// Starts (or restarts) the hold countdown for the main ring's <paramref name="tag"/>, or
        /// does nothing if that button has no hold action configured - the common case, and the one
        /// that must cost nothing: no timer ever runs for a button nobody set one up on.
        /// </summary>
        private void ArmHold(int tag)
        {
            var action = tag is >= 0 and <= 8 ? ActionsByTag[tag] : null;
            if (action == null || action.HoldKind == ActionKind.None)
            {
                DisarmHold();
                return;
            }

            ArmHoldCore(() => InvokeTag(tag, hold: true), AllButtons[tag],
                tag == 0 ? _appearance.CenterButtonSize : _appearance.ButtonSize);
        }

        /// <summary>The same countdown, for a group's satellite button - see <see cref="ArmHold"/>.</summary>
        private void ArmChildHold(int parentTag, int childIndex, ActionItem child, Button button)
        {
            if (child.HoldKind == ActionKind.None)
            {
                DisarmHold();
                return;
            }

            ArmHoldCore(() => InvokeGroupChildHold(parentTag, childIndex), button, GroupButtonSize);
        }

        private void ArmHoldCore(Action onFire, FrameworkElement target, double diameter)
        {
            DisarmHold();
            _heldAction = onFire;
            StartHoldVisual(target, diameter);
            _holdTimer.Interval = TimeSpan.FromMilliseconds(RingGeometry.HoldThresholdMs);
            _holdTimer.Start();
        }

        /// <summary>
        /// Cancels the hold countdown. Called on every release/exit/cancel a button can report, not
        /// only the one that mattered - stopping an already-stopped timer is a no-op, and that is
        /// cheaper than working out in advance which of those four events is the "real" one.
        /// </summary>
        private void DisarmHold()
        {
            _heldAction = null;
            _holdTimer.Stop();
            StopHoldVisual();
        }

        /// <summary>
        /// The hold countdown elapsed while still pressed. Fires now, preemptively, rather than
        /// waiting for release: WinUI's Button raises Click on release regardless of how long the
        /// press lasted, so waiting would still need Click suppressed afterwards, which is not
        /// reliably possible from a handler that only sees the event after ButtonBase's own Click
        /// decision is already made. Firing here instead means the button - and the window - are
        /// gone by the time the user actually lifts the mouse, so there is nothing left for Click to
        /// fire on. See InvokeTag's own <c>_closed</c> guard for the belt-and-braces version of the
        /// same reasoning.
        /// </summary>
        private void FireHold()
        {
            if (_closed || _heldAction is not Action fire)
                return;

            _heldAction = null;
            StopHoldVisual();
            fire();
        }

        /// <summary>
        /// The ring displayed while a button counts down to its hold action, so holding reads as
        /// "something is about to happen" instead of an ordinary press that never lets go. One
        /// instance, moved to wherever the current hold is - only one button can be held at a time,
        /// on either the main ring or a group's satellite ring.
        /// </summary>
        private ProgressRing? _holdIndicator;

        private ProgressRing EnsureHoldIndicator()
        {
            if (_holdIndicator != null)
                return _holdIndicator;

            _holdIndicator = new ProgressRing
            {
                IsIndeterminate = false,
                Minimum = 0,
                Maximum = 1,
                Foreground = new SolidColorBrush(Colors.White),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            Canvas.SetZIndex(_holdIndicator, 1000);
            ButtonsCanvas.Children.Add(_holdIndicator);
            return _holdIndicator;
        }

        private void StartHoldVisual(FrameworkElement target, double diameter)
        {
            var ring = EnsureHoldIndicator();
            double size = diameter + 12;
            ring.Width = size;
            ring.Height = size;
            Canvas.SetLeft(ring, Canvas.GetLeft(target) - (size - diameter) / 2.0);
            Canvas.SetTop(ring, Canvas.GetTop(target) - (size - diameter) / 2.0);
            ring.Value = 0;
            ring.Visibility = Visibility.Visible;

            var animation = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(RingGeometry.HoldThresholdMs)),
            };
            Storyboard.SetTarget(animation, ring);
            Storyboard.SetTargetProperty(animation, "Value");

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        private void StopHoldVisual()
        {
            if (_holdIndicator != null)
                _holdIndicator.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Lets <see cref="LauncherService"/> drive the same hold countdown and indicator for the
        /// flick/drag path, which never touches a Button directly - the gesture is resolved from
        /// raw cursor movement against <see cref="RingGeometry.TagForDirection"/>, entirely outside
        /// WinUI's own pointer routing.
        /// </summary>
        public void ShowHoldProgress(int tag) => ArmHold(tag);

        public void HideHoldProgress() => DisarmHold();

        /// <summary>
        /// Runs the action for a tag as if its button had been clicked or held. Used to complete a
        /// drag gesture, where there is no click to route through the button itself.
        /// </summary>
        /// <param name="hold">
        /// True when the tag was chosen by holding past <see cref="RingGeometry.HoldThresholdMs"/>
        /// rather than a quick click or flick. Runs the button's <see cref="ActionItem.HoldKind"/>
        /// action instead of its primary one - substituted onto a copy of the same
        /// <see cref="ActionItem"/> so <see cref="ActionInvoked"/> and everything downstream of it
        /// (ActionDispatcher, the log line on failure) still only ever has one shape of thing to
        /// dispatch. A tag with no hold action configured does nothing rather than falling back to
        /// the primary one - see <see cref="ActionItem.HoldKind"/>'s remarks.
        /// </param>
        public void InvokeTag(int tag, bool hold = false)
        {
            // A hold firing preemptively (see FireHold) already closed the menu once; guards a
            // native Click that still lands afterwards, when the user physically releases the mouse
            // some time after the hold already ran, from invoking the primary action a second time
            // on top of it.
            if (_closed)
                return;

            DisarmHold();
            var action = tag is >= 0 and <= 8 ? ActionsByTag[tag] : null;

            // A group reveals its children instead of dispatching anything; it never closes the
            // menu, whether it was reached by a click, a flick-release, or (see the hold branch
            // below, which never substitutes a Group action) neither. Checked before the hold
            // substitution because holding a group button - with no hold action of its own
            // configured - would otherwise fall through to "no action" below and silently do
            // nothing, which reads as broken rather than as the button working normally.
            if (action?.Kind == ActionKind.Group && (!hold || action.HoldKind == ActionKind.None))
            {
                ExpandGroup(tag, action);
                return;
            }

            if (hold)
            {
                action = action != null && action.HoldKind != ActionKind.None
                    ? action with { Kind = action.HoldKind, Value = action.HoldValue, Arguments = action.HoldArguments }
                    : null;
            }

            // Close first: the action synthesises keystrokes for whatever is underneath, and the
            // overlay must be out of the way before they land.
            CloseMenu();

            if (action != null)
                ActionInvoked?.Invoke(this, action);
        }

        /// <summary>Satellite buttons built so far, keyed by the group's own tag, kept collapsed until expanded.</summary>
        private readonly Dictionary<int, Button[]> _groupButtons = new();

        /// <summary>The one group currently showing its children, or null.</summary>
        private int? _expandedGroupTag;

        /// <summary>
        /// Reveals <paramref name="parentAction"/>'s children on the outer, concentric ring -
        /// building them the first time this group is opened, reusing them after. Only one group is
        /// ever expanded at a time; opening a second collapses the first rather than stacking both,
        /// which would put two sets of buttons at the same radius on top of each other.
        /// </summary>
        private void ExpandGroup(int parentTag, ActionItem parentAction)
        {
            if (_expandedGroupTag == parentTag)
                return;

            CollapseGroup();

            if (parentAction.GroupChildren.Count == 0)
                return;

            var buttons = _groupButtons.TryGetValue(parentTag, out var cached)
                ? cached : BuildGroupButtons(parentTag, parentAction);
            _expandedGroupTag = parentTag;

            foreach (var button in buttons)
            {
                button.Visibility = Visibility.Visible;
                button.Opacity = 0;

                var fade = new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = new Duration(TimeSpan.FromMilliseconds(140)),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                };
                Storyboard.SetTarget(fade, button);
                Storyboard.SetTargetProperty(fade, "Opacity");

                var storyboard = new Storyboard();
                storyboard.Children.Add(fade);
                storyboard.Begin();
            }
        }

        private void CollapseGroup()
        {
            HighlightChild(null);
            if (_expandedGroupTag is int tag && _groupButtons.TryGetValue(tag, out var buttons))
            {
                foreach (var button in buttons)
                    button.Visibility = Visibility.Collapsed;
            }

            _expandedGroupTag = null;
        }

        /// <summary>
        /// Creates and positions a group's satellite buttons on <see cref="GroupOrbitRadius"/>,
        /// fanning out from the parent's own position rather than starting fresh at the top of the
        /// circle. Added to <see cref="ButtonsCanvas"/> collapsed; <see cref="ExpandGroup"/> shows
        /// them.
        /// </summary>
        /// <remarks>
        /// The fan order is 0, +1, -1, +2, -2, ... steps away from the parent's own direction - child
        /// 1 starts exactly on the parent's own direction, child 2 one step clockwise, child 3 one
        /// step anticlockwise, and so on outward on alternating sides. At <see cref="ActionItem.MaxGroupChildren"/>
        /// (8) the fan reaches offsets -3..+4.
        ///
        /// The step is an angle computed from <see cref="GroupOrbitRadius"/> and
        /// <see cref="GroupButtonSize"/>, not the main ring's 45-degree slots: reusing the main
        /// ring's own slot angles read as neighbouring satellites being spaced far apart, because
        /// GroupOrbitRadius sits well outside the main orbit and arc length between two points 45
        /// degrees apart grows with the radius they're on. This packs neighbours as close as they
        /// can be without their edges overlapping instead, which is independent of the parent's
        /// distance from the ring's centre - moving one does not move the other.
        ///
        /// Every satellite is painted in <paramref name="parentAction"/>'s own background, foreground
        /// and shadow - not its own slot's default colour, and not anything a child itself carries -
        /// so a group reads as one cohesive control fanning out from its parent rather than five
        /// independently-coloured buttons. Only the icon (glyph or SVG) stays per-child; that is the
        /// one thing each button in the group actually needs to differ by.
        /// </remarks>
        private Button[] BuildGroupButtons(int parentTag, ActionItem parentAction)
        {
            var children = parentAction.GroupChildren;
            double radius = GroupOrbitRadius;
            double size = GroupButtonSize;
            double centre = MenuSize / 2.0;
            int count = children.Count;
            var buttons = new Button[count];

            var background = IconFactory.ColorOr(parentAction.Background, IconFactory.DefaultBackground(parentTag));
            var foreground = IconFactory.ColorOr(parentAction.Foreground, IconFactory.DefaultForeground(parentTag));
            var shadowColor = IconFactory.ColorOr(parentAction.Shadow, Colors.Black);

            for (int i = 0; i < count; i++)
            {
                var child = children[i];
                var offset = RingGeometry.GroupChildOffset(parentTag, i, radius, size);
                double x = centre + offset.X;
                double y = centre + offset.Y;

                var button = new Button
                {
                    Style = (Style)RootGrid.Resources["RadialButtonStyle"],
                    Width = size,
                    Height = size,
                    CornerRadius = new CornerRadius(size / 2.0),
                    Background = new SolidColorBrush(background),
                    Foreground = new SolidColorBrush(foreground),
                    Content = child.Kind == ActionKind.CopyText ? new TextBlock
                    {
                        Text = child.Label,
                        FontSize = 11,
                        Width = size - 10,
                        TextAlignment = TextAlignment.Center,
                        TextWrapping = TextWrapping.Wrap,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxLines = 3,
                        Foreground = new SolidColorBrush(foreground),
                    } : IconFactory.CreateIcon(child, size * RingGeometry.IconSizeRatio, new SolidColorBrush(foreground)),
                    Tag = $"{parentTag}:{i}",
                    Opacity = 0,
                    Visibility = Visibility.Collapsed,
                };

                if (!string.IsNullOrWhiteSpace(child.Label))
                    ToolTipService.SetToolTip(button, child.Kind == ActionKind.CopyText
                        ? $"{child.Label}\n{child.Value}\n松手复制" : child.Label);

                button.Click += GroupChildButton_Click;

                // Same trap as the main ring's ApplyPointerStates: ButtonBase marks pointer events
                // Handled while driving its own visual states, so a plain "+=" here would never run.
                int childIndex = i;
                button.AddHandler(UIElement.PointerPressedEvent,
                    new PointerEventHandler((s2, e2) => ArmChildHold(parentTag, childIndex, child, button)), true);
                button.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler(GroupChildButton_Hover), true);
                button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((s2, e2) => DisarmHold()), true);
                button.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler((s2, e2) =>
                {
                    if (!_trackingGesture) HighlightChild(null);
                }), true);
                button.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((s2, e2) => DisarmHold()), true);
                button.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((s2, e2) => DisarmHold()), true);

                // The template is only applied once the button is actually in the visual tree, which
                // for one built here happens asynchronously after ButtonsCanvas.Children.Add below -
                // unlike the main ring's static XAML buttons, whose templates are already applied by
                // the time RadialMenu_Loaded calls ApplyShadows.
                if (parentAction.ShadowEnabled)
                {
                    button.Loaded += (s2, e2) =>
                    {
                        var host = ButtonShadow.FindTemplatePart(button, "ShadowHost");
                        if (host != null)
                        {
                            ButtonShadow.Apply(host, size, shadowColor,
                                parentAction.ShadowOpacity, parentAction.ShadowBlur,
                                parentAction.ShadowOffsetX, parentAction.ShadowOffsetY);
                        }
                    };
                }

                Canvas.SetLeft(button, x - size / 2.0);
                Canvas.SetTop(button, y - size / 2.0);
                ButtonsCanvas.Children.Add(button);

                buttons[i] = button;
            }

            _groupButtons[parentTag] = buttons;
            return buttons;
        }

        /// <summary>The child a satellite button's "parentTag:childIndex" Tag refers to, or null.</summary>
        private ActionItem? ResolveGroupChild(int parentTag, int childIndex)
        {
            var parent = parentTag is >= 0 and <= 8 ? ActionsByTag[parentTag] : null;
            if (parent == null || childIndex < 0 || childIndex >= parent.GroupChildren.Count)
                return null;
            return parent.GroupChildren[childIndex];
        }

        private static bool TryParseChildTag(string tag, out int parentTag, out int childIndex)
        {
            parentTag = 0;
            childIndex = 0;
            var parts = tag.Split(':');
            return parts.Length == 2
                && int.TryParse(parts[0], out parentTag)
                && int.TryParse(parts[1], out childIndex);
        }

        /// <summary>Runs a group child's action. Mirrors InvokeTag's own close-then-dispatch order.</summary>
        private void GroupChildButton_Click(object sender, RoutedEventArgs e)
        {
            if (_closed || sender is not Button button || button.Tag is not string tag
                || !TryParseChildTag(tag, out int parentTag, out int childIndex))
            {
                return;
            }

            DisarmHold();
            var action = ResolveGroupChild(parentTag, childIndex);
            if (action == null)
                return;

            CloseMenu();
            ActionInvoked?.Invoke(this, action);
        }

        /// <summary>
        /// A group child's hold countdown elapsed. Mirrors InvokeTag's own hold substitution: the
        /// child's <see cref="ActionItem.HoldKind"/> fields stand in for its primary ones on a copy
        /// of the same <see cref="ActionItem"/>, so <see cref="ActionInvoked"/> still only ever sees
        /// one shape of thing to dispatch.
        /// </summary>
        private void InvokeGroupChildHold(int parentTag, int childIndex)
        {
            if (_closed)
                return;

            var child = ResolveGroupChild(parentTag, childIndex);
            if (child == null || child.HoldKind == ActionKind.None)
                return;

            var action = child with { Kind = child.HoldKind, Value = child.HoldValue, Arguments = child.HoldArguments };

            CloseMenu();
            ActionInvoked?.Invoke(this, action);
        }

        /// <summary>
        /// Applies each button's icon, tooltip and colours from the configuration. A button with no
        /// matching entry in actions.json keeps whatever is declared for it in XAML.
        /// </summary>
        private void ApplyActions()
        {
            var buttons = AllButtons;
            var byTag = ActionsByTag;

            for (int tag = 0; tag < buttons.Length; tag++)
            {
                var action = byTag[tag];
                if (action == null)
                    continue;

                var button = buttons[tag];

                var background = IconFactory.ColorOr(action.Background, IconFactory.DefaultBackground(tag));
                var foreground = IconFactory.ColorOr(action.Foreground, IconFactory.DefaultForeground(tag));

                // Content first: ApplyPointerStates recolours the icon element directly, so it has
                // to be able to see it.
                button.Content = IconFactory.CreateIcon(action, _appearance.IconSize);

                ApplyPointerStates(button, action, tag, background, foreground);

                if (!string.IsNullOrWhiteSpace(action.Label))
                    ToolTipService.SetToolTip(button, action.Label);

                ApplyGroupBadge(button, tag, action, background);
            }
        }

        /// <summary>One small corner badge per main-ring button, built the first time it is needed.</summary>
        private readonly Dictionary<int, Border> _groupBadges = new();

        /// <summary>
        /// Marks a Group button with the same small badge <see cref="Settings.RingPreview"/> draws in
        /// the settings preview, so a group reads as one before it is ever hovered - on the real ring,
        /// not only in Settings. Runs after <see cref="UpdateButtonPositions"/>, so the button's own
        /// <see cref="Canvas"/> position is already final and the badge can be pinned to its corner.
        /// </summary>
        private void ApplyGroupBadge(Button button, int tag, ActionItem action, Color parentBackground)
        {
            bool isGroup = action.Kind == ActionKind.Group && action.GroupChildren.Count > 0;

            if (!_groupBadges.TryGetValue(tag, out var badge))
            {
                if (!isGroup)
                    return;

                badge = new Border
                {
                    Background = new SolidColorBrush(Colors.White),
                    BorderThickness = new Thickness(1.5),
                    Child = new FontIcon
                    {
                        FontFamily = IconFont.Family,
                        Glyph = "",
                        Foreground = new SolidColorBrush(Colors.Black),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                ButtonsCanvas.Children.Add(badge);
                _groupBadges[tag] = badge;
            }

            if (!isGroup)
            {
                badge.Visibility = Visibility.Collapsed;
                return;
            }

            double diameter = tag == 0 ? _appearance.CenterButtonSize : _appearance.ButtonSize;
            double badgeSize = Math.Max(14, diameter * 0.32);

            badge.Width = badgeSize;
            badge.Height = badgeSize;
            badge.CornerRadius = new CornerRadius(badgeSize / 2.0);
            badge.BorderBrush = new SolidColorBrush(parentBackground);
            ((FontIcon)badge.Child).FontSize = badgeSize * 0.55;
            ToolTipService.SetToolTip(badge, $"Group of {action.GroupChildren.Count} button(s)");

            // Bottom-right corner of the button, the same placement RingPreview uses.
            Canvas.SetLeft(badge, Canvas.GetLeft(button) + diameter - badgeSize * 0.75);
            Canvas.SetTop(badge, Canvas.GetTop(button) + diameter - badgeSize * 0.75);
            Canvas.SetZIndex(badge, 10);
            badge.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Sets a button's colours and drives its hover/pressed appearance from code.
        /// </summary>
        /// <remarks>
        /// The colours are per-button now, so they cannot live in the ControlTemplate's VisualStates:
        /// a Setter there takes a literal, and it would overwrite whatever is assigned here. The
        /// template therefore only animates the scale, and every colour transition is handled below.
        /// Every path that ends the hover (exit, capture lost) restores the rest colours explicitly -
        /// the original "hover colour stays stuck after the pointer leaves" bug was exactly a missing
        /// restore.
        ///
        /// The subscriptions go through AddHandler with handledEventsToo: true, not "+=".
        /// ButtonBase marks the pointer events Handled while driving its own visual states, so a
        /// plain += handler is never called - the buttons kept their rest colour no matter where the
        /// pointer went, even though Click worked fine.
        /// </remarks>
        private void ApplyPointerStates(Button button, ActionItem action, int tag, Color background, Color foreground)
        {
            // "is Image" rather than IconFile.IsVector alone: IsVector only looks at the extension,
            // so a path whose .svg has since been deleted or renamed would still claim the swap.
            // The button would then invert on hover while showing a fallback glyph - and on the
            // centre button that is exactly the "solid disc disappears" look avoided below.
            // Content was assigned immediately above, so this is the icon that actually got built.
            bool swapsTintedSvg = action.IconTint
                && IconFile.IsVector(action.IconPath)
                && button.Content is Image;

            // Outer buttons invert on hover; the filled centre button lightens instead, because
            // inverting a solid disc reads as the button disappearing. A tinted SVG is the
            // exception: swapping its icon and fill colours is the contrast behaviour users
            // explicitly selected by tinting it.
            bool inverts = tag != 0 || swapsTintedSvg;

            var restBackground = new SolidColorBrush(background);
            var restForeground = new SolidColorBrush(foreground);
            var restBorder = new SolidColorBrush(tag == 0
                ? Color.FromArgb(0x59, 0x94, 0xA3, 0xB8)
                : Color.FromArgb(0x59, 0x38, 0xBD, 0xF8));

            var hoverBackground = new SolidColorBrush(tag == 0
                ? IconFactory.Lighten(background, 0.10)
                : Color.FromArgb(0xF2, 0x16, 0x4E, 0x63));
            var hoverForeground = new SolidColorBrush(tag == 0 ? foreground : Color.FromArgb(0xFF, 0xE0, 0xF2, 0xFE));
            var hoverBorder = new SolidColorBrush(Color.FromArgb(0xFF, 0x38, 0xBD, 0xF8));

            var pressedBackground = new SolidColorBrush(
                inverts ? IconFactory.Darken(foreground, 0.15) : IconFactory.Darken(background, 0.13));

            // A glyph icon has to be recoloured explicitly. Setting Button.Foreground alone is not
            // enough - the ContentPresenter passes it down as an inherited value, which an
            // IconElement does not always pick up, and the glyph would keep its rest colour on a
            // background that has already changed. A tinted SVG has its colour baked into its
            // decoded source, so prepare the inverse-colour source once and swap the Image element.
            var restContent = button.Content as FrameworkElement;

            // Built on the first hover, not here. Producing it means reading the .svg, rewriting
            // every paint attribute and decoding the result, and ApplyActions runs on the path that
            // opens the ring - where the whole budget is about 18 ms. Most buttons are never
            // hovered before the menu closes again, and IconFactory caches the decoded source, so
            // the cost is paid once per colour rather than on every open.
            FrameworkElement? tintedHover = null;
            FrameworkElement? HoverContent() => swapsTintedSvg
                ? tintedHover ??= IconFactory.CreateIcon(
                    action with { Foreground = IconFactory.ToHex(background) }, _appearance.IconSize)
                : restContent;

            void Paint(Brush fill, Brush content, Brush border, bool hover)
            {
                button.Background = tag == 0 ? fill : new SolidColorBrush(Colors.Transparent);
                button.Foreground = content;
                button.BorderBrush = tag == 0 ? border : new SolidColorBrush(Colors.Transparent);

                if (tag != 0 && _wedges[tag] is XamlPath wedge)
                {
                    wedge.Fill = fill;
                    wedge.Stroke = border;
                    SetSelectedOffset(wedge, button, tag, hover ? RingGeometry.SelectedOffsetDip : 0);
                }

                var desiredContent = hover ? HoverContent() : restContent;
                if (desiredContent != null && !ReferenceEquals(button.Content, desiredContent))
                    button.Content = desiredContent;

                if (desiredContent is IconElement icon)
                    icon.Foreground = content;
            }

            void Rest() => Paint(restBackground, restForeground, restBorder, false);
            // Keep the circular outline opposite to the fill as well. Only its brush changes; the
            // template keeps the same 2 px BorderThickness in every pointer state.
            void Hover() => Paint(hoverBackground, hoverForeground, hoverBorder, true);
            void Pressed() => Paint(pressedBackground, hoverForeground, hoverBorder, true);

            // Kept so the drag gesture can highlight a button the pointer is nowhere near.
            _buttonPainters[tag] = new ButtonPainter(Rest, Hover, Pressed);

            Rest();

            button.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler(OnButtonHover), true);
            button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnButtonHover), true);
            button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnButtonPressed), true);
            button.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler(OnButtonRest), true);
            button.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnButtonRest), true);
            button.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnButtonRest), true);
        }

        private static int? TagOf(object sender) =>
            sender is FrameworkElement element && element.Tag is string text
                && int.TryParse(text, out int tag) && tag is >= 0 and <= 8
                ? tag : null;

        private static void SetSelectedOffset(UIElement wedge, UIElement icon, int tag, double distance)
        {
            double angle = (tag - 1) * Math.PI / 4.0 - Math.PI / 2.0;
            double x = Math.Cos(angle) * distance;
            double y = Math.Sin(angle) * distance;

            SetTranslation(wedge, x, y);
            SetTranslation(icon, x, y);
        }

        private void GroupChildButton_Hover(object sender, PointerRoutedEventArgs e)
        {
            if (!_trackingGesture && sender is Button button) HighlightChild(button);
        }

        private static void SetTranslation(UIElement element, double x, double y)
        {
            var transform = element.RenderTransform as CompositeTransform ?? new CompositeTransform();
            transform.TranslateX = x;
            transform.TranslateY = y;
            element.RenderTransform = transform;
            element.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        private ButtonPainter? PainterFor(object sender) => TagOf(sender) is int tag ? _buttonPainters[tag] : null;

        private void OnButtonHover(object sender, PointerRoutedEventArgs e)
        {
            if (_trackingGesture) return;
            // Also reached on release (see ApplyPointerStates), which is exactly when a held-past-
            // threshold press needs its countdown cancelled - the button is either about to raise a
            // normal Click (release before the threshold) or already handled by FireHold (release
            // after it, but that path clears _heldAction itself before this ever runs).
            DisarmHold();
            PainterFor(sender)?.Hover();
            if (TagOf(sender) is int tag)
            {
                ShowSelectionInfo(tag);
                OnMainRingHover(tag);
            }
        }

        private void OnButtonPressed(object sender, PointerRoutedEventArgs e)
        {
            PainterFor(sender)?.Pressed();
            if (TagOf(sender) is int tag)
                ArmHold(tag);
        }

        private void OnButtonRest(object sender, PointerRoutedEventArgs e)
        {
            if (_trackingGesture) return;
            DisarmHold();
            PainterFor(sender)?.Rest();
            HideSelectionInfo();
        }

        private void InitializeWindow()
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            if (_appWindow != null)
            {
                var dpi = GetDpiForWindow(hwnd);
                if (dpi > 0) _rasterizationScale = RingGeometry.ScaleFromDpi(dpi);

                _appWindow.Resize(new SizeInt32(PhysicalMenuSize, PhysicalMenuSize));

                // The presenter has to be *attached* before SetExtendedWindowStyle runs: while it
                // was being configured before SetPresenter, the SetWindowLong calls below were
                // silently reverted and the style read back exactly as it went in.
                //
                // Nothing is configured *on* it, though, and there is no TitleBar block here any
                // more. Both used to cost 46 ms of the 52 ms it took to build this window, on every
                // single middle click, and measurably achieved nothing: the four AppWindow.TitleBar
                // properties configure a title bar this window does not have, and
                // IsResizable/IsMinimizable/IsMaximizable/IsAlwaysOnTop/SetBorderAndTitleBar are
                // each superseded a few lines later by RemoveWindowFrame (which strips the frame
                // bits directly) and SetWindowPos(HWND_TOPMOST).
                //
                // Measured, comparing the resulting window styles rather than guessing: dropping
                // both blocks takes this method from 46 ms to 1.5 ms and leaves GWL_EXSTYLE
                // byte-identical (0x08000088 = TOOLWINDOW|NOACTIVATE|TOPMOST), with GWL_STYLE
                // differing only in WS_MINIMIZEBOX|WS_MAXIMIZEBOX - two bits that cannot draw
                // anything without WS_CAPTION, which is cleared.
                _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);

                SetExtendedWindowStyle(hwnd);
                ApplyTrueTransparency(hwnd);
            }
        }

        private void SetExtendedWindowStyle(IntPtr hwnd)
        {
            try
            {
                // Set extended style
                var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                exStyle |= WS_EX_TOOLWINDOW;
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

                RemoveWindowFrame(hwnd);

                // Force window to be topmost. Deliberately NOT using SWP_SHOWWINDOW here: the window
                // does not know the cursor position yet, so showing it now would flash the menu at
                // the system's default position before ShowAtPosition gets a chance to move it.
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

            }
            catch (Exception) { }
        }

        /// <summary>
        /// Strips every frame bit from the window, which is what actually removes the outline
        /// around the overlay.
        /// </summary>
        /// <remarks>
        /// <c>OverlappedPresenter.SetBorderAndTitleBar(false, false)</c> clears WS_EX_WINDOWEDGE but
        /// leaves <c>WS_DLGFRAME</c> on the style, and that is what the white outline around the
        /// otherwise fully transparent menu was drawn from. Neither
        /// <c>DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE</c> nor the corner preference removes it -
        /// measured on the live window, both return S_OK and change nothing - so the style bit has
        /// to go.
        ///
        /// This only works once the presenter is attached to the AppWindow. While the presenter was
        /// being configured before <c>SetPresenter</c>, these SetWindowLong calls were silently
        /// reverted: the style read back exactly as it went in.
        ///
        /// Only the frame bits are cleared, deliberately with &amp;= ~: overwriting GWL_STYLE
        /// wholesale drops WS_CLIPCHILDREN/WS_CLIPSIBLINGS, which is what made the menu render
        /// blurry after the opening animation (see doc/).
        /// </remarks>
        private static void RemoveWindowFrame(IntPtr hwnd)
        {
            var style = GetWindowLong(hwnd, GWL_STYLE);
            style &= ~(WS_CAPTION | WS_BORDER | WS_DLGFRAME | WS_THICKFRAME);
            SetWindowLong(hwnd, GWL_STYLE, style);

            var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            exStyle &= ~(WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE | WS_EX_DLGMODALFRAME);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

            // Windows 11 rounds top-level windows; on a transparent overlay the rounding is visible
            // as the curved corners of that same unwanted outline.
            int corners = DWMWCP_DONOTROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));

            // The cached frame is only recomputed when the window is told its frame changed.
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        /// <summary>
        /// Makes the window's transparent XAML regions truly see-through to the desktop.
        /// WinUI3's swap chain is opaque by default, so a "Transparent" XAML background just
        /// renders as solid black instead of showing what's behind the window. The classic
        /// WS_EX_LAYERED + SetLayeredWindowAttributes color-key trick does not work here because
        /// it operates on a legacy GDI redirection surface that WinUI's DirectComposition-based
        /// content bypasses entirely. The fix that actually integrates with WinUI's composition
        /// tree: hand the window a fully transparent Composition color brush as its system
        /// backdrop (the same mechanism Mica/Acrylic backdrops use). DWM also needs to be told
        /// this window has real per-pixel alpha via DwmEnableBlurBehindWindow(fEnable: true) -
        /// without it DWM keeps compositing the window as opaque regardless of the backdrop brush.
        /// Passing a null region with only DWM_BB_ENABLE applies alpha-aware composition to the
        /// entire client area. A zero-area region is not equivalent: it limits the effect to an
        /// empty subregion, leaving WinUI's theme-coloured swap-chain clear visible (white in the
        /// Light theme).
        /// </summary>
        private void ApplyTrueTransparency(IntPtr hwnd)
        {
            try
            {
                var blurBehind = new DWM_BLURBEHIND
                {
                    dwFlags = DWM_BB_ENABLE,
                    fEnable = true,
                    hRgnBlur = IntPtr.Zero
                };
                DwmEnableBlurBehindWindow(hwnd, ref blurBehind);

                // Windows 11 draws a faint 1px accent border around top-level windows by default;
                // suppress it so the borderless popup doesn't show a rectangle outline.
                int noBorder = DWMWA_COLOR_NONE;
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));

                SystemBackdrop = new TransparentSystemBackdrop();

                // WinUI clears the native client surface with its theme background before drawing
                // XAML. In Light mode that clear is white and remains visible through transparent
                // XAML pixels. Clear it to black instead: with DWM alpha composition enabled above,
                // those untouched pixels become fully transparent rather than a white 400x400 box.
                _windowHandle = hwnd;
                // Keeps the ring out of RefreshFrame's capture, which runs while it opens.
                SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
                _backgroundBrush = CreateSolidBrush(0);
                SetWindowSubclass(hwnd, _backgroundSubclass, BackgroundSubclassId, UIntPtr.Zero);

                var hdc = GetDC(hwnd);
                if (hdc != IntPtr.Zero)
                {
                    ClearNativeBackground(hwnd, hdc);
                    ReleaseDC(hwnd, hdc);
                }
            }
            catch (Exception) { }
        }

        private IntPtr TransparentWindowProc(
            IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr subclassId, UIntPtr referenceData)
        {
            if (message == WM_ERASEBKGND && ClearNativeBackground(hwnd, wParam))
                return new IntPtr(1);

            return DefSubclassProc(hwnd, message, wParam, lParam);
        }

        private bool ClearNativeBackground(IntPtr hwnd, IntPtr hdc)
        {
            if (hdc == IntPtr.Zero || _backgroundBrush == IntPtr.Zero || !GetClientRect(hwnd, out var rect))
                return false;

            return FillRect(hdc, ref rect, _backgroundBrush) != 0;
        }

        /// <summary>
        /// A SystemBackdrop (the same extension point MicaBackdrop/DesktopAcrylicBackdrop use)
        /// that hands the window a fully transparent composition brush instead of a material.
        /// ICompositionSupportsSystemBackdrop.SystemBackdrop expects a
        /// Windows.UI.Composition.CompositionBrush from the OS-level compositor - not the
        /// Microsoft.UI.Composition.Compositor used for XAML element visuals - so a dedicated
        /// Compositor has to be created here (the standard pattern for custom system backdrops).
        /// </summary>
        private sealed class TransparentSystemBackdrop : SystemBackdrop
        {
            private WindowsSystemDispatcherQueueHelper? _dispatcherQueueHelper;
            private Windows.UI.Composition.Compositor? _compositor;

            protected override void OnTargetConnected(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
            {
                base.OnTargetConnected(connectedTarget, xamlRoot);
                _dispatcherQueueHelper = new WindowsSystemDispatcherQueueHelper();
                _dispatcherQueueHelper.EnsureWindowsSystemDispatcherQueueController();
                _compositor = new Windows.UI.Composition.Compositor();
                connectedTarget.SystemBackdrop = _compositor.CreateColorBrush(Colors.Transparent);
            }

            protected override void OnTargetDisconnected(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop disconnectedTarget)
            {
                disconnectedTarget.SystemBackdrop = null;
                _compositor?.Dispose();
                _compositor = null;
                base.OnTargetDisconnected(disconnectedTarget);
            }
        }

        /// <summary>
        /// Ensures the current thread has a DispatcherQueue, which Windows.UI.Composition.Compositor
        /// requires to construct. The app's UI thread already has one by the time a RadialMenu is
        /// created, so in practice this is a cheap no-op guard rather than dead weight.
        /// </summary>
        private sealed class WindowsSystemDispatcherQueueHelper
        {
            [StructLayout(LayoutKind.Sequential)]
            private struct DispatcherQueueOptions
            {
                internal int dwSize;
                internal int threadType;
                internal int apartmentType;
            }

            [DllImport("CoreMessaging.dll")]
            private static extern int CreateDispatcherQueueController(
                [In] DispatcherQueueOptions options,
                [In, Out, MarshalAs(UnmanagedType.IUnknown)] ref object? dispatcherQueueController);

            private object? _dispatcherQueueController;

            public void EnsureWindowsSystemDispatcherQueueController()
            {
                if (Windows.System.DispatcherQueue.GetForCurrentThread() != null)
                    return;

                if (_dispatcherQueueController == null)
                {
                    var options = new DispatcherQueueOptions
                    {
                        dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
                        threadType = 2,    // DQTYPE_THREAD_CURRENT
                        apartmentType = 2, // DQTAT_COM_STA
                    };
                    CreateDispatcherQueueController(options, ref _dispatcherQueueController);
                }
            }
        }

        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);

        private RECT GetMonitorWorkArea(PointInt32 point)
        {
            var pt = new POINT(point.X, point.Y);
            var hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMonitor, ref mi))
            {
                return mi.rcWork;
            }
            return new RECT { left = 0, top = 0, right = GetSystemMetrics(0), bottom = GetSystemMetrics(1) };
        }

        /// <summary>
        /// Presents the hold's grey cover, restores live colour beneath it, then plays the
        /// uniform blocks and bloom following two depth fronts in a topmost, click-through image.
        /// </summary>
        /// <remarks>
        /// One bounded worker uploads the latest finished capture-relative correction. The
        /// close timer shares the renderer's duration; late frames never extend playback.
        ///
        /// <paramref name="restoreColour"/> is only called once the cover has been rendered, so the
        /// matrix restoration is hidden during the handoff. If the menu closes first,
        /// LauncherService restores colour from its Closed handler.
        /// </remarks>
        internal void PlayRefresh(RefreshFrame? frame, Action restoreColour)
        {
            if (_refreshPlayback != null)
                return;
            if (_closed || _appWindow == null)
            {
                restoreColour();
                return;
            }

            DisarmHold();
            if (frame == null)
            {
                restoreColour();
                CloseMenu();
                return;
            }

            var bounds = frame.Bounds;
            double scale = _rasterizationScale;
            double layerW = bounds.Width / scale, layerH = bounds.Height / scale;

            MenuContainer.Visibility = Visibility.Collapsed;
            RefreshLayer.Children.Clear();
            RefreshLayer.Width = layerW;
            RefreshLayer.Height = layerH;
            RefreshLayer.Visibility = Visibility.Visible;

            _screenOrigin = new PointInt32(bounds.X, bounds.Y);
            _appWindow.Move(_screenOrigin);
            _appWindow.Resize(new SizeInt32(bounds.Width, bounds.Height));

            int exStyle = GetWindowLong(_windowHandle, GWL_EXSTYLE);
            SetWindowLong(_windowHandle, GWL_EXSTYLE,
                exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT);
            SetWindowPos(_windowHandle, HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                SWP_NOACTIVATE);

            // One image contains the native blocks, same-colour bloom and background correction.
            var bitmap = new WriteableBitmap(bounds.Width, bounds.Height);
            using (var stream = bitmap.PixelBuffer.AsStream())
                stream.Write(frame.Grey, 0, frame.Grey.Length);
            bitmap.Invalidate();
            AddRefreshImage(bitmap, layerW, layerH);
            _refreshPlayback = new RefreshPlayback(frame, DispatcherQueue, bitmap, CloseMenu);

            // Playback lifetime is independent of decorative visuals.
            var timer = DispatcherQueue.CreateTimer();
            timer.IsRepeating = false;
            timer.Interval = TimeSpan.FromMilliseconds(frame.Renderer.DurationMs);
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (!_closed)
                    CloseMenu();
            };
            _refreshTimer = timer;

            // A few rendered frames so DWM has presented the cover before the matrix is lifted.
            int frames = 0;
            _refreshOnRendering = (_, _) =>
            {
                if (++frames < 3 && !_closed)
                    return;
                CompositionTarget.Rendering -= _refreshOnRendering;
                _refreshOnRendering = null;
                if (_closed)
                    return;
                restoreColour();
                timer.Start();
                _refreshPlayback?.Start();
            };
            CompositionTarget.Rendering += _refreshOnRendering;
        }

        private void AddRefreshImage(WriteableBitmap bitmap, double width, double height)
        {
            RefreshLayer.Children.Add(new Image
            {
                Source = bitmap,
                Stretch = Stretch.Fill,
                Width = width,
                Height = height,
                IsHitTestVisible = false,
            });
        }

        /// <summary>
        /// Re-reads the DPI of whichever monitor <paramref name="point"/> is actually on, and
        /// resizes the physical window if that turns out to differ from the scale
        /// <see cref="InitializeWindow"/> cached when the HWND was first created.
        /// </summary>
        /// <remarks>
        /// The window is always constructed (and its size set from
        /// <c>GetDpiForWindow</c>) before <see cref="ShowAtPosition"/> ever runs - see
        /// <c>LauncherService.OpenMenuAt</c>, which builds the <c>RadialMenu</c> first and moves it
        /// to the cursor second. On a single-monitor or same-DPI setup that first read is already
        /// correct and this is a no-op. On a mixed-DPI multi-monitor setup it can belong to the
        /// wrong monitor entirely: a new top-level window is placed by Windows before anyone has
        /// told it where it is going, so opening the ring on a secondary display sized differently
        /// from the primary one would otherwise leave the native HWND sized for the wrong scale
        /// while XAML - which WinUI keeps in sync with whatever monitor the window is really on -
        /// lays the ring out at the correct one, clipping the ring or leaving dead transparent space
        /// around it.
        /// </remarks>
        private void RefreshDpiForTarget(PointInt32 point)
        {
            var pt = new POINT(point.X, point.Y);
            var hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            if (hMonitor == IntPtr.Zero
                || GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) != 0
                || dpiX == 0)
            {
                return;
            }

            double scale = RingGeometry.ScaleFromDpi(dpiX);
            if (scale == _rasterizationScale)
                return;

            _rasterizationScale = scale;
            _appWindow?.Resize(new SizeInt32(PhysicalMenuSize, PhysicalMenuSize));
        }

        public void ShowAtPosition(PointInt32 position)
        {
            if (_appWindow == null) return;

            RefreshDpiForTarget(position);

            // Place center button at cursor position.
            // Center button is at (MenuSize/2, MenuSize/2) in window coordinates, so the window's
            // top-left goes at cursor - half the window size. Every calculation here uses physical
            // pixels because AppWindow.Move/Resize and the monitor's work area are both physical.
            int size = PhysicalMenuSize;
            // Keep the logical centre at the pointer. At screen edges the window is clipped;
            // drawing and hit-testing therefore share the same centre instead of being clamped
            // to a different one.
            var target = new PointInt32(
                position.X - (int)Math.Round(size / 2.0),
                position.Y - (int)Math.Round(size / 2.0));
            _screenOrigin = target;

            // Position is set BEFORE Activate() so the menu does not flash somewhere else, but it
            // has to be set AGAIN afterwards: the first Activate() is when the window is actually
            // shown, and Win32 applies the default position (CW_USEDEFAULT/cascade) reserved for
            // the window, overwriting the earlier Move - which is why the menu used to pop up in a
            // corner of the screen instead of right under the cursor.
            _appWindow.Move(target);
            Activate();
            _appWindow.Move(target);
            // Activation can change z-order; explicitly restore topmost after the window is shown.
            SetWindowPos(_windowHandle, HWND_TOPMOST, target.X, target.Y, 0, 0,
                SWP_NOSIZE | SWP_NOACTIVATE);

            // Same reason the Move is repeated: showing the window is when the frame is finally
            // realised, and WinUI puts WS_DLGFRAME/WS_EX_WINDOWEDGE back. Stripping them once in
            // the constructor leaves the outline visible on the menu the user actually sees.
            RemoveWindowFrame(_windowHandle);

        }

        private void UpdateButtonPositions(double radius)
        {
            // The canvas is MenuSize square; keep the centre button exactly at its midpoint.
            double canvasSize = MenuSize;
            double centerBtnSize = _appearance.CenterButtonSize;
            double centerBtnLeft = (canvasSize - centerBtnSize) / 2.0;
            double centerBtnTop = (canvasSize - centerBtnSize) / 2.0;

            // Actual centre point of the centre button.
            double centerBtnCenterX = centerBtnLeft + (centerBtnSize / 2.0);
            double centerBtnCenterY = centerBtnTop + (centerBtnSize / 2.0);

            // Update button Canvas positions using the same size ApplySizes gave the buttons.
            double btnSize = _appearance.ButtonSize;
            var buttons = AllButtons;

            for (int tag = 1; tag < buttons.Length; tag++)
            {
                // Calculate angle for 8 buttons (45 degrees apart)
                var angle = (Math.PI / 4.0) * (tag - 1) - Math.PI / 2.0; // start at top

                // Calculate the position from the centre button's midpoint.
                var x = centerBtnCenterX + Math.Cos(angle) * radius;
                var y = centerBtnCenterY + Math.Sin(angle) * radius;

                Canvas.SetLeft(buttons[tag], x - (btnSize / 2.0));
                Canvas.SetTop(buttons[tag], y - (btnSize / 2.0));
            }

            // Set center button position
            Canvas.SetLeft(CenterBtn, centerBtnLeft);
            Canvas.SetTop(CenterBtn, centerBtnTop);

            CenterInfo.Width = centerBtnSize;
            CenterInfo.Height = centerBtnSize;
            CenterInfo.CornerRadius = new CornerRadius(centerBtnSize / 2.0);
            Canvas.SetLeft(CenterInfo, centerBtnLeft);
            Canvas.SetTop(CenterInfo, centerBtnTop);

        }

        private void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string tagText
                || !int.TryParse(tagText, out int tag))
            {
                return;
            }

            // Routed through InvokeTag rather than repeating the lookup: this used to scan _actions
            // with FirstOrDefault while InvokeTag read the prebuilt index, so the two could disagree
            // on which entry wins when a tag appears twice in actions.json.
            InvokeTag(tag);
        }

        private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            // If the click landed on a Button, that button's own Click handler deals with it - don't
            // close here. This used to compute the distance to each button's centre by hand, but
            // that had to be kept in sync manually with the button sizes in XAML and drifted the
            // moment the style changed.
            if (IsWithinButton(e.OriginalSource as DependencyObject))
                return;

            // Clicked inside the window but not on a button -> close menu
            CloseMenu();
        }

        private static bool IsWithinButton(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is Button || source is XamlPath)
                    return true;

                source = VisualTreeHelper.GetParent(source);
            }

            return false;
        }

        /// <summary>
        /// True if the point (screen coordinates) falls within the menu's window bounds.
        /// LauncherService uses this to tell a click on the menu apart from a click elsewhere on screen.
        /// </summary>
        public bool ContainsScreenPoint(PointInt32 point) => RingGeometry.Contains(
            new ScreenPoint(_screenOrigin.X, _screenOrigin.Y),
            PhysicalMenuSize,
            new ScreenPoint(point.X, point.Y));
    }
}
