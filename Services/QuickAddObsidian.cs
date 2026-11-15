using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Action_Wheel.Services
{
    public sealed record QuickAddVault(string Path, bool IsOpen)
    {
        public override string ToString() => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) + (IsOpen ? "（已打开）" : string.Empty);
    }

    public sealed record QuickAddNote(string FullPath, string RelativePath)
    {
        public string Name => System.IO.Path.GetFileNameWithoutExtension(FullPath);
        public override string ToString() => Name + "  ·  " + RelativePath;
    }

    public static class QuickAddObsidian
    {
        public static IReadOnlyList<QuickAddVault> DiscoverVaults(string? configPath = null)
        {
            configPath ??= System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obsidian", "obsidian.json");
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(configPath));
                if (!document.RootElement.TryGetProperty("vaults", out var vaults))
                    return Array.Empty<QuickAddVault>();
                return vaults.EnumerateObject()
                    .Where(property => property.Value.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String)
                    .Select(property => new QuickAddVault(property.Value.GetProperty("path").GetString()!,
                        property.Value.TryGetProperty("open", out var open) && open.ValueKind == JsonValueKind.True))
                    .Where(vault => Directory.Exists(vault.Path))
                    .DistinctBy(vault => vault.Path, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(vault => vault.IsOpen).ThenBy(vault => vault.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch (Exception) { return Array.Empty<QuickAddVault>(); }
        }

        /// <summary>File names only: no note bodies are read and no index is written to the vault.</summary>
        public static IReadOnlyList<QuickAddNote> ReadNotes(QuickAddVault vault, CancellationToken cancellationToken)
        {
            var result = new List<QuickAddNote>();
            var pending = new Stack<string>();
            pending.Push(vault.Path);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string directory = pending.Pop();
                try
                {
                    foreach (string path in Directory.EnumerateFiles(directory, "*.md"))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                        result.Add(new(path, System.IO.Path.GetRelativePath(vault.Path, path)));
                    }
                    foreach (string child in Directory.EnumerateDirectories(directory))
                    {
                        string name = System.IO.Path.GetFileName(child);
                        if (name.StartsWith('.') || name is "node_modules" or "bin" or "obj"
                            || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                            continue;
                        pending.Push(child);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return result.OrderBy(note => note.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public static IReadOnlyList<QuickAddNote> Search(IReadOnlyList<QuickAddNote> notes, string query, int limit = 100)
        {
            var words = query.Trim().Replace('\\', '/').Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return notes.Where(note => words.All(word => note.RelativePath.Replace('\\', '/').Contains(word, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(note => words.Length > 0 && note.Name.StartsWith(words[0], StringComparison.OrdinalIgnoreCase))
                .ThenBy(note => note.RelativePath, StringComparer.OrdinalIgnoreCase).Take(limit).ToArray();
        }
    }
}
