using System.Collections.Generic;

namespace MapleLib
{
    /// <summary>
    /// Maps common WZ Property keys (e.g. "reqSTR") to a friendlier Traditional Chinese display
    /// name (e.g. "力量限制") - for GUI display only.
    ///
    /// Lives here (rather than in HaRepacker or SkillPreview individually) because both of this
    /// helper's callers - HaRepacker\GUI\Panels\MainPanel.xaml.cs's CreateNativeTreeItem (the
    /// tree) and SkillPreview\NodeEditorPanel.cs's BuildGroupCard (the "info"-style batch
    /// property editor cards, the actual "Property 名稱左側 label" this feature targets) - already
    /// reference MapleLib, and HaRepacker/SkillPreview don't reference each other, so this is the
    /// one place both can reach without adding a new project reference.
    ///
    /// Both callers only ever pass this into a label/Header - the underlying WzImageProperty.Name
    /// / WzNode.Text / dictionary keys used for saving values back are never touched here or by
    /// either caller: sorting (HaRepacker\Comparer\TreeViewNodeSorter.cs), type-ahead, the search
    /// panel, and NodeEditorPanel's SaveGroup (which looks values up by the real property name)
    /// all keep working on the original English key exactly as before.
    ///
    /// Any key not in the table is returned unchanged - no guessing, no hiding, editing stays
    /// fully enabled either way.
    /// </summary>
    public static class PropertyDisplayName
    {
        private static readonly Dictionary<string, string> Map = new Dictionary<string, string>
        {
            // Requirement (info)
            { "reqJob", "職業限制" },
            { "reqLevel", "等級限制" },
            { "reqSTR", "力量限制" },
            { "reqDEX", "敏捷限制" },
            { "reqINT", "智力限制" },
            { "reqLUK", "幸運限制" },

            // Stat increase (info)
            { "incSTR", "力量" },
            { "incDEX", "敏捷" },
            { "incINT", "智力" },
            { "incLUK", "幸運" },
            { "incPAD", "物理攻擊力" },
            { "incMAD", "魔法攻擊力" },
            { "incPDD", "物理防禦力" },
            { "incMDD", "魔法防禦力" },
            { "incMHP", "最大 HP" },
            { "incMMP", "最大 MP" },

            // Equipment info
            { "tuc", "可升級次數" },
            { "price", "價格" },
            { "cash", "點裝" },
            { "tradeBlock", "無法交易" },
            { "only", "唯一裝備" },

            // Slot info
            { "islot", "裝備欄位" },
            { "vslot", "顯示欄位" },
        };

        /// <summary>
        /// Returns the Traditional Chinese display name for a WZ property key, or
        /// <paramref name="propertyName"/> itself unchanged if it isn't in the table (or is
        /// null). Pure lookup - never mutates the string passed in, never touches a WzObject.
        /// </summary>
        public static string GetDisplayName(string propertyName)
        {
            if (propertyName != null && Map.TryGetValue(propertyName, out string displayName))
                return displayName;
            return propertyName;
        }
    }
}
