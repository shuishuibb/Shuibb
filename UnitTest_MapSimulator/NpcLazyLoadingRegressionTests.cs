using HaCreator.Wz;
using HaCreator.MapSimulator.Animation;
using HaSharedLibrary.Render.DX;
using MapleLib.Img;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using Moq;

namespace UnitTest_MapSimulator;

public sealed class NpcLazyLoadingRegressionTests
{
    [Fact]
    public void UpdateElapsed_AdvancesNpcFramesFromUpdateDelta()
    {
        IDXObject first = CreateFrame(delay: 100);
        IDXObject second = CreateFrame(delay: 100);
        var animationSet = new NpcAnimationSet();
        animationSet.AddAnimation("stand", new List<IDXObject> { first, second });
        var controller = new AnimationController(animationSet, "stand");

        Assert.False(controller.UpdateElapsed(99));
        Assert.Same(first, controller.GetCurrentFrame());

        Assert.True(controller.UpdateElapsed(1));
        Assert.Same(second, controller.GetCurrentFrame());

        Assert.True(controller.UpdateElapsed(100));
        Assert.Same(first, controller.GetCurrentFrame());
    }

    [Fact]
    public void ExtractNpcStringData_LoadsOnlyNpcCatalogue()
    {
        var npcImage = new WzImage("Npc.img");
        var npc = new WzSubProperty("1012003");
        npc.AddProperty(new WzStringProperty("name", "Chief Stan"));
        npc.AddProperty(new WzStringProperty("func", "Henesys Chief"));
        npcImage.AddProperty(npc);

        var dataSource = new Mock<IDataSource>(MockBehavior.Strict);
        dataSource
            .Setup(source => source.GetImage("String", "Npc.img"))
            .Returns(npcImage);

        var manager = new WzInformationManager();
        new ImgDataExtractor(dataSource.Object, manager).ExtractNpcStringData();

        Assert.Equal("Chief Stan", manager.NpcNameCache["1012003"].Item1);
        Assert.Equal("Henesys Chief", manager.NpcNameCache["1012003"].Item2);
        dataSource.VerifyAll();
    }

    private static IDXObject CreateFrame(int delay)
    {
        var frame = new Mock<IDXObject>();
        frame.SetupGet(value => value.Delay).Returns(delay);
        return frame.Object;
    }
}
