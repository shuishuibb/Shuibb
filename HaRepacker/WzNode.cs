using System;
using System.Windows.Forms;
using MapleLib.Img;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using System.Collections;
using System.Drawing;
using HaRepacker.Comparer;
using HaRepacker.GUI;
using System.IO;
using System.Linq;

namespace HaRepacker
{
    public class WzNode : TreeNode
    {
        internal sealed class LazyLoadPlaceholder { }
        internal static readonly LazyLoadPlaceholder LazyLoadPlaceholderTag = new LazyLoadPlaceholder();

        public delegate ContextMenuStrip ContextMenuBuilderDelegate(WzNode node, WzObject obj);
        public static ContextMenuBuilderDelegate ContextMenuBuilder = null;

        private bool isWzObjectAddedManually = false;

        // constants
        public static Color CHANGED_NODE_FOREGROUND_COLOR = Color.Red;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="SourceObject"></param>
        /// <param name="isWzObjectAddedManually"></param>
        public WzNode(WzObject SourceObject, bool isWzObjectAddedManually = false)
            : base(SourceObject.Name)
        {
            this.isWzObjectAddedManually = isWzObjectAddedManually;
            if (isWzObjectAddedManually)
            {
                ForeColor = CHANGED_NODE_FOREGROUND_COLOR;
            }
            // Childs
            ParseChilds(SourceObject);
        }

        private void ParseChilds(WzObject SourceObject)
        {
            Tag = SourceObject ?? throw new NullReferenceException("Cannot create a null WzNode");
            SourceObject.HRTag = this;

            if (SourceObject is WzFile)
                SourceObject = ((WzFile)SourceObject).WzDirectory;

            // Handle VirtualWzDirectory specifically (must check before WzDirectory since it inherits from it)
            if (SourceObject is VirtualWzDirectory virtualDir)
            {
                // Lazy-load filesystem directories: expanding a big export should not recursively enumerate
                // and parse every IMG file (can take minutes).
                if (VirtualDirectoryMayHaveChildren(virtualDir))
                {
                    Nodes.Add(new TreeNode("...") { Tag = LazyLoadPlaceholderTag });
                }
            }
            else if (SourceObject is WzDirectory wzDir)
            {
                foreach (WzDirectory dir in wzDir.WzDirectories)
                    Nodes.Add(new WzNode(dir));
                foreach (WzImage img in wzDir.WzImages)
                    Nodes.Add(new WzNode(img));
            }
            else if (SourceObject is WzImage image)
            {
                if (image.Parsed)
                    foreach (WzImageProperty prop in image.WzProperties)
                        Nodes.Add(new WzNode(prop));
            }
            else if (SourceObject is IPropertyContainer container)
            {
                foreach (WzImageProperty prop in container.WzProperties)
                    Nodes.Add(new WzNode(prop));
            }
        }

        private static bool VirtualDirectoryMayHaveChildren(VirtualWzDirectory virtualDir)
        {
            try
            {
                if (virtualDir == null)
                    return false;

                string path = virtualDir.FilesystemPath;
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                    return false;

                if (Directory.EnumerateDirectories(path).Take(1).Any())
                    return true;

                if (Directory.EnumerateFiles(path, "*.img").Take(1).Any())
                    return true;

                return false;
            }
            catch
            {
                // If we can't check quickly, assume it might have children so the user can try expanding.
                return true;
            }
        }

        public void DeleteWzNode()
        {
            Remove();

            if (Tag is WzImageProperty property)
            {
                if (property.ParentImage == null) // _inlink WzNode doesnt have a parent
                    return;
                property.ParentImage.Changed = true;
            }
            ((WzObject)Tag).Remove();
        }

        public bool IsWzObjectAddedManually
        {
            get
            {
                return isWzObjectAddedManually;
            }
            private set { }
        }

        public bool CanHaveChilds
        {
            get
            {
                return (Tag is WzFile ||
                    Tag is WzDirectory ||
                    Tag is WzImage ||
                    Tag is IPropertyContainer);
            }
        }

        /// <summary>
        /// EnsureVisible() only means anything for a TreeView that is actually on screen, and
        /// calling it on one that is not forces the control to create its native window handle.
        /// MainPanel keeps its TreeViewMS purely as a data model - the tree the user sees is the
        /// WPF one - so that handle is pure overhead, and an expensive kind: once it exists every
        /// node insert has to cross into the native control, which takes reparsing a big IMG
        /// (String.wz/Skill.img is ~42k nodes) from ~40ms to ~13s. Scrolling the visible node into
        /// view is the WPF side's job (TreeViewItem.BringIntoView).
        /// </summary>
        /// <summary>
        /// Sorts a node's children in place, once.
        ///
        /// Assigning TreeView.TreeViewNodeSorter switches the TreeView into Sorted mode and LEAVES
        /// it there, so from then on every single Nodes.Add is a sorted insert. With the "Sort"
        /// option on, adding one big WZ file (Map002.wz - 13,978 nodes) costs ~840ms instead of
        /// ~10ms, and it is paid again for every file loaded and every IMG reparsed.
        /// Sorting explicitly and handing the tree back unsorted produces the same order for a
        /// fraction of the cost.
        /// </summary>
        public static void SortChildNodes(TreeNode node, IComparer comparer, bool recursive)
        {
            if (node == null)
                return;
            SortNodeCollection(node.Nodes, comparer, recursive);
        }

        /// <summary>
        /// Same, for a bare collection - needed for TreeView.Nodes itself, so the list of loaded
        /// WZ files keeps the alphabetical order that Sorted mode used to give it.
        /// </summary>
        public static void SortNodeCollection(TreeNodeCollection nodes, IComparer comparer, bool recursive)
        {
            if (nodes == null || comparer == null)
                return;

            // Rebuilding a level costs far more than checking it: Clear()/AddRange() tear down and
            // re-attach every TreeNode, and a parsed .ms file is thousands of levels deep. Most WZ
            // levels are already in order (or have a single child), so an O(n) comparison scan skips
            // nearly all of the churn.
            if (nodes.Count > 1 && !IsNodeCollectionSorted(nodes, comparer))
            {
                TreeNode[] children = new TreeNode[nodes.Count];
                nodes.CopyTo(children, 0);
                Array.Sort(children, comparer);
                nodes.Clear();
                nodes.AddRange(children);
            }

            if (!recursive)
                return;

            foreach (TreeNode child in nodes)
                SortNodeCollection(child.Nodes, comparer, true);
        }

        private static bool IsNodeCollectionSorted(TreeNodeCollection nodes, IComparer comparer)
        {
            for (int i = 1; i < nodes.Count; i++)
            {
                if (comparer.Compare(nodes[i - 1], nodes[i]) > 0)
                    return false;
            }
            return true;
        }

        public static void EnsureVisibleIfDisplayed(TreeNode node)
        {
            if (node == null)
                return;

            TreeView owner = node.TreeView;
            if (owner != null && owner.IsHandleCreated)
                node.EnsureVisible();
        }

        public static WzNode GetChildNode(WzNode parentNode, string name)
        {
            foreach (WzNode node in parentNode.Nodes)
                if (node.Text == name)
                    return node;
            return null;
        }

        public static bool CanNodeBeInserted(WzNode parentNode, string name)
        {
            WzObject obj = (WzObject)parentNode.Tag;
            if (obj is IPropertyContainer container) 
                return container[name] == null;
            else if (obj is WzDirectory directory) 
                return directory[name] == null;
            else if (obj is WzFile file) 
                return file.WzDirectory?[name] == null;
            else 
                return false;
        }

        private bool AddObjInternal(WzObject obj)
        {
            WzObject TaggedObject = (WzObject)Tag;
            if (TaggedObject is WzFile file) 
                TaggedObject = file.WzDirectory;
            
            if (TaggedObject is WzDirectory directory)
            {
                if (obj is WzDirectory wzDirectory)
                    directory.AddDirectory(wzDirectory);
                else if (obj is WzImage wzImgProperty)
                    directory.AddImage(wzImgProperty);
                else
                    return false;
            }
            else if (TaggedObject is WzImage wzImageProperty)
            {
                if (!wzImageProperty.Parsed) 
                    wzImageProperty.ParseImage();
                if (obj is WzImageProperty imgProperty)
                {
                    wzImageProperty.AddProperty(imgProperty);
                    wzImageProperty.Changed = true;
                }
                else 
                    return false;
            }
            else if (TaggedObject is IPropertyContainer container)
            {
                if (obj is WzImageProperty property)
                {
                    container.AddProperty(property);
                    if (TaggedObject is WzImageProperty imgProperty)
                        imgProperty.ParentImage.Changed = true;
                }
                else 
                    return false;
            }
            else 
                return false;

            return true;
        }

        /// <summary>
        /// Adds a node
        /// </summary>
        /// <param name="node"></param>
        /// <returns></returns>
        public bool AddNode(WzNode node, bool reparseImage)
        {
            if (CanNodeBeInserted(this, node.Text))
            {
                TryParseImage(reparseImage);
                this.Nodes.Add(node);
                AddObjInternal((WzObject)node.Tag);
                return true;
            }
            else
            {
                MessageBox.Show(string.Format(UiLocalization.Translate("Cannot insert node \"{0}\" because a node with the same name already exists. Skipping."), node.Text), UiLocalization.Translate("Skipping Node"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
        }

        /// <summary>
        /// Try parsing the WzImage if it have not been loaded
        /// </summary>
        private void TryParseImage(bool reparseImage = true)
        {
            if (Tag is WzImage)
            {
                ((WzImage)Tag).ParseImage();
                if (reparseImage)
                {
                    Reparse();
                }
            }
        }

        /// <summary>
        /// Adds a WzObject to the WzNode and returns the newly created WzNode
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="undoRedoMan"></param>
        /// <returns></returns>
        public WzNode AddObject(WzObject obj, UndoRedoManager undoRedoMan)
        {
            if (CanNodeBeInserted(this, obj.Name))
            {
                TryParseImage();
                if (AddObjInternal(obj))
                {
                    WzNode node = new WzNode(obj, true);
                    Nodes.Add(node);

                    if (node.Tag is WzImageProperty property)
                    {
                        property.ParentImage.Changed = true;
                    }
                    undoRedoMan.AddUndoBatch(new System.Collections.Generic.List<UndoRedoAction> { UndoRedoManager.ObjectAdded(this, node) });
                    EnsureVisibleIfDisplayed(node);
                    return node;
                }
                else
                {
                    Warning.Error(UiLocalization.Translate("Could not insert property; make sure all types are correct."));
                    return null;
                }
            }
            else
            {
                MessageBox.Show(string.Format(UiLocalization.Translate("Cannot insert object \"{0}\" because an object with the same name already exists. Skipping."), obj.Name), UiLocalization.Translate("Skipping Object"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }
        }

        public void Reparse()
        {
            Nodes.Clear();
            ParseChilds((WzObject)Tag);

            // The TreeView is no longer left in Sorted mode (see SortChildNodes), so the order the
            // "Sort" option promises has to be applied here explicitly - once, instead of once per
            // inserted child.
            if (Program.ConfigurationManager != null &&
                Program.ConfigurationManager.UserSettings != null &&
                Program.ConfigurationManager.UserSettings.Sort)
            {
                SortChildNodes(this, new TreeViewNodeSorter(null), false);
            }
        }

        public string GetTypeName()
        {
            return Tag.GetType().Name;
        }

        /// <summary>
        /// Change the name of the WzNode
        /// </summary>
        /// <param name="name"></param>
        public void ChangeName(string name)
        {
            Text = name;
            ((WzObject)Tag).Name = name;

            ChangedNodeProperty();
        }

        /// <summary>
        /// Flags this node as changed
        /// </summary>
        public void ChangedNodeProperty() {
            if (Tag is WzImageProperty property)
                property.ParentImage.Changed = true;

            isWzObjectAddedManually = true;
            ForeColor = CHANGED_NODE_FOREGROUND_COLOR;
        }

        public WzNode TopLevelNode
        {
            get
            {
                WzNode parent = this;
                while (parent.Level > 0)
                {
                    parent = (WzNode)parent.Parent;
                }
                return parent;
            }
        }

        public override ContextMenuStrip ContextMenuStrip
        {
            get
            {
                return ContextMenuBuilder == null ? null : ContextMenuBuilder(this, (WzObject)Tag);
            }
            set
            {
                base.ContextMenuStrip = value;
            }
        }
    }
}
