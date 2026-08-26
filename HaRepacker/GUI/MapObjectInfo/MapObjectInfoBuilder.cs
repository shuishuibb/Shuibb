using System;
using System.Collections.Generic;
using System.Globalization;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace HaRepacker.GUI.MapObjectInfo
{
    /// <summary>
    /// Read-only WZ traversal for the "地圖物件資訊" feature: given one or more selected tree
    /// nodes, finds the map WzImages among them and collects their mapMark/bgm/back/tile/obj/
    /// npc/mob/reactor content into a <see cref="MapObjectInfoResult"/>.
    ///
    /// Never writes to a WzObject, never sets a property, never touches Changed/undo state -
    /// every read here goes through GetFromPath/WzProperties/indexer lookups, the same read
    /// paths the rest of HaRepacker already uses.
    /// </summary>
    public static class MapObjectInfoBuilder
    {
        /// <summary>
        /// A "map IMG" is a WzImage whose name is "&lt;digits&gt;.img" - e.g. 910000000.img -
        /// matching the same convention WzInfoTools.FindMapImage relies on elsewhere.
        /// </summary>
        public static bool TryGetMapImage(WzNode node, out WzImage image, out string mapId)
        {
            image = null;
            mapId = null;

            if (node?.Tag is not WzImage img)
                return false;

            string name = img.Name;
            if (string.IsNullOrEmpty(name) || !name.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                return false;

            string baseName = name.Substring(0, name.Length - 4);
            if (!IsAllDigits(baseName))
                return false;

            image = img;
            mapId = baseName;
            return true;
        }

        public static bool IsMapImageNode(WzNode node) => TryGetMapImage(node, out _, out _);

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
        /// Builds the union summary across every valid map image found in <paramref name="nodes"/>.
        /// Anything that isn't a valid map IMG (a directory, a property, a stale/mismatched Tag,
        /// null) is silently skipped - callers are expected to have already checked
        /// IsMapImageNode before offering this feature, but this stays defensive on its own.
        /// </summary>
        public static MapObjectInfoResult Build(IEnumerable<WzNode> nodes)
        {
            HashSet<string> selectedMaps = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> mapMarks = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> bgms = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> backs = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> tiles = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> objs = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> npcs = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> mobs = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> reactors = new HashSet<string>(StringComparer.Ordinal);

            if (nodes != null)
            {
                foreach (WzNode node in nodes)
                {
                    if (!TryGetMapImage(node, out WzImage map, out string mapId))
                        continue;

                    selectedMaps.Add(mapId);

                    // A single map's data being unexpectedly shaped must not stop the summary for
                    // every other selected map, or block the window from opening at all. Primary
                    // defense is the null/type checks throughout CollectFromMap; this is the one
                    // documented last-resort boundary per map, not a blanket error suppressor.
                    try
                    {
                        CollectFromMap(map, mapMarks, bgms, backs, tiles, objs, npcs, mobs, reactors);
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            return new MapObjectInfoResult(
                Sort(selectedMaps, NumericThenOrdinalComparer.Instance),
                Sort(mapMarks, StringComparer.Ordinal),
                Sort(bgms, StringComparer.Ordinal),
                Sort(backs, StringComparer.Ordinal),
                Sort(tiles, StringComparer.Ordinal),
                Sort(objs, StringComparer.Ordinal),
                Sort(npcs, NumericThenOrdinalComparer.Instance),
                Sort(mobs, NumericThenOrdinalComparer.Instance),
                Sort(reactors, NumericThenOrdinalComparer.Instance));
        }

        private static void CollectFromMap(
            WzImage map,
            ISet<string> mapMarks, ISet<string> bgms,
            ISet<string> backs, ISet<string> tiles, ISet<string> objs,
            ISet<string> npcs, ISet<string> mobs, ISet<string> reactors)
        {
            AddStringValue(map.GetFromPath("info/mapMark"), mapMarks);
            AddStringValue(map.GetFromPath("info/bgm"), bgms);

            // back/* -> bS
            CollectChildStringValues(map.GetFromPath("back"), "bS", backs);

            // life/* -> type == "n" -> Npc id ; type == "m" -> Mob id
            WzImageProperty life = map.GetFromPath("life");
            if (life?.WzProperties != null)
            {
                foreach (WzImageProperty entry in life.WzProperties)
                {
                    WzPropertyCollection entryProps = entry?.WzProperties;
                    if (entryProps == null)
                        continue;

                    string type = GetStringValue(entryProps["type"]);
                    if (string.IsNullOrEmpty(type))
                        continue;

                    string id = GetStringValue(entryProps["id"]);
                    if (string.IsNullOrEmpty(id))
                        continue;

                    if (string.Equals(type, "n", StringComparison.Ordinal))
                        npcs.Add(id);
                    else if (string.Equals(type, "m", StringComparison.Ordinal))
                        mobs.Add(id);
                }
            }

            // reactor/* -> id
            CollectChildStringValues(map.GetFromPath("reactor"), "id", reactors);

            // tile/obj live one level under each numbered layer (0.., not a single top-level
            // "tile"/"obj" node) - confirmed against this project's own Map.wz data. Scanning
            // every top-level child (rather than assuming layer names are purely numeric) keeps
            // this tolerant of whatever the layer container happens to be named.
            WzPropertyCollection topLevel = map.WzProperties;
            if (topLevel != null)
            {
                foreach (WzImageProperty layer in topLevel)
                {
                    WzPropertyCollection layerProps = layer?.WzProperties;
                    if (layerProps == null)
                        continue;

                    CollectChildStringValues(layerProps["tile"], "u", tiles);
                    CollectChildStringValues(layerProps["obj"], "oS", objs);
                }
            }
        }

        /// <summary>
        /// For a container like "back" or "reactor" (or a layer's "tile"/"obj"), reads
        /// <paramref name="childPropertyName"/> off each direct child entry.
        /// </summary>
        private static void CollectChildStringValues(WzImageProperty container, string childPropertyName, ISet<string> target)
        {
            WzPropertyCollection entries = container?.WzProperties;
            if (entries == null)
                return;

            foreach (WzImageProperty entry in entries)
            {
                WzPropertyCollection entryProps = entry?.WzProperties;
                if (entryProps == null)
                    continue;

                AddStringValue(entryProps[childPropertyName], target);
            }
        }

        private static void AddStringValue(WzImageProperty prop, ISet<string> target)
        {
            string value = GetStringValue(prop);
            if (!string.IsNullOrEmpty(value))
                target.Add(value);
        }

        /// <summary>
        /// Every value this feature reads (bS/u/oS/type/id/mapMark/bgm) is a WzStringProperty in
        /// this project's own WZ data, but a WzIntProperty fallback is kept in case a differently
        /// packed WZ stores id/type as a number instead - anything else is an unexpected type and
        /// is safely skipped rather than guessed at.
        /// </summary>
        private static string GetStringValue(WzImageProperty prop)
        {
            if (prop is WzStringProperty stringProp)
                return stringProp.Value;
            if (prop is WzIntProperty intProp)
                return intProp.Value.ToString(CultureInfo.InvariantCulture);
            return null;
        }

        private static IReadOnlyList<string> Sort(IEnumerable<string> values, IComparer<string> comparer)
        {
            List<string> list = new List<string>(values);
            list.Sort(comparer);
            return list;
        }

        /// <summary>
        /// Map IDs and npc/mob/reactor IDs are numeric strings; comparing them numerically avoids
        /// the "100" &lt; "20" trap plain ordinal string sort has whenever two values differ in
        /// digit width. Falls back to ordinal for anything that isn't parseable as a number, so
        /// the sort stays total and deterministic no matter what shows up.
        /// </summary>
        private sealed class NumericThenOrdinalComparer : IComparer<string>
        {
            public static readonly NumericThenOrdinalComparer Instance = new NumericThenOrdinalComparer();

            public int Compare(string x, string y)
            {
                if (long.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out long lx) &&
                    long.TryParse(y, NumberStyles.None, CultureInfo.InvariantCulture, out long ly))
                {
                    int numericCompare = lx.CompareTo(ly);
                    if (numericCompare != 0)
                        return numericCompare;
                }
                return string.CompareOrdinal(x, y);
            }
        }
    }
}
