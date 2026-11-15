using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Action_Wheel.Core;

namespace Action_Wheel.Services
{
    public sealed record QuickAddPage(string Url, string Title);

    /// <summary>Optional read-only page provider; never injects keys or changes the clipboard.</summary>
    public interface IQuickAddPageSource
    {
        Task<QuickAddPage?> TryReadAsync(IntPtr originalWindow, CancellationToken cancellationToken);
    }

    public sealed class QuickAddSession : IDisposable
    {
        public IReadOnlyList<ActionItem> Actions { get; }
        public IReadOnlyList<QuickAddDestination> Destinations { get; }
        public string ProfileName { get; }
        public string? PreviewDirectory { get; }
        private readonly string _configPath;
        private readonly byte[] _configBytes;
        private readonly string? _profilePath;
        private readonly byte[]? _profileBytes;

        public QuickAddSession(bool preview = false)
        {
            var configuration = ActionConfig.LoadDetailed();
            if (configuration.Status == ConfigStatus.Rejected)
                throw new InvalidOperationException("当前配置无法读取，快捷添加不会覆盖它：" + configuration.Error);
            _configPath = configuration.Path;
            _configBytes = File.ReadAllBytes(_configPath);
            // Parse the bytes actually snapshotted, rather than a previous read before another
            // process may have changed the file.
            Actions = ActionConfig.Parse(System.Text.Encoding.UTF8.GetString(_configBytes).TrimStart('\uFEFF'));
            if (!ActionConfig.TryValidate(Actions, out string validationError))
                throw new InvalidOperationException(validationError);
            Destinations = QuickAddActions.Destinations(Actions);
            ProfileName = ActiveProfileSettings.Load();
            var library = new ProfileLibrary();
            _profilePath = library.PathFor(ProfileName);
            if (File.Exists(_profilePath))
                _profileBytes = File.ReadAllBytes(_profilePath);
            else
                _profilePath = null;
            if (preview)
            {
                PreviewDirectory = Path.Combine(Path.GetTempPath(), "ActionWheelQuickAddPreview-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(PreviewDirectory);
                _configPath = Path.Combine(PreviewDirectory, "actions.json");
                File.WriteAllBytes(_configPath, _configBytes);
                if (_profileBytes != null)
                {
                    _profilePath = Path.Combine(PreviewDirectory, "profile.xml");
                    File.WriteAllBytes(_profilePath, _profileBytes);
                }
            }
        }

        public bool TrySave(QuickAddDestination destination, ActionItem addition, out string error)
        {
            if (PreviewDirectory == null && (!string.Equals(_configPath, ActionConfig.ActiveConfigPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(ProfileName, ActiveProfileSettings.Load(), StringComparison.Ordinal)))
            {
                error = "当前轮盘配置已切换，请关闭并重新打开快捷添加。";
                return false;
            }
            // A newly-created saved profile must also be incorporated instead of being silently
            // left stale. Opening a fresh session is the only unambiguous way to select that target.
            if (PreviewDirectory == null && _profilePath == null && File.Exists(new ProfileLibrary().PathFor(ProfileName)))
            {
                error = "当前配置档刚被保存，请关闭并重新打开快捷添加。";
                return false;
            }
            return QuickAddActions.TryApply(Actions, destination, addition, out var updated, out error)
                && QuickAddActions.TryCommit(_configPath, _configBytes, _profilePath, _profileBytes, updated, out error);
        }

        public void Dispose()
        {
            if (PreviewDirectory != null)
            {
                try { Directory.Delete(PreviewDirectory, recursive: true); } catch { }
            }
        }
    }
}
