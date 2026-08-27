using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MapleLib.Converters;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace HaRepacker.GUI.WorldMap
{
    /// <summary>
    /// Visual editor for a WorldMap*.img: the BaseImg artwork with every MapList spot and MapLink
    /// drawn on top, pan/zoom, multi-selection with group dragging, and an inspector for the
    /// selection's type / X / Y / mapNo.
    ///
    /// Parked in MainPanel's grid1 next to the skill preview and node editor and shown by
    /// MainPanel.ShowWorldMapEditorIfApplicable, exactly like those two - see there for why it
    /// takes priority over them.
    ///
    /// Built in code rather than XAML to match SkillPreview's panels, which are the closest
    /// precedent for a lazily-created editor dropped into that container.
    ///
    /// Nothing here writes to the WZ as a side effect of looking: pan, zoom, selecting and
    /// Ctrl-clicking never touch a property or set ParentImage.Changed, and a click that does not
    /// travel past the system drag threshold stays a selection rather than becoming a move. Only
    /// an actual edit writes - a finished drag, a type change, an X/Y entry, a mapNo edit/add/
    /// delete - and each reports the leaf properties it wrote through <see cref="PropertiesChanged"/>
    /// so MainPanel can redden exactly those tree nodes.
    /// </summary>
    public sealed class WorldMapEditorPanel : UserControl
    {
        private const double MinZoom = 0.25;
        private const double MaxZoom = 5.0;
        private const double ZoomStep = 1.1;
        private const double SpotRadius = 5.0;
        private const double LinkRadius = 6.0;
        private const double InspectorWidth = 260.0;
        private const double CanvasPadding = 64.0;

        // Explicit layering. BaseImg keeps the default 0; every marker must stay clickable above
        // every picture, so the gaps are wide enough that no realistic number of links closes them.
        private const int LinkImageZIndexBase = 10;
        private const int OutlineZIndex = 100000;
        private const int MarkerZIndex = 200000;

        // ---- chrome ----------------------------------------------------------------------------

        private TextBlock imageNameText;
        private TextBlock zoomText;
        private TextBlock statusText;
        private Button previousImageButton;

        private Grid viewport;
        private Canvas worldCanvas;
        private Image baseImage;
        private readonly ScaleTransform zoomTransform = new ScaleTransform(1.0, 1.0);
        private readonly TranslateTransform panTransform = new TranslateTransform(0.0, 0.0);

        private StackPanel inspector;
        private TextBlock inspectorTitle;
        private ComboBox typeBox;
        private TextBox spotXBox;
        private TextBox spotYBox;
        private StackPanel linkDetails;
        private StackPanel linkOriginSection;
        private TextBox originXBox;
        private TextBox originYBox;
        private TextBlock originMissingText;
        private StackPanel mapNoSection;
        private StackPanel mapNoList;

        // ---- state -----------------------------------------------------------------------------

        private WorldMapDocument document;
        private readonly Dictionary<IWorldMapMovable, Shape> markers = new Dictionary<IWorldMapMovable, Shape>();

        /// <summary>
        /// Everything currently selected. Spots and links share one set so a mixed selection drags
        /// as one group - they both move a "spot" vector, so nothing extra is needed to support it.
        /// </summary>
        private readonly HashSet<IWorldMapMovable> selectedItems = new HashSet<IWorldMapMovable>();

        /// <summary>The item the inspector describes when exactly one kind is selected.</summary>
        private IWorldMapMovable primarySelected;

        /// <summary>
        /// The linkImg artwork for each MapLink that has one, drawn between the base map and the
        /// markers. Kept apart from <see cref="markers"/> because a picture is dragged by its own
        /// origin, never as part of a marker group.
        /// </summary>
        private readonly Dictionary<WorldMapLink, Image> linkImages = new Dictionary<WorldMapLink, Image>();

        /// <summary>
        /// A dashed frame drawn over the selected link's artwork. A separate, non-hit-testable
        /// shape so the bitmap itself is never altered.
        /// </summary>
        private readonly Dictionary<WorldMapLink, Rectangle> linkImageOutlines = new Dictionary<WorldMapLink, Rectangle>();

        /// <summary>Render order, bottom-first - reversed to hit-test topmost artwork first.</summary>
        private readonly List<WorldMapLink> linkImageOrder = new List<WorldMapLink>();

        /// <summary>Per-artwork opacity, built once at render time for pixel-accurate clicking.</summary>
        private readonly Dictionary<WorldMapLink, WorldMapAlphaMask> linkImageMasks =
            new Dictionary<WorldMapLink, WorldMapAlphaMask>();

        /// <summary>
        /// Whether the linkImg artwork is drawn. A view preference that lives as long as this
        /// panel: it deliberately survives Clear, 重設視圖 and navigating to another WorldMap, so
        /// artwork the user switched off stays off until they switch it back on.
        /// </summary>
        private bool linkImagesVisible = true;

        private Button toggleLinkImagesButton;

        /// <summary>
        /// Display slot for each entry that has no spot yet, so they fan out instead of stacking
        /// on one pixel. Display only - see WorldMapPlaceholderLayout.
        /// </summary>
        private readonly Dictionary<IWorldMapMovable, int> placeholderSlots =
            new Dictionary<IWorldMapMovable, int>();

        /// <summary>What the in-progress drag is moving, so the two never mix.</summary>
        private enum WorldMapDragKind
        {
            None,

            /// <summary>The selection's spot vectors - MapList spots and/or MapLink spots.</summary>
            ItemPosition,

            /// <summary>One MapLink's linkImg, via its canvas origin. Never a group.</summary>
            LinkImage
        }

        /// <summary>
        /// Previewed spot positions (MapList spots and MapLink spots alike) and previewed linkImg
        /// origins. Dragging and typing write only here; the WZ is untouched until 確認修改.
        /// </summary>
        private readonly WorldMapPendingPositions<IWorldMapMovable> pendingPositions =
            new WorldMapPendingPositions<IWorldMapMovable>();
        private readonly WorldMapPendingPositions<WorldMapLink> pendingOrigins =
            new WorldMapPendingPositions<WorldMapLink>();

        private Button confirmButton;

        private WorldMapDragKind dragKind = WorldMapDragKind.None;
        private WorldMapLink draggingLinkImage;
        private double dragStartImageLeft;
        private double dragStartImageTop;

        private bool isPanning;
        private Point panStartPointer;
        private double panStartX;
        private double panStartY;

        /// <summary>
        /// Set on mouse-down over a marker, but a move only begins once the pointer travels past
        /// the system drag threshold - so a plain click selects without ever writing to the WZ.
        /// </summary>
        private bool isPendingDrag;
        private bool isDraggingItems;
        private Point dragStartPointerOnCanvas;
        private readonly Dictionary<IWorldMapMovable, (int X, int Y)> dragStartPositions =
            new Dictionary<IWorldMapMovable, (int X, int Y)>();

        /// <summary>Suppresses the inspector's commit handlers while it is being populated.</summary>
        private bool isPopulatingInspector;

        /// <summary>
        /// Raised with the leaf properties an edit actually wrote. MainPanel turns each into
        /// WzNode.ChangedNodeProperty() so only those leaves go red - never their parents. A group
        /// drag raises this once with every moved vector, not once per item.
        /// </summary>
        public event EventHandler<IReadOnlyList<WzImageProperty>> PropertiesChanged;

        /// <summary>Raised with a target image name ("WorldMap050.img") to move the tree to.</summary>
        public event EventHandler<string> NavigationRequested;

        /// <summary>
        /// Supplies the sibling WorldMap images to search when a spot is double-clicked. Set by
        /// MainPanel, which owns the tree and therefore knows how to resolve/parse lazy IMG
        /// references - only WorldMap siblings, never the whole WZ.
        /// </summary>
        public Func<IReadOnlyList<WzImage>> WorldMapSiblingProvider { get; set; }

        /// <summary>
        /// The app's undo manager, so adding a mapNo registers on the same stack as every other
        /// tree insertion. Set by MainPanel; structural edits fall back to plain mutation if absent.
        /// </summary>
        public UndoRedoManager UndoRedoMan { get; set; }

        /// <summary>
        /// image name -> every mapNo it contains. Built on first use and reused; invalidated for
        /// one image when this editor edits its mapNo. Scoped to this panel, not global.
        /// </summary>
        private readonly Dictionary<string, HashSet<int>> mapNumberCache =
            new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);

        public WorldMapEditorPanel()
        {
            BuildLayout();
        }

        // ---- load ------------------------------------------------------------------------------

        /// <summary>
        /// Accepts a WorldMap*.img in a world-map container. Returns false for anything else,
        /// which is how MainPanel decides whether to show this panel at all.
        /// </summary>
        public bool TryLoad(WzObject obj)
        {
            if (!WorldMapDetector.IsWorldMapImage(obj))
                return false;

            var image = (WzImage)obj;
            try
            {
                if (!image.Parsed)
                    image.ParseImage();
            }
            catch (Exception ex)
            {
                Clear();
                statusText.Text = "無法解析 " + image.Name + "：" + ex.Message;
                return true; // still a world map; showing the failure beats falling back to another editor
            }

            // Previews belong to the map that was on screen. The user never confirmed them, so
            // they are dropped rather than carried over - and they must never follow the user to
            // another image and get written there.
            pendingPositions.Clear();
            pendingOrigins.Clear();
            RefreshPendingState();

            document = WorldMapDocument.Load(image);
            selectedItems.Clear();
            primarySelected = null;

            imageNameText.Text = document.ImageName ?? string.Empty;
            previousImageButton.IsEnabled = WorldMapNavigation.NormalizeImageName(document.ParentMap) != null;

            RenderBase();
            RenderMarkers();
            UpdateCanvasBounds();
            ResetView();
            PopulateInspector();

            statusText.Text = document.Warning ?? DescribeContents();
            return true;
        }

        private string DescribeContents()
        {
            string counts = document.Spots.Count + " 個 Spot";
            if (document.Links.Count > 0)
                counts += "、" + document.Links.Count + " 個 MapLink";
            return counts + "　·　Ctrl+點擊可多選，拖曳可一起移動，雙擊 Spot 跳到系列地圖";
        }

        public void Clear()
        {
            document = null;
            pendingPositions.Clear();
            pendingOrigins.Clear();
            RefreshPendingState();
            selectedItems.Clear();
            primarySelected = null;
            markers.Clear();
            linkImages.Clear();
            linkImageOutlines.Clear();
            worldCanvas.Children.Clear();
            baseImage.Source = null;
            worldCanvas.Children.Add(baseImage);
            imageNameText.Text = string.Empty;
            statusText.Text = string.Empty;
            previousImageButton.IsEnabled = false;
            PopulateInspector();
        }

        private void RenderBase()
        {
            baseImage.Source = null;
            baseImage.Width = double.NaN;
            baseImage.Height = double.NaN;
            if (document.BaseCanvas == null)
                return;

            try
            {
                // Decoding a linked canvas can fail on a broken _inlink/_outlink; the markers are
                // still worth showing without the artwork.
                System.Drawing.Bitmap bitmap = document.BaseCanvas.GetLinkedWzCanvasBitmap();
                if (bitmap == null)
                    return;

                using (bitmap)
                {
                    BitmapSource source = bitmap.ToWpfBitmap();
                    source.Freeze();
                    baseImage.Source = source;
                    baseImage.Width = source.PixelWidth;
                    baseImage.Height = source.PixelHeight;
                }
            }
            catch (Exception ex)
            {
                statusText.Text = "BaseImg 無法顯示：" + ex.Message;
            }
        }

        private void RenderMarkers()
        {
            foreach (Shape marker in markers.Values)
                worldCanvas.Children.Remove(marker);
            markers.Clear();
            foreach (Image image in linkImages.Values)
                worldCanvas.Children.Remove(image);
            linkImages.Clear();
            foreach (Rectangle outline in linkImageOutlines.Values)
                worldCanvas.Children.Remove(outline);
            linkImageOutlines.Clear();

            // Entries with no spot get a display slot so they fan out rather than stacking on the
            // same pixel; assigned before anything is positioned.
            placeholderSlots.Clear();
            foreach (WorldMapSpot spot in document.Spots)
            {
                if (!spot.HasPosition)
                    placeholderSlots[spot] = placeholderSlots.Count;
            }
            foreach (WorldMapLink link in document.Links)
            {
                if (!link.HasPosition)
                    placeholderSlots[link] = placeholderSlots.Count;
            }

            // linkImg artwork first so it sits under every marker - a big picture must not cover
            // the spots' click targets. Explicit ZIndex rather than relying on insertion order.
            int undecodable = RenderLinkImages();

            foreach (WorldMapSpot spot in document.Spots)
                AddMarker(spot, CreateSpotMarker(spot));

            foreach (WorldMapLink link in document.Links)
                AddMarker(link, CreateLinkMarker(link));

            if (undecodable > 0)
                statusText.Text = "有 " + undecodable + " 個 MapLink 的 linkImg 無法顯示（其餘功能不受影響）。";
        }

        /// <summary>
        /// Decodes each linkImg once, here - never during a drag - and parks it on the canvas.
        /// Returns how many could not be decoded; a broken _inlink/_outlink costs that link its
        /// picture, nothing more: its marker still shows and still drags.
        /// </summary>
        private int RenderLinkImages()
        {
            int undecodable = 0;

            linkImageOrder.Clear();
            linkImageMasks.Clear();

            foreach (WorldMapLink link in document.Links)
            {
                if (link.LinkImageDisplay == null)
                    continue;

                BitmapSource source = TryDecodeCanvas(link.LinkImageDisplay);
                if (source == null)
                {
                    undecodable++;
                    continue;
                }

                var image = new Image
                {
                    Source = source,
                    Width = source.PixelWidth,
                    Height = source.PixelHeight,
                    Stretch = Stretch.None,
                    SnapsToDevicePixels = true,
                    ToolTip = BuildLinkImageTooltip(link),
                    Tag = link,
                    // Never hit-tested by WPF: an Image's bounds are a rectangle, so a mostly
                    // transparent picture swallows clicks meant for whatever is underneath it.
                    // The viewport resolves clicks by real pixel opacity instead - see
                    // HitTestLinkImage.
                    IsHitTestVisible = false,

                    // Freshly drawn artwork has to honour the current show/hide preference.
                    Visibility = WorldMapLinkImageVisibility.ShouldShowImage(linkImagesVisible)
                        ? Visibility.Visible
                        : Visibility.Collapsed
                };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);

                // Explicit layering rather than child order: later links sit above earlier ones,
                // and every marker stays above every picture.
                Panel.SetZIndex(image, LinkImageZIndexBase + linkImageOrder.Count);
                linkImages[link] = image;
                linkImageOrder.Add(link);
                linkImageMasks[link] = BuildAlphaMask(source);
                worldCanvas.Children.Add(image);

                var outline = new Rectangle
                {
                    Width = image.Width,
                    Height = image.Height,
                    Stroke = Brushes.White,
                    StrokeThickness = 1.0,
                    StrokeDashArray = new DoubleCollection { 3.0, 3.0 },
                    Fill = Brushes.Transparent,
                    IsHitTestVisible = false,
                    Visibility = Visibility.Collapsed
                };
                Panel.SetZIndex(outline, OutlineZIndex);
                linkImageOutlines[link] = outline;
                worldCanvas.Children.Add(outline);

                PositionLinkImage(link);
            }

            return undecodable;
        }

        /// <summary>
        /// Extracts the alpha channel once, when the artwork is drawn - hit-testing must never
        /// decode anything.
        /// </summary>
        private static WorldMapAlphaMask BuildAlphaMask(BitmapSource source)
        {
            try
            {
                BitmapSource bgra = source.Format == PixelFormats.Bgra32
                    ? source
                    : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0.0);

                int width = bgra.PixelWidth;
                int height = bgra.PixelHeight;
                int stride = width * 4;
                var pixels = new byte[stride * height];
                bgra.CopyPixels(pixels, stride, 0);

                var alpha = new byte[width * height];
                for (int i = 0; i < alpha.Length; i++)
                    alpha[i] = pixels[i * 4 + 3];

                return new WorldMapAlphaMask(width, height, alpha);
            }
            catch (Exception)
            {
                // Without a mask the picture just cannot be grabbed; its marker still works.
                return null;
            }
        }

        /// <summary>
        /// The topmost artwork whose pixel under the pointer is actually opaque, or null.
        /// Transparent pixels fall through to whatever is beneath, and then to the background
        /// where a click pans the map.
        /// </summary>
        private WorldMapLink HitTestLinkImage(Point pointerOnCanvas)
        {
            // Hidden artwork must not keep catching clicks: with the pictures off, the area they
            // covered behaves like plain background again.
            if (!WorldMapLinkImageVisibility.ShouldShowImage(linkImagesVisible))
                return null;

            if (linkImageOrder.Count == 0)
                return null;

            var candidates = new List<(double Left, double Top, WorldMapAlphaMask Mask)>(linkImageOrder.Count);
            var ordered = new List<WorldMapLink>(linkImageOrder.Count);

            // Topmost first: the last one rendered carries the highest ZIndex.
            for (int index = linkImageOrder.Count - 1; index >= 0; index--)
            {
                WorldMapLink link = linkImageOrder[index];
                if (!linkImages.TryGetValue(link, out Image image))
                    continue;

                linkImageMasks.TryGetValue(link, out WorldMapAlphaMask mask);
                candidates.Add((Canvas.GetLeft(image), Canvas.GetTop(image), mask));
                ordered.Add(link);
            }

            int hit = WorldMapLinkImageHitTester.PickTopMost(candidates, pointerOnCanvas.X, pointerOnCanvas.Y);
            return hit < 0 ? null : ordered[hit];
        }

        private static BitmapSource TryDecodeCanvas(WzCanvasProperty canvas)
        {
            try
            {
                // Same decoder the base map uses; a UOL/_inlink is resolved by MapleLib rather
                // than by hunting the image for some other canvas.
                System.Drawing.Bitmap bitmap = canvas.GetLinkedWzCanvasBitmap();
                if (bitmap == null)
                    return null;

                using (bitmap)
                {
                    BitmapSource source = bitmap.ToWpfBitmap();
                    source.Freeze();
                    return source;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string BuildLinkImageTooltip(WorldMapLink link)
        {
            string tooltip = "MapLink " + link.EntryName + " linkImg";
            WzVectorProperty origin = link.LinkImageOrigin;
            tooltip += origin == null
                ? "\nOrigin: 尚無"
                : "\nOrigin: " + origin.X.Value + "," + origin.Y.Value;
            if (!string.IsNullOrEmpty(link.LinkMap))
                tooltip += "\n→ " + link.LinkMap;
            return tooltip;
        }

        /// <summary>
        /// Places the artwork from its link's current spot and its own origin. Called whenever
        /// either changes - moving the spot slides the picture with it because the anchor moved,
        /// while the origin value stays exactly as it was.
        /// </summary>
        private void PositionLinkImage(WorldMapLink link)
        {
            if (!linkImages.TryGetValue(link, out Image image))
                return;

            (double Left, double Top) position = ComputeLinkImagePosition(link);
            Canvas.SetLeft(image, position.Left);
            Canvas.SetTop(image, position.Top);

            if (linkImageOutlines.TryGetValue(link, out Rectangle outline))
            {
                Canvas.SetLeft(outline, position.Left);
                Canvas.SetTop(outline, position.Top);
            }
        }

        /// <summary>
        /// Where the artwork is drawn. Positioned purely by linkImg's own origin against the
        /// world's zero point (BaseImg's origin) - the link's spot plays no part, so moving the
        /// spot leaves the picture where it is. Uses the *previewed* origin, so a dragged picture
        /// stays where it was dropped even though the WZ still holds the old one.
        /// </summary>
        private (double Left, double Top) ComputeLinkImagePosition(WorldMapLink link)
        {
            (double x, double y) anchor = WorldMapCoordinateConverter.WorldToCanvas(document.BaseOrigin, 0, 0);
            (int X, int Y) origin = EffectiveOrigin(link);
            return WorldMapLinkImagePlacement.ToCanvasPosition(anchor, origin.X, origin.Y);
        }

        private void AddMarker(IWorldMapMovable item, Shape marker)
        {
            marker.Tag = item;
            marker.MouseLeftButtonDown += Marker_MouseLeftButtonDown;
            // Above every picture, and it marks the event handled - so a marker click can never be
            // taken by the artwork underneath it.
            Panel.SetZIndex(marker, MarkerZIndex);
            markers[item] = marker;
            worldCanvas.Children.Add(marker);
            (int X, int Y) position = EffectivePosition(item);
            PositionMarker(item, position.X, position.Y);
        }

        private Shape CreateSpotMarker(WorldMapSpot spot)
        {
            var marker = new Ellipse
            {
                Width = SpotRadius * 2.0,
                Height = SpotRadius * 2.0,
                Fill = BrushForType(spot.Type?.Value),
                Stroke = Brushes.Black,
                StrokeThickness = 1.0,
                Cursor = Cursors.Hand,
                ToolTip = BuildSpotTooltip(spot)
            };
            ApplyPlaceholderLook(marker, spot.HasPosition);
            return marker;
        }

        /// <summary>
        /// Marks an entry that has no spot yet: hollow and dashed, so it is obvious it is parked
        /// rather than positioned. It still drags normally - the spot is created on confirm.
        /// </summary>
        private static void ApplyPlaceholderLook(Shape marker, bool hasPosition)
        {
            if (hasPosition)
                return;

            marker.Opacity = 0.65;
            marker.StrokeDashArray = new DoubleCollection { 2.0, 2.0 };
            marker.StrokeThickness = 1.5;
        }

        /// <summary>
        /// A rotated square - a diamond - so a MapLink is never mistaken for a MapList spot at a
        /// glance. MapLink has no type property in the schema, so it gets one fixed colour rather
        /// than an invented per-type palette.
        /// </summary>
        private Shape CreateLinkMarker(WorldMapLink link)
        {
            var marker = new Rectangle
            {
                Width = LinkRadius * 2.0,
                Height = LinkRadius * 2.0,
                Fill = Brushes.MediumOrchid,
                Stroke = Brushes.Black,
                StrokeThickness = 1.0,
                Cursor = Cursors.Hand,
                ToolTip = BuildLinkTooltip(link),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(45.0)
            };
            ApplyPlaceholderLook(marker, link.HasPosition);
            return marker;
        }

        private static string BuildSpotTooltip(WorldMapSpot spot)
        {
            string type = spot.Type == null ? "-" : spot.Type.Value.ToString(CultureInfo.InvariantCulture);
            string firstMapNo = spot.MapNo.Count > 0
                ? spot.MapNo[0].Value.ToString(CultureInfo.InvariantCulture)
                : "-";
            string tooltip = "MapList " + spot.EntryName + "\nType " + type + "\n" + firstMapNo;
            if (!spot.HasPosition)
                tooltip += "\n（尚無 spot，拖曳後按「確認修改」即可建立）";
            return tooltip;
        }

        /// <summary>Only fields the entry actually has; nothing is invented for display.</summary>
        private static string BuildLinkTooltip(WorldMapLink link)
        {
            string tooltip = "MapLink " + link.EntryName;
            if (!string.IsNullOrEmpty(link.ToolTip))
                tooltip += "\n" + link.ToolTip;
            if (!string.IsNullOrEmpty(link.LinkMap))
                tooltip += "\n→ " + link.LinkMap;
            if (!link.HasPosition)
                tooltip += "\n（尚無 spot，拖曳後按「確認修改」即可建立）";
            return tooltip;
        }

        /// <summary>
        /// Distinct colours per type so the map is readable at a glance. Any type outside the
        /// known set still gets drawn - just in a neutral colour - because a custom server can
        /// use values this build has never seen.
        /// </summary>
        private static Brush BrushForType(int? type)
        {
            switch (type)
            {
                case 0: return Brushes.DeepSkyBlue;
                case 1: return Brushes.LimeGreen;
                case 2: return Brushes.Gold;
                case 3: return Brushes.OrangeRed;
                case null: return Brushes.LightGray;
                default: return Brushes.MediumPurple;
            }
        }

        // ---- previewed vs committed values --------------------------------------------------------

        /// <summary>
        /// Where this item is shown: its preview when it has one, else its stored spot, else the
        /// placeholder slot given to entries that have no spot yet.
        /// </summary>
        private (int X, int Y) EffectivePosition(IWorldMapMovable item)
        {
            (int X, int Y) committed = item.HasPosition
                ? (item.Position.X.Value, item.Position.Y.Value)
                : PlaceholderPosition(item);
            return pendingPositions.Effective(item, committed.X, committed.Y);
        }

        private (int X, int Y) PlaceholderPosition(IWorldMapMovable item)
            => WorldMapPlaceholderLayout.PositionFor(
                placeholderSlots.TryGetValue(item, out int slot) ? slot : 0);

        /// <summary>
        /// The origin the artwork is drawn with - previewed when it has one, else the committed
        /// origin, else (0,0) for a linkImg that has none yet.
        /// </summary>
        private (int X, int Y) EffectiveOrigin(WorldMapLink link)
        {
            WzVectorProperty origin = link.LinkImageOrigin;
            return pendingOrigins.Effective(link, origin?.X.Value ?? 0, origin?.Y.Value ?? 0);
        }

        private bool HasPendingChanges => pendingPositions.HasAny || pendingOrigins.HasAny;

        private int PendingChangeCount => pendingPositions.Count + pendingOrigins.Count;

        private void RefreshPendingState()
        {
            if (confirmButton != null)
                confirmButton.IsEnabled = HasPendingChanges;
        }

        /// <summary>
        /// Forgets every preview and puts the markers and artwork back on their committed values.
        /// Writes nothing - the WZ never held the previews in the first place - and deliberately
        /// keeps the current selection so the user does not lose their place.
        /// </summary>
        private void DiscardPendingPositions()
        {
            pendingPositions.Clear();
            pendingOrigins.Clear();
            RefreshPendingState();

            if (document == null)
                return;

            foreach (IWorldMapMovable item in markers.Keys)
            {
                (int X, int Y) committed = item.HasPosition
                    ? (item.Position.X.Value, item.Position.Y.Value)
                    : PlaceholderPosition(item); // never had a spot: back to its parking slot
                PositionMarker(item, committed.X, committed.Y);
            }
            foreach (WorldMapLink link in linkImages.Keys)
                PositionLinkImage(link);

            UpdateCanvasBounds();
            PopulateInspector();
        }

        private void PositionMarker(IWorldMapMovable item, int spotX, int spotY)
        {
            if (!markers.TryGetValue(item, out Shape marker))
                return;

            (double x, double y) = WorldMapCoordinateConverter.WorldToCanvas(document.BaseOrigin, spotX, spotY);
            Canvas.SetLeft(marker, x - marker.Width / 2.0);
            Canvas.SetTop(marker, y - marker.Height / 2.0);
        }

        /// <summary>
        /// Gives the canvas a real size so hit-testing, dragging and zooming have stable bounds.
        /// Sized to cover the artwork and every marker - markers legitimately sit outside the
        /// artwork, and their coordinates must never be clamped to fit.
        /// </summary>
        private void UpdateCanvasBounds()
        {
            double width = baseImage.Source == null ? 0.0 : baseImage.Width;
            double height = baseImage.Source == null ? 0.0 : baseImage.Height;

            foreach (IWorldMapMovable item in markers.Keys)
            {
                (int X, int Y) spot = EffectivePosition(item);
                (double x, double y) = WorldMapCoordinateConverter.WorldToCanvas(
                    document.BaseOrigin, spot.X, spot.Y);
                width = Math.Max(width, x + CanvasPadding);
                height = Math.Max(height, y + CanvasPadding);
            }

            // linkImg artwork can extend well past its own anchor, so its far corner counts too.
            // Only the canvas grows - a picture placed at a negative position still renders
            // (worldCanvas does not clip) and its origin is never adjusted to make it fit.
            foreach (KeyValuePair<WorldMapLink, Image> entry in linkImages)
            {
                (double Left, double Top) position = ComputeLinkImagePosition(entry.Key);
                width = Math.Max(width, position.Left + entry.Value.Width + CanvasPadding);
                height = Math.Max(height, position.Top + entry.Value.Height + CanvasPadding);
            }

            worldCanvas.Width = Math.Max(width, 1.0);
            worldCanvas.Height = Math.Max(height, 1.0);
        }

        // ---- view ------------------------------------------------------------------------------

        /// <summary>
        /// Resets the camera *and* throws away every unconfirmed position - the way back out of a
        /// preview. Nothing is written: the WZ never held the previews, so this is pure forgetting.
        /// No re-parse and no re-decode either; the cached bitmaps and the loaded document stay as
        /// they are and only the placements are recomputed.
        /// </summary>
        private void ResetView()
        {
            zoomTransform.ScaleX = 1.0;
            zoomTransform.ScaleY = 1.0;
            panTransform.X = 0.0;
            panTransform.Y = 0.0;
            UpdateZoomText();

            bool discarded = HasPendingChanges;
            DiscardPendingPositions();
            if (discarded)
                statusText.Text = "已取消尚未確認的位置修改並重設視圖。";
        }

        private void UpdateZoomText()
        {
            zoomText.Text = "Zoom: " + Math.Round(zoomTransform.ScaleX * 100.0) + "%";
        }

        private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (document == null)
                return;

            double factor = e.Delta > 0 ? ZoomStep : 1.0 / ZoomStep;
            double target = Math.Clamp(zoomTransform.ScaleX * factor, MinZoom, MaxZoom);
            if (Math.Abs(target - zoomTransform.ScaleX) < double.Epsilon)
                return;

            // Zoom about the cursor: keep whatever world point is under the pointer pinned to the
            // same screen position, so zooming in on a spot does not fling it off-screen.
            Point pointerInViewport = e.GetPosition(viewport);
            Point pointerInWorld = e.GetPosition(worldCanvas);

            zoomTransform.ScaleX = target;
            zoomTransform.ScaleY = target;
            panTransform.X = pointerInViewport.X - pointerInWorld.X * target;
            panTransform.Y = pointerInViewport.Y - pointerInWorld.Y * target;

            UpdateZoomText();
            e.Handled = true;
        }

        private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (document == null)
                return;

            // A marker handled the click already (it sets Handled), so reaching here means the
            // pointer is over artwork or empty background. Artwork is resolved by real pixel
            // opacity, so a transparent area falls through to the picture below and then to the
            // background - where the click pans instead.
            viewport.Focus();

            WorldMapLink hitLink = HitTestLinkImage(e.GetPosition(worldCanvas));
            if (hitLink != null)
            {
                BeginLinkImageInteraction(hitLink, e);
                return;
            }

            isPanning = true;
            panStartPointer = e.GetPosition(viewport);
            panStartX = panTransform.X;
            panStartY = panTransform.Y;
            viewport.CaptureMouse();
        }

        private void Viewport_MouseMove(object sender, MouseEventArgs e)
        {
            if (isPendingDrag || isDraggingItems)
            {
                Point pointer = e.GetPosition(worldCanvas);
                double deltaX = pointer.X - dragStartPointerOnCanvas.X;
                double deltaY = pointer.Y - dragStartPointerOnCanvas.Y;

                if (!isDraggingItems)
                {
                    // Below the system drag threshold this is still a click, not a move - a shaky
                    // hand must not silently edit the WZ.
                    if (Math.Abs(deltaX) * zoomTransform.ScaleX < SystemParameters.MinimumHorizontalDragDistance
                        && Math.Abs(deltaY) * zoomTransform.ScaleY < SystemParameters.MinimumVerticalDragDistance)
                        return;
                    isDraggingItems = true;
                }

                if (dragKind == WorldMapDragKind.LinkImage)
                    DragLinkImageBy(deltaX, deltaY);
                else
                    DragSelectionBy(deltaX, deltaY);
                return;
            }

            if (!isPanning)
                return;

            Point current = e.GetPosition(viewport);
            panTransform.X = panStartX + (current.X - panStartPointer.X);
            panTransform.Y = panStartY + (current.Y - panStartPointer.Y);
        }

        private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (isDraggingItems)
            {
                if (dragKind == WorldMapDragKind.LinkImage)
                    CommitLinkImageDrag();
                else
                    CommitDrag();
            }
            else if (isPendingDrag)
            {
                PopulateInspector(); // click without a move: selection only, nothing written
            }

            isPendingDrag = false;
            isDraggingItems = false;
            dragKind = WorldMapDragKind.None;
            draggingLinkImage = null;
            dragStartPositions.Clear();

            isPanning = false;
            if (viewport.IsMouseCaptured)
                viewport.ReleaseMouseCapture();
        }

        private void Viewport_KeyDown(object sender, KeyEventArgs e)
        {
            if (document == null || e.Key != Key.Escape)
                return;

            ClearSelection();
            e.Handled = true;
        }

        // ---- selection ---------------------------------------------------------------------------

        private void Marker_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Shape marker || marker.Tag is not IWorldMapMovable item)
                return;

            // Handled so the viewport does not also start a pan - dragging a marker must move the
            // marker, not the map.
            e.Handled = true;
            viewport.Focus();

            if (e.ClickCount >= 2)
            {
                isPendingDrag = false;
                isDraggingItems = false;
                NavigateFrom(item);
                return;
            }

            bool additive = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (additive)
            {
                // Ctrl+click is a selection toggle and nothing else - it never starts a move, so
                // building a multi-selection can never nudge anything.
                if (!selectedItems.Remove(item))
                {
                    selectedItems.Add(item);
                    primarySelected = item;
                }
                else if (ReferenceEquals(primarySelected, item))
                {
                    primarySelected = selectedItems.FirstOrDefault();
                }
                RefreshSelectionVisuals();
                PopulateInspector();
                return;
            }

            // Plain click on something outside the selection replaces it; on something already
            // selected it keeps the group so the whole group can be dragged.
            if (!selectedItems.Contains(item))
            {
                selectedItems.Clear();
                selectedItems.Add(item);
            }
            primarySelected = item;
            RefreshSelectionVisuals();
            PopulateInspector();

            BeginPendingDrag(e.GetPosition(worldCanvas));
        }

        private void BeginPendingDrag(Point pointerOnCanvas)
        {
            dragStartPositions.Clear();
            // Start from what is on screen, not from the WZ - dragging something a second time
            // must continue from where the last preview left it.
            foreach (IWorldMapMovable item in selectedItems)
                dragStartPositions[item] = EffectivePosition(item);

            dragKind = WorldMapDragKind.ItemPosition;
            dragStartPointerOnCanvas = pointerOnCanvas;
            isPendingDrag = true;
            isDraggingItems = false;
            viewport.CaptureMouse();
        }

        /// <summary>
        /// Clicking the artwork selects its MapLink - the inspector then describes that link - but
        /// the drag it starts moves only this one picture's origin, whatever else is selected.
        /// </summary>
        /// <summary>
        /// Clicking the artwork selects its MapLink - the inspector then describes that link - but
        /// the drag it starts moves only this one picture's origin, whatever else is selected.
        /// Called from the viewport once the pixel-accurate hit test has picked this link.
        /// </summary>
        private void BeginLinkImageInteraction(WorldMapLink link, MouseButtonEventArgs e)
        {
            if (!linkImages.TryGetValue(link, out Image image))
                return;

            if (e.ClickCount >= 2)
            {
                isPendingDrag = false;
                isDraggingItems = false;
                dragKind = WorldMapDragKind.None;
                NavigateFrom(link);
                return;
            }

            if (!selectedItems.Contains(link))
            {
                selectedItems.Clear();
                selectedItems.Add(link);
            }
            primarySelected = link;
            RefreshSelectionVisuals();
            PopulateInspector();

            if (link.IsLinkImageReadOnly)
            {
                // Shown, selectable, but not movable - see WorldMapLink.LinkImageEditable.
                statusText.Text = "此 linkImg 為 UOL，共享來源，暫不直接修改 origin。";
                return;
            }

            // Deliberately not BeginPendingDrag: a picture is never part of a group move.
            draggingLinkImage = link;
            dragStartImageLeft = Canvas.GetLeft(image);
            dragStartImageTop = Canvas.GetTop(image);
            dragKind = WorldMapDragKind.LinkImage;
            dragStartPointerOnCanvas = e.GetPosition(worldCanvas);
            isPendingDrag = true;
            isDraggingItems = false;
            viewport.CaptureMouse();
        }

        /// <summary>
        /// Live preview only: the picture follows the pointer by moving its Canvas.Left/Top.
        /// Nothing is written and nothing is dirtied until the button comes up.
        /// </summary>
        private void DragLinkImageBy(double deltaX, double deltaY)
        {
            if (draggingLinkImage == null || !linkImages.TryGetValue(draggingLinkImage, out Image image))
                return;

            double left = dragStartImageLeft + deltaX;
            double top = dragStartImageTop + deltaY;
            Canvas.SetLeft(image, left);
            Canvas.SetTop(image, top);

            (int x, int y) = OriginForPosition(draggingLinkImage, left, top);
            ShowOrigin(x, y);
        }

        /// <summary>
        /// The exact inverse of <see cref="ComputeLinkImagePosition"/> - same anchor, or a drag
        /// would compute an origin that puts the picture somewhere other than where it was
        /// dropped. The link is not consulted: placement depends only on linkImg's own origin.
        /// </summary>
        private (int X, int Y) OriginForPosition(WorldMapLink link, double left, double top)
        {
            (double x, double y) anchor = WorldMapCoordinateConverter.WorldToCanvas(document.BaseOrigin, 0, 0);
            return WorldMapLinkImagePlacement.ToOrigin(anchor, left, top);
        }

        /// <summary>
        /// Turns where the picture ended up back into an origin and writes it. Only linkImg's own
        /// origin is touched - the link's spot is left exactly as it was, which is what keeps the
        /// two drags independent.
        /// </summary>
        private void CommitLinkImageDrag()
        {
            WorldMapLink link = draggingLinkImage;
            if (link == null || !linkImages.TryGetValue(link, out Image image))
                return;

            (int x, int y) = OriginForPosition(link, Canvas.GetLeft(image), Canvas.GetTop(image));

            // Preview only. A linkImg with no origin yet keeps its previewed one here too - the
            // property is not created until 確認修改, so browsing still cannot alter the WZ.
            WzVectorProperty origin = link.LinkImageOrigin;
            if (origin != null && origin.X.Value == x && origin.Y.Value == y)
                pendingOrigins.Remove(link);
            else
                pendingOrigins.Stage(link, x, y);

            PositionLinkImage(link);
            UpdateCanvasBounds();
            RefreshPendingState();
            PopulateInspector();
            statusText.Text = "已預覽 MapLink " + link.EntryName + " 的 linkImg origin（" + x + ", " + y
                + "），共 " + PendingChangeCount + " 項尚未確認，請按「確認修改」套用。";
        }

        /// <summary>
        /// Adds an origin holding the confirmed values to a linkImg that has none.
        ///
        /// The canvas is the real data; whether the user happened to expand that branch of the
        /// tree has nothing to do with whether the WZ can be edited. So when the tree node exists
        /// the insertion goes through it (node appears immediately, undo registered), and when it
        /// does not, the property is added straight to the canvas. In the latter case there is no
        /// WzNode to redden - but ParentImage.Changed is what makes the file save, and expanding
        /// that branch later shows the new origin from the real data.
        ///
        /// Created with its final values rather than (0,0)-then-corrected.
        /// </summary>
        /// <summary>
        /// Adds a spot to an entry that has none, holding the confirmed coordinates. Uses the tree
        /// when that node exists (node appears immediately, undo registered) and writes straight to
        /// the entry when it does not - the WZ is the real data, and whether the branch happens to
        /// be expanded has nothing to do with whether it can be edited.
        /// </summary>
        private WzVectorProperty TryCreateSpot(IWorldMapMovable item, int x, int y)
        {
            WzSubProperty owner = item.Owner;
            if (owner == null)
            {
                statusText.Text = item.DisplayName + " 沒有可建立 spot 的節點。";
                return null;
            }

            if (owner.HRTag is WzNode entryNode)
            {
                var viaTree = new WzVectorProperty("spot", new WzIntProperty("x", x), new WzIntProperty("y", y));
                WzNode added = AddChildNode(entryNode, viaTree);
                if (added != null)
                    return added.Tag as WzVectorProperty;
                // Fall through: the tree refused it, but the WZ edit itself is still valid.
            }

            return WorldMapSpotFactory.Create(owner, x, y);
        }

        private WzVectorProperty TryCreateLinkImageOrigin(WorldMapLink link, int x, int y)
        {
            WzCanvasProperty canvas = link.LinkImageEditable;
            if (canvas == null)
            {
                statusText.Text = link.IsLinkImageReadOnly
                    ? "此 linkImg 為 UOL，共享來源，暫不直接修改 origin。"
                    : "MapLink " + link.EntryName + " 沒有可編輯的 linkImg，無法建立 origin。";
                return null;
            }

            if (canvas.HRTag is WzNode canvasNode)
            {
                var viaTree = new WzVectorProperty("origin", new WzIntProperty("x", x), new WzIntProperty("y", y));
                WzNode added = AddChildNode(canvasNode, viaTree);
                if (added != null)
                    return added.Tag as WzVectorProperty;
                // Fall through: the tree refused it, but the WZ edit itself is still valid.
            }

            return WorldMapLinkImageOrigin.Create(canvas, x, y);
        }

        private void ClearSelection()
        {
            selectedItems.Clear();
            primarySelected = null;
            RefreshSelectionVisuals();
            PopulateInspector();
        }

        private void SelectAllSpots()
        {
            if (document == null)
                return;

            selectedItems.Clear();
            foreach (WorldMapSpot spot in document.Spots)
                selectedItems.Add(spot);
            primarySelected = document.Spots.Count > 0 ? document.Spots[0] : null;
            RefreshSelectionVisuals();
            PopulateInspector();
        }

        /// <summary>
        /// Selected items get a white outline; the primary one is drawn thicker so the inspector's
        /// subject is identifiable inside a group.
        /// </summary>
        private void RefreshSelectionVisuals()
        {
            foreach (KeyValuePair<IWorldMapMovable, Shape> entry in markers)
            {
                bool selected = selectedItems.Contains(entry.Key);
                entry.Value.Stroke = selected ? Brushes.White : Brushes.Black;
                entry.Value.StrokeThickness = selected
                    ? (ReferenceEquals(entry.Key, primarySelected) ? 3.0 : 2.0)
                    : 1.0;
            }

            foreach (KeyValuePair<WorldMapLink, Rectangle> entry in linkImageOutlines)
            {
                entry.Value.Visibility = WorldMapLinkImageVisibility.ShouldShowOutline(
                    linkImagesVisible, selectedItems.Contains(entry.Key))
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Flips the linkImg artwork on or off. Pure display: no re-render, no re-decode, no
        /// reposition, and the pending previews, the document and the WZ are all left alone.
        /// </summary>
        private void ToggleLinkImages()
        {
            linkImagesVisible = !linkImagesVisible;
            UpdateLinkImageVisibility();
            statusText.Text = WorldMapLinkImageVisibility.StatusText(linkImagesVisible);
        }

        /// <summary>
        /// Applies the current preference to the artwork already on the canvas and to the button.
        /// Markers - spot dots and MapLink diamonds - are untouched on purpose: only the pictures
        /// go away.
        /// </summary>
        private void UpdateLinkImageVisibility()
        {
            Visibility imageVisibility = WorldMapLinkImageVisibility.ShouldShowImage(linkImagesVisible)
                ? Visibility.Visible
                : Visibility.Collapsed;

            foreach (Image image in linkImages.Values)
                image.Visibility = imageVisibility;

            foreach (KeyValuePair<WorldMapLink, Rectangle> entry in linkImageOutlines)
            {
                entry.Value.Visibility = WorldMapLinkImageVisibility.ShouldShowOutline(
                    linkImagesVisible, selectedItems.Contains(entry.Key))
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (toggleLinkImagesButton != null)
                toggleLinkImagesButton.Content = WorldMapLinkImageVisibility.ButtonText(linkImagesVisible);
        }

        // ---- dragging ----------------------------------------------------------------------------

        /// <summary>
        /// Live feedback only - the markers and the X/Y boxes follow the pointer, but nothing is
        /// written until the button comes up. Marking the image dirty on every mouse move would
        /// flag the WZ as changed for a drag the user then abandons.
        /// </summary>
        private void DragSelectionBy(double deltaX, double deltaY)
        {
            (int worldDeltaX, int worldDeltaY) = WorldMapCoordinateConverter.CanvasToWorld(
                new System.Drawing.PointF(0f, 0f), deltaX, deltaY);

            Dictionary<IWorldMapMovable, (int X, int Y)> moved =
                WorldMapGroupMove.Offset(dragStartPositions, worldDeltaX, worldDeltaY);

            // Only the markers move. linkImg is placed by its own origin, not by the spot, so a
            // link's artwork deliberately stays put while its spot is dragged.
            foreach (KeyValuePair<IWorldMapMovable, (int X, int Y)> entry in moved)
                PositionMarker(entry.Key, entry.Value.X, entry.Value.Y);

            if (primarySelected != null && moved.TryGetValue(primarySelected, out (int X, int Y) primary))
                ShowCoordinates(primary.X, primary.Y);
        }

        /// <summary>
        /// Writes every item that actually moved, in one go, and reports them as a single batch so
        /// MainPanel reddens all of them from one event.
        /// </summary>
        private void CommitDrag()
        {
            int staged = 0;

            foreach (KeyValuePair<IWorldMapMovable, (int X, int Y)> start in dragStartPositions)
            {
                IWorldMapMovable item = start.Key;
                if (!markers.TryGetValue(item, out Shape marker))
                    continue;

                // The marker already sits where the drag left it; convert that back to WZ space.
                double centreX = Canvas.GetLeft(marker) + marker.Width / 2.0;
                double centreY = Canvas.GetTop(marker) + marker.Height / 2.0;
                (int x, int y) = WorldMapCoordinateConverter.CanvasToWorld(document.BaseOrigin, centreX, centreY);

                // An entry with no spot yet always stages: there is no stored value it could be
                // "already at", and confirming is what creates the spot.
                if (item.HasPosition && item.Position.X.Value == x && item.Position.Y.Value == y)
                {
                    // Back where it started: drop any preview instead of keeping a no-op one.
                    pendingPositions.Remove(item);
                }
                else
                {
                    pendingPositions.Stage(item, x, y);
                    staged++;
                }
            }

            if (staged > 0)
            {
                UpdateCanvasBounds();
                // Preview only: nothing written, nothing dirtied, nothing reddened until 確認修改.
                statusText.Text = "已預覽 " + staged + " 個位置修改，共 " + PendingChangeCount + " 項尚未確認，請按「確認修改」套用。";
            }

            RefreshPendingState();
            PopulateInspector();
        }

        // ---- inspector ---------------------------------------------------------------------------

        private void ShowCoordinates(int x, int y)
        {
            isPopulatingInspector = true;
            try
            {
                spotXBox.Text = x.ToString(CultureInfo.InvariantCulture);
                spotYBox.Text = y.ToString(CultureInfo.InvariantCulture);
            }
            finally
            {
                isPopulatingInspector = false;
            }
        }

        private void PopulateInspector()
        {
            isPopulatingInspector = true;
            try
            {
                mapNoList.Children.Clear();
                linkDetails.Children.Clear();
                linkDetails.Visibility = Visibility.Collapsed;
                linkOriginSection.Visibility = Visibility.Collapsed;
                originXBox.IsEnabled = false;
                originYBox.IsEnabled = false;
                originXBox.Text = string.Empty;
                originYBox.Text = string.Empty;
                mapNoSection.Visibility = Visibility.Collapsed;

                if (selectedItems.Count == 0)
                {
                    inspectorTitle.Text = "未選取";
                    typeBox.ItemsSource = null;
                    typeBox.IsEnabled = false;
                    spotXBox.Text = string.Empty;
                    spotYBox.Text = string.Empty;
                    spotXBox.IsEnabled = false;
                    spotYBox.IsEnabled = false;
                    return;
                }

                if (selectedItems.Count > 1)
                {
                    PopulateMultiSelection();
                    return;
                }

                if (primarySelected is WorldMapSpot spot)
                    PopulateSpot(spot);
                else if (primarySelected is WorldMapLink link)
                    PopulateLink(link);
            }
            finally
            {
                isPopulatingInspector = false;
            }
        }

        private void PopulateMultiSelection()
        {
            int spots = selectedItems.OfType<WorldMapSpot>().Count();
            int links = selectedItems.OfType<WorldMapLink>().Count();
            inspectorTitle.Text = links == 0
                ? "已選取 " + spots + " 個 Spot"
                : (spots == 0 ? "已選取 " + links + " 個 MapLink"
                              : "已選取 " + spots + " 個 Spot、" + links + " 個 MapLink");

            // A single absolute X/Y would be a lie for a group, so the boxes are shown empty and
            // disabled - group positioning is done by dragging.
            spotXBox.Text = string.Empty;
            spotYBox.Text = string.Empty;
            spotXBox.IsEnabled = false;
            spotYBox.IsEnabled = false;

            var types = selectedItems.OfType<WorldMapSpot>()
                .Select(s => s.Type?.Value)
                .Distinct()
                .ToList();

            typeBox.IsEnabled = false;
            if (types.Count == 1 && types[0].HasValue)
            {
                typeBox.ItemsSource = new List<int> { types[0].Value };
                typeBox.SelectedItem = types[0].Value;
            }
            else
            {
                typeBox.ItemsSource = null;
                typeBox.Text = types.Count > 1 ? "多個值" : "-";
            }

            // mapNo belongs to one spot; hidden for a group.
        }

        private void PopulateSpot(WorldMapSpot spot)
        {
            inspectorTitle.Text = spot.DisplayName;

            // Offer the usual values, plus whatever this spot actually has - an unknown type must
            // stay selectable and must never be silently rewritten to 0.
            var typeOptions = new List<int> { 0, 1, 2, 3 };
            if (spot.Type != null && !typeOptions.Contains(spot.Type.Value))
                typeOptions.Add(spot.Type.Value);
            typeOptions.Sort();

            typeBox.ItemsSource = typeOptions;
            typeBox.IsEnabled = spot.Type != null;
            typeBox.SelectedItem = spot.Type?.Value;
            if (spot.Type == null)
                typeBox.Text = "-";

            spotXBox.IsEnabled = true;
            spotYBox.IsEnabled = true;
            // Shows the preview when there is one, so the boxes agree with what is on the map.
            (int X, int Y) shownSpot = EffectivePosition(spot);
            spotXBox.Text = shownSpot.X.ToString(CultureInfo.InvariantCulture);
            spotYBox.Text = shownSpot.Y.ToString(CultureInfo.InvariantCulture);

            mapNoSection.Visibility = Visibility.Visible;
            foreach (WzIntProperty mapNo in spot.MapNo)
                mapNoList.Children.Add(BuildMapNoRow(spot, mapNo));
        }

        private void PopulateLink(WorldMapLink link)
        {
            inspectorTitle.Text = link.DisplayName;

            // MapLink has no type in the schema, so the type row is inert rather than invented.
            typeBox.ItemsSource = null;
            typeBox.IsEnabled = false;
            typeBox.Text = "-";

            spotXBox.IsEnabled = true;
            spotYBox.IsEnabled = true;
            (int X, int Y) shownSpot = EffectivePosition(link);
            spotXBox.Text = shownSpot.X.ToString(CultureInfo.InvariantCulture);
            spotYBox.Text = shownSpot.Y.ToString(CultureInfo.InvariantCulture);

            // linkImg's own placement, separate from the spot above.
            if (link.LinkImageDisplay != null)
            {
                linkOriginSection.Visibility = Visibility.Visible;
                bool hasOrigin = link.LinkImageOrigin != null;
                bool hasPreview = pendingOrigins.TryGet(link, out (int X, int Y) _);
                bool editable = !link.IsLinkImageReadOnly;

                // No origin yet: read-only until an explicit drag previews one. Merely looking at
                // a MapLink must never create one. A UOL's artwork is shown but not repositionable.
                originXBox.IsEnabled = editable && hasOrigin;
                originYBox.IsEnabled = editable && hasOrigin;
                originMissingText.Text = editable
                    ? "尚無 origin（拖曳圖片即可建立）"
                    : "此 linkImg 為 UOL，共享來源，暫不直接修改 origin。";
                originMissingText.Visibility = !editable || !(hasOrigin || hasPreview)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                if (hasOrigin || hasPreview)
                {
                    (int X, int Y) shownOrigin = EffectiveOrigin(link);
                    ShowOrigin(shownOrigin.X, shownOrigin.Y);
                }
            }

            // Only what the entry actually carries.
            if (!string.IsNullOrEmpty(link.ToolTip))
                linkDetails.Children.Add(ReadOnlyDetail("toolTip", link.ToolTip));
            if (!string.IsNullOrEmpty(link.LinkMap))
                linkDetails.Children.Add(ReadOnlyDetail("link/linkMap", link.LinkMap));
            linkDetails.Visibility = linkDetails.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static UIElement ReadOnlyDetail(string label, string value)
        {
            var stack = new StackPanel { Margin = new Thickness(0.0, 0.0, 0.0, 8.0) };
            stack.Children.Add(FieldLabel(label));
            stack.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap });
            return stack;
        }

        private UIElement BuildMapNoRow(WorldMapSpot spot, WzIntProperty mapNo)
        {
            var row = new DockPanel { Margin = new Thickness(0.0, 0.0, 0.0, 4.0) };

            var index = new TextBlock
            {
                Text = mapNo.Name,
                Width = 24.0,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(index, Dock.Left);
            row.Children.Add(index);

            var remove = new Button
            {
                Content = "×",
                Width = 24.0,
                Height = 24.0,
                Margin = new Thickness(4.0, 0.0, 0.0, 0.0),
                ToolTip = "刪除這一筆 mapNo",
                Tag = mapNo
            };
            remove.SetResourceReference(StyleProperty, "HareButtonStyle");
            remove.Click += delegate { DeleteMapNo(spot, mapNo); };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);

            var box = new TextBox
            {
                Text = mapNo.Value.ToString(CultureInfo.InvariantCulture),
                Height = 24.0,
                VerticalContentAlignment = VerticalAlignment.Center,
                Tag = mapNo
            };
            box.KeyDown += MapNoBox_KeyDown;
            box.LostFocus += MapNoBox_LostFocus;
            row.Children.Add(box);

            return row;
        }

        private void MapNoBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;
            CommitMapNo(sender as TextBox);
            e.Handled = true;
        }

        private void MapNoBox_LostFocus(object sender, RoutedEventArgs e) => CommitMapNo(sender as TextBox);

        private void CommitMapNo(TextBox box)
        {
            if (isPopulatingInspector || box?.Tag is not WzIntProperty mapNo)
                return;

            if (!int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                box.Text = mapNo.Value.ToString(CultureInfo.InvariantCulture);
                statusText.Text = "mapNo 必須是整數";
                return;
            }
            if (value == mapNo.Value)
                return;

            mapNo.Value = value;
            MarkChanged(mapNo);
            InvalidateMapNumberCache();
            statusText.Text = "mapNo " + mapNo.Name + " 已更新為 " + value + "。";
        }

        // ---- mapNo structure ----------------------------------------------------------------------

        /// <summary>
        /// Appends a new mapNo entry, creating the mapNo container itself when the spot has none.
        /// Only ever reached from the explicit button - opening the editor never adds anything.
        /// Goes through WzNode so the tree gains the node too, rather than the WZ and the tree
        /// drifting apart.
        /// </summary>
        private void AddMapNo(WorldMapSpot spot)
        {
            if (spot.Entry.HRTag is not WzNode entryNode)
            {
                statusText.Text = "找不到對應的樹節點，無法新增 mapNo。";
                return;
            }

            WzNode containerNode;
            if (spot.Entry["mapNo"] is WzSubProperty existing)
            {
                containerNode = existing.HRTag as WzNode;
            }
            else
            {
                containerNode = AddChildNode(entryNode, new WzSubProperty("mapNo"));
            }

            if (containerNode?.Tag is not WzSubProperty container)
            {
                statusText.Text = "無法建立 mapNo。";
                return;
            }

            string name = WorldMapMapNoIndexer.NextIndexName(container.WzProperties.Select(p => p.Name));
            WzNode added = AddChildNode(containerNode, new WzIntProperty(name, 0));
            if (added == null)
                return;

            InvalidateMapNumberCache();
            ReloadPreservingSelection();
            statusText.Text = "已新增 mapNo " + name + "。";
        }

        /// <summary>
        /// Inserts through WzNode.AddObject when the undo manager is available, so the insertion
        /// lands on the same undo stack as every other tree insertion and the tree node is created
        /// for us. Falls back to a plain insert when there is no manager.
        /// </summary>
        private WzNode AddChildNode(WzNode parentNode, WzImageProperty property)
        {
            if (UndoRedoMan != null)
                return parentNode.AddObject(property, UndoRedoMan);

            if (parentNode.Tag is not IPropertyContainer container)
                return null;

            container.AddProperty(property);
            if (property.ParentImage != null)
                property.ParentImage.Changed = true;

            var node = new WzNode(property, true);
            parentNode.Nodes.Add(node);
            return node;
        }

        /// <summary>
        /// Removes one mapNo entry and renumbers the rest so the names stay contiguous - the
        /// client reads 0..n-1 and stops at the first gap, so leaving a hole would silently drop
        /// the remaining maps. Structural, so it asks first.
        ///
        /// The empty mapNo container is deliberately left behind: removing it too would be a
        /// second structural change the user did not ask for.
        /// </summary>
        private void DeleteMapNo(WorldMapSpot spot, WzIntProperty mapNo)
        {
            if (spot.Entry["mapNo"] is not WzSubProperty container)
                return;
            if (!Warning.Warn("確定刪除 mapNo " + mapNo.Name + "？"))
                return;

            if (mapNo.HRTag is WzNode mapNoNode)
                mapNoNode.DeleteWzNode();
            else
                container.RemoveProperty(mapNo);

            if (container.ParentImage != null)
                container.ParentImage.Changed = true;

            RenumberMapNo(container);
            InvalidateMapNumberCache();
            ReloadPreservingSelection();
            statusText.Text = "已刪除 mapNo " + mapNo.Name + "。";
        }

        private static void RenumberMapNo(WzSubProperty container)
        {
            List<WzImageProperty> remaining = container.WzProperties.ToList();
            Dictionary<string, string> renames = WorldMapMapNoIndexer.Renumber(
                remaining.Select(p => p.Name).ToList());
            if (renames.Count == 0)
                return;

            foreach (WzImageProperty property in remaining)
            {
                if (!renames.TryGetValue(property.Name, out string newName))
                    continue;

                // ChangeName keeps the tree label and the WZ name in step and flags the node as
                // edited; renaming without it would leave the tree showing the old index.
                if (property.HRTag is WzNode node)
                    node.ChangeName(newName);
                else
                    property.Name = newName;
            }
        }

        /// <summary>
        /// Re-reads the image after a structural change and puts the selection back on the same
        /// entries, so adding or deleting a mapNo does not dump the user out of their selection.
        /// </summary>
        private void ReloadPreservingSelection()
        {
            if (document?.Image == null)
                return;

            var selectedEntryNames = selectedItems
                .Select(item => item is WorldMapSpot spot ? "S:" + spot.EntryName
                              : item is WorldMapLink link ? "L:" + link.EntryName : null)
                .Where(key => key != null)
                .ToHashSet(StringComparer.Ordinal);
            string primaryKey = primarySelected is WorldMapSpot p ? "S:" + p.EntryName
                              : primarySelected is WorldMapLink l ? "L:" + l.EntryName : null;

            document = WorldMapDocument.Load(document.Image);
            RenderMarkers();
            UpdateCanvasBounds();

            selectedItems.Clear();
            primarySelected = null;
            foreach (IWorldMapMovable item in markers.Keys)
            {
                string key = item is WorldMapSpot spot ? "S:" + spot.EntryName : "L:" + ((WorldMapLink)item).EntryName;
                if (!selectedEntryNames.Contains(key))
                    continue;
                selectedItems.Add(item);
                if (key == primaryKey)
                    primarySelected = item;
            }
            primarySelected ??= selectedItems.FirstOrDefault();

            RefreshSelectionVisuals();
            PopulateInspector();
        }

        private void InvalidateMapNumberCache()
        {
            if (document?.ImageName != null)
                mapNumberCache.Remove(document.ImageName);
        }

        // ---- type / coordinates -------------------------------------------------------------------

        private void TypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isPopulatingInspector || primarySelected is not WorldMapSpot spot || spot.Type == null)
                return;
            if (selectedItems.Count != 1)
                return;
            if (typeBox.SelectedItem is not int value || value == spot.Type.Value)
                return;

            spot.Type.Value = value;
            MarkChanged(spot.Type);

            if (markers.TryGetValue(spot, out Shape marker))
                marker.Fill = BrushForType(value);

            statusText.Text = spot.DisplayName + " 的 type 已更新為 " + value + "。";
        }

        private void SpotCoordinateBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;
            CommitCoordinates();
            e.Handled = true;
        }

        private void SpotCoordinateBox_LostFocus(object sender, RoutedEventArgs e) => CommitCoordinates();

        private void CommitCoordinates()
        {
            if (isPopulatingInspector || primarySelected == null || selectedItems.Count != 1 || isDraggingItems)
                return;

            IWorldMapMovable item = primarySelected;

            if (!int.TryParse(spotXBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                || !int.TryParse(spotYBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
            {
                // Put the shown values back rather than nagging with a dialog.
                (int X, int Y) shown = EffectivePosition(item);
                ShowCoordinates(shown.X, shown.Y);
                statusText.Text = "座標必須是整數";
                return;
            }

            // Typed coordinates are a preview too, exactly like a drag. An entry with no spot yet
            // always stages - confirming is what creates the spot.
            if (item.HasPosition && item.Position.X.Value == x && item.Position.Y.Value == y)
                pendingPositions.Remove(item);
            else
                pendingPositions.Stage(item, x, y);

            // Marker only - a link's artwork is placed by linkImg/origin, not by its spot.
            PositionMarker(item, x, y);
            UpdateCanvasBounds();
            RefreshPendingState();
            statusText.Text = HasPendingChanges
                ? "已預覽 " + item.DisplayName + " 座標 " + x + ", " + y + "，共 " + PendingChangeCount + " 項尚未確認。"
                : item.DisplayName + " 座標維持 " + x + ", " + y + "。";
        }

        private void OriginBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;
            CommitLinkImageOrigin();
            e.Handled = true;
        }

        private void OriginBox_LostFocus(object sender, RoutedEventArgs e) => CommitLinkImageOrigin();

        /// <summary>
        /// Types an origin straight in. Writes only linkImg's origin - the link's spot is not
        /// involved - and repositions the artwork immediately.
        /// </summary>
        private void CommitLinkImageOrigin()
        {
            if (isPopulatingInspector || isDraggingItems)
                return;
            if (primarySelected is not WorldMapLink link || selectedItems.Count != 1)
                return;

            WzVectorProperty origin = link.LinkImageOrigin;
            if (origin == null)
                return;

            if (!int.TryParse(originXBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                || !int.TryParse(originYBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
            {
                (int X, int Y) shown = EffectiveOrigin(link);
                ShowOrigin(shown.X, shown.Y);
                statusText.Text = "Origin 必須是整數";
                return;
            }

            // Preview, same as dragging the picture.
            if (origin.X.Value == x && origin.Y.Value == y)
                pendingOrigins.Remove(link);
            else
                pendingOrigins.Stage(link, x, y);

            PositionLinkImage(link);
            UpdateCanvasBounds();
            RefreshPendingState();
            statusText.Text = HasPendingChanges
                ? "已預覽 MapLink " + link.EntryName + " 的 linkImg origin（" + x + ", " + y + "），共 "
                  + PendingChangeCount + " 項尚未確認。"
                : "MapLink " + link.EntryName + " 的 linkImg origin 維持 " + x + ", " + y + "。";
        }

        // ---- confirm ------------------------------------------------------------------------------

        /// <summary>
        /// The only place a previewed position reaches the WZ. Writes every pending spot and
        /// linkImg origin, dirties their images, and reports all of them in a single
        /// PropertiesChanged so the tree reddens exactly the leaves that were written.
        ///
        /// A pending item that cannot be written - a read-only UOL linkImg - keeps its preview and
        /// leaves the button enabled; the ones that succeeded are still committed rather than
        /// rolled back.
        /// </summary>
        private void ConfirmPendingChanges()
        {
            if (document == null || !HasPendingChanges)
                return;

            // Counted separately from `written`: a freshly created origin is already correct, so
            // Apply writes nothing for it - but it is still a position the user successfully
            // changed and must not be reported as "0 applied".
            int committedCount = 0;
            var written = new List<WzImageProperty>();
            var committedItems = new List<IWorldMapMovable>();
            var committedLinks = new List<WorldMapLink>();
            int failed = 0;

            // A created spot / origin is a new property the loaded document does not know about,
            // so the document is re-read afterwards - otherwise the entry would keep drawing as a
            // placeholder even though it now has a real spot.
            bool createdProperties = false;

            foreach (KeyValuePair<IWorldMapMovable, (int X, int Y)> entry in pendingPositions.Entries.ToList())
            {
                IWorldMapMovable item = entry.Key;

                if (!item.HasPosition)
                {
                    // No spot yet: create one holding where the user dropped it. Only here, on the
                    // explicit confirm - dragging alone never adds anything to the WZ.
                    WzVectorProperty created = TryCreateSpot(item, entry.Value.X, entry.Value.Y);
                    if (created == null)
                    {
                        failed++;
                        continue;
                    }

                    createdProperties = true;
                    committedCount++;
                    // Only reddenable once the tree has a node for it; the WZ edit stands regardless.
                    if (created.HRTag is WzNode)
                        written.Add(created);
                }
                else if (WorldMapPositionCommit.Apply(item.Position, entry.Value.X, entry.Value.Y))
                {
                    written.Add(item.Position);
                    committedCount++;
                }

                committedItems.Add(item);
            }

            foreach (KeyValuePair<WorldMapLink, (int X, int Y)> entry in pendingOrigins.Entries.ToList())
            {
                WorldMapLink link = entry.Key;
                WzVectorProperty origin = link.LinkImageOrigin;

                if (origin == null)
                {
                    // Creating it is fair now - this is the explicit confirm, not a browse - and
                    // it is born holding the confirmed values.
                    origin = TryCreateLinkImageOrigin(link, entry.Value.X, entry.Value.Y);
                    if (origin == null)
                    {
                        failed++;
                        continue;
                    }

                    createdProperties = true;
                    committedCount++;
                    // Only reddenable once the tree has a node for it; the WZ edit stands either way.
                    if (origin.HRTag is WzNode)
                        written.Add(origin);
                }
                else if (WorldMapPositionCommit.Apply(origin, entry.Value.X, entry.Value.Y))
                {
                    written.Add(origin);
                    committedCount++;
                }

                committedLinks.Add(link);
            }

            // The committed values are now the WZ values, so simply forgetting these previews
            // makes them the new baseline.
            foreach (IWorldMapMovable item in committedItems)
                pendingPositions.Remove(item);
            foreach (WorldMapLink link in committedLinks)
                pendingOrigins.Remove(link);

            if (written.Count > 0)
                PropertiesChanged?.Invoke(this, written);

            // Re-read so newly created spots / origins are part of the document; the selection is
            // put back on the same entries.
            if (createdProperties)
                ReloadPreservingSelection();

            RefreshPendingState();
            PopulateInspector();

            statusText.Text = failed > 0
                ? "已套用 " + committedCount + " 個位置修改，" + failed + " 個無法建立，未套用。"
                : "已套用 " + committedCount + " 個位置修改。";
        }

        private void ShowOrigin(int x, int y)
        {
            isPopulatingInspector = true;
            try
            {
                originXBox.Text = x.ToString(CultureInfo.InvariantCulture);
                originYBox.Text = y.ToString(CultureInfo.InvariantCulture);
            }
            finally
            {
                isPopulatingInspector = false;
            }
        }

        /// <summary>
        /// Dirties the owning .img so it saves, and tells MainPanel which leaf to redden. The
        /// property passed in is the one the tree shows - a spot is a single WzVectorProperty
        /// node, so that whole node reddens rather than its X/Y.
        /// </summary>
        private void MarkChanged(WzImageProperty property)
        {
            if (property?.ParentImage != null)
                property.ParentImage.Changed = true;

            PropertiesChanged?.Invoke(this, new[] { property });
        }

        // ---- navigation ---------------------------------------------------------------------------

        private void PreviousImageButton_Click(object sender, RoutedEventArgs e)
        {
            string parent = WorldMapNavigation.NormalizeImageName(document?.ParentMap);
            if (parent == null)
            {
                statusText.Text = "這張世界地圖沒有 info/parentMap，無法回上一層。";
                return;
            }

            DiscardPendingPositions(); // unconfirmed previews do not travel with the user
            NavigationRequested?.Invoke(this, parent);
        }

        private void NavigateFrom(IWorldMapMovable item)
        {
            if (document == null)
                return;

            // Leaving this map without confirming means the previews are abandoned - navigating
            // must never quietly write them.
            DiscardPendingPositions();

            // A MapLink states its destination outright, so no guessing is needed.
            if (item is WorldMapLink link)
            {
                string linkTarget = WorldMapNavigation.NormalizeImageName(link.LinkMap);
                if (linkTarget == null)
                {
                    statusText.Text = "這個 MapLink 沒有 link/linkMap，無法判斷目標。";
                    return;
                }
                NavigationRequested?.Invoke(this, linkTarget);
                return;
            }

            if (item is WorldMapSpot spot)
                NavigateFromSpot(spot);
        }

        /// <summary>
        /// Double-clicking a spot follows it into the WorldMap that covers those maps, matched on
        /// shared mapNo values rather than guessed from the id. Uses only the double-clicked
        /// spot's own mapNo, never the whole selection's.
        /// </summary>
        private void NavigateFromSpot(WorldMapSpot spot)
        {
            var clicked = new List<int>();
            foreach (WzIntProperty mapNo in spot.MapNo)
                clicked.Add(mapNo.Value);

            if (clicked.Count == 0)
            {
                statusText.Text = "這個 Spot 沒有 mapNo，無法判斷對應的系列 WorldMap。";
                return;
            }

            IReadOnlyDictionary<string, HashSet<int>> candidates = BuildCandidateMapNumbers();
            string target = WorldMapNavigation.ResolveForwardTarget(
                clicked, candidates, document.ImageName, out bool ambiguous);

            if (ambiguous)
            {
                statusText.Text = "找到多個可能的 WorldMap，未自動跳轉";
                return;
            }
            if (target == null)
            {
                statusText.Text = "找不到對應的系列 WorldMap";
                return;
            }

            NavigationRequested?.Invoke(this, target);
        }

        private IReadOnlyDictionary<string, HashSet<int>> BuildCandidateMapNumbers()
        {
            IReadOnlyList<WzImage> siblings = WorldMapSiblingProvider?.Invoke();
            if (siblings == null)
                return mapNumberCache;

            foreach (WzImage sibling in siblings)
            {
                if (sibling?.Name == null || mapNumberCache.ContainsKey(sibling.Name))
                    continue;

                try
                {
                    if (!sibling.Parsed)
                        sibling.ParseImage();

                    mapNumberCache[sibling.Name] = WorldMapDocument.Load(sibling).CollectMapNumbers();
                }
                catch (Exception)
                {
                    // One unreadable sibling must not block navigation through the rest; cache an
                    // empty set so it is not retried on every double-click.
                    mapNumberCache[sibling.Name] = new HashSet<int>();
                }
            }

            return mapNumberCache;
        }

        // ---- layout ---------------------------------------------------------------------------

        private void BuildLayout()
        {
            var root = new DockPanel();

            root.Children.Add(BuildToolbar());
            root.Children.Add(BuildStatusBar());
            root.Children.Add(BuildInspector());
            root.Children.Add(BuildViewport());

            Content = root;
        }

        private UIElement BuildToolbar()
        {
            var bar = new DockPanel { Margin = new Thickness(8.0, 6.0, 8.0, 6.0) };

            previousImageButton = PanelButton("上一張圖");
            previousImageButton.Click += PreviousImageButton_Click;
            previousImageButton.IsEnabled = false;
            DockPanel.SetDock(previousImageButton, Dock.Left);
            bar.Children.Add(previousImageButton);

            confirmButton = PanelButton("確認修改");
            confirmButton.IsEnabled = false;
            confirmButton.Click += delegate { ConfirmPendingChanges(); };
            DockPanel.SetDock(confirmButton, Dock.Right);
            bar.Children.Add(confirmButton);

            Button resetButton = PanelButton("重設視圖");
            resetButton.Margin = new Thickness(0.0, 0.0, 8.0, 0.0);
            resetButton.Click += delegate { ResetView(); };
            DockPanel.SetDock(resetButton, Dock.Right);
            bar.Children.Add(resetButton);

            Button clearSelectionButton = PanelButton("取消選取");
            clearSelectionButton.Margin = new Thickness(0.0, 0.0, 8.0, 0.0);
            clearSelectionButton.Click += delegate { ClearSelection(); };
            DockPanel.SetDock(clearSelectionButton, Dock.Right);
            bar.Children.Add(clearSelectionButton);

            Button selectAllButton = PanelButton("全選 Spot");
            selectAllButton.Margin = new Thickness(0.0, 0.0, 8.0, 0.0);
            selectAllButton.Click += delegate { SelectAllSpots(); };
            DockPanel.SetDock(selectAllButton, Dock.Right);
            bar.Children.Add(selectAllButton);

            toggleLinkImagesButton = PanelButton(WorldMapLinkImageVisibility.ButtonText(linkImagesVisible));
            toggleLinkImagesButton.Margin = new Thickness(0.0, 0.0, 8.0, 0.0);
            toggleLinkImagesButton.Click += delegate { ToggleLinkImages(); };
            DockPanel.SetDock(toggleLinkImagesButton, Dock.Right);
            bar.Children.Add(toggleLinkImagesButton);

            zoomText = new TextBlock
            {
                Text = "Zoom: 100%",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0.0, 0.0, 12.0, 0.0)
            };
            DockPanel.SetDock(zoomText, Dock.Right);
            bar.Children.Add(zoomText);

            imageNameText = new TextBlock
            {
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12.0, 0.0, 12.0, 0.0)
            };
            bar.Children.Add(imageNameText);

            var border = new Border
            {
                Child = bar,
                BorderThickness = new Thickness(0.0, 0.0, 0.0, 1.0)
            };
            border.SetResourceReference(Border.BackgroundProperty, "HareSurfaceBrush");
            border.SetResourceReference(Border.BorderBrushProperty, "HareBorderBrush");
            DockPanel.SetDock(border, Dock.Top);
            return border;
        }

        private UIElement BuildStatusBar()
        {
            statusText = new TextBlock
            {
                Margin = new Thickness(10.0, 5.0, 10.0, 5.0),
                TextWrapping = TextWrapping.Wrap
            };
            statusText.SetResourceReference(StyleProperty, "HareMutedTextStyle");
            DockPanel.SetDock(statusText, Dock.Bottom);
            return statusText;
        }

        private UIElement BuildInspector()
        {
            inspector = new StackPanel { Margin = new Thickness(10.0) };

            inspectorTitle = new TextBlock
            {
                Text = "未選取",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0.0, 0.0, 0.0, 10.0)
            };
            inspector.Children.Add(inspectorTitle);

            inspector.Children.Add(FieldLabel("type"));
            typeBox = new ComboBox { Height = 26.0, IsEditable = false, Margin = new Thickness(0.0, 0.0, 0.0, 10.0) };
            typeBox.SelectionChanged += TypeBox_SelectionChanged;
            inspector.Children.Add(typeBox);

            // "Spot" rather than plain X/Y: a MapLink also has a linkImg origin below, and the two
            // must not be mistaken for each other.
            inspector.Children.Add(FieldLabel("Spot X"));
            spotXBox = CoordinateBox();
            inspector.Children.Add(spotXBox);

            inspector.Children.Add(FieldLabel("Spot Y"));
            spotYBox = CoordinateBox();
            inspector.Children.Add(spotYBox);

            linkOriginSection = new StackPanel { Visibility = Visibility.Collapsed };
            linkOriginSection.Children.Add(FieldLabel("LinkImg Origin X"));
            originXBox = OriginBox();
            linkOriginSection.Children.Add(originXBox);
            linkOriginSection.Children.Add(FieldLabel("LinkImg Origin Y"));
            originYBox = OriginBox();
            linkOriginSection.Children.Add(originYBox);
            originMissingText = new TextBlock
            {
                Text = "尚無 origin（拖曳圖片即可建立）",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
                Margin = new Thickness(0.0, 0.0, 0.0, 10.0),
                Visibility = Visibility.Collapsed
            };
            linkOriginSection.Children.Add(originMissingText);
            inspector.Children.Add(linkOriginSection);

            linkDetails = new StackPanel { Visibility = Visibility.Collapsed };
            inspector.Children.Add(linkDetails);

            mapNoSection = new StackPanel { Visibility = Visibility.Collapsed };
            mapNoSection.Children.Add(FieldLabel("mapNo"));
            mapNoList = new StackPanel();
            mapNoSection.Children.Add(mapNoList);

            Button addMapNo = PanelButton("＋ 新增 mapNo");
            addMapNo.HorizontalAlignment = HorizontalAlignment.Stretch;
            addMapNo.Margin = new Thickness(0.0, 4.0, 0.0, 0.0);
            addMapNo.Click += delegate
            {
                if (primarySelected is WorldMapSpot spot && selectedItems.Count == 1)
                    AddMapNo(spot);
            };
            mapNoSection.Children.Add(addMapNo);
            inspector.Children.Add(mapNoSection);

            var scroller = new ScrollViewer
            {
                Content = inspector,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Width = InspectorWidth
            };

            var border = new Border
            {
                Child = scroller,
                BorderThickness = new Thickness(1.0, 0.0, 0.0, 0.0)
            };
            border.SetResourceReference(Border.BackgroundProperty, "HareSurfaceBrush");
            border.SetResourceReference(Border.BorderBrushProperty, "HareBorderBrush");
            DockPanel.SetDock(border, Dock.Right);
            return border;
        }

        private TextBox CoordinateBox()
        {
            var box = new TextBox
            {
                Height = 24.0,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0.0, 0.0, 0.0, 10.0),
                IsEnabled = false
            };
            box.KeyDown += SpotCoordinateBox_KeyDown;
            box.LostFocus += SpotCoordinateBox_LostFocus;
            return box;
        }

        private TextBox OriginBox()
        {
            var box = new TextBox
            {
                Height = 24.0,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0.0, 0.0, 0.0, 10.0),
                IsEnabled = false
            };
            box.KeyDown += OriginBox_KeyDown;
            box.LostFocus += OriginBox_LostFocus;
            return box;
        }

        private static TextBlock FieldLabel(string text)
        {
            return new TextBlock { Text = text, Margin = new Thickness(0.0, 0.0, 0.0, 3.0), Opacity = 0.75 };
        }

        private Button PanelButton(string content)
        {
            var button = new Button
            {
                Content = content,
                MinWidth = 96.0,
                Height = 28.0,
                Padding = new Thickness(12.0, 3.0, 12.0, 3.0)
            };
            button.SetResourceReference(StyleProperty, "HareButtonStyle");
            return button;
        }

        private UIElement BuildViewport()
        {
            baseImage = new Image
            {
                Stretch = Stretch.None,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(baseImage, BitmapScalingMode.NearestNeighbor);

            worldCanvas = new Canvas
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            worldCanvas.Children.Add(baseImage);

            // One transform group drives the whole world layer, so the artwork and every marker
            // always pan and zoom together.
            var transforms = new TransformGroup();
            transforms.Children.Add(zoomTransform);
            transforms.Children.Add(panTransform);
            worldCanvas.RenderTransform = transforms;

            viewport = new Grid
            {
                ClipToBounds = true,
                // A transparent background still receives mouse input, which is what lets an
                // empty area start a pan.
                Background = Brushes.Transparent,
                Focusable = true
            };
            viewport.Children.Add(worldCanvas);

            viewport.MouseWheel += Viewport_MouseWheel;
            viewport.MouseLeftButtonDown += Viewport_MouseLeftButtonDown;
            viewport.MouseMove += Viewport_MouseMove;
            viewport.MouseLeftButtonUp += Viewport_MouseLeftButtonUp;
            viewport.KeyDown += Viewport_KeyDown;

            return viewport;
        }
    }
}
