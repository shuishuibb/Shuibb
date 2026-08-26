using System.Collections.Generic;
using System.Text;

namespace HaRepacker.GUI.MapObjectInfo
{
    /// <summary>
    /// Read-only aggregate summary of one or more selected map WzImages, for the "地圖物件資訊"
    /// (map object info) context-menu feature. Pure data - no WZ access, no UI - so it can be
    /// unit tested and formatted independently of MapObjectInfoWindow.
    /// </summary>
    public sealed class MapObjectInfoResult
    {
        public IReadOnlyList<string> SelectedMaps { get; }
        public IReadOnlyList<string> MapMarks { get; }
        public IReadOnlyList<string> Bgms { get; }
        public IReadOnlyList<string> Backs { get; }
        public IReadOnlyList<string> Tiles { get; }
        public IReadOnlyList<string> Objs { get; }
        public IReadOnlyList<string> Npcs { get; }
        public IReadOnlyList<string> Mobs { get; }
        public IReadOnlyList<string> Reactors { get; }

        public MapObjectInfoResult(
            IReadOnlyList<string> selectedMaps,
            IReadOnlyList<string> mapMarks,
            IReadOnlyList<string> bgms,
            IReadOnlyList<string> backs,
            IReadOnlyList<string> tiles,
            IReadOnlyList<string> objs,
            IReadOnlyList<string> npcs,
            IReadOnlyList<string> mobs,
            IReadOnlyList<string> reactors)
        {
            SelectedMaps = selectedMaps;
            MapMarks = mapMarks;
            Bgms = bgms;
            Backs = backs;
            Tiles = tiles;
            Objs = objs;
            Npcs = npcs;
            Mobs = mobs;
            Reactors = reactors;
        }

        private const string NoValuesPlaceholder = "沒有值";

        /// <summary>
        /// Plain-text rendering of all nine blocks, in the fixed order the window displays them.
        /// Used by both the window's section text and the "複製" button, so the clipboard always
        /// carries the full summary regardless of what is currently scrolled into view.
        /// </summary>
        public string ToClipboardText()
        {
            StringBuilder sb = new StringBuilder();
            AppendSection(sb, "選取地圖", SelectedMaps, isLast: false);
            AppendSection(sb, "mapMark", MapMarks, isLast: false);
            AppendSection(sb, "bgm", Bgms, isLast: false);
            AppendSection(sb, "Back", Backs, isLast: false);
            AppendSection(sb, "Tile", Tiles, isLast: false);
            AppendSection(sb, "Obj", Objs, isLast: false);
            AppendSection(sb, "Npc", Npcs, isLast: false);
            AppendSection(sb, "Mob", Mobs, isLast: false);
            AppendSection(sb, "Reactor", Reactors, isLast: true);
            return sb.ToString();
        }

        /// <summary>
        /// Joined-by-newline rendering of one block's values, for display in a single TextBlock.
        /// </summary>
        public static string JoinForDisplay(IReadOnlyList<string> values)
        {
            return values.Count == 0 ? NoValuesPlaceholder : string.Join("\n", values);
        }

        private static void AppendSection(StringBuilder sb, string header, IReadOnlyList<string> values, bool isLast)
        {
            sb.AppendLine(header);
            if (values.Count == 0)
            {
                sb.AppendLine(NoValuesPlaceholder);
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
