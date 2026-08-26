using System;

namespace HaRepacker.GUI.Panels
{
    /// <summary>
    /// Decides when "Find next" starts over from a fresh set of search roots.
    ///
    /// Find next used to re-read the tree selection on every press. The first hit selects itself
    /// (SelectAndRevealNativeNode), so the second press searched only inside that hit and could
    /// never reach the next match - searching 0202.img for "spec" stopped forever on
    /// 02020000\spec. MainPanel now snapshots the roots once per search session and re-walks
    /// them; this is the rule for when that snapshot is retaken.
    ///
    /// Split out as a plain static so it can be tested without a tree or a window.
    /// </summary>
    public static class SearchSessionPolicy
    {
        /// <summary>
        /// True when the saved roots must be replaced with the tree's current selection.
        /// </summary>
        /// <param name="hasSavedRoots">
        /// Whether a session is already stored. False after the query changed, or after the user
        /// picked a different node themselves - a selection the search made while jumping to its
        /// own hit deliberately leaves the session alone.
        /// </param>
        /// <param name="savedQuery">The text the stored session was built for.</param>
        /// <param name="currentQuery">The text about to be searched for.</param>
        /// <param name="anyRootDetached">
        /// Whether any stored root has since been removed from the tree; its subtree can no
        /// longer be walked, so the session is stale.
        /// </param>
        public static bool ShouldSnapshotRoots(bool hasSavedRoots, string savedQuery, string currentQuery, bool anyRootDetached)
        {
            if (!hasSavedRoots)
                return true;
            if (anyRootDetached)
                return true;
            return !string.Equals(savedQuery, currentQuery, StringComparison.Ordinal);
        }
    }
}
