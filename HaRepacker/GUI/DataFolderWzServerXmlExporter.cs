using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MapleLib.WzLib;
using MapleLib.WzLib.Serializer;

namespace HaRepacker.GUI
{
    /// <summary>
    /// Writes the WZ split files of a Data folder out as server-side XML, one category at a time
    /// in parallel. The shards of a single category stay on one task and keep their order, so the
    /// directory they share is only ever written by a single thread and the Lang overlay still
    /// lands last.
    /// </summary>
    public static class DataFolderWzServerXmlExporter
    {
        /// <summary>
        /// Default degree of parallelism: half the cores, never fewer than 2 and never more than 6,
        /// so a large export leaves room for the rest of the machine.
        /// </summary>
        public static int DefaultParallelism
        {
            get { return Math.Clamp(Environment.ProcessorCount / 2, 2, 6); }
        }

        /// <summary>
        /// Turns a configured value into the degree of parallelism actually used.
        /// Zero or less means "decide for me"; anything else is capped to 1..ProcessorCount,
        /// and 1 makes the export run one category after another.
        /// </summary>
        public static int ResolveParallelism(int configuredValue)
        {
            if (configuredValue <= 0)
                return DefaultParallelism;

            return Math.Clamp(configuredValue, 1, Environment.ProcessorCount);
        }

        /// <summary>
        /// Runs the export and returns the messages of the files that could not be written.
        /// Errors are collected instead of logged so that the caller can write them out from a
        /// single thread once every category has finished.
        /// </summary>
        /// <param name="shardCompleted">Called once per split file, from the task that handled it.</param>
        public static IReadOnlyList<string> Export(
            IEnumerable<DataFolderWzShard> shards,
            string baseDirectory,
            WzMapleVersion version,
            int indentation,
            LineBreak lineBreakType,
            int maxDegreeOfParallelism,
            Action shardCompleted,
            CancellationToken cancellationToken)
        {
            ConcurrentQueue<string> errors = new ConcurrentQueue<string>();

            List<DataFolderWzCategoryBatch> batches = DataFolderWzScanner.GroupByCategory(shards);
            if (batches.Count == 0)
                return Array.Empty<string>();

            if (!Directory.Exists(baseDirectory))
            {
                Directory.CreateDirectory(baseDirectory);
            }

            ParallelOptions options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, maxDegreeOfParallelism),
                CancellationToken = cancellationToken
            };

            try
            {
                Parallel.ForEach(batches, options, batch => ExportCategory(
                    batch, baseDirectory, version, indentation, lineBreakType, errors, shardCompleted, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                // Aborted from the UI. Whatever the finished categories already wrote stays on disk.
            }

            return errors.ToArray();
        }

        private static void ExportCategory(
            DataFolderWzCategoryBatch batch,
            string baseDirectory,
            WzMapleVersion version,
            int indentation,
            LineBreak lineBreakType,
            ConcurrentQueue<string> errors,
            Action shardCompleted,
            CancellationToken cancellationToken)
        {
            // One serializer per category task - WzClassicXmlSerializer tracks curr/total while it
            // works, so sharing a single instance between tasks would corrupt both.
            WzClassicXmlSerializer serializer = new WzClassicXmlSerializer(indentation, lineBreakType, false);

            foreach (DataFolderWzShard shard in batch.Shards)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                try
                {
                    using (WzFile f = new WzFile(shard.FilePath, version))
                    {
                        WzFileParseStatus parseStatus = f.ParseWzFile();
                        if (parseStatus != WzFileParseStatus.Success)
                        {
                            errors.Enqueue($"Failed to parse WZ file '{shard.FilePath}': {parseStatus}");
                        }
                        else
                        {
                            serializer.SerializeFile(f, Path.Combine(baseDirectory, shard.OutputRelativePath));
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Enqueue($"Failed to extract WZ file '{shard.FilePath}': {ex.Message}");
                }

                shardCompleted?.Invoke();
            }
        }
    }
}
