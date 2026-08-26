using HaRepacker.GUI.Panels;
using Xunit;
using Assert = Xunit.Assert;

namespace SearchSessionTests;

/// <summary>
/// Targeted regression for the "Find next always stops on the first match" bug.
///
/// Find next used to re-read DataTree.SelectedNodes on every press. The first hit selects itself
/// (SelectAndRevealNativeNode), so by the second press the "scope" was that hit - searching
/// 0202.img for "spec" found 02020000\spec and could never reach 02020001\spec. MainPanel now
/// snapshots the roots once per search session; SearchSessionPolicy.ShouldSnapshotRoots is the
/// rule for when that snapshot is retaken, and is what these tests cover.
///
/// SCOPE - what these tests do NOT cover, and must not be cited as proof of:
///   * the actual 1st/2nd/3rd match traversal (SearchTV / SearchWzProperties / searchidx), which
///     was already correct and is unchanged,
///   * MainPanel.EnsureSearchSession reading the live tree selection,
///   * the isNavigatingSearchResult guard in SynchronizeNativeSelection that stops a search hit
///     from counting as the user picking a new root.
/// Those need a real tree and window; they are verified manually.
/// </summary>
public sealed class SearchSessionPolicyTests
{
    private const string Query = "spec";

    [Fact]
    public void FirstEverSearch_HasNoSavedRoots_SoItSnapshots()
    {
        Assert.True(SearchSessionPolicy.ShouldSnapshotRoots(
            hasSavedRoots: false, savedQuery: null, currentQuery: Query, anyRootDetached: false));
    }

    [Fact]
    public void SecondPressOfTheSameSearch_KeepsTheSavedRoots()
    {
        // The heart of the fix: pressing Find next again must NOT re-snapshot, because by now
        // the tree selection is the first hit rather than the scope the user chose.
        Assert.False(SearchSessionPolicy.ShouldSnapshotRoots(
            hasSavedRoots: true, savedQuery: Query, currentQuery: Query, anyRootDetached: false));
    }

    [Fact]
    public void QueryChanged_StartsANewSession()
    {
        Assert.True(SearchSessionPolicy.ShouldSnapshotRoots(
            hasSavedRoots: true, savedQuery: Query, currentQuery: "info", anyRootDetached: false));
    }

    [Fact]
    public void UserPickedANewRoot_StartsANewSession()
    {
        // Selecting a node clears the stored roots (MainPanel.EndSearchSession), which reaches
        // this rule as hasSavedRoots: false - so the same query re-scopes to the new selection.
        Assert.True(SearchSessionPolicy.ShouldSnapshotRoots(
            hasSavedRoots: false, savedQuery: Query, currentQuery: Query, anyRootDetached: false));
    }

    [Fact]
    public void StoredRootRemovedFromTree_StartsANewSession()
    {
        Assert.True(SearchSessionPolicy.ShouldSnapshotRoots(
            hasSavedRoots: true, savedQuery: Query, currentQuery: Query, anyRootDetached: true));
    }

    [Fact]
    public void QueryComparisonIsOrdinal_SoCasingCountsAsADifferentSearch()
    {
        Assert.True(SearchSessionPolicy.ShouldSnapshotRoots(
            hasSavedRoots: true, savedQuery: "spec", currentQuery: "SPEC", anyRootDetached: false));
    }

    [Fact]
    public void EmptyQuerySessionIsStillASession()
    {
        // Guards against treating "" as "no session" and re-snapshotting every press.
        Assert.False(SearchSessionPolicy.ShouldSnapshotRoots(
            hasSavedRoots: true, savedQuery: "", currentQuery: "", anyRootDetached: false));
    }
}
