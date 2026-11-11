using System;
using System.Linq;
using System.Threading.Tasks;
using Action_Wheel.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Action_Wheel.Settings
{
    public sealed partial class SettingsWindow
    {
        /// <summary>Reads existing settings, edits only an in-memory model, and never shows or saves.</summary>
        internal static async Task<object> RunBilingualSettingsProbeAsync()
        {
            using var viewModel = new SettingsViewModel();
            var originalRefresh = viewModel.RefreshSettings;
            var originalReclaim = viewModel.ReclaimSettings;
            var originalSounds = viewModel.SoundSettings;
            var window = new SettingsWindow(viewModel);
            try
            {
                window.AppWindow.Hide();
                await Task.Delay(150);
                Require(!viewModel.HasUnsavedChanges && viewModel.RefreshSettings == originalRefresh
                    && viewModel.ReclaimSettings == originalReclaim && viewModel.SoundSettings == originalSounds,
                    "Opening the bilingual controls changed loaded settings.");
                Require(window.Title.Contains("设置") && window.Title.Contains("Settings"),
                    "The settings title is not bilingual.");
                var appearance = window.SectionsNav.MenuItems.OfType<NavigationViewItem>()
                    .Single(item => (string)item.Tag == "Appearance");
                window.SectionsNav.SelectedItem = appearance;
                window.RootGrid.UpdateLayout();
                await Task.Delay(80);
                Require(window.AppearanceSection.Visibility == Visibility.Visible,
                    "The bilingual navigation stopped selecting its original appearance section.");
                foreach (string header in new[] { "总时长\nTotal duration", "单块持续\nBlock pulse",
                    "方块密度\nBlock density", "发光强度\nGlow strength" })
                    Require(FindSlider(window.RootGrid, header) != null, "A bilingual opening control is missing: " + header);

                var soundToggle = FindSoundToggle(window.RootGrid);
                var soundSlider = FindSlider(window.RootGrid, "音效音量\nSound volume");
                Require(soundToggle != null && soundSlider != null,
                    "The bilingual sound switch or volume control is missing.");
                viewModel.WheelSoundsEnabled = !originalSounds.Enabled;
                viewModel.WheelSoundVolumePercent = originalSounds.VolumePercent == 73 ? 61 : 73;
                window.RootGrid.UpdateLayout();
                await Task.Delay(30);
                Require(soundToggle!.IsOn == viewModel.WheelSoundsEnabled
                    && soundSlider!.IsEnabled == viewModel.WheelSoundsEnabled
                    && soundSlider.Value == viewModel.WheelSoundVolumePercent && viewModel.HasUnsavedChanges,
                    "Sound controls failed to bind or mark their unsaved state.");

                viewModel.ReclaimDurationMs = 100;
                viewModel.ReclaimPixelDurationMs = 500;
                Require(viewModel.ReclaimPixelDurationMs == 100 && viewModel.ReclaimMaxPixelDurationMs == 100,
                    "Lowering total duration failed to constrain the block pulse.");
                viewModel.ReclaimDurationMs = 420;
                viewModel.ReclaimPixelDurationMs = 140;
                viewModel.ReclaimDensityPercent = originalReclaim.DensityPercent == 175 ? 150 : 175;
                viewModel.ReclaimGlowPercent = originalReclaim.GlowPercent == 35 ? 50 : 35;
                Require(viewModel.HasUnsavedChanges && viewModel.RefreshSettings == originalRefresh,
                    "Opening edits did not mark unsaved state or changed outward timing.");
                viewModel.ReloadFromDisk();
                Require(!viewModel.HasUnsavedChanges && viewModel.ReclaimSettings == originalReclaim
                    && viewModel.RefreshSettings == originalRefresh && viewModel.SoundSettings == originalSounds,
                    "Reload did not restore the saved opening and outward settings.");
                return new { Passed = true, BilingualHeaders = 4, OriginalNavigationPreserved = true,
                    OpeningDidNotChangeSettings = true, PulseSliderTracksTotal = true,
                    UnsavedStateAndReload = true, SavedUserConfiguration = false,
                    SoundControls = 2, SoundBindingAndReload = true, AudioPlayed = false,
                    UsedDesktopCapture = false, DisplayedWindow = false };
            }
            finally
            {
                window._forceClose = true;
                window.Close();
            }
        }

        private static Slider? FindSlider(DependencyObject parent, string header)
        {
            if (parent is Slider slider && Equals(slider.Header, header)) return slider;
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
                if (FindSlider(VisualTreeHelper.GetChild(parent, index), header) is { } found) return found;
            return null;
        }

        private static ToggleSwitch? FindSoundToggle(DependencyObject parent)
        {
            if (parent is ToggleSwitch toggle && Equals(toggle.Header,
                "展开、切换、确认与取消\nOpen, switch, confirm and cancel")) return toggle;
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
                if (FindSoundToggle(VisualTreeHelper.GetChild(parent, index)) is { } found) return found;
            return null;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
