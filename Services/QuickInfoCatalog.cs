using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Action_Wheel.Services
{
    /// <summary>
    /// Loads local Markdown topics as copy groups and other resources as launch actions.
    /// </summary>
    public static class QuickInfoCatalog
    {
        public const string FileName = "quick-info.json";
        private const int MaxItems = 8;
        public const string NextPage = "quick-info.next-page";

        public static string DirectoryPath => AppDataPaths.QuickInfoDirectoryPath;
        public static string FilePath => Path.Combine(DirectoryPath, FileName);

        public static IReadOnlyList<ActionItem> Load() => Load(out _);

        public static IReadOnlyList<ActionItem> Load(out string error, string? directoryPath = null, int page = 0)
        {
            error = string.Empty;
            string directory = directoryPath ?? DirectoryPath;
            string catalog = Path.Combine(directory, FileName);
            var centre = new ActionItem
            {
                Tag = 0, Label = "Quick Info", Kind = ActionKind.Launch,
                Value = directory, IconText = "INFO",
            };
            var actions = new List<ActionItem> { centre };

            void AddResource(ActionItem resource, bool markdown)
            {
                var entries = markdown
                    ? MarkdownQuickInfo.Parse(File.ReadAllText(resource.Value), resource.Label)
                    : new[] { resource };
                foreach (var entry in entries)
                {
                    actions.Add(entry with { Tag = actions.Count });
                }
            }

            try
            {
                if (File.Exists(catalog))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(catalog));
                    if (document.RootElement.ValueKind != JsonValueKind.Array)
                        throw new InvalidDataException("Quick Info catalog must be a JSON array.");
                    foreach (var item in document.RootElement.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object
                            || !TryCreateAction(item, actions.Count, directory, out var action))
                            throw new InvalidDataException("Each Quick Info resource needs a label and target.");
                        string kind = ReadString(item, "kind").ToLowerInvariant();
                        bool markdown = kind == "markdown" || (kind.Length == 0
                            && string.Equals(Path.GetExtension(action.Value), ".md", StringComparison.OrdinalIgnoreCase));
                        AddResource(action, markdown);
                    }
                }
                else if (Directory.Exists(directory))
                {
                    foreach (var file in Directory.EnumerateFiles(directory)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                    {
                        string extension = Path.GetExtension(file).ToLowerInvariant();
                        if (!IsQuickInfoFile(extension)) continue;
                        string target = extension == ".url" && TryReadInternetShortcut(file, out var url) ? url : file;
                        AddResource(new ActionItem
                        {
                            Label = Path.GetFileNameWithoutExtension(file), Kind = ActionKind.Launch,
                            Value = target, IconText = IconTextFor(extension),
                        }, extension == ".md");
                    }
                }
            }
            catch (Exception ex)
            {
                error = $"Quick Info could not be loaded: {ex.Message}";
                return new[] { centre with { Label = "Quick Info unavailable" } };
            }
            int pageCount = Math.Max(1, (actions.Count - 2) / MaxItems + 1);
            int pageIndex = ((page % pageCount) + pageCount) % pageCount;
            if (pageCount == 1) return actions;
            return new[] { centre with
            {
                Label = $"下一页 · {pageIndex + 1}/{pageCount}（按住 Space 时点击）",
                Kind = ActionKind.Function, Value = NextPage, IconText = $"{pageIndex + 1}/{pageCount}",
            } }.Concat(actions.Skip(1 + pageIndex * MaxItems).Take(MaxItems)
                .Select((action, index) => action with { Tag = index + 1 })).ToArray();
        }

        public static void EnsureDirectory()
        {
            try { Directory.CreateDirectory(DirectoryPath); }
            catch (Exception) { }
        }

        private static string ReadString(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim() ?? string.Empty
                : string.Empty;

        private static bool TryCreateAction(JsonElement item, int tag, string directory, out ActionItem action)
        {
            action = default!;
            string label = ReadString(item, "label");
            string target = ReadString(item, "target");
            if (label.Length == 0 || target.Length == 0)
                return false;

            string kind = ReadString(item, "kind").ToLowerInvariant();
            bool isLink = kind == "link"
                || (Uri.TryCreate(target, UriKind.Absolute, out var uri) && !uri.IsFile);
            if (!isLink)
            {
                target = Path.IsPathRooted(target)
                    ? target
                    : Path.Combine(directory, target);
            }

            action = new ActionItem
            {
                Tag = tag,
                Label = label,
                Kind = ActionKind.Launch,
                Value = target,
                IconText = ReadString(item, "iconText"),
                Glyph = ReadString(item, "glyph"),
                IconPath = ReadString(item, "iconPath"),
            };
            return true;
        }

        private static bool IsQuickInfoFile(string extension) => extension is
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".md" or ".pdf" or ".txt" or ".url";

        private static string IconTextFor(string extension) => extension switch
        {
            ".md" => "MD",
            ".url" => "URL",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" => "IMG",
            _ => "FILE",
        };

        private static bool TryReadInternetShortcut(string path, out string url)
        {
            url = string.Empty;
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (!line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                        continue;

                    url = line[4..].Trim();
                    return Uri.TryCreate(url, UriKind.Absolute, out _);
                }
            }
            catch (Exception) { }

            return false;
        }
    }
}
