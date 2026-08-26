using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using HaRepacker;
using HaRepacker.GUI.Panels;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace TokiAi
{
    public enum PendingChangeKind
    {
        SetValue,
        Rename,
        Delete,
        Add,
        Copy,
        ImportImage
    }

    /// <summary>
    /// One edit the model has proposed but nobody has agreed to yet. Holds enough of the before
    /// state to show a diff, and - once applied - enough to put it back.
    /// </summary>
    public class PendingChange
    {
        public readonly PendingChangeKind Kind;
        public readonly string Path;

        // SetValue/Rename/Delete: the node itself. Add/Copy: the parent it goes under.
        public readonly WzNode Target;

        // Which editor tab this edit lands in - the tree, the undo manager and the refresh all
        // belong to that tab's MainPanel, and a port writes into a different tab than it reads.
        public readonly WzTab Tab;

        public string OldValue;
        public string NewValue;

        // Add / Copy.
        public string AddName;
        public string AddType;
        public string AddValue;

        // Copy only.
        public WzNode CopySource;
        public bool CopyOverwrites;

        // ImportImage only. The bitmap is read from disk at apply time, not now, so a queued
        // import always reflects the file as it stands when the user agrees to it.
        public string ImportFile;
        public string[] ImportParts;
        public bool ImportReplaces;
        System.Drawing.Bitmap replacedBitmap;   // kept so the import can be undone
        WzPngFormat? replacedFormat;            // …along with the surface format it had
        int createdContainerDepth = -1;         // first ImportParts index this change had to create

        /// <summary>Surface format actually written, and why it differs from the original if it does.</summary>
        public WzPngFormat WrittenFormat;
        public string FormatNote;

        // Filled in when applied, so the change can be reverted.
        public bool Applied;
        public WzNode CreatedNode;      // Add / Copy
        public WzNode DeletedNode;      // Delete, and the node a Copy overwrote
        public WzNode DeletedParent;    // Delete

        public PendingChange(PendingChangeKind kind, string path, WzNode target, WzTab tab)
        {
            Kind = kind;
            Path = path;
            Target = target;
            Tab = tab;
        }

        public MainPanel Panel
        {
            get { return Tab == null ? null : Tab.Panel; }
        }

        /// <summary>
        /// Finds the node this change created, by name, right now.
        ///
        /// A reference captured at apply time cannot be trusted: WzNode.AddObject calls
        /// TryParseImage on its target, and for a .img that means Reparse() - Nodes.Clear()
        /// followed by a full rebuild. So adding a second thing under the same image silently
        /// replaces every child WzNode object, orphaning the one the first change is holding,
        /// and its revert then detaches the WzObject while leaving a stale node in the tree.
        /// </summary>
        WzNode LocateCreated()
        {
            if (Target == null)
                return null;
            if (Kind == PendingChangeKind.ImportImage)
            {
                if (ImportParts == null)
                    return null;
                WzNode current = Target;
                foreach (string part in ImportParts)
                {
                    current = WzNode.GetChildNode(current, part);
                    if (current == null)
                        return null;
                }
                return current;
            }
            return string.IsNullOrEmpty(AddName) ? null : WzNode.GetChildNode(Target, AddName);
        }

        public string KindText
        {
            get
            {
                switch (Kind)
                {
                    case PendingChangeKind.Rename: return "改名";
                    case PendingChangeKind.Delete: return "刪除";
                    case PendingChangeKind.Add: return "新增";
                    case PendingChangeKind.Copy: return "複製";
                    case PendingChangeKind.ImportImage: return "匯入圖";
                    default: return "改值";
                }
            }
        }

        /// <summary>
        /// Performs the edit. Returns false with a reason when the tree moved under us between
        /// the proposal and the confirmation - the node may have been deleted or renamed by hand.
        /// </summary>
        public bool Apply(UndoRedoManager undoRedoMan, out string error)
        {
            error = null;
            try
            {
                switch (Kind)
                {
                    case PendingChangeKind.SetValue:
                        if (!IsStillInTree(Target))
                        {
                            error = "節點已經不在樹裡了(可能已被刪除或搬移)。";
                            return false;
                        }
                        string current = WzTools.RawValue(Target.Tag as WzObject);
                        if (current != OldValue)
                        {
                            // Not fatal - just make the log honest about what it overwrote.
                            OldValue = current;
                        }
                        if (!WzValueWriter.Apply(Target, NewValue, out error))
                            return false;
                        break;

                    case PendingChangeKind.Rename:
                        if (!IsStillInTree(Target))
                        {
                            error = "節點已經不在樹裡了。";
                            return false;
                        }
                        if (Target.Parent != null && WzNode.GetChildNode((WzNode)Target.Parent, NewValue) != null)
                        {
                            error = "同層已經有叫 " + NewValue + " 的節點。";
                            return false;
                        }
                        OldValue = Target.Text;
                        Target.ChangeName(NewValue);
                        break;

                    case PendingChangeKind.Delete:
                        if (!IsStillInTree(Target))
                        {
                            error = "節點已經不在樹裡了。";
                            return false;
                        }
                        DeletedNode = Target;
                        DeletedParent = Target.Parent as WzNode;
                        Target.DeleteWzNode();
                        break;

                    case PendingChangeKind.Add:
                        if (!IsStillInTree(Target))
                        {
                            error = "父節點已經不在樹裡了。";
                            return false;
                        }
                        if (WzNode.GetChildNode(Target, AddName) != null)
                        {
                            error = "父節點底下已經有叫 " + AddName + " 的節點。";
                            return false;
                        }
                        WzObject created = WzValueWriter.Create(AddType, AddName, AddValue, Target, out error);
                        if (created == null)
                            return false;
                        CreatedNode = Target.AddObject(created, undoRedoMan);
                        if (CreatedNode == null)
                        {
                            error = "編輯器拒絕插入這個節點。";
                            return false;
                        }
                        CreatedNode.ChangedNodeProperty();
                        break;

                    case PendingChangeKind.Copy:
                        if (!IsStillInTree(Target))
                        {
                            error = "目標父節點已經不在樹裡了。";
                            return false;
                        }
                        if (CopySource == null || !IsStillInTree(CopySource))
                        {
                            error = "來源節點已經不在樹裡了(那個分頁可能已經關閉)。";
                            return false;
                        }

                        WzObject clone = CloneWzObject(CopySource.Tag as WzObject, out error);
                        if (clone == null)
                            return false;
                        clone.Name = AddName;

                        // Overwriting: keep the old node so the revert can put it back.
                        WzNode existing = WzNode.GetChildNode(Target, AddName);
                        if (existing != null)
                        {
                            DeletedNode = existing;
                            existing.DeleteWzNode();
                        }

                        CreatedNode = Target.AddObject(clone, undoRedoMan);
                        if (CreatedNode == null)
                        {
                            // Put the original back rather than leaving the tree short a node.
                            if (DeletedNode != null)
                            {
                                Target.AddNode(DeletedNode, true);
                                DeletedNode = null;
                            }
                            error = "編輯器拒絕插入複製的節點。";
                            return false;
                        }
                        CreatedNode.ChangedNodeProperty();
                        break;

                    case PendingChangeKind.ImportImage:
                        if (!IsStillInTree(Target))
                        {
                            error = "目標節點已經不在樹裡了。";
                            return false;
                        }
                        if (!File.Exists(ImportFile))
                        {
                            error = "找不到圖片檔了:" + ImportFile;
                            return false;
                        }
                        System.Drawing.Bitmap bitmap;
                        try
                        {
                            bitmap = DiskAccess.LoadBitmap(ImportFile);
                        }
                        catch (Exception loadError)
                        {
                            error = "讀不了圖片:" + loadError.Message;
                            return false;
                        }
                        if (!ApplyImage(bitmap, undoRedoMan, out error))
                            return false;
                        break;
                }
                Applied = true;
                return true;
            }
            catch (Exception failure)
            {
                error = failure.Message;
                return false;
            }
        }

        /// <summary>Undoes an applied change. Best effort - reports rather than throws.</summary>
        public bool Revert(out string error)
        {
            error = null;
            if (!Applied)
            {
                error = "這一項還沒套用。";
                return false;
            }
            try
            {
                switch (Kind)
                {
                    case PendingChangeKind.SetValue:
                        if (!IsStillInTree(Target))
                        {
                            error = "節點已經不在樹裡了。";
                            return false;
                        }
                        if (!WzValueWriter.Apply(Target, OldValue, out error))
                            return false;
                        break;

                    case PendingChangeKind.Rename:
                        if (!IsStillInTree(Target))
                        {
                            error = "節點已經不在樹裡了。";
                            return false;
                        }
                        Target.ChangeName(OldValue);
                        break;

                    case PendingChangeKind.Delete:
                        if (DeletedNode == null || DeletedParent == null)
                        {
                            error = "沒有留下足以還原的資料。";
                            return false;
                        }
                        if (!DeletedParent.AddNode(DeletedNode, true))
                        {
                            error = "無法把節點加回去(可能同名節點已存在)。";
                            return false;
                        }
                        break;

                    case PendingChangeKind.Add:
                        WzNode addedNode = LocateCreated();
                        if (addedNode == null)
                        {
                            error = "找不到當初新增的節點了(可能已被手動刪除或改名)。";
                            return false;
                        }
                        addedNode.DeleteWzNode();
                        CreatedNode = null;
                        break;

                    case PendingChangeKind.Copy:
                        WzNode copiedNode = LocateCreated();
                        if (copiedNode == null)
                        {
                            error = "找不到當初複製過來的節點了。";
                            return false;
                        }
                        copiedNode.DeleteWzNode();
                        CreatedNode = null;
                        // If the copy replaced something, put the original back.
                        if (DeletedNode != null && Target != null)
                        {
                            if (!Target.AddNode(DeletedNode, true))
                            {
                                error = "複本已移除,但無法把原本的節點加回去。";
                                return false;
                            }
                            DeletedNode = null;
                        }
                        break;

                    case PendingChangeKind.ImportImage:
                        WzNode imported = LocateCreated();
                        if (imported == null)
                        {
                            error = "找不到當初匯入的圖片節點了。";
                            return false;
                        }
                        if (ImportReplaces)
                        {
                            // Put the previous artwork back, in the format it was stored in -
                            // re-detecting on the way back would leave the canvas changed even
                            // though the user asked to undo.
                            if (imported.Tag is WzCanvasProperty canvas && replacedBitmap != null)
                            {
                                if (replacedFormat.HasValue)
                                    CanvasWriter.SetBitmapWithFormat(canvas, replacedBitmap, replacedFormat.Value);
                                else if (canvas.PngProperty != null)
                                    canvas.PngProperty.PNG = replacedBitmap;
                            }
                            imported.ChangedNodeProperty();
                        }
                        else
                        {
                            imported.DeleteWzNode();
                            RemoveEmptyCreatedContainers();
                        }
                        CreatedNode = null;
                        break;
                }
                Applied = false;
                return true;
            }
            catch (Exception failure)
            {
                error = failure.Message;
                return false;
            }
        }

        /// <summary>
        /// Puts a bitmap onto the target canvas, creating the canvas (and any intermediate sub
        /// properties) when it does not exist yet. Mirrors the editor's own folder-import rules.
        /// </summary>
        bool ApplyImage(System.Drawing.Bitmap bitmap, UndoRedoManager undoRedoMan, out string error)
        {
            error = null;
            WzNode parent = Target;

            for (int i = 0; i < ImportParts.Length - 1; i++)
            {
                WzTools.EnsureParsed(parent);
                WzNode next = WzNode.GetChildNode(parent, ImportParts[i]);
                if (next == null)
                {
                    if (parent.Tag is not WzImage && parent.Tag is not IPropertyContainer)
                    {
                        error = ImportParts[i] + " 的父節點不能有子節點。";
                        return false;
                    }
                    next = parent.AddObject(new WzSubProperty(ImportParts[i]), undoRedoMan);
                    if (next == null)
                    {
                        error = "無法建立中間節點 " + ImportParts[i] + "。";
                        return false;
                    }
                    if (createdContainerDepth < 0)
                        createdContainerDepth = i;
                }
                parent = next;
            }

            string canvasName = ImportParts[ImportParts.Length - 1];
            WzTools.EnsureParsed(parent);
            WzNode existing = WzNode.GetChildNode(parent, canvasName);

            if (existing != null)
            {
                if (existing.Tag is not WzCanvasProperty canvas)
                {
                    error = "同名節點不是圖片(" + (existing.Tag == null ? "?" : existing.Tag.GetType().Name) + ")。";
                    return false;
                }
                // Keep the old artwork so the import can be undone.
                try
                {
                    replacedBitmap = canvas.GetLinkedWzCanvasBitmap();
                }
                catch
                {
                    replacedBitmap = null;
                }

                // A canvas borrowing its artwork through a link has to give that up first, or
                // the link keeps winning on the next read.
                if (canvas.ContainsInlinkProperty())
                {
                    canvas.RemoveProperty(canvas[WzCanvasProperty.InlinkPropertyName]);
                    WzNode inlink = WzNode.GetChildNode(existing, WzCanvasProperty.InlinkPropertyName);
                    if (inlink != null) inlink.DeleteWzNode();
                }
                if (canvas.ContainsOutlinkProperty())
                {
                    canvas.RemoveProperty(canvas[WzCanvasProperty.OutlinkPropertyName]);
                    WzNode outlink = WzNode.GetChildNode(existing, WzCanvasProperty.OutlinkPropertyName);
                    if (outlink != null) outlink.DeleteWzNode();
                }

                // Keep the canvas's surface format. Letting MapleLib re-detect it turned a
                // BGRA4444 icon into ARGB1555, which previews fine in the editor and renders as
                // garbage in the game.
                replacedFormat = canvas.PngProperty == null ? (WzPngFormat?)null : canvas.PngProperty.Format;
                WzPngFormat written;
                string note;
                CanvasWriter.SetBitmapPreservingFormat(canvas, bitmap, out written, out note);
                FormatNote = note;
                WrittenFormat = written;
                existing.ChangedNodeProperty();
                return true;
            }

            if (parent.Tag is not WzImage && parent.Tag is not IPropertyContainer)
            {
                error = "目標節點不能有子節點。";
                return false;
            }

            WzCanvasProperty created = new WzCanvasProperty(canvasName);
            WzPngProperty png = new WzPngProperty();
            png.PNG = bitmap;
            created.PngProperty = png;

            WzNode createdNode = parent.AddObject(created, undoRedoMan);
            if (createdNode == null)
            {
                error = "編輯器拒絕插入這個圖片節點。";
                return false;
            }
            createdNode.AddObject(new WzVectorProperty(WzCanvasProperty.OriginPropertyName,
                new WzIntProperty("X", 0), new WzIntProperty("Y", 0)), undoRedoMan);
            createdNode.ChangedNodeProperty();
            CreatedNode = createdNode;
            return true;
        }

        /// <summary>
        /// Drops the sub properties this import had to create, deepest first, stopping at anything
        /// that still has children. Re-walked by name for the same reason LocateCreated is.
        /// </summary>
        void RemoveEmptyCreatedContainers()
        {
            if (createdContainerDepth < 0 || ImportParts == null)
                return;
            for (int depth = ImportParts.Length - 2; depth >= createdContainerDepth; depth--)
            {
                WzNode current = Target;
                for (int i = 0; i <= depth && current != null; i++)
                    current = WzNode.GetChildNode(current, ImportParts[i]);
                if (current == null || current.Nodes.Count > 0)
                    break;
                current.DeleteWzNode();
            }
            createdContainerDepth = -1;
        }

        /// <summary>
        /// Deep-copies a WZ object. Matches the editor's own copy/paste rules: images and
        /// properties clone, directories and whole files do not.
        /// </summary>
        static WzObject CloneWzObject(WzObject source, out string error)
        {
            error = null;
            if (source is WzImage image)
                return image.DeepClone();
            if (source is WzImageProperty property)
                return property.DeepClone();
            error = "這個型別不能複製:" + (source == null ? "null" : source.GetType().Name);
            return null;
        }

        /// <summary>
        /// A WzNode removed from the tree keeps its Tag but loses its chain to a root, which is
        /// the cheap way to notice the user deleted it by hand since the proposal was made.
        /// </summary>
        static bool IsStillInTree(WzNode node)
        {
            if (node == null)
                return false;
            System.Windows.Forms.TreeNode current = node;
            while (current.Parent != null)
                current = current.Parent;
            // A root node of the TreeView has TreeView set; a detached one does not.
            return current.TreeView != null;
        }
    }

    /// <summary>The proposal queue shared between the tool layer and the review list.</summary>
    public class PendingChangeSet
    {
        readonly List<PendingChange> items = new List<PendingChange>();

        public int Count { get { return items.Count; } }
        public List<PendingChange> Items { get { return items; } }

        public event EventHandler Changed;

        public void Add(PendingChange change)
        {
            items.Add(change);
            RaiseChanged();
        }

        public void Clear()
        {
            items.Clear();
            RaiseChanged();
        }

        public void Remove(PendingChange change)
        {
            items.Remove(change);
            RaiseChanged();
        }

        public void RaiseChanged()
        {
            EventHandler handler = Changed;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Parsing and writing WZ scalar values. Kept apart from the tool layer so the same rules
    /// validate a proposal and perform it, and a value the tools accepted cannot fail at apply.
    /// </summary>
    public static class WzValueWriter
    {
        public static bool CanApply(WzObject wzObject, string text, out string error)
        {
            error = null;
            if (wzObject is WzStringProperty stringProperty)
            {
                if (stringProperty.IsSpineAtlasResources)
                {
                    error = "這是 Spine atlas 資源字串,不支援直接修改。";
                    return false;
                }
                return true;
            }
            if (wzObject is WzUOLProperty) return true;
            if (wzObject is WzIntProperty) return CheckInt(text, out error);
            if (wzObject is WzShortProperty) return CheckShort(text, out error);
            if (wzObject is WzLongProperty) return CheckLong(text, out error);
            if (wzObject is WzFloatProperty) return CheckFloat(text, out error);
            if (wzObject is WzDoubleProperty) return CheckDouble(text, out error);
            error = "型別 " + (wzObject == null ? "?" : wzObject.GetType().Name) + " 不支援直接改值。";
            return false;
        }

        public static bool Apply(WzNode node, string text, out string error)
        {
            error = null;
            WzObject wzObject = node.Tag as WzObject;
            if (!CanApply(wzObject, text, out error))
                return false;

            if (wzObject is WzStringProperty stringProperty) stringProperty.Value = text;
            else if (wzObject is WzUOLProperty uol) uol.Value = text;
            else if (wzObject is WzIntProperty integer) integer.Value = int.Parse(text, CultureInfo.InvariantCulture);
            else if (wzObject is WzShortProperty small) small.Value = short.Parse(text, CultureInfo.InvariantCulture);
            else if (wzObject is WzLongProperty big) big.Value = long.Parse(text, CultureInfo.InvariantCulture);
            else if (wzObject is WzFloatProperty single) single.Value = float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            else if (wzObject is WzDoubleProperty dbl) dbl.Value = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            else
            {
                error = "型別不支援。";
                return false;
            }

            node.ChangedNodeProperty();
            return true;
        }

        public static bool CanCreate(string type, string value, out string error)
        {
            error = null;
            switch ((type ?? "").Trim().ToLowerInvariant())
            {
                case "string":
                case "uol":
                case "sub":
                case "subproperty":
                case "null":
                    return true;
                case "int": return CheckInt(value, out error);
                case "short": return CheckShort(value, out error);
                case "long": return CheckLong(value, out error);
                case "float": return CheckFloat(value, out error);
                case "double": return CheckDouble(value, out error);
                case "vector":
                    int x, y;
                    if (!TryParseVector(value, out x, out y))
                    {
                        error = "vector 的值要寫成 \"x,y\",例如 \"0,-30\"。";
                        return false;
                    }
                    return true;
                default:
                    error = "不支援的型別:" + type
                        + "。可用的是 string、int、long、short、float、double、uol、vector、sub、null。";
                    return false;
            }
        }

        public static WzObject Create(string type, string name, string value, WzNode parent, out string error)
        {
            error = null;
            if (!CanCreate(type, value, out error))
                return null;

            switch (type.Trim().ToLowerInvariant())
            {
                case "string": return new WzStringProperty(name, value ?? "");
                case "uol": return new WzUOLProperty(name, value ?? "");
                case "int": return new WzIntProperty(name, int.Parse(value, CultureInfo.InvariantCulture));
                case "short": return new WzShortProperty(name, short.Parse(value, CultureInfo.InvariantCulture));
                case "long": return new WzLongProperty(name, long.Parse(value, CultureInfo.InvariantCulture));
                case "float": return new WzFloatProperty(name, float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture));
                case "double": return new WzDoubleProperty(name, double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture));
                case "null": return new WzNullProperty(name);
                case "sub":
                case "subproperty": return new WzSubProperty(name);
                case "vector":
                    int x, y;
                    TryParseVector(value, out x, out y);
                    return new WzVectorProperty(name, new WzIntProperty("X", x), new WzIntProperty("Y", y));
            }
            error = "不支援的型別:" + type;
            return null;
        }

        static bool TryParseVector(string value, out int x, out int y)
        {
            x = 0;
            y = 0;
            if (string.IsNullOrWhiteSpace(value))
                return true; // "0,0" is a reasonable default for a new vector.
            string[] parts = value.Split(',');
            if (parts.Length != 2)
                return false;
            return int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out x)
                && int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out y);
        }

        static bool CheckInt(string text, out string error)
        {
            int parsed;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) { error = null; return true; }
            error = "\"" + text + "\" 不是合法的 int(範圍 -2147483648 ~ 2147483647)。";
            return false;
        }

        static bool CheckShort(string text, out string error)
        {
            short parsed;
            if (short.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) { error = null; return true; }
            error = "\"" + text + "\" 不是合法的 short(範圍 -32768 ~ 32767)。";
            return false;
        }

        static bool CheckLong(string text, out string error)
        {
            long parsed;
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) { error = null; return true; }
            error = "\"" + text + "\" 不是合法的 long。";
            return false;
        }

        static bool CheckFloat(string text, out string error)
        {
            float parsed;
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) { error = null; return true; }
            error = "\"" + text + "\" 不是合法的 float。";
            return false;
        }

        static bool CheckDouble(string text, out string error)
        {
            double parsed;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) { error = null; return true; }
            error = "\"" + text + "\" 不是合法的 double。";
            return false;
        }
    }
}
