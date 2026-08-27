using System;
using System.Collections.Generic;

namespace SkillPreview
{
    /// <summary>
    /// Decides which open String WZ files belong to the same Data source as the node being
    /// edited, so that text is read from - and written to - that source only.
    ///
    /// Two full Data sets can be open at once (a private-server one and an official one), both
    /// containing a String_000.wz and both containing the same item ids. Matching by file name,
    /// or taking the first id hit across every open file, silently writes the edit into the
    /// other source's String file. Layouts differ between sources (String\ vs Lang\zh_TW\String\),
    /// so nothing here assumes any directory name; the only signal used is how much of the two
    /// files' directory paths is shared.
    /// </summary>
    public static class StringSourceScope
    {
        /// <summary>
        /// How far above the selected file's own directory the common ancestor may sit for the
        /// candidate to still count as the same source. Real layouts put category files at most
        /// three folders below the shared Data root (Data\Character\Accessory\_Canvas). Kept
        /// tight on purpose: every extra level admits more "two different sources under one
        /// nearby parent folder" false positives, and a false positive here is a write into the
        /// wrong source's file.
        /// </summary>
        private const int MaxLevelsAboveSelectedFile = 3;

        /// <summary>A drive or share root alone is never evidence of a shared source.</summary>
        private const int MinSharedSegments = 2;

        /// <summary>Path segments, case preserved, both separators accepted, empties dropped.</summary>
        private static string[] Segments(string path)
        {
            if (string.IsNullOrEmpty(path))
                return Array.Empty<string>();
            return path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>Number of leading directory segments the two file paths share.</summary>
        public static int SharedSegmentCount(string filePathA, string filePathB)
        {
            string[] a = Segments(filePathA);
            string[] b = Segments(filePathB);
            // Compare directories only - the file names themselves don't define the source.
            int lenA = Math.Max(0, a.Length - 1);
            int lenB = Math.Max(0, b.Length - 1);
            int limit = Math.Min(lenA, lenB);
            int shared = 0;
            while (shared < limit && string.Equals(a[shared], b[shared], StringComparison.OrdinalIgnoreCase))
                shared++;
            return shared;
        }

        /// <summary>
        /// From the open String WZ paths, the ones that belong to the selected file's source.
        /// Empty means "no safe choice" - the caller must fall back to read-only or no link,
        /// never to a writable file from another source.
        /// </summary>
        public static IReadOnlyList<string> PickSameSource(string selectedFilePath, IReadOnlyList<string> candidatePaths)
        {
            var picked = new List<string>();
            if (candidatePaths == null || candidatePaths.Count == 0)
                return picked;

            // The selected node has no on-disk origin (a standalone IMG, a freshly built node):
            // there is nothing to anchor a source to. Only proceed when every open String file is
            // itself one family - if two sources' String files are open, either could be wrong.
            if (string.IsNullOrEmpty(selectedFilePath))
            {
                for (int i = 0; i < candidatePaths.Count; i++)
                    for (int j = i + 1; j < candidatePaths.Count; j++)
                        if (SharedSegmentCount(candidatePaths[i], candidatePaths[j]) < MinSharedSegments)
                            return picked;
                picked.AddRange(candidatePaths);
                return picked;
            }

            int selectedDirDepth = Math.Max(0, Segments(selectedFilePath).Length - 1);

            // Deepest shared ancestry wins; the two bars throw out drive-root coincidences and
            // "same grandparent folder" neighbours.
            int best = 0;
            var scores = new int[candidatePaths.Count];
            for (int i = 0; i < candidatePaths.Count; i++)
            {
                int shared = SharedSegmentCount(selectedFilePath, candidatePaths[i]);
                bool eligible = shared >= MinSharedSegments
                    && selectedDirDepth - shared <= MaxLevelsAboveSelectedFile;
                scores[i] = eligible ? shared : -1;
                if (scores[i] > best)
                    best = scores[i];
            }
            if (best <= 0)
                return picked;

            for (int i = 0; i < candidatePaths.Count; i++)
                if (scores[i] == best)
                    picked.Add(candidatePaths[i]);

            // The survivors must also be one family among themselves (zh_TW and zh_CN under one
            // Data root are; two different sources tying by accident are not). Ambiguity means
            // no link rather than a coin toss over which file receives the write.
            for (int i = 0; i < picked.Count; i++)
                for (int j = i + 1; j < picked.Count; j++)
                    if (SharedSegmentCount(picked[i], picked[j]) < best)
                        return new List<string>();

            return picked;
        }
    }
}
