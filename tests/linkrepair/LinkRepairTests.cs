using System;
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
/// Targeted regression for "Fix '_inlink', '_outlink' nodes for old MapleStory" leaving canvases
/// blank. The repair deleted the _inlink tree node first - which removes the underlying property
/// too - and only then asked the resolver to copy the pixels; with the link already gone the
/// resolver copied nothing, so the canvas lost both its link and its picture.
///
/// These drive MainPanel.CheckImageNodeRecursively_linkRepair through real WzNodes over an
/// in-memory image, the way the context menu does. The WinForms tree is a plain model here - no
/// window, no handle. STA thread only because WzNode is UI-adjacent.
///
/// SCOPE - not covered: the context-menu wiring, the completion MessageBox, _outlink across two
/// files on disk (same resolver entry point; verified manually against real Data).
/// </summary>
public sealed class LinkRepairTests
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

    private static readonly byte[] RealPixels = { 0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01 };

    /// <summary>An image holding a real canvas and a second canvas that only points at it.</summary>
    private static WzImage BuildLinkedImage(string inlinkTarget = "stand")
    {
        var image = new WzImage("8880200.img") { Changed = false };

        var source = new WzCanvasProperty("stand") { PngProperty = new WzPngProperty() };
        source.PngProperty.SetCompressedBytes(RealPixels, 4, 4, WzPngFormat.Format2);

        var linked = new WzCanvasProperty("attack") { PngProperty = new WzPngProperty() };
        linked.PngProperty.SetCompressedBytes(new byte[] { 0x78, 0x9C }, 1, 1, WzPngFormat.Format2);
        linked.AddProperty(new WzStringProperty(WzCanvasProperty.InlinkPropertyName, inlinkTarget));

        image.AddProperty(source);
        image.AddProperty(linked);
        image.Parsed = true;
        image.Changed = false;
        return image;
    }

    [Fact]
    public void RepairingAnInlinkCanvas_CopiesThePixels_ThenRemovesTheLink()
    {
        RunSta(() =>
        {
            WzImage image = BuildLinkedImage();
            var root = new WzNode(image);
            var linked = (WzCanvasProperty)image["attack"];

            int repaired = 0, failed = 0;
            MainPanel.CheckImageNodeRecursively_linkRepair(root, ref repaired, ref failed);

            Assert.Equal(1, repaired);
            Assert.Equal(0, failed);

            // The picture is really there now - the bug left it blank.
            Assert.Equal(
                ((WzCanvasProperty)image["stand"]).PngProperty.GetCompressedBytes(saveInMemory: true),
                linked.PngProperty.GetCompressedBytes(saveInMemory: true));

            // And the link is cleaned up - in the WZ and in the tree.
            Assert.False(linked.ContainsInlinkProperty());
            WzNode canvasNode = WzNode.GetChildNode(root, "attack");
            Assert.NotNull(canvasNode);
            Assert.Null(WzNode.GetChildNode(canvasNode, WzCanvasProperty.InlinkPropertyName));

            // The save path keys off this; DeleteWzNode can no longer set it for us.
            Assert.True(image.Changed);
        });
    }

    [Fact]
    public void ABrokenLink_IsLeftExactlyAsItWas()
    {
        RunSta(() =>
        {
            WzImage image = BuildLinkedImage(inlinkTarget: "does/not/exist");
            var root = new WzNode(image);
            var linked = (WzCanvasProperty)image["attack"];

            int repaired = 0, failed = 0;
            MainPanel.CheckImageNodeRecursively_linkRepair(root, ref repaired, ref failed);

            Assert.Equal(0, repaired);
            Assert.Equal(1, failed);

            // The old code destroyed the link even when it could not resolve it. Now a link
            // that cannot be followed keeps both its property and its tree node.
            Assert.True(linked.ContainsInlinkProperty());
            WzNode canvasNode = WzNode.GetChildNode(root, "attack");
            Assert.NotNull(WzNode.GetChildNode(canvasNode, WzCanvasProperty.InlinkPropertyName));
        });
    }

    [Fact]
    public void CanvasesWithoutLinks_AreNotCounted()
    {
        RunSta(() =>
        {
            var image = new WzImage("plain.img") { Changed = false };
            var canvas = new WzCanvasProperty("icon") { PngProperty = new WzPngProperty() };
            canvas.PngProperty.SetCompressedBytes(RealPixels, 4, 4, WzPngFormat.Format2);
            image.AddProperty(canvas);
            image.Parsed = true;
            image.Changed = false;

            var root = new WzNode(image);
            int repaired = 0, failed = 0;
            MainPanel.CheckImageNodeRecursively_linkRepair(root, ref repaired, ref failed);

            Assert.Equal(0, repaired);
            Assert.Equal(0, failed);
            Assert.False(image.Changed);
        });
    }

    [Fact]
    public void HashNodes_AreStillRemoved()
    {
        RunSta(() =>
        {
            var image = new WzImage("hashed.img") { Changed = false };
            image.AddProperty(new WzStringProperty("_hash", "abc123"));
            image.Parsed = true;
            image.Changed = false;

            var root = new WzNode(image);
            int repaired = 0, failed = 0;
            MainPanel.CheckImageNodeRecursively_linkRepair(root, ref repaired, ref failed);

            Assert.Null(image.WzProperties.FirstOrDefault(p => p.Name == "_hash"));
        });
    }
}
