namespace Action_Wheel.ViewModels
{
    public sealed partial class SettingsViewModel
    {
        private WheelSoundSettings _soundSettings = WheelSoundSettings.Default;

        public WheelSoundSettings SoundSettings => _soundSettings;

        public bool WheelSoundsEnabled
        {
            get => _soundSettings.Enabled;
            set => ApplySoundSettings(_soundSettings with { Enabled = value });
        }

        public double WheelSoundVolumePercent
        {
            get => _soundSettings.VolumePercent;
            set => ApplySoundSettings(_soundSettings with { VolumePercent = value });
        }

        public string WheelSoundVolumeText => $"{_soundSettings.VolumePercent:0}%";

        public double WheelHoverVolumePercent
        {
            get => _soundSettings.HoverVolumePercent;
            set => ApplySoundSettings(_soundSettings with { HoverVolumePercent = value });
        }

        public string WheelHoverVolumeText => $"{_soundSettings.HoverVolumePercent:0}%";

        private void ApplySoundSettings(WheelSoundSettings requested)
        {
            var next = requested.Normalised();
            if (next == _soundSettings) return;
            _soundSettings = next;
            RaiseSoundSettings();
            Raise(nameof(HasUnsavedChanges));
        }

        private void RaiseSoundSettings()
        {
            Raise(nameof(SoundSettings));
            Raise(nameof(WheelSoundsEnabled));
            Raise(nameof(WheelSoundVolumePercent));
            Raise(nameof(WheelSoundVolumeText));
            Raise(nameof(WheelHoverVolumePercent));
            Raise(nameof(WheelHoverVolumeText));
        }
    }
}
