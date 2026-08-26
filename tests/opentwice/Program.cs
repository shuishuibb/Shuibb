using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using HaRepacker;
using HaRepacker.GUI;

namespace opentwice
{
    /// <summary>
    /// Two related bugs live here, both about MainForm.OpenFileInternal's duplicate handling:
    ///
    /// 1. "開兩個string會抱錯" - opening a String.wz that is already loaded threw an
    ///    InvalidOperationException inside Parallel.ForEach, which propagated out of the async
    ///    void OpenFileInternal and crashed the whole app with the raw, unhandled .NET exception
    ///    dialog. Fixed by having WzFileManager.LoadWzFile report "already loaded" as null instead
    ///    of throwing (phases 1-2 below).
    ///
    /// 2. "只能開啟一個 String WZ" - the fix for #1 keys "already loaded" off the WZ's full path
    ///    (WzFileManager.GetWzKey only strips a ".wz" suffix, nothing else), so it must not be
    ///    confused by two DIFFERENT files that merely share a name. Phases 3-5 open a second,
    ///    unrelated String_000.wz from a different folder, and a third String_001.wz, and check
    ///    each one gets its own WzFile and its own tree node rather than being skipped as a
    ///    false-positive duplicate.
    /// </summary>
    static class Program
    {
        const string Root = @"D:\3.私服檔案\技術谷4.0";
        static readonly string FixtureRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\data\multistr_fixture"));

        static string logPath;
        static MainForm form;
        static int passed, failed;
        static volatile bool unhandledCaught;
        static string unhandledMessage;

        static int TreeNodeCount()
        {
            try
            {
                object panel = typeof(MainForm)
                    .GetField("MainPanel", BindingFlags.NonPublic | BindingFlags.Instance)
                    .GetValue(form);
                object tree = panel.GetType().GetProperty("DataTree").GetValue(panel);
                object nodes = tree.GetType().GetProperty("Nodes").GetValue(tree);
                return (int)nodes.GetType().GetProperty("Count").GetValue(nodes);
            }
            catch { return -1; }
        }

        static readonly List<string> dialogTexts = new List<string>();
        static readonly object dialogLock = new object();
        static volatile bool watcherRunning = true;

        [STAThread]
        static void Main(string[] args)
        {
            logPath = args.Length > 0 ? args[0]
                : Path.Combine(AppContext.BaseDirectory, "opentwice.txt");
            File.WriteAllText(logPath, "");

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                unhandledCaught = true;
                unhandledMessage = "AppDomain: " + e.ExceptionObject;
                Log("!!! UNHANDLED (AppDomain): " + e.ExceptionObject);
            };

            System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.ThreadException += (s, e) =>
            {
                unhandledCaught = true;
                unhandledMessage = "ThreadException: " + e.Exception;
                Log("!!! UNHANDLED (Forms.ThreadException): " + e.Exception);
            };

            HaRepacker.Program.PrepareApplication(true);
            __ProtectUserSettings();
            HaRepacker.Program.ConfigurationManager.UserSettings.SuppressWarnings = true;
            HaRepacker.Program.ConfigurationManager.UserSettings.AutoloadRelatedWzFiles = false;

            StartDialogWatcher();

            var app = new System.Windows.Application();
            app.ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose;
            app.DispatcherUnhandledException += (s, e) =>
            {
                unhandledCaught = true;
                unhandledMessage = "DispatcherUnhandledException: " + e.Exception;
                Log("!!! UNHANDLED (Dispatcher): " + e.Exception);
                e.Handled = true; // keep the harness alive long enough to report the failure
            };

            form = new MainForm(null, false, false);

            string rootString = Path.Combine(Root, @"Data\Lang\zh_TW\String\String_000.wz");
            string fixtureA000 = Path.Combine(FixtureRoot, @"A\String_000.wz");
            string fixtureB000 = Path.Combine(FixtureRoot, @"B\String_000.wz");
            string fixtureA001 = Path.Combine(FixtureRoot, @"A\String_001.wz");
            string fixtureC000 = Path.Combine(FixtureRoot, @"C\String_000.wz");
            string fixtureA002 = Path.Combine(FixtureRoot, @"A\String_002.wz");
            string fixtureBroken = Path.Combine(FixtureRoot, @"bad\String_broken.wz");
            string fixtureA003 = Path.Combine(FixtureRoot, @"A\String_003.wz");
            foreach (var f in new[] { fixtureA000, fixtureB000, fixtureA001, fixtureC000, fixtureA002, fixtureBroken })
                Check("fixture exists: " + f, File.Exists(f), f);
            Log("root target   : " + rootString);
            Log("fixture A 000 : " + fixtureA000);
            Log("fixture B 000 : " + fixtureB000);
            Log("fixture A 001 : " + fixtureA001);
            Log("fixture C 000 : " + fixtureC000);
            Log("fixture A 002 : " + fixtureA002);
            Log("fixture broken: " + fixtureBroken);
            Log("fixture A 003 : " + fixtureA003 + " (created on the fly for the recovery check)");
            Log("");
            // A truncated/unparseable file must not be able to take the app down with it, and the
            // app must still work normally for the NEXT open afterwards - copy this fresh each run
            // so a leftover from a previous run can't mask the recovery check.
            File.Copy(rootString, fixtureA003, overwrite: true);

            MethodInfo open = typeof(MainForm).GetMethod("OpenFileInternal", BindingFlags.NonPublic | BindingFlags.Instance);
            Check("found OpenFileInternal via reflection", open != null);

            // Each phase opens one or more paths in a SINGLE OpenFileInternal call (multi-select
            // opens more than one path at once, exactly like selecting several files in the
            // Explorer dialog) and states what should happen to the file count and tree node count
            // relative to the phase before it. "same-file" duplicates must change neither (bug #1,
            // already fixed); every other phase opens genuinely different files - by path even
            // when the *name* collides - and must grow both by exactly the number of new paths
            // (bug #2, what this round fixes). The last phase opens two brand-new files together
            // to also cover Parallel.ForEach loading them concurrently.
            var plan = new List<(string Label, string[] Paths, int FileDelta, int TreeDelta, bool ExpectFailure)>
            {
                ("first open (clean load)", new[] { rootString }, 1, 1, false),
                ("duplicate open of the SAME path (bug #1 - must not crash, must not reload)", new[] { rootString }, 0, 0, false),
                ("different path, same filename 'String_000.wz' (bug #2 case B)", new[] { fixtureA000 }, 1, 1, false),
                ("a THIRD path, same filename again", new[] { fixtureB000 }, 1, 1, false),
                ("different filename 'String_001.wz' (bug #2 case C)", new[] { fixtureA001 }, 1, 1, false),
                ("multi-select: two more distinct String WZ opened in ONE call", new[] { fixtureC000, fixtureA002 }, 2, 2, false),
                // A file that fails to PARSE (wrong encryption, truncated, wrong format, ...) is a
                // different throw site than the "already loaded" one bug #1 fixed - LoadWzFile's
                // own parse-failure branch is not wrapped in any try/catch inside OpenFileInternal's
                // Parallel.ForEach, so it must not be allowed to reach that unhandled either.
                ("a WZ that fails to parse must not crash the app", new[] { fixtureBroken }, 0, 0, true),
                ("recovery: a normal open still works right after a parse failure", new[] { fixtureA003 }, 1, 1, false),
            };

            int phase = 0;
            int filesBefore = -1, treeBefore = -1;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
            timer.Tick += (s, e) =>
            {
                try
                {
                    // Waiting for the previous phase's open to settle before starting the next one.
                    if (filesBefore < 0)
                    {
                        var (label, paths, _, _, _) = plan[phase];
                        Log("--- phase " + (phase + 1) + "/" + plan.Count + ": " + label + " ---");
                        foreach (var p in paths) Log("  opening: " + p);
                        filesBefore = HaRepacker.Program.WzFileManager?.WzFileList.Count ?? 0;
                        treeBefore = TreeNodeCount();
                        ClearDialogs();
                        unhandledCaught = false;
                        open.Invoke(form, new object[] { paths });
                        clock.Restart();
                        return;
                    }

                    var current = plan[phase];
                    int filesNow = HaRepacker.Program.WzFileManager?.WzFileList.Count ?? 0;
                    int treeNow = TreeNodeCount();
                    // The >800ms floor matters for a zero-delta phase (the same-path duplicate):
                    // the counts trivially "match" on the very first tick, before the async open
                    // has had any real chance to run - which would let an exception on a
                    // background thread finish AFTER this phase already declared success.
                    bool settled = filesNow == filesBefore + current.FileDelta
                        && treeNow == treeBefore + current.TreeDelta
                        && clock.ElapsedMilliseconds > 800;
                    bool timedOut = clock.ElapsedMilliseconds > 15000;
                    if (!settled && !timedOut) return; // still loading, keep waiting

                    Check(current.Label + ": file count changed by " + current.FileDelta,
                        filesNow == filesBefore + current.FileDelta,
                        "before=" + filesBefore + " after=" + filesNow);
                    Check(current.Label + ": tree node count changed by " + current.TreeDelta,
                        treeNow == treeBefore + current.TreeDelta,
                        "before=" + treeBefore + " after=" + treeNow);
                    if (current.ExpectFailure)
                    {
                        foreach (var p in current.Paths)
                            Check(current.Label + ": " + Path.GetFileName(p) + " (" + p + ") correctly did NOT get loaded",
                                !HaRepacker.Program.WzFileManager.IsWzFileLoaded(p));
                        // The parse failure must surface as ONE friendly notice, not silence and
                        // not the raw crash dialog - that distinction is the entire point of the fix.
                        Check(current.Label + ": a friendly notice was shown instead of crashing",
                            DialogCount() > 0);
                        string failureText = DialogCount() > 0 ? DialogTextAt(0) : "(none)";
                        Check(current.Label + ": the notice names the file that failed",
                            failureText.IndexOf(Path.GetFileName(current.Paths[0]), StringComparison.OrdinalIgnoreCase) >= 0,
                            failureText);
                    }
                    else
                    {
                        foreach (var p in current.Paths)
                            Check(current.Label + ": " + Path.GetFileName(p) + " (" + p + ") is now loaded",
                                HaRepacker.Program.WzFileManager.IsWzFileLoaded(p));
                        Check(current.Label + ": no error dialog blocked the run",
                            DialogCount() == 0, DialogCount() > 0 ? DialogTextAt(0) : null);
                    }
                    // This is the actual point of BOTH kinds of phase: neither an already-loaded
                    // duplicate nor a genuine parse failure may reach here as an unhandled exception
                    // - only the friendly-notice path above is allowed to know about a failure.
                    Check(current.Label + ": no unhandled exception escaped",
                        !unhandledCaught, unhandledMessage);

                    phase++;
                    if (phase >= plan.Count)
                    {
                        // Final cross-check: every distinct path opened above must STILL be
                        // loaded, independently, after all the others - nothing evicted a sibling.
                        Check("root String_000.wz is still loaded", HaRepacker.Program.WzFileManager.IsWzFileLoaded(rootString));
                        Check("fixture A String_000.wz is still loaded", HaRepacker.Program.WzFileManager.IsWzFileLoaded(fixtureA000));
                        Check("fixture B String_000.wz is still loaded", HaRepacker.Program.WzFileManager.IsWzFileLoaded(fixtureB000));
                        Check("fixture A String_001.wz is still loaded", HaRepacker.Program.WzFileManager.IsWzFileLoaded(fixtureA001));
                        Check("fixture C String_000.wz is still loaded", HaRepacker.Program.WzFileManager.IsWzFileLoaded(fixtureC000));
                        Check("fixture A String_002.wz is still loaded", HaRepacker.Program.WzFileManager.IsWzFileLoaded(fixtureA002));
                        Check("fixture A String_003.wz (the post-failure recovery open) is loaded",
                            HaRepacker.Program.WzFileManager.IsWzFileLoaded(fixtureA003));
                        Check("the broken fixture never made it in", !HaRepacker.Program.WzFileManager.IsWzFileLoaded(fixtureBroken));
                        Check("seven distinct String WZ files ended up loaded, not fewer",
                            (HaRepacker.Program.WzFileManager?.WzFileList.Count ?? 0) == 7,
                            "count=" + (HaRepacker.Program.WzFileManager?.WzFileList.Count ?? 0));
                        Quit(timer);
                        return;
                    }
                    filesBefore = -1; // trigger the next phase's open on the following tick
                }
                catch (Exception ex)
                {
                    Log("!!! THREW while driving the test: " + ex);
                    Quit(timer);
                }
            };
            timer.Start();
            app.Run(form);
        }

        // ---- native-window watcher: finds and dismisses any MessageBox our own process shows,
        // instead of letting it block the harness forever waiting for a human to click OK ----

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        const uint WM_CLOSE = 0x0010;

        /// <summary>The caption only says "Warning" - the actual message lives in a child
        /// "Static" control, which is what we actually need to verify against.</summary>
        static string GetDialogBodyText(IntPtr dialogHwnd)
        {
            var parts = new List<string>();
            EnumChildWindows(dialogHwnd, (hWnd, lParam) =>
            {
                var cls = new StringBuilder(256);
                GetClassName(hWnd, cls, 256);
                if (cls.ToString() == "Static")
                {
                    var sb = new StringBuilder(2048);
                    GetWindowText(hWnd, sb, 2048);
                    if (sb.Length > 0) parts.Add(sb.ToString());
                }
                return true;
            }, IntPtr.Zero);
            return string.Join(" | ", parts);
        }

        static void StartDialogWatcher()
        {
            uint pid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            var seen = new HashSet<IntPtr>();
            var t = new Thread(() =>
            {
                while (watcherRunning)
                {
                    try
                    {
                        EnumWindows((hWnd, lParam) =>
                        {
                            GetWindowThreadProcessId(hWnd, out uint wpid);
                            if (wpid == pid && !seen.Contains(hWnd))
                            {
                                var cls = new StringBuilder(256);
                                GetClassName(hWnd, cls, 256);
                                if (cls.ToString() == "#32770") // standard Win32 dialog/MessageBox class
                                {
                                    seen.Add(hWnd);
                                    var sb = new StringBuilder(1024);
                                    GetWindowText(hWnd, sb, 1024);
                                    string body = GetDialogBodyText(hWnd);
                                    lock (dialogLock) { dialogTexts.Add(body); }
                                    Log("  [watcher] dialog appeared: title=\"" + sb + "\" body=\"" + body + "\" - dismissing it");
                                    PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                                }
                            }
                            return true;
                        }, IntPtr.Zero);
                    }
                    catch { /* best-effort */ }
                    Thread.Sleep(80);
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        static int DialogCount() { lock (dialogLock) { return dialogTexts.Count; } }
        static string DialogTextAt(int i) { lock (dialogLock) { return dialogTexts[i]; } }
        static void ClearDialogs() { lock (dialogLock) { dialogTexts.Clear(); } }

        static void Check(string label, bool ok, string detail = null)
        {
            if (ok) { passed++; Log("  [ok]   " + label); }
            else { failed++; Log("  [FAIL] " + label + (detail != null ? "  -> " + detail : "")); }
        }

        static void Quit(DispatcherTimer timer)
        {
            timer.Stop();
            watcherRunning = false;
            Log("");
            Log("=== passed " + passed + ", failed " + failed + " ===");
            __RestoreUserSettings();
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        static void Log(string text)
        {
            File.AppendAllText(logPath, text + Environment.NewLine);
        }

        static string __settingsPath;
        static byte[] __settingsBackup;

        static void __ProtectUserSettings()
        {
            try
            {
                __settingsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "HaRepacker", "Settings.txt");
                if (File.Exists(__settingsPath))
                    __settingsBackup = File.ReadAllBytes(__settingsPath);
                AppDomain.CurrentDomain.ProcessExit += delegate { __RestoreUserSettings(); };
            }
            catch { }
        }

        static void __RestoreUserSettings()
        {
            try
            {
                if (__settingsBackup != null && __settingsPath != null)
                    File.WriteAllBytes(__settingsPath, __settingsBackup);
            }
            catch { }
        }
    }
}
