namespace Action_Wheel.ViewModels
{
    /// <summary>Desktop refresh timing controls. Saving applies them to the next gesture.</summary>
    public sealed partial class SettingsViewModel
    {
        private RefreshAnimationSettings _refreshSettings = RefreshAnimationSettings.Default;

        public RefreshAnimationSettings RefreshSettings => _refreshSettings;

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
