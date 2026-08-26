using System;
using System.Drawing;
using System.IO;
using System.Threading;
using MapleLib.WzLib;
using MapleLib.WzLib.Serializer;
using MapleLib.WzLib.WzProperties;
using Xunit;
using Assert = Xunit.Assert;

namespace MapleLib.Tests;

/// <summary>
/// Targeted regression for two of this round's fixes:
///  - WzFileExporter's new CancellationToken parameter, which backs MainForm's Abort button now
///    that it no longer calls the unsupported Thread.Abort() on modern .NET (see
///    HaRepacker\GUI\MainForm.cs: AbortButton_Click, RunWzFilesExtraction, RunWzImgDirsExtraction,
///    RunWzObjExtraction, WzImporterThread).
///  - WzCanvasProperty's Origin vector being scaled directly instead of through
///    SetCanvasOriginPosition, whose zero-check incorrectly refuses a 0-anchored origin (see
///    HaRepacker\GUI\Panels\MainPanel.xaml.cs: AiBatchImageUpscaleEdit).
/// </summary>
public sealed class WzFileExporterCancellationTests
{
    private static readonly string GmsFixturePath =
        Path.Combine(Directory.GetCurrentDirectory(), "WzFiles", "Common", "TamingMob_000_GMS_237.wz");

    [Fact]
    public void RunWzFilesExtraction_PreCancelledToken_SkipsAllFiles()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        var serializer = new CountingFileSerializer();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            WzFileExporter.RunWzFilesExtraction(
                [GmsFixturePath], root, WzMapleVersion.BMS, serializer, cancellationToken: cts.Token);

            Assert.Equal(0, serializer.SerializeCount);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RunWzFilesExtraction_WithoutCancellation_StillProcessesTheFile()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        var serializer = new CountingFileSerializer();

        try
        {
            WzFileExporter.RunWzFilesExtraction(
                [GmsFixturePath], root, WzMapleVersion.BMS, serializer);

            Assert.Equal(1, serializer.SerializeCount);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RunWzImgDirsExtraction_PreCancelledToken_SkipsAllItems()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        var serializer = new CountingImageSerializer();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            WzFileExporter.RunWzImgDirsExtraction(
                [new WzDirectory("dir1")], [new WzImage("img1.img")], root, serializer,
                cancellationToken: cts.Token);

            Assert.Equal(0, serializer.ImageCount);
            Assert.Equal(0, serializer.DirectoryCount);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RunWzImgDirsExtraction_WithoutCancellation_ProcessesAllItems()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        var serializer = new CountingImageSerializer();

        try
        {
            WzFileExporter.RunWzImgDirsExtraction(
                [new WzDirectory("dir1")], [new WzImage("img1.img")], root, serializer);

            Assert.Equal(1, serializer.ImageCount);
            Assert.Equal(1, serializer.DirectoryCount);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RunWzXmlExtraction_PreCancelledToken_SkipsAllObjects()
    {
        var serializer = new CountingObjectSerializer();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        WzFileExporter.RunWzXmlExtraction(
            [new WzImage("a.img"), new WzImage("b.img")], "unused", serializer,
            cancellationToken: cts.Token);

        Assert.Equal(0, serializer.SerializeCount);
    }

    [Fact]
    public void RunWzXmlExtraction_WithoutCancellation_ProcessesAllObjects()
    {
        var serializer = new CountingObjectSerializer();

        WzFileExporter.RunWzXmlExtraction(
            [new WzImage("a.img"), new WzImage("b.img")], "unused", serializer);

        Assert.Equal(2, serializer.SerializeCount);
    }

    [Theory]
    [InlineData(10f, 20f)]
    [InlineData(10f, 0f)]
    [InlineData(0f, 20f)]
    [InlineData(0f, 0f)]
    [InlineData(-10f, 20f)]
    [InlineData(10f, -20f)]
    public void CanvasOriginVector_DirectScale_HandlesZeroAndNegativeCoordinates(float x, float y)
    {
        const float scale = 4f * 0.5f; // same shape as AiBatchImageUpscaleEdit's SCALE_UP_FACTOR * downscaleFactorAfter

        var canvas = new WzCanvasProperty("canvas");
        canvas.AddProperty(new WzVectorProperty(WzCanvasProperty.OriginPropertyName, x, y));

        // The exact operation MainPanel.xaml.cs's AI-upscale path now performs: read the Origin
        // WzVectorProperty directly and multiply, instead of going through
        // WzCanvasProperty.SetCanvasOriginPosition (see the next test for why).
        var originProp = (WzVectorProperty)canvas[WzCanvasProperty.OriginPropertyName];
        Assert.NotNull(originProp);

        originProp.X.SetValue(originProp.X.Value * scale);
        originProp.Y.SetValue(originProp.Y.Value * scale);

        PointF result = canvas.GetCanvasOriginPosition();
        Assert.Equal((int)(x * scale), (int)result.X);
        Assert.Equal((int)(y * scale), (int)result.Y);
    }

    [Fact]
    public void SetCanvasOriginPosition_StillThrowsForZeroCoordinate_DocumentingWhyDirectAccessIsUsed()
    {
        // Documents why MainPanel.xaml.cs's AI-upscale path (like ResizeCanvasByScale before it)
        // writes directly to the origin WzVectorProperty instead of calling
        // WzCanvasProperty.SetCanvasOriginPosition: that helper's own zero-check treats a
        // legitimate 0-anchored origin as "no origin present" and throws. If this assertion ever
        // starts failing because SetCanvasOriginPosition was fixed at its source, the
        // MainPanel.xaml.cs workaround can be simplified back to calling it directly.
        var canvas = new WzCanvasProperty("canvas");
        canvas.AddProperty(new WzVectorProperty(WzCanvasProperty.OriginPropertyName, 10f, 0f));

        Assert.Throws<Exception>(() => canvas.SetCanvasOriginPosition(new PointF(99, 99)));
    }

    private sealed class CountingFileSerializer : IWzFileSerializer
    {
        public int SerializeCount { get; private set; }

        public void SerializeFile(WzFile file, string path)
        {
            SerializeCount++;
        }
    }

    private sealed class CountingImageSerializer : IWzImageSerializer
    {
        public int ImageCount { get; private set; }
        public int DirectoryCount { get; private set; }

        public void SerializeFile(WzFile file, string path) { }

        public void SerializeDirectory(WzDirectory dir, string path)
        {
            DirectoryCount++;
        }

        public void SerializeImage(WzImage img, string path)
        {
            ImageCount++;
        }
    }

    private sealed class CountingObjectSerializer : ProgressingWzSerializer, IWzObjectSerializer
    {
        public int SerializeCount { get; private set; }

        public void SerializeObject(WzObject obj, string path)
        {
            SerializeCount++;
        }
    }
}
