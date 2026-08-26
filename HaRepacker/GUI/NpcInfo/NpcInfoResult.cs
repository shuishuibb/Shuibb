using System.Collections.Generic;
using System.Text;

namespace HaRepacker.GUI.NpcInfo
{
    /// <summary>
    /// Read-only summary of one NPC WzImage, for the "NPC 詳細資訊" context-menu feature. Pure
    /// data - no WZ access, no UI - so it can be unit tested and formatted independently of
    /// NpcInfoWindow. Mirrors HaRepacker\GUI\MapObjectInfo\MapObjectInfoResult's shape.
    /// </summary>
    public sealed class NpcInfoResult
    {
        private const string NoValuePlaceholder = "沒有值";

        public string NpcId { get; }
        public string NpcName { get; }
        public IReadOnlyList<string> StringExtras { get; }
        public string WzPath { get; }
        public IReadOnlyList<string> Animations { get; }

        public NpcInfoResult(
            string npcId,
            string npcName,
            IReadOnlyList<string> stringExtras,
            string wzPath,
            IReadOnlyList<string> animations)
        {
            NpcId = npcId;
            NpcName = npcName;
            StringExtras = stringExtras;
            WzPath = wzPath;
            Animations = animations;
        }

        public static string DisplayScalar(string value)
        {
            return string.IsNullOrEmpty(value) ? NoValuePlaceholder : value;
        }

        public static string JoinForDisplay(IReadOnlyList<string> values)
        {
            return values == null || values.Count == 0 ? NoValuePlaceholder : string.Join("\n", values);
        }

        /// <summary>
        /// Plain-text rendering of all five fields, in the fixed order the window displays them.
        /// Used by both the window's section text and the "複製" button.
        /// </summary>
        public string ToClipboardText()
        {
            StringBuilder sb = new StringBuilder();
            AppendScalarSection(sb, "NPC ID", NpcId);
            AppendScalarSection(sb, "NPC 名稱", NpcName);
            AppendListSection(sb, "NPC String 額外資訊", StringExtras);
            AppendScalarSection(sb, "NPC WZ 路徑", WzPath);
            AppendListSection(sb, "動作 / Animation 名稱", Animations, isLast: true);
            return sb.ToString();
        }

        private static void AppendScalarSection(StringBuilder sb, string header, string value, bool isLast = false)
        {
            sb.AppendLine(header);
            sb.AppendLine(DisplayScalar(value));
            if (!isLast)
                sb.AppendLine();
        }

        private static void AppendListSection(StringBuilder sb, string header, IReadOnlyList<string> values, bool isLast = false)
        {
            sb.AppendLine(header);
            if (values == null || values.Count == 0)
            {
                sb.AppendLine(NoValuePlaceholder);
            }
            else
            {
                foreach (string value in values)
                    sb.AppendLine(value);
            }
            if (!isLast)
                sb.AppendLine();
        }
    }
}
