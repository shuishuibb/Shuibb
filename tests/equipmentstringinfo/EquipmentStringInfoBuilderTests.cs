using System.Collections.Generic;
using HaRepacker;
using HaRepacker.GUI.EquipmentStringInfo;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using Xunit;
using Assert = Xunit.Assert;

namespace EquipmentStringInfo.Tests;

/// <summary>
/// Targeted regression for the "裝備 String 資訊" feature: EquipmentStringInfoBuilder's
/// identification/traversal/multi-source logic and EquipmentStringInfoResult's clipboard
/// formatting. No GUI is driven - these build in-memory WzFile/WzImage fixtures directly (same
/// style MapleLib's own WzSerializerAdversarialTests and tests/npcinfo use) and assert on the
/// resulting EquipmentStringInfoResult.
/// </summary>
public sealed class EquipmentStringInfoBuilderTests
{
    // ---- fixture builders -------------------------------------------------

    private static WzFile MakeWzFile(string name)
    {
        return new WzFile(1, WzMapleVersion.BMS) { Name = name };
    }

    private static WzImage AddEquipImage(WzFile equipWzFile, string paddedId)
    {
        WzImage image = new WzImage(paddedId + ".img");
        // See tests/npcinfo's AddNpcImage for why the root directory is built through the public
        // 2-arg WzDirectory constructor (named identically to the WzFile) rather than
        // equipWzFile.WzDirectory directly - it's what makes WzFileParent/the Parent walk resolve
        // the same way real parsing does.
        WzDirectory rootDirectory = new WzDirectory(equipWzFile.Name, equipWzFile);
        rootDirectory.AddImage(image);
        return image;
    }

    private static WzSubProperty AddEqpStringEntry(WzFile stringWzFile, string category, string unpaddedId)
    {
        WzImage eqpImg = (WzImage)stringWzFile.WzDirectory["Eqp.img"];
        if (eqpImg == null)
        {
            eqpImg = new WzImage("Eqp.img");
            stringWzFile.WzDirectory.AddImage(eqpImg);
        }

        WzSubProperty eqpRoot = (WzSubProperty)eqpImg["Eqp"];
        if (eqpRoot == null)
        {
            eqpRoot = new WzSubProperty("Eqp");
            eqpImg.AddProperty(eqpRoot);
        }

        WzSubProperty categoryProp = (WzSubProperty)eqpRoot[category];
        if (categoryProp == null)
        {
            categoryProp = new WzSubProperty(category);
            eqpRoot.AddProperty(categoryProp);
        }

        WzSubProperty entry = new WzSubProperty(unpaddedId);
        categoryProp.AddProperty(entry);
        return entry;
    }

    private static WzNode NodeFor(WzImage image) => new WzNode(image);

    // ---- IsEquipmentImageNode / TryGetEquipmentImage -----------------------------------

    [Fact]
    public void TryGetEquipmentImage_ValidWeapon_ResolvesUnpaddedItemId()
    {
        WzFile weaponWzFile = MakeWzFile("Weapon_000.wz");
        WzImage image = AddEquipImage(weaponWzFile, "01302000");

        bool ok = EquipmentStringInfoBuilder.TryGetEquipmentImage(NodeFor(image), out WzImage resolved, out string itemId);

        Assert.True(ok);
        Assert.Same(image, resolved);
        Assert.Equal("1302000", itemId);
    }

    [Fact]
    public void IsEquipmentImageNode_RejectsIdOutsideEquipmentRange()
    {
        // ItemIdsCategory.IsEquipment requires id / 1,000,000 == 1; "00002001" -> 2001 is not.
        WzFile someWzFile = MakeWzFile("Character_000.wz");
        WzImage image = AddEquipImage(someWzFile, "00002001");

        Assert.False(EquipmentStringInfoBuilder.IsEquipmentImageNode(NodeFor(image)));
    }

    [Theory]
    [InlineData("Npc.wz")]
    [InlineData("Mob_000.wz")]
    [InlineData("Map0_000.wz")]
    [InlineData("Reactor.wz")]
    public void IsEquipmentImageNode_RejectsEquipRangeIdUnderKnownNonEquipWzFile(string wzFileName)
    {
        // Same equip-shaped numeric range could in principle coincide under an unrelated WZ file;
        // the known-family exclusion must still reject it.
        WzFile otherWzFile = MakeWzFile(wzFileName);
        WzImage image = AddEquipImage(otherWzFile, "01234567");

        Assert.False(EquipmentStringInfoBuilder.IsEquipmentImageNode(NodeFor(image)));
    }

    [Fact]
    public void IsEquipmentImageNode_SafelyRejectsNonImageAndNullNodes()
    {
        Assert.False(EquipmentStringInfoBuilder.IsEquipmentImageNode(null));

        WzDirectory dir = new WzDirectory("01302000");
        Assert.False(EquipmentStringInfoBuilder.IsEquipmentImageNode(new WzNode(dir)));

        WzNode looseNode = new WzNode(new WzImage("01302000.img"));
        looseNode.Tag = null;
        Assert.False(EquipmentStringInfoBuilder.IsEquipmentImageNode(looseNode));
    }

    // ---- String name / extras -----------------------------------------------

    [Fact]
    public void Build_StringName_ReadCorrectlyRegardlessOfCategoryName()
    {
        WzFile weaponWzFile = MakeWzFile("Weapon_000.wz");
        WzImage image = AddEquipImage(weaponWzFile, "01302000");

        WzFile stringWzFile = MakeWzFile("String_000.wz");
        // Category name intentionally doesn't have to be derived from the equip WzFile's own
        // name - the builder scans whatever categories Eqp/Eqp actually has.
        AddEqpStringEntry(stringWzFile, "Weapon", "1302000").AddProperty(new WzStringProperty("name", "劍"));

        EquipmentStringInfoResult result = EquipmentStringInfoBuilder.Build(image, "1302000", new List<WzFile> { weaponWzFile, stringWzFile });

        Assert.Single(result.Sources);
        Assert.Equal("劍", result.Sources[0].Name);
        Assert.Equal("Eqp.img/Eqp/Weapon/1302000", result.Sources[0].LogicalPath);
        Assert.Equal(new[] { "String_000.wz" }, result.Sources[0].SourceFileNames);
    }

    [Fact]
    public void Build_ExtraScalarProperties_IncludedAndNameExcluded()
    {
        WzFile weaponWzFile = MakeWzFile("Weapon_000.wz");
        WzImage image = AddEquipImage(weaponWzFile, "01302000");

        WzFile stringWzFile = MakeWzFile("String_000.wz");
        WzSubProperty entry = AddEqpStringEntry(stringWzFile, "Weapon", "1302000");
        entry.AddProperty(new WzStringProperty("name", "劍"));
        entry.AddProperty(new WzStringProperty("desc", "一把普通的劍。"));

        EquipmentStringInfoResult result = EquipmentStringInfoBuilder.Build(image, "1302000", new List<WzFile> { weaponWzFile, stringWzFile });

        Assert.Contains("desc: 一把普通的劍。", result.Sources[0].Extras);
        Assert.DoesNotContain(result.Sources[0].Extras, v => v.StartsWith("name:"));
    }

    [Fact]
    public void Build_StringWzNotLoaded_SafePlaceholder()
    {
        WzFile weaponWzFile = MakeWzFile("Weapon_000.wz");
        WzImage image = AddEquipImage(weaponWzFile, "01302000");

        EquipmentStringInfoResult result = EquipmentStringInfoBuilder.Build(image, "1302000", new List<WzFile> { weaponWzFile });

        Assert.Single(result.Sources);
        Assert.True(result.Sources[0].IsPlaceholder);
        Assert.Null(result.Sources[0].Name);
        Assert.Equal("沒有值", EquipmentStringInfoResult.DisplayScalar(result.Sources[0].Name));
        Assert.Equal("沒有值", EquipmentStringInfoResult.DisplayScalar(result.Sources[0].LogicalPath));
    }

    [Fact]
    public void Build_NullLoadedWzFiles_IsSafe()
    {
        WzFile weaponWzFile = MakeWzFile("Weapon_000.wz");
        WzImage image = AddEquipImage(weaponWzFile, "01302000");

        EquipmentStringInfoResult result = EquipmentStringInfoBuilder.Build(image, "1302000", null);

        Assert.Single(result.Sources);
        Assert.True(result.Sources[0].IsPlaceholder);
    }

    [Fact]
    public void Build_ItemIdNotPresentInStringWz_SafePlaceholder()
    {
        WzFile weaponWzFile = MakeWzFile("Weapon_000.wz");
        WzImage image = AddEquipImage(weaponWzFile, "01302000");

        WzFile stringWzFile = MakeWzFile("String_000.wz");
        AddEqpStringEntry(stringWzFile, "Weapon", "1999999").AddProperty(new WzStringProperty("name", "SomeOtherWeapon"));

        EquipmentStringInfoResult result = EquipmentStringInfoBuilder.Build(image, "1302000", new List<WzFile> { weaponWzFile, stringWzFile });

        Assert.Single(result.Sources);
        Assert.True(result.Sources[0].IsPlaceholder);
    }

    // ---- multiple String.wz sources -----------------------------------------------

    [Fact]
    public void Build_MultipleDifferingStringSources_KeepsBothNotJustFirst()
    {
        WzFile weaponWzFile = MakeWzFile("Weapon_000.wz");
        WzImage image = AddEquipImage(weaponWzFile, "01302000");

        WzFile stringTw = MakeWzFile("String_000.wz");
        AddEqpStringEntry(stringTw, "Weapon", "1302000").AddProperty(new WzStringProperty("name", "劍"));

        WzFile stringCn = MakeWzFile("String_001.wz");
        AddEqpStringEntry(stringCn, "Weapon", "1302000").AddProperty(new WzStringProperty("name", "剑"));

        EquipmentStringInfoResult result = EquipmentStringInfoBuilder.Build(
            image, "1302000", new List<WzFile> { weaponWzFile, stringTw, stringCn });

        Assert.Equal(2, result.Sources.Count);
        List<string> names = new List<string> { result.Sources[0].Name, result.Sources[1].Name };
        Assert.Contains("劍", names);
        Assert.Contains("剑", names);
    }

    [Fact]
    public void Build_IdenticalResultsAcrossSources_Deduplicate()
    {
        WzFile weaponWzFile = MakeWzFile("Weapon_000.wz");
        WzImage image = AddEquipImage(weaponWzFile, "01302000");

        WzFile stringA = MakeWzFile("String_000.wz");
        AddEqpStringEntry(stringA, "Weapon", "1302000").AddProperty(new WzStringProperty("name", "劍"));

        WzFile stringB = MakeWzFile("String_001.wz");
        AddEqpStringEntry(stringB, "Weapon", "1302000").AddProperty(new WzStringProperty("name", "劍"));

        EquipmentStringInfoResult result = EquipmentStringInfoBuilder.Build(
            image, "1302000", new List<WzFile> { weaponWzFile, stringA, stringB });

        Assert.Single(result.Sources);
        Assert.Equal("劍", result.Sources[0].Name);
        Assert.Equal(new[] { "String_000.wz", "String_001.wz" }, result.Sources[0].SourceFileNames);
    }

    // ---- WZ path -------------------------------------------------------------

    [Fact]
    public void Build_WzPath_JoinsFileAndImageNameWithSlash()
    {
        WzFile weaponWzFile = MakeWzFile("Weapon_000.wz");
        WzImage image = AddEquipImage(weaponWzFile, "01302000");

        EquipmentStringInfoResult result = EquipmentStringInfoBuilder.Build(image, "1302000", new List<WzFile> { weaponWzFile });

        Assert.Equal("Weapon_000.wz/01302000.img", result.EquipWzPath);
    }

    // ---- read-only guarantee ---------------------------------------------------

    [Fact]
    public void Build_DoesNotMarkTheEquipImageChanged()
    {
        WzFile weaponWzFile = MakeWzFile("Weapon_000.wz");
        WzImage image = AddEquipImage(weaponWzFile, "01302000");

        WzFile stringWzFile = MakeWzFile("String_000.wz");
        AddEqpStringEntry(stringWzFile, "Weapon", "1302000").AddProperty(new WzStringProperty("name", "劍"));

        // WzImage.AddProperty marks the image Changed as an intrinsic part of constructing this
        // fixture (see tests/npcinfo's equivalent test for why) - reset it so the assertion below
        // is actually about what Build() does.
        image.Changed = false;

        EquipmentStringInfoBuilder.Build(image, "1302000", new List<WzFile> { weaponWzFile, stringWzFile });

        Assert.False(image.Changed);
    }

    // ---- clipboard formatting ---------------------------------------------------

    [Fact]
    public void ToClipboardText_SingleSource_IncludesAllFields()
    {
        EquipmentStringInfoResult result = new EquipmentStringInfoResult(
            "1302000",
            "Weapon_000.wz/01302000.img",
            new List<EquipmentStringSourceResult>
            {
                new EquipmentStringSourceResult(new[] { "String_000.wz" }, "劍", "Eqp.img/Eqp/Weapon/1302000", new[] { "desc: test" })
            });

        string text = result.ToClipboardText();

        Assert.Contains("1302000", text);
        Assert.Contains("Weapon_000.wz/01302000.img", text);
        Assert.Contains("String_000.wz", text);
        Assert.Contains("劍", text);
        Assert.Contains("Eqp.img/Eqp/Weapon/1302000", text);
        Assert.Contains("desc: test", text);
    }

    [Fact]
    public void ToClipboardText_Placeholder_ShowsNoValuesWithoutSourceHeader()
    {
        EquipmentStringInfoResult result = new EquipmentStringInfoResult(
            "1302000",
            "Weapon_000.wz/01302000.img",
            new List<EquipmentStringSourceResult>
            {
                new EquipmentStringSourceResult(System.Array.Empty<string>(), null, null, System.Array.Empty<string>())
            });

        string text = result.ToClipboardText();

        Assert.DoesNotContain("來源", text);
        Assert.Contains("名稱\r\n沒有值", text);
        Assert.Contains("String.wz logical path\r\n沒有值", text);
        Assert.Contains("String 額外資訊\r\n沒有值", text);
    }

    [Fact]
    public void ToClipboardText_MultipleSources_ShowsSourceHeaders()
    {
        EquipmentStringInfoResult result = new EquipmentStringInfoResult(
            "1302000",
            "Weapon_000.wz/01302000.img",
            new List<EquipmentStringSourceResult>
            {
                new EquipmentStringSourceResult(new[] { "String_000.wz" }, "劍", "Eqp.img/Eqp/Weapon/1302000", System.Array.Empty<string>()),
                new EquipmentStringSourceResult(new[] { "String_001.wz" }, "剑", "Eqp.img/Eqp/Weapon/1302000", System.Array.Empty<string>()),
            });

        string text = result.ToClipboardText();

        Assert.Contains("來源 1", text);
        Assert.Contains("來源 2", text);
        Assert.Contains("劍", text);
        Assert.Contains("剑", text);
    }
}
