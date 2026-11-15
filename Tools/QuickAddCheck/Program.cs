using System.Text;
using System.Text.Json;
using Action_Wheel.Core;
using Action_Wheel.Services;

int failures = 0;
void Check(string name, bool condition)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {name}");
    if (!condition) failures++;
}

string directory = Path.Combine(Path.GetTempPath(), "ActionWheelQuickAddCheck-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var actions = Enumerable.Range(0, 9).Select(tag => new ActionItem
    {
        Tag = tag, Label = "Button " + tag, Kind = ActionKind.None, Glyph = "F0AC",
    }).ToList();
    actions[2] = actions[2] with
    {
        Kind = ActionKind.Group, Label = "Links", Foreground = "#123456", IconScale = 1.2,
        GroupChildren = new[] { new ActionItem { Tag = 0, Kind = ActionKind.Launch, Label = "Old", Value = "https://old.example/", Glyph = "F0AC" } },
    };
    var addition = new ActionItem { Kind = ActionKind.Launch, Label = "New", Value = "https://new.example/", Glyph = "F0AC" };
    actions[3] = actions[3] with { HoldKind = ActionKind.Keys, HoldValue = "Ctrl+C" };
    var destinations = QuickAddActions.Destinations(actions);
    Check("Occupied main/group child destinations are explicitly described as replacements",
        destinations.Any(item => item.Tag == 2 && item.ChildIndex == null && item.ReplacesExisting && item.Description.Contains("整个分组"))
        && destinations.Any(item => item.Tag == 2 && item.ChildIndex == 0 && item.ReplacesExisting));
    Check("A button with only a hold action is occupied and requires replacement confirmation",
        destinations.Single(item => item.Tag == 3 && item.ChildIndex == null).ReplacesExisting);
    Check("Append into group retains all nine main actions and parent visual fields",
        QuickAddActions.TryApply(actions, destinations.Single(item => item.Tag == 2 && item.ChildIndex == -1), addition, out var appended, out _)
        && appended.Count == 9 && appended[2].GroupChildren.Count == 2 && appended[2].GroupChildren[1].Tag == 1
        && appended[2].Foreground == "#123456" && appended[2].IconScale == 1.2 && appended[2].GroupChildren[0] == actions[2].GroupChildren[0]);
    Check("Replacing one child preserves siblings and parent", QuickAddActions.TryApply(appended,
        new QuickAddDestination(2, 0, "replace", true), addition, out var replaced, out _)
        && replaced[2].GroupChildren[0].Label == "New" && replaced[2].GroupChildren[1] == appended[2].GroupChildren[1]);
    var full = actions.ToList();
    full[2] = full[2] with { GroupChildren = Enumerable.Range(0, 8).Select(i => addition with { Tag = i }).ToArray() };
    Check("Full groups do not offer append and reject stale append selections",
        !QuickAddActions.Destinations(full).Any(item => item.Tag == 2 && item.ChildIndex == -1)
        && !QuickAddActions.TryApply(full, new QuickAddDestination(2, -1, "append", false), addition, out _, out _));
    Check("Web links normalize shorthand and reject executable/protocol credentials",
        QuickAddActions.TryWebLink("example.com/path?q=中文", out string normalized, out string label)
        && normalized.StartsWith("https://example.com/") && label == "example.com"
        && !QuickAddActions.TryWebLink("javascript:alert(1)", out _, out _)
        && !QuickAddActions.TryWebLink("https://username:password@example.com", out _, out _)
        && !QuickAddActions.TryWebLink("file:///C:/private.txt", out _, out _));
    string notePath = Path.Combine(directory, "中文 & #", "笔记 名称.md");
    string uri = QuickAddActions.ObsidianUri(notePath);
    Check("Obsidian URI round trips the full path including Chinese, spaces, ampersands, and hash",
        Uri.UnescapeDataString(uri["obsidian://open?path=".Length..]) == Path.GetFullPath(notePath));
    Check("Pasted standard Obsidian open links are accepted, unrelated actions/protocols rejected",
        QuickAddActions.TryLaunchLink(uri, out _, out string noteLabel) && noteLabel == "笔记 名称"
        && QuickAddActions.TryLaunchLink("obsidian://open?vault=Example&file=Notes%2FWeekly", out _, out noteLabel) && noteLabel == "Weekly"
        && !QuickAddActions.TryLaunchLink("obsidian://delete?path=C%3A%5Ctest.md", out _, out _)
        && !QuickAddActions.TryLaunchLink("obsidian://open?path=relative.md", out _, out _)
        && !QuickAddActions.TryLaunchLink("javascript:alert(1)", out _, out _));
    Check("Clipboard prefill requires an explicit supported URL and never guesses copied words or bare domains",
        !QuickAddActions.TryClipboardLink("普通中文词", out _, out _)
        && !QuickAddActions.TryClipboardLink("example.com", out _, out _)
        && QuickAddActions.TryClipboardLink(" https://example.com/page ", out _, out _)
        && QuickAddActions.TryClipboardLink(uri, out _, out _)
        && QuickAddActions.TryLaunchLink("example.com", out _, out _));

    string vault = Path.Combine(directory, "vault");
    Directory.CreateDirectory(Path.Combine(vault, "主题"));
    Directory.CreateDirectory(Path.Combine(vault, ".obsidian"));
    Directory.CreateDirectory(Path.Combine(vault, "node_modules"));
    File.WriteAllText(Path.Combine(vault, "主题", "A笔记.md"), "private note body does not become a search result");
    File.WriteAllText(Path.Combine(vault, ".obsidian", "hidden.md"), "hidden");
    File.WriteAllText(Path.Combine(vault, "node_modules", "ignored.md"), "ignored");
    string obsidianConfig = Path.Combine(directory, "obsidian.json");
    File.WriteAllText(obsidianConfig, JsonSerializer.Serialize(new
    {
        vaults = new Dictionary<string, object> { ["one"] = new { path = vault, open = true }, ["two"] = new { path = Path.Combine(directory, "missing") } },
    }));
    var vaults = QuickAddObsidian.DiscoverVaults(obsidianConfig);
    var notes = QuickAddObsidian.ReadNotes(vaults[0], CancellationToken.None);
    Check("Discovery only lists existing vaults and name indexing skips hidden/dependency directories",
        vaults.Count == 1 && vaults[0].IsOpen && notes.Count == 1);
    Check("Note search accepts multiple path/name words without searching note bodies",
        QuickAddObsidian.Search(notes, "主题 笔记").Count == 1 && QuickAddObsidian.Search(notes, "private").Count == 0
        && QuickAddObsidian.Search(notes, "主题/A笔记").Count == 1);

    string config = Path.Combine(directory, "actions.json");
    string profile = Path.Combine(directory, "profile.xml");
    ActionConfig.SaveTo(config, actions, out _);
    ProfileXml.Save(profile, actions, out _);
    byte[] oldConfig = File.ReadAllBytes(config);
    byte[] oldProfile = File.ReadAllBytes(profile);
    Check("Commit updates active JSON and saved profile together", QuickAddActions.TryCommit(config, oldConfig, profile, oldProfile, appended, out _)
        && ActionConfig.Parse(File.ReadAllText(config))[2].GroupChildren.Count == 2
        && ProfileXml.TryLoad(profile, out var savedProfile, out _) && savedProfile[2].GroupChildren.Count == 2);
    byte[] latestConfig = File.ReadAllBytes(config);
    byte[] latestProfile = File.ReadAllBytes(profile);
    Check("Stale session cannot overwrite newer active JSON", !QuickAddActions.TryCommit(config, oldConfig, profile, oldProfile, actions, out _)
        && File.ReadAllBytes(config).AsSpan().SequenceEqual(latestConfig) && File.ReadAllBytes(profile).AsSpan().SequenceEqual(latestProfile));
    File.AppendAllText(profile, "\n");
    Check("External saved-profile edit also blocks the commit", !QuickAddActions.TryCommit(config, latestConfig, profile, latestProfile, actions, out _)
        && File.ReadAllBytes(config).AsSpan().SequenceEqual(latestConfig));

    // Windows denies replacement of the live file while it is open without Delete sharing.
    // The profile write goes through first, making this an actual rollback-after-partial-save check.
    if (OperatingSystem.IsWindows())
    {
        oldConfig = File.ReadAllBytes(config);
        oldProfile = File.ReadAllBytes(profile);
        using (var held = new FileStream(config, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Check("Failure replacing live JSON restores saved profile byte for byte",
                !QuickAddActions.TryCommit(config, oldConfig, profile, oldProfile, actions, out _)
                && File.ReadAllBytes(config).AsSpan().SequenceEqual(oldConfig)
                && File.ReadAllBytes(profile).AsSpan().SequenceEqual(oldProfile));
        }
    }

    // Preview uses a temporary copy of the real current config, then exercises the actual save API.
    // It never writes real actions, preferences, profiles or the Obsidian vault.
    string realConfig = ActionConfig.ActiveConfigPath;
    if (File.Exists(realConfig))
    {
        byte[] before = File.ReadAllBytes(realConfig);
        using (var preview = new QuickAddSession(preview: true))
        {
            Check("Preview has an isolated directory and can actually save", preview.PreviewDirectory != null
                && preview.TrySave(preview.Destinations.First(), addition, out _)
                && File.ReadAllBytes(realConfig).AsSpan().SequenceEqual(before));
        }
    }
}
finally
{
    Directory.Delete(directory, recursive: true);
}
return failures == 0 ? 0 : 1;
