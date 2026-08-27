using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using HaRepacker;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using Xunit;
using Assert = Xunit.Assert;

namespace RenameUndoTests;

/// <summary>
/// Rename undo/redo (audit P3): renames never entered the undo stack, and the ObjectRenamed
/// factory carried a latent copy-paste bug - it built an ObjectRemoved action, so if anything
/// had ever called it, Ctrl+Z after a rename would have re-ADDED the node instead of restoring
/// its name. These drive UndoRedoManager against real WzNodes over real WZ objects.
///
/// SCOPE - not covered here: the rename dialogs themselves (cancel producing no history is
/// enforced at the call sites by code shape - the record is only added inside the confirmed
/// branch after a name-actually-changed check) and the WPF header repaint (done by the existing
/// native-tree refresh in the Undo/Redo menu handlers). Verified manually.
/// </summary>
public sealed class RenameUndoTests
{
    private static void RunSta(Action action)
    {
        Exception captured = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { captured = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (captured != null)
            ExceptionDispatchInfo.Capture(captured).Throw();
    }

    /// <summary>A parsed image holding one int property, wrapped in WzNodes like the tree does.</summary>
    private static (WzNode imgNode, WzNode propNode, WzImage img) Fixture()
    {
        var img = new WzImage("0100100.img") { Changed = false };
        img.AddProperty(new WzIntProperty("damage", 10));
        img.Parsed = true;
        img.Changed = false;
        var imgNode = new WzNode(img);
        WzNode propNode = WzNode.GetChildNode(imgNode, "damage");
        return (imgNode, propNode, img);
    }

    [Fact]
    public void UndoRestoresTheOldName_OnNodeAndOnTheWzObject()
    {
        RunSta(() =>
        {
            var (_, propNode, img) = Fixture();
            var manager = new UndoRedoManager(null);

            propNode.ChangeName("damageNew"); // what the rename call sites do
            manager.AddUndoBatch(new List<UndoRedoAction>
                { UndoRedoManager.ObjectRenamed(propNode, "damage", "damageNew") });

            manager.Undo();

            Assert.Equal("damage", propNode.Text);
            Assert.Equal("damage", ((WzObject)propNode.Tag).Name);
            // Undoing a rename is itself a modification - the image must stay dirty.
            Assert.True(img.Changed);
        });
    }

    [Fact]
    public void RedoReappliesTheNewName()
    {
        RunSta(() =>
        {
            var (_, propNode, _) = Fixture();
            var manager = new UndoRedoManager(null);

            propNode.ChangeName("damageNew");
            manager.AddUndoBatch(new List<UndoRedoAction>
                { UndoRedoManager.ObjectRenamed(propNode, "damage", "damageNew") });

            manager.Undo();
            manager.Redo();

            Assert.Equal("damageNew", propNode.Text);
            Assert.Equal("damageNew", ((WzObject)propNode.Tag).Name);
        });
    }

    [Fact]
    public void TenRoundTrips_StayConsistent()
    {
        RunSta(() =>
        {
            var (_, propNode, _) = Fixture();
            var manager = new UndoRedoManager(null);

            propNode.ChangeName("damageNew");
            manager.AddUndoBatch(new List<UndoRedoAction>
                { UndoRedoManager.ObjectRenamed(propNode, "damage", "damageNew") });

            for (int i = 0; i < 10; i++)
            {
                manager.Undo();
                Assert.Equal("damage", ((WzObject)propNode.Tag).Name);
                manager.Redo();
                Assert.Equal("damageNew", ((WzObject)propNode.Tag).Name);
            }
        });
    }

    /// <summary>
    /// The latent bug, pinned: the action must restore the NAME, not re-add the node the way the
    /// old ObjectRemoved-typed action would have.
    /// </summary>
    [Fact]
    public void UndoOfARename_DoesNotDuplicateTheNode()
    {
        RunSta(() =>
        {
            var (imgNode, propNode, img) = Fixture();
            var manager = new UndoRedoManager(null);

            propNode.ChangeName("damageNew");
            manager.AddUndoBatch(new List<UndoRedoAction>
                { UndoRedoManager.ObjectRenamed(propNode, "damage", "damageNew") });
            manager.Undo();

            Assert.Equal(1, imgNode.Nodes.Count);
            Assert.Single(img.WzProperties);
        });
    }

    [Fact]
    public void UndoRedoWithEmptyHistory_IsANoOp_NotACrash()
    {
        RunSta(() =>
        {
            var manager = new UndoRedoManager(null);
            manager.Undo(); // used to index past the end and take the app down via the crash dialog
            manager.Redo();
        });
    }

    [Fact]
    public void AddAndRemoveUndo_StillBehave()
    {
        RunSta(() =>
        {
            var (imgNode, _, img) = Fixture();
            var manager = new UndoRedoManager(null);

            // Add a property through the same path the UI uses, recording it as an add.
            WzNode added = imgNode.AddObject(new WzIntProperty("mp", 5), manager);
            Assert.NotNull(added);
            Assert.Equal(2, img.WzProperties.Count);

            manager.Undo(); // add undone -> property gone from the real WZ
            Assert.Single(img.WzProperties);
            Assert.Null(img.WzProperties.FirstOrDefaultByName("mp"));

            manager.Redo(); // and back
            Assert.Equal(2, img.WzProperties.Count);
        });
    }
}

internal static class WzPropertyCollectionExtensions
{
    public static WzImageProperty FirstOrDefaultByName(this WzPropertyCollection props, string name)
    {
        foreach (WzImageProperty p in props)
            if (p.Name == name)
                return p;
        return null;
    }
}
