using System;
using System.Collections.Generic;
using System.Drawing;
using MapleLib;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace HaRepacker.GUI.WorldMap
{
    /// <summary>
    /// One MapList entry that has a usable spot. Holds the real WZ properties rather than paths,
    /// so an edit writes straight back to the object the tree is showing - no re-walking the IMG
    /// and no chance of touching a different node than the one on screen.
    /// </summary>
    public sealed class WorldMapSpot
    {
        /// <summary>The MapList child, e.g. MapList\4.</summary>
        public WzSubProperty Entry { get; init; }

        /// <summary>Its name as it appears in the tree ("4").</summary>
        public string EntryName { get; init; }

        /// <summary>Required - an entry without one is not a spot and is skipped.</summary>
        public WzVectorProperty Spot { get; init; }

        /// <summary>Null when the entry has no type; the inspector shows "-" and writes nothing.</summary>
        public WzIntProperty Type { get; init; }

        /// <summary>mapNo children in declaration order. Empty when absent.</summary>
        public IReadOnlyList<WzIntProperty> MapNo { get; init; }

        public int SpotX => Spot.X.Value;
        public int SpotY => Spot.Y.Value;
    }

    /// <summary>
    /// Everything the WorldMap editor needs out of one WorldMap*.img, parsed once. Pure WZ
    /// reading - no WPF, no mouse handling - so the parsing rules can be tested on synthetic
    /// images.
    /// </summary>
    public sealed class WorldMapDocument
    {
        public WzImage Image { get; private init; }
        public string ImageName { get; private init; }

        /// <summary>The BaseImg artwork, or null when it could not be resolved.</summary>
        public WzCanvasProperty BaseCanvas { get; private init; }

        /// <summary>
        /// BaseImg's origin. Spot coordinates are relative to it, so this is what turns a spot
        /// into a pixel position - see WorldMapCoordinateConverter. (0,0) when absent.
        /// </summary>
        public PointF BaseOrigin { get; private init; }

        public IReadOnlyList<WorldMapSpot> Spots { get; private init; }

        /// <summary>Raw info\parentMap value, or null. Normalize with WorldMapNavigation.</summary>
        public string ParentMap { get; private init; }

        /// <summary>Short note for the editor's status line; null when everything parsed.</summary>
        public string Warning { get; private init; }

        public static WorldMapDocument Load(WzImage image)
        {
            if (image == null)
                throw new ArgumentNullException(nameof(image));

            WzCanvasProperty baseCanvas = ResolveBaseCanvas(image["BaseImg"]);
            var spots = ReadSpots(image["MapList"]);

            return new WorldMapDocument
            {
                Image = image,
                ImageName = image.Name,
                BaseCanvas = baseCanvas,
                BaseOrigin = baseCanvas == null ? new PointF(0f, 0f) : baseCanvas.GetCanvasOriginPosition(),
                Spots = spots,
                ParentMap = (image["info"] as IPropertyContainer)?["parentMap"].ReadString(null),
                Warning = baseCanvas == null ? "無法取得 BaseImg" : null
            };
        }

        /// <summary>
        /// Finds the artwork under BaseImg. Real WorldMaps usually nest it one level down
        /// (BaseImg\0\Canvas) rather than putting the canvas on BaseImg itself, and either level
        /// can be a UOL pointing elsewhere.
        ///
        /// Deliberately limited to BaseImg and its direct children: grabbing "the first canvas in
        /// the image" would happily pick up a MapList icon and render the wrong thing.
        /// </summary>
        public static WzCanvasProperty ResolveBaseCanvas(WzImageProperty baseImg)
        {
            if (baseImg == null)
                return null;

            if (baseImg is WzCanvasProperty direct)
                return direct;

            if (baseImg is WzUOLProperty uol)
                return ResolveUolToCanvas(uol);

            if (baseImg is IPropertyContainer container)
            {
                foreach (WzImageProperty child in container.WzProperties)
                {
                    if (child is WzCanvasProperty childCanvas)
                        return childCanvas;

                    if (child is WzUOLProperty childUol)
                    {
                        WzCanvasProperty linked = ResolveUolToCanvas(childUol);
                        if (linked != null)
                            return linked;
                    }
                }
            }

            return null;
        }

        private static WzCanvasProperty ResolveUolToCanvas(WzUOLProperty uol)
        {
            try
            {
                // A UOL pointing at something that no longer exists throws rather than
                // returning null, and a broken link must not stop the editor from opening.
                return uol.LinkValue as WzCanvasProperty;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Reads MapList. An entry without a spot vector has nothing to draw, so it is skipped
        /// rather than reported - missing type and missing mapNo are both fine and handled by the
        /// inspector.
        /// </summary>
        private static List<WorldMapSpot> ReadSpots(WzImageProperty mapList)
        {
            var spots = new List<WorldMapSpot>();
            if (mapList is not IPropertyContainer container)
                return spots;

            foreach (WzImageProperty child in container.WzProperties)
            {
                if (child is not WzSubProperty entry)
                    continue;
                if (entry["spot"] is not WzVectorProperty spot)
                    continue;

                var mapNo = new List<WzIntProperty>();
                if (entry["mapNo"] is IPropertyContainer mapNoContainer)
                {
                    foreach (WzImageProperty no in mapNoContainer.WzProperties)
                    {
                        // Anything that is not an int is left alone rather than coerced.
                        if (no is WzIntProperty intNo)
                            mapNo.Add(intNo);
                    }
                }

                spots.Add(new WorldMapSpot
                {
                    Entry = entry,
                    EntryName = entry.Name,
                    Spot = spot,
                    Type = entry["type"] as WzIntProperty,
                    MapNo = mapNo
                });
            }

            return spots;
        }

        /// <summary>Every mapNo value in this image, for the forward-navigation match.</summary>
        public HashSet<int> CollectMapNumbers()
        {
            var numbers = new HashSet<int>();
            foreach (WorldMapSpot spot in Spots)
            {
                foreach (WzIntProperty no in spot.MapNo)
                    numbers.Add(no.Value);
            }
            return numbers;
        }
    }

    /// <summary>
    /// Spot coordinates are stored relative to BaseImg's origin, not to the bitmap's top-left.
    /// With origin (320,235) a spot of (138,-101) draws at (458,134). Both directions live here
    /// so no mouse handler ever open-codes a +origin / -origin and gets the sign wrong.
    /// </summary>
    public static class WorldMapCoordinateConverter
    {
        public static (double X, double Y) WorldToCanvas(PointF baseOrigin, int spotX, int spotY)
            => (baseOrigin.X + spotX, baseOrigin.Y + spotY);

        /// <summary>
        /// Inverse, rounded to int because a spot is stored as WzIntProperty X/Y - a drag must
        /// never try to persist a fractional pixel.
        /// </summary>
        public static (int X, int Y) CanvasToWorld(PointF baseOrigin, double canvasX, double canvasY)
            => ((int)Math.Round(canvasX - baseOrigin.X), (int)Math.Round(canvasY - baseOrigin.Y));
    }

    /// <summary>
    /// Which WorldMap image to move to, both upwards (info\parentMap) and forwards (double-clicking
    /// a spot). Kept free of WZ lookups so the matching rules can be tested directly.
    /// </summary>
    public static class WorldMapNavigation
    {
        /// <summary>
        /// info\parentMap holds a bare name ("WorldMap"), but sometimes already carries the
        /// extension. Returns null when there is no usable parent.
        /// </summary>
        public static string NormalizeImageName(string parentMap)
        {
            if (string.IsNullOrWhiteSpace(parentMap))
                return null;

            string trimmed = parentMap.Trim();
            return trimmed.EndsWith(".img", StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + ".img";
        }

        /// <summary>
        /// Picks the sibling WorldMap that shares the most map ids with the clicked spot.
        /// Data-driven on purpose - guessing from id prefixes breaks on any custom server.
        /// </summary>
        /// <param name="clickedMapNumbers">The double-clicked spot's mapNo values.</param>
        /// <param name="candidateMapNumbers">Sibling image name to every mapNo it contains.</param>
        /// <param name="currentImageName">Excluded from the search - a spot never links to its own map.</param>
        /// <param name="ambiguous">
        /// True when two or more candidates tie for the best score. The caller must not pick one:
        /// silently guessing sends the user to the wrong map with no way to tell.
        /// </param>
        /// <returns>The winning image name, or null when nothing overlaps or the best score ties.</returns>
        public static string ResolveForwardTarget(IReadOnlyCollection<int> clickedMapNumbers,
            IReadOnlyDictionary<string, HashSet<int>> candidateMapNumbers,
            string currentImageName,
            out bool ambiguous)
        {
            ambiguous = false;
            if (clickedMapNumbers == null || clickedMapNumbers.Count == 0 || candidateMapNumbers == null)
                return null;

            string best = null;
            int bestScore = 0;
            int bestCount = 0;

            foreach (KeyValuePair<string, HashSet<int>> candidate in candidateMapNumbers)
            {
                if (string.Equals(candidate.Key, currentImageName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (candidate.Value == null)
                    continue;

                int score = 0;
                foreach (int mapNumber in clickedMapNumbers)
                {
                    if (candidate.Value.Contains(mapNumber))
                        score++;
                }
                if (score == 0)
                    continue;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate.Key;
                    bestCount = 1;
                }
                else if (score == bestScore)
                {
                    bestCount++;
                }
            }

            if (best == null)
                return null;

            if (bestCount > 1)
            {
                ambiguous = true;
                return null;
            }

            return best;
        }
    }

    /// <summary>
    /// Whether a selected node is a WorldMap image the editor should open for.
    /// </summary>
    public static class WorldMapDetector
    {
        public const string WorldMapDirectoryName = "WorldMap";

        /// <summary>
        /// A WorldMap*.img that actually sits under a WorldMap directory. The directory check
        /// matters: a stray WorldMap123.img elsewhere in the WZ is not a world map and must not
        /// take over the panel.
        /// </summary>
        public static bool IsWorldMapImage(WzObject obj)
        {
            if (obj is not WzImage image || image.Name == null)
                return false;
            if (!image.Name.StartsWith(WorldMapDirectoryName, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!image.Name.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                return false;

            return FindWorldMapDirectory(image) != null;
        }

        /// <summary>The WorldMap directory this image lives under, or null.</summary>
        public static WzDirectory FindWorldMapDirectory(WzObject obj)
        {
            for (WzObject parent = obj?.Parent; parent != null; parent = parent.Parent)
            {
                if (parent is WzDirectory directory
                    && string.Equals(directory.Name, WorldMapDirectoryName, StringComparison.OrdinalIgnoreCase))
                    return directory;
            }
            return null;
        }
    }
}
