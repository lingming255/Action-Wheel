# Quick Info: hover a topic, release to copy

Hold **Ctrl+Alt+Space**, move toward a topic, then move onto one of its satellite buttons and release **Space**. The selected text is copied to the Windows clipboard and the wheel closes. Release on a topic, in the centre, or in a gap to cancel; Esc also cancels. Images and other launch resources still open when selected. Clicking the centre directly opens the QuickInfo folder.

The catalog is `%LOCALAPPDATA%\ActionWheel\QuickInfo\quick-info.json`:

```json
[
  { "label": "常用信息", "kind": "markdown", "target": "D:\\Notes\\常用信息.md" },
  { "label": "课表", "kind": "image", "target": "课表.jpg", "iconText": "课表" }
]
```

Absolute paths and paths relative to the QuickInfo folder are supported. Local Markdown sources expand into topics in document order. Other kinds retain their normal Windows file/URL opening behavior; use `"kind": "file"` to open a Markdown document instead of turning it into copy buttons. Without a catalog, supported files in the QuickInfo folder are discovered automatically, including Markdown topics.

## Markdown format

```markdown
### 电话
01012345678
备用：01087654321

### 邮箱
help@example.com
学校 <office@example.edu>
Email: student@example.edu

### 服务器
ssh ubuntu@example.com

### 地址
家：测试路 1 号
[地图](https://example.com/map)
```

- Each `#` through `######` heading starts a topic; empty topics are omitted.
- Each nonempty content line becomes one copy button. Simple bullet/number markers are removed.
- `Label: value` (a space after the English colon) and `标签：内容` display the label and copy the value.
- A named email in angle brackets copies the email address; a Markdown link copies its destination.
- Plain text and commands are copied literally. Fenced code is split by nonempty line, preserving punctuation and labels inside the fence.
- The wheel reads the source again on every opening. The open wheel retains its snapshot until dismissed.
- Each topic holds eight buttons. Additional entries become numbered continuation topics. Each page has eight main sectors, including image/file entries. Extra topics/resources continue on further pages: keep Space held and click the centre page indicator to advance, then move to an entry and release Space. Every new keyboard gesture starts on page one. Invalid JSON or an unreadable source reports a load error, including the actual catalog path, in the main window.

The Markdown file remains the source of truth and is never rewritten. Parsing is local; it does not call an AI or a network service. This is a line-based quick-info format, not a general Markdown renderer (tables and multiline paragraphs are not combined).

## Manual copy buttons

Ordinary wheel buttons and group children also support **Copy text** in Settings. Set the value to the text you want copied. JSON and profile exports use the action type `copy`. Existing action types remain compatible.

## Checks

```powershell
dotnet run --project Tools\QuickInfoTests\QuickInfoTests.csproj
dotnet run --project Tools\RingGestureTests\RingGestureTests.csproj
dotnet build "Action Wheel.csproj"
```
