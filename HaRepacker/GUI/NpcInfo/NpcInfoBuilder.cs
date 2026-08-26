using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using MapleLib;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace HaRepacker.GUI.NpcInfo
{
    /// <summary>
    /// Read-only WZ traversal for the "NPC 詳細資訊" feature: given the selected NPC WzImage,
    /// collects its ID, its String.wz name/extras (if String.wz happens to be loaded), its WZ
    /// path, and its animation/action node names into an <see cref="NpcInfoResult"/>.
    ///
    /// Never writes to a WzObject, never sets a property, never touches Changed/undo state, and
    /// never loads a new WZ file - String.wz lookup only looks at whatever is already in
    /// Program.WzFileManager.WzFileList at the moment this runs.
    /// </summary>
    public static class NpcInfoBuilder
    {
        private static readonly Regex NpcWzFileNamePattern =
            new Regex(@"^Npc(_\d+)?\.wz$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// An "NPC WzImage" is a WzImage named "&lt;digits&gt;.img" that actually lives under an
        /// Npc WZ - not just any digits.img (Map/Mob/Reactor images use the same convention).
        /// </summary>
        public static bool TryGetNpcImage(WzNode node, out WzImage image, out string npcId)
        {
            image = null;
            npcId = null;

            if (node?.Tag is not WzImage img)
                return false;

            string name = img.Name;
            if (string.IsNullOrEmpty(name) || !name.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                return false;

            string baseName = name.Substring(0, name.Length - 4);
            if (!IsAllDigits(baseName))
                return false;

            if (!LooksLikeNpcWzContext(img))
                return false;

            image = img;
            npcId = baseName;
            return true;
        }

        public static bool IsNpcImageNode(WzNode node) => TryGetNpcImage(node, out _, out _);

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
        /// Walks the WzObject graph (not the TreeNode graph, so this still resolves for a
        /// detached node) looking for either the owning WzFile being named Npc.wz/Npc_NNN.wz
        /// (the standalone-file layout this project's own data uses), or an ancestor WzDirectory
        /// literally named "Npc" (the combined-Data.wz layout WzInfoTools.FindMapDirectoryParent
        /// documents for maps) - either signal is enough, but a bare "all digits" name alone
        /// never is.
        /// </summary>
        private static bool LooksLikeNpcWzContext(WzImage image)
        {
            WzFile file = image.WzFileParent;
            if (file != null && NpcWzFileNamePattern.IsMatch(file.Name ?? string.Empty))
                return true;

            WzObject current = image.Parent;
            while (current != null && current is not WzFile)
            {
                if (current is WzDirectory dir && string.Equals(dir.Name, "Npc", StringComparison.OrdinalIgnoreCase))
                    return true;
                current = current.Parent;
            }
            return false;
        }

        /// <summary>
        /// Builds the summary for one NPC WzImage. loadedWzFiles is normally
        /// Program.WzFileManager.WzFileList, passed in explicitly so this stays independent of
        /// Program/UI and testable on its own. If no loaded file exposes a String.wz-shaped
        /// "Npc.img" (or the id just isn't in there), NpcName/StringExtras simply come back
        /// empty - this never loads a WZ file itself.
        /// </summary>
        public static NpcInfoResult Build(WzImage npcImage, string npcId, IEnumerable<WzFile> loadedWzFiles)
        {
            string wzPath = BuildWzPath(npcImage);
            List<string> animations = CollectAnimationNames(npcImage);

            string npcName = null;
            List<string> extras = new List<string>();

            WzSubProperty stringEntry = FindStringEntry(npcId, loadedWzFiles);
            if (stringEntry?.WzProperties != null)
            {
                foreach (WzImageProperty prop in stringEntry.WzProperties)
                {
                    if (prop == null || !IsSafeScalar(prop))
                        continue;

                    if (string.Equals(prop.Name, "name", StringComparison.OrdinalIgnoreCase))
                    {
                        npcName = prop.ReadString(null);
                        continue;
                    }

                    string value = prop.ReadString(null);
                    if (!string.IsNullOrEmpty(value))
                        extras.Add(prop.Name + ": " + value);
                }
                extras.Sort(StringComparer.Ordinal);
            }

            return new NpcInfoResult(npcId, npcName, extras, wzPath, animations);
        }

        /// <summary>
        /// Only String/Int are guaranteed safe through WzImageProperty.ReadString - everything
        /// else (SubProperty/Canvas/Vector/...) is a container, not a scalar, and is skipped
        /// rather than dumped, matching "只顯示第一層可安全轉成文字的 scalar property".
        /// </summary>
        private static bool IsSafeScalar(WzImageProperty prop) =>
            prop.PropertyType == WzPropertyType.String || prop.PropertyType == WzPropertyType.Int;

        /// <summary>
        /// Mirrors WzStringSearchFormDataCache.CacheInventoryData's own
        /// "Files["String.wz"].WzDirectory["Npc.img"]" lookup, sourced from whichever loaded WZ
        /// files are already open instead of that class's private, separately-opened file set.
        /// Entries are keyed by the plain (non-zero-padded) numeric id - confirmed against this
        /// project's own String.wz data (e.g. Npc.wz's "0002000.img" maps to String.wz's
        /// Npc.img/"2000"), matching FHMapper's own use of the unpadded id for this same path.
        /// </summary>
        private static WzSubProperty FindStringEntry(string npcId, IEnumerable<WzFile> loadedWzFiles)
        {
            if (loadedWzFiles == null || string.IsNullOrEmpty(npcId))
                return null;

            string unpaddedId = StripLeadingZeros(npcId);

            foreach (WzFile file in loadedWzFiles)
            {
                if (file?.WzDirectory?["Npc.img"] is not WzImage npcStringImg)
                    continue;

                if (npcStringImg[unpaddedId] is WzSubProperty entry)
                    return entry;
            }
            return null;
        }

        private static string StripLeadingZeros(string digits)
        {
            int i = 0;
            while (i < digits.Length - 1 && digits[i] == '0')
                i++;
            return digits.Substring(i);
        }

        /// <summary>
        /// Walks the WzObject.Parent chain (not the TreeNode chain, so a detached node still
        /// resolves) from the NPC image up to its WzFile, joining names with "/" - e.g.
        /// "Npc.wz/0002000.img". This is the same kind of parent-walk MainForm/ContextMenuManager
        /// already use for tree paths, just over the WZ object graph instead of TreeNode.Parent.
        /// </summary>
        private static string BuildWzPath(WzImage npcImage)
        {
            List<string> segments = new List<string>();
            WzObject current = npcImage;
            while (current != null)
            {
                if (!string.IsNullOrEmpty(current.Name))
                    segments.Insert(0, current.Name);
                current = current.Parent;
            }
            return string.Join("/", segments);
        }

        /// <summary>
        /// First-level scan only. Reuses AnimationBuilder.IsValidAnimationWzObject - the same
        /// check the rest of HaRepacker already uses to decide "is this a real animation" - so
        /// "info"/"link"/"script" are excluded because they structurally aren't a WzSubProperty
        /// of 2+ sequentially-numbered WzCanvasProperty frames, not because of a name blocklist.
        /// </summary>
        private static List<string> CollectAnimationNames(WzImage npcImage)
        {
            SortedSet<string> names = new SortedSet<string>(StringComparer.Ordinal);
            if (npcImage?.WzProperties != null)
            {
                foreach (WzImageProperty prop in npcImage.WzProperties)
                {
                    if (prop != null && HaRepacker.AnimationBuilder.IsValidAnimationWzObject(prop))
                        names.Add(prop.Name);
                }
            }
            return new List<string>(names);
        }
    }
}
