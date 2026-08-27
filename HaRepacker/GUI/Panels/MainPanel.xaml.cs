using HaRepacker.GUI.Input;
using HaSharedLibrary.GUI;
using MapleLib.WzLib;
using MapleLib.WzLib.Spine;
using MapleLib.WzLib.WzProperties;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using static MapleLib.Configuration.UserSettings;
using System.IO;
using HaRepacker.GUI.Panels.SubPanels;
using HaRepacker.GUI.Controls;
using MapleLib.WzLib.WzStructure.Data;
using System.ComponentModel.DataAnnotations;
using MapleLib.Img;
using HaSharedLibrary.Audio;

namespace HaRepacker.GUI.Panels
{
    /// <summary>
    /// Interaction logic for MainPanelXAML.xaml
    /// </summary>
    public partial class MainPanel : UserControl
    {
        public TreeViewMS DataTree { get; } = new TreeViewMS();
        // Constants
        private const string FIELD_LIMIT_OBJ_NAME = "fieldLimit";
        private const string FIELD_TYPE_OBJ_NAME = "fieldType";
        private const string PORTAL_NAME_OBJ_NAME = "pn";

        private readonly MainForm _mainForm;
        public MainForm MainForm
        {
            get { return _mainForm; }
            private set { }
        }

        // Data binding
        private MainPanelPropertyItems _bindingPropertyItem = new MainPanelPropertyItems();
        //private MainPanelPropertyItemInterface _bindingPropertyItemReadOnly = new MainPanelPropertyItems_ReadOnly();


        // Etc
        private readonly static List<WzObject> clipboard = new List<WzObject>();
        private readonly UndoRedoManager undoRedoMan;

        private bool isSelectingWzMapFieldLimit = false;
        private bool isLoading = false;
        private bool isSynchronizingNativeSelection = false;
        private readonly List<WzNode> nativeSelectedNodes = new List<WzNode>();
        private readonly Dictionary<WzNode, TreeViewItem> nativeTreeItems = new Dictionary<WzNode, TreeViewItem>();
        private WzNode nativeSelectionAnchor;

        // Type-ahead ("press A to jump to the next item starting with A") state for dataTreeView.
        // Deliberately left without field initializers (default '\0'/null and DateTime.MinValue are
        // treated as "no prior keystroke" below) so the constructor's IL is untouched by this feature.
        private string typeAheadBuffer;
        private DateTime typeAheadLastKeyTimeUtc;

        /// <summary>
        /// Constructor
        /// </summary>
        public MainPanel(MainForm mainForm)
        {
            InitializeComponent();

            DataTree.AfterSelect += DataTree_AfterSelect;
            DataTree.BeforeExpand += DataTree_BeforeExpand;
            DataTree.ModelChanged += (_, _) => RefreshNativeDataTree();

            isLoading = true;

            this._mainForm = mainForm;

            // Events
#if DEBUG
            toolStripStatusLabel_debugMode.Visibility = Visibility.Visible;
#else
            toolStripStatusLabel_debugMode.Visibility = Visibility.Collapsed;
#endif

            // undo redo
            undoRedoMan = new UndoRedoManager(this);

            // Set theme color
            if (Program.ConfigurationManager.UserSettings.ThemeColor == (int)UserSettingsThemeColor.Dark)
            {
                VisualStateManager.GoToState(this, "BlackTheme", false);
                DataTree.BackColor = System.Drawing.Color.Black;
                DataTree.ForeColor = System.Drawing.Color.White;
            }

            // data binding stuff. The left-hand xctk:PropertyGrid is gone; the same
            // MainPanelPropertyItems now drives the right pane's header (名稱 / 值 / X,Y), so
            // every edit still lands in propertyGrid_PropertyChanged_1 exactly as before.
            border_NodeHeader.DataContext = _bindingPropertyItem;
            _bindingPropertyItem.PropertyChanged += propertyGrid_PropertyChanged_1;
            _bindingPropertyItem.PropertyChanged += NodeHeader_BindingPropertyChanged;

            // Storyboard
            System.Windows.Media.Animation.Storyboard sbb = (System.Windows.Media.Animation.Storyboard)(this.FindResource("Storyboard_Find_FadeIn"));
            sbb.Completed += Storyboard_Find_FadeIn_Completed;


            // buttons
            menuItem_changeImage.Visibility = Visibility.Collapsed;
            menuItem_changeSound.Visibility = Visibility.Collapsed;
            menuItem_saveSound.Visibility = Visibility.Collapsed;
            menuItem_openAudioStudio.Visibility = Visibility.Collapsed;
            menuItem_applyAudioProject.Visibility = Visibility.Collapsed;
            menuItem_exportDecodedWav.Visibility = Visibility.Collapsed;
            menuItem_audioMetadata.Visibility = Visibility.Collapsed;
            menuItem_saveImage.Visibility = Visibility.Collapsed;
            button_MoreOption.Content = UiLocalization.Translate("More actions");
            button_MoreOption.ToolTip = UiLocalization.Translate("More actions");
            menuItem_changeImage.Header = UiLocalization.Translate("Change Image");
            menuItem_changeSound.Header = UiLocalization.Translate("Change Sound");
            menuItem_saveSound.Header = UiLocalization.Translate("Save Sound");
            menuItem_saveImage.Header = UiLocalization.Translate("Save Image");
            menuItem_exportFile.Header = UiLocalization.Translate("Export file");

            textEditor.SaveButtonClicked += TextEditor_SaveButtonClicked;
            Loaded += MainPanelXAML_Loaded;


            isLoading = false;
        }

        private void MainPanelXAML_Loaded(object sender, RoutedEventArgs e)
        {
            this.fieldLimitPanel1.FieldLimitChanged += FieldLimitPanel1_FieldLimitChanged;
            RefreshNativeDataTree();
            //this.fieldTypePanel.SetTextboxOnFieldTypeChange(textPropBox);
        }

        #region Exported Fields
        public UndoRedoManager UndoRedoMan { get { return undoRedoMan; } }
        public bool IsTextEditorFocused => textEditor.IsKeyboardFocusWithin;

        #endregion

        #region Data Tree
        /// <summary>
        /// How many times this panel has actually rebuilt the WPF mirror of the tree. Pure
        /// instrumentation for the tree-batching regressions - a single user action that mutates
        /// hundreds of nodes must cost about one rebuild, not hundreds.
        /// </summary>
        public int NativeTreeRefreshCount { get; private set; }

        // ---- tree mutation batching ------------------------------------------------------------

        /// <summary>Depth of nested BeginNativeTreeUpdate scopes; refreshes coalesce while &gt; 0.</summary>
        private int nativeTreeUpdateDepth;
        /// <summary>A refresh was requested inside a batch; the outermost End runs it once.</summary>
        private bool nativeTreeRefreshPending;

        /// <summary>
        /// Starts a batch: every RefreshNativeDataTree issued until the matching
        /// EndNativeTreeUpdate collapses into a single rebuild at the end. Nests; UI thread only,
        /// like the refresh itself. Prefer the IDisposable form so an exception can never leave
        /// the depth stuck.
        /// </summary>
        public void BeginNativeTreeUpdate()
        {
            nativeTreeUpdateDepth++;
        }

        public void EndNativeTreeUpdate()
        {
            if (nativeTreeUpdateDepth == 0)
                return; // unbalanced End - swallow rather than go negative and eat future refreshes
            nativeTreeUpdateDepth--;
            if (nativeTreeUpdateDepth == 0 && nativeTreeRefreshPending)
            {
                nativeTreeRefreshPending = false;
                RefreshNativeDataTree();
            }
        }

        /// <summary>try/finally in a box: <c>using (panel.NativeTreeUpdateScope()) { ... }</c></summary>
        public IDisposable NativeTreeUpdateScope()
        {
            BeginNativeTreeUpdate();
            return new NativeTreeUpdateScopeToken(this);
        }

        private sealed class NativeTreeUpdateScopeToken : IDisposable
        {
            private MainPanel panel;
            public NativeTreeUpdateScopeToken(MainPanel panel) { this.panel = panel; }
            public void Dispose()
            {
                MainPanel p = System.Threading.Interlocked.Exchange(ref panel, null);
                p?.EndNativeTreeUpdate();
            }
        }

        public void RefreshNativeDataTree()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(RefreshNativeDataTree); return; }
            if (nativeTreeUpdateDepth > 0) { nativeTreeRefreshPending = true; return; }
            NativeTreeRefreshCount++;

            // Rebuilding clears the ScrollViewer, which used to snap the picture to the top and
            // then the reveal below dragged it to the selected node - so any mutation that went
            // through here yanked the tree away from where the user was actually looking.
            // Capture where the viewport is now, put it back after the rebuild, and leave
            // revealing to explicit navigation (search, type-ahead, WorldMap jumps), which calls
            // SelectAndRevealNativeNode itself.
            treeRevealGeneration++; // stale queued reveals/restores from earlier refreshes die
            MutationViewportSnapshot viewportSnapshot = CaptureMutationViewport();

            var expandedNodes = nativeTreeItems
                .Where(pair => pair.Value.IsExpanded)
                .Select(pair => pair.Key)
                .ToHashSet();
            WzNode activeNode = DataTree.SelectedNode as WzNode;

            nativeSelectedNodes.Clear();
            nativeSelectedNodes.AddRange(DataTree.SelectedNodes.Cast<WzNode>());
            if (nativeSelectedNodes.Count == 0 && activeNode != null)
                nativeSelectedNodes.Add(activeNode);

            nativeTreeItems.Clear();
            dataTreeView.Items.Clear();
            foreach (WzNode node in DataTree.Nodes) dataTreeView.Items.Add(CreateNativeTreeItem(node));
            foreach (WzNode node in DataTree.Nodes) RestoreNativeExpansion(node, expandedNodes);
            UpdateNativeSelectionVisuals();

            QueueMutationViewportRestore(viewportSnapshot);
        }

        // ---- mutation viewport preservation ------------------------------------------------------

        /// <summary>
        /// Where the tree's picture was just before a rebuild: the raw scroll offsets, plus (best
        /// effort) the node at the top of the viewport and its pixel distance from the viewport
        /// edge, so content shifting above the viewport can be corrected for. Short-lived and
        /// local to one refresh - deliberately separate from the per-tab saved viewport that tab
        /// switching uses.
        /// </summary>
        private readonly struct MutationViewportSnapshot
        {
            public MutationViewportSnapshot(bool hasViewer, double vertical, double horizontal, WzNode anchor, double anchorY)
            {
                HasViewer = hasViewer; VerticalOffset = vertical; HorizontalOffset = horizontal;
                AnchorNode = anchor; AnchorViewportY = anchorY;
            }
            public bool HasViewer { get; }
            public double VerticalOffset { get; }
            public double HorizontalOffset { get; }
            public WzNode AnchorNode { get; }
            public double AnchorViewportY { get; }
        }

        private MutationViewportSnapshot CaptureMutationViewport()
        {
            if (FindNativeTreeScrollViewer() is not ScrollViewer viewer)
                return default;

            WzNode anchor = null;
            double anchorY = 0;
            try
            {
                // The row under the top edge of the viewport is the user's visual anchor. One
                // probe is not enough: at the left margin the point falls in the transparent
                // indentation (no hit) or on an expanded ancestor whose subtree spans the whole
                // viewport (useless as an anchor - its own top is thousands of pixels up). Probe
                // rightward until a row whose own top is actually near the edge answers.
                foreach (double probeX in new[] { 8d, 28d, 48d, 72d, 110d, 160d, 220d })
                {
                    var hit = System.Windows.Media.VisualTreeHelper.HitTest(viewer, new System.Windows.Point(probeX, 9));
                    DependencyObject current = hit?.VisualHit;
                    while (current != null && current is not TreeViewItem)
                        current = System.Windows.Media.VisualTreeHelper.GetParent(current);
                    if (current is not TreeViewItem item || item.Tag is not WzNode node)
                        continue;
                    double y = item.TransformToAncestor(viewer).Transform(new System.Windows.Point()).Y;
                    if (y < -24)
                        continue; // a spanning ancestor, not the top row
                    anchor = node;
                    anchorY = y;
                    break;
                }
            }
            catch
            {
                // The anchor is an enhancement; the offsets below are the guarantee.
            }
            return new MutationViewportSnapshot(true, viewer.VerticalOffset, viewer.HorizontalOffset, anchor, anchorY);
        }

        private void QueueMutationViewportRestore(MutationViewportSnapshot snapshot)
        {
            if (!snapshot.HasViewer)
                return;

            int restoreGen = treeRevealGeneration;
            // Swallow the focus-restore RequestBringIntoView the rebuild can trigger, exactly
            // like the tab-switch restore does. Whichever restore/refresh owns the newest
            // generation is responsible for lifting the flag again.
            suppressTreeAutoReveal = true;

            // One pass is not enough: big expanded nodes refill in queued 200-item chunks, and
            // each append makes the VirtualizingStackPanel re-estimate positions and shove the
            // offset around (measured +3200px per chunk when clamped at the bottom, and a one-off
            // +1197px drift even when not). So the restore re-asserts itself after every pending
            // fill chunk - at Background priority the passes interleave FIFO with the fills - and
            // only releases once the tree is fully built and the offset has stuck.
            Action pass = null;
            pass = () =>
            {
                bool done = false;
                try
                {
                    if (restoreGen != treeRevealGeneration)
                    {
                        done = true;
                        return; // a newer refresh or a tab switch owns the viewport now
                    }
                    if (FindNativeTreeScrollViewer() is not ScrollViewer viewer)
                    {
                        done = true;
                        return;
                    }

                    // Scrolling to an offset beyond the currently-built extent would clamp to
                    // the bottom; materialize just enough chunks for the target to exist.
                    int fillGuard = 0;
                    while (HasPendingNativeTreeFills && fillGuard++ < 512)
                    {
                        viewer.UpdateLayout();
                        if (viewer.ExtentHeight >= snapshot.VerticalOffset + viewer.ViewportHeight)
                            break;
                        FillPendingNativeTreeItems();
                    }

                    viewer.ScrollToVerticalOffset(snapshot.VerticalOffset);
                    viewer.ScrollToHorizontalOffset(snapshot.HorizontalOffset);

                    // If the old top-visible row still exists, nudge so it sits at the same
                    // pixel it did before - keeps the picture stable when rows were added or
                    // removed above the viewport. A deleted anchor leaves the offset restore
                    // standing, and the extent change clamps naturally.
                    if (snapshot.AnchorNode != null
                        && nativeTreeItems.TryGetValue(snapshot.AnchorNode, out TreeViewItem anchorItem))
                    {
                        viewer.UpdateLayout(); // commit the offset write so the measure below is real
                        if (anchorItem.IsVisible)
                        {
                            double y = anchorItem.TransformToAncestor(viewer).Transform(new System.Windows.Point()).Y;
                            double delta = y - snapshot.AnchorViewportY;
                            if (Math.Abs(delta) > 0.5)
                                viewer.ScrollToVerticalOffset(Math.Max(0, viewer.VerticalOffset + delta));
                        }
                    }

                    if (!HasPendingNativeTreeFills)
                        done = true; // fully built; this offset is final
                }
                catch
                {
                    // Best effort; never let a viewport nicety become a crash.
                    done = true;
                }
                finally
                {
                    if (!done)
                    {
                        Dispatcher.BeginInvoke(pass, DispatcherPriority.Background);
                    }
                    else if (restoreGen == treeRevealGeneration)
                    {
                        suppressTreeAutoReveal = false;
                    }
                }
            };
            Dispatcher.BeginInvoke(pass, DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Invalidates this tab's in-flight background work (a running progressive search, the
        /// search session). Called before "close all" empties the tree, so late results land in
        /// a world that no longer accepts them.
        /// </summary>
        public void CancelBackgroundTreeWork()
        {
            searchRunGeneration++;
            linkRepairGeneration++; // a running link repair stops at its next batch boundary
            EndSearchSession();
        }

        /// <summary>
        /// After "close all files": nothing may keep pointing at the objects that were just
        /// disposed - not the editors, not the queued render, not the status type.
        /// </summary>
        public void ResetViewAfterCloseAll()
        {
            nativeSelectedNodes.Clear();
            nativeSelectionPainted.Clear();
            pendingObjectValue = null;
            coloredNode = null;

            _bindingPropertyItem.WzFileName = string.Empty;
            _bindingPropertyItem.WzFileValue = string.Empty;
            SetSelectedWzTypeName(string.Empty);

            canvasPropBox.Visibility = Visibility.Collapsed;
            mp3Player.Visibility = Visibility.Collapsed;
            textEditor.Visibility = Visibility.Collapsed;
            if (nodeEditorPanel != null)
                nodeEditorPanel.Visibility = Visibility.Collapsed;
            if (skillPreviewPanel != null)
            {
                skillPreviewPanel.Visibility = Visibility.Collapsed;
                skillPreviewPanel.StopPlayback();
            }
            if (worldMapEditorPanel != null)
            {
                worldMapEditorPanel.Clear();
                worldMapEditorPanel.Visibility = Visibility.Collapsed;
            }

            RefreshNativeDataTree();
        }

        // ---- per-tab tree viewport -------------------------------------------------------------

        private double savedTreeVerticalOffset;
        private double savedTreeHorizontalOffset;
        private bool hasSavedTreeViewport;

        /// <summary>
        /// True while a tab switch is putting the viewport back. WPF re-focuses the last focused
        /// TreeViewItem when the tab's content reattaches, and a focused element raises
        /// RequestBringIntoView - which would yank the tree back to the selected node even though
        /// the user had scrolled elsewhere. Explicit navigation (search, type-ahead, WorldMap
        /// jumps) runs under isNavigatingSearchResult and is never suppressed.
        /// </summary>
        private bool suppressTreeAutoReveal;

        /// <summary>Bumped when the viewport is restored; stale queued reveals check it and bail.</summary>
        private int treeRevealGeneration;

        /// <summary>Remembers where this tab's tree is scrolled, called when the tab is left.</summary>
        public void CaptureTreeViewport()
        {
            if (FindNativeTreeScrollViewer() is ScrollViewer viewer)
            {
                savedTreeVerticalOffset = viewer.VerticalOffset;
                savedTreeHorizontalOffset = viewer.HorizontalOffset;
                hasSavedTreeViewport = true;
            }
        }

        /// <summary>
        /// Puts this tab's tree back where the user left it. Selection is untouched - selection
        /// and viewport are deliberately independent: the selected node stays selected (and the
        /// editor keeps showing it) but the picture does not jump to it.
        /// </summary>
        public void RestoreTreeViewport()
        {
            treeRevealGeneration++;
            if (!hasSavedTreeViewport)
                return;

            suppressTreeAutoReveal = true;
            double vertical = savedTreeVerticalOffset;
            double horizontal = savedTreeHorizontalOffset;
            // ContextIdle: after layout has settled and after any focus-restore reveal has fired,
            // so this write is the one that wins.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (FindNativeTreeScrollViewer() is ScrollViewer viewer)
                    {
                        viewer.ScrollToVerticalOffset(vertical);
                        viewer.ScrollToHorizontalOffset(horizontal);
                    }
                }
                finally
                {
                    suppressTreeAutoReveal = false;
                }
            }), DispatcherPriority.ContextIdle);
        }

        private ScrollViewer FindNativeTreeScrollViewer()
        {
            return FindDescendantScrollViewer(dataTreeView);
        }

        private static ScrollViewer FindDescendantScrollViewer(DependencyObject root)
        {
            if (root == null)
                return null;
            if (root is ScrollViewer viewer)
                return viewer;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                if (FindDescendantScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(root, i)) is ScrollViewer found)
                    return found;
            }
            return null;
        }

        /// <summary>
        /// WPF's TreeView does not virtualise by default: expanding a node with 10,793 children
        /// (String.wz/Skill.img) makes it lay out and render every one of them - measured at
        /// ~3,965 ms, which is the stall users hit on a big IMG. Recycling containers and only
        /// realising what is on screen brings that to ~1,556 ms and roughly halves the cost of
        /// clicking around afterwards.
        /// Set from code rather than XAML so the compiled BAML is untouched, and guarded so the
        /// repeated calls from RefreshNativeDataTree do nothing.
        /// </summary>
        private void EnableNativeTreeVirtualization()
        {
            if (nativeTreeVirtualizationApplied || dataTreeView == null)
                return;

            nativeTreeVirtualizationApplied = true;

            // Swallows the automatic scroll-to-focused-item WPF performs when a tab's content
            // reattaches; explicit navigation runs under isNavigatingSearchResult and passes.
            dataTreeView.AddHandler(FrameworkElement.RequestBringIntoViewEvent,
                new RequestBringIntoViewEventHandler((s, e) =>
                {
                    if (suppressTreeAutoReveal && !isNavigatingSearchResult)
                        e.Handled = true;
                }));

            VirtualizingStackPanel.SetIsVirtualizing(dataTreeView, true);
            VirtualizingStackPanel.SetVirtualizationMode(dataTreeView, VirtualizationMode.Recycling);
            // The ScrollViewer has to scroll by item for the panel to virtualise at all; ScrollUnit
            // Pixel keeps the scrolling itself smooth. Measured on a 3,096-child node: clicking
            // around after an expand drops from ~42ms to ~3ms per click.
            ScrollViewer.SetCanContentScroll(dataTreeView, true);
            VirtualizingPanel.SetScrollUnit(dataTreeView, ScrollUnit.Pixel);
        }

        // No initialiser: it defaults to false, which keeps MainPanel's constructor untouched.
        private bool nativeTreeVirtualizationApplied;

        private void RestoreNativeExpansion(WzNode node, HashSet<WzNode> expandedNodes)
        {
            if (!expandedNodes.Contains(node) || !nativeTreeItems.TryGetValue(node, out TreeViewItem item))
                return;

            item.IsExpanded = true;
            foreach (WzNode child in node.Nodes.Cast<WzNode>().ToArray())
                RestoreNativeExpansion(child, expandedNodes);
        }

        private TreeViewItem CreateNativeTreeItem(WzNode node)
        {
            EnableNativeTreeVirtualization();
            // Tree stays plain English (node.Text as-is) - the Chinese display-name translation
            // only applies to SkillPreview\NodeEditorPanel.cs's property editor cards, per
            // explicit user preference.
            var item = new TreeViewItem { Header = node.Text, Tag = node };
            nativeTreeItems[node] = item;
            item.PreviewMouseLeftButtonDown += DataTreeViewItem_PreviewMouseLeftButtonDown;
            item.PreviewMouseRightButtonDown += DataTreeViewItem_PreviewMouseRightButtonDown;
            // A plain string, not another TreeViewItem: WPF only materialises a container for
            // it if the parent is ever expanded, which halves the object count for a node with
            // thousands of children.
            if (node.Nodes.Count > 0) item.Items.Add(UiLocalization.Translate("Loading…"));
            ApplyNativeNodeForeground(node, item);
            return item;
        }

        private void DataTreeViewItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not TreeViewItem item || item.Tag is not WzNode node ||
                !ReferenceEquals(ItemsControl.ContainerFromElement(null, e.OriginalSource as DependencyObject), item))
                return;

            if (e.ClickCount == 2)
            {
                if (!nativeSelectedNodes.Contains(node))
                    ApplyNativeSelection(node, ModifierKeys.None);
                else
                    SynchronizeNativeSelection(node);

                bool wasExpanded = item.IsExpanded;
                DataTree_DoubleClick(dataTreeView, EventArgs.Empty);

                // Parsing an IMG on double-click can add its children after the WPF
                // container was created, so give it a placeholder before expanding.
                if (node.Nodes.Count > 0)
                {
                    if (!wasExpanded && item.Items.Count == 0)
                        item.Items.Add(UiLocalization.Translate("Loading…"));
                    item.IsExpanded = !wasExpanded;
                }

                FocusNativeTreeItem(item);
                e.Handled = true;
                return;
            }

            ApplyNativeSelection(node, Keyboard.Modifiers);
            FocusNativeTreeItem(item);
            e.Handled = true;
        }

        private void DataTreeViewItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not TreeViewItem item || item.Tag is not WzNode node ||
                !ReferenceEquals(ItemsControl.ContainerFromElement(null, e.OriginalSource as DependencyObject), item))
                return;

            if (!nativeSelectedNodes.Contains(node))
                ApplyNativeSelection(node, ModifierKeys.None);
            else
                SynchronizeNativeSelection(node);
            FocusNativeTreeItem(item);
            item.ContextMenu = BuildNativeContextMenu(node.ContextMenuStrip);
        }

        private void FocusNativeTreeItem(TreeViewItem item)
        {
            isSynchronizingNativeSelection = true;
            try
            {
                item.Focus();
            }
            finally
            {
                isSynchronizingNativeSelection = false;
            }
            UpdateNativeSelectionVisuals();
        }

        private void ApplyNativeSelection(WzNode node, ModifierKeys modifiers)
        {
            bool controlPressed = (modifiers & ModifierKeys.Control) != 0;
            bool shiftPressed = (modifiers & ModifierKeys.Shift) != 0;

            if (shiftPressed && nativeSelectionAnchor != null)
            {
                List<WzNode> visibleNodes = GetVisibleNativeNodes();
                int anchorIndex = visibleNodes.IndexOf(nativeSelectionAnchor);
                int nodeIndex = visibleNodes.IndexOf(node);
                if (anchorIndex >= 0 && nodeIndex >= 0)
                {
                    if (!controlPressed)
                        nativeSelectedNodes.Clear();
                    int first = Math.Min(anchorIndex, nodeIndex);
                    int last = Math.Max(anchorIndex, nodeIndex);
                    for (int index = first; index <= last; index++)
                    {
                        if (!nativeSelectedNodes.Contains(visibleNodes[index]))
                            nativeSelectedNodes.Add(visibleNodes[index]);
                    }
                }
                else
                {
                    ReplaceNativeSelection(node);
                }
            }
            else if (controlPressed)
            {
                if (!nativeSelectedNodes.Remove(node))
                    nativeSelectedNodes.Add(node);
                nativeSelectionAnchor = node;
            }
            else
            {
                ReplaceNativeSelection(node);
            }

            SynchronizeNativeSelection(node);
        }

        private void ReplaceNativeSelection(WzNode node)
        {
            nativeSelectedNodes.Clear();
            nativeSelectedNodes.Add(node);
            nativeSelectionAnchor = node;
        }

        private void SynchronizeNativeSelection(WzNode activeNode)
        {
            isSynchronizingNativeSelection = true;
            try
            {
                DataTree.SelectedNode = activeNode;
                DataTree.SelectedNodes = new System.Collections.ArrayList(nativeSelectedNodes);
                ShowSelectedDataTreeNode(activeNode);
            }
            finally
            {
                isSynchronizingNativeSelection = false;
            }
            UpdateNativeSelectionVisuals();

            // Picking a node is how the user says "search here", so it ends the current search
            // session and the next Find next re-snapshots from this selection. The search's own
            // jump to a hit is excluded - otherwise the first hit would become the search root
            // and Find next could never reach the second match.
            if (!isNavigatingSearchResult)
                EndSearchSession();
        }

        private List<WzNode> GetVisibleNativeNodes()
        {
            FlushPendingNativeTreeItems();
            var result = new List<WzNode>();
            foreach (TreeViewItem root in dataTreeView.Items.OfType<TreeViewItem>())
                AddVisibleNativeNodes(root, result);
            return result;
        }

        private static void AddVisibleNativeNodes(TreeViewItem item, List<WzNode> result)
        {
            if (item.Tag is WzNode node)
                result.Add(node);
            if (!item.IsExpanded)
                return;
            foreach (TreeViewItem child in item.Items.OfType<TreeViewItem>())
                AddVisibleNativeNodes(child, result);
        }

        /// <summary>
        /// The nodes currently carrying the selection highlight, so a selection change can repaint
        /// exactly those instead of going looking for them.
        /// </summary>
        private readonly HashSet<WzNode> nativeSelectionPainted = new HashSet<WzNode>();

        /// <summary>
        /// Moves the selection highlight onto <see cref="nativeSelectedNodes"/>, touching only the
        /// items whose state actually changed.
        ///
        /// This used to sweep every materialised TreeViewItem on every selection change, so the
        /// cost of a single arrow keypress grew with how much of the tree the user had opened -
        /// one expanded boss image (Mob 8880100.img is 2,594 nodes) was enough to make a held
        /// arrow key stutter, because each keypress re-set Foreground and Background on thousands
        /// of items that had nothing to do with the change.
        /// </summary>
        private void UpdateNativeSelectionVisuals()
        {
            HashSet<WzNode> selected = nativeSelectedNodes.Count == 0
                ? null
                : new HashSet<WzNode>(nativeSelectedNodes);

            foreach (WzNode node in nativeSelectionPainted)
            {
                if (selected != null && selected.Contains(node))
                    continue; // still selected - leave it painted
                if (!nativeTreeItems.TryGetValue(node, out TreeViewItem item))
                    continue;

                if (item.IsSelected)
                    item.IsSelected = false;
                item.ClearValue(Control.BackgroundProperty);
                ApplyNativeNodeForeground(node, item);
            }

            nativeSelectionPainted.Clear();
            if (selected == null)
                return;

            // Looks the item up rather than assuming one exists: a selected node under a collapsed
            // parent has no container yet, and gets painted when one is built.
            foreach (WzNode node in nativeSelectedNodes)
            {
                if (!nativeTreeItems.TryGetValue(node, out TreeViewItem item))
                    continue;

                item.Background = System.Windows.SystemColors.HighlightBrush;
                item.Foreground = System.Windows.SystemColors.HighlightTextBrush;
                nativeSelectionPainted.Add(node);
            }
        }

        /// <summary>
        /// Repaints every materialised item's foreground from its WzNode. Needed after an edit
        /// marks nodes red somewhere across the tree, where the set of changed nodes is not known
        /// here - unlike a selection change, which knows exactly what moved. Selected items are
        /// skipped: they wear the highlight foreground until they are deselected, and
        /// UpdateNativeSelectionVisuals applies the red at that point.
        /// </summary>
        private void RefreshNativeNodeForegrounds()
        {
            foreach ((WzNode node, TreeViewItem item) in nativeTreeItems)
            {
                if (nativeSelectionPainted.Contains(node))
                    continue;
                ApplyNativeNodeForeground(node, item);
            }
        }

        /// <summary>
        /// Mirrors WzNode's "added or edited by hand" red onto the WPF item. The WinForms tree is
        /// only a model here, so the ForeColor it already sets is never actually seen - without
        /// this, a pasted or edited node looks exactly like an untouched one.
        /// </summary>
        private void ApplyNativeNodeForeground(WzNode node, TreeViewItem item)
        {
            if (node == null || item == null)
                return;

            if (!node.IsWzObjectAddedManually)
            {
                item.ClearValue(Control.ForegroundProperty);
                return;
            }

            if (nativeChangedNodeBrush == null)
            {
                System.Drawing.Color changed = WzNode.CHANGED_NODE_FOREGROUND_COLOR;
                nativeChangedNodeBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(changed.A, changed.R, changed.G, changed.B));
            }
            item.Foreground = nativeChangedNodeBrush;
        }

        // Built on first use rather than in a field initialiser, so MainPanel's constructor does
        // not have to be touched.
        private System.Windows.Media.Brush nativeChangedNodeBrush;

        private ContextMenu BuildNativeContextMenu(System.Windows.Forms.ContextMenuStrip source)
        {
            if (source == null) return null;
            var menu = new ContextMenu();
            foreach (System.Windows.Forms.ToolStripItem sourceItem in source.Items)
            {
                if (sourceItem is System.Windows.Forms.ToolStripSeparator) { menu.Items.Add(new Separator()); continue; }
                if (sourceItem is not System.Windows.Forms.ToolStripMenuItem command) continue;
                menu.Items.Add(BuildNativeMenuItem(command));
            }
            return menu;
        }

        private MenuItem BuildNativeMenuItem(System.Windows.Forms.ToolStripMenuItem source)
        {
            var item = new MenuItem { Header = UiLocalization.Translate(source.Text), IsEnabled = source.Enabled, IsCheckable = source.CheckOnClick, IsChecked = source.Checked };
            foreach (System.Windows.Forms.ToolStripItem child in source.DropDownItems)
            {
                if (child is System.Windows.Forms.ToolStripSeparator) item.Items.Add(new Separator());
                else if (child is System.Windows.Forms.ToolStripMenuItem childCommand) item.Items.Add(BuildNativeMenuItem(childCommand));
            }
            item.Click += (_, _) => { source.PerformClick(); Dispatcher.BeginInvoke(RefreshNativeDataTree); };
            return item;
        }

        private void DataTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is not TreeViewItem item || item.Tag is not WzNode node) return;
            if (!isSynchronizingNativeSelection)
                ApplyNativeSelection(node, Keyboard.Modifiers);
        }

        private void DataTreeView_ItemExpanded(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not TreeViewItem item || item.Tag is not WzNode node) return;
            var args = new System.Windows.Forms.TreeViewCancelEventArgs(node, false, System.Windows.Forms.TreeViewAction.Expand);
            DataTree_BeforeExpand(DataTree, args);
            if (!args.Cancel)
                PopulateNativeTreeItem(item, node);
        }

        /// <summary>
        /// Builds the WPF children for an expanded node. A TreeViewItem costs ~60us to construct,
        /// so a node with thousands of children (String.wz/Eqp.img/Eqp/Cap has 3,096) froze the UI
        /// thread for ~440ms before anything appeared. Only the first chunk is built here; the rest
        /// is appended at Background priority, so the expand paints immediately and the tree stays
        /// responsive while it fills in. Anything that needs the full list calls
        /// FlushPendingNativeTreeItems first.
        /// </summary>
        private void PopulateNativeTreeItem(TreeViewItem item, WzNode node)
        {
            RemoveNativeDescendantItems(item);
            item.Items.Clear();
            if (pendingNativeFills != null)
                pendingNativeFills.Remove(item);

            int filled = AppendNativeTreeItems(item, node, NativeTreeFillChunk);
            if (filled < node.Nodes.Count)
            {
                if (pendingNativeFills == null)
                    pendingNativeFills = new Dictionary<TreeViewItem, WzNode>();
                pendingNativeFills[item] = node;
                Dispatcher.BeginInvoke(new Action(FillPendingNativeTreeItems), DispatcherPriority.Background);
            }
            UpdateNativeSelectionVisuals();
        }

        /// <summary>
        /// Appends up to <paramref name="limit"/> more children, continuing from wherever
        /// item.Items stopped. Returns how many children exist in the WPF item afterwards.
        /// </summary>
        private int AppendNativeTreeItems(TreeViewItem item, WzNode node, int limit)
        {
            int start = item.Items.Count;
            int total = node.Nodes.Count;
            int added = 0;
            for (int i = start; i < total && added < limit; i++)
            {
                if (node.Nodes[i] is not WzNode child)
                    continue;
                item.Items.Add(CreateNativeTreeItem(child));
                added++;
            }
            return start + added;
        }

        /// <summary>
        /// One background batch of the pending fills. Re-queues itself until everything is built.
        /// </summary>
        private void FillPendingNativeTreeItems()
        {
            if (pendingNativeFills == null || pendingNativeFills.Count == 0)
                return;

            TreeViewItem target = null;
            WzNode node = null;
            foreach (KeyValuePair<TreeViewItem, WzNode> pair in pendingNativeFills)
            {
                target = pair.Key;
                node = pair.Value;
                break;
            }
            if (target == null)
                return;

            // A RefreshNativeDataTree since the fill was queued throws the old containers away and
            // builds new ones; finishing the orphan would be wasted work.
            if (!nativeTreeItems.TryGetValue(node, out TreeViewItem live) || !ReferenceEquals(live, target))
            {
                pendingNativeFills.Remove(target);
            }
            else if (AppendNativeTreeItems(target, node, NativeTreeFillChunk) >= node.Nodes.Count)
            {
                pendingNativeFills.Remove(target);
            }

            if (pendingNativeFills.Count > 0)
                Dispatcher.BeginInvoke(new Action(FillPendingNativeTreeItems), DispatcherPriority.Background);
            else
                UpdateNativeSelectionVisuals();
        }

        /// <summary>
        /// Finishes every outstanding fill immediately. Anything that walks the WPF children and
        /// needs them all - revealing a node, working out a shift-select range - must call this
        /// first, or it will only see the first chunk.
        /// </summary>
        private bool HasPendingNativeTreeFills => pendingNativeFills != null && pendingNativeFills.Count > 0;

        private void FlushPendingNativeTreeItems()
        {
            if (pendingNativeFills == null || pendingNativeFills.Count == 0)
                return;

            TreeViewItem[] targets = new TreeViewItem[pendingNativeFills.Count];
            pendingNativeFills.Keys.CopyTo(targets, 0);
            for (int i = 0; i < targets.Length; i++)
            {
                if (!pendingNativeFills.TryGetValue(targets[i], out WzNode node))
                    continue;
                while (AppendNativeTreeItems(targets[i], node, NativeTreeFillChunk) < node.Nodes.Count)
                {
                }
                pendingNativeFills.Remove(targets[i]);
            }
            UpdateNativeSelectionVisuals();
        }

        // Big enough to fill any realistic viewport in the first, synchronous pass.
        private const int NativeTreeFillChunk = 200;

        // Created lazily so MainPanel's constructor does not have to change.
        private Dictionary<TreeViewItem, WzNode> pendingNativeFills;

        /// <summary>
        /// Set while the search is jumping to one of its own hits. The selection that happens in
        /// there is the search showing a result, not the user choosing a new place to search, so
        /// it must not restart the search session - see SynchronizeNativeSelection.
        /// </summary>
        private bool isNavigatingSearchResult;

        private void SelectAndRevealNativeNode(WzNode node)
        {
            if (node == null)
                return;

            isNavigatingSearchResult = true;
            try
            {
                RevealAndSelectNativeNodeCore(node);
            }
            finally
            {
                isNavigatingSearchResult = false;
            }
        }

        private void RevealAndSelectNativeNodeCore(WzNode node)
        {
            FlushPendingNativeTreeItems();

            var path = new List<WzNode>();
            for (WzNode current = node; current != null; current = current.Parent as WzNode)
                path.Add(current);
            path.Reverse();

            TreeViewItem item = null;
            for (int index = 0; index < path.Count; index++)
            {
                WzNode pathNode = path[index];
                if (!nativeTreeItems.TryGetValue(pathNode, out item))
                {
                    if (index == 0 || !nativeTreeItems.TryGetValue(path[index - 1], out TreeViewItem parentItem))
                        return;

                    PopulateNativeTreeItem(parentItem, path[index - 1]);
                    if (!nativeTreeItems.TryGetValue(pathNode, out item))
                    {
                        // PopulateNativeTreeItem builds only the first chunk synchronously; a
                        // target past it (child #836 of a 2,171-image file, say) has no
                        // container yet, and the reveal used to silently stop here - the hit
                        // was found but never selected. Finish the fill, then look again.
                        FlushPendingNativeTreeItems();
                        if (!nativeTreeItems.TryGetValue(pathNode, out item))
                            return;
                    }
                }

                if (index < path.Count - 1 && !item.IsExpanded)
                    item.IsExpanded = true;
            }

            ReplaceNativeSelection(node);
            SynchronizeNativeSelection(node);
            FocusNativeTreeItem(item);
            BringNativeNodeIntoView(node);
        }

        /// <summary>
        /// Scrolls a node's TreeViewItem into view, realizing its container first when
        /// virtualization has thrown it away.
        ///
        /// <see cref="UIElement.BringIntoView()"/> on its own is a no-op for a virtualized item:
        /// with no visual parent there is nothing to scroll toward, so the selection moved while
        /// the tree stayed exactly where it was. Deferring the call does not help either - the
        /// item is not merely un-laid-out, it is not in the visual tree at all. Walking the path
        /// from the root and asking each level's VirtualizingStackPanel to bring the child index
        /// into view realizes the container, after which BringIntoView can position it exactly.
        /// </summary>
        private void BringNativeNodeIntoView(WzNode node)
        {
            if (node == null)
                return;

            var path = new List<WzNode>();
            for (WzNode current = node; current != null; current = current.Parent as WzNode)
                path.Add(current);
            path.Reverse();

            for (int index = 0; index < path.Count; index++)
            {
                if (!nativeTreeItems.TryGetValue(path[index], out TreeViewItem item))
                    return;

                ItemsControl owner;
                if (index == 0)
                    owner = dataTreeView;
                else if (!nativeTreeItems.TryGetValue(path[index - 1], out TreeViewItem parentItem))
                    return;
                else
                    owner = parentItem;

                int childIndex = owner.Items.IndexOf(item);
                if (childIndex >= 0 && childIndex < owner.Items.Count
                    && FindNativeItemsHost(owner) is VirtualizingStackPanel itemsHost)
                {
                    itemsHost.BringIndexIntoViewPublic(childIndex);
                }
                item.BringIntoView();
            }
        }

        /// <summary>
        /// The panel an ItemsControl actually lays its containers out in. Found by walking the
        /// visual tree for <see cref="Panel.IsItemsHost"/> because ItemsControl.ItemsHost itself
        /// is not public.
        /// </summary>
        private static Panel FindNativeItemsHost(DependencyObject root)
        {
            if (root == null)
                return null;
            if (root is Panel panel && panel.IsItemsHost)
                return panel;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < count; index++)
            {
                Panel found = FindNativeItemsHost(System.Windows.Media.VisualTreeHelper.GetChild(root, index));
                if (found != null)
                    return found;
            }
            return null;
        }

        private void RemoveNativeDescendantItems(TreeViewItem parent)
        {
            foreach (TreeViewItem child in parent.Items.OfType<TreeViewItem>())
            {
                RemoveNativeDescendantItems(child);
                if (child.Tag is WzNode node)
                    nativeTreeItems.Remove(node);
            }
        }

        private void DataTreeView_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Delete) { PromptRemoveSelectedTreeNodes(); e.Handled = true; RefreshNativeDataTree(); }
            else if (e.Key == Key.F5) { StartAnimateSelectedCanvas(); e.Handled = true; }
            else if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.C) { DoCopy(); e.Handled = true; }
            else if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.V) { DoPaste(); e.Handled = true; RefreshNativeDataTree(); }
            // Ctrl+F is deliberately not handled here. MainForm's Window-level PreviewKeyDown
            // tunnels first and marks the key handled, so this branch could never run anyway -
            // and leaving it would risk toggling the find panel twice for one keypress if that
            // routing ever changed. One key, one route, one toggle.
            else if ((Keyboard.Modifiers & ~ModifierKeys.Shift) == 0 && TryGetTypeAheadChar(e.Key, out char typedChar))
            {
                JumpToTypeAheadMatch(typedChar);
                e.Handled = true;
            }
        }

        /// <summary>
        /// Maps a plain letter/digit key press to the character it represents, for
        /// tree type-ahead. Returns false for anything else (function keys, Ctrl/Alt
        /// combinations, punctuation, etc.) so those keys fall through untouched.
        /// </summary>
        private static bool TryGetTypeAheadChar(Key key, out char ch)
        {
            if (key >= Key.A && key <= Key.Z)
            {
                ch = (char)('A' + (key - Key.A));
                return true;
            }
            if (key >= Key.D0 && key <= Key.D9)
            {
                ch = (char)('0' + (key - Key.D0));
                return true;
            }
            if (key >= Key.NumPad0 && key <= Key.NumPad9)
            {
                ch = (char)('0' + (key - Key.NumPad0));
                return true;
            }
            ch = '\0';
            return false;
        }

        /// <summary>
        /// Explorer-style "type to select": pressing a letter jumps the tree selection to
        /// the next visible node whose name starts with that letter (wrapping around, and
        /// skipping past the currently selected node so repeated presses of the same key
        /// cycle through every match, e.g. A, A, A -> Apple, Avocado, Ant, Apple, ...).
        /// Typing several different letters within about a second of each other builds a
        /// multi-character prefix (e.g. "AP" jumps straight to "Apple").
        /// </summary>
        private void JumpToTypeAheadMatch(char typedChar)
        {
            DateTime nowUtc = DateTime.UtcNow;
            bool timedOut = (nowUtc - typeAheadLastKeyTimeUtc) > TimeSpan.FromSeconds(1);
            typeAheadLastKeyTimeUtc = nowUtc;

            // Always extend the buffer on every keystroke within the timeout window - do NOT
            // special-case "same character pressed again" as a single-char "cycle" search (the
            // way Explorer's type-ahead does). That collapsing is wrong for this tree: node
            // names here are frequently plain numbers with repeated digits (e.g. "112", "100"),
            // and treating the second '1' in "1","1","2" as a repeat-cycle instead of extending
            // the buffer to "11" makes the search land on "120" instead of "112".
            if (timedOut || string.IsNullOrEmpty(typeAheadBuffer))
                typeAheadBuffer = typedChar.ToString();
            else
                typeAheadBuffer += typedChar;

            List<WzNode> visibleNodes = GetVisibleNativeNodes();
            if (visibleNodes.Count == 0)
                return;

            WzNode currentNode = nativeSelectionAnchor;
            if (currentNode == null && nativeSelectedNodes.Count > 0)
                currentNode = nativeSelectedNodes[nativeSelectedNodes.Count - 1];
            int currentIndex = currentNode != null ? visibleNodes.IndexOf(currentNode) : -1;

            // Walk forward starting just after the current node, wrapping around, and
            // checking the current node last so a lone match keeps its own selection.
            for (int offset = 1; offset <= visibleNodes.Count; offset++)
            {
                int index = (currentIndex + offset + visibleNodes.Count) % visibleNodes.Count;
                WzNode candidate = visibleNodes[index];
                if (candidate.Text != null && candidate.Text.StartsWith(typeAheadBuffer, StringComparison.CurrentCultureIgnoreCase))
                {
                    ApplyNativeSelection(candidate, ModifierKeys.None);
                    if (nativeTreeItems.TryGetValue(candidate, out TreeViewItem matchedItem))
                    {
                        FocusNativeTreeItem(matchedItem);
                        BringNativeNodeIntoView(candidate);
                    }
                    return;
                }
            }
        }

        /// <summary>
        /// Ctrl+F: opens the find panel when it's hidden, closes it when it's showing. Closing
        /// leaves the typed search text alone, so reopening resumes where the user left off.
        /// The X button keeps using the same fade-out, so both routes behave identically.
        /// </summary>
        public void ToggleSearchPanel()
        {
            if (grid_FindPanel.Visibility == Visibility.Visible)
            {
                HideSearchPanel();
                return;
            }
            ShowSearchPanel();
        }

        public void ShowSearchPanel()
        {
            if (grid_FindPanel.Visibility != Visibility.Visible)
            {
                var storyboard = (System.Windows.Media.Animation.Storyboard)FindResource("Storyboard_Find_FadeIn");
                storyboard.Begin();
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                findBox.Focus();
                Keyboard.Focus(findBox);
                findBox.SelectAll();
            }), DispatcherPriority.Input);
        }

        /// <summary>
        /// Runs the same fade-out the X button uses. Focus goes back to the tree so the next
        /// keystroke isn't swallowed by the find box that's on its way out.
        /// </summary>
        private void HideSearchPanel()
        {
            var storyboard = (System.Windows.Media.Animation.Storyboard)FindResource("Storyboard_Find_FadeOut");
            storyboard.Begin();
            dataTreeView.Focus();
        }

        /// <summary>
        /// Enter commits the header field being edited, matching how the old PropertyGrid
        /// behaved. Without this a TwoWay binding would only write back on LostFocus.
        /// </summary>
        private void NodeHeaderField_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || sender is not TextBox box)
                return;

            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }

        /// <summary>
        /// X/Y only make sense for a WzVectorProperty. IsXYPanelReadOnly is already maintained
        /// for exactly that case by ShowObjectValue (false only for vectors), so the header just
        /// follows it instead of duplicating the type check.
        /// </summary>
        private void NodeHeader_BindingPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(MainPanelPropertyItems.IsXYPanelReadOnly))
                return;

            bool isVector = !_bindingPropertyItem.IsXYPanelReadOnly;
            panel_NodeVector.Visibility = isVector ? Visibility.Visible : Visibility.Collapsed;

            // A vector has no single scalar value, so the 值 box would only be confusing.
            Visibility valueVisibility = isVector ? Visibility.Collapsed : Visibility.Visible;
            label_NodeValue.Visibility = valueVisibility;
            textBox_NodeValue.Visibility = valueVisibility;
        }

        private void DataTreeView_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void DataTreeView_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) NativeFilesDropped?.Invoke(this, paths);
            e.Handled = true;
        }

        public event EventHandler<string[]> NativeFilesDropped;
        private void DataTree_DoubleClick(object sender, EventArgs e)
        {
            if (DataTree.SelectedNode is not WzNode selectedNode)
                return;

            // IMG filesystem tree uses lightweight references until explicitly opened.
            if (TryResolveImgFilesystemImageNode(selectedNode, out var resolved) && resolved != null)
            {
                // If node was a reference, it is now a WzImage. Continue into normal flow.
            }

            if (DataTree.SelectedNode != null && DataTree.SelectedNode.Tag is WzImage && DataTree.SelectedNode.Nodes.Count == 0)
            {
                // A huge IMG parses on a worker thread so the window keeps painting; everything
                // else keeps the plain synchronous path.
                if (!TryBeginAsyncImageParse((WzNode)DataTree.SelectedNode))
                    ParseOnDataTreeSelectedItem(((WzNode)DataTree.SelectedNode), true);
            }

            // The node may only have become a real WzImage just now, and the editors were picked
            // back when it was still a reference. Re-run the display so a WorldMap opens on the
            // double-click that resolved it, instead of needing the user to click away and back.
            if (DataTree.SelectedNode is WzNode resolvedNode)
                ShowSelectedDataTreeNode(resolvedNode);
        }

        private void DataTree_AfterSelect(object sender, System.Windows.Forms.TreeViewEventArgs e)
        {
            if (isSynchronizingNativeSelection)
                return;

            WzNode selectedNode = e.Node as WzNode ?? DataTree.SelectedNode as WzNode;
            if (selectedNode != null)
            {
                ReplaceNativeSelection(selectedNode);
                nativeSelectedNodes.Clear();
                nativeSelectedNodes.AddRange(DataTree.SelectedNodes.Cast<WzNode>());
                if (nativeSelectedNodes.Count == 0)
                    nativeSelectedNodes.Add(selectedNode);
                UpdateNativeSelectionVisuals();
            }
            ShowSelectedDataTreeNode(selectedNode);
            //selectionLabel.Text = string.Format(Properties.Resources.SelectionType, ((WzNode)DataTree.SelectedNode).GetTypeName());
        }

        /// <summary>
        /// The selected node's WZ type name, shown next to "Ready" in MainForm's status bar.
        /// Empty when this tab has no selection - each tab keeps its own, so switching tabs shows
        /// that tab's selection rather than whatever the previous one had.
        /// </summary>
        public string SelectedWzTypeName { get; private set; } = string.Empty;

        /// <summary>Raised when <see cref="SelectedWzTypeName"/> changes.</summary>
        public event EventHandler SelectedWzTypeNameChanged;

        private void SetSelectedWzTypeName(string typeName)
        {
            typeName ??= string.Empty;
            if (SelectedWzTypeName == typeName)
                return;

            SelectedWzTypeName = typeName;
            SelectedWzTypeNameChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ShowSelectedDataTreeNode(WzNode node)
        {
            if (node?.Tag is not WzObject selectedObject)
                return;

            // Cheap enough to do straight away, so the status bar keeps up with the caret.
            _bindingPropertyItem.WzFileType = node.GetTypeName();
            SetSelectedWzTypeName(_bindingPropertyItem.WzFileType);

            // While this image parses on a worker, the editors must not touch it: several of
            // them index into the image, WzImage's indexer parses on demand, and ParseImage
            // locks the file's shared reader - the render would sit on that lock for the whole
            // parse, freezing the UI the async parse just unblocked. The completion handler
            // re-runs this method once the image is ready.
            if (selectedObject is WzImage pendingImage && imagesBeingParsed.Contains(pendingImage))
                return;

            QueueShowObjectValue(selectedObject);
        }

        /// <summary>The selection the editor still has to draw, always the newest one.</summary>
        private WzObject pendingObjectValue;

        /// <summary>The queued render, so a burst of selection changes only ever schedules one.</summary>
        private DispatcherOperation pendingObjectValueRender;

        /// <summary>
        /// Draws the right-hand editor for the latest selection, once.
        ///
        /// Every arrow keypress raises a selection change, and drawing one costs real work -
        /// decoding a canvas, rebuilding the editor cards, resolving a skill. Running that per
        /// keypress made holding an arrow key stutter, for nodes the user was only passing over.
        /// The render is queued at Background priority and always reads the newest selection, so a
        /// burst draws the node the user actually stopped on and skips everything in between. A
        /// single click still renders on the very next idle moment, which is imperceptible.
        /// </summary>
        private void QueueShowObjectValue(WzObject obj)
        {
            pendingObjectValue = obj;

            if (pendingObjectValueRender != null &&
                pendingObjectValueRender.Status == DispatcherOperationStatus.Pending)
                return; // already queued - it will pick up the selection above when it runs

            pendingObjectValueRender = Dispatcher.BeginInvoke(new Action(() =>
            {
                pendingObjectValueRender = null;

                WzObject target = pendingObjectValue;
                // Deferred, not dropped: while an image is still parsing on a worker, touching
                // it here would block on the file's shared reader lock for the whole parse.
                // The parse-completion handler re-renders it.
                if (target is WzImage busy && imagesBeingParsed.Contains(busy))
                    return;
                if (target != null)
                    ShowObjectValue(target);
            }), DispatcherPriority.Background);
        }

        /// <summary>
        /// Parse the data tree selected item on double clicking, or copy pasting into it.
        /// </summary>
        /// <param name="selectedNode"></param>
        // ---- async parse for huge IMGs -------------------------------------------------------

        /// <summary>
        /// Images whose parse is currently running on a worker thread. Guards against a second
        /// double-click starting a second ParseImage on the same image.
        /// </summary>
        private readonly HashSet<WzImage> imagesBeingParsed = new HashSet<WzImage>();

        /// <summary>
        /// Above this raw block size the double-click parse runs on a worker thread. Chosen from
        /// measurement, not from the node count (which is unknown before parsing): the smallest
        /// image that visibly froze the UI (Etc/Commodity.img, ~0.8s) has a 1.8MB block, while
        /// ordinary equip/mob images sit far below 1MB and keep the cheap synchronous path.
        /// </summary>
        private const int AsyncParseMinBlockSize = 1024 * 1024;

        /// <summary>
        /// Starts a background parse for a big unparsed image. Returns false when the image is
        /// small enough for the synchronous path; returns true (and does everything itself) when
        /// the parse has been started - or is already running - on a worker.
        ///
        /// The split of the work: ParseImage (pure MapleLib reader work, ~85% of the stall) runs
        /// on the worker; Reparse (the WinForms node model), the WPF mirror fill and the editors
        /// stay on the UI thread, exactly as before. One caveat is accepted and documented: WZ
        /// images of one file share that file's reader, and ParseImage locks it - a synchronous
        /// parse of another image in the same file would wait for a running background parse.
        /// </summary>
        private bool TryBeginAsyncImageParse(WzNode node)
        {
            if (node?.Tag is not WzImage img || img.Parsed)
                return false;
            if (img.BlockSize < AsyncParseMinBlockSize)
                return false;
            if (imagesBeingParsed.Contains(img))
                return true; // already loading - swallow the extra double-click entirely

            imagesBeingParsed.Add(img);
            nativeTreeItems.TryGetValue(node, out TreeViewItem loadingItem);
            if (loadingItem != null)
                loadingItem.Header = node.Text + "（載入中…）";

            System.Threading.Tasks.Task.Run(() => img.ParseImage())
                .ContinueWith(t =>
                {
                    Exception error = t.Exception; // observed either way, so a failure can never
                                                   // escalate to an unobserved-task crash
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() => CompleteAsyncImageParse(node, img, error)));
                    }
                    catch
                    {
                        // Dispatcher gone - the window is closing; nothing left to update.
                    }
                });
            return true;
        }

        /// <summary>
        /// UI-thread tail of the background parse: validates the world hasn't moved on, then does
        /// what the synchronous path always did - Reparse, expand, refresh the editor.
        /// </summary>
        private void CompleteAsyncImageParse(WzNode node, WzImage img, Exception error)
        {
            imagesBeingParsed.Remove(img);

            // Put the label back regardless of the outcome.
            nativeTreeItems.TryGetValue(node, out TreeViewItem item);
            if (item != null)
                item.Header = node.Text;

            // The user may have unloaded or reloaded the file, or replaced the node, while the
            // worker was running. A result for a world that no longer exists is dropped whole -
            // never grafted onto whatever tree is showing now.
            bool alive;
            try
            {
                alive = ReferenceEquals(node.Tag, img)
                    && node.TreeView == DataTree
                    && img.WzFileParent?.IsUnloaded != true;
            }
            catch
            {
                alive = false;
            }
            if (!alive)
                return;

            if (error != null)
            {
                // This image only; it stays unparsed, so double-clicking again retries.
                string reason = error is AggregateException agg ? (agg.InnerException?.Message ?? agg.Message) : error.Message;
                System.Windows.Forms.MessageBox.Show("無法解析 " + node.Text + "：\n" + reason,
                    HaRepacker.Properties.Resources.Error,
                    System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
                return;
            }

            // BeginUpdate suppresses per-node native messages in case the WinForms tree has a
            // window handle (created e.g. by a SelectedNode assignment) - with one, every one of
            // thousands of adds would otherwise round-trip through the control.
            DataTree.BeginUpdate();
            try
            {
                node.Reparse();
                node.Expand(); // keep the WinForms model's expansion state in step, as the sync path does
            }
            finally
            {
                DataTree.EndUpdate();
            }

            if (item != null)
            {
                if (node.Nodes.Count > 0 && item.Items.Count == 0)
                    item.Items.Add(UiLocalization.Translate("Loading…"));
                if (item.IsExpanded)
                    PopulateNativeTreeItem(item, node); // already open - Expanded won't re-fire
                else
                    item.IsExpanded = true; // triggers the usual chunked PopulateNativeTreeItem fill
            }

            // Refresh the right side only when the user is still on this node - never steal a
            // selection they have since moved elsewhere.
            if (ReferenceEquals(DataTree.SelectedNode, node))
                ShowSelectedDataTreeNode(node);
        }

        private static void ParseOnDataTreeSelectedItem(WzNode selectedNode, bool expandDataTree = true)
        {
            if (selectedNode.Tag is ImgFileWzImageReference imgRef)
            {
                var resolved = imgRef.Resolve();
                if (resolved == null)
                    return;

                resolved.HRTag = selectedNode;
                selectedNode.Tag = resolved;
            }

            if (selectedNode.Tag is not WzImage wzImage)
                return;

            if (!wzImage.Parsed)
                wzImage.ParseImage();
            selectedNode.Reparse();
            if (expandDataTree)
            {
                selectedNode.Expand();
            }
        }

        private void DataTree_KeyDown(object sender, System.Windows.Forms.KeyEventArgs e)
        {
            if (!DataTree.Focused) return;
            bool ctrl = (System.Windows.Forms.Control.ModifierKeys & System.Windows.Forms.Keys.Control) == System.Windows.Forms.Keys.Control;
            bool alt = (System.Windows.Forms.Control.ModifierKeys & System.Windows.Forms.Keys.Alt) == System.Windows.Forms.Keys.Alt;
            bool shift = (System.Windows.Forms.Control.ModifierKeys & System.Windows.Forms.Keys.Shift) == System.Windows.Forms.Keys.Shift;
            System.Windows.Forms.Keys filteredKeys = e.KeyData;
            if (ctrl) filteredKeys = filteredKeys ^ System.Windows.Forms.Keys.Control;
            if (alt) filteredKeys = filteredKeys ^ System.Windows.Forms.Keys.Alt;
            if (shift) filteredKeys = filteredKeys ^ System.Windows.Forms.Keys.Shift;

            switch (filteredKeys)
            {
                case System.Windows.Forms.Keys.F5:
                    StartAnimateSelectedCanvas();
                    break;
                case System.Windows.Forms.Keys.Escape:
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    break;

                case System.Windows.Forms.Keys.Delete:
                    e.Handled = true;
                    e.SuppressKeyPress = true;

                    PromptRemoveSelectedTreeNodes();
                    break;
            }
            if (ctrl)
            {
                switch (filteredKeys)
                {
                    case System.Windows.Forms.Keys.R: // Render map        
                        //HaRepackerMainPanel.

                        e.Handled = true;
                        e.SuppressKeyPress = true;
                        break;
                    case System.Windows.Forms.Keys.C:
                        DoCopy();
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                        break;
                    case System.Windows.Forms.Keys.V:
                        DoPaste();
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                        break;
                    case System.Windows.Forms.Keys.F: // open search box
                        ShowSearchPanel();
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                        break;
                    case System.Windows.Forms.Keys.T:
                    case System.Windows.Forms.Keys.O:
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                        break;
                }
            }
        }

        private void DataTree_BeforeExpand(object sender, System.Windows.Forms.TreeViewCancelEventArgs e)
        {
            if (e.Node is not WzNode node)
                return;

            if (node.Tag is not VirtualWzDirectory virtualDir)
                return;

            // Only populate once: placeholder is inserted by WzNode for VirtualWzDirectory.
            if (node.Nodes.Count != 1 || node.Nodes[0].Tag is not WzNode.LazyLoadPlaceholder)
                return;

            DataTree.BeginUpdate();
            try
            {
                node.Nodes.Clear();

                foreach (WzDirectory dir in virtualDir.WzDirectories)
                {
                    node.Nodes.Add(new WzNode(dir));
                }

                foreach (string fileName in virtualDir.GetImageNames())
                {
                    node.Nodes.Add(new WzNode(new ImgFileWzImageReference(virtualDir, fileName)));
                }

                if (Program.ConfigurationManager.UserSettings.Sort)
                {
                    _mainForm.SortNodesRecursively(node, true);
                }
            }
            finally
            {
                DataTree.EndUpdate();
            }
        }

        private static bool TryResolveImgFilesystemImageNode(WzNode node, out WzImage resolved)
        {
            resolved = null;

            if (node.Tag is not ImgFileWzImageReference imgRef)
                return false;

            resolved = imgRef.Resolve();
            if (resolved == null)
                return true;

            resolved.HRTag = node;
            node.Tag = resolved;
            return true;
        }
        #endregion

        #region Wz Directory Context Menu
        /// <summary>
        /// WzDirectory
        /// </summary>
        /// <param name="target"></param>
        public void AddWzDirectoryToSelectedNode(System.Windows.Forms.TreeNode target)
        {
            if (!(target.Tag is WzDirectory) && !(target.Tag is WzFile))
            {
                Warning.Error(Properties.Resources.MainCannotInsertToNode);
                return;
            }
            string name;
            if (!NameInputBox.Show(Properties.Resources.MainAddDir, 0, out name))
                return;

            bool added = false;

            WzObject obj = (WzObject)target.Tag;
            while (obj is WzFile || ((obj = obj.Parent) is WzFile))
            {
                WzFile topMostWzFileParent = (WzFile)obj;

                ((WzNode)target).AddObject(new WzDirectory(name, topMostWzFileParent), UndoRedoMan);
                added = true;
                break;
            }
            if (!added)
            {
                MessageBox.Show(Properties.Resources.MainTreeAddDirError);
            }
        }

        /// <summary>
        /// WzDirectory
        /// </summary>
        /// <param name="target"></param>
        public void AddWzImageToSelectedNode(System.Windows.Forms.TreeNode target)
        {
            string name;
            if (!(target.Tag is WzDirectory) && !(target.Tag is WzFile))
            {
                Warning.Error(Properties.Resources.MainCannotInsertToNode);
                return;
            }
            else if (!NameInputBox.Show(Properties.Resources.MainAddImg, 0, out name))
                return;
            ((WzNode)target).AddObject(new WzImage(name) { Changed = true }, UndoRedoMan);
        }

        /// <summary>
        /// WzByteProperty
        /// </summary>
        /// <param name="target"></param>
        public void AddWzByteFloatToSelectedNode(System.Windows.Forms.TreeNode target)
        {
            string name;
            double? d;
            if (!(target.Tag is IPropertyContainer))
            {
                Warning.Error(Properties.Resources.MainCannotInsertToNode);
                return;
            }
            else if (!FloatingPointInputBox.Show(Properties.Resources.MainAddFloat, out name, out d))
                return;
            ((WzNode)target).AddObject(new WzFloatProperty(name, (float)d), UndoRedoMan);
        }

        /// <summary>
        /// WzCanvasProperty
        /// </summary>
        /// <param name="target"></param>
        public void AddWzCanvasToSelectedNode(System.Windows.Forms.TreeNode target)
        {
            string name;
            List<Bitmap> bitmaps = new();
            if (!(target.Tag is IPropertyContainer))
            {
                Warning.Error(Properties.Resources.MainCannotInsertToNode);
                return;
            }
            else if (!BitmapInputBox.Show(Properties.Resources.MainAddCanvas, out name, out bitmaps))
                return;

            WzNode wzNode = ((WzNode)target);

            int i = 0;
            foreach (System.Drawing.Bitmap bmp in bitmaps)
            {
                string proposedName = bitmaps.Count == 1 ? name : (name + i);

                // Check if name already exists in parent
                if (WzNode.GetChildNode(wzNode, proposedName) != null)
                {
                    Warning.Error(Properties.Resources.MainNodeExists);
                    continue;
                }

                WzPngProperty pngProperty = new();
                pngProperty.PNG = bmp;

                WzCanvasProperty canvas = new(proposedName);
                canvas.PngProperty = pngProperty;

                WzNode newInsertedNode = wzNode.AddObject(canvas, UndoRedoMan);
                // Add an additional WzVectorProperty with X Y of 0,0
                newInsertedNode.AddObject(new WzVectorProperty(WzCanvasProperty.OriginPropertyName, new WzIntProperty("X", 0), new WzIntProperty("Y", 0)), UndoRedoMan);

                i++;
            }
        }

        /// <summary>
        /// WzCompressedInt
        /// </summary>
        /// <param name="target"></param>
        public void AddWzCompressedIntToSelectedNode(System.Windows.Forms.TreeNode target)
        {
            string name;
            int? value;
            if (!(target.Tag is IPropertyContainer))
            {
                Warning.Error(Properties.Resources.MainCannotInsertToNode);
                return;
            }
            else if (!IntInputBox.Show(
                Properties.Resources.MainAddInt,
                "", 0,
                out name, out value))
                return;
            ((WzNode)target).AddObject(new WzIntProperty(name, (int)value), UndoRedoMan);
        }

        /// <summary>
        /// WzLongProperty
        /// </summary>
        /// <param name="target"></param>
        public void AddWzLongToSelectedNode(System.Windows.Forms.TreeNode target)
        {
            string name;
            long? value;
            if (!(target.Tag is IPropertyContainer))
            {
                Warning.Error(Properties.Resources.MainCannotInsertToNode);
                return;
            }
            else if (!LongInputBox.Show(Properties.Resources.MainAddInt, out name, out value))
                return;
            ((WzNode)target).AddObject(new WzLongProperty(name, (long)value), UndoRedoMan);
        }

        /// <summary>
        /// WzConvexProperty
        /// </summary>
        /// <param name="target"></param>
        public void AddWzConvexPropertyToSelectedNode(System.Windows.Forms.TreeNode target)
        {
            string name;
            if (!(target.Tag is IPropertyContainer))
            {
                Warning.Error(Properties.Resources.MainCannotInsertToNode);
                return;
            }
            else if (!NameInputBox.Show(Properties.Resources.MainAddConvex, 0, out name))
                return;
            ((WzNode)target).AddObject(new WzConvexProperty(name), UndoRedoMan);
        }

        /// <summary>
        /// WzNullProperty
        /// </summary>
        /// <param name="target"></param>
        public void AddWzDoublePropertyToSelectedNode(System.Windows.Forms.TreeNode target)
        {
            string name;
            double? d;
            if (!(target.Tag is IPropertyContainer))
            {
                Warning.Error(Properties.Resources.MainCannotInsertToNode);
                return;
            }
            else if (!FloatingPointInputBox.Show(Properties.Resources.MainAddDouble, out name, out d))
                return;
            ((WzNode)target).AddObject(new WzDoubleProperty(name, (double)d), UndoRedoMan);
        }

        /// <summary>
        /// WzNullProperty
        /// </summary>
        /// <param name="target"></param>
        public void AddWzNullPropertyToSelectedNode(System.Windows.Forms.TreeNode target)
        {
            string name;
            if (!(target.Tag is IPropertyContainer))
            {
                Warning.Error(Properties.Resources.MainCannotInsertToNode);
                return;
            }
            else if (!NameInputBox.Show(Properties.Resources.MainAddNull, 0, out name))
                return;
            ((WzNode)target).AddObject(new WzNullProperty(name), UndoRedoMan);
        }

        /// <summary>
        /// WzSoundProperty
        /// </summary>
        /// <param name="target"></param>
        public void AddWzSoundPropertyToSelectedNode(System.Windows.Forms.TreeNode target)
        {
            string name;
            string path;
            if (!(target.Tag is IPropertyContainer))
            {
                Warning.Error(Properties.Resources.MainCannotInsertToNode);
                return;
            }
            else if (!SoundInputBox.Show(Properties.Resources.MainAddSound, out name, out path))
                return;
            ((WzNode)target).AddObject(new WzBinaryProperty(name, path), UndoRedoMan);
        }

        /// <summary>
        /// WzStringProperty
        /// </summary>
        /// <param name="target"></param>
        public void AddWzStringPropertyToSelectedIndex(System.Windows.Forms.TreeNode target)
        {
            string name;
            string value;
            if (!(target.Tag is IPropertyContainer))
            {
                Warning.Error(Properties.Resources.MainCannotInsertToNode);
                return;
            }
            else if (!NameValueInputBox.Show(Properties.Resources.MainAddString, out name, out value))
                return;
            ((WzNode)target).AddObject(new WzStringProperty(name, value), UndoRedoMan);
        }

        /// <summary>
        /// WzSubProperty
        /// </summary>
        /// <param name="target"></param>
        public void AddWzSubPropertyToSelectedIndex(System.Windows.Forms.TreeNode target)
        {
            string name;
            if (!(target.Tag is IPropertyContainer))
            {
                Warning.Error(Properties.Resources.MainCannotInsertToNode);
                return;
            }
            else if (!NameInputBox.Show(Properties.Resources.MainAddSub, 0, out name))
                return;
            ((WzNode)target).AddObject(new WzSubProperty(name), UndoRedoMan);
        }

        /// <summary>
        /// WzUnsignedShortProperty
        /// </summary>
        /// <param name="target"></param>
        public void AddWzUnsignedShortPropertyToSelectedIndex(System.Windows.Forms.TreeNode target)
        {
            string name;
            int? value;
            if (!(target.Tag is IPropertyContainer))
            {
                Warning.Error(Properties.Resources.MainCannotInsertToNode);
                return;
            }
            else if (!IntInputBox.Show(Properties.Resources.MainAddShort,
                "", 0,
                out name, out value))
                return;
            ((WzNode)target).AddObject(new WzShortProperty(name, (short)value), UndoRedoMan);
        }

        /// <summary>
        /// WzUOLProperty
        /// </summary>
        /// <param name="target"></param>
        public void AddWzUOLPropertyToSelectedIndex(System.Windows.Forms.TreeNode target)
        {
            string name;
            string value;
            if (!(target.Tag is IPropertyContainer))
            {
                Warning.Error(Properties.Resources.MainCannotInsertToNode);
                return;
            }
            else if (!NameValueInputBox.Show(Properties.Resources.MainAddLink, out name, out value))
                return;
            ((WzNode)target).AddObject(new WzUOLProperty(name, value), UndoRedoMan);
        }

        /// <summary>
        /// WzVectorProperty
        /// </summary>
        /// <param name="target"></param>
        public void AddWzVectorPropertyToSelectedIndex(System.Windows.Forms.TreeNode target)
        {
            string name;
            System.Drawing.Point? pt;
            if (!(target.Tag is IPropertyContainer))
            {
                Warning.Error(Properties.Resources.MainCannotInsertToNode);
                return;
            }
            else if (!VectorInputBox.Show(Properties.Resources.MainAddVec, out name, out pt))
                return;
            ((WzNode)target).AddObject(new WzVectorProperty(name, new WzIntProperty("X", ((System.Drawing.Point)pt).X), new WzIntProperty("Y", ((System.Drawing.Point)pt).Y)), UndoRedoMan);
        }

        /// <summary>
        /// WzLuaProperty
        /// </summary>
        /// <param name="target"></param>
        public void AddWzLuaPropertyToSelectedIndex(System.Windows.Forms.TreeNode target)
        {
 /*           string name;
            string value;
            if (!(target.Tag is WzDirectory) && !(target.Tag is WzFile))
            {
                Warning.Error(Properties.Resources.MainCannotInsertToNode);
                return;
            }
            else if (!NameValueInputBox.Show(Properties.Resources.MainAddString, out name, out value))
                return;

            string propertyName = name;
            if (!propertyName.EndsWith(".lua"))
            {
                propertyName += ".lua"; // it must end with .lua regardless
            }
            ((WzNode)target).AddObject(new WzImage(propertyName), UndoRedoMan);*/
        }

        /// <summary>
        /// Remove selected nodes
        /// </summary>
        public void PromptRemoveSelectedTreeNodes()
        {
            if (!Warning.Warn(Properties.Resources.MainConfirmRemove))
            {
                return;
            }

            List<UndoRedoAction> actions = new List<UndoRedoAction>();

            System.Windows.Forms.TreeNode[] nodeArr = new System.Windows.Forms.TreeNode[DataTree.SelectedNodes.Count];
            DataTree.SelectedNodes.CopyTo(nodeArr, 0);

            // Deleting 100 nodes is one user action: whatever refreshes the deletions trigger
            // collapse into the single one the caller issues afterwards.
            using (NativeTreeUpdateScope())
            {
                foreach (WzNode node in nodeArr)
                    if (!(node.Tag is WzFile) && node.Parent != null)
                    {
                        actions.Add(UndoRedoManager.ObjectRemoved((WzNode)node.Parent, node));
                        node.DeleteWzNode();
                    }
            }
            UndoRedoMan.AddUndoBatch(actions);
        }

        /// <summary>
        /// Rename an individual node
        /// </summary>
        public void PromptRenameWzTreeNode(WzNode node)
        {
            if (node == null)
                return;

            string newName = "";
            WzNode wzNode = node;
            if (RenameInputBox.Show(Properties.Resources.MainConfirmRename, wzNode.Text, out newName))
            {
                // Cancel never reaches here; a same-name confirm is a no-op and records nothing.
                string oldName = wzNode.Text;
                if (string.IsNullOrEmpty(newName) || string.Equals(newName, oldName, StringComparison.Ordinal))
                    return;

                wzNode.ChangeName(newName);
                // TreeViewItem.Header is a one-time copy of node.Text; push the rename across.
                UpdateNativeNodeHeader(wzNode);
                UndoRedoMan.AddUndoBatch(new System.Collections.Generic.List<UndoRedoAction>
                    { UndoRedoManager.ObjectRenamed(wzNode, oldName, newName) });
            }
        }
        #endregion

        #region Panel Loading Events
        /// <summary>
        /// Set panel loading splash screen from MainForm.cs
        /// <paramref name="currentDispatcher"/>
        /// </summary>
        public void OnSetPanelLoading(Dispatcher currentDispatcher = null)
        {
            Action action = () =>
            {
                loadingPanel.OnStartAnimate();
                grid_LoadingPanel.Visibility = Visibility.Visible;
                dataTreeView.Visibility = Visibility.Collapsed;
            };
            if (currentDispatcher != null)
                currentDispatcher.BeginInvoke(action);
            else
                grid_LoadingPanel.Dispatcher.BeginInvoke(action);
        }

        /// <summary>
        /// Remove panel loading splash screen from MainForm.cs
        /// <paramref name="currentDispatcher"/>
        /// </summary>
        public void OnSetPanelLoadingCompleted(Dispatcher currentDispatcher = null)
        {
            Action action = () =>
            {
                loadingPanel.OnPauseAnimate();
                grid_LoadingPanel.Visibility = Visibility.Collapsed;
                dataTreeView.Visibility = Visibility.Visible;
                RefreshNativeDataTree();
            };
            if (currentDispatcher != null)
                currentDispatcher.BeginInvoke(action);
            else
                grid_LoadingPanel.Dispatcher.BeginInvoke(action);
        }

        /// <summary>
        /// Save the image animation into a JPG file
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public void SaveImageAnimation_Click()
        {
            WzObject seletedWzObject = (WzObject)DataTree.SelectedNode.Tag;

            if (!AnimationBuilder.IsValidAnimationWzObject(seletedWzObject))
                return;

            System.Windows.Forms.SaveFileDialog dialog = new System.Windows.Forms.SaveFileDialog()
            {
                Title = HaRepacker.Properties.Resources.SelectOutApng,
                Filter = string.Format("{0}|*.png", HaRepacker.Properties.Resources.ApngFilter)
            };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return;

            AnimationBuilder.ExtractAnimation((WzSubProperty)seletedWzObject, dialog.FileName, Program.ConfigurationManager.UserSettings.UseApngIncompatibilityFrame);
        }
        #endregion

        #region Animate
        /// <summary>
        /// Animate the list of selected canvases
        /// </summary>
        public void StartAnimateSelectedCanvas()
        {
            if (DataTree.SelectedNodes.Count == 0)
            {
                MessageBox.Show(UiLocalization.Translate("Please select one or more canvas nodes."));
                return;
            }

            List<WzNode> selectedNodes = new List<WzNode>();
            foreach (WzNode node in DataTree.SelectedNodes)
            {
                selectedNodes.Add(node);
            }

            string path_title = ((WzNode)DataTree.SelectedNodes[0]).Parent?.FullPath ?? "Animate";

            Thread thread = new Thread(() =>
            {
                try
                {
                    ImageAnimationPreviewWindow previewWnd = new ImageAnimationPreviewWindow(
                        selectedNodes.Select(node => (WzObject)node.Tag), path_title);
                    previewWnd.Run();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(string.Format(UiLocalization.Translate("Error previewing animation: {0}"), ex));
                }
            });
            thread.Start();
            // thread.Join();
        }

        private void nextLoopTime_comboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            /* if (nextLoopTime_comboBox == null)
                  return;

              switch (nextLoopTime_comboBox.SelectedIndex)
              {
                  case 1:
                      Program.ConfigurationManager.UserSettings.DelayNextLoop = 1000;
                      break;
                  case 2:
                      Program.ConfigurationManager.UserSettings.DelayNextLoop = 2000;
                      break;
                  case 3:
                      Program.ConfigurationManager.UserSettings.DelayNextLoop = 5000;
                      break;
                  case 4:
                      Program.ConfigurationManager.UserSettings.DelayNextLoop = 10000;
                      break;
                  default:
                      Program.ConfigurationManager.UserSettings.DelayNextLoop = Program.TimeStartAnimateDefault;
                      break;
              }*/
        }
        #endregion

        #region Buttons
        /// <summary>
        /// On texteditor save button clicked
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void TextEditor_SaveButtonClicked(object sender, EventArgs e)
        {
            if (DataTree.SelectedNode == null)
                return;

            WzNode node = (WzNode)DataTree.SelectedNode;
            WzObject obj = (WzObject)DataTree.SelectedNode.Tag;
            if (obj is WzLuaProperty luaProp)
            {
                string setText = textEditor.textEditor.Text;
                // Lua payloads are UTF-8 after decryption; preserve non-ASCII
                // characters when converting edited text back to bytes.
                luaProp.SetString(setText);

                // highlight node to the user
                node.ChangedNodeProperty();
            } 
            else if (obj is WzStringProperty stringProp)
            {
                //if (stringProp.IsSpineAtlasResources)
               // {
                    string setText = textEditor.textEditor.Text;

                    stringProp.Value = setText;

                    // highlight node to the user
                    node.ChangedNodeProperty();
                /*  } 
                  else
                  {
                      throw new NotSupportedException("Usage of TextEditor for non-spine WzStringProperty.");
                  }*/
            }
        }
        #endregion 

        #region Batch Edit
        /// <summary>
        /// Check for image updates to the ImageRenderViewer that the user is currently selecting, after batch operation
        /// </summary>
        /// <param name="selectedTreeNode"></param>
        /// <param name="canvasPropBox"></param>
        private void RefreshSelectedImageToImageRenderviewer(object selectedTreeNode, ImageRenderViewer canvasPropBox) {
            // Check for updates to the changed canvas image that the user is currently selecting
            if (selectedTreeNode is WzCanvasProperty) // only allow button click if its an image property
            {
                WzCanvasProperty canvas = (WzCanvasProperty)selectedTreeNode;
                System.Drawing.Image img = canvas?.GetLinkedWzCanvasBitmap();
                if (img != null && canvas != null) {
                    canvasPropBox.BindingPropertyItem.SurfaceFormat = WzPngFormatExtensions.GetXNASurfaceFormat(canvas.PngProperty.Format);
                    canvasPropBox.BindingPropertyItem.Bitmap = (Bitmap)img;
                    canvasPropBox.BindingPropertyItem.BitmapBackup = (Bitmap)img;
                }
            }
        }

        /// <summary>
        /// Fix the '_inlink' and '_outlink' image property for compatibility to old MapleStory ver.
        /// </summary>
        public async void FixLinkForOldMapleStory_OnClick()
        {
            // One repair per panel at a time: a second copy racing the first over the same
            // canvases would double-apply payloads and double-count the totals.
            if (linkRepairInProgress)
            {
                BatchInfo("補圖仍在進行中，請等它完成後再執行。");
                return;
            }

            List<WzNode> roots = DataTree.SelectedNodes.Cast<WzNode>().ToList();
            if (roots.Count == 0 && DataTree.SelectedNode is WzNode activeNode)
                roots.Add(activeNode);
            if (roots.Count == 0)
                return;

            DateTime t0 = DateTime.Now;
            LinkRepairResult result = await RunLinkRepairAsync(roots, showDialog: true, showProgress: true);
            if (result == null || result.Aborted)
                return;

            if (DataTree.SelectedNode?.Tag != null)
                RefreshSelectedImageToImageRenderviewer(DataTree.SelectedNode.Tag, canvasPropBox);

            double ms = (DateTime.Now - t0).TotalMilliseconds;
            if (result.Total == 0)
            {
                MessageBox.Show("沒有需要補圖的節點。");
                return;
            }
            MessageBox.Show(string.Format(UiLocalization.Translate("Completed.\nElapsed time: {0} ms (average: {1})"), ms, ms / roots.Count)
                + "\n" + result.Repaired + " 個連結已補上圖片。"
                + (result.Failed > 0 ? "\n" + result.Failed + " 個連結找不到來源圖片，原樣保留（未刪除 _inlink/_outlink）。" : ""));
        }

        /// <summary>What one link-repair run did, for the caller's summary and the harnesses.</summary>
        public sealed class LinkRepairResult
        {
            public int Total { get; init; }
            public int Repaired { get; init; }
            public int Failed { get; init; }
            public bool Aborted { get; init; }
            public string PhaseTimings { get; init; }
        }

        /// <summary>Guards against a second concurrent repair on this panel.</summary>
        private bool linkRepairInProgress;
        /// <summary>
        /// Bumped by CancelBackgroundTreeWork (close all / unload); a running repair checks it at
        /// every batch boundary and stops cleanly, leaving already-applied items applied.
        /// </summary>
        private int linkRepairGeneration;

        /// <summary>Thread-shared progress counters; workers write, the UI timer reads.</summary>
        private sealed class LinkRepairProgress
        {
            public int ImagesScanned;          // worker: Interlocked
            public volatile int Total = -1;    // -1 while the scan is still running
            public volatile int Completed;     // UI thread
            public volatile int Repaired;      // UI thread
            public volatile int Failed;        // UI thread
        }

        /// <summary>
        /// The async "_inlink/_outlink 補圖" pipeline. Phase 0 (UI): walk the selected nodes'
        /// structure without parsing anything. Phase 1 (background): parse the images and collect
        /// the linked canvases and _hash properties from the WZ model. Phase 2/3 (alternating):
        /// resolve + extract payloads for a batch on the background thread, then apply that batch
        /// - byte swap, link-property removal, dirty flags, node cleanup - on the UI thread, with
        /// a dispatcher yield between batches so input keeps pumping. One native tree refresh at
        /// the very end.
        ///
        /// Same per-canvas semantics as the old synchronous CheckImageNodeRecursively_linkRepair:
        /// resolution runs FIRST and a link that cannot be resolved is left exactly as it was.
        /// The one deliberate difference: images nobody has materialized in the tree are repaired
        /// in the model only - their WinForms/WPF nodes get built correctly later by the normal
        /// lazy Reparse, instead of being force-built just to delete two children again (that
        /// force-build was most of the old path's cost).
        /// </summary>
        public async Task<LinkRepairResult> RunLinkRepairAsync(IReadOnlyList<WzNode> rootNodes, bool showDialog, bool showProgress)
        {
            if (linkRepairInProgress)
                return new LinkRepairResult { Aborted = true, PhaseTimings = "already running" };
            linkRepairInProgress = true;
            int generation = ++linkRepairGeneration;

            var progress = new LinkRepairProgress();
            DispatcherTimer progressTimer = null;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            long discoverMs = 0, prepareMs = 0, applyMs = 0;
            int repaired = 0, failedCount = 0, total = 0;
            bool aborted = false;
            string failureSummary = null;
            bool treeBatchOpen = false;

            try
            {
                if (showProgress)
                    progressTimer = StartLinkRepairProgressTimer(progress, generation);

                // ---- Phase 0 (UI): structure only - no image is parsed here ----
                var images = new List<WzImage>();
                var propertyRoots = new List<WzImageProperty>();
                var directoryHashNodes = new List<WzNode>();
                foreach (WzNode root in rootNodes)
                    CollectLinkRepairRoots(root, images, propertyRoots, directoryHashNodes);

                // ---- Phase 1 (background): parse + discover ----
                var canvases = new List<WzCanvasProperty>();
                var hashProps = new List<WzImageProperty>();
                await Task.Run(() =>
                {
                    foreach (WzImage img in images)
                    {
                        if (generation != linkRepairGeneration)
                            return;
                        try
                        {
                            if (!img.Parsed)
                                img.ParseImage();
                            foreach (WzImageProperty prop in img.WzProperties.ToArray())
                            {
                                if (prop.Name == "_hash")
                                    hashProps.Add(prop);
                                DiscoverLinkRepairTargets(prop, canvases, hashProps);
                            }
                        }
                        catch
                        {
                            // One unparseable image costs that image, not the batch.
                        }
                        System.Threading.Interlocked.Increment(ref progress.ImagesScanned);
                    }
                    foreach (WzImageProperty prop in propertyRoots)
                    {
                        if (generation != linkRepairGeneration)
                            return;
                        try { DiscoverLinkRepairTargets(prop, canvases, hashProps); } catch { }
                    }
                });
                discoverMs = stopwatch.ElapsedMilliseconds;
                if (generation != linkRepairGeneration)
                    aborted = true;

                total = canvases.Count;
                progress.Total = total;

                // ---- Phase 2 + 3, alternating in bounded batches ----
                BeginNativeTreeUpdate();
                treeBatchOpen = true;

                const int BatchSize = 128;
                var payloads = new MapleLib.WzLib.PreparedCanvasLink[BatchSize];
                // Operation-local duplicate-target cache: 55% of the links in a full Npc repair
                // point at a target some earlier canvas already extracted (many frames share one
                // picture). Keyed by the resolution anchor's identity plus the link string -
                // an _inlink resolves relative to its own image, an _outlink through its own
                // file - so a hit is by construction the same source pixels. Only the immutable
                // prepared payload is shared; the flags are re-derived per canvas.
                var preparedByTarget = new Dictionary<(object anchor, string link), MapleLib.WzLib.PreparedCanvasLink>();
                for (int start = 0; start < canvases.Count; start += BatchSize)
                {
                    if (generation != linkRepairGeneration) { aborted = true; break; }
                    int count = Math.Min(BatchSize, canvases.Count - start);

                    long prepareStart = stopwatch.ElapsedMilliseconds;
                    await Task.Run(() =>
                    {
                        for (int i = 0; i < count; i++)
                        {
                            WzCanvasProperty canvas = canvases[start + i];
                            (object anchor, string link) key = default;
                            try
                            {
                                string inlink = (canvas[WzCanvasProperty.InlinkPropertyName] as WzStringProperty)?.Value;
                                if (inlink != null && canvas.ParentImage != null)
                                    key = (canvas.ParentImage, "I" + inlink);
                                else
                                {
                                    string outlink = (canvas[WzCanvasProperty.OutlinkPropertyName] as WzStringProperty)?.Value;
                                    if (outlink != null && canvas.WzFileParent != null)
                                        key = (canvas.WzFileParent, "O" + outlink);
                                }
                            }
                            catch { }

                            if (key.anchor != null && preparedByTarget.TryGetValue(key, out MapleLib.WzLib.PreparedCanvasLink hit))
                            {
                                payloads[i] = hit == null ? null : new MapleLib.WzLib.PreparedCanvasLink
                                {
                                    CompressedBytes = hit.CompressedBytes,
                                    Width = hit.Width,
                                    Height = hit.Height,
                                    Format = hit.Format,
                                    HadInlink = canvas.ContainsInlinkProperty(),
                                    HadOutlink = canvas.ContainsOutlinkProperty(),
                                };
                                continue;
                            }

                            payloads[i] = MapleLib.WzLib.WzLinkResolver.PrepareSingleCanvas(canvas);
                            if (key.anchor != null)
                                preparedByTarget[key] = payloads[i]; // failures cached too - same inputs resolve the same way
                        }
                    });
                    prepareMs += stopwatch.ElapsedMilliseconds - prepareStart;
                    if (generation != linkRepairGeneration) { aborted = true; break; }

                    long applyStart = stopwatch.ElapsedMilliseconds;
                    for (int i = 0; i < count; i++)
                    {
                        WzCanvasProperty canvas = canvases[start + i];
                        MapleLib.WzLib.PreparedCanvasLink payload = payloads[i];
                        payloads[i] = null;

                        bool ok = false;
                        try
                        {
                            // A file unloaded mid-run: leave its canvases alone.
                            if (payload != null && canvas.ParentImage?.WzFileParent?.IsUnloaded != true
                                && MapleLib.WzLib.WzLinkResolver.ApplyPreparedCanvas(canvas, payload))
                            {
                                if (canvas.ParentImage != null)
                                    canvas.ParentImage.Changed = true;
                                // Only canvases someone actually materialized in the tree need
                                // node surgery; everyone else's nodes are built fresh from the
                                // (already repaired) model on the next lazy Reparse.
                                if (canvas.HRTag is WzNode canvasNode && canvasNode.TreeView == DataTree)
                                {
                                    if (payload.HadInlink)
                                        WzNode.GetChildNode(canvasNode, WzCanvasProperty.InlinkPropertyName)?.DeleteWzNode();
                                    if (payload.HadOutlink)
                                        WzNode.GetChildNode(canvasNode, WzCanvasProperty.OutlinkPropertyName)?.DeleteWzNode();
                                    canvasNode.ChangedNodeProperty();
                                }
                                ok = true;
                            }
                        }
                        catch
                        {
                            // This canvas only; the loop goes on.
                        }
                        if (ok) { repaired++; progress.Repaired = repaired; }
                        else { failedCount++; progress.Failed = failedCount; }
                        progress.Completed = repaired + failedCount;
                    }
                    applyMs += stopwatch.ElapsedMilliseconds - applyStart;

                    await Dispatcher.Yield(DispatcherPriority.Background);
                }

                // ---- _hash cleanup (same rule the old traversal applied at every node) ----
                if (!aborted)
                {
                    int hashSinceYield = 0;
                    foreach (WzImageProperty hashProp in hashProps)
                    {
                        if (generation != linkRepairGeneration) { aborted = true; break; }
                        try
                        {
                            if (hashProp.ParentImage?.WzFileParent?.IsUnloaded == true)
                                continue;
                            if (hashProp.HRTag is WzNode hashNode && hashNode.TreeView == DataTree)
                            {
                                hashNode.DeleteWzNode();
                            }
                            else
                            {
                                if (hashProp.ParentImage != null)
                                    hashProp.ParentImage.Changed = true;
                                hashProp.Remove();
                            }
                        }
                        catch { }
                        if (++hashSinceYield >= 512)
                        {
                            hashSinceYield = 0;
                            await Dispatcher.Yield(DispatcherPriority.Background);
                        }
                    }
                    foreach (WzNode dirHashNode in directoryHashNodes)
                    {
                        try
                        {
                            if (dirHashNode.TreeView == DataTree)
                                dirHashNode.DeleteWzNode();
                        }
                        catch { }
                    }
                }

                // The WPF mirror shows the result once - inside the batch this marks pending,
                // and the End in finally performs the single real rebuild.
                RefreshNativeDataTree();
            }
            catch (Exception ex)
            {
                failureSummary = ex.Message;
                try
                {
                    if (showDialog)
                        MessageBox.Show("補圖失敗。\n\n原因：\n" + ex.Message, Properties.Resources.Error);
                }
                catch { }
            }
            finally
            {
                if (treeBatchOpen)
                    EndNativeTreeUpdate();
                linkRepairInProgress = false;
                stopwatch.Stop();
                if (showProgress)
                    FinishLinkRepairProgress(progressTimer, generation, total, repaired, failedCount,
                        aborted, failureSummary, stopwatch.ElapsedMilliseconds);
            }

            return new LinkRepairResult
            {
                Total = total,
                Repaired = repaired,
                Failed = failedCount,
                Aborted = aborted || failureSummary != null,
                PhaseTimings = "discover=" + discoverMs + "ms prepare=" + prepareMs + "ms apply=" + applyMs + "ms",
            };
        }

        /// <summary>
        /// Structure walk on the UI thread, parsing nothing: images and directly-selected
        /// property subtrees go to the background phase; a directory-level child literally named
        /// "_hash" (the old traversal removed those too) is remembered as a node. Unmaterialized
        /// virtual directories (.ms packs) still hold their lazy placeholder and are skipped -
        /// exactly what the old node recursion did.
        /// </summary>
        private static void CollectLinkRepairRoots(WzNode node, List<WzImage> images,
            List<WzImageProperty> propertyRoots, List<WzNode> directoryHashNodes)
        {
            if (node.Tag is WzImage img)
            {
                images.Add(img);
                return;
            }
            if (node.Tag is WzImageProperty prop)
            {
                propertyRoots.Add(prop);
                return;
            }
            foreach (System.Windows.Forms.TreeNode child in node.Nodes)
                if (child is WzNode wzChild)
                    CollectLinkRepairRoots(wzChild, images, propertyRoots, directoryHashNodes);
            if (WzNode.GetChildNode(node, "_hash") is WzNode dirHash)
                directoryHashNodes.Add(dirHash);
        }

        /// <summary>
        /// Model-side mirror of the old node traversal: canvases with a link are collected (their
        /// children are not descended into, as before), every other property recurses, and a
        /// direct child named "_hash" at any visited level is collected for removal.
        /// </summary>
        private static void DiscoverLinkRepairTargets(WzImageProperty prop,
            List<WzCanvasProperty> canvases, List<WzImageProperty> hashProps)
        {
            if (prop is WzCanvasProperty canvas)
            {
                if (canvas.ContainsInlinkProperty() || canvas.ContainsOutlinkProperty())
                    canvases.Add(canvas);
                if (canvas["_hash"] is WzImageProperty canvasHash)
                    hashProps.Add(canvasHash);
                return;
            }

            // Containers only - mirroring WzNode.ParseChilds, which is what the old node
            // traversal saw. In particular a UOL's WzProperties RESOLVES the link and returns
            // the target's children; descending there would repair canvases outside the
            // selection (the old path never did).
            if (prop is not IPropertyContainer)
                return;
            var children = prop.WzProperties;
            if (children == null || children.Count == 0)
                return;
            foreach (WzImageProperty child in children.ToArray())
            {
                if (child.Name == "_hash")
                    hashProps.Add(child);
                DiscoverLinkRepairTargets(child, canvases, hashProps);
            }
        }

        // ---- link repair progress UI -------------------------------------------------------------

        /// <summary>
        /// Coalesced progress rendering: the workers only bump counters; this timer reads them at
        /// 10 Hz on the UI thread and paints the panel's own status bar. Display is delayed 200ms
        /// so a repair that finishes immediately never flashes a progress bar at all.
        /// </summary>
        private DispatcherTimer StartLinkRepairProgressTimer(LinkRepairProgress progress, int generation)
        {
            DateTime startedUtc = DateTime.UtcNow;
            bool shown = false;
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) =>
            {
                if (generation != linkRepairGeneration)
                {
                    timer.Stop();
                    return;
                }
                if (!shown)
                {
                    if ((DateTime.UtcNow - startedUtc).TotalMilliseconds < 200)
                        return;
                    shown = true;
                }
                try
                {
                    int totalNow = progress.Total;
                    if (totalNow < 0)
                    {
                        mainProgressBar.IsIndeterminate = true;
                        toolStripStatusLabel_additionalInfo.Text =
                            "補圖中：正在掃描需要補圖的節點…（已掃 "
                            + System.Threading.Volatile.Read(ref progress.ImagesScanned) + " 張 IMG）";
                    }
                    else
                    {
                        int done = progress.Completed;
                        mainProgressBar.IsIndeterminate = false;
                        mainProgressBar.Maximum = Math.Max(1, totalNow);
                        mainProgressBar.Value = Math.Min(done, totalNow);
                        int percent = totalNow == 0 ? 100 : (int)(100.0 * done / totalNow);
                        toolStripStatusLabel_additionalInfo.Text =
                            "補圖中：" + done + " / " + totalNow
                            + "（成功 " + progress.Repaired + "，失敗 " + progress.Failed + "）" + percent + "%";
                    }
                }
                catch
                {
                    // Progress is an observation layer - it must never take the repair down.
                }
            };
            timer.Start();
            return timer;
        }

        private void FinishLinkRepairProgress(DispatcherTimer timer, int generation, int total,
            int repaired, int failedCount, bool aborted, string failureSummary, long elapsedMs)
        {
            try
            {
                timer?.Stop();
                mainProgressBar.IsIndeterminate = false;
                if (failureSummary != null)
                {
                    mainProgressBar.Value = 0;
                    toolStripStatusLabel_additionalInfo.Text = "補圖失敗：" + failureSummary;
                }
                else if (aborted)
                {
                    mainProgressBar.Value = 0;
                    toolStripStatusLabel_additionalInfo.Text = "補圖已中止（檔案已卸載或關閉）。";
                }
                else if (total == 0)
                {
                    mainProgressBar.Value = 0;
                    toolStripStatusLabel_additionalInfo.Text = "沒有需要補圖的節點。";
                }
                else
                {
                    // Land on 100% with the summary, then put the bar back a few seconds later.
                    mainProgressBar.Maximum = Math.Max(1, total);
                    mainProgressBar.Value = mainProgressBar.Maximum;
                    toolStripStatusLabel_additionalInfo.Text =
                        "補圖完成：成功 " + repaired + "，失敗 " + failedCount
                        + "，耗時 " + (elapsedMs / 1000.0).ToString("0.0") + " 秒";
                    int shownGeneration = generation;
                    var resetTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(4) };
                    resetTimer.Tick += (s2, _) =>
                    {
                        ((DispatcherTimer)s2).Stop();
                        // Leave newer operations' progress alone.
                        if (shownGeneration == linkRepairGeneration && !linkRepairInProgress)
                            mainProgressBar.Value = 0;
                    };
                    resetTimer.Start();
                }
            }
            catch
            {
                // The panel may be tearing down; nothing to clean then.
            }
        }

        /// <summary>
        /// Check image node recursively, if it needs repairs for '_inlink' or '_outlink'.
        ///
        /// Resolution must run FIRST: the _inlink/_outlink string is the only pointer to the
        /// source pixels, and ResolveSingleCanvas refuses a canvas that no longer carries one.
        /// The old order deleted the child node up front - DeleteWzNode removes the underlying
        /// property too, so the resolver then found nothing to resolve, quietly copied no pixels,
        /// and the canvas was left blank with its link destroyed. Now the link is only cleaned up
        /// after the pixels are confirmed in place, and a link that cannot be resolved is left
        /// exactly as it was.
        ///
        /// Static on purpose - operates only on the nodes it is given (see ConvertImagesToBgra32
        /// for why), which also lets the regression tests drive it directly.
        /// </summary>
        public static void CheckImageNodeRecursively_linkRepair(WzNode node, ref int repaired, ref int failed) {
            if (node.Tag is WzImage img) {
                if (!img.Parsed) {
                    img.ParseImage();
                }
                node.Reparse();
            }

            if (node.Tag is WzCanvasProperty property) {
                bool hadInlink = property.ContainsInlinkProperty();
                bool hadOutlink = property.ContainsOutlinkProperty();
                if (hadInlink || hadOutlink)
                {
                    // Copies the linked pixels into this canvas and removes the _inlink/_outlink
                    // properties - but only on success.
                    if (WzLinkResolver.ResolveSingleCanvas(property))
                    {
                        // The properties are gone from the WZ; drop their now-dead tree nodes.
                        if (hadInlink)
                            WzNode.GetChildNode(node, WzCanvasProperty.InlinkPropertyName)?.DeleteWzNode();
                        if (hadOutlink)
                            WzNode.GetChildNode(node, WzCanvasProperty.OutlinkPropertyName)?.DeleteWzNode();

                        // DeleteWzNode can no longer flag the save for us - by the time it runs,
                        // the property is already detached and has no ParentImage.
                        if (property.ParentImage != null)
                            property.ParentImage.Changed = true;

                        // Updates
                        node.ChangedNodeProperty();
                        repaired++;
                    }
                    else
                    {
                        // Source pixels not found (target missing, file not loaded). Keep the
                        // link untouched rather than trading a working reference for a blank.
                        failed++;
                    }
                }
            }
            else {
                foreach (WzNode child in node.Nodes) {
                    CheckImageNodeRecursively_linkRepair(child, ref repaired, ref failed);
                }
            }
            WzNode hash = WzNode.GetChildNode(node, "_hash");
            if (hash != null) {
                hash.DeleteWzNode();
            }
        }

        /// <summary>
        /// Force-recompresses every canvas image under the selected node(s) to the uncompressed
        /// BGRA32 format (WzPngFormat.Format2), regardless of what compressed format (DXT3/DXT5/
        /// BC7/...) they currently use. Useful when pulling artwork from a source WZ that uses a
        /// compressed canvas format into a client build whose renderer only understands BGRA32.
        /// Decoding a compressed canvas back to BGRA32 does not lose any information beyond what
        /// the original compression already discarded; canvases that are already BGRA32 are left
        /// untouched.
        /// </summary>
        /// <summary>
        /// Static and node-scoped on purpose: this is invoked from the tree's right-click menu,
        /// which (see ContextMenuManager.GetNodes) is bound to whichever node was actually
        /// clicked - not to "the" MainPanel instance. HaRepacker can have several tabs open at
        /// once, each with its own MainPanel/DataTree, but only ONE ContextMenuManager is ever
        /// constructed (MainForm.cs) and it captures a single fixed MainPanel reference at
        /// startup. Reading DataTree.SelectedNodes here would silently operate on whichever tab
        /// was active when that ContextMenuManager was built, regardless of which tab the user
        /// is actually looking at when they click this menu item. Taking the target nodes as a
        /// parameter (instead of reaching for `this.DataTree`) sidesteps that entirely.
        /// </summary>
        public static void ConvertImagesToBgra32(IEnumerable<WzNode> nodes)
        {
            List<WzNode> nodeList = nodes as List<WzNode> ?? new List<WzNode>(nodes);
            int convertedCount = 0;
            int skippedCount = 0;
            DateTime t0 = DateTime.Now;
            foreach (WzNode node in nodeList)
            {
                ConvertImageNodeRecursively_toBgra32(node, ref convertedCount, ref skippedCount);
            }

            double ms = (DateTime.Now - t0).TotalMilliseconds;
            MessageBox.Show(string.Format(UiLocalization.Translate("Completed.\nElapsed time: {0} ms (average: {1})"), ms, ms / Math.Max(1, nodeList.Count))
                + "\n" + convertedCount + " 個已轉換為 BGRA32，" + skippedCount + " 個略過（已是 BGRA32 或沒有圖片資料）。");
        }

        /// <summary>
        /// Walks a node and its descendants, converting every WzCanvasProperty found to BGRA32.
        /// </summary>
        private static void ConvertImageNodeRecursively_toBgra32(WzNode node, ref int convertedCount, ref int skippedCount)
        {
            if (node.Tag is WzImage img)
            {
                if (!img.Parsed)
                {
                    img.ParseImage();
                }
                node.Reparse();
            }

            if (node.Tag is WzCanvasProperty property)
            {
                if (ConvertCanvasToBgra32(property.PngProperty))
                {
                    convertedCount++;
                    node.ChangedNodeProperty();
                }
                else
                {
                    skippedCount++;
                }
            }
            else
            {
                foreach (WzNode child in node.Nodes)
                {
                    ConvertImageNodeRecursively_toBgra32(child, ref convertedCount, ref skippedCount);
                }
            }
        }

        /// <summary>
        /// Prompts for a percentage, then resizes every canvas image under the given node(s) to
        /// that percentage of its current size. The canvas's 'origin' anchor (if present) is
        /// scaled by the same ratio, so this canvas still lines up correctly when composited
        /// with other frames/parts (e.g. equips against a body, effect frames) after resizing -
        /// otherwise the anchor would still point at the pre-resize pixel coordinates and the
        /// combined image would visibly shift. Static and node-scoped for the same reason as
        /// ConvertImagesToBgra32: see that method's remarks.
        /// </summary>
        public static void ResizeImagesByPercent(IEnumerable<WzNode> nodes)
        {
            ResizeImagesByPercentCore(nodes, false);
        }

        /// <summary>
        /// Resize and force the result to uncompressed BGRA32 in a single pass. Running the two
        /// batch actions one after the other would decode and re-encode every canvas twice,
        /// compounding the quality loss; doing both here keeps it to one round trip.
        /// </summary>
        public static void ResizeImagesByPercentToBgra32(IEnumerable<WzNode> nodes)
        {
            ResizeImagesByPercentCore(nodes, true);
        }

        private static void ResizeImagesByPercentCore(IEnumerable<WzNode> nodes, bool forceBgra32)
        {
            string title = forceBgra32 ? "縮小圖片並轉 BGRA32 (%)" : "縮小圖片 (%)";
            if (!IntInputBox.Show(title, null, 50, out _, out int? percent, bHideNameInputBox: true))
                return;
            if (percent == null || percent.Value <= 0)
            {
                MessageBox.Show("請輸入大於 0 的百分比。");
                return;
            }
            float scale = percent.Value / 100f;

            List<WzNode> nodeList = nodes as List<WzNode> ?? new List<WzNode>(nodes);
            int resizedCount = 0;
            int skippedCount = 0;
            DateTime t0 = DateTime.Now;
            foreach (WzNode node in nodeList)
            {
                ResizeImageNodeRecursively(node, scale, forceBgra32, ref resizedCount, ref skippedCount);
            }

            double ms = (DateTime.Now - t0).TotalMilliseconds;
            MessageBox.Show(string.Format(UiLocalization.Translate("Completed.\nElapsed time: {0} ms (average: {1})"), ms, ms / Math.Max(1, nodeList.Count))
                + "\n" + resizedCount + " 個已縮放為 " + percent.Value + "%"
                + (forceBgra32 ? "並轉為 BGRA32" : "") + "，" + skippedCount + " 個略過（沒有圖片資料）。");
        }

        /// <summary>
        /// Walks a node and its descendants, resizing every WzCanvasProperty found.
        /// </summary>
        private static void ResizeImageNodeRecursively(WzNode node, float scale, bool forceBgra32, ref int resizedCount, ref int skippedCount)
        {
            if (node.Tag is WzImage img)
            {
                if (!img.Parsed)
                {
                    img.ParseImage();
                }
                node.Reparse();
            }

            if (node.Tag is WzCanvasProperty property)
            {
                if (ResizeCanvasByScale(property, scale, forceBgra32))
                {
                    resizedCount++;
                    node.ChangedNodeProperty();
                }
                else
                {
                    skippedCount++;
                }
            }
            else
            {
                foreach (WzNode child in node.Nodes)
                {
                    ResizeImageNodeRecursively(child, scale, forceBgra32, ref resizedCount, ref skippedCount);
                }
            }
        }

        /// <summary>
        /// Resizes a single canvas's pixel data by the given scale (e.g. 0.5 = 50%), and scales
        /// its 'origin' anchor (if present) by the same ratio. Returns false (no changes made) if
        /// the canvas has no image data.
        /// </summary>
        private static bool ResizeCanvasByScale(WzCanvasProperty canvas, float scale, bool forceBgra32)
        {
            WzPngProperty png = canvas.PngProperty;
            if (png == null)
                return false;

            using (Bitmap original = png.GetImage(false))
            {
                if (original == null)
                    return false;

                int newWidth = Math.Max(1, (int)Math.Round(original.Width * scale));
                int newHeight = Math.Max(1, (int)Math.Round(original.Height * scale));

                using (Bitmap resized = new Bitmap(newWidth, newHeight))
                {
                    using (Graphics g = Graphics.FromImage(resized))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                        g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                        g.DrawImage(original, 0, 0, newWidth, newHeight);
                    }

                    if (forceBgra32)
                    {
                        // Encode straight to uncompressed BGRA32 instead of letting the setter
                        // pick a format, and store it as plain zlib so the bytes are readable
                        // whatever encryption the file uses.
                        (MapleLib.WzLib.WzProperties.WzPngFormat newFormat, byte[] rawBytes) =
                            MapleLib.Helpers.PngUtility.CompressImageToPngFormat(resized, Microsoft.Xna.Framework.Graphics.SurfaceFormat.Bgra32);
                        png.SetCompressedBytes(DeflateCompressWithZlibHeader(rawBytes), resized.Width, resized.Height, newFormat);
                    }
                    else
                    {
                        // Not converting to BGRA32, so the canvas keeps whatever format it had -
                        // a resize must not silently re-encode it the way PngProperty.PNG would.
                        SetCanvasBitmapPreservingFormat(canvas, resized);
                    }
                }
            }

            // Scale the 'origin' anchor by the same ratio, if present. Written directly against
            // the WzVectorProperty (instead of via WzCanvasProperty.SetCanvasOriginPosition)
            // because that helper's existing zero-check ("X != 0 && Y != 0") incorrectly throws
            // for a legitimate origin whose X or Y happens to be exactly 0, which is common
            // (e.g. an origin that's a pure vertical offset).
            WzVectorProperty originProp = (WzVectorProperty)canvas[WzCanvasProperty.OriginPropertyName];
            if (originProp != null)
            {
                originProp.X.SetValue(originProp.X.Value * scale);
                originProp.Y.SetValue(originProp.Y.Value * scale);
            }

            return true;
        }

        /// <summary>
        /// Re-encodes a single canvas's pixel data as WzPngFormat.Format2 (uncompressed BGRA32).
        /// Returns false (no changes made) if the canvas is already BGRA32 or has no image data.
        /// </summary>
        private static bool ConvertCanvasToBgra32(MapleLib.WzLib.WzProperties.WzPngProperty png)
        {
            if (png == null || png.Format == MapleLib.WzLib.WzProperties.WzPngFormat.Format2)
                return false;

            using (Bitmap decoded = png.GetImage(false))
            {
                if (decoded == null)
                    return false;

                (MapleLib.WzLib.WzProperties.WzPngFormat newFormat, byte[] rawBytes) =
                    MapleLib.Helpers.PngUtility.CompressImageToPngFormat(decoded, Microsoft.Xna.Framework.Graphics.SurfaceFormat.Bgra32);

                // Always store plain zlib, even for canvases that were originally in the
                // XOR-masked list.wz shape. The reader detects which shape it is from the two
                // header bytes, so plain zlib is read back correctly either way - whereas
                // re-applying the mask needs the file's own encryption key, and guessing it
                // wrong writes data that can never be decoded again.
                byte[] finalBytes = DeflateCompressWithZlibHeader(rawBytes);
                png.SetCompressedBytes(finalBytes, decoded.Width, decoded.Height, newFormat);
                return true;
            }
        }

        /// <summary>
        /// Mirrors WzPngProperty's internal zlib-header + DEFLATE wrapping so the bytes handed to
        /// SetCompressedBytes are stored in the same on-disk shape as the rest of the WZ file.
        /// </summary>
        private static byte[] DeflateCompressWithZlibHeader(byte[] decompressedBuffer)
        {
            using (MemoryStream memStream = new MemoryStream())
            {
                memStream.WriteByte(0x78);
                memStream.WriteByte(0x9C);
                using (System.IO.Compression.DeflateStream zip = new System.IO.Compression.DeflateStream(memStream, System.IO.Compression.CompressionMode.Compress, true))
                {
                    zip.Write(decompressedBuffer, 0, decompressedBuffer.Length);
                }
                return memStream.ToArray();
            }
        }

        /// <summary>
        /// AI Upscale all image currently in the selected node
        /// by 4x with AI, then down-scale it by 50%.
        /// 
        /// if there is an 'origin' x & y coordinate in the WzNode, update that by x2
        /// <param name="downscaleFactor">The factor to downscale the image after upscaling.  0.5 = 50%, 0.375 = 37.5%</param>
        /// </summary>
        public async void AiBatchImageUpscaleEdit(float downscaleFactorAfter) {
            const float SCALE_UP_FACTOR = 4; // faactor to scale up to with neural networks

            // Reset progress bar
            mainProgressBar.Value = 20; // 20% at the start
            secondaryProgressBar.Value = 0;

            // disable inputs in the main UI from the user.
            gridMain.IsEnabled = false;

            Dispatcher currentDispatcher = this.Dispatcher;

            await Task.Run(async () => {
                // Image key = <image path>.GetHashCode().ToString()
                Dictionary<string, Tuple<Bitmap, WzCanvasProperty, WzNode>> toUpscaleImageDictionary = new Dictionary<string, Tuple<Bitmap, WzCanvasProperty, WzNode>>();

                // handle multiple nodes...
                int nodeCount = DataTree.SelectedNodes.Count;
                DateTime t0 = DateTime.Now;
                foreach (WzNode node in DataTree.SelectedNodes) {
                    UpscaleImageNodesRecursively(node, toUpscaleImageDictionary, currentDispatcher);
                }

                // Save all of the bitmap to a folder
                const string FILE_IN = "HaRepacker_ImageUpscaleInput";
                const string FILE_OUT = "HaRepacker_ImageUpscaleOutput";

                string pathIn = System.IO.Path.Combine(System.IO.Path.GetTempPath(), FILE_IN + "_" + new Random().Next().ToString()); // random folder in case multiple instances are running.
                string pathOut = System.IO.Path.Combine(System.IO.Path.GetTempPath(), FILE_OUT + "_" + new Random().Next().ToString()); // random folder in case multiple instances are running.

                try {
                    if (Directory.Exists(pathIn)) { // clear existing first
                        Directory.Delete(pathIn, true);
                    }
                    if (Directory.Exists(pathOut)) { // clear existing first
                        Directory.Delete(pathOut, true);
                    }
                    Directory.CreateDirectory(pathIn);
                    Directory.CreateDirectory(pathOut);

                    foreach (var kvp in toUpscaleImageDictionary) {
                        string fileName = System.IO.Path.GetFileName(kvp.Key) + ".png";
                        string filePath = System.IO.Path.Combine(pathIn, fileName);

                        kvp.Value.Item1.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
                    }

                    // Upscale all image saved in the folder
                    await RealESRGAN_AI_Upscale.EsrganNcnn.Run(pathIn, pathOut, (int) SCALE_UP_FACTOR);

                    // Update main progress bar to 50% once AI upscaling is done
                    mainProgressBar.Dispatcher.BeginInvoke(new Action(() => {
                        mainProgressBar.Value = 50;
                    }));


                    foreach (KeyValuePair<string, Tuple<Bitmap, WzCanvasProperty, WzNode>> img in toUpscaleImageDictionary) {
                        string fileName = System.IO.Path.GetFileName(img.Key) + ".png";
                        string filePath = System.IO.Path.Combine(pathOut, fileName);

                        // Update secondary progress bar
                        // at the beginning of this loop, it should be 30%
                        // 60% once image is loaded
                        // then 90% once it is done downscaling image
                        secondaryProgressBar.Dispatcher.BeginInvoke(new Action(() => {
                            secondaryProgressBar.Value = 30;
                        }));

                        // Get the bitmap from the output folder
                        using (System.Drawing.Bitmap originalBitmap = new System.Drawing.Bitmap(filePath)) {

                            byte[] bitmapBytes;
                            if (downscaleFactorAfter != 1) { // re-sizing is not necessary if its the same

                                // Calculate new dimensions (50% of original)
                                int newWidth = (int)(originalBitmap.Width * downscaleFactorAfter);
                                int newHeight = (int)(originalBitmap.Height * downscaleFactorAfter);

                                // Create a new bitmap with the reduced size
                                using (System.Drawing.Bitmap downscaledBitmap = new System.Drawing.Bitmap(newWidth, newHeight)) {
                                    // Update secondary progress bar
                                    // at the beginning of this loop, it should be 30%
                                    // 60% once image is loaded
                                    // then 90% once it is done downscaling image
                                    secondaryProgressBar.Dispatcher.BeginInvoke(new Action(() => {
                                        secondaryProgressBar.Value = 60;
                                    }));

                                    // Use high quality downscaling
                                    using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(downscaledBitmap)) {
                                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                                        g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;

                                        g.DrawImage(originalBitmap, 0, 0, newWidth, newHeight);
                                    }

                                    // Update secondary progress bar
                                    // at the beginning of this loop, it should be 30%
                                    // 60% once image is loaded
                                    // then 90% once it is done downscaling image
                                    secondaryProgressBar.Dispatcher.BeginInvoke(new Action(() => {
                                        secondaryProgressBar.Value = 90;
                                    }));

                                    // Convert downscaled Bitmap to byte array
                                    using (MemoryStream ms = new MemoryStream()) {
                                        downscaledBitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                                        bitmapBytes = ms.ToArray();
                                    }
                                }
                            }
                            else {
                                // Update secondary progress bar
                                // at the beginning of this loop, it should be 30%
                                // 60% once image is loaded
                                // then 90% once it is done downscaling image
                                secondaryProgressBar.Dispatcher.BeginInvoke(new Action(() => {
                                    secondaryProgressBar.Value = 90;
                                }));

                                // Convert downscaled Bitmap to byte array
                                using (MemoryStream ms = new MemoryStream()) {
                                    originalBitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                                    bitmapBytes = ms.ToArray();
                                }
                            }

                            // Create a new Bitmap from the byte array
                            using (MemoryStream ms = new MemoryStream(bitmapBytes)) {
                                System.Drawing.Bitmap newBitmap = new System.Drawing.Bitmap(ms);

                                img.Value.Item2.PngProperty.PNG = newBitmap;

                                // Update 'origin' x/y if it exists.
                                // Written directly against the WzVectorProperty (instead of via
                                // WzCanvasProperty.SetCanvasOriginPosition) for the same reason
                                // ResizeCanvasByScale is above: that helper's zero-check
                                // ("X != 0 && Y != 0") incorrectly treats a legitimate origin whose
                                // X or Y is exactly 0 as "no origin", so a 0-anchored origin (e.g.
                                // a pure vertical/horizontal offset) never scales with the image.
                                WzVectorProperty originProp = (WzVectorProperty)img.Value.Item2[WzCanvasProperty.OriginPropertyName];
                                if (originProp != null) {
                                    // 4 * 0.25 = 1, 4 * 0.5 = 2
                                    float originScale = SCALE_UP_FACTOR * downscaleFactorAfter;
                                    originProp.X.SetValue(originProp.X.Value * originScale);
                                    originProp.Y.SetValue(originProp.Y.Value * originScale);
                                }

                                // Update 'changed'
                                img.Value.Item3.ChangedNodeProperty();
                            }
                        }
                        // for each image completed thereafter, add main progress bar
                        mainProgressBar.Dispatcher.BeginInvoke(new Action(() => {
                            mainProgressBar.Value += (50d / (double) toUpscaleImageDictionary.Count);
                        }));
                    }

                    double ms_runtime = (DateTime.Now - t0).TotalSeconds;

                    MessageBox.Show(string.Format(UiLocalization.Translate("Completed.\nElapsed time: {0} seconds (average: {1})"), ms_runtime.ToString("N2"), (ms_runtime / nodeCount).ToString("N2")));
                }
                catch (Exception exp) {
                    MessageBox.Show(UiLocalization.Translate("Error"), string.Format(UiLocalization.Translate("Unable to upscale image:\n{0}"), exp));
                }
                finally {
                    await canvasPropBox.Dispatcher.BeginInvoke(new Action(() => {
                        RefreshSelectedImageToImageRenderviewer(DataTree.SelectedNode.Tag, canvasPropBox);
                    }));
                    await gridMain.Dispatcher.BeginInvoke(new Action(() => {
                        // Reset progress bar
                        mainProgressBar.Value = 0;
                        secondaryProgressBar.Value = 0;

                        gridMain.IsEnabled = true; // disable inputs in the main UI from the user.
                    }));

                    // Clean-up
                    if (Directory.Exists(pathIn)) { // clear existing first
                        Directory.Delete(pathIn, true);
                    }
                    if (Directory.Exists(pathOut)) { // clear existing first
                        Directory.Delete(pathOut, true);
                    }

                    toUpscaleImageDictionary.Clear();
                    GC.Collect();
                }
            });
        }


        /// <summary>
        /// AI Upscale all image currently in the selected node (internal)
        /// </summary>
        /// <param name="node"></param>
        /// <param name="toUpscaleImageDictionary"></param>
        /// <param name="currentDispatcher">Main thread dispatcher</param>
        private void UpscaleImageNodesRecursively(WzNode node, Dictionary<string, Tuple<Bitmap, WzCanvasProperty, WzNode>> toUpscaleImageDictionary,
            Dispatcher currentDispatcher) {
            if (node == null || node.Tag == null) {
                return;
            }
            if (node.Tag is WzImage img) {
                if (!img.Parsed) {
                    currentDispatcher.BeginInvoke(new Action(() => {
                        img.ParseImage();
                    }));
                }
                currentDispatcher.BeginInvoke(new Action(() => {
                    node.Reparse();
                }));
            }

            if (node.Tag is WzCanvasProperty property) {
                WzImageProperty linkedTarget = property.GetLinkedWzImageProperty();
                if (!property.ContainsInlinkProperty() && !property.ContainsOutlinkProperty()) // skip link properties
                {
                    string key = property.FullPath.GetHashCode().ToString(); // happens when multiple nodes are selected while expanded
                    if (!toUpscaleImageDictionary.ContainsKey(key)) {
                        Bitmap bitmap = linkedTarget.GetBitmap();

                        toUpscaleImageDictionary.Add(property.FullPath.GetHashCode().ToString(), new Tuple<Bitmap, WzCanvasProperty, WzNode>(bitmap, property, node));
                    }
                }
            }
            else {
                foreach (WzNode child in node.Nodes) {
                    UpscaleImageNodesRecursively(child, toUpscaleImageDictionary, currentDispatcher);
                }
            }
        }
        #endregion

        #region Batch Edit (node / string / folder-image batch tools)

        /// <summary>
        /// Generic prompt: N labelled text fields plus an optional checkbox, built entirely from
        /// code. No designer, no .resx and no BAML, so the whole feature can be transplanted into
        /// an already-compiled assembly without touching its resource streams. The buttons carry
        /// a DialogResult instead of a Click handler, which keeps this method free of any
        /// compiler-generated closure class.
        /// </summary>
        private static bool BatchPrompt(string title, string[] labels, string[] defaults, string checkBoxText, out string[] values, out bool isChecked)
        {
            values = null;
            isChecked = false;

            const int rowHeight = 32;
            int rows = labels.Length;
            int checkBoxHeight = checkBoxText == null ? 0 : 28;

            System.Windows.Forms.Form form = new System.Windows.Forms.Form();
            form.Text = title;
            form.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            form.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            form.MinimizeBox = false;
            form.MaximizeBox = false;
            form.ShowInTaskbar = false;
            form.ClientSize = new System.Drawing.Size(460, 20 + rows * rowHeight + checkBoxHeight + 52);

            System.Windows.Forms.TextBox[] boxes = new System.Windows.Forms.TextBox[rows];
            for (int i = 0; i < rows; i++)
            {
                System.Windows.Forms.Label label = new System.Windows.Forms.Label();
                label.Text = labels[i];
                label.SetBounds(14, 17 + i * rowHeight, 165, 22);
                form.Controls.Add(label);

                System.Windows.Forms.TextBox box = new System.Windows.Forms.TextBox();
                if (defaults != null && i < defaults.Length && defaults[i] != null)
                {
                    box.Text = defaults[i];
                }
                box.SetBounds(185, 14 + i * rowHeight, 255, 24);
                form.Controls.Add(box);
                boxes[i] = box;
            }

            System.Windows.Forms.CheckBox checkBox = null;
            if (checkBoxText != null)
            {
                checkBox = new System.Windows.Forms.CheckBox();
                checkBox.Text = checkBoxText;
                checkBox.SetBounds(185, 17 + rows * rowHeight, 255, 22);
                form.Controls.Add(checkBox);
            }

            int buttonTop = 24 + rows * rowHeight + checkBoxHeight;
            System.Windows.Forms.Button okButton = new System.Windows.Forms.Button();
            okButton.Text = "確定";
            okButton.DialogResult = System.Windows.Forms.DialogResult.OK;
            okButton.SetBounds(260, buttonTop, 85, 28);
            form.Controls.Add(okButton);

            System.Windows.Forms.Button cancelButton = new System.Windows.Forms.Button();
            cancelButton.Text = "取消";
            cancelButton.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            cancelButton.SetBounds(355, buttonTop, 85, 28);
            form.Controls.Add(cancelButton);

            form.AcceptButton = okButton;
            form.CancelButton = cancelButton;

            bool accepted;
            try
            {
                accepted = form.ShowDialog() == System.Windows.Forms.DialogResult.OK;
                if (accepted)
                {
                    string[] result = new string[rows];
                    for (int i = 0; i < rows; i++)
                    {
                        result[i] = boxes[i].Text;
                    }
                    values = result;
                    isChecked = checkBox != null && checkBox.Checked;
                }
            }
            finally
            {
                form.Dispose();
            }
            return accepted;
        }

        private static bool BatchConfirm(string text)
        {
            return System.Windows.Forms.MessageBox.Show(text, "確認",
                System.Windows.Forms.MessageBoxButtons.YesNo,
                System.Windows.Forms.MessageBoxIcon.Warning) == System.Windows.Forms.DialogResult.Yes;
        }

        private static void BatchInfo(string text)
        {
            System.Windows.Forms.MessageBox.Show(text, "批次處理",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Information);
        }

        /// <summary>
        /// The tree's multi-selection, filtered down to real WzNodes. DataTree.SelectedNodes is
        /// an untyped ArrayList and can also hold the lazy-load placeholder TreeNode.
        /// </summary>
        private WzNode[] GetSelectedBatchNodes()
        {
            List<WzNode> selected = new List<WzNode>();
            foreach (object item in DataTree.SelectedNodes)
            {
                if (item is WzNode node)
                {
                    selected.Add(node);
                }
            }
            return selected.ToArray();
        }

        /// <summary>
        /// A WzImage node only materialises its children once the image has been parsed; batch
        /// walks have to force that or they silently see an empty subtree.
        /// </summary>
        private static void BatchEnsureParsed(WzNode node)
        {
            if (node.Tag is WzImage image)
            {
                if (!image.Parsed)
                {
                    image.ParseImage();
                }
                if (node.Nodes.Count == 0 && image.WzProperties.Count > 0)
                {
                    node.Reparse();
                }
            }
        }

        #region 技能節點批量修改

        /// <summary>
        /// Walks everything under the selection and writes a running number into the value of
        /// every node whose name matches. The number starts at 起始數值 and, after each hit,
        /// advances by 遞增/倍率值 - added when 遞增模式 is ticked, multiplied otherwise.
        /// </summary>
        public void BatchSetValuesByNodeName()
        {
            WzNode[] selected = GetSelectedBatchNodes();
            if (selected.Length == 0)
            {
                BatchInfo("請先在樹狀圖選取要處理的節點。");
                return;
            }

            string[] labels = new string[] { "節點名稱:", "起始數值:", "遞增/倍率值:" };
            string[] defaults = new string[] { "", "0", "1" };
            string[] values;
            bool addMode;
            if (!BatchPrompt("技能節點批量修改", labels, defaults, "遞增模式（數值用加的，不勾選則用乘的）", out values, out addMode))
                return;

            string targetName = values[0].Trim();
            if (targetName.Length == 0)
            {
                BatchInfo("請輸入節點名稱。");
                return;
            }

            double running;
            if (!double.TryParse(values[1].Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out running))
            {
                BatchInfo("起始數值必須是數字。");
                return;
            }

            double rate;
            if (!double.TryParse(values[2].Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out rate))
            {
                BatchInfo("遞增/倍率值必須是數字。");
                return;
            }

            int changed = 0;
            int skipped = 0;
            for (int i = 0; i < selected.Length; i++)
            {
                BatchSetValueRecursive(selected[i], targetName, ref running, rate, addMode, ref changed, ref skipped);
            }

            string skipText = skipped > 0 ? "，" + skipped + " 個型別不支援已略過" : "";
            BatchInfo("已修改 " + changed + " 個「" + targetName + "」節點" + skipText + "。\n下一個數值會是 " + BatchFormatNumber(running) + "。");
        }

        private static void BatchSetValueRecursive(WzNode node, string targetName, ref double running, double rate, bool addMode, ref int changed, ref int skipped)
        {
            BatchEnsureParsed(node);

            if (string.Equals(node.Text, targetName, StringComparison.Ordinal))
            {
                if (BatchApplyScalarValue(node, BatchFormatNumber(running)))
                {
                    changed++;
                    running = addMode ? running + rate : running * rate;
                }
                else
                {
                    skipped++;
                }
            }

            foreach (System.Windows.Forms.TreeNode child in node.Nodes)
            {
                if (child is WzNode wzChild)
                {
                    BatchSetValueRecursive(wzChild, targetName, ref running, rate, addMode, ref changed, ref skipped);
                }
            }
        }

        /// <summary>
        /// Whole numbers are written without a decimal point, so an int property does not end up
        /// rejecting a value that only differs by a trailing ".0".
        /// </summary>
        private static string BatchFormatNumber(double value)
        {
            if (value == Math.Floor(value) && Math.Abs(value) < 9.2E+18)
            {
                return ((long)value).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return value.ToString("0.##########", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool BatchApplyScalarValue(WzNode node, string text)
        {
            if (node.Tag is not WzObject obj)
                return false;

            if (obj is WzStringProperty stringProperty)
            {
                if (stringProperty.IsSpineAtlasResources)
                    return false;
                stringProperty.Value = text;
            }
            else if (obj is WzIntProperty intProperty)
            {
                int parsed;
                if (!int.TryParse(text, out parsed))
                    return false;
                intProperty.Value = parsed;
            }
            else if (obj is WzLongProperty longProperty)
            {
                long parsed;
                if (!long.TryParse(text, out parsed))
                    return false;
                longProperty.Value = parsed;
            }
            else if (obj is WzShortProperty shortProperty)
            {
                short parsed;
                if (!short.TryParse(text, out parsed))
                    return false;
                shortProperty.Value = parsed;
            }
            else if (obj is WzFloatProperty floatProperty)
            {
                float parsed;
                if (!float.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed))
                    return false;
                floatProperty.Value = parsed;
            }
            else if (obj is WzDoubleProperty doubleProperty)
            {
                double parsed;
                if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed))
                    return false;
                doubleProperty.Value = parsed;
            }
            else if (obj is WzUOLProperty uolProperty)
            {
                uolProperty.Value = text;
            }
            else
            {
                return false;
            }

            node.ChangedNodeProperty();
            return true;
        }

        #endregion

        #region 批量刪除節點

        /// <summary>
        /// Deletes every node under the selection whose name is one of the comma separated names.
        /// A matched node is not descended into, so a parent and its own child are never both
        /// queued for deletion.
        /// </summary>
        public void BatchDeleteNodesByName()
        {
            WzNode[] selected = GetSelectedBatchNodes();
            if (selected.Length == 0)
            {
                BatchInfo("請先在樹狀圖選取要處理的節點。");
                return;
            }

            string[] labels = new string[] { "節點名稱（逗號分隔）:" };
            string[] values;
            bool unused;
            if (!BatchPrompt("批量刪除節點", labels, null, null, out values, out unused))
                return;

            List<string> names = new List<string>();
            string[] rawNames = values[0].Split(',');
            for (int i = 0; i < rawNames.Length; i++)
            {
                string name = rawNames[i].Trim();
                if (name.Length > 0 && !names.Contains(name))
                {
                    names.Add(name);
                }
            }
            if (names.Count == 0)
            {
                BatchInfo("請至少輸入一個節點名稱。");
                return;
            }

            List<WzNode> found = new List<WzNode>();
            for (int i = 0; i < selected.Length; i++)
            {
                BatchCollectNodesByName(selected[i], names, found);
            }

            if (found.Count == 0)
            {
                BatchInfo("選取範圍內找不到符合的節點。");
                return;
            }
            if (!BatchConfirm("將刪除 " + found.Count + " 個節點，確定要繼續嗎？"))
                return;

            for (int i = 0; i < found.Count; i++)
            {
                found[i].DeleteWzNode();
            }
            BatchInfo("已刪除 " + found.Count + " 個節點。");
        }

        private static void BatchCollectNodesByName(WzNode node, List<string> names, List<WzNode> found)
        {
            BatchEnsureParsed(node);

            foreach (System.Windows.Forms.TreeNode child in node.Nodes)
            {
                if (child is not WzNode wzChild)
                    continue;

                if (names.Contains(wzChild.Text))
                {
                    found.Add(wzChild);
                    continue; // do not descend into a node that is about to be deleted
                }
                BatchCollectNodesByName(wzChild, names, found);
            }
        }

        #endregion

        #region 批量更改節點（數字位移）

        /// <summary>
        /// Shifts the numeric name of every selected node by a fixed offset, keeping the original
        /// digit count (zero padded) and any ".img" suffix. e.g. +100 turns "0002.img" into
        /// "0102.img".
        /// </summary>
        public void BatchOffsetNodeNames()
        {
            WzNode[] selected = GetSelectedBatchNodes();
            if (selected.Length == 0)
            {
                BatchInfo("請先在樹狀圖選取要處理的節點。");
                return;
            }

            string[] labels = new string[] { "位移量（可為負數）:" };
            string[] defaults = new string[] { "0" };
            string[] values;
            bool unused;
            if (!BatchPrompt("批量更改節點（數字位移）", labels, defaults, null, out values, out unused))
                return;

            long offset;
            if (!long.TryParse(values[0].Trim(), out offset))
            {
                BatchInfo("位移量必須是整數。");
                return;
            }

            // Renaming in place can collide with a sibling that has not been shifted yet, so
            // work out every new name first and reject the whole batch if any of them clashes.
            List<WzNode> targets = new List<WzNode>();
            List<string> newNames = new List<string>();
            int skipped = 0;
            for (int i = 0; i < selected.Length; i++)
            {
                WzNode node = selected[i];
                string newName = BatchShiftNumericName(node.Text, offset);
                if (newName == null)
                {
                    skipped++;
                    continue;
                }
                targets.Add(node);
                newNames.Add(newName);
            }

            if (targets.Count == 0)
            {
                BatchInfo("選取的節點名稱都不是數字，沒有可以處理的項目。");
                return;
            }

            string collision = BatchFindRenameCollision(targets, newNames);
            if (collision != null)
            {
                BatchInfo("名稱衝突：「" + collision + "」已經存在，請調整位移量。沒有做任何變更。");
                return;
            }

            // Rename in an order that never writes onto a name still owned by a pending node:
            // ascending offsets are applied from the highest name down, and vice versa.
            bool descending = offset > 0;
            BatchSortRenameOrder(targets, newNames, descending);

            for (int i = 0; i < targets.Count; i++)
            {
                targets[i].ChangeName(newNames[i]);
            }

            string skipText = skipped > 0 ? "，" + skipped + " 個非數字名稱已略過" : "";
            BatchInfo("已重新命名 " + targets.Count + " 個節點" + skipText + "。");
        }

        /// <summary>
        /// "0002.img" + 100 -> "0102.img". Returns null when the name is not numeric.
        /// </summary>
        private static string BatchShiftNumericName(string name, long offset)
        {
            const string imgSuffix = ".img";
            bool hasImgSuffix = name.EndsWith(imgSuffix, StringComparison.OrdinalIgnoreCase);
            string digits = hasImgSuffix ? name.Substring(0, name.Length - imgSuffix.Length) : name;

            long parsed;
            if (digits.Length == 0 || !long.TryParse(digits, out parsed))
                return null;

            long shifted = parsed + offset;
            if (shifted < 0)
                return null;

            string shiftedText = shifted.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (shiftedText.Length < digits.Length)
            {
                shiftedText = shiftedText.PadLeft(digits.Length, '0');
            }
            return hasImgSuffix ? shiftedText + name.Substring(name.Length - imgSuffix.Length) : shiftedText;
        }

        /// <summary>
        /// Returns the first new name that already belongs to a sibling which is NOT itself being
        /// renamed, or null when the batch is safe to apply.
        /// </summary>
        private static string BatchFindRenameCollision(List<WzNode> targets, List<string> newNames)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                System.Windows.Forms.TreeNode parent = targets[i].Parent;
                if (parent == null)
                    continue;

                foreach (System.Windows.Forms.TreeNode sibling in parent.Nodes)
                {
                    if (sibling is not WzNode wzSibling)
                        continue;
                    if (!string.Equals(wzSibling.Text, newNames[i], StringComparison.Ordinal))
                        continue;
                    if (targets.Contains(wzSibling))
                        continue; // it is moving out of the way too
                    return newNames[i];
                }
            }
            return null;
        }

        private static void BatchSortRenameOrder(List<WzNode> targets, List<string> newNames, bool descending)
        {
            // Small selections: a plain insertion sort keeps this free of comparison delegates.
            for (int i = 1; i < targets.Count; i++)
            {
                WzNode node = targets[i];
                string newName = newNames[i];
                int j = i - 1;
                while (j >= 0 && BatchNameOrderIsAfter(newNames[j], newName, descending))
                {
                    targets[j + 1] = targets[j];
                    newNames[j + 1] = newNames[j];
                    j--;
                }
                targets[j + 1] = node;
                newNames[j + 1] = newName;
            }
        }

        private static bool BatchNameOrderIsAfter(string left, string right, bool descending)
        {
            int compare = string.CompareOrdinal(left, right);
            return descending ? compare < 0 : compare > 0;
        }

        #endregion

        #region 批量替換字符 / 批量替換&刪除節點

        /// <summary>
        /// Replaces a substring in node names and in WzStringProperty values, everywhere under
        /// the selection.
        /// </summary>
        public void BatchReplaceText()
        {
            BatchReplaceTextCore("批量替換字符", false);
        }

        /// <summary>
        /// Same as <see cref="BatchReplaceText"/>, except that leaving 新字串 empty deletes every
        /// matching node instead of renaming it.
        /// </summary>
        public void BatchReplaceOrDeleteText()
        {
            BatchReplaceTextCore("批量替換&刪除節點", true);
        }

        private void BatchReplaceTextCore(string title, bool deleteWhenNewTextEmpty)
        {
            WzNode[] selected = GetSelectedBatchNodes();
            if (selected.Length == 0)
            {
                BatchInfo("請先在樹狀圖選取要處理的節點。");
                return;
            }

            string emptyHint = deleteWhenNewTextEmpty ? "新字串（留空 = 刪除該節點）:" : "新字串:";
            string[] labels = new string[] { "舊字串:", emptyHint };
            string[] values;
            bool unused;
            if (!BatchPrompt(title, labels, null, null, out values, out unused))
                return;

            string oldText = values[0];
            string newText = values[1];
            if (oldText.Length == 0)
            {
                BatchInfo("請輸入舊字串。");
                return;
            }

            bool deleting = deleteWhenNewTextEmpty && newText.Length == 0;
            if (!deleting && newText.Length == 0)
            {
                BatchInfo("請輸入新字串。");
                return;
            }

            if (deleting && !BatchConfirm("新字串留空：包含「" + oldText + "」的節點會被刪除，確定要繼續嗎？"))
                return;

            int renamed = 0;
            int deleted = 0;
            List<WzNode> pendingDeletes = new List<WzNode>();
            for (int i = 0; i < selected.Length; i++)
            {
                BatchReplaceRecursive(selected[i], oldText, newText, deleting, ref renamed, pendingDeletes);
            }
            for (int i = 0; i < pendingDeletes.Count; i++)
            {
                pendingDeletes[i].DeleteWzNode();
                deleted++;
            }

            if (deleting)
            {
                BatchInfo("已刪除 " + deleted + " 個節點。");
            }
            else
            {
                BatchInfo("已替換 " + renamed + " 處（節點名稱與字串值）。");
            }
        }

        private static void BatchReplaceRecursive(WzNode node, string oldText, string newText, bool deleting, ref int renamed, List<WzNode> pendingDeletes)
        {
            BatchEnsureParsed(node);

            foreach (System.Windows.Forms.TreeNode child in node.Nodes)
            {
                if (child is not WzNode wzChild)
                    continue;

                bool nameMatches = wzChild.Text.Contains(oldText);
                bool valueMatches = false;
                if (wzChild.Tag is WzStringProperty stringProperty &&
                    !stringProperty.IsSpineAtlasResources &&
                    stringProperty.Value != null &&
                    stringProperty.Value.Contains(oldText))
                {
                    valueMatches = true;
                }

                if (nameMatches || valueMatches)
                {
                    if (deleting)
                    {
                        pendingDeletes.Add(wzChild);
                        continue; // do not descend into a node that is about to be deleted
                    }

                    if (nameMatches)
                    {
                        wzChild.ChangeName(wzChild.Text.Replace(oldText, newText));
                        renamed++;
                    }
                    if (valueMatches)
                    {
                        WzStringProperty target = (WzStringProperty)wzChild.Tag;
                        target.Value = target.Value.Replace(oldText, newText);
                        wzChild.ChangedNodeProperty();
                        renamed++;
                    }
                }

                BatchReplaceRecursive(wzChild, oldText, newText, deleting, ref renamed, pendingDeletes);
            }
        }

        #endregion

        #region String移除多餘 + 自動補缺少

        /// <summary>
        /// Reconciles String.wz against the item/equip IDs that actually exist under the
        /// selection: entries in String.wz with no matching item are removed, and IDs with no
        /// String.wz entry get an empty placeholder added. Both halves are computed as a dry run
        /// first and only applied after the user confirms the counts.
        /// </summary>
        public void BatchCleanupStringWz()
        {
            WzNode[] selected = GetSelectedBatchNodes();
            if (selected.Length == 0)
            {
                BatchInfo("請先在樹狀圖選取要處理的節點。");
                return;
            }

            string scanRoot = selected[0].FullPath;
            bool itemWz = scanRoot.IndexOf("Item", StringComparison.OrdinalIgnoreCase) >= 0;
            bool characterWz = scanRoot.IndexOf("Character", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!itemWz && !characterWz)
            {
                BatchInfo("此功能只支援在 Item.wz 或 Character.wz 底下的節點使用。\n目前選取的是：" + scanRoot);
                return;
            }

            List<WzFile> stringFiles = BatchGetStringWzFiles();
            if (stringFiles.Count == 0)
            {
                BatchInfo("找不到已載入的 String.wz，請先開啟它。");
                return;
            }

            List<int> ids = new List<int>();
            for (int i = 0; i < selected.Length; i++)
            {
                BatchCollectItemIds(selected[i], ids);
            }
            if (ids.Count == 0)
            {
                BatchInfo("選取範圍內沒有找到任何道具 ID（1000000 ~ 6000000）。");
                return;
            }
            ids.Sort();

            List<WzObject> orphans = new List<WzObject>();
            for (int i = 0; i < stringFiles.Count; i++)
            {
                BatchScanStringDirectory(stringFiles[i].WzDirectory, ids, itemWz, characterWz, orphans);
            }

            List<int> missing = new List<int>();
            List<IPropertyContainer> missingParents = new List<IPropertyContainer>();
            for (int i = 0; i < ids.Count; i++)
            {
                int itemId = ids[i];
                string category = BatchGetStringCategory(itemId);
                if (category == null)
                    continue;

                string idText = itemId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (BatchResolveInStringWz(stringFiles, category + "/" + idText) != null)
                    continue;

                if (BatchResolveInStringWz(stringFiles, category) is IPropertyContainer parent)
                {
                    missing.Add(itemId);
                    missingParents.Add(parent);
                }
            }

            if (orphans.Count == 0 && missing.Count == 0)
            {
                BatchInfo("String.wz 已經和選取範圍一致，沒有需要處理的項目。\n掃描到的道具 ID：" + ids.Count + " 個。");
                return;
            }

            string summary = "掃描到 " + ids.Count + " 個道具 ID。\n\n"
                + "將從 String.wz 移除 " + orphans.Count + " 筆多餘資料\n"
                + "將補上 " + missing.Count + " 筆缺少的資料\n\n"
                + "此操作會直接修改 String.wz，確定要繼續嗎？";
            if (!BatchConfirm(summary))
                return;

            int removed = 0;
            for (int i = 0; i < orphans.Count; i++)
            {
                WzObject orphan = orphans[i];
                if (orphan is WzImageProperty imageProperty && imageProperty.ParentImage != null)
                {
                    imageProperty.ParentImage.Changed = true;
                }
                if (orphan.HRTag is WzNode treeNode)
                {
                    treeNode.Remove();
                }
                orphan.Remove();
                removed++;
            }

            int added = 0;
            for (int i = 0; i < missing.Count; i++)
            {
                string idText = missing[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
                IPropertyContainer parent = missingParents[i];
                WzSubProperty placeholder = new WzSubProperty(idText);

                if (parent is WzObject parentObject && parentObject.HRTag is WzNode parentNode)
                {
                    if (parentNode.AddObject(placeholder, UndoRedoMan) == null)
                        continue;
                }
                else
                {
                    parent.AddProperty(placeholder);
                    if (placeholder.ParentImage != null)
                    {
                        placeholder.ParentImage.Changed = true;
                    }
                }
                added++;
            }

            BatchInfo("執行完畢。\n移除 " + removed + " 筆多餘 String，新增 " + added + " 筆缺少的 String。");
        }

        private static List<WzFile> BatchGetStringWzFiles()
        {
            List<WzFile> stringFiles = new List<WzFile>();
            MapleLib.WzFileManager fileManager = Program.WzFileManager;
            if (fileManager == null)
                return stringFiles;

            foreach (WzFile file in fileManager.WzFileList)
            {
                if (file == null || file.Name == null)
                    continue;
                if (file.Name.StartsWith("String", StringComparison.OrdinalIgnoreCase))
                {
                    stringFiles.Add(file);
                }
            }
            return stringFiles;
        }

        /// <summary>
        /// Resolves a "String.wz/Eqp.img/Eqp/Cap" style path against the loaded String files.
        /// Navigating by hand rather than via WzFile.GetObjectFromPath, because that helper
        /// treats a single-segment path as "give me the root of whichever file you were called
        /// on" and would happily answer with an unrelated WZ.
        /// </summary>
        private static WzObject BatchResolveInStringWz(List<WzFile> stringFiles, string path)
        {
            string[] parts = path.Split('/');
            for (int f = 0; f < stringFiles.Count; f++)
            {
                WzObject current = stringFiles[f].WzDirectory;
                int start = 0;
                // The root directory carries the file name ("String.wz"), so skip that segment
                // when the caller included it.
                if (parts.Length > 0 && current != null &&
                    string.Equals(current.Name, parts[0], StringComparison.OrdinalIgnoreCase))
                {
                    start = 1;
                }

                for (int i = start; i < parts.Length && current != null; i++)
                {
                    current = BatchGetChildObject(current, parts[i]);
                }
                if (current != null && start < parts.Length)
                    return current;
            }
            return null;
        }

        private static WzObject BatchGetChildObject(WzObject parent, string name)
        {
            if (parent is WzDirectory directory)
                return directory[name];

            if (parent is WzImage image)
            {
                if (!image.Parsed)
                {
                    image.ParseImage();
                }
                return image[name];
            }

            if (parent is IPropertyContainer container)
                return container[name];

            return null;
        }

        private static void BatchCollectItemIds(WzNode node, List<int> ids)
        {
            BatchEnsureParsed(node);

            string text = node.Text;
            if (text.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(0, text.Length - 4);
            }
            int itemId;
            if (int.TryParse(text, out itemId) && itemId >= 1000000 && itemId <= 6000000 && !ids.Contains(itemId))
            {
                ids.Add(itemId);
            }

            foreach (System.Windows.Forms.TreeNode child in node.Nodes)
            {
                if (child is WzNode wzChild)
                {
                    BatchCollectItemIds(wzChild, ids);
                }
            }
        }

        private static void BatchScanStringDirectory(WzDirectory directory, List<int> ids, bool itemWz, bool characterWz, List<WzObject> orphans)
        {
            if (directory == null)
                return;

            foreach (WzDirectory subDirectory in directory.WzDirectories)
            {
                BatchScanStringDirectory(subDirectory, ids, itemWz, characterWz, orphans);
            }
            foreach (WzImage image in directory.WzImages)
            {
                if (!BatchIsScannedStringImage(image.Name, itemWz, characterWz))
                    continue;
                if (!image.Parsed)
                {
                    image.ParseImage();
                }
                BatchScanStringContainer(image, ids, orphans);
            }
        }

        private static void BatchScanStringContainer(IPropertyContainer container, List<int> ids, List<WzObject> orphans)
        {
            foreach (WzImageProperty property in container.WzProperties)
            {
                int itemId;
                if (int.TryParse(property.Name, out itemId) && itemId >= 1000000 && itemId <= 6000000)
                {
                    if (!ids.Contains(itemId))
                    {
                        orphans.Add(property);
                    }
                    continue; // an ID node's children are its name/desc, never more IDs
                }

                if (property is WzSubProperty subProperty)
                {
                    BatchScanStringContainer(subProperty, ids, orphans);
                }
            }
        }

        /// <summary>
        /// Which String.wz images a selection governs: an Item.wz selection owns the item string
        /// images, a Character.wz selection owns Eqp.img.
        /// </summary>
        private static bool BatchIsScannedStringImage(string imageName, bool itemWz, bool characterWz)
        {
            if (imageName == null)
                return false;

            if (itemWz)
            {
                if (string.Equals(imageName, "Cash.img", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(imageName, "Consume.img", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(imageName, "Etc.img", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(imageName, "Ins.img", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(imageName, "Pet.img", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            if (characterWz)
            {
                if (string.Equals(imageName, "Eqp.img", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The String.wz path that owns a given item ID.
        /// </summary>
        private static string BatchGetStringCategory(int itemId)
        {
            if (itemId >= 5010000)
                return "String.wz/Cash.img";
            if (itemId >= 2000000 && itemId < 3000000)
                return "String.wz/Consume.img";
            if (itemId >= 1000000 && itemId < 1010000)
                return "String.wz/Eqp.img/Eqp/Cap";
            if (itemId >= 1010000 && itemId < 1040000)
                return "String.wz/Eqp.img/Eqp/Accessory";
            if (itemId >= 1040000 && itemId < 1050000)
                return "String.wz/Eqp.img/Eqp/Coat";
            if (itemId >= 1050000 && itemId < 1060000)
                return "String.wz/Eqp.img/Eqp/Longcoat";
            if (itemId >= 1060000 && itemId < 1070000)
                return "String.wz/Eqp.img/Eqp/Pants";
            if (itemId >= 1070000 && itemId < 1080000)
                return "String.wz/Eqp.img/Eqp/Shoes";
            if (itemId >= 1080000 && itemId < 1090000)
                return "String.wz/Eqp.img/Eqp/Glove";
            if (itemId >= 1090000 && itemId < 1100000)
                return "String.wz/Eqp.img/Eqp/Shield";
            if (itemId >= 1100000 && itemId < 1110000)
                return "String.wz/Eqp.img/Eqp/Cape";
            if (itemId >= 1111000 && itemId < 1122000)
                return "String.wz/Eqp.img/Eqp/Ring";
            if (itemId >= 1122000 && itemId < 1130000)
                return "String.wz/Eqp.img/Eqp/Accessory";
            if (itemId >= 1130000 && itemId < 1132000)
                return "String.wz/Eqp.img/Eqp/Ring";
            if (itemId >= 1132000 && itemId < 1172000)
                return "String.wz/Eqp.img/Eqp/Accessory";
            if (itemId >= 1172000 && itemId < 1180000)
                return "String.wz/Eqp.img/Eqp/MonsterBook";
            if (itemId >= 1180000 && itemId < 1210000)
                return "String.wz/Eqp.img/Eqp/Accessory";
            if (itemId >= 1603000 && itemId < 1604000)
                return "String.wz/Eqp.img/Eqp/Skillskin";
            if (itemId >= 1610000 && itemId < 1660000)
                return "String.wz/Eqp.img/Eqp/Mechanic";
            if (itemId >= 1662000 && itemId < 1680000)
                return "String.wz/Eqp.img/Eqp/Android";
            if (itemId >= 1680000 && itemId < 1690000)
                return "String.wz/Eqp.img/Eqp/Bits";
            if (itemId >= 1802000 && itemId < 1820000)
                return "String.wz/Eqp.img/Eqp/PetEquip";
            if (itemId >= 1842000 && itemId < 1893000)
                return "String.wz/Eqp.img/Eqp/MonsterBattle";
            if (itemId >= 1900000 && itemId < 1940000)
                return "String.wz/Eqp.img/Eqp/Taming";
            if (itemId >= 1940000 && itemId < 1980000)
                return "String.wz/Eqp.img/Eqp/Dragon";
            if (itemId >= 1980000 && itemId < 2000000)
                return "String.wz/Eqp.img/Eqp/Taming";
            if (itemId >= 1210000 && itemId < 1800000)
                return "String.wz/Eqp.img/Eqp/Weapon";
            if (itemId >= 4000000 && itemId < 5000000)
                return "String.wz/Etc.img/Etc";
            if (itemId >= 3000000 && itemId < 4000000)
                return "String.wz/Ins.img";
            if (itemId >= 5000000 && itemId < 5010000)
                return "String.wz/Pet.img";
            return null;
        }

        #endregion

        #region 一鍵覆蓋 / 匯入資料夾圖片

        public void BatchCoverFolderImages()
        {
            BatchFolderImages(true);
        }

        public void BatchImportFolderImages()
        {
            BatchFolderImages(false);
        }

        /// <summary>
        /// Pushes a folder of images back into the WZ tree. Each file is addressed by its path
        /// RELATIVE to the chosen folder (minus the extension), so an export/edit/re-import round
        /// trip lines up - with a flat filename lookup as fallback for folders that were not
        /// produced by an export. In cover mode only canvases that already exist are touched; in
        /// import mode a missing canvas (and any missing parent) is created.
        /// </summary>
        private void BatchFolderImages(bool coverOnly)
        {
            WzNode[] selected = GetSelectedBatchNodes();
            if (selected.Length == 0)
            {
                BatchInfo("請先在樹狀圖選取要套用的節點。");
                return;
            }

            string folder;
            using (System.Windows.Forms.FolderBrowserDialog dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = coverOnly ? "選擇要覆蓋回 WZ 的圖片資料夾" : "選擇要匯入 WZ 的圖片資料夾";
                dialog.ShowNewFolderButton = false;
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK ||
                    string.IsNullOrWhiteSpace(dialog.SelectedPath))
                    return;
                folder = dialog.SelectedPath;
            }

            string[] allFiles = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
            List<string> imageFiles = new List<string>();
            for (int i = 0; i < allFiles.Length; i++)
            {
                if (BatchIsSupportedImageFile(allFiles[i]))
                {
                    imageFiles.Add(allFiles[i]);
                }
            }
            if (imageFiles.Count == 0)
            {
                BatchInfo("資料夾內沒有找到支援的圖片檔（png / bmp / jpg / gif / tif）。");
                return;
            }

            int covered = 0;
            int created = 0;
            int failed = 0;
            List<string> failedFiles = new List<string>();

            for (int i = 0; i < selected.Length; i++)
            {
                BatchEnsureParsed(selected[i]);
            }

            for (int i = 0; i < imageFiles.Count; i++)
            {
                string filePath = imageFiles[i];
                string relative = BatchTrimLeadingSeparators(filePath.Substring(folder.Length));
                string withoutExtension = BatchStripExtension(relative);
                string[] pathParts = withoutExtension.Replace('/', '\\').Split('\\');

                Bitmap bitmap = BatchLoadBitmap(filePath);
                if (bitmap == null)
                {
                    failed++;
                    if (failedFiles.Count < 10)
                    {
                        failedFiles.Add(relative);
                    }
                    continue;
                }

                bool applied = false;
                for (int n = 0; n < selected.Length && !applied; n++)
                {
                    applied = BatchApplyFolderImage(selected[n], pathParts, bitmap, coverOnly, UndoRedoMan, ref created);
                }
                // WzPngProperty.PNG compresses the bitmap on assignment and does not keep a
                // reference to it, so the Bitmap (and the MemoryStream behind it) is ours to free
                // either way.
                bitmap.Dispose();
                if (applied)
                {
                    covered++;
                }
            }

            string createdText = coverOnly ? "" : "，其中新建 " + created + " 個節點";
            string failedText = failed > 0 ? "\n" + failed + " 個檔案讀取失敗" : "";
            if (failedFiles.Count > 0)
            {
                failedText = failedText + "：\n" + string.Join("\n", failedFiles.ToArray());
            }
            BatchInfo("共處理 " + imageFiles.Count + " 個檔案，成功套用 " + covered + " 張圖片" + createdText + "。" + failedText);
        }

        private static bool BatchIsSupportedImageFile(string filePath)
        {
            string extension = Path.GetExtension(filePath);
            if (extension == null)
                return false;
            return string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".bmp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".gif", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".tif", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".tiff", StringComparison.OrdinalIgnoreCase);
        }

        private static string BatchStripExtension(string relativePath)
        {
            int dot = relativePath.LastIndexOf('.');
            int separator = Math.Max(relativePath.LastIndexOf('\\'), relativePath.LastIndexOf('/'));
            return dot > separator ? relativePath.Substring(0, dot) : relativePath;
        }

        /// <summary>
        /// TrimStart(params char[]) would compile to a char[] literal, and the C# compiler backs
        /// those with an RVA blob in <PrivateImplementationDetails> - a static data field that
        /// cannot be transplanted into an already-built assembly. Same reason Split('/', '\\\\')
        /// is written as Replace + single-char Split above.
        /// </summary>
        private static string BatchTrimLeadingSeparators(string path)
        {
            int start = 0;
            while (start < path.Length && (path[start] == '\\' || path[start] == '/'))
            {
                start++;
            }
            return start == 0 ? path : path.Substring(start);
        }

        /// <summary>
        /// Loads the file into a Bitmap that does not keep the source file locked - Image.FromFile
        /// holds the stream open for the lifetime of the bitmap.
        /// </summary>
        private static Bitmap BatchLoadBitmap(string filePath)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(filePath);
                MemoryStream stream = new MemoryStream(bytes); // owned by the Bitmap, do not dispose
                return new Bitmap(stream);
            }
            catch
            {
                return null;
            }
        }

        private static bool BatchApplyFolderImage(WzNode rootNode, string[] pathParts, Bitmap bitmap, bool coverOnly, UndoRedoManager undoMan, ref int created)
        {
            WzNode target = BatchFindNodeByPath(rootNode, pathParts);

            // Fallback for a flat folder that was not produced by an export: match on file name.
            if (target == null && pathParts.Length > 1)
            {
                target = BatchFindNodeByPath(rootNode, new string[] { pathParts[pathParts.Length - 1] });
            }

            if (target != null)
            {
                return BatchSetCanvasBitmap(target, bitmap);
            }
            if (coverOnly)
                return false;

            return BatchCreateCanvasNode(rootNode, pathParts, bitmap, undoMan, ref created);
        }

        private static WzNode BatchFindNodeByPath(WzNode root, string[] pathParts)
        {
            WzNode current = root;
            for (int i = 0; i < pathParts.Length; i++)
            {
                if (pathParts[i].Length == 0)
                    continue;

                BatchEnsureParsed(current);
                WzNode next = null;
                foreach (System.Windows.Forms.TreeNode child in current.Nodes)
                {
                    if (child is WzNode wzChild && string.Equals(wzChild.Text, pathParts[i], StringComparison.OrdinalIgnoreCase))
                    {
                        next = wzChild;
                        break;
                    }
                }
                if (next == null)
                    return null;
                current = next;
            }
            return current == root ? null : current;
        }

        private static bool BatchSetCanvasBitmap(WzNode canvasNode, Bitmap bitmap)
        {
            if (canvasNode.Tag is not WzCanvasProperty canvas)
                return false;

            // A canvas that borrows its artwork through a link has to give that up before it can
            // own a bitmap, otherwise the link keeps winning on the next read.
            if (canvas.ContainsInlinkProperty())
            {
                canvas.RemoveProperty(canvas[WzCanvasProperty.InlinkPropertyName]);
                WzNode inlinkNode = WzNode.GetChildNode(canvasNode, WzCanvasProperty.InlinkPropertyName);
                if (inlinkNode != null)
                {
                    inlinkNode.DeleteWzNode();
                }
            }
            if (canvas.ContainsOutlinkProperty())
            {
                canvas.RemoveProperty(canvas[WzCanvasProperty.OutlinkPropertyName]);
                WzNode outlinkNode = WzNode.GetChildNode(canvasNode, WzCanvasProperty.OutlinkPropertyName);
                if (outlinkNode != null)
                {
                    outlinkNode.DeleteWzNode();
                }
            }

            SetCanvasBitmapPreservingFormat(canvas, bitmap);
            canvasNode.ChangedNodeProperty();
            return true;
        }

        /// <summary>
        /// Replaces a canvas's artwork while keeping the surface format it already had.
        ///
        /// WzPngProperty.PNG runs the format detector over the new pixels and overwrites Format
        /// with whatever it guesses, so replacing a BGRA4444 icon could silently rewrite it as
        /// ARGB1555. The editor previews that correctly, but the game reads the canvas with the
        /// format it expects and draws garbage. Use ConvertCanvasToBgra32 when a format change is
        /// actually what is wanted; a plain replacement must not change it.
        /// </summary>
        private static void SetCanvasBitmapPreservingFormat(WzCanvasProperty canvas, Bitmap bitmap)
        {
            if (canvas.PngProperty == null)
            {
                // A brand new canvas has no original format to honour.
                canvas.PngProperty = new WzPngProperty();
                canvas.PngProperty.PNG = bitmap;
                return;
            }

            MapleLib.WzLib.WzProperties.WzPngFormat original = canvas.PngProperty.Format;
            Microsoft.Xna.Framework.Graphics.SurfaceFormat surface;
            bool grayscale = false;
            switch (original)
            {
                case MapleLib.WzLib.WzProperties.WzPngFormat.Format1:
                    surface = Microsoft.Xna.Framework.Graphics.SurfaceFormat.Bgra4444; break;
                case MapleLib.WzLib.WzProperties.WzPngFormat.Format2:
                    surface = Microsoft.Xna.Framework.Graphics.SurfaceFormat.Bgra32; break;
                case MapleLib.WzLib.WzProperties.WzPngFormat.Format257:
                    surface = Microsoft.Xna.Framework.Graphics.SurfaceFormat.Bgra5551; break;
                case MapleLib.WzLib.WzProperties.WzPngFormat.Format513:
                case MapleLib.WzLib.WzProperties.WzPngFormat.Format517:
                    surface = Microsoft.Xna.Framework.Graphics.SurfaceFormat.Bgr565; break;
                case MapleLib.WzLib.WzProperties.WzPngFormat.Format3:
                    surface = Microsoft.Xna.Framework.Graphics.SurfaceFormat.Dxt3; grayscale = true; break;
                case MapleLib.WzLib.WzProperties.WzPngFormat.Format1026:
                    surface = Microsoft.Xna.Framework.Graphics.SurfaceFormat.Dxt3; break;
                case MapleLib.WzLib.WzProperties.WzPngFormat.Format2050:
                    surface = Microsoft.Xna.Framework.Graphics.SurfaceFormat.Dxt5; break;
                default:
                    // A format this build cannot re-encode; the detector is the only option left.
                    canvas.PngProperty.PNG = bitmap;
                    return;
            }

            (MapleLib.WzLib.WzProperties.WzPngFormat produced, byte[] rawBytes) =
                MapleLib.Helpers.PngUtility.CompressImageToPngFormat(bitmap, surface, grayscale);
            // Plain zlib, so the bytes stay readable whatever encryption the file uses.
            canvas.PngProperty.SetCompressedBytes(DeflateCompressWithZlibHeader(rawBytes),
                bitmap.Width, bitmap.Height, produced);
        }

        private static bool BatchCreateCanvasNode(WzNode rootNode, string[] pathParts, Bitmap bitmap, UndoRedoManager undoMan, ref int created)
        {
            WzNode parent = rootNode;
            for (int i = 0; i < pathParts.Length - 1; i++)
            {
                if (pathParts[i].Length == 0)
                    continue;

                BatchEnsureParsed(parent);
                WzNode next = WzNode.GetChildNode(parent, pathParts[i]);
                if (next == null)
                {
                    if (parent.Tag is not WzImage && parent.Tag is not IPropertyContainer)
                        return false;
                    next = parent.AddObject(new WzSubProperty(pathParts[i]), undoMan);
                    if (next == null)
                        return false;
                    created++;
                }
                parent = next;
            }

            string canvasName = pathParts[pathParts.Length - 1];
            if (canvasName.Length == 0)
                return false;

            BatchEnsureParsed(parent);
            if (parent.Tag is not WzImage && parent.Tag is not IPropertyContainer)
                return false;

            WzCanvasProperty canvas = new WzCanvasProperty(canvasName);
            WzPngProperty png = new WzPngProperty();
            png.PNG = bitmap;
            canvas.PngProperty = png;

            WzNode canvasNode = parent.AddObject(canvas, undoMan);
            if (canvasNode == null)
                return false;

            canvasNode.AddObject(new WzVectorProperty(WzCanvasProperty.OriginPropertyName,
                new WzIntProperty("X", 0), new WzIntProperty("Y", 0)), undoMan);
            created++;
            return true;
        }

        #endregion

        #endregion


        #region Menu Item
        /// <summary>
        /// More option -- Shows ContextMenuStrip 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void button_MoreOption_Click(object sender, RoutedEventArgs e) {
            Button clickSrc = (Button)sender;

            clickSrc.ContextMenu.IsOpen = true;
            //  System.Windows.Forms.ContextMenuStrip contextMenu = new System.Windows.Forms.ContextMenuStrip();
            //  contextMenu.Show(clickSrc, 0, 0);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MenuItem_changeSound_Click(object sender, RoutedEventArgs e)
        {
            if (DataTree.SelectedNode.Tag is WzBinaryProperty)
            {
                System.Windows.Forms.OpenFileDialog dialog = new System.Windows.Forms.OpenFileDialog()
                {
                    Title = UiLocalization.Translate("Select the sound"),
                    Filter = UiLocalization.AudioFileDialogFilter
                };
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                WzBinaryProperty prop;
                try
                {
                    prop = new WzBinaryProperty(((WzBinaryProperty)DataTree.SelectedNode.Tag).Name, dialog.FileName);
                }
                catch
                {
                    Warning.Error(Properties.Resources.MainImageLoadError);
                    return;
                }
                IPropertyContainer parent = (IPropertyContainer)((WzBinaryProperty)DataTree.SelectedNode.Tag).Parent;
                ((WzBinaryProperty)DataTree.SelectedNode.Tag).ParentImage.Changed = true;
                ((WzBinaryProperty)DataTree.SelectedNode.Tag).Remove();
                DataTree.SelectedNode.Tag = prop;
                parent.AddProperty(prop);
                mp3Player.SoundProperty = prop;
            }
        }

        /// <summary>
        /// Saving the sound from WzSoundProperty
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MenuItem_saveSound_Click(object sender, RoutedEventArgs e)
        {
            if (!(DataTree.SelectedNode.Tag is WzBinaryProperty))
                return;
            WzBinaryProperty sound = (WzBinaryProperty)DataTree.SelectedNode.Tag;
            string extension = sound.FileExtension;
            string fileName = sound.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                ? sound.Name
                : sound.Name + extension;
            string saveTitle = sound.IsWaveFile
                ? "Select where to save the WAV file"
                : "Select where to save the MP3 file";
            string saveFilter = sound.IsWaveFile
                ? "WAV audio (*.wav)|*.wav"
                : "MP3 audio (*.mp3)|*.mp3";

            System.Windows.Forms.SaveFileDialog dialog = new System.Windows.Forms.SaveFileDialog()
            {
                FileName = fileName,
                DefaultExt = extension.TrimStart('.'),
                AddExtension = true,
                Title = UiLocalization.Translate(saveTitle),
                Filter = UiLocalization.Translate(saveFilter)
            };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return;

            sound.SaveToFile(dialog.FileName);
        }

        private void MenuItem_openAudioStudio_Click(object sender, RoutedEventArgs e)
        {
            if (DataTree.SelectedNode?.Tag is not WzBinaryProperty sound)
                return;
            var dialog = new System.Windows.Forms.SaveFileDialog
            {
                FileName = sound.Name + AudioProject.FileExtension,
                DefaultExt = "hasound.json",
                AddExtension = true,
                Filter = "HaCreator Audio Project (*.hasound.json)|*.hasound.json",
                Title = UiLocalization.Translate("Create an Audio Studio project")
            };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return;

            var project = AudioProject.Create(sound.Name);
            var track = project.AddTrack(sound.Name, AudioTrackRole.SoundEffect);
            long durationSamples = Math.Max(1L,
                (long)Math.Round(sound.Length * project.MasterFormat.SampleRate / 1000d,
                    MidpointRounding.AwayFromZero));
            track.AddClip(new AudioSourceReference
            {
                SourceKind = AudioSourceKind.NativeWz,
                SourceId = sound.FullPath,
                PropertyPath = sound.FullPath,
                FormatMetadata = new AudioClipMetadata
                {
                    DeclaredDurationMilliseconds = sound.Length,
                    PayloadSizeBytes = sound.SoundDataLength,
                    IsNativeWz = true
                }
            }, 0, durationSamples);
            project.Save(dialog.FileName);
            System.Windows.MessageBox.Show(UiLocalization.Translate("The Audio Studio project was created and can be opened in HaCreator."),
                UiLocalization.Translate("Audio Studio"), MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void MenuItem_applyAudioProject_Click(object sender, RoutedEventArgs e)
        {
            if (DataTree.SelectedNode?.Tag is not WzBinaryProperty sound)
                return;
            var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Filter = "HaCreator Audio Project (*.hasound.json)|*.hasound.json",
                DefaultExt = "hasound.json",
                Title = UiLocalization.Translate("Apply Audio Studio project")
            };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return;

            try
            {
                AudioProject project = AudioProject.Load(dialog.FileName);
                string projectDirectory = Path.GetDirectoryName(Path.GetFullPath(dialog.FileName));
                var codec = new DefaultAudioCodecProvider();
                var renderer = new AudioRenderer();
                var renderRequest = new AudioRenderRequest
                {
                    Project = project,
                    SourceResolver = async (source, cancellationToken) =>
                    {
                        if (source.SourceKind == AudioSourceKind.NativeWz)
                        {
                            // A project created from this selected node can be
                            // rendered offline without retaining a WZ path.
                            // Other native references are deliberately rejected
                            // instead of silently reading a different source.
                            string expected = sound.FullPath;
                            if (string.Equals(source.PropertyPath, expected, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(source.SourceId, expected, StringComparison.OrdinalIgnoreCase))
                                return await codec.DecodeAsync(sound, cancellationToken).ConfigureAwait(false);
                            throw new AudioCodecException(AudioDiagnosticCode.MissingSource,
                                "The project references a native WZ sound other than the selected property.");
                        }

                        return await codec.DecodeAsync(source, projectDirectory, cancellationToken).ConfigureAwait(false);
                    }
                };
                AudioEncodeResult rendered = await renderer.RenderToAsync(renderRequest, codec,
                    new AudioEncodeSettings
                    {
                        Encoding = AudioEncoding.Pcm,
                        SampleRate = project.MasterFormat.SampleRate,
                        ChannelCount = project.MasterFormat.ChannelCount,
                        BitsPerSample = 16,
                    });
                string temporaryPath = Path.Combine(Path.GetTempPath(), $"harepacker-bake-{Guid.NewGuid():N}.wav");
                try
                {
                    File.WriteAllBytes(temporaryPath, rendered.Data);
                    var baked = new WzBinaryProperty(sound.Name, temporaryPath);
                    if (sound.Parent is not IPropertyContainer parent)
                        throw new InvalidOperationException("The selected sound does not have a writable parent.");
                    WzImage parentImage = sound.ParentImage;
                    if (parentImage == null)
                        throw new InvalidOperationException("The selected sound is not attached to an image.");
                    sound.Remove();
                    parent.AddProperty(baked);
                    parentImage.Changed = true;
                    DataTree.SelectedNode.Tag = baked;
                    mp3Player.SoundProperty = baked;
                    System.Windows.MessageBox.Show(UiLocalization.Translate("The Audio Studio project was rendered and baked into the selected WZ property. Save the WZ/IMG to commit it."),
                        UiLocalization.Translate("Audio Studio"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
                finally
                {
                    try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                    catch { }
                }
            }
            catch (Exception exception)
            {
                Warning.Error(exception.Message);
            }
        }

        private async void MenuItem_exportDecodedWav_Click(object sender, RoutedEventArgs e)
        {
            if (DataTree.SelectedNode?.Tag is not WzBinaryProperty sound)
                return;
            var dialog = new System.Windows.Forms.SaveFileDialog
            {
                FileName = sound.Name + ".wav",
                DefaultExt = "wav",
                AddExtension = true,
                Filter = "WAV audio (*.wav)|*.wav",
                Title = UiLocalization.Translate("Export decoded WAV")
            };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return;
            try
            {
                byte[] bytes = sound.IsWaveFile ? sound.GetBytesForWAVPlayback() : sound.GetBytes(false);
                using var input = new MemoryStream(bytes, writable: false);
                var codec = new NAudioCodecProvider();
                AudioDecodeResult decoded = await codec.DecodeAsync(input, sound.FileExtension,
                    new AudioClipMetadata { DeclaredDurationMilliseconds = sound.Length, IsNativeWz = true });
                AudioEncodeResult encoded = await codec.EncodeAsync(decoded.Buffer, new AudioEncodeSettings
                {
                    Encoding = AudioEncoding.Pcm,
                    SampleRate = decoded.Buffer.Format.SampleRate,
                    ChannelCount = decoded.Buffer.Format.ChannelCount,
                    BitsPerSample = 16
                });
                File.WriteAllBytes(dialog.FileName, encoded.Data);
            }
            catch (Exception exception)
            {
                Warning.Error(exception.Message);
            }
        }

        private void MenuItem_audioMetadata_Click(object sender, RoutedEventArgs e)
        {
            if (DataTree.SelectedNode?.Tag is not WzBinaryProperty sound)
                return;
            string kind = sound.IsWaveFile ? "PCM WAV" : "MP3";
            string text = $"{sound.FullPath}\nEncoding: {kind}\nWZ duration: {sound.Length} ms\nPayload: {sound.SoundDataLength:N0} bytes";
            System.Windows.MessageBox.Show(text, UiLocalization.Translate("Audio metadata"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// Saving the image from WzCanvasProperty
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void menuItem_saveImage_Click(object sender, RoutedEventArgs e)
        {
            if (!(DataTree.SelectedNode.Tag is WzCanvasProperty) && !(DataTree.SelectedNode.Tag is WzUOLProperty))
            {
                return;
            }

            System.Drawing.Bitmap wzCanvasPropertyObjLocation = null;
            string fileName = string.Empty;

            if (DataTree.SelectedNode.Tag is WzCanvasProperty)
            {
                WzCanvasProperty canvas = (WzCanvasProperty)DataTree.SelectedNode.Tag;

                wzCanvasPropertyObjLocation = canvas.GetLinkedWzCanvasBitmap();
                fileName = canvas.Name;
            }
            else
            {
                WzObject linkValue = ((WzUOLProperty)DataTree.SelectedNode.Tag).LinkValue;
                if (linkValue is WzCanvasProperty)
                {
                    WzCanvasProperty canvas = (WzCanvasProperty)linkValue;

                    wzCanvasPropertyObjLocation = canvas.GetLinkedWzCanvasBitmap();
                    fileName = canvas.Name;
                }
                else
                    return;
            }
            if (wzCanvasPropertyObjLocation == null)
                return; // oops, we're fucked lulz

            System.Windows.Forms.SaveFileDialog dialog = new System.Windows.Forms.SaveFileDialog()
            {
                FileName = fileName,
                Title = UiLocalization.Translate("Select where to save the image"),
                Filter = UiLocalization.Translate("Image files|*.png;*.gif;*.bmp;*.jpg;*.tif")
            };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) 
                return;
            switch (dialog.FilterIndex)
            {
                case 1: //png
                    wzCanvasPropertyObjLocation.Save(dialog.FileName, System.Drawing.Imaging.ImageFormat.Png);
                    break;
                case 2: //gif
                    wzCanvasPropertyObjLocation.Save(dialog.FileName, System.Drawing.Imaging.ImageFormat.Gif);
                    break;
                case 3: //bmp
                    wzCanvasPropertyObjLocation.Save(dialog.FileName, System.Drawing.Imaging.ImageFormat.Bmp);
                    break;
                case 4: //jpg
                    wzCanvasPropertyObjLocation.Save(dialog.FileName, System.Drawing.Imaging.ImageFormat.Jpeg);
                    break;
                case 5: //tiff
                    wzCanvasPropertyObjLocation.Save(dialog.FileName, System.Drawing.Imaging.ImageFormat.Tiff);
                    break;
            }
        }

        /// <summary>
        /// Export .json, .atlas, as file
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void menuItem_ExportFile_Click(object sender, RoutedEventArgs e)
        {
            if (!(DataTree.SelectedNode.Tag is WzStringProperty))
            {
                return;
            }
            WzStringProperty stProperty = DataTree.SelectedNode.Tag as WzStringProperty;

            string fileName = stProperty.Name;
            string value = stProperty.Value;

            string[] fileNameSplit = fileName.Split('.');
            string fileType = fileNameSplit.Length > 1 ? fileNameSplit[fileNameSplit.Length - 1] : "txt";

            System.Windows.Forms.SaveFileDialog saveFileDialog1 = new System.Windows.Forms.SaveFileDialog()
            {
                FileName = fileName,
                Title = UiLocalization.Translate("Select where to save the file"),
                Filter = string.Format(UiLocalization.Translate("{0} files (*.{0})|*.{0}|All files (*.*)|*.*"), fileType)
            }
            ;
            if (saveFileDialog1.ShowDialog() != System.Windows.Forms.DialogResult.OK) 
                return;

            using (System.IO.FileStream fs = (System.IO.FileStream)saveFileDialog1.OpenFile())
            {
                using (StreamWriter sw = new StreamWriter(fs))
                {
                    sw.WriteLine(value);
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MenuItem_changeImage_Click(object sender, RoutedEventArgs e) {
            if (DataTree.SelectedNode.Tag is WzCanvasProperty) // only allow button click if its an image property
            {
                System.Windows.Forms.OpenFileDialog dialog = new System.Windows.Forms.OpenFileDialog() {
                    Title = UiLocalization.Translate("Select an image"),
                    Filter = UiLocalization.Translate("Image files|*.png;*.bmp;*.jpg;*.gif;*.jpeg;*.tif;*.tiff")
                };
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return;

                byte[] bitmapBytes = null;
                try {
                    using (System.Drawing.Bitmap originalBitmap = new System.Drawing.Bitmap(dialog.FileName)) {
                        using (MemoryStream ms = new MemoryStream()) {
                            originalBitmap.Save(ms, originalBitmap.RawFormat);
                            bitmapBytes = ms.ToArray();
                        }
                    }
                }
                catch {
                    Warning.Error(Properties.Resources.MainImageLoadError);
                    return;
                }
                //List<UndoRedoAction> actions = new List<UndoRedoAction>(); // Undo action

                if (bitmapBytes != null) {
                    MemoryStream ms = new MemoryStream(bitmapBytes); // dont close this
                    System.Drawing.Bitmap newBitmap = new System.Drawing.Bitmap(ms);

                    ChangeCanvasPropBoxImage(newBitmap);
                }
            }
        }

        /// <summary>
        /// Changes the displayed image in 'canvasPropBox' with a user defined input.
        /// </summary>
        /// <param name="image"></param>
        /// <param name=""></param>
        public void ChangeCanvasPropBoxImage(Bitmap bmp) {
            if (DataTree.SelectedNode.Tag is WzCanvasProperty property) {
                WzNode parentCanvasNode = (WzNode)DataTree.SelectedNode;

                WzCanvasProperty selectedWzCanvas = property;

                if (selectedWzCanvas.ContainsInlinkProperty()) // if its an inlink property, remove that before updating base image.
                {
                    selectedWzCanvas.RemoveProperty(selectedWzCanvas[WzCanvasProperty.InlinkPropertyName]);

                    WzNode childInlinkNode = WzNode.GetChildNode(parentCanvasNode, WzCanvasProperty.InlinkPropertyName);

                    // Add undo actions
                    //actions.Add(UndoRedoManager.ObjectRemoved((WzNode)parentCanvasNode, childInlinkNode));
                    childInlinkNode.DeleteWzNode(); // Delete '_inlink' node

                    // TODO: changing _Inlink image crashes
                    // Mob2.wz/9400121/hit/0
                }
                else if (selectedWzCanvas.ContainsOutlinkProperty()) // if its an inlink property, remove that before updating base image.
                {
                    selectedWzCanvas.RemoveProperty(selectedWzCanvas[WzCanvasProperty.OutlinkPropertyName]);

                    WzNode childInlinkNode = WzNode.GetChildNode(parentCanvasNode, WzCanvasProperty.OutlinkPropertyName);

                    // Add undo actions
                    //actions.Add(UndoRedoManager.ObjectRemoved((WzNode)parentCanvasNode, childInlinkNode));
                    childInlinkNode.DeleteWzNode(); // Delete '_inlink' node
                }

                // Keep the canvas's surface format: swapping an icon's artwork must not silently
                // re-encode BGRA4444 as ARGB1555, which previews fine here and breaks in-game.
                SetCanvasBitmapPreservingFormat(selectedWzCanvas, bmp);

                canvasPropBox.SetIsLoading(true);
                try {
                    canvasPropBox.BindingPropertyItem.SurfaceFormat = WzPngFormatExtensions.GetXNASurfaceFormat(selectedWzCanvas.PngProperty.Format);
                    canvasPropBox.BindingPropertyItem.Bitmap = bmp;
                    canvasPropBox.BindingPropertyItem.BitmapBackup = bmp;
                }
                finally {
                    canvasPropBox.SetIsLoading(false);
                }

                // flag changed for saving updates
                // and also node foreground color
                parentCanvasNode.ChangedNodeProperty();

                // Add undo actions
                //UndoRedoMan.AddUndoBatch(actions);
            }
        }
        #endregion

        #region Drag and Drop Image
        private bool bDragEnterActive = false;
        /// <summary>
        /// Scroll viewer drag enter
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void canvasPropBox_DragEnter(object sender, DragEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("Drag Enter");
            if (!bDragEnterActive)
            {
                bDragEnterActive = true;
            }
        }

        /// <summary>
        ///  Scroll viewer drag leave
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void canvasPropBox_DragLeave(object sender, DragEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("Drag Leave");

            bDragEnterActive = false;
        }
        /// <summary>
        /// Scroll viewer drag drop
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void canvasPropBox_Drop(object sender, DragEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("Drag Drop");
            if (bDragEnterActive && DataTree.SelectedNode.Tag is WzCanvasProperty) // only allow button click if its an image property
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files.Length == 0)
                        return;

                    System.Drawing.Bitmap bmp;
                    try
                    {
                        bmp = (System.Drawing.Bitmap)System.Drawing.Image.FromFile(files[0]);
                    }
                    catch (Exception exp)
                    {
                        return;
                    }
                    if (bmp != null)
                        ChangeCanvasPropBoxImage(bmp);

                    //List<UndoRedoAction> actions = new List<UndoRedoAction>(); // Undo action
                }
            }
        }
        #endregion

        #region Copy & Paste
        /// <summary>
        /// Clones a WZ object
        /// </summary>
        /// <param name="obj"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private WzObject CloneWzObject(WzObject obj)
        {
            if (obj is WzDirectory)
            {
                Warning.Error(Properties.Resources.MainCopyDirError);
                return null;
            }
            else if (obj is WzImage)
            {
                return ((WzImage)obj).DeepClone();
            }
            else if (obj is WzImageProperty)
            {
                return ((WzImageProperty)obj).DeepClone();
            }
            else
            {
                MapleLib.Helpers.ErrorLogger.Log(MapleLib.Helpers.ErrorLevel.MissingFeature, "The current WZ object type cannot be cloned " + obj.ToString() + " " + obj.FullPath);
                return null;
            }
        }

        /// <summary>
        /// Flag to determine if a copy task is currently active.
        /// </summary>
        private bool
            bPasteTaskActive = false;

        /// <summary>
        /// Copies from the selected Wz object
        /// </summary>
        public void DoCopy()
        {
            if (!Warning.Warn(Properties.Resources.MainConfirmCopy) || bPasteTaskActive)
                return;

            // "Last Ctrl+C wins": a whole-node tree copy always cancels a pending field copy
            // staged in the property editor, so a following Ctrl+V goes back to this tree paste
            // instead of trying to apply stale field values - see the editor pass-throughs below
            // and MainForm.MainWindow_PreviewKeyDown. Placed after the confirmation above, so
            // answering No to 是否複製 leaves the field copy alone too.
            nodeEditorPanel?.ClearCopiedFields();

            foreach (WzObject obj in clipboard)
            {
                //this causes minor weirdness with png's in copied nodes but otherwise memory is not free'd
                obj.Dispose();
            }

            clipboard.Clear();

            // Remember which container the copy came out of, so a paste can land in the matching
            // container on the other side - see RedirectPasteTarget. Only kept when every copied
            // node shares one parent name; a mixed selection has no single right answer.
            clipboardParentName = null;
            bool firstCopiedNode = true;

            foreach (WzNode node in DataTree.SelectedNodes)
            {
                WzObject clone = CloneWzObject((WzObject)((WzNode)node).Tag);
                if (clone != null)
                    clipboard.Add(clone);

                string parentName = ((WzNode)node).Parent == null ? null : ((WzNode)node).Parent.Text;
                if (firstCopiedNode)
                {
                    clipboardParentName = parentName;
                    firstCopiedNode = false;
                }
                else if (!string.Equals(clipboardParentName, parentName, StringComparison.Ordinal))
                {
                    clipboardParentName = null;
                }
            }
        }

        // ---- property-editor field copy/paste, as seen by MainForm's Ctrl+C / Ctrl+V ------------
        //
        // Pure pass-throughs to the node editor panel. MainForm decides which of the two
        // clipboards a keystroke belongs to and shows the matching MainConfirmCopy /
        // MainConfirmPaste prompt itself; DoCopy/DoPaste below still prompt for the tree's own
        // clipboard exactly as they always did, so a keystroke never prompts twice.

        /// <summary>
        /// True when the property editor has selected field rows, i.e. Ctrl+C means "copy those
        /// fields" rather than "copy the selected tree node".
        /// </summary>
        public bool HasSelectedEditorFields => nodeEditorPanel?.HasSelectedFields == true;

        /// <summary>
        /// True when a property-editor field copy is staged, i.e. Ctrl+V means "paste those field
        /// values" rather than "paste the tree clipboard".
        /// </summary>
        public bool HasCopiedEditorFields => nodeEditorPanel?.HasCopiedFields == true;

        /// <summary>
        /// True when keyboard focus is inside one of the property editor's value boxes.
        ///
        /// No longer used for Ctrl+C/Ctrl+V routing: MainForm now checks for *any* focused
        /// TextBox (see ClipboardShortcutPolicy), which covers these boxes along with the node
        /// header's 名稱 / 值 / X / Y and the find box. Kept because it answers a narrower
        /// question than that check does.
        /// </summary>
        public bool IsNodeEditorValueBoxFocused => nodeEditorPanel?.IsValueTextBoxFocused == true;

        /// <summary>Copies the property editor's selected fields. Caller confirms first.</summary>
        public void CopySelectedEditorFields() => nodeEditorPanel?.CopySelectedFieldsShortcut();

        /// <summary>
        /// Applies the staged field copy onto the current node's matching card, writing the
        /// values straight into their WZ properties - a confirmed Ctrl+V is the commit, so
        /// 儲存數值 isn't needed afterwards. Caller confirms first.
        ///
        /// Reddens exactly the leaf properties the paste wrote - pasting price onto
        /// 02020001\info\price marks `price`, never `info`, the item, or a sibling the paste
        /// didn't touch. A value the property's type rejected isn't in the returned list, so a
        /// half-successful paste only reddens the half that landed.
        /// </summary>
        public void PasteCopiedEditorFields()
        {
            if (nodeEditorPanel == null)
                return;

            // Every selected node is a paste target, not just the active one - selecting
            // 02000001..02000005 and hitting Ctrl+V writes all five. Snapshotted up front, and
            // the paste works straight on the WzObjects, so the selection and the active node are
            // never disturbed: no re-selecting, no panel rebuild, no scrolling.
            WzNode[] targetNodes = GetSelectedBatchNodes();
            if (targetNodes.Length == 0 && DataTree.SelectedNode is WzNode activeNode)
                targetNodes = new WzNode[] { activeNode };

            List<WzObject> targets = new List<WzObject>(targetNodes.Length);
            foreach (WzNode node in targetNodes)
            {
                if (node.Tag is WzObject target)
                    targets.Add(target);
            }

            IReadOnlyList<WzImageProperty> changedProperties = nodeEditorPanel.PasteCopiedFieldsToTargets(targets);
            if (changedProperties.Count == 0)
                return;

            bool marked = false;
            foreach (WzImageProperty property in changedProperties)
            {
                // Every WzNode registers itself on its WzObject (WzNode.ParseChilds sets
                // SourceObject.HRTag), and the editor only ever shows properties of an already
                // parsed image, so the node for each written property exists here. Nothing to
                // mark if it somehow doesn't - the WZ write itself already happened, and
                // ParentImage.Changed is set regardless, so the file still saves correctly.
                if (property.HRTag is not WzNode propertyNode)
                    continue;

                propertyNode.ChangedNodeProperty();
                marked = true;
            }

            if (marked)
            {
                // Repaints foregrounds from the model for the items that actually exist in the
                // WPF mirror, instead of rebuilding the tree. A property under a collapsed parent
                // has no TreeViewItem yet; its WzNode is marked all the same, and
                // CreateNativeTreeItem applies the red when the user expands it later.
                RefreshNativeNodeForegrounds();
            }
        }

        /// <summary>
        /// The name of the node the clipboard contents were copied out of, or null when the
        /// selection spanned several different parents.
        /// </summary>
        private static string clipboardParentName;

        /// <summary>
        /// Sends a paste one level deeper when the target has a child matching the container the
        /// copy came from.
        ///
        /// Item artwork lives at &lt;id&gt;\info\icon, so copying an icon and pasting it onto another
        /// item id would otherwise drop it at &lt;id&gt;\icon, where the game never looks. With this,
        /// selecting the item ids and pasting puts each icon inside that item's own 'info'.
        /// Nothing happens when the target is already that container, or has no such child.
        /// </summary>
        private WzNode RedirectPasteTarget(WzNode target)
        {
            if (target == null || string.IsNullOrEmpty(clipboardParentName))
                return target;
            if (string.Equals(target.Text, clipboardParentName, StringComparison.Ordinal))
                return target;

            BatchEnsureParsed(target);
            WzNode child = WzNode.GetChildNode(target, clipboardParentName);
            if (child == null)
                return target;

            // Only descend into something that can actually hold the pasted properties.
            BatchEnsureParsed(child);
            if (child.Tag is not IPropertyContainer && child.Tag is not WzImage)
                return target;
            return child;
        }

        private ReplaceResult replaceBoxResult = ReplaceResult.NoneSelectedYet;

        /// <summary>
        /// Paste to the selected WzObject
        /// </summary>
        /// <summary>
        /// Pastes the clipboard into EVERY selected node, not just the active one - copying a
        /// property and then selecting level 1..30 pastes it into all thirty. The replace answer
        /// lives in a field on purpose, so one "yes/no to all" covers every target instead of
        /// asking once per target.
        /// </summary>
        public void DoPaste()
        {
            if (clipboard.Count == 0)
            {
                BatchInfo("剪貼簿是空的，請先複製節點。");
                return;
            }
            if (!Warning.Warn(Properties.Resources.MainConfirmPaste))
                return;

            WzNode[] targets = GetSelectedBatchNodes();
            if (targets.Length == 0 && DataTree.SelectedNode is WzNode activeNode)
                targets = new WzNode[] { activeNode };
            if (targets.Length == 0)
            {
                BatchInfo("請先選取要貼上的目標節點。");
                return;
            }

            bPasteTaskActive = true;
            using var pasteTreeBatch = NativeTreeUpdateScope(); // one rebuild per paste, however many targets
            try
            {
                // Reset replace option
                replaceBoxResult = ReplaceResult.NoneSelectedYet;

                int pasted = 0;
                for (int i = 0; i < targets.Length; i++)
                {
                    pasted += PasteIntoNode(RedirectPasteTarget(targets[i]));
                }

                // Deliberately silent on success, however many targets: the pasted nodes turn red
                // in the tree, which is the feedback. Only a paste that achieved nothing is worth
                // a dialog, because otherwise the user has no way to tell it was rejected.
                if (pasted == 0)
                {
                    BatchInfo("沒有貼上任何節點 - 選取的目標可能無法容納剪貼簿裡的類型。");
                }
            }
            finally
            {
                bPasteTaskActive = false;
            }
        }

        /// <summary>
        /// Pastes the clipboard into one target. Returns how many nodes were actually inserted.
        /// </summary>
        private int PasteIntoNode(WzNode parent)
        {
            if (parent == null || parent.Tag is not WzObject)
                return 0;

            if (parent.Tag is WzImage && parent.Nodes.Count == 0)
            {
                ParseOnDataTreeSelectedItem(parent); // only parse the main node.
            }

            WzObject parentObj = (WzObject)parent.Tag;
            if (parentObj is WzFile)
                parentObj = ((WzFile)parentObj).WzDirectory;

            int pasted = 0;
            bool bNoToAllComplete = false;
            foreach (WzObject obj in clipboard)
            {
                if (((obj is WzDirectory || obj is WzImage) && parentObj is WzDirectory) || (obj is WzImageProperty && parentObj is IPropertyContainer))
                {
                    WzObject clone = CloneWzObject(obj);
                    if (clone == null)
                        continue;

                    WzNode node = new WzNode(clone, true);
                    WzNode child = WzNode.GetChildNode(parent, node.Text);
                    if (child != null) // A Child already exist
                    {
                        if (replaceBoxResult == ReplaceResult.NoneSelectedYet)
                        {
                            ReplaceBox.Show(node.Text, out replaceBoxResult);
                        }

                        switch (replaceBoxResult)
                        {
                            case ReplaceResult.No: // Skip just this
                                replaceBoxResult = ReplaceResult.NoneSelectedYet; // reset after use
                                break;

                            case ReplaceResult.Yes: // Replace just this
                                child.DeleteWzNode();
                                if (parent.AddNode(node, false))
                                    pasted++;
                                replaceBoxResult = ReplaceResult.NoneSelectedYet; // reset after use
                                break;

                            case ReplaceResult.NoToAll:
                                bNoToAllComplete = true;
                                break;

                            case ReplaceResult.YesToAll:
                                child.DeleteWzNode();
                                if (parent.AddNode(node, false))
                                    pasted++;
                                break;
                        }

                        if (bNoToAllComplete)
                            break;
                    }
                    else // not not in this 
                    {
                        if (parent.AddNode(node, false))
                            pasted++;
                    }
                }
            }
            return pasted;
        }
        #endregion

        #region UI layout
        /// <summary>
        /// Shows the selected data treeview object to UI
        /// </summary>
        /// <param name="obj"></param>
        /// <summary>
        /// The skill preview panel, created on first use and parked in grid1 alongside the
        /// other mutually-exclusive editors. Building it here rather than in MainPanel.xaml
        /// keeps the compiled XAML untouched.
        /// </summary>
        private SkillPreview.SkillPreviewPanel skillPreviewPanel;

        /// <summary>
        /// The inline field editor for an ordinary WZ entity. Parked into the same container as
        /// the skill preview and driven from the same place, so exactly one of them is visible.
        /// </summary>
        private SkillPreview.NodeEditorPanel nodeEditorPanel;

        /// <summary>
        /// The WorldMap visual editor, parked in the same container as the other editors. Takes
        /// priority over them: a WorldMap*.img is also an ordinary image, so without this it
        /// would fall through to the generic panels.
        /// </summary>
        private HaRepacker.GUI.WorldMap.WorldMapEditorPanel worldMapEditorPanel;

        private bool ShowWorldMapEditorIfApplicable(WzObject obj)
        {
            if (!HaRepacker.GUI.WorldMap.WorldMapDetector.IsWorldMapImage(obj))
            {
                if (worldMapEditorPanel != null)
                    worldMapEditorPanel.Visibility = Visibility.Collapsed;
                return false;
            }

            if (worldMapEditorPanel == null)
            {
                worldMapEditorPanel = new HaRepacker.GUI.WorldMap.WorldMapEditorPanel();
                worldMapEditorPanel.Visibility = Visibility.Collapsed;
                worldMapEditorPanel.PropertiesChanged += WorldMapEditor_PropertiesChanged;
                worldMapEditorPanel.NavigationRequested += WorldMapEditor_NavigationRequested;
                worldMapEditorPanel.WorldMapSiblingProvider = GetWorldMapSiblings;
                // Adding a mapNo goes through WzNode.AddObject, which registers on the app's
                // existing undo stack rather than a WorldMap-specific one.
                worldMapEditorPanel.UndoRedoMan = undoRedoMan;
                grid1.Children.Add(worldMapEditorPanel);
            }

            bool loaded = worldMapEditorPanel.TryLoad(obj);
            worldMapEditorPanel.Visibility = loaded ? Visibility.Visible : Visibility.Collapsed;
            return loaded;
        }

        /// <summary>
        /// Reddens exactly the leaf properties a WorldMap edit wrote - the spot vector, a type, a
        /// mapNo entry - never their MapList / mapNo parents. Same mechanism the property editor's
        /// paste uses; no second changed-colour system.
        /// </summary>
        private void WorldMapEditor_PropertiesChanged(object sender, IReadOnlyList<WzImageProperty> changedProperties)
        {
            if (changedProperties == null || changedProperties.Count == 0)
                return;

            bool marked = false;
            foreach (WzImageProperty property in changedProperties)
            {
                if (property?.HRTag is not WzNode propertyNode)
                    continue;

                propertyNode.ChangedNodeProperty();
                marked = true;
            }

            // Repaints the items that exist in the WPF mirror. Deliberately not
            // RefreshNativeDataTree() - dragging a spot must not rebuild the whole tree.
            if (marked)
                RefreshNativeNodeForegrounds();
        }

        /// <summary>
        /// Moves the tree onto the requested WorldMap image so both halves stay in step - the
        /// editor never swaps its own picture behind the tree's back. Reuses the normal selection
        /// path, which then re-runs ShowObjectValue and reloads the editor.
        /// </summary>
        private void WorldMapEditor_NavigationRequested(object sender, string targetImageName)
        {
            if (string.IsNullOrEmpty(targetImageName) || DataTree.SelectedNode is not WzNode currentNode)
                return;

            if (currentNode.Parent is not WzNode directoryNode)
                return;

            foreach (WzNode sibling in directoryNode.Nodes.OfType<WzNode>())
            {
                if (!string.Equals(sibling.Text, targetImageName, StringComparison.OrdinalIgnoreCase))
                    continue;

                // The target may still be a lightweight IMG reference; resolve and parse it the
                // same way double-clicking would before handing it to the editor.
                TryResolveImgFilesystemImageNode(sibling, out _);
                if (sibling.Tag is WzImage image && !image.Parsed)
                    ParseOnDataTreeSelectedItem(sibling, false);

                SelectAndRevealNativeNode(sibling);
                return;
            }

            BatchInfo("在同一個 WorldMap 目錄中找不到 " + targetImageName + "。");
        }

        /// <summary>
        /// The WorldMap images sitting alongside the one being edited, for the double-click
        /// forward match. Walks the tree rather than the WzDirectory so lazily-referenced IMG
        /// files are included, and touches only this directory - never the rest of the WZ.
        /// </summary>
        private IReadOnlyList<WzImage> GetWorldMapSiblings()
        {
            var siblings = new List<WzImage>();
            if (DataTree.SelectedNode is not WzNode currentNode || currentNode.Parent is not WzNode directoryNode)
                return siblings;

            foreach (WzNode sibling in directoryNode.Nodes.OfType<WzNode>())
            {
                if (sibling.Text == null || !sibling.Text.StartsWith(
                        HaRepacker.GUI.WorldMap.WorldMapDetector.WorldMapDirectoryName, StringComparison.OrdinalIgnoreCase))
                    continue;

                TryResolveImgFilesystemImageNode(sibling, out _);
                if (sibling.Tag is WzImage image)
                    siblings.Add(image);
            }
            return siblings;
        }

        private void ShowNodeEditorIfApplicable(WzObject obj)
        {
            if (skillPreviewPanel != null && skillPreviewPanel.Visibility == Visibility.Visible)
            {
                if (nodeEditorPanel != null)
                    nodeEditorPanel.Visibility = Visibility.Collapsed;
                return;
            }

            if (nodeEditorPanel == null)
            {
                nodeEditorPanel = new SkillPreview.NodeEditorPanel();
                nodeEditorPanel.Visibility = Visibility.Collapsed;
                grid1.Children.Add(nodeEditorPanel);
            }

            nodeEditorPanel.Visibility = nodeEditorPanel.TryLoad(obj, Program.WzFileManager)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void ShowSkillPreviewIfApplicable(WzObject obj)
        {
            if (skillPreviewPanel == null)
            {
                skillPreviewPanel = new SkillPreview.SkillPreviewPanel();
                skillPreviewPanel.Visibility = Visibility.Collapsed;
                grid1.Children.Add(skillPreviewPanel);
            }

            bool isSkill = skillPreviewPanel.TryLoad(obj, Program.WzFileManager);
            if (isSkill)
            {
                skillPreviewPanel.Visibility = Visibility.Visible;
            }
            else
            {
                skillPreviewPanel.Visibility = Visibility.Collapsed;
                skillPreviewPanel.StopPlayback();
            }
        }

        private void ShowObjectValue(WzObject obj)
        {
            if (obj.WzFileParent != null && obj.WzFileParent.IsUnloaded) // this WZ is already unloaded from memory, dont attempt to display it (when the user clicks "reload" button while selection is on that)
                return;

            isLoading = true;

            try {
                mp3Player.SoundProperty = null;

                // Set file name binding
                _bindingPropertyItem.WzFileName = obj.Name;

                toolStripStatusLabel_additionalInfo.Text = "-"; // Reset additional info to default
                if (isSelectingWzMapFieldLimit) // previously already selected. update again
                {
                    isSelectingWzMapFieldLimit = false;
                }

                // Canvas animation
                if (DataTree.SelectedNodes.Count <= 1)
                {
                }
                else
                {
                    bool bIsAllCanvas = true;
                    // check if everything selected is WzUOLProperty and WzCanvasProperty
                    foreach (WzNode tree in DataTree.SelectedNodes)
                    {
                        WzObject wzobj = (WzObject)tree.Tag;
                        if (!(wzobj is WzUOLProperty) && !(wzobj is WzCanvasProperty))
                        {
                            bIsAllCanvas = false;
                            break;
                        }
                    }
                }

                // Set default layout collapsed state
                mp3Player.Visibility = Visibility.Collapsed;

                // Button collapsed state
                menuItem_changeImage.Visibility = Visibility.Collapsed;
                menuItem_saveImage.Visibility = Visibility.Collapsed;
                menuItem_changeSound.Visibility = Visibility.Collapsed;
                menuItem_saveSound.Visibility = Visibility.Collapsed;
                menuItem_openAudioStudio.Visibility = Visibility.Collapsed;
                menuItem_applyAudioProject.Visibility = Visibility.Collapsed;
                menuItem_exportDecodedWav.Visibility = Visibility.Collapsed;
                menuItem_audioMetadata.Visibility = Visibility.Collapsed;
                menuItem_exportFile.Visibility = Visibility.Collapsed;

                // Canvas collapsed state
                canvasPropBox.Visibility = Visibility.Collapsed;

                // Value`
                _bindingPropertyItem.WzFileValue = string.Empty;
                _bindingPropertyItem.ChangeReadOnlyAttribute(true, _bindingPropertyItem, o => o.IsWzValueReadOnly, o => o.WzFileValue);

                // Field limit panel Map.wz/../fieldLimit
                fieldLimitPanelHost.Visibility = Visibility.Collapsed;
                // fieldType panel Map.wz/../fieldType
                fieldTypePanel.Visibility = Visibility.Collapsed;

                // Vector panel
                //_bindingPropertyItem.XYVector = new NotifyPointF(0, 0);
                _bindingPropertyItem.ChangeReadOnlyAttribute(true, _bindingPropertyItem, o => o.IsXYPanelReadOnly, o => o.XYVector);

                // Avalon Text editor
                textEditor.Visibility = Visibility.Collapsed;

                // WorldMap*.img under a WorldMap directory gets the visual world map editor, and
                // it outranks the others: a world map is also an ordinary image, so letting the
                // generic panels see it first would hide the map behind a field list.
                if (ShowWorldMapEditorIfApplicable(obj))
                {
                    if (skillPreviewPanel != null)
                    {
                        skillPreviewPanel.Visibility = Visibility.Collapsed;
                        skillPreviewPanel.StopPlayback();
                    }
                    if (nodeEditorPanel != null)
                        nodeEditorPanel.Visibility = Visibility.Collapsed;
                }
                else
                {
                    // Skill range / effect preview - shown in place of the other editors whenever
                    // the selection resolves to a skill (a node owning a "level" container).
                    ShowSkillPreviewIfApplicable(obj);

                    // Ordinary entities (an item code, a mob, an npc) get the inline field editor.
                    // Runs after the skill check so a skill keeps its own richer panel.
                    ShowNodeEditorIfApplicable(obj);
                }

                // vars
                bool bIsWzFile = obj is WzFile file;
                bool bIsWzDirectory = obj is WzDirectory;
                bool bIsWzImage = obj is WzImage;
                bool bIsWzLuaProperty = obj is WzLuaProperty;
                bool bIsWzSoundProperty = obj is WzBinaryProperty;
                bool bIsWzStringProperty = obj is WzStringProperty;
                bool bIsWzIntProperty = obj is WzIntProperty;
                bool bIsWzLongProperty = obj is WzLongProperty;
                bool bIsWzDoubleProperty = obj is WzDoubleProperty;
                bool bIsWzFloatProperty = obj is WzFloatProperty;
                bool bIsWzShortProperty = obj is WzShortProperty;
                bool bIsWzNullProperty = obj is WzNullProperty;
                bool bIsWzSubProperty = obj is WzSubProperty;
                bool bIsWzConvexProperty = obj is WzConvexProperty;

                bool bAnimateMoreButton = false; // The button to animate when there is more option under button_MoreOption

                // Set layout visibility
                if (bIsWzFile || bIsWzDirectory || bIsWzImage || bIsWzNullProperty || bIsWzSubProperty || bIsWzConvexProperty) {
                    /*if (obj is WzSubProperty) { // detect String.wz/Npc.img/ directory for AI related tools
                         if (obj.Parent.Name == "Npc.img") 
                         {
                             WzObject wzObj = obj.GetTopMostWzDirectory();
                             if (wzObj.Name == "String.wz" || (wzObj.Name.StartsWith("String") && wzObj.Name.EndsWith(".wz"))) 
                             {
                             }
                         }
                     }*/

                    if (bIsWzFile) {
                        _bindingPropertyItem.WzFileValue = (obj as WzFile).Header.Copyright;
                        _bindingPropertyItem.ChangeReadOnlyAttribute(false, _bindingPropertyItem, o => o.IsWzValueReadOnly, o => o.WzFileValue); // dont allow user to change fieldLimit manually
                    }
                }
                else if (obj is WzCanvasProperty canvasProp) {
                    bAnimateMoreButton = true; // flag

                    menuItem_changeImage.Visibility = Visibility.Visible;
                    menuItem_saveImage.Visibility = Visibility.Visible;

                    // Image
                    if (canvasProp.ContainsInlinkProperty() || canvasProp.ContainsOutlinkProperty()) {
                        System.Drawing.Image img = canvasProp.GetLinkedWzCanvasBitmap();
                        if (img != null) {
                            canvasPropBox.BindingPropertyItem.SurfaceFormat = WzPngFormatExtensions.GetXNASurfaceFormat(canvasProp.PngProperty.Format);
                            canvasPropBox.BindingPropertyItem.Bitmap = (System.Drawing.Bitmap)img;
                            canvasPropBox.BindingPropertyItem.BitmapBackup = (System.Drawing.Bitmap)img;
                        }
                    }
                    else {
                        Bitmap bmp = canvasProp.GetLinkedWzCanvasBitmap();

                        canvasPropBox.BindingPropertyItem.SurfaceFormat = WzPngFormatExtensions.GetXNASurfaceFormat(canvasProp.PngProperty.Format);
                        canvasPropBox.BindingPropertyItem.Bitmap = bmp;
                        canvasPropBox.BindingPropertyItem.BitmapBackup = bmp;
                    }
                    SetImageRenderView(canvasProp);
                }
                else if (obj is WzPngProperty pngProp && pngProp.Parent is WzCanvasProperty parentCanvas) {
                    bAnimateMoreButton = true;

                    menuItem_changeImage.Visibility = Visibility.Visible;
                    menuItem_saveImage.Visibility = Visibility.Visible;

                    Bitmap bmp = pngProp.GetImage(false);
                    canvasPropBox.BindingPropertyItem.SurfaceFormat = WzPngFormatExtensions.GetXNASurfaceFormat(pngProp.Format);
                    canvasPropBox.BindingPropertyItem.Bitmap = bmp;
                    canvasPropBox.BindingPropertyItem.BitmapBackup = bmp;
                    SetImageRenderView(parentCanvas);
                }
                else if (obj is WzUOLProperty uolProperty) {
                    bAnimateMoreButton = true; // flag

                    // Image
                    WzObject linkValue = uolProperty.LinkValue;
                    if (linkValue is WzCanvasProperty canvasUOL) {
                        canvasPropBox.Visibility = Visibility.Visible;

                        Bitmap bmp = canvasUOL.GetLinkedWzCanvasBitmap();

                        canvasPropBox.BindingPropertyItem.SurfaceFormat = WzPngFormatExtensions.GetXNASurfaceFormat(canvasUOL.PngProperty.Format);
                        canvasPropBox.BindingPropertyItem.Bitmap = bmp; // in any event that the WzCanvasProperty is an '_inlink' or '_outlink'
                        canvasPropBox.BindingPropertyItem.BitmapBackup = bmp; // in any event that the WzCanvasProperty is an '_inlink' or '_outlink'

                        menuItem_saveImage.Visibility = Visibility.Visible; // dont show change image, as its a UOL

                        SetImageRenderView(canvasUOL);
                    }
                    else if (linkValue is WzBinaryProperty binProperty) // Sound, used rarely in wz. i.e Sound.wz/Rune/1/Destroy
                    {
                        mp3Player.Visibility = Visibility.Visible;
                        mp3Player.SoundProperty = binProperty;

                        menuItem_changeSound.Visibility = Visibility.Visible;
                        menuItem_saveSound.Visibility = Visibility.Visible;
                        menuItem_openAudioStudio.Visibility = Visibility.Visible;
                        menuItem_applyAudioProject.Visibility = Visibility.Visible;
                        menuItem_exportDecodedWav.Visibility = Visibility.Visible;
                        menuItem_audioMetadata.Visibility = Visibility.Visible;
                    }

                    // Value
                    // set wz file value binding
                    _bindingPropertyItem.WzFileValue = obj.ToString();
                    _bindingPropertyItem.ChangeReadOnlyAttribute(false, _bindingPropertyItem, o => o.IsWzValueReadOnly, o => o.WzFileValue); // can be changed
                }
                else if (bIsWzSoundProperty) {
                    bAnimateMoreButton = true; // flag

                    mp3Player.Visibility = Visibility.Visible;
                    mp3Player.SoundProperty = (WzBinaryProperty)obj;

                    menuItem_changeSound.Visibility = Visibility.Visible;
                    menuItem_saveSound.Visibility = Visibility.Visible;
                    menuItem_openAudioStudio.Visibility = Visibility.Visible;
                    menuItem_applyAudioProject.Visibility = Visibility.Visible;
                    menuItem_exportDecodedWav.Visibility = Visibility.Visible;
                    menuItem_audioMetadata.Visibility = Visibility.Visible;
                }
                else if (bIsWzLuaProperty) {
                    textEditor.Visibility = Visibility.Visible;
                    textEditor.SetHighlightingDefinitionIndex(2); // javascript

                    textEditor.textEditor.Text = obj.ToString();
                }
                else if (bIsWzStringProperty || bIsWzIntProperty || bIsWzLongProperty || bIsWzDoubleProperty || bIsWzFloatProperty || bIsWzShortProperty) {
                    // If text is a string property, expand the textbox
                    if (bIsWzStringProperty) {
                        WzStringProperty stringObj = (WzStringProperty)obj;

                        if (stringObj.IsSpineAtlasResources) // spine related resource
                        {
                            bAnimateMoreButton = true;
                            menuItem_exportFile.Visibility = Visibility.Visible;

                            textEditor.Visibility = Visibility.Visible;
                            textEditor.SetHighlightingDefinitionIndex(20); // json
                            textEditor.textEditor.Text = obj.ToString();


                            string path_title = stringObj.Parent?.FullPath ?? "Animate";

                            Thread thread = new Thread(() => {
                                try {
                                    WzSpineAnimationItem item = new WzSpineAnimationItem(stringObj);

                                    // Create xna window
                                    SpineAnimationWindow Window = new SpineAnimationWindow(item, path_title);
                                    Window.Run();
                                }
                                catch (Exception e) {
                                    Warning.Error(string.Format(UiLocalization.Translate("Error initializing or rendering Spine object: {0}"), e));
                                }
                            });
                            thread.Start();
                            thread.Join();
                        }
                        else if (stringObj.Name.EndsWith(".json")) // Map001.wz/Back/BM3_3.img/spine/skeleton.json
                        {
                            bAnimateMoreButton = true;
                            menuItem_exportFile.Visibility = Visibility.Visible;

                            textEditor.Visibility = Visibility.Visible;
                            textEditor.SetHighlightingDefinitionIndex(20); // json
                            textEditor.textEditor.Text = obj.ToString();
                        }
                        else {
                            // Value
                            _bindingPropertyItem.WzFileValue = obj.ToString();
                            _bindingPropertyItem.ChangeReadOnlyAttribute(false, _bindingPropertyItem, o => o.IsWzValueReadOnly, o => o.WzFileValue); // can be changed

                            if (stringObj.Name == PORTAL_NAME_OBJ_NAME) // Portal type name display - "pn" = portal name 
                            {
                                PortalType portalType = PortalTypeExtensions.FromCode(obj.ToString());
                                
                                toolStripStatusLabel_additionalInfo.Text =
                                    string.Format(Properties.Resources.MainAdditionalInfo_PortalType, portalType.GetFriendlyName());
                            }
                            else {
                                //textPropBox.AcceptsReturn = true;
                                // TODO
                            }
                        }
                    }
                    else if (bIsWzLongProperty || bIsWzIntProperty || bIsWzShortProperty) {
                        // field limit UI
                        if (obj.Name == FIELD_LIMIT_OBJ_NAME) // fieldLimit
                        {
                            isSelectingWzMapFieldLimit = true;

                            ulong value_ = 0;
                            if (bIsWzLongProperty) // use uLong for field limit
                            {
                                value_ = (ulong)((WzLongProperty)obj).GetLong();
                            }
                            else if (bIsWzIntProperty) {
                                value_ = (ulong)((WzIntProperty)obj).GetLong();
                            }
                            else if (bIsWzShortProperty) {
                                value_ = (ulong)((WzShortProperty)obj).GetLong();
                            }

                            fieldLimitPanel1.UpdateFieldLimitCheckboxes(value_);

                            _bindingPropertyItem.WzFileValue = value_.ToString();
                            _bindingPropertyItem.ChangeReadOnlyAttribute(true, _bindingPropertyItem, o => o.IsWzValueReadOnly, o => o.WzFileValue); // dont allow user to change fieldLimit manually

                            // Set visibility
                            fieldLimitPanelHost.Visibility = Visibility.Visible;
                        }
                        else {
                            long value_ = 0; // long for others, in the case of negative value
                            if (bIsWzLongProperty) {
                                value_ = ((WzLongProperty)obj).GetLong();
                            }
                            else if (bIsWzIntProperty) {
                                value_ = ((WzIntProperty)obj).GetLong();
                            }
                            else if (bIsWzShortProperty) {
                                value_ = ((WzShortProperty)obj).GetLong();
                            }
                            _bindingPropertyItem.WzFileValue = value_.ToString();
                            _bindingPropertyItem.ChangeReadOnlyAttribute(false, _bindingPropertyItem, o => o.IsWzValueReadOnly, o => o.WzFileValue); // can be changed
                        }
                    }
                    else if (bIsWzDoubleProperty || bIsWzFloatProperty) {
                        _bindingPropertyItem.ChangeReadOnlyAttribute(false, _bindingPropertyItem, o => o.IsWzValueReadOnly, o => o.WzFileValue); // can be changed

                        if (bIsWzFloatProperty) {
                            _bindingPropertyItem.WzFileValue = ((WzFloatProperty)obj).GetFloat().ToString();
                        }
                        else if (bIsWzDoubleProperty) {
                            _bindingPropertyItem.WzFileValue = ((WzDoubleProperty)obj).GetDouble().ToString();
                        }
                    }
                    else {
                        //textPropBox.AcceptsReturn = false;
                        // TODO
                    }
                }
                else if (obj is WzVectorProperty property) {
                    _bindingPropertyItem.XYVector.X = property.X.Value;
                    _bindingPropertyItem.XYVector.Y = property.Y.Value;

                    _bindingPropertyItem.ChangeReadOnlyAttribute(false, _bindingPropertyItem, o => o.IsXYPanelReadOnly, o => o.XYVector);
                }
                else {
                }

                // Animation button
                if (AnimationBuilder.IsValidAnimationWzObject(obj)) {
                    bAnimateMoreButton = true; // flag
                }
                else {
                }

                // Storyboard hint
                button_MoreOption.Visibility = bAnimateMoreButton ? Visibility.Visible : Visibility.Collapsed;
                if (bAnimateMoreButton) {
                    System.Windows.Media.Animation.Storyboard storyboard_moreAnimation = (System.Windows.Media.Animation.Storyboard)(this.FindResource("Storyboard_TreeviewItemSelectedAnimation"));
                    storyboard_moreAnimation.Begin();
                }
            } finally {
                isLoading = false;
            }
        }

        /// <summary>
        ///  Sets the ImageRender view on clicked, or via animation tick
        /// </summary>
        /// <param name="canvas"></param>
        /// <param name="animationFrame"></param>
        private void SetImageRenderView(WzCanvasProperty canvas)
        {
            // origin
            int? delay = canvas[WzCanvasProperty.AnimationDelayPropertyName]?.GetInt();
            PointF originVector = canvas.GetCanvasOriginPosition();
            PointF headVector = canvas.GetCanvasHeadPosition();
            PointF ltVector = canvas.GetCanvasLtPosition();

            canvasPropBox.SetIsLoading(true);
            try {
                canvasPropBox.SetParentMainPanel(this);

                // Set XY point to canvas xaml
                canvasPropBox.BindingPropertyItem.ParentWzCanvasProperty = canvas;
                canvasPropBox.BindingPropertyItem.Delay = delay ?? 0;
                canvasPropBox.BindingPropertyItem.CanvasVectorOrigin = new NotifyPointF(originVector);
                canvasPropBox.BindingPropertyItem.CanvasVectorHead = new NotifyPointF(headVector);
                canvasPropBox.BindingPropertyItem.CanvasVectorLt = new NotifyPointF(ltVector);

                if (canvasPropBox.Visibility != Visibility.Visible)
                    canvasPropBox.Visibility = Visibility.Visible;
            }
            finally {
                canvasPropBox.SetIsLoading(false);
            }
        }
        #endregion

        #region Property Item
        /// <summary>
        /// On property item selection changed
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        /// <summary>
        /// Repaints one node's label in the WPF mirror. TreeViewItem.Header is a plain copy of
        /// node.Text taken when the item was created (see CreateNativeTreeItem) rather than a
        /// binding, so a rename has to push the new text across. Only this node is touched - no
        /// tree rebuild, so scroll position and expansion state stay exactly as they were.
        /// </summary>
        private void UpdateNativeNodeHeader(WzNode node)
        {
            if (node != null && nativeTreeItems.TryGetValue(node, out TreeViewItem item))
                item.Header = node.Text;
        }

        /// <summary>
        /// Puts the 名稱 box back to the node's real name after a rename that was refused, so the
        /// header never shows a name that was not applied. isLoading suppresses the
        /// PropertyChanged this assignment raises - the same guard ShowObjectValue uses while
        /// populating - which is what keeps this from re-entering the rename handler.
        /// </summary>
        private void RevertNodeNameBox(WzNode node)
        {
            bool wasLoading = isLoading;
            isLoading = true;
            try {
                _bindingPropertyItem.WzFileName = node.Text;
            }
            finally {
                isLoading = wasLoading;
            }
        }

        private void propertyGrid_PropertyChanged_1(object sender, System.ComponentModel.PropertyChangedEventArgs e) {
            if (isLoading) {
                return;
            }
            switch (e.PropertyName) {
                case "WzFileType": { // does nothing
                        break;
                    }
                case "WzFileName": {
                        if (DataTree.SelectedNode is not WzNode node)
                            return;

                        string setText = _bindingPropertyItem.WzFileName;

                        // Re-entering the same name (or just pressing Enter without editing) is a
                        // no-op, not a duplicate - CanNodeBeInserted would otherwise find this
                        // very node under that name and report "node exists".
                        if (string.Equals(setText, node.Text, StringComparison.Ordinal))
                            break;

                        if (node.Tag is WzFile) {
                            // A WZ file node is not renamed here; put the box back so the header
                            // never shows a name the tree does not actually have.
                            RevertNodeNameBox(node);
                        }
                        else if (WzNode.CanNodeBeInserted((WzNode)node.Parent, setText)) {
                            string oldName = node.Text;
                            node.ChangeName(setText);
                            // TreeViewItem.Header is a one-time copy of node.Text taken in
                            // CreateNativeTreeItem, so the rename has to be pushed across or the
                            // visible tree keeps the old name until it is rebuilt.
                            UpdateNativeNodeHeader(node);
                            UndoRedoMan.AddUndoBatch(new System.Collections.Generic.List<UndoRedoAction>
                                { UndoRedoManager.ObjectRenamed(node, oldName, setText) });
                        }
                        else {
                            Warning.Error(Properties.Resources.MainNodeExists);
                            RevertNodeNameBox(node);
                        }
                        break;
                    }
                case "XYVector":
                case "WzFileValue": {
                        if (DataTree.SelectedNode == null)
                            return;

                        string setText = _bindingPropertyItem.WzFileValue;

                        bool bChangedNode = false;

                        WzNode node = (WzNode) DataTree.SelectedNode;
                        WzObject obj = (WzObject)DataTree.SelectedNode.Tag;

                        bool bIsWzFile = obj is WzFile file;
                        bool bIsWzDirectory = obj is WzDirectory;
                        bool bIsWzImage = obj is WzImage;
                        bool bIsWzLuaProperty = obj is WzLuaProperty;
                        bool bIsWzSoundProperty = obj is WzBinaryProperty;
                        bool bIsWzStringProperty = obj is WzStringProperty;
                        bool bIsWzIntProperty = obj is WzIntProperty;
                        bool bIsWzLongProperty = obj is WzLongProperty;
                        bool bIsWzDoubleProperty = obj is WzDoubleProperty;
                        bool bIsWzFloatProperty = obj is WzFloatProperty;
                        bool bIsWzShortProperty = obj is WzShortProperty;
                        bool bIsWzNullProperty = obj is WzNullProperty;
                        bool bIsWzSubProperty = obj is WzSubProperty;
                        bool bIsWzConvexProperty = obj is WzConvexProperty;


                        if (bIsWzFile) {
                            ((WzFile)node.Tag).Header.Copyright = setText;
                            ((WzFile)node.Tag).Header.RecalculateFileStart();

                            bChangedNode = true;
                        }
                        else if (obj is WzVectorProperty vectorProperty) {
                            vectorProperty.X.Value = (int) _bindingPropertyItem.XYVector.X;
                            vectorProperty.Y.Value = (int) _bindingPropertyItem.XYVector.Y;

                            bChangedNode = true;
                        }
                        else if (obj is WzStringProperty stringProperty) {
                            if (!stringProperty.IsSpineAtlasResources) {
                                stringProperty.Value = setText;

                                bChangedNode = true;
                            }
                            else {
                                throw new NotSupportedException("Usage of textBoxProp for spine WzStringProperty.");
                            }
                        }
                        else if (obj is WzFloatProperty floatProperty) {
                            float val;
                            if (!float.TryParse(setText, out val)) {
                                Warning.Error(string.Format(Properties.Resources.MainConversionError, setText));
                                return;
                            }
                            floatProperty.Value = val;

                            bChangedNode = true;
                        }
                        else if (obj is WzIntProperty intProperty) {
                            int val;
                            if (!int.TryParse(setText, out val)) {
                                Warning.Error(string.Format(Properties.Resources.MainConversionError, setText));
                                return;
                            }
                            intProperty.Value = val;

                            bChangedNode = true;
                        }
                        else if (obj is WzLongProperty longProperty) {
                            long val;
                            if (!long.TryParse(setText, out val)) {
                                Warning.Error(string.Format(Properties.Resources.MainConversionError, setText));
                                return;
                            }
                            longProperty.Value = val;

                            bChangedNode = true;
                        }
                        else if (obj is WzDoubleProperty doubleProperty) {
                            double val;
                            if (!double.TryParse(setText, out val)) {
                                Warning.Error(string.Format(Properties.Resources.MainConversionError, setText));
                                return;
                            }
                            doubleProperty.Value = val;

                            bChangedNode = true;
                        }
                        else if (obj is WzShortProperty shortProperty) {
                            short val;
                            if (!short.TryParse(setText, out val)) {
                                Warning.Error(string.Format(Properties.Resources.MainConversionError, setText));
                                return;
                            }
                            shortProperty.Value = val;

                            bChangedNode = true;
                        }
                        else if (obj is WzUOLProperty UOLProperty) {
                            UOLProperty.Value = setText;

                            bChangedNode = true;
                        }
                        else if (obj is WzLuaProperty) {
                            throw new NotSupportedException("Moved to TextEditor_SaveButtonClicked()");
                        }

                        if (bChangedNode) {
                            node.ChangedNodeProperty();
                        }
                        break;
                    }
                default: {
                        break;
                    }
            }
        }

        /// <summary>
        /// On field limit checkboxes changes, update the PropertyItem values accordingly
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void FieldLimitPanel1_FieldLimitChanged(object sender, FieldLimitChangedEventArgs e) {
            _bindingPropertyItem.WzFileValue = e.FieldLimit.ToString();
        }
        #endregion

        #region Search

        /// <summary>
        /// On search box fade in completed
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Storyboard_Find_FadeIn_Completed(object sender, EventArgs e)
        {
            findBox.Focus();
        }

        private int searchidx = 0;
        private bool finished = false;
        private bool listSearchResults = false;
        private List<string> searchResultsList = new List<string>();
        private bool searchValues = true;
        private WzNode coloredNode = null;
        private int currentidx = 0;
        private string searchText = "";
        private bool extractImages = false;

        /// <summary>
        /// Close search box
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void button_closeSearch_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Media.Animation.Storyboard sbb = (System.Windows.Media.Animation.Storyboard)(this.FindResource("Storyboard_Find_FadeOut"));
            sbb.Begin();
        }

        private void SearchWzProperties(IPropertyContainer parent)
        {
            foreach (WzImageProperty prop in parent.WzProperties)
            {
                if ((0 <= prop.Name.IndexOf(searchText, StringComparison.InvariantCultureIgnoreCase)) || (searchValues && prop is WzStringProperty && (0 <= ((WzStringProperty)prop).Value.IndexOf(searchText, StringComparison.InvariantCultureIgnoreCase))))
                {
                    if (listSearchResults)
                        searchResultsList.Add(prop.FullPath.Replace(";", @"\"));
                    else if (currentidx == searchidx)
                    {
                        if (prop.HRTag == null)
                            ((WzNode)prop.ParentImage.HRTag).Reparse();
                        WzNode node = (WzNode)prop.HRTag;
                        //if (node.Style == null) node.Style = new ElementStyle();
                        node.BackColor = System.Drawing.Color.Yellow;
                        coloredNode = node;
                        SelectAndRevealNativeNode(node);
                        finished = true;
                        searchidx++;
                        return;
                    }
                    else
                        currentidx++;
                }
                if (prop is IPropertyContainer && prop.WzProperties.Count != 0)
                {
                    SearchWzProperties((IPropertyContainer)prop);
                    if (finished)
                        return;
                }
            }
        }

        private void SearchTV(WzNode node)
        {
            foreach (WzNode subnode in node.Nodes)
            {
                if (0 <= subnode.Text.IndexOf(searchText, StringComparison.InvariantCultureIgnoreCase))
                {
                    if (listSearchResults)
                        searchResultsList.Add(subnode.FullPath.Replace(";", @"\"));
                    else if (currentidx == searchidx)
                    {
                        //if (subnode.Style == null) subnode.Style = new ElementStyle();
                        subnode.BackColor = System.Drawing.Color.Yellow;
                        coloredNode = subnode;
                        SelectAndRevealNativeNode(subnode);
                        finished = true;
                        searchidx++;
                        return;
                    }
                    else
                        currentidx++;
                }
                if (subnode.Tag is WzImage)
                {
                    WzImage img = (WzImage)subnode.Tag;
                    if (img.Parsed)
                        SearchWzProperties(img);
                    else if (extractImages)
                    {
                        img.ParseImage();
                        SearchWzProperties(img);
                    }
                    if (finished) return;
                }
                else SearchTV(subnode);
            }
        }

        // ---- progressive Find next --------------------------------------------------------------

        /// <summary>
        /// Cancels whichever progressive search is still running: bumped by a new press, a query
        /// change, a user re-selection ending the session, and close-all. A run compares its own
        /// copy after every await and quietly stops when it lost.
        /// </summary>
        private int searchRunGeneration;

        /// <summary>
        /// The async walk behind Find next. Same traversal order and same match counter as
        /// <see cref="SearchTV"/>, plus: an unparsed image is parsed when the walk reaches it -
        /// big ones on a worker thread through the same in-flight state the double-click parse
        /// uses - and the walk yields the dispatcher regularly, so scanning thousands of images
        /// never freezes the window. Search stays display-clean: parsing marks nothing changed,
        /// expands nothing, and only the branch of an actual hit is materialized (by the hit
        /// handler, exactly as before).
        /// </summary>
        private async Task<bool> SearchTVAsync(WzNode node, int generation, System.Diagnostics.Stopwatch yieldClock, int[] imagesScanned)
        {
            // Snapshot: parsing/reveal work may add nodes while we walk.
            var children = new List<WzNode>();
            foreach (System.Windows.Forms.TreeNode child in node.Nodes)
                if (child is WzNode wz)
                    children.Add(wz);

            foreach (WzNode subnode in children)
            {
                if (generation != searchRunGeneration)
                    return false;

                if (0 <= subnode.Text.IndexOf(searchText, StringComparison.InvariantCultureIgnoreCase))
                {
                    if (currentidx == searchidx)
                    {
                        subnode.BackColor = System.Drawing.Color.Yellow;
                        coloredNode = subnode;
                        SelectAndRevealNativeNode(subnode);
                        finished = true;
                        searchidx++;
                        return true;
                    }
                    currentidx++;
                }

                if (subnode.Tag is WzImage img)
                {
                    if (!img.Parsed)
                    {
                        await EnsureImageParsedForSearchAsync(img, generation);
                        if (generation != searchRunGeneration)
                            return false;
                        if (!img.Parsed)
                            continue; // broken image: skip it, keep searching the rest
                    }
                    SearchWzProperties(img);
                    if (finished)
                        return true;

                    imagesScanned[0]++;
                    if (yieldClock.ElapsedMilliseconds > 40)
                    {
                        // Let paint/input through and show progress before continuing.
                        toolStripStatusLabel_additionalInfo.Text = "搜尋中… 已掃 " + imagesScanned[0] + " 張 IMG";
                        await Dispatcher.Yield(DispatcherPriority.Background);
                        yieldClock.Restart();
                        if (generation != searchRunGeneration)
                            return false;
                    }
                }
                else
                {
                    if (await SearchTVAsync(subnode, generation, yieldClock, imagesScanned))
                        return true;
                    if (generation != searchRunGeneration)
                        return false;
                }
            }
            return finished;
        }

        /// <summary>
        /// Parses one image for the search walk, sharing <see cref="imagesBeingParsed"/> with the
        /// double-click parse so the same image is never parsed twice concurrently: if the other
        /// path started it, this one just waits for it. Big images go to a worker thread (the
        /// same size threshold as double-click); small ones parse inline, with the caller's
        /// yield cadence keeping the pump alive. Parsing here never touches Changed and never
        /// expands anything.
        /// </summary>
        private async Task EnsureImageParsedForSearchAsync(WzImage img, int generation)
        {
            while (imagesBeingParsed.Contains(img) && generation == searchRunGeneration)
                await Task.Delay(25);
            if (img.Parsed || generation != searchRunGeneration)
                return;

            if (img.BlockSize >= AsyncParseMinBlockSize)
            {
                imagesBeingParsed.Add(img);
                try
                {
                    await System.Threading.Tasks.Task.Run(() => img.ParseImage());
                }
                catch
                {
                    // A broken image just doesn't get searched.
                }
                finally
                {
                    imagesBeingParsed.Remove(img);
                }
            }
            else
            {
                try { img.ParseImage(); }
                catch { }
            }
        }

        /// <summary>
        /// Find all
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void button_allSearch_Click(object sender, RoutedEventArgs e)
        {
            if (coloredNode != null)
            {
                coloredNode.BackColor = System.Drawing.Color.White;
                coloredNode = null;
            }
            if (findBox.Text == "" || DataTree.Nodes.Count == 0)
                return;
            if (DataTree.SelectedNode == null)
                DataTree.SelectedNode = DataTree.Nodes[0];

            finished = false;
            listSearchResults = true;
            searchResultsList.Clear();
            //searchResultsBox.Items.Clear();
            searchValues = Program.ConfigurationManager.UserSettings.SearchStringValues;
            currentidx = 0;
            searchText = findBox.Text;
            extractImages = Program.ConfigurationManager.UserSettings.ParseImagesInSearch;

            // Same session as Find next, for the same reason: run after a few Find next presses,
            // the live selection is a previous hit rather than the scope the user chose.
            EnsureSearchSession(searchText);
            foreach (WzNode node in searchRootNodes)
            {
                if (node.Tag is WzImageProperty)
                    continue;
                else if (node.Tag is IPropertyContainer)
                    SearchWzProperties((IPropertyContainer)node.Tag);
                else
                    SearchTV(node);
            }

            SearchSelectionForm form = SearchSelectionForm.Show(searchResultsList);
            form.OnSelectionChanged += Form_OnSelectionChanged;

            findBox.Focus();
        }

        /// <summary>
        /// On search selection from SearchSelectionForm list changed
        /// </summary>
        /// <param name="str"></param>
        private void Form_OnSelectionChanged(string str)
        {
            string[] splitPath = str.Split(@"\".ToCharArray());
            WzNode node = null;
            System.Windows.Forms.TreeNodeCollection collection = DataTree.Nodes;
            for (int i = 0; i < splitPath.Length; i++)
            {
                node = GetNodeByName(collection, splitPath[i]);
                if (node != null)
                {
                    if (node.Tag is WzImage && !((WzImage)node.Tag).Parsed && i != splitPath.Length - 1)
                    {
                        ParseOnDataTreeSelectedItem(node, false);
                    }
                    collection = node.Nodes;
                }
            }
            if (node != null)
            {
                SelectAndRevealNativeNode(node);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private WzNode GetNodeByName(System.Windows.Forms.TreeNodeCollection collection, string name)
        {
            foreach (WzNode node in collection)
                if (node.Text == name)
                    return node;
            return null;
        }

        /// <summary>
        /// Find next
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        /// <summary>
        /// The nodes the current run of "Find next" walks, snapshotted once when the session
        /// starts. Held across presses on purpose: the tree selection moves to each hit as it is
        /// found, so re-reading the selection every press would shrink the search down to its own
        /// first result and never reach the second match.
        /// </summary>
        private readonly List<WzNode> searchRootNodes = new List<WzNode>();
        private string searchRootQuery;

        /// <summary>
        /// Drops the saved roots so the next Find next starts a fresh session from whatever is
        /// selected then. Called when the user selects a node themselves and when the query text
        /// changes.
        /// </summary>
        private void EndSearchSession()
        {
            searchRootNodes.Clear();
            searchRootQuery = null;
        }

        /// <summary>
        /// Makes sure <see cref="searchRootNodes"/> holds the roots for the text about to be
        /// searched, re-snapshotting from the tree selection when the session is new, the query
        /// changed, or a stored root has been removed from the tree. A brand new snapshot also
        /// restarts the match counter, so the first press lands on the first match.
        /// </summary>
        private void EnsureSearchSession(string query)
        {
            bool anyRootDetached = false;
            foreach (WzNode root in searchRootNodes)
            {
                if (root.TreeView == null)
                {
                    anyRootDetached = true;
                    break;
                }
            }

            if (!SearchSessionPolicy.ShouldSnapshotRoots(searchRootNodes.Count > 0, searchRootQuery, query, anyRootDetached))
                return;

            searchRootNodes.Clear();
            foreach (object item in DataTree.SelectedNodes)
            {
                if (item is WzNode node)
                    searchRootNodes.Add(node);
            }
            if (searchRootNodes.Count == 0 && DataTree.SelectedNode is WzNode activeNode)
                searchRootNodes.Add(activeNode);

            searchRootQuery = query;
            searchidx = 0;
        }

        private async void button_nextSearch_Click(object sender, RoutedEventArgs e)
        {
            if (coloredNode != null)
            {
                coloredNode.BackColor = System.Drawing.Color.White;
                coloredNode = null;
            }
            if (findBox.Text == "" || DataTree.Nodes.Count == 0) return;
            if (DataTree.SelectedNode == null) DataTree.SelectedNode = DataTree.Nodes[0];

            // A press while the previous search is still walking supersedes it.
            int generation = ++searchRunGeneration;

            finished = false;
            listSearchResults = false;
            searchResultsList.Clear();
            searchValues = Program.ConfigurationManager.UserSettings.SearchStringValues;
            currentidx = 0;
            searchText = findBox.Text;
            string query = searchText;

            // Walk the session's roots, not the live selection - the live selection is now
            // whichever hit the previous press jumped to.
            EnsureSearchSession(searchText);

            var yieldClock = System.Diagnostics.Stopwatch.StartNew();
            int[] imagesScanned = { 0 };
            try
            {
                foreach (WzNode node in searchRootNodes.ToList())
                {
                    if (node.Tag is IPropertyContainer container)
                        SearchWzProperties(container);
                    else if (node.Tag is WzImageProperty) continue;
                    else await SearchTVAsync(node, generation, yieldClock, imagesScanned);
                    if (finished || generation != searchRunGeneration) break;
                }
            }
            catch (Exception ex)
            {
                if (generation == searchRunGeneration)
                    toolStripStatusLabel_additionalInfo.Text = "搜尋失敗：" + ex.Message;
                return;
            }

            // Superseded by a newer press / query change / close-all: this run says nothing.
            if (generation != searchRunGeneration)
                return;

            // Past the last match: rewind so the next press starts over from the first one. The
            // session's roots stay put, so "start over" means the original scope, not the hit the
            // selection happens to be sitting on. Reported in the status line, not a MessageBox.
            if (!finished)
            {
                toolStripStatusLabel_additionalInfo.Text = "搜尋完成，找不到「" + query + "」。";
                searchidx = 0;
                WzNode.EnsureVisibleIfDisplayed(DataTree.SelectedNode);
            }
            else
            {
                toolStripStatusLabel_additionalInfo.Text = "-";
            }
            findBox.Focus();
        }

        private void findBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                button_nextSearch_Click(null, null);
                e.Handled = true;
            }
        }

        private void findBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            searchidx = 0;
            // A running progressive search for the old text must not finish later and jump the
            // selection to a result the user no longer wants.
            searchRunGeneration++;
            // Different text means the next Find next is a new search, so the roots get
            // re-snapshotted from whatever the user has selected at that point.
            EndSearchSession();
        }
        #endregion
    }
}
