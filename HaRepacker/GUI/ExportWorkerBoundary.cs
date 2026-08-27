using System;

namespace HaRepacker.GUI
{
    /// <summary>
    /// The safety boundary every export worker thread runs its work inside. An exception on a
    /// plain background Thread has nowhere to go but AppDomain.UnhandledException, which shows
    /// the crash dialog and exits the whole app - a full disk, an unplugged drive or a denied
    /// output folder used to cost the user every open file. Here it becomes a description the
    /// worker reports once, and the worker's cleanup still runs.
    /// </summary>
    public static class ExportWorkerBoundary
    {
        /// <summary>Runs the export work; null on success, otherwise what went wrong. Never throws.</summary>
        public static string Run(Action work)
        {
            if (work == null)
                return "沒有可執行的匯出工作。";
            try
            {
                work();
                return null;
            }
            catch (OperationCanceledException)
            {
                // The user pressed abort - that is an outcome, not a failure to report.
                return null;
            }
            catch (Exception ex)
            {
                return Describe(ex);
            }
        }

        /// <summary>Best-effort message for a failure - describing an error must never fail.</summary>
        public static string Describe(Exception ex)
        {
            try
            {
                return ex == null ? "未知錯誤。" : ex.Message;
            }
            catch
            {
                return "未知錯誤。";
            }
        }
    }
}
