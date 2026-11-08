using System.Text.Json;
using Action_Wheel.Services;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

string markdown = """
    # 常用信息
    ### 电话
    01012345678
    - 01087654321

    ### 邮箱
    help@example.com
    School <office@example.edu>
    student.id
    Email: student.id@example.edu
    ### 服务器
    ssh ubuntu@example.com
    ### 地址
    家：测试路 1 号
    [地图](https://example.com/map)
    """;
var groups = MarkdownQuickInfo.Parse(markdown, "Info");
Check(groups.Select(g => g.Label).SequenceEqual(new[] { "电话", "邮箱", "服务器", "地址" }), "Headings become topics, without an empty document-title group");
Check(groups.Select(g => g.GroupChildren.Count).SequenceEqual(new[] { 2, 4, 1, 2 }), "Blank lines do not create buttons");
Check(groups.SelectMany(g => g.GroupChildren).All(c => c.Kind == ActionKind.CopyText), "Information copies instead of launching");
Check(groups[1].GroupChildren[1].Value == "office@example.edu", "Named email copies the address");
Check(groups[1].GroupChildren[3].Value == "student.id@example.edu", "Email label is not copied");
Check(groups[2].GroupChildren[0].Value == "ssh ubuntu@example.com", "SSH command remains intact");
Check(groups[3].GroupChildren[0].Value == "测试路 1 号", "Chinese label separates display and value");
Check(groups[3].GroupChildren[1].Value == "https://example.com/map", "Markdown link copies URL");
var overflow = MarkdownQuickInfo.Parse("# Items\n" + string.Join('\n', Enumerable.Range(1, 9)), "Info");
Check(overflow.Count == 2 && overflow[1].GroupChildren[0].Value == "9", "Extra children become another topic, never disappear");
var code = MarkdownQuickInfo.Parse("# Commands\n```sh\n# literal comment\necho hello: world\n```", "Info");
Check(code.Count == 1 && code[0].GroupChildren[1].Value == "echo hello: world", "Fenced command lines stay literal");
Check(ActionValueCodec.TryParseKind("copy", out var kind) && kind == ActionKind.CopyText
    && ActionValueCodec.KindToString(kind) == "copy"
    && ActionValueCodec.IndexToKind(ActionValueCodec.KindToIndex(kind)) == kind, "Copy survives kind codecs");

string directory = Path.Combine(Path.GetTempPath(), "ActionWheel-QuickInfoTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    string source = Path.Combine(directory, "info.md");
    string catalog = Path.Combine(directory, QuickInfoCatalog.FileName);
    File.WriteAllText(source, markdown);
    File.WriteAllText(catalog, JsonSerializer.Serialize(new[]
    {
        new { label = "Info", kind = "markdown", target = "info.md" },
        new { label = "Image", kind = "image", target = "image.png" },
    }));
    string copiedSource = Path.Combine(directory, "copy.md");
    File.WriteAllText(copiedSource, markdown);
    var actions = QuickInfoCatalog.Load(out string error, directory);
    Check(error.Length == 0 && actions.Count == 6, "Catalog expands its Markdown source and ignores extra copied Markdown files");
    File.Delete(copiedSource);
    Check(actions.Select(a => a.Tag).SequenceEqual(Enumerable.Range(0, 6)), "Expanded topics keep contiguous ring tags");
    Check(actions[^1].Kind == ActionKind.Launch, "Images retain their launch behavior");
    File.WriteAllText(source, "# Changed\nupdated");
    actions = QuickInfoCatalog.Load(out error, directory);
    Check(actions[1].GroupChildren[0].Value == "updated", "Reload sees source edits");
    File.WriteAllText(source, string.Join('\n', Enumerable.Range(1, 9).Select(i => $"# Topic {i}\nvalue")));
    actions = QuickInfoCatalog.Load(out error, directory);
    Check(error.Length == 0 && actions.Count == 9 && actions[0].Value == QuickInfoCatalog.NextPage,
        "Overflow creates a full first page with a next-page action");
    var nextPage = QuickInfoCatalog.Load(out error, directory, page: 1);
    Check(error.Length == 0 && nextPage.Count == 3 && nextPage[1].Label == "Topic 9"
        && nextPage[2].Label == "Image", "Remaining topics and images stay accessible on the next page");
    Check(nextPage.Select(a => a.Tag).SequenceEqual(new[] { 0, 1, 2 }), "Each page has contiguous tags");
    var wrapped = QuickInfoCatalog.Load(out error, directory, page: 2);
    Check(wrapped[1].Label == "Topic 1", "Next page wraps to the first page");
    File.Delete(source);
    actions = QuickInfoCatalog.Load(out error, directory);
    Check(error.Length > 0 && actions.Count == 1, "Missing Markdown never becomes a launch action");
    File.Delete(catalog);
    File.WriteAllText(source, "# Auto\ncopy me");
    actions = QuickInfoCatalog.Load(out error, directory);
    Check(error.Length == 0 && actions[1].GroupChildren[0].Value == "copy me", "Folder discovery also parses Markdown");
}
finally { Directory.Delete(directory, recursive: true); }

// Optional local source check prints counts only, never personal values.
if (args.Length > 0 && !args[0].StartsWith("--"))
{
    var personal = MarkdownQuickInfo.Parse(File.ReadAllText(args[0]), "Info");
    Check(personal.Count > 0 && personal.SelectMany(g => g.GroupChildren).All(c => c.Kind == ActionKind.CopyText), "Local source produces copy entries");
    Console.WriteLine($"Local source: {personal.Count} topics, {personal.Sum(g => g.GroupChildren.Count)} copy entries");
}
Console.WriteLine($"PASS: {checks} Quick Info checks");

if (args.Length > 0 && args[0] == "--catalog")
{
    var current = QuickInfoCatalog.Load(out string error);
    if (error.Length > 0) throw new InvalidOperationException(error);
    Console.WriteLine("Current catalog: " + string.Join(", ", current.Select(a => $"{a.Tag}:{a.Label} ({a.Kind}, {a.GroupChildren.Count})")));
}
