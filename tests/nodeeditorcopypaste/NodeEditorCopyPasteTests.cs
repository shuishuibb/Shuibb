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
/// Targeted regression for NodeEditorPanel's field copy/paste *state machine* - the part
/// MainForm.MainWindow_PreviewKeyDown queries to decide whether a Ctrl+C/Ctrl+V belongs to the
/// property editor's fields or to the tree's whole-node clipboard:
///
///   HasSelectedFields / HasCopiedFields  - which clipboard the keystroke belongs to
///   CopySelectedFieldsShortcut()         - stage the selected fields
///   PasteCopiedFieldsShortcut()          - apply them to the current node's matching card
///   ClearCopiedFields()                  - "last Ctrl+C wins", called from MainPanel.DoCopy()
///
/// SCOPE - what these tests do NOT cover, and must not be cited as proof of:
///   * the real Ctrl+C / Ctrl+V keystrokes and MainWindow_PreviewKeyDown's routing,
///   * the 是否複製 / 是否貼上 confirmation prompts (Warning.Warn), which live in HaRepacker
///     and are deliberately not reachable from this project,
///   * IsValueTextBoxFocused's real behaviour with live keyboard focus in a TextBox,
///   * anything about the tree's own DoCopy/DoPaste.
/// Those are keyboard/confirmation UI behaviour and are verified manually.
///
/// No GUI is driven here - no window, no message pump, no simulated clicks or key presses.
/// These call the panel's public API directly against in-memory WzSubProperty fixtures, and use
/// reflection only to seed the private per-card selection state (GroupBinding.SelectedFieldNames,
/// normally set by clicking a field label) and to read back staged TextBox values, since the
/// panel intentionally exposes no other hook into that. Each test body runs on its own STA
/// thread (plain System.Threading, no extra package) purely because constructing any WPF Control
/// throws off an MTA thread - a WPF requirement unrelated to the logic under test.
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
    public void NoFieldSelected_HasSelectedFieldsIsFalse_SoCtrlCStaysATreeCopy() => RunSta(() =>
    {
        var panel = new NodeEditorPanel();
        Assert.True(panel.TryLoad(MakeConsumeItem("2040000", ("price", 100)), null));

        // MainForm reads exactly this to pick its branch: false here means Ctrl+C falls through
        // to MainPanel.DoCopy(), which shows 是否複製 on its own.
        Assert.False(panel.HasSelectedFields);
        Assert.False(panel.HasCopiedFields);
    });

    [Fact]
    public void SelectingAField_MakesCtrlCTheFieldBranch_AndStagesTheCopy() => RunSta(() =>
    {
        var panel = new NodeEditorPanel();
        Assert.True(panel.TryLoad(MakeConsumeItem("2040000", ("price", 100), ("slotMax", 9999)), null));

        object loose = LooseCard(panel);
        Assert.NotNull(loose);
        Selected(loose).Add("price");

        Assert.True(panel.HasSelectedFields);

        // MainForm calls this only after the user answers Yes to 是否複製.
        panel.CopySelectedFieldsShortcut();
        Assert.True(panel.HasCopiedFields);
    });

    [Fact]
    public void ConsumeLooseFields_CopyOnOneItemId_PastesOntoMatchingFieldsOfAnotherItemId() => RunSta(() =>
    {
        // The reported workflow: 02020000 -> select 價格/slotMax -> Ctrl+C -> click 02020001 in
        // the tree -> Ctrl+V, expecting only those field values to land on 02020001.
        var panel = new NodeEditorPanel();

        Assert.True(panel.TryLoad(MakeConsumeItem("2020000", ("price", 100), ("slotMax", 9999)), null));
        object sourceCard = LooseCard(panel);
        Fields(sourceCard)["price"].Text = "210";
        Fields(sourceCard)["slotMax"].Text = "500";
        Selected(sourceCard).Add("price");
        Selected(sourceCard).Add("slotMax");

        Assert.True(panel.HasSelectedFields);
        panel.CopySelectedFieldsShortcut();
        Assert.True(panel.HasCopiedFields);

        // Clicking another item in the tree rebuilds the panel for it, same as
        // MainPanel.ShowNodeEditorIfApplicable does on every selection change. Nothing needs to
        // be (re)selected on the target - the staged copy survives the rebuild on purpose.
        Assert.True(panel.TryLoad(MakeConsumeItem("2020001", ("price", 1), ("slotMax", 2)), null));
        Assert.False(panel.HasSelectedFields);
        Assert.True(panel.HasCopiedFields); // so MainForm's Ctrl+V takes the field branch

        panel.PasteCopiedFieldsShortcut();

        object targetCard = LooseCard(panel);
        Assert.Equal("210", Fields(targetCard)["price"].Text);
        Assert.Equal("500", Fields(targetCard)["slotMax"].Text);

        // Still 2020001's own card - the values were applied onto the *target*, not the source
        // card swapped in. There is no tree in this test at all, which is the point: this path
        // has no way to clone or insert a WZ node.
        Assert.Equal("2020001", Title(targetCard));
    });

    [Fact]
    public void MismatchedTargetCard_KeepsFieldBranchAndClipboard_SoTreePasteNeverRuns() => RunSta(() =>
    {
        var panel = new NodeEditorPanel();

        Assert.True(panel.TryLoad(MakeConsumeItem("2040000", ("price", 100)), null));
        Selected(LooseCard(panel)).Add("price");
        panel.CopySelectedFieldsShortcut();

        Assert.True(panel.TryLoad(MakeItemWithOnlySpecContainer("9999999"), null));
        Assert.Null(LooseCard(panel)); // no loose card was even built for this item

        panel.PasteCopiedFieldsShortcut();

        // HasCopiedFields staying true is what stops MainForm from ever reaching DoPaste() - an
        // incompatible target must not silently fall back to pasting the whole tree node. The
        // copy also stays staged so the user can go to a compatible card and retry.
        Assert.True(panel.HasCopiedFields);
        Assert.Contains("沒有同類卡片可以貼上", StatusText(panel));
    });

    [Fact]
    public void TreeCopyClearsFieldClipboard_SoTheNextCtrlVIsATreePaste() => RunSta(() =>
    {
        var panel = new NodeEditorPanel();

        Assert.True(panel.TryLoad(MakeConsumeItem("2040000", ("price", 100)), null));
        Selected(LooseCard(panel)).Add("price");
        panel.CopySelectedFieldsShortcut();
        Assert.True(panel.HasCopiedFields);

        // Exactly what MainPanel.DoCopy() calls before taking its own whole-node copy -
        // "last Ctrl+C wins".
        panel.ClearCopiedFields();

        Assert.True(panel.TryLoad(MakeConsumeItem("2040001", ("price", 1)), null));
        Assert.False(panel.HasCopiedFields); // MainForm's Ctrl+V now goes to DoPaste()
    });

    [Fact]
    public void FieldCopyAfterATreeCopy_WinsTheNextCtrlV() => RunSta(() =>
    {
        // The mirror of the previous test: a tree copy happened first (modelled by the
        // ClearCopiedFields that DoCopy performs), then the user copies fields - the field
        // clipboard must be the one a following Ctrl+V uses.
        var panel = new NodeEditorPanel();
        Assert.True(panel.TryLoad(MakeConsumeItem("2040000", ("price", 100)), null));

        panel.ClearCopiedFields();

        Selected(LooseCard(panel)).Add("price");
        panel.CopySelectedFieldsShortcut();

        Assert.True(panel.TryLoad(MakeConsumeItem("2040001", ("price", 1)), null));
        Assert.True(panel.HasCopiedFields);
    });

    [Fact]
    public void EquipStyleInfoCard_CopyThenPasteAcrossDifferentItemIds_StillMatchesByTitle() => RunSta(() =>
    {
        // Regression for the Equip (MatchKey-by-Title) path - only the Consume loose-card path
        // needed the synthetic match key, and this must keep working alongside it.
        var panel = new NodeEditorPanel();

        Assert.True(panel.TryLoad(MakeEquipItem("01000000", ("reqSTR", 40), ("incPDD", 10)), null));
        object sourceCard = CardTitled(panel, "info");
        Assert.NotNull(sourceCard);
        Fields(sourceCard)["reqSTR"].Text = "999";
        Selected(sourceCard).Add("reqSTR");
        panel.CopySelectedFieldsShortcut();

        Assert.True(panel.TryLoad(MakeEquipItem("01000001", ("reqSTR", 1), ("incPDD", 1)), null));
        panel.PasteCopiedFieldsShortcut();

        Assert.Equal("999", Fields(CardTitled(panel, "info"))["reqSTR"].Text);
    });

    [Fact]
    public void IsValueTextBoxFocused_IsFalseWhenNothingInThePanelHasFocus() => RunSta(() =>
    {
        // Narrow guard only: with no live focus the TextBox escape hatch must not misfire and
        // swallow Ctrl+C/Ctrl+V. Its real behaviour with keyboard focus inside a value box needs
        // a focused window and is verified manually - see the class doc comment's SCOPE note.
        var panel = new NodeEditorPanel();
        Assert.True(panel.TryLoad(MakeConsumeItem("2040000", ("price", 100)), null));

        Assert.False(panel.IsValueTextBoxFocused);
    });
}
