using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Action_Wheel.Core
{
    /// <summary>Headings become groups; each nonempty content line becomes a copy action.</summary>
    public static class MarkdownQuickInfo
    {
        public static IReadOnlyList<ActionItem> Parse(string markdown, string defaultLabel)
        {
            var groups = new List<ActionItem>();
            var children = new List<ActionItem>();
            string title = defaultLabel;
            string? fence = null;

            void Flush()
            {
                // ponytail: use the existing eight-child ring; overflow becomes numbered groups.
                for (int start = 0; start < children.Count; start += ActionItem.MaxGroupChildren)
                {
                    string label = start == 0 ? title : $"{title} {start / ActionItem.MaxGroupChildren + 1}";
                    groups.Add(new ActionItem
                    {
                        Label = label, IconText = label, Kind = ActionKind.Group,
                        GroupChildren = children.Skip(start).Take(ActionItem.MaxGroupChildren)
                            .Select((child, index) => child with { Tag = index }).ToArray(),
                    });
                }
                children.Clear();
            }

            using var reader = new StringReader(markdown);
            while (reader.ReadLine() is string raw)
            {
                string line = raw.Trim();
                if (line.StartsWith("```") || line.StartsWith("~~~"))
                {
                    if (fence == null) fence = line[..3];
                    else if (line.StartsWith(fence)) fence = null;
                    continue;
                }
                var heading = Regex.Match(line, @"^#{1,6}\s+(.+?)(?:\s+#+)?$");
                if (fence == null && heading.Success)
                {
                    Flush();
                    title = heading.Groups[1].Value;
                    continue;
                }
                if (line.Length == 0 || (fence == null && Regex.IsMatch(line, @"^(?:-{3,}|\*{3,}|_{3,})$")))
                    continue;

                if (fence == null)
                    line = Regex.Replace(line, @"^(?:[-*+]\s+(?:\[[ xX]\]\s+)?|\d+[.)]\s+)", "");
                string label = line;
                string value = line;
                if (fence == null)
                {
                    var link = Regex.Match(line, @"^\[([^\]]+)\]\(([^)]+)\)$");
                    var email = Regex.Match(line, @"^.*<([^<>\s]+@[^<>\s]+)>$");
                    var pair = Regex.Match(line, @"^([^:：]+)(?:：\s*|:\s+)(.+)$");
                    if (link.Success)
                    {
                        label = link.Groups[1].Value;
                        value = link.Groups[2].Value;
                    }
                    else if (email.Success) value = email.Groups[1].Value;
                    else if (pair.Success)
                    {
                        label = pair.Groups[1].Value;
                        value = pair.Groups[2].Value;
                    }
                }
                children.Add(new ActionItem { Label = label, Kind = ActionKind.CopyText, Value = value });
            }
            Flush();
            return groups;
        }
    }
}
