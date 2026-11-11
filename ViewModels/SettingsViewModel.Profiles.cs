using System;
using System.Linq;
using Action_Wheel.Core;
using Action_Wheel.Services;
using InfoBarSeverity = Action_Wheel.ViewModels.StatusLevel;

namespace Action_Wheel.ViewModels
{
    public sealed partial class SettingsViewModel
    {
        private string _profileName = string.Empty;
        private string? _selectedProfile;
        private string _loadedProfileName = "Main configuration";
        private string _activeProfileName = "Main configuration";

        public string ActiveProfileText => $"当前配置 / Active profile: {_activeProfileName}";
        public string ProfileCountText => $"已保存配置 / Saved profiles: {Profiles.Count}";

        public string ProfileName { get => _profileName; set => Set(ref _profileName, value); }
        public string? SelectedProfile
        {
            get => _selectedProfile;
            set
            {
                if (!Set(ref _selectedProfile, value)) return;
                LoadProfileCommand.RaiseCanExecuteChanged();
                DeleteProfileCommand.RaiseCanExecuteChanged();
                RenameProfileCommand.RaiseCanExecuteChanged();
            }
        }

        private void RefreshProfiles()
        {
            Profiles.Clear();
            if (_profileLibrary.TryList(out var names, out string error))
                foreach (string name in names) Profiles.Add(name);
            else
            {
                ShowStatus(InfoBarSeverity.Error, "无法读取配置列表 / Profiles unavailable", error);
            }
            Raise(nameof(ProfileCountText));

            var functionChoices = WindowsFunctions.Choices.Concat(Profiles.Select(WindowsFunctions.ProfileChoice)).ToArray();
            foreach (var item in Items)
                item.RefreshFunctionChoices(functionChoices);
        }

        private void SaveNamedProfile()
        {
            string name = ProfileLibrary.SafeName(ProfileName);
            if (string.IsNullOrWhiteSpace(name))
            {
                ShowStatus(InfoBarSeverity.Warning, "请输入配置名称 / Profile name required", "请先输入有效的配置名称。/ Enter a valid profile name first.");
                return;
            }
            var problems = Validate();
            if (problems.Count > 0)
            {
                ShowStatus(InfoBarSeverity.Error, "无法保存配置 / Could not save the profile", string.Join("  •  ", problems));
                return;
            }
            if (!_profileLibrary.TrySave(name, Snapshot(), out string error))
            {
                ShowStatus(InfoBarSeverity.Error, "无法保存配置 / Could not save the profile", error);
                return;
            }
            RefreshProfiles();
            SelectedProfile = name;
        }

        private void LoadNamedProfile()
        {
            if (SelectedProfile == null) return;
            string profileName = SelectedProfile;
            if (!_profileLibrary.TryLoad(profileName, out var actions, out string error))
            {
                ShowStatus(InfoBarSeverity.Error, "无法载入配置 / Could not load the profile", error);
                return;
            }

            CaptureUndoPoint();
            LoadItems(actions);

            if (!ActionConfig.Save(actions, out string configError))
            {
                ShowStatus(InfoBarSeverity.Error, "无法启用配置 / Could not activate the profile", configError);
                return;
            }

            if (!ActiveProfileSettings.Save(profileName, out string labelError))
            {
                ShowStatus(InfoBarSeverity.Error, "配置已载入，但有错误 / Profile loaded with an error",
                    $"按钮已应用，但当前配置名称无法保存。/ Buttons applied, but the active profile name could not be saved: {labelError}");
                ActionsSaved?.Invoke(this, EventArgs.Empty);
                return;
            }

            _profileLibrary.TryTouch(profileName);
            SetActiveProfile(profileName);
            CaptureUndoPoint();
            ActionsSaved?.Invoke(this, EventArgs.Empty);
            ShowStatus(InfoBarSeverity.Success, "配置已启用 / Profile activated",
                $"已启用“{profileName}”，保存将更新此配置。/ '{profileName}' is active. Save will update this profile.");
        }

        private void DeleteNamedProfile()
        {
            if (SelectedProfile == null) return;
            string name = SelectedProfile;
            if (string.Equals(name, _activeProfileName, StringComparison.OrdinalIgnoreCase))
            {
                ShowStatus(InfoBarSeverity.Warning, "未删除当前配置 / Active profile not deleted",
                    "请先载入其他配置，再删除此配置。/ Load another profile before deleting this one.");
                return;
            }
            if (!_profileLibrary.TryDelete(name, out string error))
            {
                ShowStatus(InfoBarSeverity.Error, "配置未删除 / Profile not deleted", error);
                return;
            }
            SelectedProfile = null;
            RefreshProfiles();
            ShowStatus(InfoBarSeverity.Informational, "配置已删除 / Profile deleted", "当前配置未更改。/ The active configuration was not changed.");
        }

        private void RenameNamedProfile()
        {
            if (SelectedProfile == null) return;
            string oldName = SelectedProfile;
            string newName = ProfileLibrary.SafeName(ProfileName);
            if (!_profileLibrary.TryRename(oldName, newName, out string error))
            {
                ShowStatus(InfoBarSeverity.Error, "配置未重命名 / Profile not renamed", error);
                return;
            }

            string? renameWarning = null;
            if (string.Equals(oldName, _activeProfileName, StringComparison.OrdinalIgnoreCase))
            {
                if (!ActiveProfileSettings.Save(newName, out string labelError))
                {
                    renameWarning = $"当前配置名称无法更新 / The active profile label could not be updated: {labelError}";
                }
                else
                {
                    SetActiveProfile(newName);
                    ActionsSaved?.Invoke(this, EventArgs.Empty);
                }
            }
            RefreshProfiles();
            SelectedProfile = newName;
            if (renameWarning == null)
                ShowStatus(InfoBarSeverity.Success, "配置已重命名 / Profile renamed", newName);
            else
                ShowStatus(InfoBarSeverity.Warning, "配置已重命名，但有错误 / Profile renamed with an error", renameWarning);
        }

        private void SetActiveProfile(string name)
        {
            _activeProfileName = string.IsNullOrWhiteSpace(name) ? "Main configuration" : name;
            _loadedProfileName = _activeProfileName;
            Raise(nameof(ActiveProfileText));
        }
    }
}
