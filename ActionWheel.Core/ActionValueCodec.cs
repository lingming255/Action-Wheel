using System;
using System.Globalization;
using System.Text;

namespace Action_Wheel.Core
{
    public static class ActionValueCodec
    {
        public const int NoneIndex = 0;
        public const int KeysIndex = 1;
        public const int LaunchIndex = 2;
        public const int FunctionIndex = 3;
        public const int GroupIndex = 4;
        public const int CopyTextIndex = 5;

        public static int KindToIndex(ActionKind kind) => kind switch
        {
            ActionKind.Keys => KeysIndex,
            ActionKind.Launch => LaunchIndex,
            ActionKind.Function => FunctionIndex,
            ActionKind.Group => GroupIndex,
            ActionKind.CopyText => CopyTextIndex,
            _ => NoneIndex,
        };

        public static ActionKind IndexToKind(int index) => index switch
        {
            KeysIndex => ActionKind.Keys,
            LaunchIndex => ActionKind.Launch,
            FunctionIndex => ActionKind.Function,
            GroupIndex => ActionKind.Group,
            CopyTextIndex => ActionKind.CopyText,
            _ => ActionKind.None,
        };

        public static string KindToString(ActionKind kind) => kind switch
        {
            ActionKind.Keys => "keys",
            ActionKind.Launch => "launch",
            ActionKind.Function => "function",
            ActionKind.Group => "group",
            ActionKind.CopyText => "copy",
            _ => "none",
        };

        public static bool TryParseKind(string? value, out ActionKind kind)
        {
            kind = value?.ToLowerInvariant() switch
            {
                "none" => ActionKind.None,
                "keys" => ActionKind.Keys,
                "launch" => ActionKind.Launch,
                "function" => ActionKind.Function,
                "group" => ActionKind.Group,
                "copy" => ActionKind.CopyText,
                _ => (ActionKind)(-1),
            };
            return kind != (ActionKind)(-1);
        }

        public static bool TryConvertGlyph(string? hex, out string text)
        {
            text = string.Empty;
            if (string.IsNullOrWhiteSpace(hex)
                || !int.TryParse(hex.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int codePoint)
                || !Rune.IsValid(codePoint))
                return false;
            text = char.ConvertFromUtf32(codePoint);
            return true;
        }
    }
}
