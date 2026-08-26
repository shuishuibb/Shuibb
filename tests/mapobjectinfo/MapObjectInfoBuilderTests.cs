using System.Collections.Generic;
using HaRepacker;
using HaRepacker.GUI.MapObjectInfo;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using Xunit;
using Assert = Xunit.Assert;

namespace MapObjectInfo.Tests;

/// <summary>
/// Targeted regression for the "地圖物件資訊" (map object info) feature: MapObjectInfoBuilder's
/// WZ traversal/union logic and MapObjectInfoResult's clipboard formatting. No GUI is driven -
/// these build in-memory WzImage fixtures directly (same style MapleLib's own
/// WzSerializerAdversarialTests uses) and assert on the resulting MapObjectInfoResult.
/// </summary>
public sealed class MapObjectInfoBuilderTests
{
    // ---- fixture builders -------------------------------------------------

    private static WzImage MakeMapImage(string mapId)
    {
        return new WzImage(mapId + ".img");
    }

    private static WzSubProperty MakeContainer(WzImage owner, string name)
    {
        WzSubProperty container = new WzSubProperty(name);
        owner.AddProperty(container);
        return container;
    }

    private static WzSubProperty AddEntry(WzSubProperty container, string entryName)
    {
        WzSubProperty entry = new WzSubProperty(entryName);
        container.AddProperty(entry);
        return entry;
    }

    private static void AddString(WzSubProperty entry, string propName, string value)
    {
        entry.AddProperty(new WzStringProperty(propName, value));
    }

    private static WzNode NodeFor(WzImage image) => new WzNode(image);

    // ---- IsMapImageNode / TryGetMapImage -----------------------------------

    [Theory]
    [InlineData("910000000.img", true)]
    [InlineData("910000001.img", true)]
    [InlineData("0.img", true)]
    [InlineData("String.img", false)]
    [InlineData("910000000.xml", false)]
    [InlineData("910000000", false)]
    [InlineData(".img", false)]
    public void IsMapImageNode_RecognizesOnlyNumericDotImgNames(string imageName, bool expected)
    {
        WzImage image = new WzImage(imageName);
        WzNode node = NodeFor(image);

        Assert.Equal(expected, MapObjectInfoBuilder.IsMapImageNode(node));
    }

    [Fact]
    public void IsMapImageNode_SafelyRejectsNonImageAndNullNodes()
    {
        Assert.False(MapObjectInfoBuilder.IsMapImageNode(null));

        WzDirectory dir = new WzDirectory("910000000");
        WzNode dirNode = new WzNode(dir);
        Assert.False(MapObjectInfoBuilder.IsMapImageNode(dirNode));

        WzNode looseNode = new WzNode(new WzImage("910000000.img"));
        looseNode.Tag = null;
        Assert.False(MapObjectInfoBuilder.IsMapImageNode(looseNode));
    }

    [Fact]
    public void TryGetMapImage_StripsImgExtensionForMapId()
    {
        WzImage image = MakeMapImage("910000000");
        WzNode node = NodeFor(image);

        bool ok = MapObjectInfoBuilder.TryGetMapImage(node, out WzImage resolved, out string mapId);

        Assert.True(ok);
        Assert.Same(image, resolved);
        Assert.Equal("910000000", mapId);
    }

    // ---- single map summary -------------------------------------------------

    [Fact]
    public void Build_SingleMap_CollectsAllNineBlocks()
    {
        WzImage map = MakeMapImage("910000000");

        WzSubProperty info = MakeContainer(map, "info");
        info.AddProperty(new WzStringProperty("mapMark", "town"));
        info.AddProperty(new WzStringProperty("bgm", "Bgm00/FloralLife"));

        WzSubProperty back = MakeContainer(map, "back");
        AddString(AddEntry(back, "0"), "bS", "grassySoil");

        WzSubProperty layer0 = MakeContainer(map, "0");
        WzSubProperty tile = new WzSubProperty("tile");
        layer0.AddProperty(tile);
        AddString(AddEntry(tile, "0"), "u", "wood");
        WzSubProperty obj = new WzSubProperty("obj");
        layer0.AddProperty(obj);
        AddString(AddEntry(obj, "0"), "oS", "mapleMap");

        WzSubProperty life = MakeContainer(map, "life");
        WzSubProperty npcEntry = AddEntry(life, "0");
        AddString(npcEntry, "type", "n");
        AddString(npcEntry, "id", "9000000");
        WzSubProperty mobEntry = AddEntry(life, "1");
        AddString(mobEntry, "type", "m");
        AddString(mobEntry, "id", "100100");

        WzSubProperty reactor = MakeContainer(map, "reactor");
        AddString(AddEntry(reactor, "0"), "id", "2001000");

        MapObjectInfoResult result = MapObjectInfoBuilder.Build(new[] { NodeFor(map) });

        Assert.Equal(new[] { "910000000" }, result.SelectedMaps);
        Assert.Equal(new[] { "town" }, result.MapMarks);
        Assert.Equal(new[] { "Bgm00/FloralLife" }, result.Bgms);
        Assert.Equal(new[] { "grassySoil" }, result.Backs);
        Assert.Equal(new[] { "wood" }, result.Tiles);
        Assert.Equal(new[] { "mapleMap" }, result.Objs);
        Assert.Equal(new[] { "9000000" }, result.Npcs);
        Assert.Equal(new[] { "100100" }, result.Mobs);
        Assert.Equal(new[] { "2001000" }, result.Reactors);
    }

    // ---- multi-map union + dedup --------------------------------------------

    [Fact]
    public void Build_MultipleMaps_UnionsAndDeduplicatesAcrossMaps()
    {
        WzImage mapA = MakeMapImage("910000000");
        MakeContainer(mapA, "info").AddProperty(new WzStringProperty("mapMark", "town"));
        WzSubProperty backA = MakeContainer(mapA, "back");
        AddString(AddEntry(backA, "0"), "bS", "grassySoil");

        WzImage mapB = MakeMapImage("910000001");
        MakeContainer(mapB, "info").AddProperty(new WzStringProperty("mapMark", "field"));
        WzSubProperty backB = MakeContainer(mapB, "back");
        AddString(AddEntry(backB, "0"), "bS", "grassySoil"); // duplicate of mapA's value
        AddString(AddEntry(backB, "1"), "bS", "cloud");

        MapObjectInfoResult result = MapObjectInfoBuilder.Build(new[] { NodeFor(mapA), NodeFor(mapB) });

        Assert.Equal(new[] { "910000000", "910000001" }, result.SelectedMaps);
        // Both mapMark values kept - not just the first map's.
        Assert.Equal(new[] { "field", "town" }, result.MapMarks);
        // "grassySoil" appears in both maps but only once in the union.
        Assert.Equal(new[] { "cloud", "grassySoil" }, result.Backs);
    }

    [Fact]
    public void Build_DuplicateValuesWithinTheSameMap_AppearOnlyOnce()
    {
        WzImage map = MakeMapImage("910000000");
        WzSubProperty reactor = MakeContainer(map, "reactor");
        AddString(AddEntry(reactor, "0"), "id", "2001000");
        AddString(AddEntry(reactor, "1"), "id", "2001000");
        AddString(AddEntry(reactor, "2"), "id", "2001001");

        MapObjectInfoResult result = MapObjectInfoBuilder.Build(new[] { NodeFor(map) });

        Assert.Equal(new[] { "2001000", "2001001" }, result.Reactors);
    }

    // ---- life type classification -------------------------------------------

    [Fact]
    public void Build_LifeEntries_ClassifyByTypeAndIgnoreOtherTypes()
    {
        WzImage map = MakeMapImage("910000000");
        WzSubProperty life = MakeContainer(map, "life");

        WzSubProperty npc = AddEntry(life, "0");
        AddString(npc, "type", "n");
        AddString(npc, "id", "9000000");

        WzSubProperty mob = AddEntry(life, "1");
        AddString(mob, "type", "m");
        AddString(mob, "id", "100100");

        // Neither "n" nor "m" - must not show up as either.
        WzSubProperty other = AddEntry(life, "2");
        AddString(other, "type", "s");
        AddString(other, "id", "9999999");

        MapObjectInfoResult result = MapObjectInfoBuilder.Build(new[] { NodeFor(map) });

        Assert.Equal(new[] { "9000000" }, result.Npcs);
        Assert.Equal(new[] { "100100" }, result.Mobs);
    }

    // ---- defensive: missing/malformed data -----------------------------------

    [Fact]
    public void Build_LifeEntryMissingTypeOrId_IsSkippedSafely()
    {
        WzImage map = MakeMapImage("910000000");
        WzSubProperty life = MakeContainer(map, "life");

        WzSubProperty missingId = AddEntry(life, "0");
        AddString(missingId, "type", "n"); // no id

        WzSubProperty missingType = AddEntry(life, "1");
        AddString(missingType, "id", "123"); // no type

        WzSubProperty valid = AddEntry(life, "2");
        AddString(valid, "type", "n");
        AddString(valid, "id", "555");

        MapObjectInfoResult result = MapObjectInfoBuilder.Build(new[] { NodeFor(map) });

        Assert.Equal(new[] { "555" }, result.Npcs);
        Assert.Empty(result.Mobs);
    }

    [Fact]
    public void Build_MissingTopLevelContainers_ProduceEmptyListsNotCrash()
    {
        // A map image with no back/life/reactor/info/layers at all.
        WzImage map = MakeMapImage("910000000");

        MapObjectInfoResult result = MapObjectInfoBuilder.Build(new[] { NodeFor(map) });

        Assert.Equal(new[] { "910000000" }, result.SelectedMaps);
        Assert.Empty(result.MapMarks);
        Assert.Empty(result.Bgms);
        Assert.Empty(result.Backs);
        Assert.Empty(result.Tiles);
        Assert.Empty(result.Objs);
        Assert.Empty(result.Npcs);
        Assert.Empty(result.Mobs);
        Assert.Empty(result.Reactors);
    }

    [Fact]
    public void Build_BackEntryMissingBS_IsSkippedSafely()
    {
        WzImage map = MakeMapImage("910000000");
        WzSubProperty back = MakeContainer(map, "back");
        AddEntry(back, "0"); // no bS property at all
        AddString(AddEntry(back, "1"), "bS", "grassySoil");

        MapObjectInfoResult result = MapObjectInfoBuilder.Build(new[] { NodeFor(map) });

        Assert.Equal(new[] { "grassySoil" }, result.Backs);
    }

    [Fact]
    public void Build_NonMapNodesAndNullsInSelection_AreSkippedWithoutAffectingValidMaps()
    {
        WzImage map = MakeMapImage("910000000");
        MakeContainer(map, "info").AddProperty(new WzStringProperty("mapMark", "town"));

        WzDirectory dir = new WzDirectory("SomeDir");
        WzImage nonMapImage = new WzImage("String.img");

        List<WzNode> selection = new List<WzNode>
        {
            new WzNode(dir),
            NodeFor(nonMapImage),
            null,
            NodeFor(map),
        };

        MapObjectInfoResult result = MapObjectInfoBuilder.Build(selection);

        Assert.Equal(new[] { "910000000" }, result.SelectedMaps);
        Assert.Equal(new[] { "town" }, result.MapMarks);
    }

    [Fact]
    public void Build_EmptyOrNullSelection_ReturnsEmptyResultWithoutThrowing()
    {
        MapObjectInfoResult emptyList = MapObjectInfoBuilder.Build(new List<WzNode>());
        MapObjectInfoResult nullList = MapObjectInfoBuilder.Build(null);

        Assert.Empty(emptyList.SelectedMaps);
        Assert.Empty(nullList.SelectedMaps);
    }

    // ---- numeric sort order ---------------------------------------------------

    [Fact]
    public void Build_NumericIds_SortNumericallyNotLexicographically()
    {
        WzImage map = MakeMapImage("910000000");
        WzSubProperty life = MakeContainer(map, "life");

        WzSubProperty mob20 = AddEntry(life, "0");
        AddString(mob20, "type", "m");
        AddString(mob20, "id", "20");

        WzSubProperty mob100 = AddEntry(life, "1");
        AddString(mob100, "type", "m");
        AddString(mob100, "id", "100");

        WzSubProperty mob3 = AddEntry(life, "2");
        AddString(mob3, "type", "m");
        AddString(mob3, "id", "3");

        MapObjectInfoResult result = MapObjectInfoBuilder.Build(new[] { NodeFor(map) });

        // Plain ordinal string sort would give "100", "20", "3"; numeric sort must give 3, 20, 100.
        Assert.Equal(new[] { "3", "20", "100" }, result.Mobs);
    }

    // ---- clipboard formatting ---------------------------------------------------

    [Fact]
    public void ToClipboardText_IncludesAllNineHeadersInOrder()
    {
        MapObjectInfoResult result = new MapObjectInfoResult(
            new[] { "910000000" },
            new[] { "town" },
            new[] { "Bgm00/FloralLife" },
            new[] { "grassySoil" },
            new[] { "wood" },
            new[] { "mapleMap" },
            new[] { "9000000" },
            new[] { "100100" },
            new[] { "2001000" });

        string text = result.ToClipboardText();

        int selectedIdx = text.IndexOf("選取地圖");
        int markIdx = text.IndexOf("mapMark");
        int bgmIdx = text.IndexOf("bgm");
        int backIdx = text.IndexOf("Back");
        int tileIdx = text.IndexOf("Tile");
        int objIdx = text.IndexOf("Obj");
        int npcIdx = text.IndexOf("Npc");
        int mobIdx = text.IndexOf("Mob");
        int reactorIdx = text.IndexOf("Reactor");

        Assert.True(selectedIdx >= 0 && selectedIdx < markIdx);
        Assert.True(markIdx < bgmIdx);
        Assert.True(bgmIdx < backIdx);
        Assert.True(backIdx < tileIdx);
        Assert.True(tileIdx < objIdx);
        Assert.True(objIdx < npcIdx);
        Assert.True(npcIdx < mobIdx);
        Assert.True(mobIdx < reactorIdx);

        Assert.Contains("910000000", text);
        Assert.Contains("2001000", text);
    }

    [Fact]
    public void ToClipboardText_EmptyBlock_ShowsNoValuesPlaceholder()
    {
        MapObjectInfoResult result = new MapObjectInfoResult(
            new[] { "910000000" },
            new string[0],
            new string[0],
            new string[0],
            new string[0],
            new string[0],
            new string[0],
            new string[0],
            new string[0]);

        string text = result.ToClipboardText();

        Assert.Contains("Npc\r\n沒有值", text);
    }
}
