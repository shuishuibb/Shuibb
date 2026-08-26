using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Controls;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using SkillPreview;
using Xunit;
using Assert = Xunit.Assert;

namespace NodeEditorCopyPasteTests;

/// <summary>
/// Targeted regression for the Ctrl+C / Ctrl+V routing bug: selecting fields on
/// NodeEditorPanel's property cards and pressing Ctrl+C/Ctrl+V pasted/cloned the *entire tree
/// node* instead of applying just the selected field values to the target's matching card.
///
/// Root cause was MainForm.MainWindow_PreviewKeyDown - a Window-level PreviewKeyDown handler
/// that always runs first (WPF tunnels PreviewKeyDown root-to-leaf) and unconditionally called
/// MainPanel.DoCopy()/DoPaste(), regardless of what had keyboard focus. The fix adds
/// NodeEditorPanel.HasCopiedFields / ClearCopiedFields / TryHandleFieldCopyShortcut /
/// TryHandleFieldPasteShortcut, which MainForm now consults before falling back to the tree's
/// own whole-node copy/paste - see MainPanel.TryHandleFieldCopyShortcut/
/// TryHandleFieldPasteShortcut and MainForm.MainWindow_PreviewKeyDown for the wiring.
///
/// No GUI is driven, no window/message pump, no simulated clicks or key presses - these tests
/// call the panel's public API directly (exactly like MainForm now does) against in-memory
/// WzSubProperty fixtures, and use reflection only to read/seed the private per-card selection
/// state (GroupBinding.SelectedFieldNames) and to read back the staged TextBox values, since
/// NodeEditorPanel intentionally exposes no other hook into that state. Each test body runs on
/// its own STA thread (plain System.Threading, no extra package) purely because constructing any
/// WPF Control - NodeEditorPanel included - throws off an MTA thread; that's a WPF requirement
/// unrelated to this bug and has nothing to do with the field-selection/keyboard-routing logic
/// under test.
/// </summary>
public sealed class NodeEditorCopyPasteTests
{
    // ---- STA helper (see class doc comment) -----------------------------------------------

    private static void RunSta(Action action)
    {
        Exception captured = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (captured != null)
            ExceptionDispatchInfo.Capture(captured).Throw();
    }

    // ---- fixture builders --------------------------------------------------------------------

    /// <summary>
    /// A Consume-style item: fields sit directly on the item's own WzSubProperty (batched
    /// Item.wz storage), which is what made the loose-fields card's Title the item's own unique
    /// code (e.g. "2040000") rather than a shared category name - see GroupBinding.MatchKey.
    /// </summary>
    private static WzSubProperty MakeConsumeItem(string itemId, params (string Name, int Value)[] looseFields)
    {
        var item = new WzSubProperty(itemId);
        foreach ((string name, int value) in looseFields)
            item.AddProperty(new WzIntProperty(name, value));
        return item;
    }

    /// <summary>An Equip-style item: fields sit under a shared "info" sub-container.</summary>
    private static WzSubProperty MakeEquipItem(string itemId, params (string Name, int Value)[] infoFields)
    {
        var item = new WzSubProperty(itemId);
        var info = new WzSubProperty("info");
        foreach ((string name, int value) in infoFields)
            info.AddProperty(new WzIntProperty(name, value));
        item.AddProperty(info);
        return item;
    }

    /// <summary>
    /// An item with something editable, but none of it sitting loose on the item itself - so
    /// Rebuild never builds a loose-fields card for it at all (only a "spec" card).
    /// </summary>
    private static WzSubProperty MakeItemWithOnlySpecContainer(string itemId)
    {
        var item = new WzSubProperty(itemId);
        var spec = new WzSubProperty("spec");
        spec.AddProperty(new WzIntProperty("someField", 1));
        item.AddProperty(spec);
        return item;
    }

    // ---- reflection helpers (see class doc comment for why) ----------------------------------

    private static object GetPrivate(object instance, string fieldName)
    {
        FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(field != null, "NodeEditorPanel's private field '" + fieldName + "' was not found - did its internal layout change?");
        return field.GetValue(instance);
    }

    private static List<object> Groups(NodeEditorPanel panel)
    {
        return ((IEnumerable)GetPrivate(panel, "groups")).Cast<object>().ToList();
    }

    private static bool IsLoose(object binding) => (bool)binding.GetType().GetField("IsLooseFieldsCard").GetValue(binding);
    private static string Title(object binding) => (string)binding.GetType().GetField("Title").GetValue(binding);
    private static HashSet<string> Selected(object binding) => (HashSet<string>)binding.GetType().GetField("SelectedFieldNames").GetValue(binding);
    private static Dictionary<string, TextBox> Fields(object binding) => (Dictionary<string, TextBox>)binding.GetType().GetField("Fields").GetValue(binding);
    private static string StatusText(NodeEditorPanel panel) => ((TextBlock)GetPrivate(panel, "statusText")).Text;

    private static object LooseCard(NodeEditorPanel panel) => Groups(panel).SingleOrDefault(IsLoose);
    private static object CardTitled(NodeEditorPanel panel, string title) => Groups(panel).SingleOrDefault(g => !IsLoose(g) && Title(g) == title);

    // ---- tests ---------------------------------------------------------------------------------

    [Fact]
    public void FieldCopy_SetsHasCopiedFields_AndClearResetsIt() => RunSta(() =>
    {
        var panel = new NodeEditorPanel();
        Assert.True(panel.TryLoad(MakeConsumeItem("2040000", ("price", 100), ("incPDD", 5)), null));
        Assert.False(panel.HasCopiedFields);

        object loose = LooseCard(panel);
        Assert.NotNull(loose);
        Selected(loose).Add("price");

        Assert.True(panel.TryHandleFieldCopyShortcut());
        Assert.True(panel.HasCopiedFields);

        panel.ClearCopiedFields();
        Assert.False(panel.HasCopiedFields);
    });

    [Fact]
    public void TryHandleFieldCopyShortcut_NoFieldSelected_ReturnsFalse_SoOrdinaryTreeCopyStillRuns() => RunSta(() =>
    {
        var panel = new NodeEditorPanel();
        Assert.True(panel.TryLoad(MakeConsumeItem("2040000", ("price", 100)), null));

        // Nothing selected - MainForm's Ctrl+C handler must fall back to MainPanel.DoCopy().
        Assert.False(panel.TryHandleFieldCopyShortcut());
        Assert.False(panel.HasCopiedFields);
    });

    [Fact]
    public void ConsumeLooseFields_CopyOnOneItemId_PastesOntoMatchingFieldsOfAnotherItemId() => RunSta(() =>
    {
        // This is the exact reported workflow: 2040000 -> select fields -> Ctrl+C ->
        // select 2040001 -> Ctrl+V, expecting only the field values to land on 2040001.
        var panel = new NodeEditorPanel();

        Assert.True(panel.TryLoad(MakeConsumeItem("2040000", ("price", 100), ("incPDD", 5)), null));
        object sourceCard = LooseCard(panel);
        Fields(sourceCard)["price"].Text = "777";
        Fields(sourceCard)["incPDD"].Text = "42";
        Selected(sourceCard).Add("price");
        Selected(sourceCard).Add("incPDD");

        Assert.True(panel.TryHandleFieldCopyShortcut());
        Assert.True(panel.HasCopiedFields);

        // Switching tree selection to a different item id rebuilds the panel for 2040001, same
        // as MainPanel.ShowNodeEditorIfApplicable does on every tree selection change. No field
        // needs to be (re)selected on the target per the reported workflow.
        Assert.True(panel.TryLoad(MakeConsumeItem("2040001", ("price", 1), ("incPDD", 2)), null));

        Assert.True(panel.TryHandleFieldPasteShortcut());

        object targetCard = LooseCard(panel);
        Assert.Equal("777", Fields(targetCard)["price"].Text);
        Assert.Equal("42", Fields(targetCard)["incPDD"].Text);

        // The card is still 2040001's own - proving this applied values onto the *target's*
        // card rather than swapping in the source's. There is no tree at all in this test, which
        // is the point: field paste has no code path that could ever insert/clone a WZ node.
        Assert.Equal("2040001", Title(targetCard));
    });

    [Fact]
    public void MismatchedTargetCard_PasteShortcut_ReturnsTrueWithoutFallback_AndKeepsClipboardStaged() => RunSta(() =>
    {
        var panel = new NodeEditorPanel();

        Assert.True(panel.TryLoad(MakeConsumeItem("2040000", ("price", 100)), null));
        object sourceCard = LooseCard(panel);
        Selected(sourceCard).Add("price");
        Assert.True(panel.TryHandleFieldCopyShortcut());

        Assert.True(panel.TryLoad(MakeItemWithOnlySpecContainer("9999999"), null));
        Assert.Null(LooseCard(panel)); // no loose card was even built for this item

        // Must still report "handled" (true) so MainForm never falls back to the tree's
        // whole-node paste - that fallback is exactly what the reported bug looked like.
        Assert.True(panel.TryHandleFieldPasteShortcut());

        // Nothing to apply the values to, and nothing should be silently dropped - the clipboard
        // stays staged so the user can navigate to a compatible card and try again.
        Assert.True(panel.HasCopiedFields);
        Assert.Contains("沒有同類卡片可以貼上", StatusText(panel));
    });

    [Fact]
    public void ClearCopiedFields_MakesPasteShortcutReturnFalse_SoTreeLevelPasteRunsInstead() => RunSta(() =>
    {
        var panel = new NodeEditorPanel();

        Assert.True(panel.TryLoad(MakeConsumeItem("2040000", ("price", 100)), null));
        object sourceCard = LooseCard(panel);
        Selected(sourceCard).Add("price");
        Assert.True(panel.TryHandleFieldCopyShortcut());
        Assert.True(panel.HasCopiedFields);

        // Exactly what MainPanel.DoCopy() now calls before doing its own whole-node tree copy -
        // "the last Ctrl+C wins".
        panel.ClearCopiedFields();

        Assert.True(panel.TryLoad(MakeConsumeItem("2040001", ("price", 1)), null));
        Assert.False(panel.TryHandleFieldPasteShortcut());
    });

    [Fact]
    public void EquipStyleInfoCard_CopyThenPasteAcrossDifferentItemIds_StillMatchesByTitle() => RunSta(() =>
    {
        // Regression for the pre-existing (Equip) MatchKey-by-Title path, composed with the new
        // shortcut methods - only the Consume loose-card path changed for this bug.
        var panel = new NodeEditorPanel();

        Assert.True(panel.TryLoad(MakeEquipItem("01000000", ("reqSTR", 40), ("incPDD", 10)), null));
        object sourceCard = CardTitled(panel, "info");
        Assert.NotNull(sourceCard);
        Fields(sourceCard)["reqSTR"].Text = "999";
        Selected(sourceCard).Add("reqSTR");
        Assert.True(panel.TryHandleFieldCopyShortcut());

        Assert.True(panel.TryLoad(MakeEquipItem("01000001", ("reqSTR", 1), ("incPDD", 1)), null));
        Assert.True(panel.TryHandleFieldPasteShortcut());

        object targetCard = CardTitled(panel, "info");
        Assert.Equal("999", Fields(targetCard)["reqSTR"].Text);
    });
}
