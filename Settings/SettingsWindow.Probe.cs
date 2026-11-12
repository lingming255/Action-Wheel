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

                var soundToggle = FindSoundToggle(window.RootGrid)
                    ?? throw new InvalidOperationException("The bilingual sound switch is missing.");
                var soundSlider = FindSlider(window.RootGrid, "音效音量\nSound volume")
                    ?? throw new InvalidOperationException("The bilingual master volume control is missing.");
                var hoverSlider = FindSlider(window.RootGrid, "悬停音量\nHover volume")
                    ?? throw new InvalidOperationException("The bilingual hover volume control is missing.");
                Require(hoverSlider.Minimum == 0 && hoverSlider.Maximum == 200
                    && WheelSoundSettings.Default.HoverVolumePercent == 100,
                    "Hover volume lost its 0-200 range or original-level default.");
                Require(hoverSlider.Value == originalSounds.HoverVolumePercent
                    && viewModel.WheelHoverVolumeText == $"{originalSounds.HoverVolumePercent:0}%",
                    "The loaded hover volume was not reflected in its controls.");
                viewModel.WheelSoundsEnabled = !originalSounds.Enabled;
                viewModel.WheelSoundVolumePercent = originalSounds.VolumePercent == 73 ? 61 : 73;
                viewModel.WheelHoverVolumePercent = originalSounds.HoverVolumePercent == 137 ? 149 : 137;
                window.RootGrid.UpdateLayout();
                await Task.Delay(30);
                Require(soundToggle.IsOn == viewModel.WheelSoundsEnabled
                    && soundSlider.IsEnabled == viewModel.WheelSoundsEnabled
                    && hoverSlider.IsEnabled == viewModel.WheelSoundsEnabled
                    && soundSlider.Value == viewModel.WheelSoundVolumePercent
                    && hoverSlider.Value == viewModel.WheelHoverVolumePercent && viewModel.HasUnsavedChanges,
                    "Sound controls failed to bind or mark their unsaved state.");
                Require(FindTextBlock(window.RootGrid, viewModel.WheelHoverVolumeText) != null,
                    "The hover volume label did not follow the view model.");

                soundToggle.IsOn = true;
                window.RootGrid.UpdateLayout();
                await Task.Delay(30);
                Require(viewModel.WheelSoundsEnabled && soundSlider.IsEnabled && hoverSlider.IsEnabled,
                    "The sound toggle did not enable both independent volume controls.");
                double hoverBeforeMasterEdit = viewModel.WheelHoverVolumePercent;
                soundSlider.Value = viewModel.WheelSoundVolumePercent == 42 ? 54 : 42;
                window.RootGrid.UpdateLayout();
                await Task.Delay(30);
                Require(viewModel.WheelSoundVolumePercent == soundSlider.Value
                    && viewModel.WheelHoverVolumePercent == hoverBeforeMasterEdit,
                    "Editing the master volume changed hover volume or failed to update the model.");
                double masterBeforeHoverEdit = viewModel.WheelSoundVolumePercent;
                foreach (double boundary in new[] { 0d, 200d })
                {
                    hoverSlider.Value = boundary;
                    window.RootGrid.UpdateLayout();
                    await Task.Delay(30);
                    Require(viewModel.WheelHoverVolumePercent == boundary
                        && viewModel.WheelHoverVolumeText == $"{boundary:0}%"
                        && FindTextBlock(window.RootGrid, viewModel.WheelHoverVolumeText) != null
                        && viewModel.WheelSoundVolumePercent == masterBeforeHoverEdit
                        && viewModel.WheelSoundsEnabled,
                        "Hover volume failed a boundary binding or changed other sound settings.");
                }
                soundToggle.IsOn = false;
                window.RootGrid.UpdateLayout();
                await Task.Delay(30);
                Require(!viewModel.WheelSoundsEnabled && !soundSlider.IsEnabled && !hoverSlider.IsEnabled
                    && viewModel.WheelHoverVolumePercent == 200
                    && viewModel.WheelSoundVolumePercent == masterBeforeHoverEdit,
                    "Disabling sound failed to disable both sliders or changed their values.");

                viewModel.ReloadFromDisk();
                window.RootGrid.UpdateLayout();
                await Task.Delay(30);
                Require(!viewModel.HasUnsavedChanges && viewModel.SoundSettings == originalSounds
                    && soundToggle.IsOn == originalSounds.Enabled
                    && soundSlider.Value == originalSounds.VolumePercent
                    && hoverSlider.Value == originalSounds.HoverVolumePercent
                    && hoverSlider.IsEnabled == originalSounds.Enabled,
                    "Reload did not restore independent sound control values and enabled state.");
                viewModel.WheelHoverVolumePercent = originalSounds.HoverVolumePercent == 137 ? 149 : 137;
                Require(viewModel.HasUnsavedChanges
                    && viewModel.WheelSoundVolumePercent == originalSounds.VolumePercent
                    && viewModel.WheelSoundsEnabled == originalSounds.Enabled
                    && viewModel.RefreshSettings == originalRefresh && viewModel.ReclaimSettings == originalReclaim,
                    "A hover-only edit failed to become dirty or changed unrelated settings.");

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
                window.RootGrid.UpdateLayout();
                await Task.Delay(30);
                Require(!viewModel.HasUnsavedChanges && viewModel.ReclaimSettings == originalReclaim
                    && viewModel.RefreshSettings == originalRefresh && viewModel.SoundSettings == originalSounds
                    && hoverSlider.Value == originalSounds.HoverVolumePercent,
                    "Reload did not restore the saved opening and outward settings.");
                return new { Passed = true, BilingualHeaders = 4, OriginalNavigationPreserved = true,
                    OpeningDidNotChangeSettings = true, PulseSliderTracksTotal = true,
                    UnsavedStateAndReload = true, SavedUserConfiguration = false,
                    SoundControls = 3, SoundBindingAndReload = true,
                    HoverVolumeTwoWayBinding = true, HoverVolumeRange = "0-200", HoverVolumeDefault = 100,
                    MasterAndHoverIndependent = true, HoverBoundaryBinding = true,
                    SoundEnabledBinding = true, HoverOnlyDirtyState = true, AudioPlayed = false,
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

        private static TextBlock? FindTextBlock(DependencyObject parent, string text)
        {
            if (parent is TextBlock block && block.Text == text) return block;
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
                if (FindTextBlock(VisualTreeHelper.GetChild(parent, index), text) is { } found) return found;
            return null;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
