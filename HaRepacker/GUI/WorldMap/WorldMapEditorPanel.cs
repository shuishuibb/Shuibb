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
    /// Visual editor for a WorldMap*.img: the BaseImg artwork with every MapList spot drawn on
    /// top, pan/zoom, and an inspector for the selected spot's type / X / Y / mapNo.
    ///
    /// Parked in MainPanel's grid1 next to the skill preview and node editor and shown by
    /// MainPanel.ShowWorldMapEditorIfApplicable, exactly like those two - see there for why it
    /// takes priority over them.
    ///
    /// Built in code rather than XAML to match SkillPreview's panels, which are the closest
    /// precedent for a lazily-created editor dropped into that container.
    ///
    /// Nothing here writes to the WZ as a side effect of looking: pan, zoom and selecting a spot
    /// never touch a property or set ParentImage.Changed. Only an actual edit does - a finished
    /// spot drag, a type change, an X/Y entry, a mapNo entry - and each reports the leaf
    /// properties it wrote through <see cref="PropertiesChanged"/> so MainPanel can redden
    /// exactly those tree nodes.
    /// </summary>
    public sealed class WorldMapEditorPanel : UserControl
    {
        private const double MinZoom = 0.25;
        private const double MaxZoom = 5.0;
        private const double ZoomStep = 1.1;
        private const double SpotRadius = 5.0;
        private const double InspectorWidth = 260.0;

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
        private StackPanel mapNoList;

        // ---- state -----------------------------------------------------------------------------

        private WorldMapDocument document;
        private readonly Dictionary<WorldMapSpot, Ellipse> spotMarkers = new Dictionary<WorldMapSpot, Ellipse>();
        private WorldMapSpot selectedSpot;

        private bool isPanning;
        private Point panStartPointer;
        private double panStartX;
        private double panStartY;

        private WorldMapSpot draggingSpot;
        private Point dragStartPointerOnCanvas;
        private int dragStartSpotX;
        private int dragStartSpotY;

        /// <summary>Suppresses the inspector's commit handlers while it is being populated.</summary>
        private bool isPopulatingInspector;

        /// <summary>
        /// Raised with the leaf properties an edit actually wrote. MainPanel turns each into
        /// WzNode.ChangedNodeProperty() so only those leaves go red - never their parents.
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
        /// Accepts a WorldMap*.img sitting under a WorldMap directory. Returns false for anything
        /// else, which is how MainPanel decides whether to show this panel at all.
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

            document = WorldMapDocument.Load(image);
            selectedSpot = null;

            imageNameText.Text = document.ImageName ?? string.Empty;
            previousImageButton.IsEnabled = WorldMapNavigation.NormalizeImageName(document.ParentMap) != null;

            RenderBase();
            RenderSpots();
            ResetView();
            PopulateInspector();

            statusText.Text = document.Warning
                ?? (document.Spots.Count + " 個 Spot　·　拖曳 Spot 修改座標，雙擊 Spot 跳到系列地圖");
            return true;
        }

        public void Clear()
        {
            document = null;
            selectedSpot = null;
            spotMarkers.Clear();
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
            if (document.BaseCanvas == null)
                return;

            try
            {
                // Decoding a linked canvas can fail on a broken _inlink/_outlink; the spots are
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

        private void RenderSpots()
        {
            foreach (Ellipse marker in spotMarkers.Values)
                worldCanvas.Children.Remove(marker);
            spotMarkers.Clear();

            foreach (WorldMapSpot spot in document.Spots)
            {
                Ellipse marker = CreateSpotMarker(spot);
                spotMarkers[spot] = marker;
                worldCanvas.Children.Add(marker);
                PositionMarker(spot, spot.SpotX, spot.SpotY);
            }
        }

        private Ellipse CreateSpotMarker(WorldMapSpot spot)
        {
            var marker = new Ellipse
            {
                Width = SpotRadius * 2.0,
                Height = SpotRadius * 2.0,
                Fill = BrushForType(spot.Type?.Value),
                Stroke = Brushes.Black,
                StrokeThickness = 1.0,
                Cursor = Cursors.Hand,
                ToolTip = BuildSpotTooltip(spot),
                Tag = spot
            };

            marker.MouseLeftButtonDown += SpotMarker_MouseLeftButtonDown;
            return marker;
        }

        private static string BuildSpotTooltip(WorldMapSpot spot)
        {
            string type = spot.Type == null ? "-" : spot.Type.Value.ToString(CultureInfo.InvariantCulture);
            string firstMapNo = spot.MapNo.Count > 0
                ? spot.MapNo[0].Value.ToString(CultureInfo.InvariantCulture)
                : "-";
            return "MapList " + spot.EntryName + "\nType " + type + "\n" + firstMapNo;
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

        private void PositionMarker(WorldMapSpot spot, int spotX, int spotY)
        {
            if (!spotMarkers.TryGetValue(spot, out Ellipse marker))
                return;

            (double x, double y) = WorldMapCoordinateConverter.WorldToCanvas(document.BaseOrigin, spotX, spotY);
            Canvas.SetLeft(marker, x - SpotRadius);
            Canvas.SetTop(marker, y - SpotRadius);
        }

        // ---- view ------------------------------------------------------------------------------

        private void ResetView()
        {
            zoomTransform.ScaleX = 1.0;
            zoomTransform.ScaleY = 1.0;
            panTransform.X = 0.0;
            panTransform.Y = 0.0;
            UpdateZoomText();
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

            // A spot handled the click already (it sets Handled), so reaching here means empty
            // background: start panning rather than moving anything.
            isPanning = true;
            panStartPointer = e.GetPosition(viewport);
            panStartX = panTransform.X;
            panStartY = panTransform.Y;
            viewport.CaptureMouse();
        }

        private void Viewport_MouseMove(object sender, MouseEventArgs e)
        {
            if (draggingSpot != null)
            {
                DragSpotTo(e.GetPosition(worldCanvas));
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
            if (draggingSpot != null)
            {
                CommitSpotDrag();
            }

            isPanning = false;
            if (viewport.IsMouseCaptured)
                viewport.ReleaseMouseCapture();
        }

        // ---- spot interaction -------------------------------------------------------------------

        private void SpotMarker_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Ellipse marker || marker.Tag is not WorldMapSpot spot)
                return;

            // Handled so the viewport does not also start a pan - dragging a spot must move the
            // spot, not the map.
            e.Handled = true;

            if (e.ClickCount >= 2)
            {
                NavigateFromSpot(spot);
                return;
            }

            SelectSpot(spot);

            draggingSpot = spot;
            dragStartPointerOnCanvas = e.GetPosition(worldCanvas);
            dragStartSpotX = spot.SpotX;
            dragStartSpotY = spot.SpotY;
            viewport.CaptureMouse();
        }

        private void SelectSpot(WorldMapSpot spot)
        {
            // Selecting only changes this panel - the tree's selection is left alone so the user
            // does not lose their place, and nothing is written to the WZ.
            if (selectedSpot != null && spotMarkers.TryGetValue(selectedSpot, out Ellipse previous))
            {
                previous.Stroke = Brushes.Black;
                previous.StrokeThickness = 1.0;
            }

            selectedSpot = spot;

            if (spot != null && spotMarkers.TryGetValue(spot, out Ellipse marker))
            {
                marker.Stroke = Brushes.White;
                marker.StrokeThickness = 3.0;
            }

            PopulateInspector();
        }

        /// <summary>
        /// Live feedback only - the marker and the X/Y boxes follow the pointer, but nothing is
        /// written until the button comes up. Marking the image dirty on every mouse move would
        /// flag the WZ as changed for a drag the user then abandons.
        /// </summary>
        private void DragSpotTo(Point pointerOnCanvas)
        {
            double deltaX = pointerOnCanvas.X - dragStartPointerOnCanvas.X;
            double deltaY = pointerOnCanvas.Y - dragStartPointerOnCanvas.Y;

            (double canvasX, double canvasY) = WorldMapCoordinateConverter.WorldToCanvas(
                document.BaseOrigin, dragStartSpotX, dragStartSpotY);
            (int worldX, int worldY) = WorldMapCoordinateConverter.CanvasToWorld(
                document.BaseOrigin, canvasX + deltaX, canvasY + deltaY);

            PositionMarker(draggingSpot, worldX, worldY);

            isPopulatingInspector = true;
            try
            {
                spotXBox.Text = worldX.ToString(CultureInfo.InvariantCulture);
                spotYBox.Text = worldY.ToString(CultureInfo.InvariantCulture);
            }
            finally
            {
                isPopulatingInspector = false;
            }
        }

        private void CommitSpotDrag()
        {
            WorldMapSpot spot = draggingSpot;
            draggingSpot = null;

            if (!int.TryParse(spotXBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int newX)
                || !int.TryParse(spotYBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int newY))
            {
                PositionMarker(spot, spot.SpotX, spot.SpotY);
                PopulateInspector();
                return;
            }

            ApplySpotPosition(spot, newX, newY);
        }

        /// <summary>
        /// The single place a spot's coordinates reach the WZ - used by both the drag and the X/Y
        /// boxes. A no-op move writes nothing, so nudging a spot back to where it started does not
        /// leave the file dirty.
        /// </summary>
        private void ApplySpotPosition(WorldMapSpot spot, int newX, int newY)
        {
            if (spot == null)
                return;

            if (spot.SpotX == newX && spot.SpotY == newY)
            {
                PositionMarker(spot, newX, newY);
                return;
            }

            spot.Spot.X.Value = newX;
            spot.Spot.Y.Value = newY;
            MarkChanged(spot.Spot);

            PositionMarker(spot, newX, newY);
            statusText.Text = "MapList " + spot.EntryName + " 座標已更新為 " + newX + ", " + newY + "。";
        }

        // ---- inspector ---------------------------------------------------------------------------

        private void PopulateInspector()
        {
            isPopulatingInspector = true;
            try
            {
                mapNoList.Children.Clear();

                if (selectedSpot == null)
                {
                    inspectorTitle.Text = "未選取 Spot";
                    typeBox.ItemsSource = null;
                    typeBox.IsEnabled = false;
                    spotXBox.Text = string.Empty;
                    spotYBox.Text = string.Empty;
                    spotXBox.IsEnabled = false;
                    spotYBox.IsEnabled = false;
                    return;
                }

                inspectorTitle.Text = "MapList " + selectedSpot.EntryName;

                // Offer the usual values, plus whatever this spot actually has - an unknown type
                // must stay selectable and must never be silently rewritten to 0.
                var typeOptions = new List<int> { 0, 1, 2, 3 };
                if (selectedSpot.Type != null && !typeOptions.Contains(selectedSpot.Type.Value))
                    typeOptions.Add(selectedSpot.Type.Value);
                typeOptions.Sort();

                typeBox.ItemsSource = typeOptions;
                typeBox.IsEnabled = selectedSpot.Type != null;
                typeBox.SelectedItem = selectedSpot.Type?.Value;
                if (selectedSpot.Type == null)
                    typeBox.Text = "-";

                spotXBox.IsEnabled = true;
                spotYBox.IsEnabled = true;
                spotXBox.Text = selectedSpot.SpotX.ToString(CultureInfo.InvariantCulture);
                spotYBox.Text = selectedSpot.SpotY.ToString(CultureInfo.InvariantCulture);

                foreach (WzIntProperty mapNo in selectedSpot.MapNo)
                    mapNoList.Children.Add(BuildMapNoRow(mapNo));
            }
            finally
            {
                isPopulatingInspector = false;
            }
        }

        private UIElement BuildMapNoRow(WzIntProperty mapNo)
        {
            var row = new DockPanel { Margin = new Thickness(0.0, 0.0, 0.0, 4.0) };

            row.Children.Add(new TextBlock
            {
                Text = mapNo.Name,
                Width = 28.0,
                VerticalAlignment = VerticalAlignment.Center
            });

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

            // This image's map ids just changed, so the cached set used for spot navigation is
            // stale for it (and only for it).
            if (document?.ImageName != null)
                mapNumberCache.Remove(document.ImageName);

            statusText.Text = "mapNo " + mapNo.Name + " 已更新為 " + value + "。";
        }

        private void TypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isPopulatingInspector || selectedSpot?.Type == null)
                return;
            if (typeBox.SelectedItem is not int value || value == selectedSpot.Type.Value)
                return;

            selectedSpot.Type.Value = value;
            MarkChanged(selectedSpot.Type);

            if (spotMarkers.TryGetValue(selectedSpot, out Ellipse marker))
                marker.Fill = BrushForType(value);

            statusText.Text = "MapList " + selectedSpot.EntryName + " 的 type 已更新為 " + value + "。";
        }

        private void SpotCoordinateBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;
            CommitSpotCoordinates();
            e.Handled = true;
        }

        private void SpotCoordinateBox_LostFocus(object sender, RoutedEventArgs e) => CommitSpotCoordinates();

        private void CommitSpotCoordinates()
        {
            if (isPopulatingInspector || selectedSpot == null || draggingSpot != null)
                return;

            if (!int.TryParse(spotXBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                || !int.TryParse(spotYBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
            {
                // Put the real values back rather than nagging with a dialog.
                isPopulatingInspector = true;
                try
                {
                    spotXBox.Text = selectedSpot.SpotX.ToString(CultureInfo.InvariantCulture);
                    spotYBox.Text = selectedSpot.SpotY.ToString(CultureInfo.InvariantCulture);
                }
                finally
                {
                    isPopulatingInspector = false;
                }
                statusText.Text = "座標必須是整數";
                return;
            }

            ApplySpotPosition(selectedSpot, x, y);
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
            NavigationRequested?.Invoke(this, parent);
        }

        /// <summary>
        /// Double-clicking a spot follows it into the WorldMap that covers those maps, matched on
        /// shared mapNo values rather than guessed from the id - see
        /// WorldMapNavigation.ResolveForwardTarget.
        /// </summary>
        private void NavigateFromSpot(WorldMapSpot spot)
        {
            if (document == null)
                return;

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

            Button resetButton = PanelButton("重設視圖");
            resetButton.Click += delegate { ResetView(); };
            DockPanel.SetDock(resetButton, Dock.Right);
            bar.Children.Add(resetButton);

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
                Text = "未選取 Spot",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0.0, 0.0, 0.0, 10.0)
            };
            inspector.Children.Add(inspectorTitle);

            inspector.Children.Add(FieldLabel("type"));
            typeBox = new ComboBox { Height = 26.0, IsEditable = false, Margin = new Thickness(0.0, 0.0, 0.0, 10.0) };
            typeBox.SelectionChanged += TypeBox_SelectionChanged;
            inspector.Children.Add(typeBox);

            inspector.Children.Add(FieldLabel("X"));
            spotXBox = CoordinateBox();
            inspector.Children.Add(spotXBox);

            inspector.Children.Add(FieldLabel("Y"));
            spotYBox = CoordinateBox();
            inspector.Children.Add(spotYBox);

            inspector.Children.Add(FieldLabel("mapNo"));
            mapNoList = new StackPanel();
            inspector.Children.Add(mapNoList);

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

            // One transform group drives the whole world layer, so the artwork and every spot
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
                Background = Brushes.Transparent
            };
            viewport.Children.Add(worldCanvas);

            viewport.MouseWheel += Viewport_MouseWheel;
            viewport.MouseLeftButtonDown += Viewport_MouseLeftButtonDown;
            viewport.MouseMove += Viewport_MouseMove;
            viewport.MouseLeftButtonUp += Viewport_MouseLeftButtonUp;

            return viewport;
        }
    }
}
