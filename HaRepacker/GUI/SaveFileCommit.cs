using System;
using System.IO;

namespace HaRepacker.GUI
{
    /// <summary>
    /// Outcome of committing a finished temp file onto its target path. Success is only ever
    /// reported after the new bytes are actually at the target; on failure the fields say where
    /// the recoverable copies are, so the UI can tell the user instead of guessing.
    /// </summary>
    public sealed class SaveCommitResult
    {
        /// <summary>The new file is at the target path.</summary>
        public bool Success { get; init; }

        /// <summary>Why the commit failed; null on success.</summary>
        public string Error { get; init; }

        /// <summary>Where the finished-but-uncommitted temp file still sits; null when consumed.</summary>
        public string TempPathKept { get; init; }

        /// <summary>
        /// Where the original ended up when it could not be put back; null when the original is
        /// safe at the target (or never existed).
        /// </summary>
        public string OriginalMovedTo { get; init; }
    }

    /// <summary>
    /// Puts a fully written temp file onto its target path without ever deleting the original
    /// first. The old flow was Delete(original) then Move(temp): a failure between the two left
    /// no file at the target at all. Here the original is moved aside as a backup, the temp is
    /// moved in, and only then is the backup dropped - every failure path leaves either the
    /// original or the new file at the target, and reports where everything is.
    /// </summary>
    public static class SaveFileCommit
    {
        public static SaveCommitResult Replace(string tempPath, string targetPath)
        {
            if (string.IsNullOrEmpty(tempPath) || !File.Exists(tempPath))
                return new SaveCommitResult { Success = false, Error = "找不到已寫好的暫存檔。" };

            string backupPath = null;
            try
            {
                if (File.Exists(targetPath))
                {
                    backupPath = UniquePath(targetPath + ".bak");
                    // The original leaves the target name but stays on disk, next to it.
                    File.Move(targetPath, backupPath);
                }
            }
            catch (Exception ex)
            {
                // Nothing has changed: the original is still at the target, the temp still exists.
                return new SaveCommitResult
                {
                    Success = false,
                    Error = "無法移開原有檔案：" + ex.Message,
                    TempPathKept = tempPath
                };
            }

            try
            {
                File.Move(tempPath, targetPath);
            }
            catch (Exception ex)
            {
                // Put the original back so the target path is never left empty.
                if (backupPath != null)
                {
                    try
                    {
                        File.Move(backupPath, targetPath);
                        backupPath = null;
                    }
                    catch
                    {
                        // Original could not be restored - report exactly where it is.
                    }
                }
                return new SaveCommitResult
                {
                    Success = false,
                    Error = "無法將新檔案放到目標位置：" + ex.Message,
                    TempPathKept = File.Exists(tempPath) ? tempPath : null,
                    OriginalMovedTo = backupPath
                };
            }

            if (backupPath != null)
            {
                try { File.Delete(backupPath); }
                catch
                {
                    // The save itself succeeded; a backup that refuses to delete is left behind
                    // rather than turned into a failure.
                }
            }
            return new SaveCommitResult { Success = true };
        }

        private static string UniquePath(string path)
        {
            string candidate = path;
            for (int i = 2; File.Exists(candidate); i++)
                candidate = path + i;
            return candidate;
        }
    }
}
