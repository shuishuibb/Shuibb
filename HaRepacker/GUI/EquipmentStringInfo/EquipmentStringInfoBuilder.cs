using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using HaSharedLibrary.Wz;
using MapleLib;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using MapleLib.WzLib.WzStructure.Data.ItemStructure;

namespace HaRepacker.GUI.EquipmentStringInfo
{
    /// <summary>
    /// Read-only WZ traversal for the "裝備 String 資訊" feature: given the selected equipment
    /// WzImage, finds its item id and cross-references every currently loaded String.wz-shaped
    /// WzFile's Eqp.img for a matching entry.
    ///
    /// Scope note (verified against this project's own real WZ data before writing this):
    /// Character.wz's per-slot files (Coat_000.wz, Weapon_000.wz, ...) each hold one WzImage per
    /// item id, and String.wz's Eqp.img/Eqp/&lt;category&gt;/&lt;id&gt; mirrors that 1:1 - this is
    /// the case this class covers. Item.wz's Cash/Consume/Etc/Install/Pet/Special files were
    /// checked too, but their items are packed in *batches* per WzImage (one WzImage there holds
    /// many item ids as children, not one id per image), so there is no reliable "this WzImage is
    /// exactly this item id" mapping to build without guessing at the batching scheme - so those
    /// are intentionally not treated as valid entry points this round, rather than offering a
    /// menu item that would just show misleadingly-empty results for real items.
    ///
    /// Never writes to a WzObject, never loads a new WZ file - only looks at whatever is already
    /// in Program.WzFileManager.WzFileList at the moment this runs.
    /// </summary>
    public static class EquipmentStringInfoBuilder
    {
        // Already-identified other WZ families (see MapObjectInfoBuilder/NpcInfoBuilder) that
        // share the same bare "<digits>.img" naming - excluded so a coincidental id in the
        // equip numeric range under one of these can't be misread as equipment.
        private static readonly Regex NonEquipWzFileNamePattern =
            new Regex(@"^(Npc|Mob|Map\d*|Reactor)(_\d+)?\.wz$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// An "equipment WzImage" is a WzImage named "&lt;digits&gt;.img" whose id - after
        /// stripping leading zeros the same way the rest of this project does
        /// (WzInfoTools.RemoveLeadingZeros) - falls in MapleStory's equipment id range
        /// (ItemIdsCategory.IsEquipment: id / 1,000,000 == 1). That range check is the real
        /// signal, not the file name shape or a hardcoded list of Character.wz sub-folder names -
        /// it is what tells apart an equip id from an npc/mob/map id that happens to also be all
        /// digits. The WzFile-name exclusion below is an extra safety net on top of it.
        /// </summary>
        public static bool TryGetEquipmentImage(WzNode node, out WzImage image, out string itemId)
        {
            image = null;
            itemId = null;

            if (node?.Tag is not WzImage img)
                return false;

            string name = img.Name;
            if (string.IsNullOrEmpty(name) || !name.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                return false;

            string baseName = name.Substring(0, name.Length - 4);
            if (!IsAllDigits(baseName))
                return false;

            string normalizedId = WzInfoTools.RemoveLeadingZeros(baseName);
            if (!int.TryParse(normalizedId, NumberStyles.None, CultureInfo.InvariantCulture, out int idValue))
                return false;
            if (!ItemIdsCategory.IsEquipment(idValue))
                return false;

            WzFile file = img.WzFileParent;
            if (file != null && NonEquipWzFileNamePattern.IsMatch(file.Name ?? string.Empty))
                return false;

            image = img;
            itemId = normalizedId;
            return true;
        }

        public static bool IsEquipmentImageNode(WzNode node) => TryGetEquipmentImage(node, out _, out _);

        private static bool IsAllDigits(string value)
        {
            if (value.Length == 0)
                return false;

            foreach (char c in value)
            {
                if (c < '0' || c > '9')
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Builds the summary for one equipment WzImage. loadedWzFiles is normally
        /// Program.WzFileManager.WzFileList, passed in explicitly so this stays independent of
        /// Program/UI and testable on its own - and so every currently loaded String.wz-shaped
        /// file is checked (not just the first one), correctly covering multiple loaded String
        /// WZs (e.g. two languages) without dropping any of them.
        /// </summary>
        public static EquipmentStringInfoResult Build(WzImage equipImage, string itemId, IEnumerable<WzFile> loadedWzFiles)
        {
            string wzPath = BuildWzPath(equipImage);
            List<RawSource> rawSources = new List<RawSource>();

            if (loadedWzFiles != null)
            {
                foreach (WzFile file in loadedWzFiles)
                {
                    RawSource match = FindInStringFile(file, itemId);
                    if (match != null)
                        rawSources.Add(match);
                }
            }

            List<EquipmentStringSourceResult> sources = DeduplicateSources(rawSources);
            return new EquipmentStringInfoResult(itemId, wzPath, sources);
        }

        /// <summary>
        /// Mirrors WzStringSearchFormDataCache.CacheInventoryData's own
        /// "Files["String.wz"].WzDirectory["Eqp.img"]" lookup, sourced from whichever loaded WZ
        /// files are already open. Rather than deriving which of Eqp/Eqp's ~19 category
        /// sub-nodes (Weapon, Coat, Cap, ...) this id belongs to from the equip WzFile's own name
        /// (which this project's data shows doesn't always match exactly - Character.wz's
        /// "TamingMob" folder is String.wz's "Taming" category), this checks every category
        /// actually present under Eqp/Eqp for a child keyed by the id. Each category lookup is a
        /// single dictionary hit (WzPropertyCollection indexes by name), so this stays cheap even
        /// though a category can hold thousands of items.
        /// </summary>
        private static RawSource FindInStringFile(WzFile file, string itemId)
        {
            if (file?.WzDirectory?["Eqp.img"] is not WzImage eqpImg)
                return null;

            if (eqpImg["Eqp"]?.WzProperties is not WzPropertyCollection categories)
                return null;

            foreach (WzImageProperty category in categories)
            {
                if (category?.WzProperties is not WzPropertyCollection categoryEntries)
                    continue;

                if (categoryEntries[itemId] is not WzSubProperty entry)
                    continue;

                string logicalPath = "Eqp.img/Eqp/" + category.Name + "/" + itemId;
                return ReadEntry(file.Name, entry, logicalPath);
            }
            return null;
        }

        private static RawSource ReadEntry(string sourceFileName, WzSubProperty entry, string logicalPath)
        {
            string name = null;
            List<string> extras = new List<string>();

            foreach (WzImageProperty prop in entry.WzProperties)
            {
                if (prop == null || !IsSafeScalar(prop))
                    continue;

                if (string.Equals(prop.Name, "name", StringComparison.OrdinalIgnoreCase))
                {
                    name = prop.ReadString(null);
                    continue;
                }

                string value = prop.ReadString(null);
                if (!string.IsNullOrEmpty(value))
                    extras.Add(prop.Name + ": " + value);
            }
            extras.Sort(StringComparer.Ordinal);

            return new RawSource(sourceFileName, name, logicalPath, extras);
        }

        /// <summary>
        /// Only String/Int are guaranteed safe through WzImageProperty.ReadString - everything
        /// else (SubProperty/Canvas/Vector/...) is a container, not a scalar, and is skipped
        /// rather than dumped, matching "只顯示第一層可安全轉成文字的 scalar property".
        /// </summary>
        private static bool IsSafeScalar(WzImageProperty prop) =>
            prop.PropertyType == WzPropertyType.String || prop.PropertyType == WzPropertyType.Int;

        /// <summary>
        /// Walks the WzObject.Parent chain (not the TreeNode chain, so a detached node still
        /// resolves) from the equip image up to its WzFile, joining names with "/" - e.g.
        /// "Weapon_000.wz/01302000.img". Same technique as NpcInfoBuilder.BuildWzPath.
        /// </summary>
        private static string BuildWzPath(WzImage equipImage)
        {
            List<string> segments = new List<string>();
            WzObject current = equipImage;
            while (current != null)
            {
                if (!string.IsNullOrEmpty(current.Name))
                    segments.Insert(0, current.Name);
                current = current.Parent;
            }
            return string.Join("/", segments);
        }

        /// <summary>
        /// Groups raw per-file matches that produced the exact same Name+Extras into one
        /// displayed source (per "如果完全相同的結果可去重"), while keeping every contributing
        /// file name so a collapsed entry still says which files agreed. Sources that actually
        /// differ (e.g. two languages with different translations) are kept separate, satisfying
        /// "不得任意只取第一筆". If nothing matched anywhere, returns the single "not found"
        /// placeholder (empty file-name list) instead of an empty list, so callers/UI never have
        /// to special-case "zero sources".
        /// </summary>
        private static List<EquipmentStringSourceResult> DeduplicateSources(List<RawSource> rawSources)
        {
            if (rawSources.Count == 0)
            {
                return new List<EquipmentStringSourceResult>
                {
                    new EquipmentStringSourceResult(Array.Empty<string>(), null, null, Array.Empty<string>())
                };
            }

            List<(string Name, IReadOnlyList<string> Extras, string LogicalPath, List<string> FileNames)> groups = new();
            foreach (RawSource raw in rawSources)
            {
                int matchIndex = groups.FindIndex(g =>
                    string.Equals(g.Name, raw.Name, StringComparison.Ordinal) &&
                    g.Extras.SequenceEqual(raw.Extras, StringComparer.Ordinal));

                if (matchIndex >= 0)
                {
                    groups[matchIndex].FileNames.Add(raw.SourceFileName);
                }
                else
                {
                    groups.Add((raw.Name, raw.Extras, raw.LogicalPath, new List<string> { raw.SourceFileName }));
                }
            }

            List<EquipmentStringSourceResult> results = new List<EquipmentStringSourceResult>(groups.Count);
            foreach (var g in groups)
                results.Add(new EquipmentStringSourceResult(g.FileNames, g.Name, g.LogicalPath, g.Extras));
            return results;
        }

        private sealed class RawSource
        {
            public string SourceFileName { get; }
            public string Name { get; }
            public string LogicalPath { get; }
            public List<string> Extras { get; }

            public RawSource(string sourceFileName, string name, string logicalPath, List<string> extras)
            {
                SourceFileName = sourceFileName;
                Name = name;
                LogicalPath = logicalPath;
                Extras = extras;
            }
        }
    }
}
