using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Action_Wheel.Overlay
{
    public sealed partial class RadialMenu
    {
        internal static async Task<object> RunGlassProbeAsync()
        {
            var actions = ActionConfig.Defaults().ToList();
            actions[1] = actions[1] with
            {
                Kind = ActionKind.Group,
                GroupChildren = new[] { actions[2] with { Label = "probe child", Kind = ActionKind.None } },
            };
            var menu = new RadialMenu(actions);
            menu.AppWindow.Hide();
            menu.AppWindow.Move(new PointInt32(-20000, -20000));
            try
            {
                await Task.Delay(120);
                menu.RootGrid.Measure(new Windows.Foundation.Size(menu.MenuSize, menu.MenuSize));
                menu.RootGrid.Arrange(new Windows.Foundation.Rect(0, 0, menu.MenuSize, menu.MenuSize));
                menu.RootGrid.UpdateLayout();
                if (menu._wedges[1] == null) menu.RadialMenu_Loaded(null, new RoutedEventArgs());
                await Task.Delay(220);
                RequireReleaseProbe(menu._glassSurface != null, "Live desktop glass was not installed: "
                    + WheelGlassSurface.LastAttachFailure);
                menu._glassSurface!.RefreshDiagnostics();
                var mask = menu._glassSurface.Diagnostics;
                RequireReleaseProbe(mask.BrushChainConnected && mask.Radius.X > 0
                    && Math.Abs(mask.Center.X - mask.ClientSize.X / 2) < 1
                    && Math.Abs(mask.Center.Y - mask.ClientSize.Y / 2) < 1,
                    "Desktop glass mask does not match the wheel's physical centre: " + mask);
                RequireReleaseProbe(Math.Abs(mask.MaterialOpacity - .60) < .001
                    && mask.MaskOpacity > .59 && mask.MaskOpacity < .61,
                    "Frosted material is no longer independently translucent: " + mask);
                var dividers = menu.ButtonsCanvas.Children.OfType<Microsoft.UI.Xaml.Shapes.Line>().ToArray();
                RequireReleaseProbe(dividers.Length == 8 && dividers.All(line => Math.Abs(line.Opacity - .72) < .000001
                    && Math.Abs(line.StrokeThickness - .7) < .000001),
                    "Material transparency also changed the divider layer: "
                    + string.Join(", ", dividers.Select(line => $"{line.Opacity:R}/{line.StrokeThickness:R}")));
                RequireReleaseProbe(menu._motionHalo != null && menu._openingSweep != null,
                    "Opening/confirmation motion accents were not created.");
                RequireReleaseProbe(menu._wedges.Skip(1).All(w => w is { StrokeThickness: 0 }),
                    "The unified ring has missing or separately stroked sectors.");
                RequireReleaseProbe(menu._directionArcs.Skip(1).All(a => a != null), "Missing direction arcs.");
                menu.HighlightTag(3);
                await Task.Delay(130);
                RequireReleaseProbe(menu.CenterInfo.Visibility == Visibility.Visible && menu.CenterBtn.Opacity == 0,
                    "Selection text overlaps the centre glyph.");
                RequireReleaseProbe(menu._directionArcs[3]!.Opacity == 1
                    && IsIdentity(menu._wedges[3]!.RenderTransform) && IsIdentity(menu.Btn3.RenderTransform),
                    "Selection moved a sector out of the continuous ring.");
                menu.HighlightTag(1);
                RequireReleaseProbe(menu._expandedGroupTag == 1 && menu._groupButtons[1].Length == 1,
                    "A group no longer expands from the glass ring.");
                menu.HighlightTag(4);
                await Task.Delay(130);
                RequireReleaseProbe(menu._expandedGroupTag == null && menu._directionArcs[3]!.Opacity == 0,
                    "Previous group or selection did not clear.");
                menu.HighlightTag(2);
                menu.HighlightTag(3);
                menu.HighlightTag(4);
                await Task.Delay(140);
                RequireReleaseProbe(menu._directionArcs.Skip(1).Select((arc, index) =>
                        arc!.Opacity == (index == 3 ? 1 : 0)).All(matches => matches),
                    "Rapid direction changes left an old selection arc visible.");
                int invoked = 0;
                menu.ActionInvoked += (_, _) => invoked++;
                menu.InvokeTag(4);
                menu.InvokeTag(4);
                menu.BeginRefreshFeedback(new PointInt32(-20000, -20000));
                RequireReleaseProbe(menu._closing && !menu.RootGrid.IsHitTestVisible,
                    "Closing tail still intercepts desktop input.");
                RequireReleaseProbe(!menu.IsRefreshPlaying, "Release during confirmation replaced the pending action with refresh.");
                await Task.Delay(250);
                RequireReleaseProbe(menu._closed && menu._glassSurface == null && invoked == 1,
                    "Animated confirmation did not close/release/dispatch exactly once.");

                var cancel = new RadialMenu(appearance: RingAppearance.Default with { Animation = RingAnimation.None });
                cancel.AppWindow.Hide();
                cancel.AppWindow.Move(new PointInt32(-20000, -20000));
                await Task.Delay(120);
                cancel.DismissMenu();
                RequireReleaseProbe(cancel._closed, "Animation None did not dismiss synchronously.");
                return new { LiveDesktopGlass = true, UnifiedSectorCount = 8, GroupExpansion = true,
                    SelectionDoesNotMove = true, ConfirmDispatchesOnce = true, ClosingInputTransparent = true,
                    GlassDisposedAtClose = true, AnimationNoneImmediate = true, HiddenWindow = true,
                    MaterialOpacity = mask.MaterialOpacity, DividerLayerIndependent = true,
                    MotionAccents = true, AnimatedSelection = true };
            }
            finally { if (!menu._closed) menu.CloseMenu(); }
        }

        private static bool IsIdentity(Transform? transform) => transform == null ||
            transform.TransformPoint(new Windows.Foundation.Point(0, 0)) == new Windows.Foundation.Point(0, 0)
            && transform.TransformPoint(new Windows.Foundation.Point(1, 0)) == new Windows.Foundation.Point(1, 0)
            && transform.TransformPoint(new Windows.Foundation.Point(0, 1)) == new Windows.Foundation.Point(0, 1);
    }
}
