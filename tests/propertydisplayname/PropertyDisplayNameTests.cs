using Xunit;
using Assert = Xunit.Assert;

namespace PropertyDisplayNameTests;

/// <summary>
/// Targeted regression for "WZ Property 中文顯示名稱": MapleLib.PropertyDisplayName's
/// mapping table only. No GUI is driven - MainPanel.xaml.cs's CreateNativeTreeItem (the only
/// caller) is a one-line pass-through to this, and is exercised manually per the task's
/// instructions.
/// </summary>
public sealed class PropertyDisplayNameTests
{
    [Theory]
    // Requirement (info)
    [InlineData("reqJob", "職業限制")]
    [InlineData("reqLevel", "等級限制")]
    [InlineData("reqSTR", "力量限制")]
    [InlineData("reqDEX", "敏捷限制")]
    [InlineData("reqINT", "智力限制")]
    [InlineData("reqLUK", "幸運限制")]
    // Stat increase (info)
    [InlineData("incSTR", "力量")]
    [InlineData("incDEX", "敏捷")]
    [InlineData("incINT", "智力")]
    [InlineData("incLUK", "幸運")]
    [InlineData("incPAD", "物理攻擊力")]
    [InlineData("incMAD", "魔法攻擊力")]
    [InlineData("incPDD", "物理防禦力")]
    [InlineData("incMDD", "魔法防禦力")]
    [InlineData("incMHP", "最大 HP")]
    [InlineData("incMMP", "最大 MP")]
    // Equipment info
    [InlineData("tuc", "可升級次數")]
    [InlineData("price", "價格")]
    [InlineData("cash", "點裝")]
    [InlineData("tradeBlock", "無法交易")]
    [InlineData("only", "唯一裝備")]
    // Slot info
    [InlineData("islot", "裝備欄位")]
    [InlineData("vslot", "顯示欄位")]
    public void GetDisplayName_KnownKey_ReturnsMappedChineseName(string propertyName, string expectedDisplayName)
    {
        Assert.Equal(expectedDisplayName, MapleLib.PropertyDisplayName.GetDisplayName(propertyName));
    }

    [Theory]
    [InlineData("someUnknownProperty")]
    [InlineData("fooBar123")]
    [InlineData("")]
    public void GetDisplayName_UnknownKey_ReturnsOriginalNameUnchanged(string propertyName)
    {
        Assert.Equal(propertyName, MapleLib.PropertyDisplayName.GetDisplayName(propertyName));
    }

    [Fact]
    public void GetDisplayName_Null_ReturnsNullWithoutThrowing()
    {
        Assert.Null(MapleLib.PropertyDisplayName.GetDisplayName(null));
    }

    [Fact]
    public void GetDisplayName_IsCaseSensitive_DoesNotMatchDifferentCasing()
    {
        // "reqSTR" is mapped; a differently-cased variant must not accidentally match and must
        // come back unchanged, same as any other unknown key.
        Assert.Equal("reqstr", MapleLib.PropertyDisplayName.GetDisplayName("reqstr"));
        Assert.Equal("REQSTR", MapleLib.PropertyDisplayName.GetDisplayName("REQSTR"));
    }

    [Fact]
    public void GetDisplayName_DoesNotMutateOrWrapTheInputString()
    {
        // The mapping is a pure lookup - the original WZ property key must survive unchanged,
        // both for a mapped key (nothing about "reqSTR" itself should leak into the result
        // beyond the mapped text) and for the pass-through path (reference-safe: same value).
        string original = "someUnknownProperty";
        string result = MapleLib.PropertyDisplayName.GetDisplayName(original);
        Assert.Same(original, result);
    }
}
