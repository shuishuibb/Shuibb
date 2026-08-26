using System.Collections.Generic;
using HaRepacker;
using HaRepacker.GUI.NpcInfo;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using Xunit;
using Assert = Xunit.Assert;

namespace NpcInfo.Tests;

/// <summary>
/// Targeted regression for the "NPC 詳細資訊" feature: NpcInfoBuilder's identification/traversal
/// logic and NpcInfoResult's clipboard formatting. No GUI is driven - these build in-memory
/// WzFile/WzImage fixtures directly (same style MapleLib's own WzSerializerAdversarialTests and
/// tests/mapobjectinfo use) and assert on the resulting NpcInfoResult.
/// </summary>
public sealed class NpcInfoBuilderTests
{
    // ---- fixture builders -------------------------------------------------

    private static WzFile MakeWzFile(string name)
    {
        return new WzFile(1, WzMapleVersion.BMS) { Name = name };
    }

    private static WzImage AddNpcImage(WzFile npcWzFile, string paddedId)
    {
        WzImage image = new WzImage(paddedId + ".img");
        // WzFile(short, WzMapleVersion) - used by MakeWzFile, matching the rest of this test
        // project's fixture style - leaves WzDirectory's own WzFileParent link unset (that
        // linkage is only wired up by the internal 5-arg WzDirectory constructor real parsing
        // uses). Building the root directory through the public 2-arg constructor instead,
        // named identically to the WzFile itself (exactly what real parsing does - see
        // WzFile.ParseMainWzDirectory: "new WzDirectory(reader, this.name, ...)"), reproduces
        // the same WzFileParent/path-walk shape a real loaded WzFile would have.
        WzDirectory rootDirectory = new WzDirectory(npcWzFile.Name, npcWzFile);
        rootDirectory.AddImage(image);
        return image;
    }

    private static WzSubProperty AddStringNpcEntry(WzFile stringWzFile, string unpaddedId)
    {
        WzImage npcStringImg = (WzImage)stringWzFile.WzDirectory["Npc.img"];
        if (npcStringImg == null)
        {
            npcStringImg = new WzImage("Npc.img");
            stringWzFile.WzDirectory.AddImage(npcStringImg);
        }

        WzSubProperty entry = new WzSubProperty(unpaddedId);
        npcStringImg.AddProperty(entry);
        return entry;
    }

    private static void AddCanvasFrames(WzSubProperty container, params string[] frameNames)
    {
        foreach (string frameName in frameNames)
            container.AddProperty(new WzCanvasProperty(frameName));
    }

    private static WzNode NodeFor(WzImage image) => new WzNode(image);

    // ---- IsNpcImageNode / TryGetNpcImage -----------------------------------

    [Fact]
    public void TryGetNpcImage_ValidNpcWz_ResolvesIdAndDoesNotRequireLeadingZerosInResult()
    {
        WzFile npcWzFile = MakeWzFile("Npc.wz");
        WzImage image = AddNpcImage(npcWzFile, "0002000");

        bool ok = NpcInfoBuilder.TryGetNpcImage(NodeFor(image), out WzImage resolved, out string npcId);

        Assert.True(ok);
        Assert.Same(image, resolved);
        Assert.Equal("0002000", npcId);
    }

    [Theory]
    [InlineData("Npc.wz")]
    [InlineData("Npc_000.wz")]
    [InlineData("Npc_001.wz")]
    [InlineData("NPC.WZ")]
    public void IsNpcImageNode_AcceptsStandaloneNpcWzNamingVariants(string wzFileName)
    {
        WzFile npcWzFile = MakeWzFile(wzFileName);
        WzImage image = AddNpcImage(npcWzFile, "9000000");

        Assert.True(NpcInfoBuilder.IsNpcImageNode(NodeFor(image)));
    }

    [Theory]
    [InlineData("Mob.wz")]
    [InlineData("Map0_000.wz")]
    [InlineData("Reactor.wz")]
    public void IsNpcImageNode_RejectsDigitsDotImgUnderNonNpcWzFile(string wzFileName)
    {
        // Same "digits.img" naming Npc/Mob/Map/Reactor all share - only the owning WZ file
        // decides this, not the name shape alone.
        WzFile otherWzFile = MakeWzFile(wzFileName);
        WzImage image = AddNpcImage(otherWzFile, "9000000");

        Assert.False(NpcInfoBuilder.IsNpcImageNode(NodeFor(image)));
    }

    [Fact]
    public void IsNpcImageNode_RejectsNonNumericOrNonImgNames()
    {
        WzFile npcWzFile = MakeWzFile("Npc.wz");

        WzImage notAnImg = new WzImage("info.xml");
        npcWzFile.WzDirectory.AddImage(notAnImg);
        Assert.False(NpcInfoBuilder.IsNpcImageNode(NodeFor(notAnImg)));

        WzImage notNumeric = new WzImage("readme.img");
        npcWzFile.WzDirectory.AddImage(notNumeric);
        Assert.False(NpcInfoBuilder.IsNpcImageNode(NodeFor(notNumeric)));
    }

    [Fact]
    public void IsNpcImageNode_SafelyRejectsNonImageAndNullNodes()
    {
        Assert.False(NpcInfoBuilder.IsNpcImageNode(null));

        WzDirectory dir = new WzDirectory("9000000");
        Assert.False(NpcInfoBuilder.IsNpcImageNode(new WzNode(dir)));

        WzNode looseNode = new WzNode(new WzImage("9000000.img"));
        looseNode.Tag = null;
        Assert.False(NpcInfoBuilder.IsNpcImageNode(looseNode));
    }

    // ---- NPC ID -------------------------------------------------------------

    [Fact]
    public void Build_NpcId_MatchesImageNameWithoutExtension()
    {
        WzFile npcWzFile = MakeWzFile("Npc.wz");
        WzImage image = AddNpcImage(npcWzFile, "9000000");

        NpcInfoResult result = NpcInfoBuilder.Build(image, "9000000", new List<WzFile> { npcWzFile });

        Assert.Equal("9000000", result.NpcId);
    }

    // ---- String.wz name/extras -----------------------------------------------

    [Fact]
    public void Build_StringNpcName_ReadCorrectlyByUnpaddedId()
    {
        WzFile npcWzFile = MakeWzFile("Npc.wz");
        WzImage image = AddNpcImage(npcWzFile, "0002000");

        WzFile stringWzFile = MakeWzFile("String.wz");
        WzSubProperty entry = AddStringNpcEntry(stringWzFile, "2000"); // unpadded, matching real data
        entry.AddProperty(new WzStringProperty("name", "羅傑"));
        entry.AddProperty(new WzStringProperty("func", "some_func"));

        NpcInfoResult result = NpcInfoBuilder.Build(image, "0002000", new List<WzFile> { npcWzFile, stringWzFile });

        Assert.Equal("羅傑", result.NpcName);
        Assert.Contains("func: some_func", result.StringExtras);
        Assert.DoesNotContain(result.StringExtras, v => v.StartsWith("name:"));
    }

    [Fact]
    public void Build_StringWzNotLoaded_NameAndExtrasAreEmptyWithoutThrowing()
    {
        WzFile npcWzFile = MakeWzFile("Npc.wz");
        WzImage image = AddNpcImage(npcWzFile, "9000000");

        // Only Npc.wz is "loaded" - no String.wz among the loaded files at all.
        NpcInfoResult result = NpcInfoBuilder.Build(image, "9000000", new List<WzFile> { npcWzFile });

        Assert.Null(result.NpcName);
        Assert.Empty(result.StringExtras);
        Assert.Equal("沒有值", NpcInfoResult.DisplayScalar(result.NpcName));
    }

    [Fact]
    public void Build_NullLoadedWzFiles_IsSafe()
    {
        WzFile npcWzFile = MakeWzFile("Npc.wz");
        WzImage image = AddNpcImage(npcWzFile, "9000000");

        NpcInfoResult result = NpcInfoBuilder.Build(image, "9000000", null);

        Assert.Null(result.NpcName);
        Assert.Empty(result.StringExtras);
    }

    [Fact]
    public void Build_NpcIdNotPresentInStringWz_NameAndExtrasAreEmptyWithoutThrowing()
    {
        WzFile npcWzFile = MakeWzFile("Npc.wz");
        WzImage image = AddNpcImage(npcWzFile, "9000000");

        WzFile stringWzFile = MakeWzFile("String.wz");
        AddStringNpcEntry(stringWzFile, "1234567").AddProperty(new WzStringProperty("name", "SomeoneElse"));

        NpcInfoResult result = NpcInfoBuilder.Build(image, "9000000", new List<WzFile> { npcWzFile, stringWzFile });

        Assert.Null(result.NpcName);
        Assert.Empty(result.StringExtras);
    }

    // ---- animation detection ---------------------------------------------------

    [Fact]
    public void Build_Animations_UsesAnimationBuilderDetectionAndExcludesNonAnimationNodes()
    {
        WzFile npcWzFile = MakeWzFile("Npc.wz");
        WzImage image = AddNpcImage(npcWzFile, "9000000");

        // "info" - a plain scalar container, not a sequence of canvas frames.
        WzSubProperty info = new WzSubProperty("info");
        info.AddProperty(new WzStringProperty("link", "9000001"));
        image.AddProperty(info);

        // "stand" - single-frame, does not satisfy AnimationBuilder's 2+-frame rule (confirmed
        // against this project's own real Npc.wz data).
        WzSubProperty stand = new WzSubProperty("stand");
        AddCanvasFrames(stand, "0");
        image.AddProperty(stand);

        // "wink" - a genuine 2-frame animation.
        WzSubProperty wink = new WzSubProperty("wink");
        AddCanvasFrames(wink, "0", "1");
        image.AddProperty(wink);

        NpcInfoResult result = NpcInfoBuilder.Build(image, "9000000", new List<WzFile> { npcWzFile });

        Assert.Equal(new[] { "wink" }, result.Animations);
    }

    [Fact]
    public void Build_AnimationNames_AreSortedNotInsertionOrder()
    {
        // WzImage.AddProperty rejects a second sibling with an already-used name outright, so a
        // real duplicate-name fixture can't be constructed here; SortedSet<string> in
        // NpcInfoBuilder still makes dedup structurally guaranteed regardless. This covers the
        // part that *is* observable: output order is sorted, not insertion order.
        WzFile npcWzFile = MakeWzFile("Npc.wz");
        WzImage image = AddNpcImage(npcWzFile, "9000000");

        WzSubProperty zzz = new WzSubProperty("zzz_anim");
        AddCanvasFrames(zzz, "0", "1");
        image.AddProperty(zzz);

        WzSubProperty aaa = new WzSubProperty("aaa_anim");
        AddCanvasFrames(aaa, "0", "1");
        image.AddProperty(aaa);

        WzSubProperty mmm = new WzSubProperty("mmm_anim");
        AddCanvasFrames(mmm, "0", "1");
        image.AddProperty(mmm);

        NpcInfoResult result = NpcInfoBuilder.Build(image, "9000000", new List<WzFile> { npcWzFile });

        Assert.Equal(new[] { "aaa_anim", "mmm_anim", "zzz_anim" }, result.Animations);
    }

    [Fact]
    public void Build_EmptyNpcImage_DoesNotCrashAndProducesEmptyResults()
    {
        WzFile npcWzFile = MakeWzFile("Npc.wz");
        WzImage image = AddNpcImage(npcWzFile, "9000000");

        NpcInfoResult result = NpcInfoBuilder.Build(image, "9000000", new List<WzFile> { npcWzFile });

        Assert.Equal("9000000", result.NpcId);
        Assert.Null(result.NpcName);
        Assert.Empty(result.StringExtras);
        Assert.Empty(result.Animations);
        Assert.Equal("Npc.wz/9000000.img", result.WzPath);
    }

    // ---- WZ path -------------------------------------------------------------

    [Fact]
    public void Build_WzPath_JoinsFileAndImageNameWithSlash()
    {
        WzFile npcWzFile = MakeWzFile("Npc_000.wz");
        WzImage image = AddNpcImage(npcWzFile, "0002000");

        NpcInfoResult result = NpcInfoBuilder.Build(image, "0002000", new List<WzFile> { npcWzFile });

        Assert.Equal("Npc_000.wz/0002000.img", result.WzPath);
    }

    // ---- read-only guarantee ---------------------------------------------------

    [Fact]
    public void Build_DoesNotMarkTheNpcImageChanged()
    {
        WzFile npcWzFile = MakeWzFile("Npc.wz");
        WzImage image = AddNpcImage(npcWzFile, "9000000");
        WzSubProperty stand = new WzSubProperty("stand");
        AddCanvasFrames(stand, "0", "1");
        image.AddProperty(stand);

        WzFile stringWzFile = MakeWzFile("String.wz");
        AddStringNpcEntry(stringWzFile, "9000000").AddProperty(new WzStringProperty("name", "Test"));

        // WzImage.AddProperty marks the image Changed as an intrinsic part of constructing this
        // fixture (a real loaded-from-disk image's pre-existing properties never go through
        // AddProperty, so they'd never set this) - reset it here so the assertion below is
        // actually about what Build() does, not about how the test data was assembled.
        image.Changed = false;

        NpcInfoBuilder.Build(image, "9000000", new List<WzFile> { npcWzFile, stringWzFile });

        Assert.False(image.Changed);
    }

    // ---- clipboard formatting ---------------------------------------------------

    [Fact]
    public void ToClipboardText_IncludesAllFiveHeadersInOrder()
    {
        NpcInfoResult result = new NpcInfoResult(
            "9000000",
            "羅傑",
            new[] { "func: some_func" },
            "Npc.wz/9000000.img",
            new[] { "wink" });

        string text = result.ToClipboardText();

        int idIdx = text.IndexOf("NPC ID");
        int nameIdx = text.IndexOf("NPC 名稱");
        int extrasIdx = text.IndexOf("NPC String 額外資訊");
        int pathIdx = text.IndexOf("NPC WZ 路徑");
        int animIdx = text.IndexOf("動作 / Animation 名稱");

        Assert.True(idIdx >= 0 && idIdx < nameIdx);
        Assert.True(nameIdx < extrasIdx);
        Assert.True(extrasIdx < pathIdx);
        Assert.True(pathIdx < animIdx);

        Assert.Contains("9000000", text);
        Assert.Contains("羅傑", text);
        Assert.Contains("wink", text);
    }

    [Fact]
    public void ToClipboardText_EmptyFields_ShowNoValuesPlaceholder()
    {
        NpcInfoResult result = new NpcInfoResult(
            "9000000",
            null,
            new string[0],
            "Npc.wz/9000000.img",
            new string[0]);

        string text = result.ToClipboardText();

        Assert.Contains("NPC 名稱\r\n沒有值", text);
        Assert.Contains("動作 / Animation 名稱\r\n沒有值", text);
    }
}
