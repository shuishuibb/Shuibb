using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace HaRepacker.GUI
{
    /// <summary>
    /// One WZ split file (Character_000.wz, Map_013.wz, ...) found under a selected Data folder,
    /// together with the legacy WZ directory it has to be merged into.
    /// </summary>
    public class DataFolderWzShard
    {
        public DataFolderWzShard(string filePath, string categoryName, string outputRelativePath, string langLocale = null)
        {
            FilePath = filePath;
            CategoryName = categoryName;
            OutputRelativePath = outputRelativePath;
            LangLocale = langLocale;
        }

        /// <summary>
        /// Full path of the .wz split file.
        /// </summary>
        public string FilePath { get; }

        /// <summary>
        /// First path segment under the selected Data folder, after the Lang\&lt;locale&gt;\ part is dropped.
        /// </summary>
        public string CategoryName { get; }

        /// <summary>
        /// Locale folder this split file came from, or null when it does not sit under Lang.
        /// </summary>
        public string LangLocale { get; }

        /// <summary>
        /// A localized file that has to be written after the base files of the same category,
        /// the way the client layers Lang on top of the base data.
        /// </summary>
        public bool IsLangOverlay { get { return LangLocale != null; } }

        /// <summary>
        /// Directory this split file is written into, relative to the chosen output folder.
        /// The folders below the category are kept, so Character\Weapon\Weapon_000.wz lands in
        /// Character.wz\Weapon and Map\Map\Map0\Map0_000.wz lands in Map.wz\Map\Map0.
        /// </summary>
        public string OutputRelativePath { get; }
    }

    /// <summary>
    /// Every split file of one category, in the order they have to be written.
    /// This is the unit of parallelism: two batches may run side by side, the shards inside
    /// a batch may not, because they share an output directory.
    /// </summary>
    public class DataFolderWzCategoryBatch
    {
        public DataFolderWzCategoryBatch(string categoryName, IReadOnlyList<DataFolderWzShard> shards)
        {
            CategoryName = categoryName;
            Shards = shards;
        }

        public string CategoryName { get; }

        public IReadOnlyList<DataFolderWzShard> Shards { get; }
    }

    /// <summary>
    /// Scans a Toki-HA 1.6.8 style Data folder for WZ split files and maps each of them
    /// back to the old-style WZ directory it belongs to.
    /// </summary>
    public static class DataFolderWzScanner
    {
        private static readonly Regex ShardSuffixRegex = new Regex(@"_[0-9]{3}\.wz$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex LangFolderRegex = new Regex(@"Lang\\([^\\]*)\\", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Locale to keep when the Data folder ships several of them under Lang.
        /// Without this, Lang\zh_TW\String\String_000.wz and Lang\zh_CN\String\String_000.wz both
        /// normalise to String\String_000.wz and overwrite each other inside String.wz.
        /// </summary>
        public const string PreferredLangLocale = "zh_TW";

        private const string WzDirectorySuffix = ".wz";

        /// <summary>
        /// True only for split file names such as Character_000.wz or Map_013.wz.
        /// Plain WZ files (Character.wz) are not part of a Data folder export.
        /// </summary>
        public static bool IsShardFileName(string fileName)
        {
            return !string.IsNullOrEmpty(fileName) && ShardSuffixRegex.IsMatch(fileName);
        }

        /// <summary>
        /// Drops the Lang\&lt;locale&gt;\ part so that Lang\zh-TW\String\String_000.wz
        /// is treated as String\String_000.wz.
        /// </summary>
        public static string NormalizeRelativePath(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
                return string.Empty;

            string normalized = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            return LangFolderRegex.Replace(normalized, string.Empty);
        }

        /// <summary>
        /// Category of a split file relative to the selected Data folder.
        /// </summary>
        public static string GetCategoryName(string dataRootPath, string wzFilePath)
        {
            return GetCategoryNameFromRelativePath(Path.GetRelativePath(dataRootPath, wzFilePath));
        }

        /// <summary>
        /// Category of a split file from its path relative to the selected Data folder.
        /// </summary>
        public static string GetCategoryNameFromRelativePath(string relativePath)
        {
            string normalized = NormalizeRelativePath(relativePath);

            string directory = Path.GetDirectoryName(normalized);
            if (!string.IsNullOrEmpty(directory))
                return directory.Split(Path.DirectorySeparatorChar)[0];

            // Split file sitting directly in the selected folder: use its own name without the _NNN suffix.
            return ShardSuffixRegex.Replace(Path.GetFileName(normalized), string.Empty);
        }

        /// <summary>
        /// Old-style WZ directory a split file is written into, relative to the output folder:
        /// the category turned into &lt;category&gt;.wz, followed by whatever folders sat below it.
        /// </summary>
        public static string GetOutputRelativePath(string relativePath)
        {
            string normalized = NormalizeRelativePath(relativePath);

            string directory = Path.GetDirectoryName(normalized);
            if (string.IsNullOrEmpty(directory))
                return GetCategoryNameFromRelativePath(relativePath) + WzDirectorySuffix;

            string[] segments = directory.Split(Path.DirectorySeparatorChar);
            segments[0] += WzDirectorySuffix;
            return Path.Combine(segments);
        }

        /// <summary>
        /// Lang locale a split file belongs to, or null when it does not sit under a Lang folder.
        /// </summary>
        public static string GetLangLocale(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
                return null;

            Match match = LangFolderRegex.Match(relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// Locale comparison that ignores case and the zh-TW / zh_TW separator difference.
        /// </summary>
        public static bool IsPreferredLangLocale(string locale)
        {
            return locale != null
                && locale.Replace('-', '_').Equals(PreferredLangLocale, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Recursively collects every WZ split file below <paramref name="dataRootPath"/>,
        /// grouped so that all shards of one category end up in the same output directory.
        /// When the folder carries the preferred locale under Lang, the other locales are left out
        /// so they cannot overwrite it; a folder without the preferred locale keeps whatever it has.
        /// </summary>
        public static List<DataFolderWzShard> Scan(string dataRootPath)
        {
            List<DataFolderWzShard> shards = new List<DataFolderWzShard>();
            if (string.IsNullOrEmpty(dataRootPath) || !Directory.Exists(dataRootPath))
                return shards;

            List<DataFolderWzShard> candidates = new List<DataFolderWzShard>();
            foreach (string filePath in Directory.EnumerateFiles(dataRootPath, "*.wz", SearchOption.AllDirectories))
            {
                if (!IsShardFileName(Path.GetFileName(filePath)))
                    continue;

                string relativePath = Path.GetRelativePath(dataRootPath, filePath);
                string category = GetCategoryNameFromRelativePath(relativePath);
                if (string.IsNullOrEmpty(category))
                    continue;

                candidates.Add(new DataFolderWzShard(filePath, category,
                    GetOutputRelativePath(relativePath), GetLangLocale(relativePath)));
            }

            bool hasPreferredLocale = candidates.Any(candidate => IsPreferredLangLocale(candidate.LangLocale));
            foreach (DataFolderWzShard candidate in candidates)
            {
                // Files outside Lang belong to every locale, so they are always kept.
                if (!candidate.IsLangOverlay || !hasPreferredLocale || IsPreferredLangLocale(candidate.LangLocale))
                    shards.Add(candidate);
            }

            return shards
                .OrderBy(shard => shard.OutputRelativePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(shard => shard.IsLangOverlay ? 1 : 0)
                .ThenBy(shard => Path.GetFileName(shard.FilePath), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Splits the scan result into one batch per category, keeping the order the shards came in.
        /// Categories are compared case-insensitively because two spellings of the same name would
        /// end up writing into the same folder on Windows.
        /// </summary>
        public static List<DataFolderWzCategoryBatch> GroupByCategory(IEnumerable<DataFolderWzShard> shards)
        {
            if (shards == null)
                return new List<DataFolderWzCategoryBatch>();

            return shards
                .GroupBy(shard => shard.CategoryName, StringComparer.OrdinalIgnoreCase)
                .Select(group => new DataFolderWzCategoryBatch(group.Key, group.ToList()))
                .OrderBy(batch => batch.CategoryName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
