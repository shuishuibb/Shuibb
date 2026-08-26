using HaRepacker.GUI;
using HaRepacker.GUI.Input;
using HaRepacker.GUI.MapObjectInfo;
using HaRepacker.GUI.Panels;
using MapleLib.Img;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace HaRepacker
{
    public class ContextMenuManager
    {
        private static string T(string text) => UiLocalization.Translate(text);
        private static string TF(string text, params object[] args) => string.Format(T(text), args);
        // A delegate rather than a stored MainPanel reference: HaRepacker can have several tabs
        // open at once, each with its own MainPanel, but only ONE ContextMenuManager is ever
        // constructed (see MainForm.cs) - it lives for the whole app session. Capturing a fixed
        // MainPanel here would silently keep operating on whichever tab was active at that one
        // moment, forever, regardless of which tab the user is actually looking at later. This
        // is invoked fresh every time a menu action needs "the current tab's MainPanel", which
        // MainForm keeps up to date as the user switches tabs.
        //
        // Every click handler that needs it is a NAMED method (not an inline delegate) so that
        // "getMainPanel" is read as a plain this.getMainPanel field access - inline delegates
        // referencing it would capture it into a compiler-generated closure class instead.
        private readonly Func<MainPanel> getMainPanel;

        private ToolStripMenuItem SaveFile;
        private ToolStripMenuItem SaveImg;
        private ToolStripMenuItem CreateNewImgFile;
        private ToolStripMenuItem DeleteImgFile;
        private ToolStripMenuItem Remove;
        private ToolStripMenuItem Unload;
        private ToolStripMenuItem Reload;
        private ToolStripMenuItem CollapseAllChildNode;
        private ToolStripMenuItem ExpandAllChildNode;
        private ToolStripMenuItem SortAllChildViewNode, SortAllChildViewNode2;
        private ToolStripMenuItem SortPropertiesByName;

        private ToolStripMenuItem AddPropsSubMenu;
        private ToolStripMenuItem AddDirsSubMenu;
        private ToolStripMenuItem AddBatchMenu;
        private ToolStripMenuItem AddSortMenu;
        private ToolStripMenuItem AddSortMenu_WithoutPropSort;
        private ToolStripMenuItem AddImage;
        private ToolStripMenuItem AddDirectory;
        private ToolStripMenuItem AddByteFloat;
        private ToolStripMenuItem AddCanvas;
        private ToolStripMenuItem AddLong;
        private ToolStripMenuItem AddInt;
        private ToolStripMenuItem AddConvex;
        private ToolStripMenuItem AddDouble;
        private ToolStripMenuItem AddNull;
        private ToolStripMenuItem AddSound;
        private ToolStripMenuItem AddString;
        private ToolStripMenuItem AddSub;
        private ToolStripMenuItem AddUshort;
        private ToolStripMenuItem AddUOL;
        private ToolStripMenuItem AddVector;
        private ToolStripMenuItem Rename;
        private ToolStripMenuItem Animate;
        private ToolStripMenuItem SaveAnimation;
        private ToolStripMenuItem FixInlink, AiUpscaleImage, AiUpscaleImageSubMenu_QualityOnly, AiUpscaleImageSubMenu_1_5x, AiUpscaleImageSubMenu_2x, AiUpscaleImageSubMenu_4x;
        private ToolStripMenuItem ConvertToBgra32;
        private ToolStripMenuItem ResizeImage;
        private ToolStripMenuItem ResizeImageToBgra32;

        // BOBO-derived node/string/folder-image batch tools, grouped under their own submenu so
        // the existing image-oriented "batch edit" menu keeps its short, predictable shape.
        private ToolStripMenuItem AddNodeBatchMenu;
        private ToolStripMenuItem AskAiAboutNode;
        private ToolStripMenuItem BatchSetValues;
        private ToolStripMenuItem BatchOffsetNumber;
        private ToolStripMenuItem BatchReplaceText;
        private ToolStripMenuItem BatchReplaceOrDelete;
        private ToolStripMenuItem BatchDeleteNodes;
        private ToolStripMenuItem BatchCleanupString;
        private ToolStripMenuItem BatchCoverFolderImages;
        private ToolStripMenuItem BatchImportFolderImages;

        // Read-only map summary (mapMark/bgm/back/tile/obj/npc/mob/reactor); see
        // HaRepacker\GUI\MapObjectInfo\.
        private ToolStripMenuItem MapObjectInfoMenuItem;

        /*private ToolStripMenuItem ExportPropertySubMenu;
        private ToolStripMenuItem ExportAnimationSubMenu;
        private ToolStripMenuItem ExportDirectorySubMenu;
        private ToolStripMenuItem ExportPServerXML;
        private ToolStripMenuItem ExportDataXML;
        private ToolStripMenuItem ExportImgData;
        private ToolStripMenuItem ExportRawData;
        private ToolStripMenuItem ExportGIF;
        private ToolStripMenuItem ExportAPNG;

        private ToolStripMenuItem ImportSubMenu;
        private ToolStripMenuItem ImportXML;
        private ToolStripMenuItem ImportImgData;*/

        public ContextMenuManager(Func<MainPanel> getMainPanel)
        {
            this.getMainPanel = getMainPanel;

            SaveFile = new ToolStripMenuItem(UiLocalization.Translate("Save"), Properties.Resources.disk, new EventHandler(SaveFile_Click));
            SaveImg = new ToolStripMenuItem(UiLocalization.Translate("Save to IMG"), Properties.Resources.disk, new EventHandler(SaveImg_Click));
            CreateNewImgFile = new ToolStripMenuItem(UiLocalization.Translate("Create New IMG File"), Properties.Resources.add, new EventHandler(CreateNewImgFile_Click));
            DeleteImgFile = new ToolStripMenuItem(UiLocalization.Translate("Delete IMG File"), Properties.Resources.delete, new EventHandler(DeleteImgFile_Click));
            Rename = new ToolStripMenuItem(UiLocalization.Translate("Rename"), Properties.Resources.rename, new EventHandler(Rename_Click));
            Remove = new ToolStripMenuItem(UiLocalization.Translate("Remove"), Properties.Resources.delete, new EventHandler(Remove_Click));

            Unload = new ToolStripMenuItem(UiLocalization.Translate("Unload"), Properties.Resources.delete, new EventHandler(Unload_Click));
            Reload = new ToolStripMenuItem(UiLocalization.Translate("Reload"), Properties.Resources.arrow_refresh, new EventHandler(Reload_Click));
            CollapseAllChildNode = new ToolStripMenuItem(UiLocalization.Translate("Collapse All"), Properties.Resources.collapse, new EventHandler(CollapseAllChildNode_Click));
            ExpandAllChildNode = new ToolStripMenuItem(UiLocalization.Translate("Expand all"), Properties.Resources.expand, new EventHandler(ExpandAllChildNode_Click));

            // This only sorts the view, does not affect the actual order of the
            // wz properties
            SortAllChildViewNode = new ToolStripMenuItem(UiLocalization.Translate("Sort child nodes view"), null, new EventHandler(SortChildNodesView_Click)); // SortAllChildViewNode cant be in 2 place at once, gotta make copies
            SortAllChildViewNode2 = new ToolStripMenuItem(UiLocalization.Translate("Sort child nodes view"), null, new EventHandler(SortChildNodesView_Click)); // SortAllChildViewNode cant be in 2 place at once, gotta make copies
            SortPropertiesByName = new ToolStripMenuItem(UiLocalization.Translate("Sort properties by name"), null, new EventHandler(SortPropertiesByName_Click));

            AddImage = new ToolStripMenuItem(UiLocalization.Translate("Image"), null, new EventHandler(AddImage_Click));
            AddDirectory = new ToolStripMenuItem(UiLocalization.Translate("Directory"), null, new EventHandler(AddDirectory_Click));
            AddByteFloat = new ToolStripMenuItem(UiLocalization.Translate("Float"), null, new EventHandler(AddByteFloat_Click));
            AddCanvas = new ToolStripMenuItem(UiLocalization.Translate("Canvas"), null, new EventHandler(AddCanvas_Click));
            AddLong = new ToolStripMenuItem(UiLocalization.Translate("Long"), null, new EventHandler(AddLong_Click));
            AddInt = new ToolStripMenuItem(UiLocalization.Translate("Int"), null, new EventHandler(AddInt_Click));
            AddConvex = new ToolStripMenuItem(UiLocalization.Translate("Convex"), null, new EventHandler(AddConvex_Click));
            AddDouble = new ToolStripMenuItem(UiLocalization.Translate("Double"), null, new EventHandler(AddDouble_Click));
            AddNull = new ToolStripMenuItem(UiLocalization.Translate("Null"), null, new EventHandler(AddNull_Click));
            AddSound = new ToolStripMenuItem(UiLocalization.Translate("Sound"), null, new EventHandler(AddSound_Click));
            AddString = new ToolStripMenuItem(UiLocalization.Translate("String"), null, new EventHandler(AddString_Click));
            AddSub = new ToolStripMenuItem(UiLocalization.Translate("Sub"), null, new EventHandler(AddSub_Click));
            AddUshort = new ToolStripMenuItem(UiLocalization.Translate("Short"), null, new EventHandler(AddUshort_Click));
            AddUOL = new ToolStripMenuItem(UiLocalization.Translate("UOL"), null, new EventHandler(AddUOL_Click));
            AddVector = new ToolStripMenuItem(UiLocalization.Translate("Vector"), null, new EventHandler(AddVector_Click));
            Animate = new ToolStripMenuItem(Properties.Resources.MainPanel_Animate, Properties.Resources.animate, new EventHandler(Animate_Click));
            SaveAnimation = new ToolStripMenuItem(Properties.Resources.MainPanel_SaveAnimate, Properties.Resources.animate_save, new EventHandler(SaveAnimation_Click));

            FixInlink = new ToolStripMenuItem(Properties.Resources.MainContextMenu_Batch_EditInlink, null, new EventHandler(FixInlink_Click));

            // Force-recompress every canvas under the selection to the uncompressed BGRA32
            // format, e.g. when pulling art from a source WZ that uses DXT5/DXT3/BC7 into a
            // client build whose renderer only understands the uncompressed format.
            ConvertToBgra32 = new ToolStripMenuItem("轉換圖片為 BGRA32", null, new EventHandler(ConvertToBgra32_Click));

            // Resize every canvas under the selection by a user-entered percentage, scaling the
            // 'origin' anchor by the same ratio so composited sprites/effects don't visibly
            // shift after the resize.
            ResizeImage = new ToolStripMenuItem("縮小圖片...", null, new EventHandler(ResizeImage_Click));

            // Both operations in one pass, so each canvas is only decoded and re-encoded once.
            ResizeImageToBgra32 = new ToolStripMenuItem("縮小圖片並轉 BGRA32...", null, new EventHandler(ResizeImageToBgra32_Click));

            // Batch edit
            AiUpscaleImageSubMenu_QualityOnly = new ToolStripMenuItem(Properties.Resources.MainContextMenu_Batch_AIUpscaleImage_QualityOnly, null, new EventHandler(AiUpscaleQualityOnly_Click));
            AiUpscaleImageSubMenu_1_5x = new ToolStripMenuItem("1.5x", null, new EventHandler(AiUpscale1_5x_Click));
            AiUpscaleImageSubMenu_2x = new ToolStripMenuItem("2x", null, new EventHandler(AiUpscale2x_Click));
            AiUpscaleImageSubMenu_4x = new ToolStripMenuItem("4x", null, new EventHandler(AiUpscale4x_Click));
            AiUpscaleImage = new ToolStripMenuItem(Properties.Resources.MainContextMenu_Batch_AIUpscaleImage, null,
                AiUpscaleImageSubMenu_QualityOnly, AiUpscaleImageSubMenu_1_5x, AiUpscaleImageSubMenu_2x, AiUpscaleImageSubMenu_4x
            );


            // Menu
            AddDirsSubMenu = new ToolStripMenuItem(UiLocalization.Translate("Add"), Properties.Resources.add,
                AddDirectory, AddImage);

            AddPropsSubMenu = new ToolStripMenuItem(UiLocalization.Translate("Add"), Properties.Resources.add,
                AddCanvas, AddConvex, AddDouble, AddByteFloat, AddLong, AddInt, AddNull, AddUshort, AddSound, AddString, AddSub, AddUOL, AddVector);

            AddBatchMenu = new ToolStripMenuItem(Properties.Resources.MainContextMenu_Batch, Properties.Resources.batch_edit,
                FixInlink, AiUpscaleImage, ConvertToBgra32, ResizeImage, ResizeImageToBgra32);

            BatchSetValues = new ToolStripMenuItem("技能節點批量修改...", null, new EventHandler(BatchSetValuesByNodeName_Click));
            BatchOffsetNumber = new ToolStripMenuItem("批量更改節點（數字位移）...", null, new EventHandler(BatchOffsetNodeNames_Click));
            BatchReplaceText = new ToolStripMenuItem("批量替換字符...", null, new EventHandler(BatchReplaceText_Click));
            BatchReplaceOrDelete = new ToolStripMenuItem("批量替換&&刪除節點...", null, new EventHandler(BatchReplaceOrDeleteText_Click));
            BatchDeleteNodes = new ToolStripMenuItem("批量刪除節點...", null, new EventHandler(BatchDeleteNodesByName_Click));
            BatchCleanupString = new ToolStripMenuItem("String移除多餘+自動補缺少...", null, new EventHandler(BatchCleanupStringWz_Click));
            BatchCoverFolderImages = new ToolStripMenuItem("一鍵覆蓋資料夾圖片...", null, new EventHandler(BatchCoverFolderImages_Click));
            BatchImportFolderImages = new ToolStripMenuItem("一鍵匯入資料夾圖片...", null, new EventHandler(BatchImportFolderImages_Click));

            AddNodeBatchMenu = new ToolStripMenuItem("批次節點工具", Properties.Resources.batch_edit,
                BatchSetValues, BatchOffsetNumber, BatchReplaceText, BatchReplaceOrDelete, BatchDeleteNodes,
                new ToolStripSeparator(),
                BatchCleanupString,
                new ToolStripSeparator(),
                BatchCoverFolderImages, BatchImportFolderImages);

            AskAiAboutNode = new ToolStripMenuItem("AI 助手（詢問這個節點）...", null, new EventHandler(AskAiAboutNode_Click));

            MapObjectInfoMenuItem = new ToolStripMenuItem("地圖物件資訊", null, new EventHandler(MapObjectInfo_Click));

            AddSortMenu = new ToolStripMenuItem(UiLocalization.Translate("Sort"), Properties.Resources.sort, SortAllChildViewNode, SortPropertiesByName);

            Debug.WriteLine(AddSortMenu.DropDown.Items.Count.ToString());
            AddSortMenu_WithoutPropSort = new ToolStripMenuItem(UiLocalization.Translate("Sort"), Properties.Resources.sort, SortAllChildViewNode2);
        }

        private void SaveFile_Click(object sender, EventArgs e)
        {
            foreach (WzNode node in GetNodes(sender))
            {
                new SaveForm(getMainPanel(), node).ShowDialog();
            }
        }

        private void SaveImg_Click(object sender, EventArgs e)
        {
            foreach (WzNode node in GetNodes(sender))
            {
                SaveImgNode(node);
            }
        }

        private void CreateNewImgFile_Click(object sender, EventArgs e)
        {
            WzNode[] nodes = GetNodes(sender);
            if (nodes.Length != 1)
            {
                MessageBox.Show(UiLocalization.Translate("Please select only one node."));
                return;
            }
            CreateNewImgFileInDirectory(nodes[0]);
        }

        private void DeleteImgFile_Click(object sender, EventArgs e)
        {
            WzNode[] nodes = GetNodes(sender);
            if (nodes.Length != 1)
            {
                MessageBox.Show(UiLocalization.Translate("Please select only one node."));
                return;
            }
            DeleteImgFileFromDirectory(nodes[0]);
        }

        private void CollapseAllChildNode_Click(object sender, EventArgs e)
        {
            foreach (WzNode node in GetNodes(sender))
            {
                node.Collapse();
            }
        }

        private void ExpandAllChildNode_Click(object sender, EventArgs e)
        {
            foreach (WzNode node in GetNodes(sender))
            {
                node.ExpandAll();
            }
        }

        private void Rename_Click(object sender, EventArgs e)
        {
            WzNode currentNode = currNode;

            getMainPanel().PromptRenameWzTreeNode(currentNode);
        }

        private void Remove_Click(object sender, EventArgs e)
        {
            getMainPanel().PromptRemoveSelectedTreeNodes();
        }

        private void Unload_Click(object sender, EventArgs e)
        {
            if (!Warning.Warn(UiLocalization.Translate("Are you sure you want to unload this?")))
                return;

            var nodesSelected = GetNodes(sender);
            foreach (WzNode node in nodesSelected)
            {
                if (node.Tag is VirtualWzDirectory virtualDir)
                {
                    // For VirtualWzDirectory, just remove from tree and dispose
                    virtualDir.Dispose();
                    node.Remove();
                }
                else if (node.Tag is WzFile)
                {
                    getMainPanel().MainForm.UnloadWzFile(node.Tag as WzFile);
                }
                else if (node.Tag is WzImage)
                {
                    getMainPanel().MainForm.UnloadWzImageFile(node.Tag as WzImage);
                }
            }
        }

        private void Reload_Click(object sender, EventArgs e)
        {
            if (!Warning.Warn(UiLocalization.Translate("Are you sure you want to reload this file?")))
                return;

            var nodesSelected = GetNodes(sender);
            foreach (WzNode node in nodesSelected) // selected nodes
            {
                getMainPanel().MainForm.ReloadWzFile(node.Tag as WzFile);
            }
        }

        private void SortChildNodesView_Click(object sender, EventArgs e)
        {
            foreach (WzNode node in GetNodes(sender))
            {
                getMainPanel().MainForm.SortNodesRecursively(node, true);
            }
        }

        private void SortPropertiesByName_Click(object sender, EventArgs e)
        {
            foreach (WzNode node in GetNodes(sender))
            {
                getMainPanel().MainForm.SortNodeProperties(node);
            }
        }

        private void AddImage_Click(object sender, EventArgs e)
        {
            WzNode[] nodes = GetNodes(sender);
            if (nodes.Length != 1)
            {
                MessageBox.Show(UiLocalization.Translate("Please select only one node."));
                return;
            }

            string name;
            if (NameInputBox.Show("Add Image", 0, out name))
                nodes[0].AddObject(new WzImage(name) { Changed = true }, getMainPanel().UndoRedoMan);
        }

        private void AddDirectory_Click(object sender, EventArgs e)
        {
            WzNode[] nodes = GetNodes(sender);
            if (nodes.Length != 1)
            {
                MessageBox.Show(UiLocalization.Translate("Please select only one node."));
                return;
            }
            getMainPanel().AddWzDirectoryToSelectedNode(nodes[0]);
        }

        private void AddByteFloat_Click(object sender, EventArgs e)
        {
            WzNode[] nodes = GetNodes(sender);
            if (nodes.Length != 1)
            {
                MessageBox.Show(UiLocalization.Translate("Please select only one node."));
                return;
            }

            getMainPanel().AddWzByteFloatToSelectedNode(nodes[0]);
        }

        private void AddCanvas_Click(object sender, EventArgs e)
        {
            WzNode[] nodes = GetNodes(sender);
            if (nodes.Length != 1)
            {
                MessageBox.Show(UiLocalization.Translate("Please select only one node."));
                return;
            }

            getMainPanel().AddWzCanvasToSelectedNode(nodes[0]);
        }

        private void AddLong_Click(object sender, EventArgs e)
        {
            WzNode[] nodes = GetNodes(sender);
            if (nodes.Length != 1)
            {
                MessageBox.Show(UiLocalization.Translate("Please select only one node."));
                return;
            }
            getMainPanel().AddWzLongToSelectedNode(nodes[0]);
        }

        private void AddInt_Click(object sender, EventArgs e)
        {
            WzNode[] nodes = GetNodes(sender);
            if (nodes.Length != 1)
            {
                MessageBox.Show(UiLocalization.Translate("Please select only one node."));
                return;
            }
            getMainPanel().AddWzCompressedIntToSelectedNode(nodes[0]);
        }

        private void AddConvex_Click(object sender, EventArgs e)
        {
            WzNode[] nodes = GetNodes(sender);
            if (nodes.Length != 1)
            {
                MessageBox.Show(UiLocalization.Translate("Please select only one node."));
                return;
            }

            getMainPanel().AddWzConvexPropertyToSelectedNode(nodes[0]);
        }

        private void AddDouble_Click(object sender, EventArgs e)
        {
            WzNode[] nodes = GetNodes(sender);
            if (nodes.Length != 1)
            {
                MessageBox.Show(UiLocalization.Translate("Please select only one node."));
                return;
            }
            getMainPanel().AddWzDoublePropertyToSelectedNode(nodes[0]);
        }

        private void AddNull_Click(object sender, EventArgs e)
        {
            WzNode[] nodes = GetNodes(sender);
            if (nodes.Length != 1)
            {
                MessageBox.Show(UiLocalization.Translate("Please select only one node."));
                return;
            }

            getMainPanel().AddWzNullPropertyToSelectedNode(nodes[0]);
        }

        private void AddSound_Click(object sender, EventArgs e)
        {
            WzNode[] nodes = GetNodes(sender);
            if (nodes.Length != 1)
            {
                MessageBox.Show(UiLocalization.Translate("Please select only one node."));
                return;
            }

            getMainPanel().AddWzSoundPropertyToSelectedNode(nodes[0]);
        }

        private void AddString_Click(object sender, EventArgs e)
        {
            WzNode[] nodes = GetNodes(sender);
            if (nodes.Length != 1)
            {
                MessageBox.Show(UiLocalization.Translate("Please select only one node."));
                return;
            }

            getMainPanel().AddWzStringPropertyToSelectedIndex(nodes[0]);
        }

        private void AddSub_Click(object sender, EventArgs e)
        {
            WzNode[] nodes = GetNodes(sender);
            if (nodes.Length != 1)
            {
                MessageBox.Show(UiLocalization.Translate("Please select only one node."));
                return;
            }

            getMainPanel().AddWzSubPropertyToSelectedIndex(nodes[0]);
        }

        private void AddUshort_Click(object sender, EventArgs e)
        {
            WzNode[] nodes = GetNodes(sender);
            if (nodes.Length != 1)
            {
                MessageBox.Show(UiLocalization.Translate("Please select only one node."));
                return;
            }

            getMainPanel().AddWzUnsignedShortPropertyToSelectedIndex(nodes[0]);
        }

        private void AddUOL_Click(object sender, EventArgs e)
        {
            WzNode[] nodes = GetNodes(sender);
            if (nodes.Length != 1)
            {
                MessageBox.Show(UiLocalization.Translate("Please select only one node."));
                return;
            }
            getMainPanel().AddWzUOLPropertyToSelectedIndex(nodes[0]);
        }

        private void AddVector_Click(object sender, EventArgs e)
        {
            WzNode[] nodes = GetNodes(sender);
            if (nodes.Length != 1)
            {
                MessageBox.Show(UiLocalization.Translate("Please select only one node."));
                return;
            }
            getMainPanel().AddWzVectorPropertyToSelectedIndex(nodes[0]);
        }

        private void Animate_Click(object sender, EventArgs e)
        {
            getMainPanel().StartAnimateSelectedCanvas();
        }

        private void SaveAnimation_Click(object sender, EventArgs e)
        {
            getMainPanel().SaveImageAnimation_Click();
        }

        private void FixInlink_Click(object sender, EventArgs e)
        {
            getMainPanel().FixLinkForOldMapleStory_OnClick();
        }

        private void ConvertToBgra32_Click(object sender, EventArgs e)
        {
            // GetNodes(sender) resolves to the node this context menu was actually built for
            // (see CreateMenu), i.e. whichever tab/tree the user right-clicked in. This static
            // overload needs nothing else from any particular MainPanel instance.
            MainPanel.ConvertImagesToBgra32(GetNodes(sender));
        }

        private void ResizeImage_Click(object sender, EventArgs e)
        {
            MainPanel.ResizeImagesByPercent(GetNodes(sender));
        }

        private void BatchSetValuesByNodeName_Click(object sender, EventArgs e)
        {
            getMainPanel().BatchSetValuesByNodeName();
        }

        /// <summary>
        /// Opens the AI assistant already pointed at whichever node was right-clicked. The path
        /// is seeded into the assistant's input box, so the user still sees and edits the exact
        /// text that gets sent.
        /// </summary>
        private void AskAiAboutNode_Click(object sender, EventArgs e)
        {
            MainPanel panel = getMainPanel();
            if (panel == null)
                return;
            HaRepacker.GUI.MainForm.ShowAiAssistantWindow(panel, BuildAiNodePath(panel.DataTree.SelectedNode));
        }

        /// <summary>
        /// Walks a node back to its WZ file root. TreeNode.FullPath would do this, but it throws
        /// once a node is detached and depends on the TreeView's PathSeparator; the assistant's
        /// path parser expects a backslash either way.
        /// </summary>
        private static string BuildAiNodePath(System.Windows.Forms.TreeNode node)
        {
            if (node == null)
                return null;
            System.Text.StringBuilder path = new System.Text.StringBuilder();
            System.Windows.Forms.TreeNode current = node;
            while (current != null)
            {
                if (path.Length > 0)
                    path.Insert(0, '\\');
                path.Insert(0, current.Text);
                current = current.Parent;
            }
            return path.ToString();
        }

        /// <summary>
        /// Opens the read-only "地圖物件資訊" summary for every valid map WzImage among the
        /// current selection (falling back to just the right-clicked node if the tree doesn't
        /// report a multi-selection - see GetMapObjectInfoCandidateNodes). Purely a read + a
        /// modal window: no WzObject is touched.
        /// </summary>
        private void MapObjectInfo_Click(object sender, EventArgs e)
        {
            MainPanel panel = getMainPanel();
            List<WzNode> candidates = GetMapObjectInfoCandidateNodes(panel, currNode);
            MapObjectInfoResult result = MapObjectInfoBuilder.Build(candidates);
            if (result.SelectedMaps.Count == 0)
                return; // CreateMenu only offers this item when a valid map is present; this is just the same safety net at click time.

            MapObjectInfoWindow.Show(result);
        }

        /// <summary>
        /// The tree's own multi-selection (TreeViewMS.SelectedNodes) if it has anything, else the
        /// single node this context menu was actually built for. A plain right-click collapses
        /// TreeViewMS's selection to just the clicked node (see TreeViewMS.OnAfterSelect), so in
        /// practice this is "the selection" whenever it truly is multiple nodes, and "the clicked
        /// node" otherwise - either way it's the tree's own selection state, not a separate one.
        /// </summary>
        private static List<WzNode> GetMapObjectInfoCandidateNodes(MainPanel panel, WzNode fallbackNode)
        {
            List<WzNode> nodes = new List<WzNode>();
            System.Collections.ArrayList selectedNodes = panel?.DataTree?.SelectedNodes;
            if (selectedNodes != null)
            {
                foreach (object selected in selectedNodes)
                {
                    if (selected is WzNode wzNode)
                        nodes.Add(wzNode);
                }
            }
            if (nodes.Count == 0 && fallbackNode != null)
                nodes.Add(fallbackNode);
            return nodes;
        }

        private void BatchOffsetNodeNames_Click(object sender, EventArgs e)
        {
            getMainPanel().BatchOffsetNodeNames();
        }

        private void BatchReplaceText_Click(object sender, EventArgs e)
        {
            getMainPanel().BatchReplaceText();
        }

        private void BatchReplaceOrDeleteText_Click(object sender, EventArgs e)
        {
            getMainPanel().BatchReplaceOrDeleteText();
        }

        private void BatchDeleteNodesByName_Click(object sender, EventArgs e)
        {
            getMainPanel().BatchDeleteNodesByName();
        }

        private void BatchCleanupStringWz_Click(object sender, EventArgs e)
        {
            getMainPanel().BatchCleanupStringWz();
        }

        private void BatchCoverFolderImages_Click(object sender, EventArgs e)
        {
            getMainPanel().BatchCoverFolderImages();
        }

        private void BatchImportFolderImages_Click(object sender, EventArgs e)
        {
            getMainPanel().BatchImportFolderImages();
        }

        private void ResizeImageToBgra32_Click(object sender, EventArgs e)
        {
            MainPanel.ResizeImagesByPercentToBgra32(GetNodes(sender));
        }

        private void AiUpscaleQualityOnly_Click(object sender, EventArgs e)
        {
            getMainPanel().AiBatchImageUpscaleEdit(0.25f);
        }

        private void AiUpscale1_5x_Click(object sender, EventArgs e)
        {
            getMainPanel().AiBatchImageUpscaleEdit(0.375f);
        }

        private void AiUpscale2x_Click(object sender, EventArgs e)
        {
            getMainPanel().AiBatchImageUpscaleEdit(0.5f);
        }

        private void AiUpscale4x_Click(object sender, EventArgs e)
        {
            getMainPanel().AiBatchImageUpscaleEdit(1f);
        }

        /// <summary>
        /// Toolstrip menu when right clicking on nodes
        /// </summary>
        /// <param name="node"></param>
        /// <param name="Tag"></param>
        /// <returns></returns>
        public ContextMenuStrip CreateMenu(WzNode node, WzObject Tag)
        {
            int currentDataTreeSelectedCount = getMainPanel().DataTree.SelectedNodes.Count;

            List<ToolStripItem> toolStripmenuItems = new List<ToolStripItem>();

            ContextMenuStrip menu = new ContextMenuStrip();
            if (Tag is WzImage || Tag is IPropertyContainer)
            {
                toolStripmenuItems.Add(AddPropsSubMenu);
                toolStripmenuItems.Add(Rename);
                // Add SaveImg and DeleteImgFile options if from VirtualWzDirectory
                if (IsFromVirtualWzDirectory(Tag))
                {
                    toolStripmenuItems.Add(SaveImg);
                    if (Tag is WzImage)
                    {
                        toolStripmenuItems.Add(DeleteImgFile);
                    }
                }
                else
                {
                    // export, import
                    toolStripmenuItems.Add(Remove);
                }
            }
            else if (Tag is WzImageProperty)
            {
                toolStripmenuItems.Add(Rename);
                // Add SaveImg option if from VirtualWzDirectory
                if (IsFromVirtualWzDirectory(Tag))
                {
                    toolStripmenuItems.Add(SaveImg);
                }
                toolStripmenuItems.Add(Remove);
            }
            else if (Tag is VirtualWzDirectory)
            {
                toolStripmenuItems.Add(CreateNewImgFile);
                toolStripmenuItems.Add(AddDirsSubMenu);
                toolStripmenuItems.Add(Rename);
                toolStripmenuItems.Add(SaveImg);
                toolStripmenuItems.Add(Unload);
            }
            else if (Tag is WzDirectory)
            {
                toolStripmenuItems.Add(AddDirsSubMenu);
                toolStripmenuItems.Add(Rename);
                toolStripmenuItems.Add(Remove);
            }
            else if (Tag is WzFile)
            {
                toolStripmenuItems.Add(AddDirsSubMenu);
                toolStripmenuItems.Add(Rename);
                toolStripmenuItems.Add(SaveFile);
                toolStripmenuItems.Add(Unload);
                toolStripmenuItems.Add(Reload);
            }

            toolStripmenuItems.Add(ExpandAllChildNode);
            toolStripmenuItems.Add(CollapseAllChildNode);

            toolStripmenuItems.Add(AddBatchMenu);
            toolStripmenuItems.Add(AddNodeBatchMenu);
            toolStripmenuItems.Add(AskAiAboutNode);

            // Only offered when at least one valid map WzImage (single or multi-selected) is
            // among the current selection - see MapObjectInfoBuilder.IsMapImageNode.
            List<WzNode> mapObjectInfoCandidates = GetMapObjectInfoCandidateNodes(getMainPanel(), node);
            foreach (WzNode candidate in mapObjectInfoCandidates)
            {
                if (MapObjectInfoBuilder.IsMapImageNode(candidate))
                {
                    toolStripmenuItems.Add(MapObjectInfoMenuItem);
                    break;
                }
            }

            if (Tag is WzCanvasProperty)
            {
                toolStripmenuItems.Add(Animate);
            }

            if (Tag.GetType() == typeof(WzSubProperty)) {
                toolStripmenuItems.Add(SaveAnimation);
                toolStripmenuItems.Add(AddSortMenu);
            } else {
                toolStripmenuItems.Add(AddSortMenu_WithoutPropSort);
            }

            // Add
            foreach (ToolStripItem toolStripItem in toolStripmenuItems)
            {
                menu.Items.Add(toolStripItem);
            }

            currNode = node;
            return menu;
        }

        private WzNode currNode = null;

        private WzNode[] GetNodes(object sender)
        {
            return new WzNode[] { currNode };
        }

        /// <summary>
        /// Saves a node from a VirtualWzDirectory to the IMG filesystem
        /// </summary>
        private void SaveImgNode(WzNode node)
        {
            WzObject tag = (WzObject)node.Tag;

            // Find the parent VirtualWzDirectory
            WzObject current = tag;
            VirtualWzDirectory virtualDir = null;

            while (current != null)
            {
                if (current.Parent is VirtualWzDirectory vDir)
                {
                    virtualDir = vDir;
                    break;
                }
                current = current.Parent;
            }

            if (virtualDir == null)
            {
                // Check if tag itself is the VirtualWzDirectory
                if (tag is VirtualWzDirectory vd)
                {
                    virtualDir = vd;
                }
            }

            if (virtualDir == null)
            {
                MessageBox.Show(T("This item is not from an IMG filesystem directory."),
                    T("Cannot Save"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                if (tag is ImgFileWzImageReference imgRef)
                {
                    // Resolve to an actual WzImage so Save works as expected if the image was edited.
                    // (If it was never loaded/changed, this will effectively be a no-op save.)
                    var resolved = imgRef.Resolve();
                    if (resolved != null)
                    {
                        resolved.HRTag = node;
                        node.Tag = resolved;
                        tag = resolved;
                    }
                }

                if (tag is WzImage image)
                {
                    // Save single image
                    if (virtualDir.SaveImage(image))
                    {
                        MessageBox.Show(TF("Saved {0} successfully.", image.Name),
                            T("Save Complete"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                        node.ForeColor = System.Drawing.Color.Black; // Reset color
                    }
                    else
                    {
                        MessageBox.Show(TF("Failed to save {0}.", image.Name),
                            T("Save Failed"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                else if (tag is VirtualWzDirectory vDir)
                {
                    // Save all changed images in directory
                    int savedCount = vDir.SaveAllChangedImages();
                    if (savedCount > 0)
                    {
                        MessageBox.Show(TF("Saved {0} changed image(s) successfully.", savedCount),
                            T("Save Complete"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        MessageBox.Show(T("No changed images to save."),
                            T("Save Complete"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                else if (tag is WzImageProperty prop)
                {
                    // Save the parent image
                    if (prop.ParentImage != null)
                    {
                        if (virtualDir.SaveImage(prop.ParentImage))
                        {
                            MessageBox.Show(TF("Saved {0} successfully.", prop.ParentImage.Name),
                                T("Save Complete"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            MessageBox.Show(TF("Failed to save {0}.", prop.ParentImage.Name),
                                T("Save Failed"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(TF("Error saving: {0}", ex.Message),
                    T("Save Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Checks if a WzObject is from a VirtualWzDirectory
        /// </summary>
        private bool IsFromVirtualWzDirectory(WzObject obj)
        {
            if (obj is VirtualWzDirectory)
                return true;

            WzObject current = obj;
            while (current != null)
            {
                if (current.Parent is VirtualWzDirectory)
                    return true;
                current = current.Parent;
            }
            return false;
        }

        /// <summary>
        /// Creates a new IMG file in a VirtualWzDirectory
        /// </summary>
        private void CreateNewImgFileInDirectory(WzNode node)
        {
            WzObject tag = (WzObject)node.Tag;

            VirtualWzDirectory virtualDir = tag as VirtualWzDirectory;
            if (virtualDir == null)
            {
                MessageBox.Show(T("Please select a directory from an IMG filesystem."),
                    T("Cannot Create File"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Prompt for new file name
            string name;
            if (!NameInputBox.Show(T("Create New IMG File"), 0, out name))
                return;

            // Ensure .img extension
            if (!name.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                name += ".img";

            // Check if file already exists
            if (virtualDir.ImageExists(name))
            {
                MessageBox.Show(TF("A file named '{0}' already exists in this directory.", name),
                    T("File Exists"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                // Create the new IMG file
                string relativePath = name;
                if (!string.IsNullOrEmpty(virtualDir.RelativePath))
                {
                    relativePath = Path.Combine(virtualDir.RelativePath, name);
                }

                WzImage newImage = virtualDir.Manager.CreateImage(virtualDir.CategoryName, relativePath);
                if (newImage != null)
                {
                    // Add to tree
                    WzNode newNode = new WzNode(newImage, true);
                    node.Nodes.Add(newNode);
                    WzNode.EnsureVisibleIfDisplayed(newNode);

                    MessageBox.Show(TF("Created '{0}' successfully.", name),
                        T("File Created"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show(TF("Failed to create '{0}'.", name),
                        T("Creation Failed"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(TF("Error creating file: {0}", ex.Message),
                    T("Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Deletes an IMG file from the filesystem
        /// </summary>
        private void DeleteImgFileFromDirectory(WzNode node)
        {
            WzObject tag = (WzObject)node.Tag;

            WzImage image = tag as WzImage;
            if (image == null && tag is ImgFileWzImageReference imgRef)
            {
                // Keep deletion cheap: we only need a name for the prompt and relative path construction.
                image = new WzImage(imgRef.FileName) { Changed = false };
            }

            if (image == null)
            {
                MessageBox.Show(T("Please select an IMG file to delete."),
                    T("Cannot Delete"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Find parent VirtualWzDirectory
            VirtualWzDirectory virtualDir = null;
            WzObject current = tag;
            while (current != null)
            {
                if (current.Parent is VirtualWzDirectory vDir)
                {
                    virtualDir = vDir;
                    break;
                }
                current = current.Parent;
            }

            if (virtualDir == null)
            {
                MessageBox.Show(T("This file is not from an IMG filesystem directory."),
                    T("Cannot Delete"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Confirm deletion
            DialogResult result = MessageBox.Show(
                TF("Are you sure you want to delete '{0}'?\n\nThis will permanently delete the file from disk.", image.Name),
                T("Confirm Delete"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result != DialogResult.Yes)
                return;

            try
            {
                // Build relative path
                string relativePath = image.Name;
                if (!string.IsNullOrEmpty(virtualDir.RelativePath))
                {
                    relativePath = Path.Combine(virtualDir.RelativePath, image.Name);
                }

                if (virtualDir.Manager.DeleteImage(virtualDir.CategoryName, relativePath))
                {
                    // Remove from tree
                    node.Remove();

                    MessageBox.Show(TF("Deleted '{0}' successfully.", image.Name),
                        T("File Deleted"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show(TF("Failed to delete '{0}'.", image.Name),
                        T("Deletion Failed"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(TF("Error deleting file: {0}", ex.Message),
                    T("Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
