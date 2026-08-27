using System.Collections.Generic;
using System.Linq;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using SkillPreview;
using Xunit;
using Assert = Xunit.Assert;

namespace NodeEditorCopyPasteTests;

/// <summary>
/// Targeted regression for "String.wz entries showed name/desc twice".
///
/// Selecting e.g. String_000.wz\Consume.img\2000000 rendered the dedicated STRING 文字 card *and*
/// the node's own field list, both editing the same name/desc properties. NodeEditorPanel now
/// drops the keys the STRING card took over before building the generic cards;
/// NodeEditorStringFieldFilter is that rule.
///
/// SCOPE - what these tests do NOT cover:
///   * NodeEditorPanel deciding which container the STRING card is bound to
///     (KeysHandledByStringCard's reference check against the resolved String entry),
///   * a card with nothing left not being built at all,
///   * the button relabelled 儲存文字 -> 儲存.
/// Those are panel/GUI behaviour and are verified manually.
/// </summary>
public sealed class NodeEditorStringFieldFilterTests
{
    private static List<WzImageProperty> Fields(params string[] names)
        => names.Select(name => (WzImageProperty)new WzStringProperty(name, "value")).ToList();

    private static string[] NamesOf(IEnumerable<WzImageProperty> fields)
        => fields.Select(f => f.Name).ToArray();

    /// <summary>What the STRING card binds when the entry has both texts.</summary>
    private static readonly string[] StringCardKeys = { "name", "desc" };

    [Fact]
    public void StringEntryWithOnlyNameAndDesc_LeavesNothingForTheGenericCard()
    {
        // Case A: 2000000 { name, desc } - the generic card must not be built at all.
        List<WzImageProperty> kept = NodeEditorStringFieldFilter.ExcludeHandled(
            Fields("name", "desc"), StringCardKeys);

        Assert.Empty(kept);
    }

    [Fact]
    public void StringEntryWithExtraFields_KeepsOnlyTheExtras()
    {
        // Case B: the extras stay editable in the generic card, the duplicated texts do not.
        List<WzImageProperty> kept = NodeEditorStringFieldFilter.ExcludeHandled(
            Fields("name", "desc", "price", "customFlag"), StringCardKeys);

        Assert.Equal(new[] { "price", "customFlag" }, NamesOf(kept));
    }

    [Fact]
    public void OutsideAStringContext_NameAndDescAreOrdinaryFields()
    {
        // Case C: no keys handled - nothing is filtered. This is what stops the fix from hiding
        // a name/desc pair that belongs to some unrelated node.
        List<WzImageProperty> kept = NodeEditorStringFieldFilter.ExcludeHandled(
            Fields("name", "desc"), new string[0]);

        Assert.Equal(new[] { "name", "desc" }, NamesOf(kept));
    }

    [Fact]
    public void OnlyTheKeysActuallyBoundAreRemoved()
    {
        // An entry with a desc but no name: the STRING card binds desc only, so name stays in the
        // generic card rather than vanishing.
        List<WzImageProperty> kept = NodeEditorStringFieldFilter.ExcludeHandled(
            Fields("name", "desc"), new[] { "desc" });

        Assert.Equal(new[] { "name" }, NamesOf(kept));
    }

    [Fact]
    public void UnrelatedFieldsAreNeverRemoved()
    {
        List<WzImageProperty> kept = NodeEditorStringFieldFilter.ExcludeHandled(
            Fields("price", "slotMax", "incPAD"), StringCardKeys);

        Assert.Equal(new[] { "price", "slotMax", "incPAD" }, NamesOf(kept));
    }

    [Fact]
    public void FilteringIsCaseSensitive_SoADifferentlyCasedKeyIsKept()
    {
        // WZ keys are case-sensitive; "Name" is a different property from "name" and must survive.
        List<WzImageProperty> kept = NodeEditorStringFieldFilter.ExcludeHandled(
            Fields("Name", "DESC"), StringCardKeys);

        Assert.Equal(new[] { "Name", "DESC" }, NamesOf(kept));
    }

    [Fact]
    public void TheFilterReturnsACopyAndNeverTouchesTheProperties()
    {
        // Display filtering only - the WZ objects themselves must come back untouched.
        List<WzImageProperty> source = Fields("name", "price");
        WzImageProperty price = source[1];

        List<WzImageProperty> kept = NodeEditorStringFieldFilter.ExcludeHandled(source, StringCardKeys);

        Assert.Same(price, Assert.Single(kept));
        Assert.Equal(2, source.Count);      // the input list is not mutated
        Assert.Equal("name", source[0].Name); // and nothing was renamed
    }

    [Fact]
    public void NullFields_YieldAnEmptyListRatherThanThrowing()
    {
        Assert.Empty(NodeEditorStringFieldFilter.ExcludeHandled(null, StringCardKeys));
    }
}
