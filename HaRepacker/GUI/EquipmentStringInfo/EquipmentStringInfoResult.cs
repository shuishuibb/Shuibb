using System.Collections.Generic;
using System.Text;

namespace HaRepacker.GUI.EquipmentStringInfo
{
    /// <summary>
    /// One String.wz's answer for a given item id - either a real match (SourceFileNames has at
    /// least one entry, one per loaded WzFile that produced this exact Name+Extras combination),
    /// or the single "nothing found anywhere" placeholder (SourceFileNames is empty).
    /// </summary>
    public sealed class EquipmentStringSourceResult
    {
        public IReadOnlyList<string> SourceFileNames { get; }
        public string Name { get; }
        public string LogicalPath { get; }
        public IReadOnlyList<string> Extras { get; }

        public EquipmentStringSourceResult(
            IReadOnlyList<string> sourceFileNames,
            string name,
            string logicalPath,
            IReadOnlyList<string> extras)
        {
            SourceFileNames = sourceFileNames;
            Name = name;
            LogicalPath = logicalPath;
            Extras = extras;
        }

        public bool IsPlaceholder => SourceFileNames.Count == 0;
    }

    /// <summary>
    /// Read-only summary of one equipment WzImage, for the "裝備 String 資訊" context-menu
    /// feature. Pure data - no WZ access, no UI - so it can be unit tested and formatted
    /// independently of EquipmentStringInfoWindow. Mirrors the MapObjectInfo/NpcInfo shape.
    /// </summary>
    public sealed class EquipmentStringInfoResult
    {
        private const string NoValuePlaceholder = "沒有值";

        public string ItemId { get; }
        public string EquipWzPath { get; }

        /// <summary>Always has at least one entry - a single placeholder when nothing was found.</summary>
        public IReadOnlyList<EquipmentStringSourceResult> Sources { get; }

        public EquipmentStringInfoResult(string itemId, string equipWzPath, IReadOnlyList<EquipmentStringSourceResult> sources)
        {
            ItemId = itemId;
            EquipWzPath = equipWzPath;
            Sources = sources;
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
        /// Plain-text rendering of the whole summary. Used by both the window's section text and
        /// the "複製" button, so the clipboard always carries every source, not just what's
        /// scrolled into view.
        /// </summary>
        public string ToClipboardText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Item ID");
            sb.AppendLine(DisplayScalar(ItemId));
            sb.AppendLine();
            sb.AppendLine("裝備 WZ 路徑");
            sb.AppendLine(DisplayScalar(EquipWzPath));

            for (int i = 0; i < Sources.Count; i++)
            {
                sb.AppendLine();
                AppendSource(sb, Sources[i], i + 1, Sources.Count > 1);
            }

            return sb.ToString();
        }

        private static void AppendSource(StringBuilder sb, EquipmentStringSourceResult source, int index, bool showSourceHeader)
        {
            // A real match always shows which 來源/file it came from. The single "nothing found
            // anywhere" placeholder (String.wz not loaded, or this id just isn't in it) has no
            // file to name, so it renders as the plain two-line "名稱 / 沒有值" shape the spec
            // calls for instead of an empty "來源 1" header.
            if (!source.IsPlaceholder)
            {
                if (showSourceHeader)
                    sb.AppendLine("來源 " + index);
                sb.AppendLine(string.Join(", ", source.SourceFileNames));
            }

            sb.AppendLine("名稱");
            sb.AppendLine(DisplayScalar(source.Name));
            sb.AppendLine("String.wz logical path");
            sb.AppendLine(DisplayScalar(source.LogicalPath));
            sb.AppendLine("String 額外資訊");
            sb.AppendLine(JoinForDisplay(source.Extras));
        }
    }
}
