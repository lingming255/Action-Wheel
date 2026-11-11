namespace Action_Wheel.ViewModels
{
    /// <summary>Desktop refresh timing controls. Saving applies them to the next gesture.</summary>
    public sealed partial class SettingsViewModel
    {
        private RefreshAnimationSettings _refreshSettings = RefreshAnimationSettings.Default;
        private ReclaimAnimationSettings _reclaimSettings = ReclaimAnimationSettings.Default;

        public RefreshAnimationSettings RefreshSettings => _refreshSettings;
        public ReclaimAnimationSettings ReclaimSettings => _reclaimSettings;

        public double ReclaimDurationMs
        {
            get => _reclaimSettings.DurationMs;
            set => ApplyReclaimSettings(_reclaimSettings with { DurationMs = value }, nameof(ReclaimDurationMs));
        }

        public double ReclaimPixelDurationMs
        {
            get => _reclaimSettings.PixelDurationMs;
            set => ApplyReclaimSettings(_reclaimSettings with { PixelDurationMs = value }, nameof(ReclaimPixelDurationMs));
        }

        public double ReclaimDensityPercent
        {
            get => _reclaimSettings.DensityPercent;
            set => ApplyReclaimSettings(_reclaimSettings with { DensityPercent = value }, nameof(ReclaimDensityPercent));
        }

        public double ReclaimGlowPercent
        {
            get => _reclaimSettings.GlowPercent;
            set => ApplyReclaimSettings(_reclaimSettings with { GlowPercent = value }, nameof(ReclaimGlowPercent));
        }

        public double ReclaimMaxPixelDurationMs => System.Math.Min(ReclaimAnimationSettings.MaxPixelDurationMs, _reclaimSettings.DurationMs);
        public string ReclaimDurationMsText => $"{_reclaimSettings.DurationMs:0} ms";
        public string ReclaimPixelDurationMsText => $"{_reclaimSettings.PixelDurationMs:0} ms";
        public string ReclaimDensityPercentText => $"{_reclaimSettings.DensityPercent:0}%";
        public string ReclaimGlowPercentText => $"{_reclaimSettings.GlowPercent:0}%";

        private void ApplyReclaimSettings(ReclaimAnimationSettings requested, string propertyName)
        {
            var next = requested.Normalised();
            if (next == _reclaimSettings)
            {
                Raise(propertyName);
                return;
            }
            _reclaimSettings = next;
            RaiseReclaimSettings();
            Raise(nameof(HasUnsavedChanges));
        }

        private void RaiseReclaimSettings()
        {
            Raise(nameof(ReclaimSettings));
            // Clamp the pulse slider before raising its value; changing total can lower its max.
            Raise(nameof(ReclaimMaxPixelDurationMs));
            Raise(nameof(ReclaimDurationMs));
            Raise(nameof(ReclaimDurationMsText));
            Raise(nameof(ReclaimPixelDurationMs));
            Raise(nameof(ReclaimPixelDurationMsText));
            Raise(nameof(ReclaimDensityPercent));
            Raise(nameof(ReclaimDensityPercentText));
            Raise(nameof(ReclaimGlowPercent));
            Raise(nameof(ReclaimGlowPercentText));
        }

        public double RefreshDurationMs
        {
            get => _refreshSettings.DurationMs;
            set => ApplyRefreshSettings(_refreshSettings with { DurationMs = value }, nameof(RefreshDurationMs));
        }

        public double RefreshDistanceDelayPercent
        {
            get => _refreshSettings.DistanceDelayPercent;
            set => ApplyRefreshSettings(_refreshSettings with { DistanceDelayPercent = value }, nameof(RefreshDistanceDelayPercent));
        }

        public double RefreshPixelDurationPercent
        {
            get => _refreshSettings.PixelDurationPercent;
            set => ApplyRefreshSettings(_refreshSettings with { PixelDurationPercent = value }, nameof(RefreshPixelDurationPercent));
        }

        public string RefreshDurationMsText => $"{_refreshSettings.DurationMs:0} ms";
        public string RefreshDistanceDelayPercentText => $"{_refreshSettings.DistanceDelayPercent:0}%";
        public string RefreshPixelDurationPercentText => $"{_refreshSettings.PixelDurationPercent:0}%";

        private void ApplyRefreshSettings(RefreshAnimationSettings requested, string propertyName)
        {
            var next = requested.Normalised();
            if (next == _refreshSettings)
            {
                Raise(propertyName);
                return;
            }
            _refreshSettings = next;
            RaiseRefreshSettings();
            Raise(nameof(HasUnsavedChanges));
        }

        private void RaiseRefreshSettings()
        {
            Raise(nameof(RefreshSettings));
            Raise(nameof(RefreshDurationMs));
            Raise(nameof(RefreshDurationMsText));
            Raise(nameof(RefreshDistanceDelayPercent));
            Raise(nameof(RefreshDistanceDelayPercentText));
            Raise(nameof(RefreshPixelDurationPercent));
            Raise(nameof(RefreshPixelDurationPercentText));
        }
    }
}
