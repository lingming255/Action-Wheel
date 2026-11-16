using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Action_Wheel.Services;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Action_Wheel.Overlay
{
    public sealed partial class RadialMenu
    {
        /// <summary>Opt-in, cloaked lifecycle checks; never installs hooks or captures desktop pixels.</summary>
        internal static async Task<object> RunReuseProbeAsync(RingAppearance appearance)
        {
            var failures = new List<string>();
            var rounds = new List<object>();
            var confirmationRetirements = new List<object>();
            var actions = ActionConfig.Defaults().ToList();
            actions[1] = actions[1] with
            {
                Kind = ActionKind.Group,
                GroupChildren = new[]
                {
                    new ActionItem { Label = "reuse child", Kind = ActionKind.None, Glyph = "F00D",
                        HoldKind = ActionKind.CopyText, HoldValue = "probe never dispatches" },
                },
            };
            actions[2] = actions[2] with { HoldKind = ActionKind.CopyText, HoldValue = "probe never dispatches" };
            var monitors = PositionProbeReadMonitors();
            nint originalForeground = WindowInputFocus.Foreground;
            RadialMenu? menu = null;
            RadialMenu? refresh = null;
            RadialMenu? activeDestroy = null;
            RadialMenu? confirmedDestroy = null;
            int sessions = 0, closed = 0;
            double constructMs = 0, prepareMs = 0;
            object? parkedDestruction = null, activeDestruction = null, refreshRetirement = null;
            bool prepareKeptForeground = false, originalForegroundRestored = false;
            void Check(bool condition, string message)
            {
                if (!condition) failures.Add(message);
            }
            void Cloak(RadialMenu instance)
            {
                int cloak = 1;
                if (DwmSetWindowAttribute(instance._windowHandle, PositionProbeCloakAttribute, ref cloak, sizeof(int)) != 0)
                    throw new InvalidOperationException("Reuse diagnostic window could not be cloaked.");
                PositionProbeRequireCloaked(instance._windowHandle);
            }
            void RestoreForeground()
            {
                if (originalForeground != 0) WindowInputFocus.TryActivate(originalForeground);
            }
            try
            {
                var timer = Stopwatch.StartNew();
                menu = new RadialMenu(actions, appearance)
                {
                    ActionTargetWindow = originalForeground, MeasureShowTimings = true,
                };
                constructMs = timer.Elapsed.TotalMilliseconds;
                menu.SessionEnded += (_, _) => sessions++;
                menu.Closed += (_, _) => closed++;
                Cloak(menu);
                timer.Restart();
                menu.PrepareForReuse();
                prepareMs = timer.Elapsed.TotalMilliseconds;
                prepareKeptForeground = WindowInputFocus.Foreground == originalForeground;
                Check(prepareKeptForeground, "PrepareForReuse changed the foreground window.");
                Cloak(menu);
                Check(menu.IsParked && menu.CanReuse && !menu._closed,
                    "Prepared main menu is not parked and reusable.");
                Check(sessions == 0 && closed == 0, "Idle preparation ended a session or destroyed the window.");
                nint hwnd = menu._windowHandle;
                object? glass = menu._glassSurface, sheet = menu._glassSheet;
                var buttons = menu.AllButtons.ToArray();
                var wedges = menu._wedges.ToArray();
                var arcs = menu._directionArcs.ToArray();
                Check(glass != null && sheet != null && wedges.Skip(1).All(w => w != null),
                    "Synchronous preparation did not create the glass and main-ring visual tree.");
                int? primedChildren = null, primedRootChildren = null;
                object[]? groupButtons = null;
                object? holdIndicator = null;
                // Keep the original twice-across-all-monitors rounds, then measure repeated
                // openings at different anchors on each monitor. A final injected frame checks
                // that the fast path still repairs frame bits reintroduced by a presenter.
                var cases = monitors.Concat(monitors)
                    .Select(monitor => (Monitor: monitor, Scenario: "cross-monitor")).ToList();
                foreach (var monitor in monitors)
                    for (int repeat = 0; repeat < 3; repeat++) cases.Add((monitor, "same-monitor"));
                cases.Add((monitors[^1], "repair-injected-frame"));
                for (int round = 0; round < cases.Count; round++)
                {
                    var (monitor, scenario) = cases[round];
                    int anchorOffset = scenario == "same-monitor" ? 30 * (round % 3 - 1) : 0;
                    var point = new PointInt32((monitor.Bounds.left + monitor.Bounds.right) / 2 + anchorOffset,
                        (monitor.Bounds.top + monitor.Bounds.bottom) / 2 + anchorOffset);
                    var roundFailures = new List<string>();
                    void RoundCheck(bool condition, string message)
                    {
                        if (!condition) { roundFailures.Add(message); failures.Add($"Round {round}: {message}"); }
                    }
                    menu.ActionTargetWindow = originalForeground;
                    Cloak(menu);
                    bool previouslyShown = menu._hasShownMenu;
                    bool sameMonitorBeforeShow = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)
                        == MonitorFromPoint(new POINT(point.X, point.Y), MONITOR_DEFAULTTONEAREST);
                    bool injectedFrame = scenario == "repair-injected-frame";
                    if (injectedFrame)
                    {
                        SetWindowLong(hwnd, GWL_STYLE, GetWindowLong(hwnd, GWL_STYLE) | WS_DLGFRAME);
                        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_WINDOWEDGE);
                    }
                    timer.Restart();
                    menu.ShowAtPosition(point);
                    double showMs = timer.Elapsed.TotalMilliseconds;
                    var showStages = menu.LastShowTimings;
                    PositionProbeRequireCloaked(hwnd);
                    RestoreForeground();
                    RoundCheck(showStages != null
                        && showStages.MonitorStagingMoved == (!previouslyShown || !sameMonitorBeforeShow),
                        "A parked warm wheel staged on its existing monitor or skipped a necessary monitor migration.");
                    int nativeStyle = GetWindowLong(hwnd, GWL_STYLE);
                    int nativeExStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                    bool frameless = (nativeStyle & (WS_CAPTION | WS_BORDER | WS_DLGFRAME | WS_THICKFRAME)) == 0
                        && (nativeExStyle & (WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE | WS_EX_DLGMODALFRAME)) == 0;
                    RoundCheck(frameless, "A reused Show retained native frame bits.");
                    RoundCheck((nativeExStyle & (WS_EX_NOACTIVATE | WS_EX_TRANSPARENT)) == 0,
                        "A reused interactive wheel retained no-activate or click-through styles.");
                    bool cornersSupported = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
                    int corners = 0;
                    bool cornersCorrect = !cornersSupported || (PositionProbeGetDwmAttribute(hwnd,
                        DWMWA_WINDOW_CORNER_PREFERENCE, out corners, sizeof(int)) == 0 && corners == DWMWCP_DONOTROUND);
                    RoundCheck(cornersCorrect, "A reused wheel lost its no-rounding DWM preference.");
                    uint nativeDpi = GetDpiForWindow(hwnd);
                    bool clientBoundsRead = menu.TryGetScreenClientBounds(out var clientBounds);
                    int expectedSize = menu.PhysicalMenuSize;
                    bool anchorCorrect = clientBoundsRead
                        && clientBounds.left == point.X - (int)Math.Round(expectedSize / 2.0)
                        && clientBounds.top == point.Y - (int)Math.Round(expectedSize / 2.0)
                        && clientBounds.right - clientBounds.left == expectedSize
                        && clientBounds.bottom - clientBounds.top == expectedSize;
                    RoundCheck(nativeDpi == monitor.Dpi && anchorCorrect,
                        "A reused wheel did not preserve its target DPI, size and pointer-centred bounds.");
                    RoundCheck(!menu.IsParked && menu.OwnsInput && !menu._inputReturned,
                        "Show did not reset parked/input-returned state for a new session.");
                    RoundCheck(!menu._trackingGesture && menu._highlightedTag == null
                        && menu._highlightedChild == null && menu._expandedGroupTag == null
                        && menu._heldAction == null && !menu._holdTimer.IsRunning,
                        "A new session inherited gesture, selection, group or hold state.");
                    RoundCheck(menu._windowHandle == hwnd && ReferenceEquals(glass, menu._glassSurface)
                        && ReferenceEquals(sheet, menu._glassSheet)
                        && buttons.Zip(menu.AllButtons).All(pair => ReferenceEquals(pair.First, pair.Second))
                        && wedges.Zip(menu._wedges).All(pair => ReferenceEquals(pair.First, pair.Second))
                        && arcs.Zip(menu._directionArcs).All(pair => ReferenceEquals(pair.First, pair.Second)),
                        "A warm Show replaced the HWND, glass, buttons, wedges or direction arcs.");

                    // Exercise real selection/hold setup without allowing its timer to fire and
                    // without invoking any action, keyboard shortcut, clipboard or input event.
                    menu.UpdateGesture(point, 2, allowHold: true);
                    RoundCheck(menu._trackingGesture && menu._heldAction != null && menu._holdTimer.IsRunning,
                        "Synthetic gesture did not arm the main hold before reset.");
                    menu.HighlightTag(1);
                    menu.ExpandGroup(1, actions[1]);
                    var child = menu._groupButtons[1][0];
                    menu.HighlightChild(child);
                    menu.ArmChildHold(1, 0, actions[1].GroupChildren[0], child);
                    RoundCheck(menu._expandedGroupTag == 1 && menu._highlightedChild == child
                        && menu._heldAction != null && menu._holdTimer.IsRunning,
                        "Synthetic group/child hold setup did not become active.");

                    if (round == 0)
                    {
                        // Group satellites and the hold indicator are intentionally lazy. Their
                        // first allocation is reported separately from steady reuse invariants.
                        primedChildren = menu.ButtonsCanvas.Children.Count;
                        primedRootChildren = menu.RootGrid.Children.Count;
                        groupButtons = menu._groupButtons[1].Cast<object>().ToArray();
                        holdIndicator = menu._holdIndicator;
                    }
                    timer.Restart();
                    menu.CloseMenu();
                    double closeMs = timer.Elapsed.TotalMilliseconds;
                    RoundCheck(menu.IsParked && menu.CanReuse && !menu.OwnsInput && !menu._closed,
                        "Ordinary close destroyed the window or left it interactive.");
                    RoundCheck(menu._inputReturned && !menu._trackingGesture && menu._highlightedTag == null
                        && menu._highlightedChild == null && menu._expandedGroupTag == null
                        && menu._heldAction == null && !menu._holdTimer.IsRunning,
                        "Park did not clear gesture, selection, child-group and hold state.");
                    RoundCheck(menu._groupButtons[1].All(b => b.Visibility == Visibility.Collapsed)
                        && menu._holdIndicator?.Visibility == Visibility.Collapsed,
                        "Park left a group button or hold indicator visible.");
                    RoundCheck(menu.ButtonsCanvas.Children.Count == primedChildren
                        && menu.RootGrid.Children.Count == primedRootChildren
                        && groupButtons!.Zip(menu._groupButtons[1]).All(pair => ReferenceEquals(pair.First, pair.Second))
                        && ReferenceEquals(holdIndicator, menu._holdIndicator),
                        "Warm reuse added visual children or rebuilt lazy group/hold nodes.");
                    RoundCheck(sessions == round + 1 && closed == 0,
                        "Park did not end exactly one active session without firing Closed.");
                    menu.CloseMenu();
                    RoundCheck(sessions == round + 1, "Closing an already parked menu ended the same session twice.");
                    await Task.Delay(35);
                    PositionProbeRequireCloaked(hwnd);
                    RoundCheck(menu.IsParked && !menu._holdTimer.IsRunning
                        && menu.ButtonsCanvas.Children.Count == primedChildren,
                        "A deferred layout/hold callback changed the parked menu.");
                    rounds.Add(new
                    {
                        Round = round, Monitor = monitor.Index, Scenario = scenario,
                        SameMonitorBeforeShow = sameMonitorBeforeShow, Passed = roundFailures.Count == 0,
                        ShowSynchronousMs = showMs, CloseSynchronousMs = closeMs,
                        ShowTimings = showStages,
                        Frameless = frameless, NativeStyle = nativeStyle, NativeExtendedStyle = nativeExStyle,
                        CornersCorrect = cornersCorrect, CornerPreference = corners, NativeDpi = nativeDpi,
                        AnchorCorrect = anchorCorrect, FrameInjected = injectedFrame,
                        Hwnd = hwnd.ToInt64(), GlassIdentity = RuntimeHelpers.GetHashCode(glass!),
                        MainButtonIdentities = buttons.Select(RuntimeHelpers.GetHashCode).ToArray(),
                        VisualChildren = menu.ButtonsCanvas.Children.Count,
                        RootChildren = menu.RootGrid.Children.Count, SessionEndedCount = sessions,
                        Failures = roundFailures,
                    });
                }
                timer.Restart();
                menu.DestroyMenu();
                double destroyMs = timer.Elapsed.TotalMilliseconds;
                await Task.Delay(50);
                bool parkedDestroyed = menu._closed && menu._glassSurface == null
                    && !PositionProbeIsWindow(hwnd) && !menu.CanReuse && sessions == cases.Count && closed == 1;
                Check(parkedDestroyed, "Destroying a parked menu did not release its HWND/glass exactly once.");
                parkedDestruction = new { Passed = parkedDestroyed, SynchronousMs = destroyMs,
                    ClosedCount = closed, SessionEndedCount = sessions, NativeDestroyed = !PositionProbeIsWindow(hwnd) };

                activeDestroy = new RadialMenu(actions, appearance with
                {
                    Animation = RingAnimation.Fade, AnimationDurationMs = 120,
                }) { ActionTargetWindow = originalForeground };
                Cloak(activeDestroy);
                activeDestroy.PrepareForReuse();
                Cloak(activeDestroy);
                int activeSessions = 0, activeClosed = 0;
                activeDestroy.SessionEnded += (_, _) => activeSessions++;
                activeDestroy.Closed += (_, _) => activeClosed++;
                var firstMonitor = monitors[0];
                var firstPoint = new PointInt32((firstMonitor.Bounds.left + firstMonitor.Bounds.right) / 2,
                    (firstMonitor.Bounds.top + firstMonitor.Bounds.bottom) / 2);
                nint activeHwnd = activeDestroy._windowHandle;
                activeDestroy.ShowAtPosition(firstPoint);
                RestoreForeground();
                int canceledTailCompletions = 0;
                activeDestroy.DismissMenu(completed: () => canceledTailCompletions++);
                activeDestroy.CloseMenu();
                Check(activeDestroy.IsParked && activeSessions == 1,
                    "Forced parking during a closing animation did not end its session.");
                activeDestroy.ShowAtPosition(firstPoint);
                RestoreForeground();
                await Task.Delay(260);
                Check(!activeDestroy.IsParked && activeDestroy.OwnsInput && activeSessions == 1
                    && canceledTailCompletions == 0,
                    "A canceled closing tail completed or parked a later session.");
                activeDestroy.DestroyMenu();
                await Task.Delay(35);
                bool activeDestroyed = activeDestroy._closed && !activeDestroy.CanReuse
                    && activeSessions == 2 && activeClosed == 1 && !PositionProbeIsWindow(activeHwnd);
                Check(activeDestroyed, "Destroying an active menu did not end one session and close exactly once.");
                activeDestruction = new { Passed = activeDestroyed, SessionEndedCount = activeSessions,
                    ClosedCount = activeClosed, NativeDestroyed = !PositionProbeIsWindow(activeHwnd),
                    CanceledTailCompletions = canceledTailCompletions };

                // Pool retirement can synchronously destroy the owner inside CloseMenu. The
                // selected action must still publish once after that retirement returns. Observe
                // only the event: never connect a dispatcher or execute the synthetic action.
                foreach (var animation in new[] { RingAnimation.None, RingAnimation.Fade })
                {
                    confirmedDestroy = new RadialMenu(actions, appearance with
                    {
                        Animation = animation, AnimationDurationMs = 120,
                    }) { ActionTargetWindow = originalForeground };
                    var confirmedMenu = confirmedDestroy;
                    Cloak(confirmedMenu);
                    confirmedMenu.PrepareForReuse();
                    Cloak(confirmedMenu);
                    int confirmedSessions = 0, confirmedClosed = 0, actionInvocations = 0;
                    bool retirementReturned = false, invokedAfterRetirement = false, selectedActionPreserved = false;
                    var selectedAction = new ActionItem { Label = "confirm retirement probe", Kind = ActionKind.None };
                    confirmedMenu.SessionEnded += (_, _) =>
                    {
                        confirmedSessions++;
                        confirmedMenu.DestroyMenu();
                        retirementReturned = true;
                    };
                    confirmedMenu.Closed += (_, _) => confirmedClosed++;
                    confirmedMenu.ActionInvoked += (sender, action) =>
                    {
                        actionInvocations++;
                        invokedAfterRetirement = retirementReturned;
                        selectedActionPreserved = ReferenceEquals(sender, confirmedMenu)
                            && ReferenceEquals(action, selectedAction);
                    };
                    nint confirmedHwnd = confirmedMenu._windowHandle;
                    confirmedMenu.ShowAtPosition(firstPoint);
                    RestoreForeground();
                    confirmedMenu.ConfirmGlassAction(selectedAction);
                    await Task.Delay(animation == RingAnimation.None ? 50 : 260);
                    bool confirmationSurvived = confirmedMenu._closed && confirmedSessions == 1
                        && confirmedClosed == 1 && actionInvocations == 1 && invokedAfterRetirement
                        && selectedActionPreserved && !PositionProbeIsWindow(confirmedHwnd);
                    Check(confirmationSurvived,
                        $"{animation} confirmation did not publish its selected action exactly once after session retirement.");
                    confirmationRetirements.Add(new { Animation = animation.ToString(), Passed = confirmationSurvived,
                        SessionEndedCount = confirmedSessions, ClosedCount = confirmedClosed,
                        ActionInvokedCount = actionInvocations, InvokedAfterRetirement = invokedAfterRetirement,
                        SelectedActionPreserved = selectedActionPreserved,
                        NativeDestroyed = !PositionProbeIsWindow(confirmedHwnd) });
                }

                refresh = new RadialMenu(actions, appearance) { ActionTargetWindow = originalForeground };
                Cloak(refresh);
                refresh.PrepareForReuse();
                Cloak(refresh);
                int refreshSessions = 0, refreshClosed = 0;
                refresh.SessionEnded += (_, _) => refreshSessions++;
                refresh.Closed += (_, _) => refreshClosed++;
                nint refreshHwnd = refresh._windowHandle;
                refresh.ShowAtPosition(firstPoint);
                RestoreForeground();
                refresh.BeginRefreshFeedback(firstPoint);
                Check(refresh.IsRefreshPlaying && !refresh.CanReuse && !refresh.OwnsInput,
                    "Refresh feedback did not retire the interactive menu from reuse.");
                Check(refresh._refreshFeedbackVisual != null && refresh._glassSurface == null,
                    "Synthetic feedback did not create its own decoration and release wheel glass.");
                refresh.CloseMenu();
                await Task.Delay(50);
                bool refreshDestroyed = refresh._closed && !refresh.IsParked && !refresh.CanReuse
                    && !PositionProbeIsWindow(refreshHwnd) && refresh._refreshFeedbackVisual == null
                    && refresh._refreshFeedbackResources.Count == 0 && refreshSessions == 1 && refreshClosed == 1;
                Check(refreshDestroyed, "Closing refresh feedback parked its transformed window or leaked decoration resources.");
                refreshRetirement = new { Passed = refreshDestroyed, SessionEndedCount = refreshSessions,
                    ClosedCount = refreshClosed, NativeDestroyed = !PositionProbeIsWindow(refreshHwnd),
                    RemainingFeedbackResources = refresh._refreshFeedbackResources.Count };
            }
            catch (Exception exception) { failures.Add(exception.ToString()); }
            finally
            {
                try { if (menu is { _closed: false }) menu.DestroyMenu(); }
                catch (Exception exception) { failures.Add("Main cleanup: " + exception.Message); }
                try { if (activeDestroy is { _closed: false }) activeDestroy.DestroyMenu(); }
                catch (Exception exception) { failures.Add("Active cleanup: " + exception.Message); }
                try { if (confirmedDestroy is { _closed: false }) confirmedDestroy.DestroyMenu(); }
                catch (Exception exception) { failures.Add("Confirmation cleanup: " + exception.Message); }
                try { if (refresh is { _closed: false }) refresh.DestroyMenu(); }
                catch (Exception exception) { failures.Add("Refresh cleanup: " + exception.Message); }
                RestoreForeground();
                originalForegroundRestored = originalForeground == 0 || WindowInputFocus.Foreground == originalForeground;
            }
            return new
            {
                Passed = failures.Count == 0, ConstructMs = constructMs, PrepareSynchronousMs = prepareMs,
                PrepareKeptForeground = prepareKeptForeground, OriginalForegroundRestored = originalForegroundRestored,
                RoundCount = rounds.Count, Rounds = rounds, ParkedDestruction = parkedDestruction,
                ActiveDestruction = activeDestruction, ConfirmationRetirements = confirmationRetirements,
                RefreshRetirement = refreshRetirement, Failures = failures,
            };
        }
    }
}
