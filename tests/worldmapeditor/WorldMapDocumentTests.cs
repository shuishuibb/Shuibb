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
        Assert.Same(worldMapDirectory, WorldMapDetector.FindWorldMapDirectory(image));
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
