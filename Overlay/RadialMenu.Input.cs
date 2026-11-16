using Action_Wheel.Services;
using Microsoft.UI.Xaml;

namespace Action_Wheel.Overlay
{
    public sealed partial class RadialMenu
    {
        internal bool IsQuickInfo { get; set; }
        internal bool OwnsInput => !_closed && !_parked && !_closing && !IsRefreshPlaying;
        private bool _inputReturned;

        private void AcquireInputFocus()
        {
            if (!OwnsInput) return;
            int style = GetWindowLong(_windowHandle, GWL_EXSTYLE);
            SetWindowLong(_windowHandle, GWL_EXSTYLE,
                (style | WS_EX_TOOLWINDOW) & ~(WS_EX_NOACTIVATE | WS_EX_TRANSPARENT));
            WindowInputFocus.TryActivate(_windowHandle);
            CenterBtn.Focus(FocusState.Programmatic);
        }

        private void ReturnInputFocus()
        {
            if (_inputReturned) return;
            _inputReturned = true;
            // Alt+Tab or an explicit focus change belongs to the user; a late closing tail must
            // never steal it back. Only return focus while this HWND still owns the foreground.
            if (WindowInputFocus.Foreground == _windowHandle)
                WindowInputFocus.TryActivate(ActionTargetWindow);
        }

    }
}
