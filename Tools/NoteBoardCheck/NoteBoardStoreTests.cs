using Action_Wheel.Services;

var root = Path.Combine(Path.GetTempPath(), "ActionWheel-NoteBoardCheck-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    string path = Path.Combine(root, "notes", "noteboard.txt");
    var board = new NoteBoardStore(path);
    Check(!board.TrySave("unread", out _), "A draft must be loaded before it can be saved.");
    Check(board.TryLoad(out string first, out _) && first == "", "A new board loads empty.");
    Check(board.TrySave("", out _) && !File.Exists(path), "Opening and closing creates no empty file.");
    string draft = "第一行：想法 📝\r\nsecond line\n末行";
    Check(board.TrySave(draft, out string saveError), "Unicode draft saves: " + saveError);
    var reopened = new NoteBoardStore(path);
    Check(reopened.TryLoad(out string restored, out _) && restored == draft, "Reopening preserves Unicode and line endings.");
    Check(reopened.TrySave(draft + "\n更多", out _), "A later draft saves.");
    Check(File.ReadAllText(path + ".bak") == draft, "The previous draft remains in the backup.");

    File.WriteAllText(path, "external change");
    Check(!reopened.TrySave("edited in board", out _), "An external change blocks overwrite.");
    Check(File.ReadAllText(path) == "external change", "External content survives a conflicting save.");
    Check(reopened.TrySaveCopy("edited in board", out string recovery, out _) && File.ReadAllText(recovery) == "edited in board",
        "A conflicting draft can be recovered to its own file.");
    Check(File.ReadAllText(path) == "external change", "A recovery copy does not replace the live file.");

    string blockedPath = Path.Combine(root, "blocked");
    Directory.CreateDirectory(blockedPath);
    var unreadable = new NoteBoardStore(blockedPath);
    Check(!unreadable.TryLoad(out _, out _), "A path that cannot be read is reported.");
    Check(!unreadable.TrySave("empty replacement", out _), "A failed load never authorizes replacement.");
    Check(Directory.Exists(blockedPath), "A failed save preserves the original path.");

    string invalidUtf8 = Path.Combine(root, "invalid.txt");
    File.WriteAllBytes(invalidUtf8, new byte[] { 0xC3, 0x28 });
    var invalid = new NoteBoardStore(invalidUtf8);
    Check(!invalid.TryLoad(out _, out _), "Invalid UTF-8 fails rather than replacing bytes with lossy text.");
    Check(!invalid.TrySave("", out _) && File.ReadAllBytes(invalidUtf8).SequenceEqual(new byte[] { 0xC3, 0x28 }),
        "Invalid input remains untouched.");

    string retryParent = Path.Combine(root, "retry");
    var retry = new NoteBoardStore(Path.Combine(retryParent, "noteboard.txt"));
    Check(retry.TryLoad(out _, out _), "The retry board starts empty.");
    File.WriteAllText(retryParent, "parent path is temporarily blocked");
    Check(!retry.TrySave("retry draft", out _), "A failed write is reported.");
    File.Delete(retryParent);
    Check(retry.TrySave("retry draft", out _) && File.ReadAllText(retry.FilePath) == "retry draft",
        "The same draft saves successfully after the filesystem is repaired.");

    string settingsPath = Path.Combine(root, "preferences", "location.json");
    string chosenPath = Path.Combine(root, "chosen", "我的笔记.md");
    Check(retry.TrySaveAs(chosenPath, "saved at chosen place", true, out _, settingsPath), "Saving to a chosen location succeeds.");
    Check(retry.FilePath == chosenPath && File.ReadAllText(chosenPath) == "saved at chosen place", "The chosen file becomes the active draft.");
    Check(NoteBoardLocationSettings.TryLoad(out string remembered, out _, settingsPath) && remembered == chosenPath,
        "The new location is remembered for the next launch.");
    Check(retry.TrySave("later edit", out _) && File.ReadAllText(chosenPath) == "later edit", "Subsequent autosaves use the chosen location.");
    var chosenReopen = new NoteBoardStore(remembered);
    Check(chosenReopen.TryLoad(out string chosenText, out _) && chosenText == "later edit", "Reopening the remembered file restores the draft.");

    string blockedSettings = Path.Combine(root, "blocked-settings");
    Directory.CreateDirectory(blockedSettings);
    string unsuccessfulTarget = Path.Combine(root, "saved-copy.md");
    Check(!retry.TrySaveAs(unsuccessfulTarget, "copy retained", true, out _, blockedSettings), "A location-preference failure is reported.");
    Check(retry.FilePath == chosenPath && File.ReadAllText(unsuccessfulTarget) == "copy retained", "A failed switch retains the old active file and the new copy.");
    Check(retry.TrySave("still old active location", out _) && File.ReadAllText(chosenPath) == "still old active location",
        "Editing remains attached to the previous file after a failed switch.");
    Check(!retry.TrySaveAs(chosenPath, "saved to the same target", true, out _, blockedSettings),
        "A same-target location-preference failure is still reported.");
    Check(retry.FilePath == chosenPath && File.ReadAllText(chosenPath) == "saved to the same target",
        "A successful same-target write remains the active draft despite a preference failure.");
    Check(retry.TrySave("next autosave after preference failure", out _) && File.ReadAllText(chosenPath) == "next autosave after preference failure",
        "The next autosave accepts the updated baseline after a same-target preference failure.");
    string missingTargetPath = Path.Combine(root, "first-same-target.txt");
    var missingTarget = new NoteBoardStore(missingTargetPath);
    Check(missingTarget.TryLoad(out _, out _), "A new same-target draft starts empty.");
    Check(!missingTarget.TrySaveAs(missingTargetPath, "first write", true, out _, blockedSettings),
        "A first-write same-target preference failure is reported.");
    Check(missingTarget.TrySave("second write", out _) && File.ReadAllText(missingTargetPath) == "second write",
        "A newly created same-target file also updates the existence baseline after preference failure.");
    string previewPath = Path.Combine(root, "preview-only.md");
    Check(retry.TrySaveAs(previewPath, "isolated preview", false, out _, blockedSettings), "An isolated preview never writes location preferences.");

    File.WriteAllText(settingsPath, "invalid json");
    Check(!NoteBoardLocationSettings.TryLoad(out _, out _, settingsPath), "Invalid location preferences are surfaced instead of silently replaced.");
    Console.WriteLine("NoteBoardCheck passed: persistence, backup, conflicts, failed reads and Unicode.");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
