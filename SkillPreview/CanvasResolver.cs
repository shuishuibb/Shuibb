using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using MapleLib;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace SkillPreview
{
    /// <summary>
    /// Gets the pixels behind a canvas, including canvases that only carry an "_outlink".
    ///
    /// WzCanvasProperty resolves an _outlink by looking the path up inside its OWN WzFile.
    /// That works for a client whose artwork sits in the same file, but not for the layout
    /// where skills live in Data/Packs/Skill_00000.ms while the art lives in separate
    /// Data/Skill/_Canvas/_canvas_NNN.wz files - the parent lookup finds nothing there and
    /// the canvas falls back to its own 1x1 placeholder.
    ///
    /// So when the normal path yields no real pixels, the outlink is resolved against every
    /// file the WzFileManager has open, loading the relevant _Canvas section first.
    /// </summary>
    internal static class CanvasResolver
    {
        // Canvas -> resolved bitmap. Resolution walks every loaded WZ file, which is far too
        // costly to repeat for each of the ~60 frames a second the effect view draws.
        private static readonly Dictionary<WzCanvasProperty, Bitmap> bitmapCache =
            new Dictionary<WzCanvasProperty, Bitmap>();

        // "skill|112.img|True" -> every loaded image of that name, in search order. Scanning
        // all open WZ files is expensive, so the candidate list is built once per image name.
        private static readonly Dictionary<string, List<WzImage>> imageCache =
            new Dictionary<string, List<WzImage>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Directory name the client stores extracted artwork under.</summary>
        private const string CanvasDirectoryName = "_Canvas";

        private static readonly HashSet<string> attemptedSections =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Forgets which canvas sections failed to load, so the next skill selection tries
        /// them again. Successful lookups are kept - they stay valid, and re-resolving them
        /// would mean re-scanning every open WZ file. Called whenever a skill is loaded, which
        /// is the point at which the set of open files may have changed.
        /// </summary>
        internal static void ResetFailedLookups()
        {
            attemptedSections.Clear();
        }

        internal static Bitmap GetBitmap(WzCanvasProperty canvas, WzFileManager fileManager)
        {
            if (canvas == null)
                return null;

            Bitmap cached;
            if (bitmapCache.TryGetValue(canvas, out cached))
                return cached;

            Bitmap resolved = ResolveUncached(canvas, fileManager);

            // Only successes are cached. A failure is usually transient - the _Canvas section
            // simply had not been loaded yet - and caching it would leave the frame blank for
            // the rest of the session even once the artwork becomes reachable.
            if (IsUsable(resolved))
                bitmapCache[canvas] = resolved;

            return resolved;
        }

        private static Bitmap ResolveUncached(WzCanvasProperty canvas, WzFileManager fileManager)
        {
            Bitmap direct = null;
            try
            {
                direct = canvas.GetLinkedWzCanvasBitmap();
            }
            catch
            {
                direct = null;
            }

            if (IsUsable(direct))
                return direct;

            string outlink = GetOutlink(canvas);
            if (outlink == null || fileManager == null)
                return direct;

            try
            {
                Bitmap viaOutlink = ResolveOutlink(outlink, canvas, fileManager);
                if (IsUsable(viaOutlink))
                    return viaOutlink;
            }
            catch
            {
                // Fall through to whatever the direct call produced.
            }

            return direct;
        }

        /// <summary>A 1x1 canvas is the placeholder left behind when art was moved out.</summary>
        private static bool IsUsable(Bitmap bitmap)
        {
            return bitmap != null && bitmap.Width > 1 && bitmap.Height > 1;
        }

        internal static string GetOutlink(WzCanvasProperty canvas)
        {
            WzStringProperty outlink = canvas["_outlink"] as WzStringProperty;
            return (outlink != null && !string.IsNullOrEmpty(outlink.Value)) ? outlink.Value : null;
        }

        /// <summary>
        /// "Skill/_Canvas/112.img/skill/1121008/effect/0"
        ///   category  = "Skill"      -> which _Canvas section to load
        ///   image     = "112.img"    -> which file to find it in
        ///   remainder = "skill/1121008/effect/0"
        /// </summary>
        private static Bitmap ResolveOutlink(string outlink, WzCanvasProperty source,
            WzFileManager fileManager)
        {
            string[] segments = outlink.Split('/');
            int imageIndex = -1;
            for (int i = 0; i < segments.Length; i++)
            {
                if (segments[i].EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                {
                    imageIndex = i;
                    break;
                }
            }
            if (imageIndex < 0 || imageIndex == segments.Length - 1)
                return null;

            string category = segments[0];
            string imageName = segments[imageIndex];
            string remainder = string.Join("/", segments.Skip(imageIndex + 1));

            // The skill file and the canvas file both contain an image of the same name - e.g.
            // "4100.img" exists in Skill_00005.ms (carrying 1x1 placeholders) and again in
            // _canvas_075.wz (carrying the real art). When the link names _Canvas, only the
            // canvas files can satisfy it; picking the skill file's copy would resolve the
            // placeholder back onto itself and yield nothing.
            bool canvasOnly = outlink.IndexOf("/" + CanvasDirectoryName + "/", StringComparison.OrdinalIgnoreCase) >= 0;

            // Several loaded files can each hold an image of this name. A classic client splits
            // one logical tree across Skill.wz / Skill001.wz / Skill002.wz, and a link written
            // as "Skill/222.img/..." does not say which file 222.img ended up in - so every
            // candidate is tried until one actually yields the frame.
            foreach (WzImage image in FindImages(category, imageName, source, fileManager, canvasOnly))
            {
                try
                {
                    if (!image.Parsed)
                        image.ParseImage();
                }
                catch
                {
                    continue;
                }

                WzCanvasProperty target = image.GetFromPath(remainder) as WzCanvasProperty;
                if (target == null || ReferenceEquals(target, source))
                    continue;

                // A candidate that resolves to another placeholder is no better than the
                // canvas we started from, so keep looking rather than settling for it.
                Bitmap bitmap = null;
                try { bitmap = target.GetLinkedWzCanvasBitmap(); } catch { }
                if (IsUsable(bitmap))
                    return bitmap;
            }

            return null;
        }

        private static List<WzImage> FindImages(string category, string imageName, WzCanvasProperty source,
            WzFileManager fileManager, bool canvasOnly)
        {
            string key = category + "|" + imageName + "|" + canvasOnly;
            List<WzImage> cached;
            if (imageCache.TryGetValue(key, out cached))
                return cached;

            List<WzImage> found = SearchLoadedFiles(imageName, fileManager, canvasOnly);
            if (found.Count == 0)
            {
                // Pull in Data/<category>/_Canvas/_canvas_NNN.wz, then look again.
                // LoadCanvasSection itself no-ops once a section is loaded, and the
                // attemptedSections guard stops a genuinely absent section being retried
                // for every frame.
                if (attemptedSections.Add(key))
                {
                    try
                    {
                        fileManager.LoadCanvasSection(category.ToLower(), GetMapleVersion(source));
                    }
                    catch
                    {
                        // No canvas section available - handled by returning null below.
                    }
                    found = SearchLoadedFiles(imageName, fileManager, canvasOnly);
                }
            }

            // Same reasoning as the bitmap cache: remember hits, keep retrying misses.
            if (found.Count > 0)
                imageCache[key] = found;

            return found;
        }

        private static List<WzImage> SearchLoadedFiles(string imageName, WzFileManager fileManager, bool canvasOnly)
        {
            List<WzImage> matches = new List<WzImage>();
            foreach (WzFile file in fileManager.WzFileList)
            {
                WzDirectory root = file == null ? null : file.WzDirectory;
                if (root == null)
                    continue;
                if (canvasOnly && !IsCanvasFile(file))
                    continue;

                CollectImages(root, imageName, 0, matches);
            }
            return matches;
        }

        /// <summary>The extracted-artwork files are named _canvas_000.wz, _canvas_001.wz, ...</summary>
        private static bool IsCanvasFile(WzFile file)
        {
            return file.Name != null
                && file.Name.StartsWith("_canvas", StringComparison.OrdinalIgnoreCase);
        }

        private static void CollectImages(WzDirectory directory, string imageName, int depth, List<WzImage> into)
        {
            if (directory == null || depth > 3)
                return;

            foreach (WzImage image in directory.WzImages)
            {
                if (string.Equals(image.Name, imageName, StringComparison.OrdinalIgnoreCase))
                    into.Add(image);
            }

            foreach (WzDirectory sub in directory.WzDirectories)
                CollectImages(sub, imageName, depth + 1, into);
        }

        private static WzMapleVersion GetMapleVersion(WzCanvasProperty source)
        {
            try
            {
                WzFile parent = source == null ? null : source.WzFileParent;
                if (parent != null)
                    return parent.MapleVersion;
            }
            catch
            {
                // Fall through to the default below.
            }
            return WzMapleVersion.BMS;
        }
    }
}
