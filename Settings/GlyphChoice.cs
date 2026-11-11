using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Action_Wheel.Services;

namespace Action_Wheel.Settings
{
    /// <summary>
    /// One entry of the icon picker: an icon-font code point and the character it renders.
    /// </summary>
    public sealed class GlyphChoice
    {
        private GlyphChoice(string code, string preview, string name)
        {
            Code = code;
            Preview = preview;
            Name = name;
        }

        /// <summary>The bundled icon-font code as stored in actions.json, e.g. "F0C5".</summary>
        public string Code { get; }

        /// <summary>The character itself, for display.</summary>
        public string Preview { get; }

        /// <summary>What the icon depicts, where that is known. Empty for the rest of the font.</summary>
        public string Name { get; }

        /// <summary>What the picker shows on hover.</summary>
        public string Tooltip => Name.Length == 0 ? Code : $"{Name}  ·  {Code}";

        /// <summary>
        /// The icons people reach for first, in a sensible reading order rather than by code point.
        /// </summary>
        /// <remarks>
        /// Only glyphs whose meaning is unambiguous are named here. The rest of the font is still
        /// offered through <see cref="All"/> - a name that is merely a good guess would make search
        /// worse than no search, because a wrong hit is harder to recover from than a missing one.
        /// </remarks>
        public static IReadOnlyList<GlyphChoice> Common { get; } = Build(new (string Code, string Name)[]
        {
            // Editing
            ("F0C5", "复制 / Copy"), ("F0EA", "粘贴 / Paste"), ("F0C4", "剪切 / Cut"), ("F040", "编辑 / Edit"),
            ("F0C7", "保存 / Save"), ("F014", "删除 / Delete"), ("F055", "添加 / Add"), ("F056", "移除 / Remove"),
            ("F0E2", "撤销 / Undo"), ("F01E", "重做 / Redo"), ("F00C", "勾选 / Tick"), ("F00D", "关闭 / Close"),
            ("F02F", "打印 / Print"), ("F046", "任务完成 / Task done"), ("F0C6", "附件 / Attach"),

            // Navigation
            ("F0C9", "菜单 / Menu"), ("F015", "主页 / Home"), ("F04F", "返回 / Back"),
            ("F107", "向下 / Chevron down"), ("F106", "向上 / Chevron up"),
            ("F104", "向左 / Chevron left"), ("F105", "向右 / Chevron right"),
            ("F002", "搜索 / Search"), ("F00E", "放大 / Zoom in"), ("F010", "缩小 / Zoom out"),
            ("F009", "网格 / Grid"), ("F00B", "列表 / List"), ("F013", "设置 / Settings"),
            ("EAF1", "筛选 / Filter"), ("EA7C", "更多 / More"), ("F047", "移动 / Move"),

            // Files
            ("F114", "文件夹 / Folder"), ("F115", "打开文件夹 / Folder open"), ("F016", "文件 / File"),
            ("F0F6", "文档 / Document"), ("F02D", "书籍 / Book"), ("F02E", "书签 / Bookmark"),
            ("F019", "下载 / Download"), ("F0EE", "上传 / Upload"), ("F021", "刷新 / Refresh"),
            ("F0ED", "云端下载 / Cloud download"), ("F0C2", "云 / Cloud"), ("EAEF", "归档 / Archive"),

            // Media
            ("F04B", "播放 / Play"), ("F04C", "暂停 / Pause"), ("F04D", "停止 / Stop"),
            ("F048", "上一个 / Previous"), ("F051", "下一个 / Next"), ("F04A", "快退 / Rewind"), ("F04E", "快进 / Fast forward"),
            ("F028", "音量 / Volume"), ("F026", "静音 / Mute"), ("F025", "耳机 / Headphones"),
            ("F03D", "视频 / Video"), ("F030", "相机 / Camera"), ("F03E", "图片 / Image"), ("F001", "音乐 / Music"),

            // System and hardware
            ("F023", "锁定 / Lock"), ("F011", "电源 / Power"), ("F108", "桌面 / Desktop"), ("F109", "笔记本 / Laptop"),
            ("F10B", "手机 / Phone"), ("F11C", "键盘 / Keyboard"), ("EA85", "终端 / Terminal"), ("EACE", "数据库 / Database"),
            ("F0E7", "闪电 / Lightning"), ("EAAF", "错误排查 / Bug"), ("EB44", "火箭 / Rocket"), ("EB53", "盾牌 / Shield"),

            // People, places and messages
            ("F0E0", "邮件 / Mail"), ("F007", "个人 / Person"), ("F0C0", "用户群 / People"),
            ("F041", "位置 / Location"), ("EB01", "地球 / Globe"), ("F0C1", "链接 / Link"), ("F045", "分享 / Share"),
            ("F0F3", "铃铛 / Bell"), ("F017", "时钟 / Clock"), ("EAB0", "日历 / Calendar"), ("F0E5", "评论 / Comment"),
            ("F005", "星标 / Star"), ("F006", "空心星标 / Star outline"), ("F004", "爱心 / Heart"), ("F024", "旗帜 / Flag"),
            ("F059", "帮助 / Help"), ("F05A", "信息 / Info"), ("EA6C", "警告 / Warning"), ("EA87", "错误 / Error"),

            // Logos, which is most of why this font replaced the system's
            ("E70F", "视窗 / Windows"), ("E711", "苹果 / Apple"), ("E712", "Linux"), ("E70E", "安卓 / Android"),
            ("E743", "Chrome"), ("E745", "Firefox"), ("E744", "Edge"), ("E746", "Opera"),
            ("E70C", "Visual Studio Code"), ("E709", "GitHub"), ("E702", "Git"),
            ("E73C", "Python"), ("E738", "Java"), ("E74E", "JavaScript"), ("E739", "Ruby"),
            ("E73D", "PHP"), ("E736", "HTML"), ("E749", "CSS"), ("E73A", "Ubuntu"),
            ("E718", "Node.js"), ("E71E", "npm"), ("E753", "Angular"), ("E755", "Swift"),
            ("E71D", "Django"), ("E70B", "WordPress"), ("E721", "Unity"), ("E722", "树莓派 / Raspberry Pi"),
            ("E76E", "PostgreSQL"), ("E704", "MySQL"), ("E705", "数据库 / Database"),
            ("E731", "谷歌云端硬盘 / Google Drive"), ("E707", "Dropbox"),
        });

        /// <summary>
        /// Every icon the installed font can draw, named where <see cref="Common"/> knows the name.
        /// Falls back to the curated list on a machine with no icon font.
        /// </summary>
        public static IReadOnlyList<GlyphChoice> All { get; } = BuildAll();

        /// <summary>
        /// Filters a list by name or by code. A hex query matches as a prefix so typing "E8" walks
        /// into that part of the font, which is how someone with a code from a web reference arrives.
        /// </summary>
        public static IReadOnlyList<GlyphChoice> Search(IReadOnlyList<GlyphChoice> source, string? query)
        {
            var trimmed = (query ?? string.Empty).Trim();
            if (trimmed.Length == 0)
                return source;

            return source.Where(choice =>
                choice.Code.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)
                || (choice.Name.Length > 0
                    && choice.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        }

        private static IReadOnlyList<GlyphChoice> BuildAll()
        {
            var codePoints = IconFont.CodePoints;
            if (codePoints.Count == 0)
                return Common;

            var names = Common.ToDictionary(choice => choice.Code, choice => choice.Name,
                StringComparer.OrdinalIgnoreCase);

            var list = new List<GlyphChoice>(codePoints.Count);
            foreach (int codePoint in codePoints)
            {
                string code = codePoint.ToString("X4", CultureInfo.InvariantCulture);
                if (TryRender(codePoint, out string preview))
                    list.Add(new GlyphChoice(code, preview, names.GetValueOrDefault(code, string.Empty)));
            }

            return list;
        }

        private static IReadOnlyList<GlyphChoice> Build((string Code, string Name)[] entries)
        {
            var list = new List<GlyphChoice>(entries.Length);

            foreach (var (code, name) in entries)
            {
                if (int.TryParse(code, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int codePoint)
                    && TryRender(codePoint, out string preview))
                {
                    list.Add(new GlyphChoice(code, preview, name));
                }
            }

            return list;
        }

        private static bool TryRender(int codePoint, out string preview)
        {
            preview = string.Empty;
            if (!System.Text.Rune.IsValid(codePoint))
                return false;

            try
            {
                preview = char.ConvertFromUtf32(codePoint);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                // Not a representable code point - just leave it out of the picker.
                return false;
            }
        }
    }
}
