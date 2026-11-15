using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Action_Wheel.Core
{
    /// <summary>A location explicitly chosen by the user; -1 appends to an existing group.</summary>
    public sealed record QuickAddDestination(int Tag, int? ChildIndex, string Description, bool ReplacesExisting)
    {
        public override string ToString() => Description;
    }

    public static class QuickAddActions
    {
        private static readonly string[] Positions = { "中心", "上", "右上", "右", "右下", "下", "左下", "左", "左上" };

        public static IReadOnlyList<QuickAddDestination> Destinations(IReadOnlyList<ActionItem> actions)
        {
            var result = new List<QuickAddDestination>();
            foreach (var item in actions.OrderBy(item => item.Tag))
            {
                string position = Positions[item.Tag];
                string label = string.IsNullOrWhiteSpace(item.Label) ? "未命名" : item.Label;
                bool occupied = item.Kind != ActionKind.None || item.HoldKind != ActionKind.None;
                result.Add(new(item.Tag, null,
                    occupied ? $"{position} · 替换「{label}」{(item.Kind == ActionKind.Group ? $"（整个分组，共 {item.GroupChildren.Count} 项）" : item.HoldKind != ActionKind.None ? "（含长按动作）" : string.Empty)}"
                        : $"{position} · 空位置", occupied));
                if (item.Kind != ActionKind.Group)
                    continue;
                if (item.GroupChildren.Count < ActionItem.MaxGroupChildren)
                    result.Add(new(item.Tag, -1, $"{position}「{label}」· 添加到分组（{item.GroupChildren.Count}/{ActionItem.MaxGroupChildren}）", false));
                for (int i = 0; i < item.GroupChildren.Count; i++)
                {
                    var child = item.GroupChildren[i];
                    result.Add(new(item.Tag, i, $"{position}「{label}」· 替换子项 {i + 1}「{child.Label}」",
                        child.Kind != ActionKind.None || child.HoldKind != ActionKind.None));
                }
            }
            return result;
        }

        public static bool TryApply(IReadOnlyList<ActionItem> actions, QuickAddDestination destination,
            ActionItem addition, out IReadOnlyList<ActionItem> updated, out string error)
        {
            updated = actions;
            error = string.Empty;
            var result = actions.ToList();
            int index = result.FindIndex(item => item.Tag == destination.Tag);
            if (index < 0)
            {
                error = "所选位置已不存在，请重新打开快捷添加。";
                return false;
            }
            if (destination.ChildIndex == null)
                result[index] = addition with { Tag = destination.Tag };
            else
            {
                var parent = result[index];
                int childIndex = destination.ChildIndex.Value;
                if (parent.Kind != ActionKind.Group || childIndex < -1 || childIndex >= parent.GroupChildren.Count)
                {
                    error = "所选分组位置已变化，请重新打开快捷添加。";
                    return false;
                }
                var children = parent.GroupChildren.ToList();
                if (childIndex == -1)
                {
                    if (children.Count >= ActionItem.MaxGroupChildren)
                    {
                        error = "这个分组已满，请选择其他位置。";
                        return false;
                    }
                    children.Add(addition with { Tag = children.Count });
                }
                else
                    children[childIndex] = addition with { Tag = children[childIndex].Tag };
                result[index] = parent with { GroupChildren = children };
            }
            if (!ActionConfig.TryValidate(result, out error))
                return false;
            updated = result;
            return true;
        }

        public static bool TryWebLink(string input, out string url, out string suggestedLabel)
        {
            url = string.Empty;
            suggestedLabel = string.Empty;
            string value = input.Trim();
            if (value.Length == 0 || value.Any(char.IsWhiteSpace))
                return false;
            if (!value.Contains("://", StringComparison.Ordinal))
                value = "https://" + value;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
                return false;
            url = uri.AbsoluteUri;
            suggestedLabel = uri.Host;
            return true;
        }

        /// <summary>Only ordinary web URLs and Obsidian's standard open-note action are accepted.</summary>
        public static bool TryLaunchLink(string input, out string url, out string suggestedLabel)
        {
            if (TryWebLink(input, out url, out suggestedLabel))
                return true;
            try
            {
                if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)
                    || !string.Equals(uri.Scheme, "obsidian", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(uri.Host, "open", StringComparison.OrdinalIgnoreCase)
                    || (uri.AbsolutePath.Length > 0 && uri.AbsolutePath != "/")
                    || !string.IsNullOrEmpty(uri.UserInfo))
                    return false;
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string field in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] pair = field.Split('=', 2);
                    if (pair.Length == 2)
                        values[Uri.UnescapeDataString(pair[0])] = Uri.UnescapeDataString(pair[1].Replace('+', ' '));
                }
                string note;
                if (values.TryGetValue("path", out string? path) && Path.IsPathFullyQualified(path))
                    note = path;
                else if (values.TryGetValue("vault", out string? vault) && !string.IsNullOrWhiteSpace(vault)
                    && values.TryGetValue("file", out string? file) && !string.IsNullOrWhiteSpace(file))
                    note = file;
                else return false;
                url = uri.AbsoluteUri;
                suggestedLabel = Path.GetFileNameWithoutExtension(note.Replace('/', Path.DirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(suggestedLabel)) suggestedLabel = "Obsidian 笔记";
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Clipboard suggestions require an explicit URL; ordinary copied words stay untouched.</summary>
        public static bool TryClipboardLink(string input, out string url, out string suggestedLabel)
        {
            string value = input.Trim();
            bool explicitLink = value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("obsidian://open", StringComparison.OrdinalIgnoreCase);
            url = string.Empty;
            suggestedLabel = string.Empty;
            return explicitLink && TryLaunchLink(value, out url, out suggestedLabel);
        }

        public static string ObsidianUri(string fullNotePath) =>
            "obsidian://open?path=" + Uri.EscapeDataString(Path.GetFullPath(fullNotePath));

        /// <summary>
        /// Optimistic concurrency plus rollback for the two user-owned files. All callers pass the
        /// exact bytes shown when the location was selected; an intervening edit is never overwritten.
        /// The existing ActionConfig writer supplies the durable atomic replacement and .bak.
        /// </summary>
        public static bool TryCommit(string configPath, byte[] expectedConfig, string? profilePath,
            byte[]? expectedProfile, IReadOnlyList<ActionItem> actions, out string error)
        {
            error = string.Empty;
            bool profileWritten = false;
            string? stagedProfile = null;
            byte[]? writtenProfile = null;
            try
            {
                if (profilePath != null && expectedProfile == null)
                {
                    error = "配置档快照缺失，无法安全更新，请重新打开快捷添加。";
                    return false;
                }
                if (!Matches(configPath, expectedConfig)
                    || (profilePath != null && !Matches(profilePath, expectedProfile)))
                {
                    error = "配置在添加期间发生了变化。请关闭并重新打开快捷添加，再选择位置。";
                    return false;
                }
                if (!ActionConfig.TryValidate(actions, out error))
                    return false;
                if (profilePath != null)
                {
                    stagedProfile = profilePath + "." + Guid.NewGuid().ToString("N") + ".quick-add.tmp";
                    if (!ProfileXml.Save(stagedProfile, actions, out error))
                        return false;
                    writtenProfile = File.ReadAllBytes(stagedProfile);
                    if (!Matches(profilePath, expectedProfile) || !Matches(configPath, expectedConfig))
                    {
                        error = "配置刚被其他窗口修改，快捷添加已取消。";
                        return false;
                    }
                    File.Move(stagedProfile, profilePath, overwrite: true);
                    stagedProfile = null;
                    profileWritten = true;
                }
                // Recheck after the profile write, before replacing the live configuration.
                if (!Matches(configPath, expectedConfig) || (profilePath != null && !Matches(profilePath, writtenProfile)))
                    error = "配置刚被其他窗口修改，快捷添加已取消。";
                else if (ActionConfig.SaveTo(configPath, actions, out error))
                    return true;

                if (profileWritten && !TryRestore(profilePath!, expectedProfile!, writtenProfile!, out string rollbackError))
                    error += $" 当前配置未保存；配置档恢复失败：{rollbackError}。请保留窗口并检查文件：{profilePath}";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                if (profileWritten && !TryRestore(profilePath!, expectedProfile!, writtenProfile!, out string rollbackError))
                    error += $" 配置档恢复失败：{rollbackError}";
                return false;
            }
            finally
            {
                if (stagedProfile != null)
                {
                    try { File.Delete(stagedProfile); } catch { }
                }
            }
        }

        private static bool Matches(string path, byte[]? expected) =>
            expected == null ? !File.Exists(path) : File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(expected);

        private static bool TryRestore(string path, byte[] original, byte[] expectedCurrent, out string error)
        {
            string staging = path + "." + Guid.NewGuid().ToString("N") + ".rollback.tmp";
            try
            {
                if (!Matches(path, expectedCurrent))
                {
                    error = "配置档在保存期间被其他窗口修改，已保留该外部版本，没有覆盖。";
                    return false;
                }
                using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(original);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(staging, path, overwrite: true);
                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                try { File.Delete(staging); } catch { }
            }
        }
    }
}
