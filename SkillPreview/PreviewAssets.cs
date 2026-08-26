using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;

namespace SkillPreview
{
    /// <summary>
    /// The character silhouette images shipped next to the executable (111/222/333.png).
    /// They are purely cosmetic reference art, so a missing file degrades to "no character
    /// drawn" rather than an error.
    /// </summary>
    internal static class PreviewAssets
    {
        private static readonly Dictionary<string, BitmapSource> cache =
            new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);

        internal static BitmapSource Load(string fileName)
        {
            BitmapSource cached;
            if (cache.TryGetValue(fileName, out cached))
                return cached;

            BitmapSource loaded = null;
            try
            {
                string path = FindPath(fileName);
                if (path != null)
                {
                    BitmapImage image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.UriSource = new Uri(path, UriKind.Absolute);
                    image.EndInit();
                    image.Freeze();
                    loaded = image;
                }
            }
            catch
            {
                loaded = null;
            }

            cache[fileName] = loaded;
            return loaded;
        }

        private static string FindPath(string fileName)
        {
            List<string> candidates = new List<string>();
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            candidates.Add(Path.Combine(baseDirectory, fileName));
            candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), fileName));

            DirectoryInfo dir = new DirectoryInfo(baseDirectory);
            for (int i = 0; i < 6 && dir != null; i++)
            {
                candidates.Add(Path.Combine(dir.FullName, fileName));
                dir = dir.Parent;
            }

            return candidates.FirstOrDefault(File.Exists);
        }
    }
}
