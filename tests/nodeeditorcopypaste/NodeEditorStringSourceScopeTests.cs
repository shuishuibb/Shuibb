using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using MapleLib;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using SkillPreview;
using Xunit;
using Assert = Xunit.Assert;

namespace NodeEditorCopyPasteTests;

/// <summary>
/// Targeted regression for "editing an item from one Data source read - and would have written -
/// the other source's String.wz" (audit P0-1).
///
/// Two layers:
///   * pure path-rule tests against NodeEditorStringSourceScope,
///   * an end-to-end test that builds two complete miniature Data sources as real .wz files under
///     %TEMP%\ShuibbFixValidation, opens both through one real WzFileManager (the app has exactly
///     one), and asserts which file the panel links each item to, both load orders, and that an
///     edit through the resolved entry lands in its own source only.
///
/// SCOPE - not covered here: the STRING card's save button click itself (unchanged code), and
/// multi-tab UI. Tabs share the single WzFileManager, and the resolution depends only on the
/// selected node and that manager, so per-tab behaviour follows from these tests by construction.
/// </summary>
public sealed class NodeEditorStringSourceScopeTests
{
    // ---- pure path rules ----------------------------------------------------------------------

    private const string AItem = @"D:\3.私服檔案\技術谷4.0\Data\Item\Consume\Consume_000.wz";
    private const string AString = @"D:\3.私服檔案\技術谷4.0\Data\Lang\zh_TW\String\String_000.wz";
    private const string BItem = @"D:\Program Files (x86)\MapleStory\Data\Item\Consume\Consume_000.wz";
    private const string BString = @"D:\Program Files (x86)\MapleStory\Data\String\String_000.wz";

    [Fact]
    public void RealLayouts_EachItemPicksItsOwnString()
    {
        var candidates = new List<string> { AString, BString };

        Assert.Equal(new[] { AString }, NodeEditorStringSourceScope.PickSameSource(AItem, candidates));
        Assert.Equal(new[] { BString }, NodeEditorStringSourceScope.PickSameSource(BItem, candidates));
    }

    [Fact]
    public void OnlyTheWrongSourceOpen_LinksNothing()
    {
        // B item selected, only A's String open: sharing just the drive root is not a source.
        Assert.Empty(NodeEditorStringSourceScope.PickSameSource(BItem, new List<string> { AString }));
        Assert.Empty(NodeEditorStringSourceScope.PickSameSource(AItem, new List<string> { BString }));
    }

    [Fact]
    public void TwoLocalesOfOneSource_BothSurviveForTheLocalePreference()
    {
        string zhCn = @"D:\3.私服檔案\技術谷4.0\Data\Lang\zh_CN\String\String_000.wz";
        var picked = NodeEditorStringSourceScope.PickSameSource(AItem, new List<string> { zhCn, AString });

        // Both are the same Data root; the caller's existing zh_TW ordering chooses between them.
        Assert.Equal(2, picked.Count);
    }

    [Fact]
    public void UnknownSelectedPath_OneFamilyOpen_StillLinks()
    {
        var picked = NodeEditorStringSourceScope.PickSameSource(null,
            new List<string> { AString, @"D:\3.私服檔案\技術谷4.0\Data\Lang\zh_CN\String\String_000.wz" });
        Assert.Equal(2, picked.Count);
    }

    [Fact]
    public void UnknownSelectedPath_TwoFamiliesOpen_RefusesToGuess()
    {
        Assert.Empty(NodeEditorStringSourceScope.PickSameSource(null, new List<string> { AString, BString }));
    }

    [Fact]
    public void ForwardSlashesAndCaseDifferencesStillMatch()
    {
        var picked = NodeEditorStringSourceScope.PickSameSource(
            BItem.Replace('\\', '/').ToUpperInvariant(),
            new List<string> { BString });
        Assert.Single(picked);
    }

    [Fact]
    public void CommonAncestorTooFarAboveTheSelectedFile_IsNotASource()
    {
        // Both under one nearby parent, but the shared folder sits four directories above the
        // selected file - past the deepest real layout. Linking would risk the wrong file.
        string item = @"C:\stuff\deep\a\b\c\d\Item_000.wz";
        string str = @"C:\stuff\other\String_000.wz";
        Assert.Empty(NodeEditorStringSourceScope.PickSameSource(item, new List<string> { str }));
    }

    // ---- end-to-end: two real miniature sources, one real manager --------------------------------

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

    private static string BuildItemWz(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using var wz = new WzFile(1, WzMapleVersion.BMS) { Name = Path.GetFileName(path) };
        var img = new WzImage("0200.img");
        var item = new WzSubProperty("02000000");
        var info = new WzSubProperty("info");
        info.AddProperty(new WzIntProperty("price", 50));
        item.AddProperty(info);
        item.AddProperty(new WzIntProperty("max", 100));
        img.AddProperty(item);
        wz.WzDirectory.AddImage(img);
        wz.SaveToDisk(path, false);
        return path;
    }

    private static string BuildStringWz(string path, string potionName)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using var wz = new WzFile(1, WzMapleVersion.BMS) { Name = Path.GetFileName(path) };
        var img = new WzImage("Consume.img");
        var entry = new WzSubProperty("2000000");
        entry.AddProperty(new WzStringProperty("name", potionName));
        entry.AddProperty(new WzStringProperty("desc", potionName + " desc"));
        img.AddProperty(entry);
        wz.WzDirectory.AddImage(img);
        wz.SaveToDisk(path, false);
        return path;
    }

    /// <summary>Item entries are named 02000000; String keys drop the leading zeros.</summary>
    private static WzSubProperty ItemNode(WzFile itemWz)
        => (WzSubProperty)itemWz.WzDirectory["0200.img"]["02000000"];

    private static string StringNameValue(WzFile stringWz)
        => ((WzStringProperty)stringWz.WzDirectory["Consume.img"]["2000000"]["name"]).Value;

    [Fact]
    public void TwoSources_EachItemLinksToItsOwnString_AndEditsStayInside()
    {
        RunSta(() =>
        {
            string root = Path.Combine(Path.GetTempPath(), "ShuibbFixValidation",
                "scope_" + Guid.NewGuid().ToString("N"));
            try
            {
                // Two miniature but complete sources with the two real-world String layouts.
                string aItem = BuildItemWz(Path.Combine(root, "srcA", "Data", "Item", "Consume", "Consume_000.wz"));
                string aStr = BuildStringWz(Path.Combine(root, "srcA", "Data", "Lang", "zh_TW", "String", "String_000.wz"), "A_RED_POTION");
                string bItem = BuildItemWz(Path.Combine(root, "srcB", "Data", "Item", "Consume", "Consume_000.wz"));
                string bStr = BuildStringWz(Path.Combine(root, "srcB", "Data", "String", "String_000.wz"), "B_RED_POTION");

                foreach (bool aFirst in new[] { true, false })
                {
                    var manager = new WzFileManager();
                    var order = aFirst ? new[] { aStr, aItem, bStr, bItem } : new[] { bItem, bStr, aItem, aStr };
                    var byPath = new Dictionary<string, WzFile>(StringComparer.OrdinalIgnoreCase);
                    foreach (string p in order)
                        byPath[p] = manager.LoadWzFile(p, WzMapleVersion.BMS);
                    Assert.All(byPath.Values, f => Assert.NotNull(f));

                    var panel = new NodeEditorPanel();

                    // A item -> A's String, regardless of which source was opened first.
                    Assert.True(panel.TryLoad(ItemNode(byPath[aItem]), manager));
                    Assert.Equal(aStr, panel.LinkedStringFilePath, ignoreCase: true);
                    Assert.False(panel.IsLinkedStringReadOnly);

                    // B item -> B's String - the bug used to resolve this to A (zh_TW ordering).
                    Assert.True(panel.TryLoad(ItemNode(byPath[bItem]), manager));
                    Assert.Equal(bStr, panel.LinkedStringFilePath, ignoreCase: true);
                    Assert.False(panel.IsLinkedStringReadOnly);

                    // Editing through B's resolved entry touches B only.
                    var bEntry = (WzStringProperty)byPath[bStr].WzDirectory["Consume.img"]["2000000"]["name"];
                    bEntry.Value = "B_EDITED";
                    Assert.Equal("B_EDITED", StringNameValue(byPath[bStr]));
                    Assert.Equal("A_RED_POTION", StringNameValue(byPath[aStr]));

                    // And the reverse.
                    var aEntry = (WzStringProperty)byPath[aStr].WzDirectory["Consume.img"]["2000000"]["name"];
                    aEntry.Value = "A_EDITED";
                    Assert.Equal("A_EDITED", StringNameValue(byPath[aStr]));
                    Assert.Equal("B_EDITED", StringNameValue(byPath[bStr]));

                    foreach (var f in byPath.Values)
                        f.Dispose();
                }
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        });
    }

    [Fact]
    public void OnlyTheOtherSourcesStringOpen_PanelLinksNoText()
    {
        RunSta(() =>
        {
            string root = Path.Combine(Path.GetTempPath(), "ShuibbFixValidation",
                "scope_" + Guid.NewGuid().ToString("N"));
            try
            {
                string bItem = BuildItemWz(Path.Combine(root, "srcB", "Data", "Item", "Consume", "Consume_000.wz"));
                string aStr = BuildStringWz(Path.Combine(root, "srcA", "Data", "Lang", "zh_TW", "String", "String_000.wz"), "A_RED_POTION");

                var manager = new WzFileManager();
                var itemWz = manager.LoadWzFile(bItem, WzMapleVersion.BMS);
                var strWz = manager.LoadWzFile(aStr, WzMapleVersion.BMS);
                Assert.NotNull(itemWz);
                Assert.NotNull(strWz);

                var panel = new NodeEditorPanel();
                Assert.True(panel.TryLoad(ItemNode(itemWz), manager));

                // No same-source String is open: nothing may be linked writably. No link at all
                // beats a link into the other source's file.
                Assert.True(panel.LinkedStringFilePath == null || panel.IsLinkedStringReadOnly);

                itemWz.Dispose();
                strWz.Dispose();
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        });
    }
}
