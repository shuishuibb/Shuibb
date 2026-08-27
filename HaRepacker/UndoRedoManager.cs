using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HaRepacker.GUI.Panels;

namespace HaRepacker
{
    public class UndoRedoManager
    {
        public List<UndoRedoBatch> UndoList = new List<UndoRedoBatch>();
        public List<UndoRedoBatch> RedoList = new List<UndoRedoBatch>();
        private MainPanel parentPanel;

        public UndoRedoManager(MainPanel parentPanel)
        {
            this.parentPanel = parentPanel;
        }

        public void AddUndoBatch(List<UndoRedoAction> actions)
        {
            UndoRedoBatch batch = new UndoRedoBatch() { Actions = actions };
            UndoList.Add(batch);
            RedoList.Clear();
        }

        #region Undo Actions Creation
        public static UndoRedoAction ObjectAdded(WzNode parent, WzNode item)
        {
            return new UndoRedoAction(item, parent, UndoRedoType.ObjectAdded);
        }

        public static UndoRedoAction ObjectRemoved(WzNode parent, WzNode item)
        {
            return new UndoRedoAction(item, parent, UndoRedoType.ObjectRemoved);
        }

        /// <summary>
        /// A rename that can be walked back and forth. Identity is the WzNode/WzObject reference
        /// itself, never a path - the rename is exactly what changes the path. (The old factory
        /// here mislabelled renames as ObjectRemoved and had no callers; undoing one would have
        /// re-ADDED the node.)
        /// </summary>
        public static UndoRedoAction ObjectRenamed(WzNode item, string oldName, string newName)
        {
            return new UndoRedoAction(item, oldName, newName);
        }
        #endregion

        public void Undo()
        {
            if (UndoList.Count == 0)
                return; // Ctrl+Z with nothing to undo indexed past the end and crashed the app
            UndoRedoBatch action = UndoList[UndoList.Count - 1];
            action.UndoRedo();
            action.SwitchActions();
            UndoList.RemoveAt(UndoList.Count - 1);
            RedoList.Add(action);
        }

        public void Redo()
        {
            if (RedoList.Count == 0)
                return;
            UndoRedoBatch action = RedoList[RedoList.Count - 1];
            action.UndoRedo();
            action.SwitchActions();
            RedoList.RemoveAt(RedoList.Count - 1);
            UndoList.Add(action);
        }
    }

    public class UndoRedoBatch
    {
        public List<UndoRedoAction> Actions = new List<UndoRedoAction>();

        public void UndoRedo()
        {
            foreach (UndoRedoAction action in Actions) action.UndoRedo();
        }

        public void SwitchActions()
        {
            foreach (UndoRedoAction action in Actions) action.SwitchAction();
        }
    }

    public class UndoRedoAction
    {
        private readonly WzNode item;
        private readonly WzNode parent;
        private UndoRedoType type;

        // Rename bookkeeping: applying the action puts undoName on; SwitchAction swaps the two.
        private string undoName;
        private string redoName;

        public UndoRedoAction(WzNode item, WzNode parent, UndoRedoType type)
        {
            this.item = item;
            this.parent = parent;
            this.type = type;
        }

        public UndoRedoAction(WzNode item, string oldName, string newName)
        {
            this.item = item;
            this.type = UndoRedoType.ObjectRenamed;
            this.undoName = oldName;
            this.redoName = newName;
        }

        public void UndoRedo()
        {
            switch (type)
            {
                case UndoRedoType.ObjectAdded:
                    item.DeleteWzNode();
                    break;
                case UndoRedoType.ObjectRemoved:
                    parent.AddNode(item, true);
                    break;
                case UndoRedoType.ObjectRenamed:
                    // ChangeName writes both the tree text and the WzObject name, and keeps the
                    // dirty flag honest - undoing a rename is itself a modification. The callers
                    // of Undo/Redo refresh the native tree, which repaints the WPF header.
                    item.ChangeName(undoName);
                    break;
            }
        }


        public unsafe void SwitchAction()
        {
            switch (type)
            {
                case UndoRedoType.ObjectAdded:
                    type = UndoRedoType.ObjectRemoved;
                    break;
                case UndoRedoType.ObjectRemoved:
                    type = UndoRedoType.ObjectAdded;
                    break;
                case UndoRedoType.ObjectRenamed:
                    (undoName, redoName) = (redoName, undoName);
                    break;

            }
        }
    }

    public enum UndoRedoType
    {
        ObjectAdded,
        ObjectRemoved,
        ObjectRenamed,
        ObjectChanged
    }
}
