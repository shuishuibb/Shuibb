using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using HaRepacker.GUI.WorldMap;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using Xunit;
using Assert = Xunit.Assert;

namespace WorldMapEditorTests;

/// <summary>
/// Targeted regression for the WorldMap editor's data layer: reading a WorldMap*.img, the
/// spot/origin coordinate transform, the mutations an edit performs, and the navigation matching.
///
/// SCOPE - what these tests do NOT cover, and must not be cited as proof of:
///   * that the BaseImg artwork or the spot markers land in the right place on screen,
///   * mouse pan / zoom / spot dragging,
///   * double-click navigation actually moving the tree,
///   * anything about a real Map.wz - the fixtures here are synthetic.
/// Those are visual and interaction behaviour, verified manually against a real Map.wz.
/// </summary>
public sealed class WorldMapDocumentTests
{
    // ---- fixtures ----------------------------------------------------------------------------

    /// <summary>
    /// Builds a WorldMap image with the structure real ones use: info\parentMap, BaseImg\0 with
    /// an origin, and MapList entries carrying spot / type / mapNo.
    /// </summary>
    private static WzImage MakeWorldMapImage(string name, string parentMap, PointF? baseOrigin,
        params (string Entry, int X, int Y, int? Type, int[] MapNo)[] spots)
    {
        var image = new WzImage(name) { Changed = false };

        if (parentMap != null)
        {
            var info = new WzSubProperty("info");
            info.AddProperty(new WzStringProperty("parentMap", parentMap));
            image.AddProperty(info);
        }

        // BaseImg is a container with the canvas one level down, as WorldMap*.img actually
        // stores it - not a canvas directly on BaseImg.
        var baseImg = new WzSubProperty("BaseImg");
        var frame = new WzCanvasProperty("0");
        if (baseOrigin.HasValue)
        {
            frame.AddProperty(new WzVectorProperty("origin",
                new WzIntProperty("x", (int)baseOrigin.Value.X),
                new WzIntProperty("y", (int)baseOrigin.Value.Y)));
        }
        baseImg.AddProperty(frame);
        image.AddProperty(baseImg);

        var mapList = new WzSubProperty("MapList");
        foreach ((string entryName, int x, int y, int? type, int[] mapNo) in spots)
        {
            var entry = new WzSubProperty(entryName);
            entry.AddProperty(new WzVectorProperty("spot", new WzIntProperty("x", x), new WzIntProperty("y", y)));
            if (type.HasValue)
                entry.AddProperty(new WzIntProperty("type", type.Value));
            if (mapNo != null)
            {
                var mapNoContainer = new WzSubProperty("mapNo");
                for (int i = 0; i < mapNo.Length; i++)
                    mapNoContainer.AddProperty(new WzIntProperty(i.ToString(), mapNo[i]));
                entry.AddProperty(mapNoContainer);
            }
            mapList.AddProperty(entry);
        }
        image.AddProperty(mapList);

        // AddProperty dirties the image while the fixture is being built; reset so a test can
        // tell an edit's dirty flag apart from construction.
        image.Changed = false;
        return image;
    }

    // ---- parsing -------------------------------------------------------------------------------

    [Fact]
    public void Load_RealWorldMapStructure_ReadsParentMapBaseOriginAndSpot()
    {
        WzImage image = MakeWorldMapImage("WorldMap060.img", "WorldMap", new PointF(320f, 235f),
            ("0", 138, -101, 0, new[] { 250000000 }));

        WorldMapDocument document = WorldMapDocument.Load(image);

        Assert.Equal("WorldMap", document.ParentMap);
        Assert.NotNull(document.BaseCanvas);
        Assert.Equal(new PointF(320f, 235f), document.BaseOrigin);
        Assert.Null(document.Warning);

        WorldMapSpot spot = Assert.Single(document.Spots);
        Assert.Equal("0", spot.EntryName);
        Assert.Equal(138, spot.SpotX);
        Assert.Equal(-101, spot.SpotY);
        Assert.Equal(0, spot.Type.Value);
        Assert.Equal(250000000, Assert.Single(spot.MapNo).Value);
    }

    [Fact]
    public void Load_ReadsEverySpotInMapList()
    {
        WzImage image = MakeWorldMapImage("WorldMap000.img", "WorldMap", new PointF(0f, 0f),
            ("0", 1, 2, 0, null), ("1", 3, 4, 1, null), ("2", 5, 6, 2, null));

        WorldMapDocument document = WorldMapDocument.Load(image);

        Assert.Equal(new[] { "0", "1", "2" }, document.Spots.Select(s => s.EntryName).ToArray());
    }

    [Fact]
    public void Load_EntryWithoutSpot_IsSkippedRatherThanCrashing()
    {
        WzImage image = MakeWorldMapImage("WorldMap000.img", "WorldMap", new PointF(0f, 0f),
            ("0", 1, 2, 0, null));

        // A MapList child carrying only a type - nothing to draw.
        var stray = new WzSubProperty("1");
        stray.AddProperty(new WzIntProperty("type", 3));
        ((WzSubProperty)image["MapList"]).AddProperty(stray);

        WorldMapDocument document = WorldMapDocument.Load(image);

        Assert.Equal("0", Assert.Single(document.Spots).EntryName);
    }

    [Fact]
    public void Load_MissingTypeAndMapNo_AreToleratedNotInvented()
    {
        WzImage image = MakeWorldMapImage("WorldMap000.img", null, new PointF(0f, 0f),
            ("0", 1, 2, null, null));

        WorldMapSpot spot = Assert.Single(WorldMapDocument.Load(image).Spots);

        Assert.Null(spot.Type);
        Assert.Empty(spot.MapNo);
        // Nothing was added to the WZ just by looking at it.
        Assert.Null(spot.Entry["type"]);
        Assert.Null(spot.Entry["mapNo"]);
    }

    [Fact]
    public void Load_WithoutBaseImg_StillReadsSpotsAndReportsTheProblem()
    {
        var image = new WzImage("WorldMap000.img");
        var mapList = new WzSubProperty("MapList");
        var entry = new WzSubProperty("0");
        entry.AddProperty(new WzVectorProperty("spot", new WzIntProperty("x", 7), new WzIntProperty("y", 8)));
        mapList.AddProperty(entry);
        image.AddProperty(mapList);

        WorldMapDocument document = WorldMapDocument.Load(image);

        Assert.Null(document.BaseCanvas);
        Assert.Equal("無法取得 BaseImg", document.Warning);
        Assert.Equal(new PointF(0f, 0f), document.BaseOrigin);
        Assert.Single(document.Spots);
    }

    [Fact]
    public void Load_WithoutMapList_YieldsNoSpots()
    {
        var image = new WzImage("WorldMap000.img");
        Assert.Empty(WorldMapDocument.Load(image).Spots);
    }

    [Fact]
    public void ResolveBaseCanvas_PrefersACanvasDirectlyOnBaseImg()
    {
        var direct = new WzCanvasProperty("BaseImg");
        Assert.Same(direct, WorldMapDocument.ResolveBaseCanvas(direct));
    }

    [Fact]
    public void BaseImgWithoutOrigin_FallsBackToZero()
    {
        WzImage image = MakeWorldMapImage("WorldMap000.img", null, baseOrigin: null, ("0", 1, 2, 0, null));

        WorldMapDocument document = WorldMapDocument.Load(image);

        Assert.NotNull(document.BaseCanvas);
        Assert.Equal(new PointF(0f, 0f), document.BaseOrigin);
    }

    // ---- coordinates ---------------------------------------------------------------------------

    [Fact]
    public void WorldToCanvas_OffsetsBySpotByTheBaseOrigin()
    {
        (double x, double y) = WorldMapCoordinateConverter.WorldToCanvas(new PointF(320f, 235f), 138, -101);

        Assert.Equal(458.0, x);
        Assert.Equal(134.0, y);
    }

    [Fact]
    public void CanvasToWorld_IsTheInverse()
    {
        (int x, int y) = WorldMapCoordinateConverter.CanvasToWorld(new PointF(320f, 235f), 458.0, 134.0);

        Assert.Equal(138, x);
        Assert.Equal(-101, y);
    }

    [Fact]
    public void CanvasToWorld_RoundsToInt_BecauseSpotIsStoredAsIntXY()
    {
        (int x, int y) = WorldMapCoordinateConverter.CanvasToWorld(new PointF(0f, 0f), 10.6, -3.2);

        Assert.Equal(11, x);
        Assert.Equal(-3, y);
    }

    [Fact]
    public void WithoutOrigin_CoordinatesPassThroughUnchanged()
    {
        (double x, double y) = WorldMapCoordinateConverter.WorldToCanvas(new PointF(0f, 0f), 48, 62);

        Assert.Equal(48.0, x);
        Assert.Equal(62.0, y);
    }

    // ---- mutations -----------------------------------------------------------------------------

    [Fact]
    public void MovingASpot_WritesTheVectorAndDirtiesTheImage()
    {
        WzImage image = MakeWorldMapImage("WorldMap000.img", null, new PointF(0f, 0f), ("0", 10, 20, 0, null));
        WorldMapSpot spot = Assert.Single(WorldMapDocument.Load(image).Spots);
        Assert.False(image.Changed);

        // What the editor's ApplySpotPosition performs on drag/entry commit.
        spot.Spot.X.Value = 30;
        spot.Spot.Y.Value = 40;
        spot.Spot.ParentImage.Changed = true;

        Assert.Equal(30, ((WzVectorProperty)spot.Entry["spot"]).X.Value);
        Assert.Equal(40, ((WzVectorProperty)spot.Entry["spot"]).Y.Value);
        Assert.True(image.Changed);
    }

    [Fact]
    public void EditingType_WritesTheIntProperty()
    {
        WzImage image = MakeWorldMapImage("WorldMap000.img", null, new PointF(0f, 0f), ("0", 1, 2, 0, null));
        WorldMapSpot spot = Assert.Single(WorldMapDocument.Load(image).Spots);

        spot.Type.Value = 2;
        spot.Type.ParentImage.Changed = true;

        Assert.Equal(2, ((WzIntProperty)spot.Entry["type"]).Value);
        Assert.True(image.Changed);
    }

    [Fact]
    public void EditingMapNo_WritesTheIntProperty()
    {
        WzImage image = MakeWorldMapImage("WorldMap000.img", null, new PointF(0f, 0f),
            ("0", 1, 2, 0, new[] { 240010300, 240010301 }));
        WorldMapSpot spot = Assert.Single(WorldMapDocument.Load(image).Spots);

        spot.MapNo[0].Value = 999;
        spot.MapNo[0].ParentImage.Changed = true;

        Assert.Equal(999, ((WzIntProperty)((WzSubProperty)spot.Entry["mapNo"])["0"]).Value);
        Assert.Equal(240010301, ((WzIntProperty)((WzSubProperty)spot.Entry["mapNo"])["1"]).Value);
        Assert.True(image.Changed);
    }

    [Fact]
    public void JustReadingTheDocument_ChangesNothing()
    {
        // Loading is what pan / zoom / selecting a spot amount to at the data layer: no writes,
        // and crucially no Changed flag - browsing a world map must not dirty the WZ.
        WzImage image = MakeWorldMapImage("WorldMap000.img", "WorldMap", new PointF(320f, 235f),
            ("0", 138, -101, 1, new[] { 250000000 }));

        WorldMapDocument document = WorldMapDocument.Load(image);
        WorldMapSpot spot = Assert.Single(document.Spots);
        _ = WorldMapCoordinateConverter.WorldToCanvas(document.BaseOrigin, spot.SpotX, spot.SpotY);
        _ = document.CollectMapNumbers();

        Assert.False(image.Changed);
        Assert.Equal(138, spot.SpotX);
        Assert.Equal(-101, spot.SpotY);
        Assert.Equal(1, spot.Type.Value);
    }

    // ---- navigation ----------------------------------------------------------------------------

    [Theory]
    [InlineData("WorldMap", "WorldMap.img")]
    [InlineData("WorldMap050.img", "WorldMap050.img")]
    [InlineData("  WorldMap010  ", "WorldMap010.img")]
    public void NormalizeImageName_AddsTheExtensionOnlyWhenMissing(string parentMap, string expected)
    {
        Assert.Equal(expected, WorldMapNavigation.NormalizeImageName(parentMap));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeImageName_WithoutAParent_ReturnsNull(string parentMap)
    {
        Assert.Null(WorldMapNavigation.NormalizeImageName(parentMap));
    }

    [Fact]
    public void ForwardResolver_PicksTheCandidateSharingTheMostMapNumbers()
    {
        var candidates = new Dictionary<string, HashSet<int>>
        {
            ["WorldMap050.img"] = new HashSet<int> { 250000000, 250000001, 250000002 },
            ["WorldMap060.img"] = new HashSet<int> { 300000000 }
        };

        string target = WorldMapNavigation.ResolveForwardTarget(
            new[] { 250000000, 250000001 }, candidates, "WorldMap.img", out bool ambiguous);

        Assert.Equal("WorldMap050.img", target);
        Assert.False(ambiguous);
    }

    [Fact]
    public void ForwardResolver_TieIsReportedAmbiguousRatherThanGuessed()
    {
        var candidates = new Dictionary<string, HashSet<int>>
        {
            ["WorldMap050.img"] = new HashSet<int> { 250000000 },
            ["WorldMap060.img"] = new HashSet<int> { 250000001 }
        };

        string target = WorldMapNavigation.ResolveForwardTarget(
            new[] { 250000000, 250000001 }, candidates, "WorldMap.img", out bool ambiguous);

        Assert.Null(target);
        Assert.True(ambiguous);
    }

    [Fact]
    public void ForwardResolver_NoOverlap_ReturnsNullWithoutClaimingAmbiguity()
    {
        var candidates = new Dictionary<string, HashSet<int>>
        {
            ["WorldMap050.img"] = new HashSet<int> { 111 }
        };

        string target = WorldMapNavigation.ResolveForwardTarget(
            new[] { 250000000 }, candidates, "WorldMap.img", out bool ambiguous);

        Assert.Null(target);
        Assert.False(ambiguous);
    }

    [Fact]
    public void ForwardResolver_NeverNavigatesToTheImageAlreadyOpen()
    {
        var candidates = new Dictionary<string, HashSet<int>>
        {
            ["WorldMap050.img"] = new HashSet<int> { 250000000 }
        };

        string target = WorldMapNavigation.ResolveForwardTarget(
            new[] { 250000000 }, candidates, "WorldMap050.img", out bool ambiguous);

        Assert.Null(target);
        Assert.False(ambiguous);
    }

    [Fact]
    public void CollectMapNumbers_GathersEveryMapNoInTheImage()
    {
        WzImage image = MakeWorldMapImage("WorldMap050.img", null, new PointF(0f, 0f),
            ("0", 1, 2, 0, new[] { 250000000, 250000001 }),
            ("1", 3, 4, 0, new[] { 250000002 }));

        Assert.Equal(new[] { 250000000, 250000001, 250000002 },
            WorldMapDocument.Load(image).CollectMapNumbers().OrderBy(n => n).ToArray());
    }

    // ---- group move ----------------------------------------------------------------------------

    [Fact]
    public void GroupMove_ShiftsEveryItemByTheSameDelta_KeepingTheirRelativeLayout()
    {
        WzImage image = MakeWorldMapImage("WorldMap000.img", null, new PointF(0f, 0f),
            ("0", 10, 20, 0, null), ("1", 30, 40, 0, null));
        IReadOnlyList<WorldMapSpot> spots = WorldMapDocument.Load(image).Spots;

        var start = new Dictionary<IWorldMapMovable, (int X, int Y)>
        {
            [spots[0]] = (10, 20),
            [spots[1]] = (30, 40)
        };

        Dictionary<IWorldMapMovable, (int X, int Y)> moved = WorldMapGroupMove.Offset(start, 5, -10);

        Assert.Equal((15, 10), moved[spots[0]]);
        Assert.Equal((35, 30), moved[spots[1]]);
    }

    [Fact]
    public void GroupMove_CommittingWritesOnlyTheItemsThatActuallyMoved()
    {
        WzImage image = MakeWorldMapImage("WorldMap000.img", null, new PointF(0f, 0f),
            ("0", 10, 20, 0, null), ("1", 30, 40, 0, null));
        IReadOnlyList<WorldMapSpot> spots = WorldMapDocument.Load(image).Spots;
        Assert.False(image.Changed);

        // What CommitDrag does: skip anything whose position is unchanged.
        var target = new Dictionary<WorldMapSpot, (int X, int Y)>
        {
            [spots[0]] = (15, 10),
            [spots[1]] = (30, 40) // unchanged
        };
        var written = new List<WzVectorProperty>();
        foreach (KeyValuePair<WorldMapSpot, (int X, int Y)> entry in target)
        {
            if (entry.Key.SpotX == entry.Value.X && entry.Key.SpotY == entry.Value.Y)
                continue;
            entry.Key.Spot.X.Value = entry.Value.X;
            entry.Key.Spot.Y.Value = entry.Value.Y;
            entry.Key.Spot.ParentImage.Changed = true;
            written.Add(entry.Key.Spot);
        }

        Assert.Same(spots[0].Spot, Assert.Single(written));
        Assert.Equal(15, spots[0].SpotX);
        Assert.Equal(30, spots[1].SpotX); // untouched
        Assert.True(image.Changed);
    }

    [Fact]
    public void SelectionAloneNeverWritesAnything()
    {
        WzImage image = MakeWorldMapImage("WorldMap000.img", null, new PointF(0f, 0f),
            ("0", 10, 20, 0, null), ("1", 30, 40, 0, null));
        WorldMapDocument document = WorldMapDocument.Load(image);

        // Building a multi-selection touches only the panel's own set.
        var selection = new HashSet<IWorldMapMovable>();
        foreach (WorldMapSpot spot in document.Spots)
            selection.Add(spot);

        Assert.Equal(2, selection.Count);
        Assert.False(image.Changed);
        Assert.Equal(10, document.Spots[0].SpotX);
        Assert.Equal(30, document.Spots[1].SpotX);
    }

    // ---- MapLink -------------------------------------------------------------------------------

    /// <summary>
    /// Builds MapLink using the schema this repository's own codec reads
    /// (HaCreator\WorldMap\WorldMapCodec.cs): toolTip, spot, link\linkMap.
    /// </summary>
    private static void AddMapLink(WzImage image, string key, int? x, int? y, string toolTip, string linkMap)
    {
        var links = image["MapLink"] as WzSubProperty;
        if (links == null)
        {
            links = new WzSubProperty("MapLink");
            image.AddProperty(links);
        }

        var entry = new WzSubProperty(key);
        if (x.HasValue && y.HasValue)
            entry.AddProperty(new WzVectorProperty("spot", new WzIntProperty("x", x.Value), new WzIntProperty("y", y.Value)));
        if (toolTip != null)
            entry.AddProperty(new WzStringProperty("toolTip", toolTip));
        if (linkMap != null)
        {
            var nested = new WzSubProperty("link");
            nested.AddProperty(new WzStringProperty("linkMap", linkMap));
            entry.AddProperty(nested);
        }
        links.AddProperty(entry);
        image.Changed = false;
    }

    [Fact]
    public void Load_ReadsMapLinkPositionToolTipAndLinkMap()
    {
        WzImage image = MakeWorldMapImage("WorldMap010.img", "WorldMap", new PointF(0f, 0f), ("0", 1, 2, 0, null));
        AddMapLink(image, "5", 100, -50, "victoria", "WorldMap020");

        WorldMapLink link = Assert.Single(WorldMapDocument.Load(image).Links);

        Assert.Equal("5", link.EntryName);
        Assert.Equal(100, link.SpotX);
        Assert.Equal(-50, link.SpotY);
        Assert.Equal("victoria", link.ToolTip);
        Assert.Equal("WorldMap020", link.LinkMap);
        // linkMap normalizes to a navigable image name.
        Assert.Equal("WorldMap020.img", WorldMapNavigation.NormalizeImageName(link.LinkMap));
    }

    [Fact]
    public void Load_MapLinkWithoutSpot_IsSkippedRatherThanGivenAGuessedPosition()
    {
        WzImage image = MakeWorldMapImage("WorldMap010.img", null, new PointF(0f, 0f), ("0", 1, 2, 0, null));
        AddMapLink(image, "5", x: null, y: null, toolTip: "no position", linkMap: "WorldMap020");

        Assert.Empty(WorldMapDocument.Load(image).Links);
    }

    [Fact]
    public void Load_MapLinkWithoutOptionalFields_ReadsNullsNotInventedValues()
    {
        WzImage image = MakeWorldMapImage("WorldMap010.img", null, new PointF(0f, 0f), ("0", 1, 2, 0, null));
        AddMapLink(image, "0", 10, 20, toolTip: null, linkMap: null);

        WorldMapLink link = Assert.Single(WorldMapDocument.Load(image).Links);

        Assert.Null(link.ToolTip);
        Assert.Null(link.LinkMap);
        Assert.Null(link.Entry["toolTip"]);
        Assert.Null(link.Entry["link"]);
    }

    [Fact]
    public void Load_WithoutMapLink_YieldsNoLinks()
    {
        WzImage image = MakeWorldMapImage("WorldMap000.img", null, new PointF(0f, 0f), ("0", 1, 2, 0, null));
        Assert.Empty(WorldMapDocument.Load(image).Links);
    }

    [Fact]
    public void MovingAMapLink_WritesItsSpotAndDirtiesTheImage()
    {
        WzImage image = MakeWorldMapImage("WorldMap010.img", null, new PointF(0f, 0f), ("0", 1, 2, 0, null));
        AddMapLink(image, "5", 100, -50, null, null);
        WorldMapLink link = Assert.Single(WorldMapDocument.Load(image).Links);
        Assert.False(image.Changed);

        link.Position.X.Value = 130;
        link.Position.Y.Value = -20;
        link.Position.ParentImage.Changed = true;

        Assert.Equal(130, ((WzVectorProperty)link.Entry["spot"]).X.Value);
        Assert.Equal(-20, ((WzVectorProperty)link.Entry["spot"]).Y.Value);
        Assert.True(image.Changed);
    }

    [Fact]
    public void SpotsAndLinksShareTheMovableContract_SoAMixedSelectionDragsAsOneGroup()
    {
        WzImage image = MakeWorldMapImage("WorldMap010.img", null, new PointF(0f, 0f), ("0", 10, 20, 0, null));
        AddMapLink(image, "5", 100, -50, null, null);
        WorldMapDocument document = WorldMapDocument.Load(image);

        var start = new Dictionary<IWorldMapMovable, (int X, int Y)>
        {
            [document.Spots[0]] = (10, 20),
            [document.Links[0]] = (100, -50)
        };

        Dictionary<IWorldMapMovable, (int X, int Y)> moved = WorldMapGroupMove.Offset(start, -5, 5);

        Assert.Equal((5, 25), moved[document.Spots[0]]);
        Assert.Equal((95, -45), moved[document.Links[0]]);
    }

    // ---- mapNo structure -------------------------------------------------------------------------

    [Fact]
    public void NextIndexName_AppendsAfterTheHighestExistingIndex()
    {
        Assert.Equal("0", WorldMapMapNoIndexer.NextIndexName(new string[0]));
        Assert.Equal("1", WorldMapMapNoIndexer.NextIndexName(new[] { "0" }));
        Assert.Equal("3", WorldMapMapNoIndexer.NextIndexName(new[] { "0", "1", "2" }));
        // A non-numeric sibling must not derail the count.
        Assert.Equal("2", WorldMapMapNoIndexer.NextIndexName(new[] { "0", "junk", "1" }));
    }

    [Fact]
    public void Renumber_ClosesTheGapLeftByADeletion()
    {
        // 0=A, 1=B, 2=C with 1 deleted leaves 0, 2 -> the 2 must become 1.
        Dictionary<string, string> renames = WorldMapMapNoIndexer.Renumber(new[] { "0", "2" });

        Assert.Equal("1", renames["2"]);
        Assert.False(renames.ContainsKey("0")); // already correct, so not needlessly renamed
    }

    [Fact]
    public void Renumber_AlreadyContiguous_RenamesNothing()
    {
        Assert.Empty(WorldMapMapNoIndexer.Renumber(new[] { "0", "1", "2" }));
    }

    [Fact]
    public void DeletingAMapNo_ThenRenumbering_LeavesContiguousNamesAndKeepsValueOrder()
    {
        WzImage image = MakeWorldMapImage("WorldMap000.img", null, new PointF(0f, 0f),
            ("0", 1, 2, 0, new[] { 111, 222, 333 }));
        WorldMapSpot spot = Assert.Single(WorldMapDocument.Load(image).Spots);
        var container = (WzSubProperty)spot.Entry["mapNo"];

        // Delete index 1 (value 222), the way the × button does.
        container.RemoveProperty(container["1"]);

        Dictionary<string, string> renames = WorldMapMapNoIndexer.Renumber(
            container.WzProperties.Select(p => p.Name).ToList());
        foreach (WzImageProperty property in container.WzProperties.ToList())
        {
            if (renames.TryGetValue(property.Name, out string newName))
                property.Name = newName;
        }

        Assert.Equal(new[] { "0", "1" }, container.WzProperties.Select(p => p.Name).ToArray());
        Assert.Equal(111, ((WzIntProperty)container["0"]).Value);
        Assert.Equal(333, ((WzIntProperty)container["1"]).Value);
    }

    [Fact]
    public void ASpotWithNoMapNo_GetsNoContainerUntilOneIsExplicitlyAdded()
    {
        WzImage image = MakeWorldMapImage("WorldMap000.img", null, new PointF(0f, 0f), ("0", 1, 2, 0, null));
        WorldMapSpot spot = Assert.Single(WorldMapDocument.Load(image).Spots);

        // Merely loading and inspecting must not create it.
        Assert.Null(spot.Entry["mapNo"]);
        Assert.Empty(spot.MapNo);
        Assert.False(image.Changed);

        // The explicit add is what creates it, with a first entry defaulting to 0.
        var container = new WzSubProperty("mapNo");
        spot.Entry.AddProperty(container);
        string name = WorldMapMapNoIndexer.NextIndexName(container.WzProperties.Select(p => p.Name));
        container.AddProperty(new WzIntProperty(name, 0));
        container.ParentImage.Changed = true;

        Assert.Equal("0", name);
        WorldMapSpot reloaded = Assert.Single(WorldMapDocument.Load(image).Spots);
        Assert.Equal(0, Assert.Single(reloaded.MapNo).Value);
        Assert.True(image.Changed);
    }

    [Fact]
    public void AddingASecondMapNo_DefaultsToZeroAtTheNextIndex()
    {
        WzImage image = MakeWorldMapImage("WorldMap000.img", null, new PointF(0f, 0f),
            ("0", 1, 2, 0, new[] { 240010300 }));
        WorldMapSpot spot = Assert.Single(WorldMapDocument.Load(image).Spots);
        var container = (WzSubProperty)spot.Entry["mapNo"];

        string name = WorldMapMapNoIndexer.NextIndexName(container.WzProperties.Select(p => p.Name));
        container.AddProperty(new WzIntProperty(name, 0));

        Assert.Equal("1", name);
        WorldMapSpot reloaded = Assert.Single(WorldMapDocument.Load(image).Spots);
        Assert.Equal(new[] { 240010300, 0 }, reloaded.MapNo.Select(m => m.Value).ToArray());
    }

    // ---- bounds --------------------------------------------------------------------------------

    [Fact]
    public void MarkersOutsideTheArtwork_KeepTheirCoordinatesUnclamped()
    {
        // A spot legitimately sitting far outside BaseImg must not be pulled back into it - the
        // canvas grows instead, which is a rendering concern only.
        WzImage image = MakeWorldMapImage("WorldMap000.img", null, new PointF(320f, 235f),
            ("0", -5000, -5000, 0, null));

        WorldMapSpot spot = Assert.Single(WorldMapDocument.Load(image).Spots);

        Assert.Equal(-5000, spot.SpotX);
        Assert.Equal(-5000, spot.SpotY);
        Assert.False(image.Changed);
    }

    // ---- detection -----------------------------------------------------------------------------

    [Fact]
    public void Detector_AcceptsAWorldMapImageUnderAWorldMapDirectory()
    {
        var file = new WzFile(1, WzMapleVersion.BMS) { Name = "Map.wz" };
        var root = new WzDirectory(file.Name, file);
        var worldMapDirectory = new WzDirectory("WorldMap");
        root.AddDirectory(worldMapDirectory);
        var image = new WzImage("WorldMap050.img");
        worldMapDirectory.AddImage(image);

        Assert.True(WorldMapDetector.IsWorldMapImage(image));
        Assert.Same(worldMapDirectory, WorldMapDetector.FindWorldMapContainer(image));
    }

    [Fact]
    public void Detector_AcceptsASplitWorldMapWzWhereImagesSitAtTheRoot()
    {
        // WorldMap_000.wz holds WorldMap*.img directly, with no WorldMap directory in between -
        // the layout that left the editor blank when only the directory form was accepted.
        var file = new WzFile(1, WzMapleVersion.BMS) { Name = "WorldMap_000.wz" };
        var root = new WzDirectory(file.Name, file);
        var image = new WzImage("WorldMap020.img");
        root.AddImage(image);

        Assert.True(WorldMapDetector.IsWorldMapImage(image));
        Assert.Same(root, WorldMapDetector.FindWorldMapContainer(image));
    }

    [Fact]
    public void Detector_RejectsAWorldMapNamedImageSomewhereElse()
    {
        var file = new WzFile(1, WzMapleVersion.BMS) { Name = "Map.wz" };
        var root = new WzDirectory(file.Name, file);
        var otherDirectory = new WzDirectory("Obj");
        root.AddDirectory(otherDirectory);
        var image = new WzImage("WorldMap123.img");
        otherDirectory.AddImage(image);

        Assert.False(WorldMapDetector.IsWorldMapImage(image));
    }

    [Fact]
    public void Detector_RejectsNonImagesAndUnrelatedNames()
    {
        Assert.False(WorldMapDetector.IsWorldMapImage(null));
        Assert.False(WorldMapDetector.IsWorldMapImage(new WzImage("Map001.img")));
        Assert.False(WorldMapDetector.IsWorldMapImage(new WzSubProperty("WorldMap050.img")));
    }
}
