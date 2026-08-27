using HaRepacker.GUI.WorldMap;
using Xunit;
using Assert = Xunit.Assert;

namespace WorldMapEditorTests;

/// <summary>
/// Targeted tests for the 顯示 / 隱藏 linkImg toggle's decision rule.
///
/// SCOPE - what these tests do NOT cover, and must not be cited as proof of:
///   * the button actually appearing in the toolbar or its click wiring,
///   * artwork really disappearing from the canvas,
///   * a click over hidden artwork panning the map instead,
///   * the spot dots and MapLink diamonds staying on screen.
/// Those are visual and interaction behaviour, verified manually.
///
/// The toggle holding no document state is enforced by construction rather than by a test:
/// WorldMapLinkImageVisibility takes only the two booleans below, so it cannot reach the pending
/// previews, the document or the WZ.
/// </summary>
public sealed class WorldMapLinkImageVisibilityTests
{
    /// <summary>The panel starts with the artwork on, so the button offers to hide it.</summary>
    [Fact]
    public void DefaultState_ShowsArtworkAndOffersToHideIt()
    {
        Assert.True(WorldMapLinkImageVisibility.ShouldShowImage(true));
        Assert.Equal("隱藏 linkImg", WorldMapLinkImageVisibility.ButtonText(true));
    }

    [Fact]
    public void Hiding_StopsDrawingTheArtworkAndOffersToShowItAgain()
    {
        Assert.False(WorldMapLinkImageVisibility.ShouldShowImage(false));
        Assert.Equal("顯示 linkImg", WorldMapLinkImageVisibility.ButtonText(false));
    }

    [Fact]
    public void StatusLineReportsWhatJustHappened()
    {
        Assert.Equal("已隱藏 linkImg。", WorldMapLinkImageVisibility.StatusText(false));
        Assert.Equal("已顯示 linkImg。", WorldMapLinkImageVisibility.StatusText(true));
    }

    /// <summary>
    /// HitTestLinkImage bails on this: hidden artwork must not keep catching clicks that belong
    /// to the background, a spot marker or a MapLink marker underneath it.
    /// </summary>
    [Fact]
    public void HiddenArtworkIsNotClickable()
    {
        Assert.False(WorldMapLinkImageVisibility.ShouldShowImage(false));
    }

    /// <summary>
    /// The regression this rule exists for: hiding the artwork of a selected MapLink used to
    /// leave its dashed frame floating over the map with nothing inside it.
    /// </summary>
    [Fact]
    public void HidingTheArtworkAlsoHidesTheSelectionFrame()
    {
        Assert.False(WorldMapLinkImageVisibility.ShouldShowOutline(false, isSelected: true));
    }

    [Fact]
    public void ShowingTheArtworkRestoresTheFrameOnlyForTheSelectedLink()
    {
        Assert.True(WorldMapLinkImageVisibility.ShouldShowOutline(true, isSelected: true));
        Assert.False(WorldMapLinkImageVisibility.ShouldShowOutline(true, isSelected: false));
    }

    [Fact]
    public void AnUnselectedLinkNeverGetsAFrame()
    {
        Assert.False(WorldMapLinkImageVisibility.ShouldShowOutline(false, isSelected: false));
    }

    /// <summary>Toggling twice is a round trip - the toggle carries no other state.</summary>
    [Fact]
    public void TogglingTwiceReturnsToTheStartingState()
    {
        bool visible = true;

        visible = !visible;
        Assert.False(WorldMapLinkImageVisibility.ShouldShowImage(visible));
        Assert.Equal("顯示 linkImg", WorldMapLinkImageVisibility.ButtonText(visible));

        visible = !visible;
        Assert.True(WorldMapLinkImageVisibility.ShouldShowImage(visible));
        Assert.Equal("隱藏 linkImg", WorldMapLinkImageVisibility.ButtonText(visible));
    }
}
