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
///   PasteCopiedFieldsShortcut()          - write them straight into the current node's matching
///                                          card's WZ properties, returning how many were written
///   ClearCopiedFields()                  - "last Ctrl+C wins", called from MainPanel.DoCopy()
///
/// SCOPE - what these tests do NOT cover, and must not be cited as proof of:
///   * the real Ctrl+C / Ctrl+V keystrokes and MainWindow_PreviewKeyDown's routing,
///   * the 是否複製 / 是否貼上 confirmation prompts (Warning.Warn), which live in HaRepacker
///     and are deliberately not reachable from this project,
///   * IsValueTextBoxFocused's real behaviour with live keyboard focus in a TextBox,
///   * anything about the tree's own DoCopy/DoPaste,
///   * the target tree node turning red after a paste - that is MainPanel calling
///     WzNode.ChangedNodeProperty() on the strength of the count returned here, and there is no
///     tree in this project at all.
/// Those are keyboard/confirmation/tree UI behaviour and are verified manually.
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
    /// Parents an item under a real WzImage, the way batched Item.wz storage holds many item
    /// codes in one .img. Required, not cosmetic: a paste now writes through to the WZ and
    /// touches prop.ParentImage.Changed, which only resolves when there is a WzImage ancestor -
    /// exactly as in the running editor. Changed is reset afterwards so tests can tell a paste's
    /// dirty-marking apart from the one AddProperty does while the fixture is being built.
    /// </summary>
    private static WzSubProperty AttachToImage(WzSubProperty item, string imageName)
    {
        var image = new WzImage(imageName);
        image.AddProperty(item);
        image.Changed = false;
        return item;
    }

    private static WzImage ImageOf(WzSubProperty item) => ((WzImageProperty)item).ParentImage;

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
        return AttachToImage(item, "0202.img");
    }

    /// <summary>An Equip-style item: fields sit under a shared "info" sub-container.</summary>
    private static WzSubProperty MakeEquipItem(string itemId, params (string Name, int Value)[] infoFields)
    {
        var item = new WzSubProperty(itemId);
        var info = new WzSubProperty("info");
        foreach ((string name, int value) in infoFields)
            info.AddProperty(new WzIntProperty(name, value));
        item.AddProperty(info);
        return AttachToImage(item, itemId + ".img");
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
        return AttachToImage(item, "0202.img");
    }

    /// <summary>
    /// The names of the properties a paste reported writing, in order. MainPanel turns exactly
    /// this list into red tree nodes (one ChangedNodeProperty per entry, via property.HRTag), so
    /// what's in here is precisely what does and doesn't go red.
    /// </summary>
    private static string[] NamesOf(IReadOnlyList<WzImageProperty> written) => written.Select(p => p.Name).ToArray();

    /// <summary>
    /// One Ctrl+V over the given tree targets. MainPanel snapshots DataTree.SelectedNodes into
    /// exactly this call - a single paste for the whole selection, which is why the confirmation
    /// prompt appears once no matter how many nodes are selected.
    /// </summary>
    private static IReadOnlyList<WzImageProperty> PasteTo(NodeEditorPanel panel, params WzObject[] targets)
        => panel.PasteCopiedFieldsToTargets(targets);

    /// <summary>Reads a scalar straight out of the WZ, bypassing the panel entirely.</summary>
    private static int IntValueOf(WzSubProperty owner, string path)
    {
        WzImageProperty prop = owner;
        foreach (string step in path.Split('/'))
        {
            prop = prop[step];
            Assert.True(prop != null, "fixture has no property at '" + path + "'");
        }
        return Assert.IsType<WzIntProperty>(prop).Value;
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
        WzSubProperty target = MakeConsumeItem("2020001", ("price", 1), ("slotMax", 2));
        Assert.True(panel.TryLoad(target, null));
        Assert.False(panel.HasSelectedFields);
        Assert.True(panel.HasCopiedFields); // so MainForm's Ctrl+V takes the field branch

        IReadOnlyList<WzImageProperty> written = PasteTo(panel, target);

        // Exactly the two pasted leaf properties are reported - these are the only nodes
        // MainPanel will redden. The item itself and its other properties are not in the list.
        Assert.Equal(new[] { "price", "slotMax" }, NamesOf(written));
        Assert.Same(target["price"], written[0]);
        Assert.Same(target["slotMax"], written[1]);

        // The WZ properties themselves are already updated - 儲存數值 is NOT pressed anywhere in
        // this test. This is the behaviour change: a confirmed Ctrl+V is the commit.
        Assert.Equal(210, IntValueOf(target, "price"));
        Assert.Equal(500, IntValueOf(target, "slotMax"));
        Assert.True(ImageOf(target).Changed); // whole .img still dirty, so it saves correctly

        // ...and the boxes show what the WZ now actually holds.
        object targetCard = LooseCard(panel);
        Assert.Equal("210", Fields(targetCard)["price"].Text);
        Assert.Equal("500", Fields(targetCard)["slotMax"].Text);

        // Still 2020001's own card - the values were applied onto the *target*, not the source
        // card swapped in. There is no tree in this test at all, which is the point: this path
        // has no way to clone or insert a WZ node.
        Assert.Equal("2020001", Title(targetCard));
    });

    [Fact]
    public void Paste_WritesOnlyTheCopiedFields_LeavingOtherStagedEditsUncommitted() => RunSta(() =>
    {
        // The reason a paste must not just call SaveGroup: SaveGroup walks the whole card, which
        // would also commit a box the user had typed into but not yet confirmed with 儲存數值.
        var panel = new NodeEditorPanel();

        Assert.True(panel.TryLoad(MakeConsumeItem("2040000", ("price", 100), ("slotMax", 200)), null));
        object sourceCard = LooseCard(panel);
        Fields(sourceCard)["slotMax"].Text = "9999";
        Selected(sourceCard).Add("slotMax"); // clipboard carries slotMax only
        panel.CopySelectedFieldsShortcut();

        WzSubProperty target = MakeConsumeItem("2040001", ("price", 1), ("slotMax", 2));
        Assert.True(panel.TryLoad(target, null));

        // An unconfirmed hand edit sitting in a box the paste does not carry.
        Fields(LooseCard(panel))["price"].Text = "999";

        // Only the pasted field is reported, so only slotMax goes red - the untouched hand edit
        // neither reddens nor commits.
        Assert.Equal(new[] { "slotMax" }, NamesOf(PasteTo(panel, target)));

        Assert.Equal(9999, IntValueOf(target, "slotMax")); // the pasted field was written
        Assert.Equal(1, IntValueOf(target, "price"));      // the staged hand edit was NOT
        Assert.Equal("999", Fields(LooseCard(panel))["price"].Text); // still staged, untouched
    });

    [Fact]
    public void Paste_PartialSuccess_ReportsOnlyThePropertyThatWasActuallyWritten() => RunSta(() =>
    {
        // One value the target's type accepts, one it doesn't - only the successful one may be
        // reported, so only it goes red.
        var panel = new NodeEditorPanel();

        Assert.True(panel.TryLoad(MakeConsumeItem("2040000", ("price", 100), ("slotMax", 200)), null));
        object sourceCard = LooseCard(panel);
        Fields(sourceCard)["price"].Text = "777";
        Fields(sourceCard)["slotMax"].Text = "abc"; // never parses into a WzIntProperty
        Selected(sourceCard).Add("price");
        Selected(sourceCard).Add("slotMax");
        panel.CopySelectedFieldsShortcut();

        WzSubProperty target = MakeConsumeItem("2040001", ("price", 1), ("slotMax", 2));
        Assert.True(panel.TryLoad(target, null));

        Assert.Equal(new[] { "price" }, NamesOf(PasteTo(panel, target)));

        Assert.Equal(777, IntValueOf(target, "price"));
        Assert.Equal(2, IntValueOf(target, "slotMax")); // rejected value left the property alone
        Assert.Contains("型別不符", StatusText(panel));
    });

    [Fact]
    public void Paste_TypeMismatch_LeavesTargetPropertyAlone_AndReportsNothingWritten() => RunSta(() =>
    {
        var panel = new NodeEditorPanel();

        Assert.True(panel.TryLoad(MakeConsumeItem("2040000", ("price", 100)), null));
        object sourceCard = LooseCard(panel);
        Fields(sourceCard)["price"].Text = "abc"; // never parses into the target's WzIntProperty
        Selected(sourceCard).Add("price");
        panel.CopySelectedFieldsShortcut();

        WzSubProperty target = MakeConsumeItem("2040001", ("price", 1));
        Assert.True(panel.TryLoad(target, null));

        // Nothing written -> nothing reported -> MainPanel reddens no node at all.
        Assert.Empty(PasteTo(panel, target));
        Assert.Equal(1, IntValueOf(target, "price"));
        Assert.False(ImageOf(target).Changed);
        Assert.Contains("型別不符", StatusText(panel));
    });

    [Fact]
    public void Paste_NeverAddsOrRemovesNodes_OnEitherSide() => RunSta(() =>
    {
        // Guards the original bug from the other direction: a field paste must only ever change
        // values in place - never insert the source item under the target, never touch the source.
        var panel = new NodeEditorPanel();

        WzSubProperty source = MakeConsumeItem("2040000", ("price", 100));
        Assert.True(panel.TryLoad(source, null));
        Fields(LooseCard(panel))["price"].Text = "777";
        Selected(LooseCard(panel)).Add("price");
        panel.CopySelectedFieldsShortcut();

        WzSubProperty target = MakeConsumeItem("2040001", ("price", 1));
        int targetChildCountBefore = target.WzProperties.Count;
        Assert.True(panel.TryLoad(target, null));

        Assert.Equal(new[] { "price" }, NamesOf(PasteTo(panel, target)));

        Assert.Equal(targetChildCountBefore, target.WzProperties.Count); // no node inserted
        Assert.Null(target["2040000"]);                                  // specifically not the source
        Assert.Equal(100, IntValueOf(source, "price"));                  // source left alone
        Assert.False(ImageOf(source).Changed);
    });

    [Fact]
    public void MismatchedTargetCard_KeepsFieldBranchAndClipboard_SoTreePasteNeverRuns() => RunSta(() =>
    {
        var panel = new NodeEditorPanel();

        Assert.True(panel.TryLoad(MakeConsumeItem("2040000", ("price", 100)), null));
        Selected(LooseCard(panel)).Add("price");
        panel.CopySelectedFieldsShortcut();

        WzSubProperty target = MakeItemWithOnlySpecContainer("9999999");
        Assert.True(panel.TryLoad(target, null));
        Assert.Null(LooseCard(panel)); // no loose card was even built for this item

        // Nothing written, so nothing is reported and MainPanel reddens no node.
        Assert.Empty(PasteTo(panel, target));
        Assert.False(ImageOf(target).Changed);

        // HasCopiedFields staying true is what stops MainForm from ever reaching DoPaste() - an
        // incompatible target must not silently fall back to pasting the whole tree node. The
        // copy also stays staged so the user can go to a compatible card and retry.
        Assert.True(panel.HasCopiedFields);

        // The user is told nothing landed. A loose copy treats the target item itself as the
        // container, so this reports the field as absent rather than the card as absent - the
        // outcome is identical either way (properties are never created by a paste), and
        // BatchPaste_NoTargetHasAMatchingCard... covers the no-card-at-all wording.
        Assert.Contains("略過", StatusText(panel));
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

    // ---- batch paste: one Ctrl+V over a multi-node tree selection --------------------------

    /// <summary>Copies one field off a fresh source item and returns the panel, ready to paste.</summary>
    private static NodeEditorPanel PanelWithCopiedConsumeField(string fieldName, string stagedValue,
        params (string Name, int Value)[] sourceFields)
    {
        var panel = new NodeEditorPanel();
        Assert.True(panel.TryLoad(MakeConsumeItem("2040000", sourceFields), null));
        object sourceCard = LooseCard(panel);
        Fields(sourceCard)[fieldName].Text = stagedValue;
        Selected(sourceCard).Add(fieldName);
        panel.CopySelectedFieldsShortcut();
        return panel;
    }

    [Fact]
    public void BatchPaste_WritesEveryTarget_NotJustTheActiveOne() => RunSta(() =>
    {
        NodeEditorPanel panel = PanelWithCopiedConsumeField("price", "777", ("price", 100));

        WzSubProperty[] targets = new[] { "2040001", "2040002", "2040003", "2040004", "2040005" }
            .Select(id => MakeConsumeItem(id, ("price", 1)))
            .ToArray();

        // Only one target is ever displayed; the rest are written straight on their WzObject.
        Assert.True(panel.TryLoad(targets[0], null));

        IReadOnlyList<WzImageProperty> written = PasteTo(panel, targets);

        Assert.Equal(5, written.Count);
        Assert.All(NamesOf(written), name => Assert.Equal("price", name));
        foreach (WzSubProperty target in targets)
        {
            Assert.Equal(777, IntValueOf(target, "price"));
            Assert.True(ImageOf(target).Changed);
            // The reported property is that target's own leaf, so MainPanel reddens five
            // separate price nodes and never their parents.
            Assert.Contains(target["price"], written);
        }

        // The displayed target's box tracks the WZ; the others have no UI at all.
        Assert.Equal("777", Fields(LooseCard(panel))["price"].Text);
    });

    [Fact]
    public void BatchPaste_TwoFieldsAcrossFiveTargets_ReportsAllTenWrites() => RunSta(() =>
    {
        var panel = new NodeEditorPanel();
        Assert.True(panel.TryLoad(MakeConsumeItem("2040000", ("price", 100), ("slotMax", 200)), null));
        object sourceCard = LooseCard(panel);
        Fields(sourceCard)["price"].Text = "777";
        Fields(sourceCard)["slotMax"].Text = "888";
        Selected(sourceCard).Add("price");
        Selected(sourceCard).Add("slotMax");
        panel.CopySelectedFieldsShortcut();

        WzSubProperty[] targets = new[] { "2040001", "2040002", "2040003", "2040004", "2040005" }
            .Select(id => MakeConsumeItem(id, ("price", 1), ("slotMax", 2)))
            .ToArray();

        Assert.Equal(10, PasteTo(panel, targets).Count);
        foreach (WzSubProperty target in targets)
        {
            Assert.Equal(777, IntValueOf(target, "price"));
            Assert.Equal(888, IntValueOf(target, "slotMax"));
        }
    });

    [Fact]
    public void BatchPaste_OneTargetMissingTheField_LeavesTheOthersWritten() => RunSta(() =>
    {
        NodeEditorPanel panel = PanelWithCopiedConsumeField("price", "777", ("price", 100));

        WzSubProperty ok1 = MakeConsumeItem("2040001", ("price", 1));
        WzSubProperty noPrice = MakeConsumeItem("2040002", ("slotMax", 2)); // no price at all
        WzSubProperty ok2 = MakeConsumeItem("2040003", ("price", 3));

        Assert.Equal(2, PasteTo(panel, ok1, noPrice, ok2).Count);

        Assert.Equal(777, IntValueOf(ok1, "price"));
        Assert.Equal(777, IntValueOf(ok2, "price"));
        Assert.Equal(2, IntValueOf(noPrice, "slotMax")); // untouched
        Assert.False(ImageOf(noPrice).Changed);          // and not marked dirty either
    });

    [Fact]
    public void BatchPaste_OneTargetTypeMismatch_LeavesTheOthersWritten() => RunSta(() =>
    {
        NodeEditorPanel panel = PanelWithCopiedConsumeField("price", "777", ("price", 100));

        WzSubProperty ok1 = MakeConsumeItem("2040001", ("price", 1));
        WzSubProperty ok2 = MakeConsumeItem("2040003", ("price", 3));

        // A price that isn't an int property: "777" can't be written into a sub-container.
        var mismatch = new WzSubProperty("2040002");
        var nested = new WzSubProperty("price");
        nested.AddProperty(new WzIntProperty("inner", 1));
        mismatch.AddProperty(nested);
        mismatch.AddProperty(new WzIntProperty("slotMax", 5));
        AttachToImage(mismatch, "0204.img");

        IReadOnlyList<WzImageProperty> written = PasteTo(panel, ok1, mismatch, ok2);

        Assert.Equal(2, written.Count);
        Assert.Equal(777, IntValueOf(ok1, "price"));
        Assert.Equal(777, IntValueOf(ok2, "price"));
        Assert.DoesNotContain(nested, written);   // rejected -> not reported -> never reddened
        Assert.False(ImageOf(mismatch).Changed);
    });

    [Fact]
    public void BatchPaste_OnlyWritesCopiedFields_AcrossEveryTarget() => RunSta(() =>
    {
        NodeEditorPanel panel = PanelWithCopiedConsumeField("slotMax", "9999", ("price", 100), ("slotMax", 200));

        WzSubProperty[] targets = new[] { "2040001", "2040002" }
            .Select(id => MakeConsumeItem(id, ("price", 1), ("slotMax", 2)))
            .ToArray();

        Assert.Equal(new[] { "slotMax", "slotMax" }, NamesOf(PasteTo(panel, targets)));
        foreach (WzSubProperty target in targets)
        {
            Assert.Equal(9999, IntValueOf(target, "slotMax"));
            Assert.Equal(1, IntValueOf(target, "price")); // never carried, never written
        }
    });

    [Fact]
    public void BatchPaste_EquipInfoCard_LandsInEachTargetsOwnInfo() => RunSta(() =>
    {
        var panel = new NodeEditorPanel();
        Assert.True(panel.TryLoad(MakeEquipItem("01000000", ("reqSTR", 40), ("incPDD", 10)), null));
        object sourceCard = CardTitled(panel, "info");
        Fields(sourceCard)["reqSTR"].Text = "999";
        Selected(sourceCard).Add("reqSTR");
        panel.CopySelectedFieldsShortcut();

        WzSubProperty[] targets = new[] { "01000001", "01000002", "01000003" }
            .Select(id => MakeEquipItem(id, ("reqSTR", 1), ("incPDD", 1)))
            .ToArray();

        IReadOnlyList<WzImageProperty> written = PasteTo(panel, targets);

        Assert.Equal(3, written.Count);
        foreach (WzSubProperty target in targets)
        {
            Assert.Equal(999, IntValueOf(target, "info/reqSTR"));
            Assert.Equal(1, IntValueOf(target, "info/incPDD"));
            // Matched into that target's own info, so info's leaf - not info itself - is reported.
            Assert.Contains(target["info"]["reqSTR"], written);
            Assert.DoesNotContain(target["info"], written);
        }
    });

    [Fact]
    public void BatchPaste_NeverClonesTheSourceNodeIntoAnyTarget() => RunSta(() =>
    {
        NodeEditorPanel panel = PanelWithCopiedConsumeField("price", "777", ("price", 100));

        WzSubProperty[] targets = new[] { "2040001", "2040002", "2040003" }
            .Select(id => MakeConsumeItem(id, ("price", 1)))
            .ToArray();
        int[] childCountsBefore = targets.Select(t => t.WzProperties.Count).ToArray();

        Assert.Equal(3, PasteTo(panel, targets).Count);

        for (int i = 0; i < targets.Length; i++)
        {
            Assert.Equal(childCountsBefore[i], targets[i].WzProperties.Count);
            Assert.Null(targets[i]["2040000"]); // specifically not the source item
        }
    });

    [Fact]
    public void BatchPaste_NoTargetHasAMatchingCard_WritesNothingAndStaysOnTheFieldBranch() => RunSta(() =>
    {
        // An "info" copy onto targets that have no info at all - the copy must not leak into
        // their loose fields or any other container.
        var panel = new NodeEditorPanel();
        Assert.True(panel.TryLoad(MakeEquipItem("01000000", ("reqSTR", 40)), null));
        object sourceCard = CardTitled(panel, "info");
        Fields(sourceCard)["reqSTR"].Text = "999";
        Selected(sourceCard).Add("reqSTR");
        panel.CopySelectedFieldsShortcut();

        WzSubProperty[] targets = new[] { "2040001", "2040002" }
            .Select(id => MakeConsumeItem(id, ("reqSTR", 1)))
            .ToArray();

        Assert.Empty(PasteTo(panel, targets));
        Assert.Contains("沒有同類卡片可以貼上", StatusText(panel));

        foreach (WzSubProperty target in targets)
        {
            Assert.Equal(1, IntValueOf(target, "reqSTR")); // same-named loose field left alone
            Assert.False(ImageOf(target).Changed);
        }

        // Clipboard survives, so MainForm keeps routing Ctrl+V to fields and never falls back to
        // the tree's whole-node paste.
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

        WzSubProperty target = MakeEquipItem("01000001", ("reqSTR", 1), ("incPDD", 1));
        Assert.True(panel.TryLoad(target, null));

        // Reported property is the leaf under info - info itself is never reported, so it never
        // goes red.
        IReadOnlyList<WzImageProperty> written = PasteTo(panel, target);
        Assert.Equal(new[] { "reqSTR" }, NamesOf(written));
        Assert.Same(target["info"]["reqSTR"], written[0]);

        Assert.Equal(999, IntValueOf(target, "info/reqSTR"));
        Assert.Equal(1, IntValueOf(target, "info/incPDD")); // not carried by the copy
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
