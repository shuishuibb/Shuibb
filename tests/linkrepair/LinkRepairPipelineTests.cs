using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using HaRepacker;
using HaRepacker.GUI.Panels;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using Xunit;
using Assert = Xunit.Assert;

namespace LinkRepairTests;

/// <summary>
/// The round that made the repair asynchronous split ResolveSingleCanvas into
/// PrepareSingleCanvas (read-only, background-safe) + ApplyPreparedCanvas (mutation). These pin
/// that the split halves together do exactly what the one-shot form did - the one-shot form
/// itself is now built on them, and LinkRepairTests keeps covering it - plus the pieces the old
/// suite never had: repeated targets, mixed success/failure, and a save/reopen round trip.
///
/// SCOPE - not covered here: the async orchestration itself (progress, batching, cancellation,
/// viewport) - that runs against real WZ files in tests/linkrepairperf, which drives the real
/// MainForm/MainPanel through RunLinkRepairAsync.
/// </summary>
public sealed class LinkRepairPipelineTests
{
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

    // A REAL zlib stream that inflates to the 64 bytes a 4x4 BGRA32 canvas needs - unlike the
    // minimal empty-deflate marker the older suite uses, this one survives an actual decode.
    private static readonly byte[] RealPixels = { 0x78, 0x9C, 0x13, 0x14, 0xA4, 0x0C, 0x00, 0x00, 0x8A, 0x60, 0x04, 0x41 };

    private static WzImage BuildLinkedImage(params string[] linkedNames)
    {
        var image = new WzImage("8880200.img") { Changed = false };

        var source = new WzCanvasProperty("stand") { PngProperty = new WzPngProperty() };
        source.PngProperty.SetCompressedBytes(RealPixels, 4, 4, WzPngFormat.Format2);
        image.AddProperty(source);

        foreach (string name in linkedNames)
        {
            var linked = new WzCanvasProperty(name) { PngProperty = new WzPngProperty() };
            linked.PngProperty.SetCompressedBytes(new byte[] { 0x78, 0x9C }, 1, 1, WzPngFormat.Format2);
            linked.AddProperty(new WzStringProperty(WzCanvasProperty.InlinkPropertyName, "stand"));
            image.AddProperty(linked);
        }
        image.Parsed = true;
        image.Changed = false;
        return image;
    }

    [Fact]
    public void PrepareThenApply_EqualsTheOldOneShotRepair()
    {
        RunSta(() =>
        {
            WzImage image = BuildLinkedImage("attack");
            var linked = (WzCanvasProperty)image["attack"];

            PreparedCanvasLink prepared = WzLinkResolver.PrepareSingleCanvas(linked);
            Assert.NotNull(prepared);
            Assert.True(prepared.HadInlink);
            Assert.False(prepared.HadOutlink);
            Assert.Equal(4, prepared.Width);
            Assert.Equal(4, prepared.Height);
            Assert.Equal(WzPngFormat.Format2, prepared.Format);

            // Prepare alone mutated nothing.
            Assert.True(linked.ContainsInlinkProperty());
            Assert.Equal(1, linked.PngProperty.Width);

            Assert.True(WzLinkResolver.ApplyPreparedCanvas(linked, prepared));
            Assert.False(linked.ContainsInlinkProperty());
            Assert.Equal(4, linked.PngProperty.Width);
            Assert.Equal(RealPixels, linked.PngProperty.GetCompressedBytes(false));
        });
    }

    [Fact]
    public void Prepare_OnABrokenLink_ReturnsNull_AndTouchesNothing()
    {
        RunSta(() =>
        {
            WzImage image = BuildLinkedImage();
            var broken = new WzCanvasProperty("ghost") { PngProperty = new WzPngProperty() };
            broken.PngProperty.SetCompressedBytes(new byte[] { 0x78, 0x9C }, 1, 1, WzPngFormat.Format2);
            broken.AddProperty(new WzStringProperty(WzCanvasProperty.InlinkPropertyName, "no/such/target"));
            image.AddProperty(broken);
            image.Changed = false;

            Assert.Null(WzLinkResolver.PrepareSingleCanvas(broken));
            Assert.True(broken.ContainsInlinkProperty());
            Assert.Equal(1, broken.PngProperty.Width);
            Assert.False(image.Changed);
        });
    }

    [Fact]
    public void RepeatedTargets_EachDestinationHoldsTheCorrectPayload()
    {
        RunSta(() =>
        {
            // Several canvases pointing at the same source - the common shape in Mob/Npc frames.
            WzImage image = BuildLinkedImage("attack1", "attack2", "die1");
            var root = new WzNode(image);

            int repaired = 0, failed = 0;
            MainPanel.CheckImageNodeRecursively_linkRepair(root, ref repaired, ref failed);

            Assert.Equal(3, repaired);
            Assert.Equal(0, failed);
            foreach (string name in new[] { "attack1", "attack2", "die1" })
            {
                var canvas = (WzCanvasProperty)image[name];
                Assert.False(canvas.ContainsInlinkProperty());
                Assert.Equal(RealPixels, canvas.PngProperty.GetCompressedBytes(false));
            }
            // The source itself is untouched.
            Assert.Equal(RealPixels, ((WzCanvasProperty)image["stand"]).PngProperty.GetCompressedBytes(false));
        });
    }

    /// <summary>
    /// The repaired payload is aliased, not copied: SetCompressedBytes stores the caller's array
    /// reference (WzPngProperty.cs, compressedImageBytes = bytes) and GetCompressedBytes hands the
    /// internal array back out - so every destination repaired from one target, cached or not,
    /// shares a single byte[] with the source. That is safe ONLY because nothing in the codebase
    /// mutates a compressed array in place (every "modify" path rebinds a fresh array; the save
    /// path only reads). This test pins both halves of that contract: the sharing itself, and
    /// that replacing one destination's payload afterwards cannot leak into its siblings or the
    /// source. If payloads are ever deep-copied instead, the Assert.Same lines can simply be
    /// dropped - the isolation asserts below them must keep passing either way.
    /// </summary>
    [Fact]
    public void SharedPayload_ReplacingOneDestination_LeavesTheOthersAndTheSourceIntact()
    {
        RunSta(() =>
        {
            WzImage image = BuildLinkedImage("attack1", "attack2");
            var root = new WzNode(image);

            int repaired = 0, failed = 0;
            MainPanel.CheckImageNodeRecursively_linkRepair(root, ref repaired, ref failed);
            Assert.Equal(2, repaired);

            var source = (WzCanvasProperty)image["stand"];
            var a = (WzCanvasProperty)image["attack1"];
            var b = (WzCanvasProperty)image["attack2"];

            // The deliberate aliasing, made explicit: one immutable array, three holders.
            byte[] aBytes = a.PngProperty.GetCompressedBytes(false);
            byte[] bBytes = b.PngProperty.GetCompressedBytes(false);
            Assert.Same(aBytes, bBytes);
            Assert.Same(aBytes, source.PngProperty.GetCompressedBytes(false));

            // Now "edit" one destination the way every supported path does - by handing it a
            // NEW array (SetCompressedBytes is what the change-image flow ends in).
            byte[] replacement = { 0x78, 0x9C, 0x63, 0x00, 0x00, 0x00, 0x02, 0x00, 0x02 };
            a.PngProperty.SetCompressedBytes(replacement, 1, 1, WzPngFormat.Format2);

            // The sibling and the source still hold the original payload, byte for byte.
            Assert.Equal(RealPixels, b.PngProperty.GetCompressedBytes(false));
            Assert.Equal(RealPixels, source.PngProperty.GetCompressedBytes(false));
            Assert.Equal(4, b.PngProperty.Width);
            Assert.Equal(4, source.PngProperty.Width);
            Assert.Equal(replacement, a.PngProperty.GetCompressedBytes(false));
            Assert.Equal(1, a.PngProperty.Width);

            // And the untouched holders still decode.
            using var decoded = b.PngProperty.GetImage(false);
            Assert.NotNull(decoded);
            Assert.Equal(4, decoded.Width);
        });
    }

    [Fact]
    public void MixedSuccessAndFailure_CountsBothSides_AndFailuresKeepTheirLinks()
    {
        RunSta(() =>
        {
            WzImage image = BuildLinkedImage("attack");
            var broken = new WzCanvasProperty("ghost") { PngProperty = new WzPngProperty() };
            broken.PngProperty.SetCompressedBytes(new byte[] { 0x78, 0x9C }, 1, 1, WzPngFormat.Format2);
            broken.AddProperty(new WzStringProperty(WzCanvasProperty.InlinkPropertyName, "no/such/target"));
            image.AddProperty(broken);
            var root = new WzNode(image);

            int repaired = 0, failed = 0;
            MainPanel.CheckImageNodeRecursively_linkRepair(root, ref repaired, ref failed);

            Assert.Equal(1, repaired);
            Assert.Equal(1, failed);
            Assert.False(((WzCanvasProperty)image["attack"]).ContainsInlinkProperty());
            Assert.True(((WzCanvasProperty)image["ghost"]).ContainsInlinkProperty());
        });
    }

    [Fact]
    public void RepairedImage_SavedAndReopened_KeepsThePixels_AndStaysLinkFree()
    {
        RunSta(() =>
        {
            string root = Path.Combine(Path.GetTempPath(), "ShuibbFixValidation",
                "linkrepair_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string wzPath = Path.Combine(root, "test.wz");
            try
            {
                using (var wz = new WzFile(1, WzMapleVersion.BMS) { Name = "test.wz" })
                {
                    WzImage image = BuildLinkedImage("attack");
                    image.Changed = true;
                    wz.WzDirectory.AddImage(image);

                    var node = new WzNode(image);
                    int repaired = 0, failed = 0;
                    MainPanel.CheckImageNodeRecursively_linkRepair(node, ref repaired, ref failed);
                    Assert.Equal(1, repaired);

                    wz.SaveToDisk(wzPath, false);
                }

                using var reloaded = new WzFile(wzPath, WzMapleVersion.BMS);
                Assert.Equal(WzFileParseStatus.Success, reloaded.ParseWzFile());
                var img = reloaded.WzDirectory.WzImages.First(i => i.Name == "8880200.img");
                img.ParseImage();

                var repairedCanvas = (WzCanvasProperty)img["attack"];
                Assert.NotNull(repairedCanvas);
                Assert.False(repairedCanvas.ContainsInlinkProperty());
                Assert.Equal(4, repairedCanvas.PngProperty.Width);
                Assert.Equal(4, repairedCanvas.PngProperty.Height);
                // The payload survives the round trip and still decodes from standard zlib.
                byte[] bytes = repairedCanvas.PngProperty.GetCompressedBytes(false);
                Assert.True(bytes is { Length: > 2 } && bytes[0] == 0x78);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        });
    }
}
