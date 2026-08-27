using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using MapleLib;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace HaRepacker.GUI.WorldMap
{
    /// <summary>
    /// Something on the map the user can select and drag. Both MapList spots and MapLink entries
    /// store their position in a "spot" WzVectorProperty, so group dragging, selection and the
    /// dirty/red bookkeeping are written once against this instead of twice.
    /// </summary>
    public interface IWorldMapMovable
    {
        /// <summary>
        /// The vector written when the item is moved, or null when the entry has no spot yet.
        /// Both MapList entries and MapLink entries may legitimately lack one - this repository's
        /// own codec tracks OriginalHasSpot for both - so a null here is ordinary data, not an
        /// error. It is created on the first confirmed move; see <see cref="Owner"/>.
        /// </summary>
        WzVectorProperty Position { get; }

        /// <summary>False while the entry has no spot vector at all.</summary>
        bool HasPosition { get; }

        /// <summary>The entry the spot belongs under, used to create one when it is missing.</summary>
        WzSubProperty Owner { get; }

        /// <summary>Shown in the inspector title, e.g. "MapList 4" / "MapLink 5".</summary>
        string DisplayName { get; }
    }

    /// <summary>
    /// One MapList entry that has a usable spot. Holds the real WZ properties rather than paths,
    /// so an edit writes straight back to the object the tree is showing - no re-walking the IMG
    /// and no chance of touching a different node than the one on screen.
    /// </summary>
    public sealed class WorldMapSpot : IWorldMapMovable
    {
        /// <summary>The MapList child, e.g. MapList\4.</summary>
        public WzSubProperty Entry { get; init; }

        /// <summary>Its name as it appears in the tree ("4").</summary>
        public string EntryName { get; init; }

        /// <summary>Null when the entry has no spot yet - see IWorldMapMovable.Position.</summary>
        public WzVectorProperty Spot { get; init; }

        /// <summary>Null when the entry has no type; the inspector shows "-" and writes nothing.</summary>
        public WzIntProperty Type { get; init; }

        /// <summary>mapNo children in declaration order. Empty when absent.</summary>
        public IReadOnlyList<WzIntProperty> MapNo { get; init; }

        public int SpotX => Spot?.X.Value ?? 0;
        public int SpotY => Spot?.Y.Value ?? 0;

        public WzVectorProperty Position => Spot;
        public bool HasPosition => Spot != null;
        public WzSubProperty Owner => Entry;
        public string DisplayName => "MapList " + EntryName;
    }

    /// <summary>
    /// One MapLink entry. The schema is the one this repository's own WorldMap codec reads and
    /// writes (HaCreator\WorldMap\WorldMapCodec.cs - ReadLink / PatchLink):
    ///
    ///   MapLink\&lt;key&gt;
    ///   ├─ toolTip   (string, optional)
    ///   ├─ spot      (vector - the position)
    ///   └─ link      (optional)
    ///      ├─ linkMap (string - the world map this jumps to)
    ///      └─ linkImg (canvas)
    ///
    /// Only properties that are actually present are read; nothing is invented, and an entry
    /// without a spot vector is skipped rather than given a guessed position.
    /// </summary>
    public sealed class WorldMapLink : IWorldMapMovable
    {
        public WzSubProperty Entry { get; init; }
        public string EntryName { get; init; }

        /// <summary>Null when the entry has no spot yet - see IWorldMapMovable.Position.</summary>
        public WzVectorProperty Spot { get; init; }

        /// <summary>toolTip text, or null when the entry has none.</summary>
        public string ToolTip { get; init; }

        /// <summary>link\linkMap - the world map this link points at, or null.</summary>
        public string LinkMap { get; init; }

        /// <summary>
        /// The raw link\linkImg property as it appears in the WZ - a canvas, a UOL, or null.
        /// </summary>
        public WzImageProperty LinkImageSource { get; init; }

        /// <summary>
        /// The canvas to draw, after following a UOL. Null when there is no artwork or the link
        /// could not be resolved.
        /// </summary>
        public WzCanvasProperty LinkImageDisplay { get; init; }

        /// <summary>
        /// The canvas whose origin this link may edit - set only when linkImg *is* a canvas of its
        /// own. A UOL resolves to a canvas that some other entry probably shares, and writing an
        /// origin there would move every user of it, so those stay display-only until the schema
        /// proves otherwise.
        /// </summary>
        public WzCanvasProperty LinkImageEditable { get; init; }

        /// <summary>Artwork that can be shown but must not be repositioned - a shared UOL target.</summary>
        public bool IsLinkImageReadOnly => LinkImageDisplay != null && LinkImageEditable == null;

        /// <summary>
        /// linkImg's origin vector, or null when it has none. Distinct from
        /// GetCanvasOriginPosition(), which reports (0,0) for both "origin is (0,0)" and "there is
        /// no origin" - the editor has to tell those apart, because it must never create one just
        /// because someone opened the map.
        /// </summary>
        public WzVectorProperty LinkImageOrigin => LinkImageEditable?["origin"] as WzVectorProperty;

        public int SpotX => Spot?.X.Value ?? 0;
        public int SpotY => Spot?.Y.Value ?? 0;

        public WzVectorProperty Position => Spot;
        public bool HasPosition => Spot != null;
        public WzSubProperty Owner => Entry;
        public string DisplayName => "MapLink " + EntryName;
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

        /// <summary>MapLink entries that have a usable spot. Empty when the image has no MapLink.</summary>
        public IReadOnlyList<WorldMapLink> Links { get; private init; }

        /// <summary>
        /// MapLink entries skipped because they carry no spot vector. They have no anchor to draw
        /// or move from, and one is not invented for them - but they are counted so the editor can
        /// say so rather than silently showing fewer links than the tree does.
        /// </summary>
        public int LinksWithoutSpot { get; private init; }

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
            var links = ReadLinks(image["MapLink"], out int linksWithoutSpot);

            return new WorldMapDocument
            {
                Image = image,
                ImageName = image.Name,
                BaseCanvas = baseCanvas,
                BaseOrigin = baseCanvas == null ? new PointF(0f, 0f) : baseCanvas.GetCanvasOriginPosition(),
                Spots = spots,
                Links = links,
                LinksWithoutSpot = linksWithoutSpot,
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

                // A missing spot is ordinary data, not a reason to hide the entry: it is shown at
                // a placeholder position and gets a real spot the first time the user confirms a
                // move. Skipping these is what made most of WorldMap082's entries undraggable.
                var spot = entry["spot"] as WzVectorProperty;

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

        /// <summary>
        /// Reads MapLink using only properties that are actually present. An entry with no spot
        /// vector has no position that could be drawn or dragged, so it is skipped - guessing one
        /// would risk writing a coordinate into the wrong property.
        /// </summary>
        private static List<WorldMapLink> ReadLinks(WzImageProperty mapLink, out int withoutSpot)
        {
            var links = new List<WorldMapLink>();
            withoutSpot = 0;
            if (mapLink is not IPropertyContainer container)
                return links;

            foreach (WzImageProperty child in container.WzProperties)
            {
                if (child is not WzSubProperty entry)
                    continue;

                // As with MapList, a MapLink without a spot is still shown and still movable -
                // the spot is created on the first confirmed move.
                var spot = entry["spot"] as WzVectorProperty;
                if (spot == null)
                    withoutSpot++;

                var nested = entry["link"] as IPropertyContainer;
                WzImageProperty linkImageSource = nested?["linkImg"];
                ResolveLinkImageCanvas(linkImageSource, out WzCanvasProperty display, out WzCanvasProperty editable);

                links.Add(new WorldMapLink
                {
                    Entry = entry,
                    EntryName = entry.Name,
                    Spot = spot,
                    ToolTip = entry["toolTip"].ReadString(null),
                    LinkMap = nested?["linkMap"].ReadString(null),
                    LinkImageSource = linkImageSource,
                    LinkImageDisplay = display,
                    LinkImageEditable = editable
                });
            }

            return links;
        }

        /// <summary>
        /// Works out what to draw for a linkImg property, and whether this link owns it.
        ///
        /// A plain canvas is both drawable and editable. A UOL is followed so the artwork can
        /// still be shown, but the canvas it lands on is very likely shared with other entries -
        /// writing an origin into it would silently move all of them - so it comes back
        /// display-only. Nothing else is accepted, and the WZ is never searched for a substitute.
        /// </summary>
        public static void ResolveLinkImageCanvas(WzImageProperty linkImage,
            out WzCanvasProperty display, out WzCanvasProperty editable)
        {
            display = null;
            editable = null;
            if (linkImage == null)
                return;

            if (linkImage is WzCanvasProperty canvas)
            {
                display = canvas;
                editable = canvas;
                return;
            }

            if (linkImage is WzUOLProperty uol)
                display = ResolveUolToCanvas(uol);
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
    /// Where a MapLink's linkImg artwork sits, and what its origin has to become when the user
    /// drags it somewhere else.
    ///
    /// linkImg is positioned entirely by its own origin - **not** by the link's spot. The two are
    /// separate things: spot is the link's point on the map, linkImg/origin places the picture.
    /// So the anchor here is the world's zero point, which in canvas space is BaseImg's origin;
    /// moving a link's spot does not move its picture.
    ///
    /// A WZ canvas origin is the anchor point *inside* the bitmap, so the picture's top-left is
    /// the anchor minus the origin - the same convention this codebase already draws with
    /// (HaRepacker\FHMapper\FHMapper.cs: DrawImage(bmp, x - origin.X, y - origin.Y), and
    /// AnimationBuilder's Size - origin).
    ///
    /// The consequence to keep straight: dragging the picture right by 20 lowers origin.X by 20.
    /// Both directions live here so no mouse handler ever open-codes that sign and inverts the
    /// drag.
    /// </summary>
    public static class WorldMapLinkImagePlacement
    {
        /// <summary>Top-left corner the artwork should be drawn at.</summary>
        public static (double Left, double Top) ToCanvasPosition((double X, double Y) anchor, int originX, int originY)
            => (anchor.X - originX, anchor.Y - originY);

        /// <summary>
        /// The origin that puts the artwork's top-left at the given canvas position - the inverse
        /// of <see cref="ToCanvasPosition"/>, rounded because origin is stored as int x/y.
        /// </summary>
        public static (int X, int Y) ToOrigin((double X, double Y) anchor, double left, double top)
            => ((int)Math.Round(anchor.X - left), (int)Math.Round(anchor.Y - top));
    }

    /// <summary>
    /// Positions the user has moved but not yet confirmed. Dragging and typing only ever write in
    /// here, so the WZ keeps its committed values until 確認修改 - which also means saving the WZ
    /// while a preview is on screen cannot leak the preview into the file.
    ///
    /// There is deliberately no stored "baseline": the committed value *is* whatever the WZ
    /// property currently holds, so discarding a preview is just forgetting it, and confirming one
    /// makes it the new baseline for free.
    /// </summary>
    public sealed class WorldMapPendingPositions<TKey>
    {
        private readonly Dictionary<TKey, (int X, int Y)> pending = new Dictionary<TKey, (int X, int Y)>();

        public int Count => pending.Count;
        public bool HasAny => pending.Count > 0;

        public IEnumerable<KeyValuePair<TKey, (int X, int Y)>> Entries => pending;

        public void Stage(TKey key, int x, int y) => pending[key] = (x, y);

        public bool TryGet(TKey key, out (int X, int Y) value) => pending.TryGetValue(key, out value);

        /// <summary>The previewed position when one exists, otherwise the committed one.</summary>
        public (int X, int Y) Effective(TKey key, int committedX, int committedY)
            => pending.TryGetValue(key, out (int X, int Y) preview) ? preview : (committedX, committedY);

        public void Remove(TKey key) => pending.Remove(key);

        public void Clear() => pending.Clear();
    }

    /// <summary>
    /// Creating a spot vector for an entry that has none yet.
    /// </summary>
    public static class WorldMapSpotFactory
    {
        /// <summary>
        /// Adds a spot holding the given coordinates to the entry and dirties its image. Returns
        /// the new vector, or null when there is no entry or one already exists.
        ///
        /// Only ever called from 確認修改: the position it stores is where the user actually
        /// dropped the marker, so nothing is being guessed on their behalf. Independent of the
        /// tree, because whether that branch happens to be expanded has nothing to do with
        /// whether the WZ can be edited.
        /// </summary>
        public static WzVectorProperty Create(WzSubProperty entry, int x, int y)
        {
            if (entry == null || entry["spot"] != null)
                return null;

            var spot = new WzVectorProperty("spot", new WzIntProperty("x", x), new WzIntProperty("y", y));
            entry.AddProperty(spot);
            if (spot.ParentImage != null)
                spot.ParentImage.Changed = true;
            return spot;
        }
    }

    /// <summary>
    /// Where to park entries that have no spot yet.
    ///
    /// Their stored position is effectively (0,0) - that is what this repository's codec reads a
    /// missing spot as - so they would all pile onto one pixel and be impossible to tell apart.
    /// Fanning them into a small grid is a display decision only: nothing is written, and whatever
    /// the user drags one to is what gets stored.
    /// </summary>
    public static class WorldMapPlaceholderLayout
    {
        public const int Columns = 8;
        public const int Step = 20;

        public static (int X, int Y) PositionFor(int index)
        {
            if (index < 0)
                index = 0;
            return ((index % Columns) * Step, (index / Columns) * Step);
        }
    }

    /// <summary>
    /// Creating a linkImg origin that does not exist yet.
    /// </summary>
    public static class WorldMapLinkImageOrigin
    {
        /// <summary>
        /// Adds an origin holding the given values to the canvas and dirties its image. Returns
        /// the new vector, or null when there is no canvas or one already exists.
        ///
        /// Created with its final values rather than (0,0)-then-edited, so confirming a preview is
        /// a single change instead of a create plus an immediate correction.
        ///
        /// Deliberately independent of the tree: the canvas is the real data, and whether the user
        /// happens to have expanded that branch has nothing to do with whether the WZ can be
        /// edited.
        /// </summary>
        public static WzVectorProperty Create(WzCanvasProperty canvas, int x, int y)
        {
            if (canvas == null || canvas["origin"] != null)
                return null;

            var origin = new WzVectorProperty("origin", new WzIntProperty("x", x), new WzIntProperty("y", y));
            canvas.AddProperty(origin);
            if (origin.ParentImage != null)
                origin.ParentImage.Changed = true;
            return origin;
        }
    }

    /// <summary>
    /// Per-pixel opacity for one piece of artwork, taken once when it is drawn.
    ///
    /// A WPF Image hit-tests as a plain rectangle, so a mostly-transparent picture swallows clicks
    /// meant for whatever sits underneath it - which is why some link images could never be
    /// grabbed. Testing the actual pixel instead lets a click fall through transparent areas.
    /// </summary>
    public sealed class WorldMapAlphaMask
    {
        /// <summary>Below this an edge pixel counts as transparent rather than a grab handle.</summary>
        public const byte OpaqueThreshold = 8;

        private readonly byte[] alpha;

        public int Width { get; }
        public int Height { get; }

        public WorldMapAlphaMask(int width, int height, byte[] alpha)
        {
            Width = width;
            Height = height;
            this.alpha = alpha;
        }

        /// <summary>True when the pixel at this offset inside the artwork can be grabbed.</summary>
        public bool IsOpaqueAt(double localX, double localY)
        {
            if (alpha == null)
                return false;

            int x = (int)Math.Floor(localX);
            int y = (int)Math.Floor(localY);
            if (x < 0 || y < 0 || x >= Width || y >= Height)
                return false;

            int index = y * Width + x;
            return index >= 0 && index < alpha.Length && alpha[index] >= OpaqueThreshold;
        }
    }

    /// <summary>
    /// Picks which piece of artwork a click landed on.
    /// </summary>
    public static class WorldMapLinkImageHitTester
    {
        /// <summary>
        /// The index of the topmost artwork whose pixel under (x, y) is opaque, or -1.
        /// Candidates must be ordered topmost first; transparent pixels fall through to whatever
        /// is beneath, so a big transparent frame never steals a click from the picture below it.
        /// </summary>
        public static int PickTopMost(
            IReadOnlyList<(double Left, double Top, WorldMapAlphaMask Mask)> candidatesTopFirst, double x, double y)
        {
            if (candidatesTopFirst == null)
                return -1;

            for (int index = 0; index < candidatesTopFirst.Count; index++)
            {
                (double left, double top, WorldMapAlphaMask mask) = candidatesTopFirst[index];
                if (mask == null)
                    continue;
                if (mask.IsOpaqueAt(x - left, y - top))
                    return index;
            }
            return -1;
        }
    }

    /// <summary>
    /// The one place a previewed position becomes a real WZ value.
    /// </summary>
    public static class WorldMapPositionCommit
    {
        /// <summary>
        /// Writes x/y into the vector and dirties its image. Returns false - writing nothing -
        /// when the vector is missing or already holds that position, so confirming a preview
        /// that ended up back where it started leaves the file clean.
        /// </summary>
        public static bool Apply(WzVectorProperty target, int x, int y)
        {
            if (target == null)
                return false;
            if (target.X.Value == x && target.Y.Value == y)
                return false;

            target.X.Value = x;
            target.Y.Value = y;
            if (target.ParentImage != null)
                target.ParentImage.Changed = true;
            return true;
        }
    }

    /// <summary>
    /// Moving a group of selected items together. Every item keeps its own position and shifts by
    /// the same delta, so a multi-selection holds its shape instead of collapsing onto one point.
    /// </summary>
    public static class WorldMapGroupMove
    {
        /// <summary>
        /// The position each item should end up at after shifting the whole selection by
        /// (deltaX, deltaY) from the positions captured when the drag started.
        /// </summary>
        public static Dictionary<IWorldMapMovable, (int X, int Y)> Offset(
            IReadOnlyDictionary<IWorldMapMovable, (int X, int Y)> startPositions, int deltaX, int deltaY)
        {
            var moved = new Dictionary<IWorldMapMovable, (int X, int Y)>();
            if (startPositions == null)
                return moved;

            foreach (KeyValuePair<IWorldMapMovable, (int X, int Y)> start in startPositions)
                moved[start.Key] = (start.Value.X + deltaX, start.Value.Y + deltaY);

            return moved;
        }
    }

    /// <summary>
    /// mapNo is a numerically-named list, so adding and removing entries has to keep the names
    /// contiguous - the client reads 0..n-1 and stops at the first gap. Pure index arithmetic,
    /// kept here so it can be tested without a tree.
    /// </summary>
    public static class WorldMapMapNoIndexer
    {
        /// <summary>
        /// The name a newly appended entry should take: one past the highest numeric name, so it
        /// lands at the end even if the existing names are not perfectly contiguous.
        /// </summary>
        public static string NextIndexName(IEnumerable<string> existingNames)
        {
            int next = 0;
            if (existingNames != null)
            {
                foreach (string name in existingNames)
                {
                    if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) && index >= next)
                        next = index + 1;
                }
            }
            return next.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The names the remaining entries should carry after a removal, in their current order:
        /// "0", "1", "2"... Returns only the ones that actually need renaming, so an unchanged
        /// entry is not needlessly marked as edited.
        /// </summary>
        public static Dictionary<string, string> Renumber(IReadOnlyList<string> remainingNamesInOrder)
        {
            var renames = new Dictionary<string, string>();
            if (remainingNamesInOrder == null)
                return renames;

            for (int index = 0; index < remainingNamesInOrder.Count; index++)
            {
                string expected = index.ToString(CultureInfo.InvariantCulture);
                if (!string.Equals(remainingNamesInOrder[index], expected, StringComparison.Ordinal))
                    renames[remainingNamesInOrder[index]] = expected;
            }
            return renames;
        }
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
        /// A WorldMap*.img that actually lives in a world-map container. The container check
        /// matters: a stray WorldMap123.img somewhere unrelated is not a world map and must not
        /// take over the panel.
        ///
        /// Two layouts count, because both occur in the wild:
        ///   Map.wz\WorldMap\WorldMap050.img   - a WorldMap directory inside a bigger WZ
        ///   WorldMap_000.wz\WorldMap050.img   - a split WZ that is itself the world map file,
        ///                                       where the images sit at the root with no
        ///                                       WorldMap directory above them at all
        /// </summary>
        public static bool IsWorldMapImage(WzObject obj)
        {
            if (obj is not WzImage image || image.Name == null)
                return false;
            if (!image.Name.StartsWith(WorldMapDirectoryName, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!image.Name.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                return false;

            return FindWorldMapContainer(image) != null;
        }

        /// <summary>
        /// The directory holding this image's world-map siblings, or null when it is not in one.
        /// Matches a container whose name starts with "WorldMap" so it covers both a WorldMap
        /// directory and the root of a WorldMap_000.wz style file (whose root directory carries
        /// the file's own name).
        /// </summary>
        public static WzDirectory FindWorldMapContainer(WzObject obj)
        {
            for (WzObject parent = obj?.Parent; parent != null; parent = parent.Parent)
            {
                if (parent is WzDirectory directory && IsWorldMapContainerName(directory.Name))
                    return directory;
            }

            // The root directory of a split file can be named without the WorldMap prefix even
            // though the file itself carries it, so fall back to the owning WZ file's name.
            if (obj is WzImage image && IsWorldMapContainerName(image.WzFileParent?.Name))
                return image.Parent as WzDirectory;

            return null;
        }

        private static bool IsWorldMapContainerName(string name)
            => name != null && name.StartsWith(WorldMapDirectoryName, StringComparison.OrdinalIgnoreCase);
    }
}
