using System;
using Action_Wheel.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Action_Wheel.Overlay
{
    public sealed partial class RadialMenu
    {
        private bool _parked;
        private bool _preparingReuse;
        private bool _menuVisualsReady;
        private bool _sessionEnded;
        private long _sessionGeneration;
        private long _openingGeneration = -1;
        private int _interactiveExtendedStyle;

        internal bool ReuseEnabled { get; set; }
        internal bool IsParked => _parked;
        internal bool CanReuse => ReuseEnabled && !_closed && _menuVisualsReady && !IsRefreshPlaying;
        internal event EventHandler? SessionEnded;

        /// <summary>
        /// Realise the actual wheel tree while cloaked and without activation. Keep its HWND,
        /// XAML controls and material for the next gesture; no opening animation or sound runs.
        /// A failed cloak never reveals a preparation window.
        /// </summary>
        internal void PrepareForReuse()
        {
            if (_closed || _appWindow == null) return;
            ReuseEnabled = true;
            _preparingReuse = true;
            _parked = true;
            _sessionEnded = true;
            _interactiveExtendedStyle = GetWindowLong(_windowHandle, GWL_EXSTYLE);
            int cloak = 1;
            if (DwmSetWindowAttribute(_windowHandle, 13, ref cloak, sizeof(int)) != 0)
            {
                _preparingReuse = false;
                return; // First real Show will finish initialising the already-created window.
            }
            try
            {
                _appWindow.Show(false);
                RootGrid.Measure(new Size(MenuSize, MenuSize));
                RootGrid.Arrange(new Rect(0, 0, MenuSize, MenuSize));
                RootGrid.UpdateLayout();
                EnsureMenuVisuals();
                RootGrid.UpdateLayout();
                ResetInteraction();
                _glassSurface?.Suspend();
            }
            finally
            {
                _appWindow.Hide();
                ShowWindow(_windowHandle, SW_HIDE);
                cloak = 0;
                DwmSetWindowAttribute(_windowHandle, 13, ref cloak, sizeof(int));
                _preparingReuse = false;
            }
        }

        private void EnsureMenuVisuals()
        {
            if (_menuVisualsReady) return;
            ApplySizes();
            UpdateButtonPositions(_appearance.OrbitRadius);
            BuildWedges();
            ApplyActions();
            ConnectGlassMaterial();
            _menuVisualsReady = true;
        }

        private void BeginMenuSession()
        {
            _sessionGeneration++;
            _sessionEnded = false;
            _inputReturned = false;
            _closing = false;
            _parked = false;
            ResetInteraction();
            // Dismissal makes a window layered/click-through. Restore exactly its original
            // interactive bits rather than retaining the decoration's WS_EX_LAYERED flag.
            if (_interactiveExtendedStyle != 0)
            {
                const int sessionBits = WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | 0x80000;
                int current = GetWindowLong(_windowHandle, GWL_EXSTYLE);
                SetWindowLong(_windowHandle, GWL_EXSTYLE,
                    (current & ~sessionBits) | (_interactiveExtendedStyle & sessionBits));
            }
            else _interactiveExtendedStyle = GetWindowLong(_windowHandle, GWL_EXSTYLE);
        }

        private void StartPreparedOpening()
        {
            if (_closed || _parked || _preparingReuse || _closing || IsRefreshPlaying) return;
            if (_openingGeneration == _sessionGeneration) return;
            _openingGeneration = _sessionGeneration;
            if (_glassSurface?.IsConnected == false) _glassSurface = null;
            if (_glassSurface == null) ConnectGlassMaterial();
            _glassSurface?.Resume();
            BeginGlassOpening();
            WheelSounds.Play(WheelSound.Open);
        }

        private void ResetInteraction()
        {
            DisarmHold();
            _ringMotion?.Stop();
            _ringMotion = null;
            StopGlassMotion();
            HighlightChild(null);
            CollapseGroup();
            _highlightedTag = null;
            _trackingGesture = false;
            _soundSelection = null;
            foreach (var button in AllButtons)
            {
                button.ReleasePointerCaptures();
                VisualStateManager.GoToState(button, "Normal", false);
            }
            foreach (var children in _groupButtons.Values)
                foreach (var button in children)
                {
                    button.ReleasePointerCaptures();
                    VisualStateManager.GoToState(button, "Normal", false);
                }
            for (int tag = 0; tag < _selectedGlass.Length; tag++)
            {
                _selectionMotions[tag] = null;
                _selectedGlass[tag] = false;
                _buttonPainters[tag]?.Rest();
                if (_wedges[tag] is { } wedge) wedge.Opacity = 0;
                if (_directionArcs[tag] is { } arc) arc.Opacity = 0;
            }
            _infoMotion = null;
            CenterInfo.Opacity = 1;
            CenterInfo.RenderTransform = null;
            HideSelectionInfo();
            RootGrid.IsHitTestVisible = true;
            MenuContainer.Visibility = Visibility.Visible;
            MenuContainer.Opacity = 0;
            MenuContainer.RenderTransform = new CompositeTransform();
            ApplyGlassSaturation(1);
        }

        private void ParkMenu()
        {
            if (_parked || _closed) return;
            ReturnInputFocus();
            _parked = true;
            _sessionGeneration++;
            _closing = true;
            ShowWindow(_windowHandle, SW_HIDE);
            StopMenuPlacement();
            try
            {
                ResetInteraction();
                // The frame belongs to LauncherService. Detach UI borrowing before SessionEnded
                // cancels preparation/disposes that frame, retaining only the wheel's own material.
                DetachBorrowedRefreshPresentation();
                _glassSurface?.Suspend();
            }
            catch (Exception)
            {
                ReuseEnabled = false; // An incomplete reset must never enter the idle slot.
            }
            finally { NotifySessionEnded(); }
        }

        private void NotifySessionEnded()
        {
            if (_sessionEnded) return;
            _sessionEnded = true;
            SessionEnded?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Final shutdown/invalidated snapshot, including an already-hidden wheel.</summary>
        internal void DestroyMenu()
        {
            if (_closed) return;
            ReuseEnabled = false;
            ReturnInputFocus();
            _sessionGeneration++;
            _closing = true;
            StopMenuPlacement();
            ShowWindow(_windowHandle, SW_HIDE);
            Close();
        }
    }
}
