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
    /// Reproduces the reported crash exactly: "開兩個string會抱錯". Opening a String.wz that is
    /// already loaded threw an InvalidOperationException inside Parallel.ForEach, which propagated
    /// out of the async void OpenFileInternal and crashed the whole app with the raw, unhandled
    /// .NET exception dialog. Drives the REAL MainForm.OpenFileInternal (the freshly-built host,
    /// not yet deployed) twice with the same real String_000.wz and checks that the second open
    /// degrades to a friendly notice instead of taking the process down.
    /// </summary>
    static class Program
    {
        const string Root = @"D:\3.私服檔案\技術谷4.0";

        static string logPath;
        static MainForm form;
        static int passed, failed;
        static volatile bool unhandledCaught;
        static string unhandledMessage;

        static int treeCountAfterFirstOpen = -1;
        static int filesAfterFirstOpen = -1;

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

            string stringWzPath = Path.Combine(Root, @"Data\Lang\zh_TW\String\String_000.wz");
            Log("target: " + stringWzPath);
            Log("");

            MethodInfo open = typeof(MainForm).GetMethod("OpenFileInternal", BindingFlags.NonPublic | BindingFlags.Instance);
            Check("found OpenFileInternal via reflection", open != null);

            int step = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
            timer.Tick += (s, e) =>
            {
                try
                {
                    if (step == 0)
                    {
                        Log("--- first open (clean load) ---");
                        open.Invoke(form, new object[] { new string[] { stringWzPath } });
                        step = 1;
                        clock.Restart();
                        return;
                    }
                    if (step == 1)
                    {
                        bool loaded = HaRepacker.Program.WzFileManager != null
                            && HaRepacker.Program.WzFileManager.IsWzFileLoaded(stringWzPath);
                        if (loaded && TreeNodeCount() > 0)
                        {
                            Check("first open loaded String_000.wz", true);
                            Check("no dialog appeared on the clean first open", DialogCount() == 0, "saw " + DialogCount());
                            Check("no unhandled exception on the clean first open", !unhandledCaught, unhandledMessage);
                            treeCountAfterFirstOpen = TreeNodeCount();
                            filesAfterFirstOpen = HaRepacker.Program.WzFileManager.WzFileList.Count;
                            Log("  tree root nodes = " + treeCountAfterFirstOpen
                                + ", loaded wz files = " + filesAfterFirstOpen);
                            Log("");
                            Log("--- second open of the SAME already-loaded file (the reported bug) ---");
                            ClearDialogs();
                            unhandledCaught = false;
                            open.Invoke(form, new object[] { new string[] { stringWzPath } });
                            step = 2;
                            clock.Restart();
                            return;
                        }
                        if (clock.ElapsedMilliseconds > 60000) { Log("timed out on first open"); Quit(timer); }
                        return;
                    }
                    if (step == 2)
                    {
                        // Give the second open time to finish (or to blow up, which is the point).
                        if (clock.ElapsedMilliseconds > 8000)
                        {
                            Check("no unhandled exception escaped the duplicate open (the reported crash)",
                                !unhandledCaught, unhandledMessage);
                            Check("no error dialog was shown - the duplicate is simply skipped",
                                DialogCount() == 0, DialogCount() > 0 ? DialogTextAt(0) : null);
                            Check("the file is still loaded",
                                HaRepacker.Program.WzFileManager.IsWzFileLoaded(stringWzPath));
                            Check("it was not loaded a second time",
                                HaRepacker.Program.WzFileManager.WzFileList.Count == filesAfterFirstOpen,
                                "before=" + filesAfterFirstOpen + " after=" + HaRepacker.Program.WzFileManager.WzFileList.Count);
                            Check("the tree gained no duplicate node",
                                TreeNodeCount() == treeCountAfterFirstOpen,
                                "before=" + treeCountAfterFirstOpen + " after=" + TreeNodeCount());
                            Quit(timer);
                        }
                        return;
                    }
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
