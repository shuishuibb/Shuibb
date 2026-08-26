using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

namespace TokiAi
{
    /// <summary>
    /// The assistant's view of the local filesystem.
    ///
    /// The safety model here is deliberately NOT "restrict which folders are visible" - it is
    /// "never hand file contents to the model". Listing returns names, sizes and image
    /// dimensions; importing reads pixels straight into a WZ node without the bytes ever
    /// entering the conversation. That is what makes whole-machine access reasonable, and it is
    /// why there is no read-file tool here. Do not add one without revisiting the default mode.
    /// </summary>
    public static class DiskAccess
    {
        public static readonly string[] ImageExtensions = { ".png", ".bmp", ".jpg", ".jpeg", ".gif", ".tif", ".tiff" };

        public static bool IsImageFile(string path)
        {
            string extension = Path.GetExtension(path);
            if (string.IsNullOrEmpty(extension))
                return false;
            foreach (string candidate in ImageExtensions)
                if (string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>
        /// Normalises a path and checks it against the configured mode. Returns null when it is
        /// allowed, or a message explaining the refusal.
        /// </summary>
        public static string CheckAllowed(AiSettings settings, string path, out string fullPath)
        {
            fullPath = null;
            if (string.IsNullOrWhiteSpace(path))
                return "路徑不能是空的。";

            if (settings.DiskAccess == DiskAccessMode.Off)
                return "磁碟存取目前是關閉的。要使用請到「設定」把「磁碟存取」改成「整台電腦」或「指定資料夾」。";

            try
            {
                fullPath = Path.GetFullPath(path.Trim());
            }
            catch (Exception error)
            {
                return "路徑格式不正確:" + error.Message;
            }

            if (settings.DiskAccess == DiskAccessMode.Full)
                return null;

            if (settings.AllowedFolders == null || settings.AllowedFolders.Count == 0)
                return "目前是「指定資料夾」模式,但還沒有加入任何資料夾。請到「設定」加入,或改成「整台電腦」。";

            foreach (string allowed in settings.AllowedFolders)
            {
                if (string.IsNullOrWhiteSpace(allowed))
                    continue;
                string root;
                try
                {
                    root = Path.GetFullPath(allowed.Trim());
                }
                catch
                {
                    continue;
                }
                if (IsInside(root, fullPath))
                    return null;
            }
            return "這個路徑不在允許的資料夾清單裡:" + fullPath
                + "\n允許的是:" + string.Join("、", settings.AllowedFolders);
        }

        /// <summary>
        /// True when candidate is root itself or sits underneath it. Compared on normalised full
        /// paths with a trailing separator so "C:\Data2" does not count as inside "C:\Data".
        /// </summary>
        public static bool IsInside(string root, string candidate)
        {
            string normalisedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(normalisedRoot, candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
                return true;
            return candidate.StartsWith(normalisedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Width x height without decoding the whole image where the format allows it.</summary>
        public static string DescribeImage(string path)
        {
            try
            {
                using (FileStream stream = File.OpenRead(path))
                using (Image image = Image.FromStream(stream, false, false))
                    return image.Width + "x" + image.Height;
            }
            catch
            {
                return "無法讀取尺寸";
            }
        }

        public static string HumanSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.#") + " MB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.##") + " GB";
        }

        /// <summary>
        /// Every image file under a folder, paired with its path relative to that folder. The
        /// relative path (minus extension) is what maps onto WZ node names on import.
        /// </summary>
        public static List<KeyValuePair<string, string>> CollectImages(string folder, bool recursive, int limit)
        {
            List<KeyValuePair<string, string>> found = new List<KeyValuePair<string, string>>();
            Collect(folder, folder, recursive, limit, found);
            found.Sort(delegate (KeyValuePair<string, string> a, KeyValuePair<string, string> b)
            {
                return string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            });
            return found;
        }

        static void Collect(string root, string folder, bool recursive, int limit,
            List<KeyValuePair<string, string>> found)
        {
            if (found.Count >= limit)
                return;
            string[] files;
            try
            {
                files = Directory.GetFiles(folder);
            }
            catch
            {
                return; // unreadable folder: skip rather than abort the whole walk
            }
            foreach (string file in files)
            {
                if (found.Count >= limit)
                    return;
                if (!IsImageFile(file))
                    continue;
                string relative = file.Substring(root.Length).TrimStart(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                found.Add(new KeyValuePair<string, string>(relative, file));
            }
            if (!recursive)
                return;
            string[] directories;
            try
            {
                directories = Directory.GetDirectories(folder);
            }
            catch
            {
                return;
            }
            foreach (string directory in directories)
                Collect(root, directory, true, limit, found);
        }

        /// <summary>
        /// Loads an image into a plain 32bpp bitmap detached from the file, so the file handle
        /// is not held open and the WZ owns its own copy of the pixels.
        /// </summary>
        public static Bitmap LoadBitmap(string path)
        {
            using (Image source = Image.FromFile(path))
            {
                Bitmap copy = new Bitmap(source.Width, source.Height,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (Graphics canvas = Graphics.FromImage(copy))
                {
                    canvas.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    canvas.DrawImage(source, 0, 0, source.Width, source.Height);
                }
                return copy;
            }
        }
    }
}
