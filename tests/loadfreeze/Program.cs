using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Threading;
using HaRepacker;
using HaRepacker.GUI;
using HaRepacker.GUI.Panels;
using MapleLib.WzLib;

namespace LoadFreezeHarness
{
    /// <summary>
    /// Dispatcher-integration harness for the round-3 performance work: measures how long the UI
    /// message pump actually stalls while a huge IMG is double-click parsed and while .ms packs
    /// are opened, and exercises the lifecycle races (unload during load, repeated double-click).
    ///
    /// Drives the real MainForm/MainPanel through their own methods - no mouse automation.
    /// Usage: loadfreeze &lt;log&gt; img &lt;wz&gt; &lt;imgName&gt;
    ///        loadfreeze &lt;log&gt; imgunload &lt;wz&gt; &lt;imgName&gt;
    ///        loadfreeze &lt;log&gt; imgdouble &lt;wz&gt; &lt;imgName&gt;
    ///        loadfreeze &lt;log&gt; ms &lt;file1.ms&gt; [file2.ms ...]
    /// </summary>
    static class Program
    {
        static string logPath, mode;
        static string[] targets;
        static MainForm form;
        static MainPanel panel;
        static readonly Stopwatch clock = new Stopwatch();
        static int passed, failed;

        // heartbeat
        static readonly Stopwatch beat = new Stopwatch();
        static long maxGapMs;
        static bool watchGaps;

        static void Log(string s)
        {
            Console.WriteLine(s);
            try { File.AppendAllText(logPath, s + Environment.NewLine); } catch { }
        }
        static void Check(string what, bool ok)
        {
            if (ok) passed++; else failed++;
            Log("  [" + (ok ? "ok" : "FAIL") + "]   " + what);
        }

        [STAThread]
        static int Main(string[] args)
        {
            logPath = args[0];
            mode = args[1];
            targets = args.Skip(2).ToArray();
            try { File.Delete(logPath); } catch { }

            AppDomain.CurrentDomain.UnhandledException += (s, e) => { Log("!!! UNHANDLED: " + e.ExceptionObject); Environment.Exit(2); };

            System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
            System.Windows.Forms.Application.EnableVisualStyles();
            HaRepacker.Program.PrepareApplication(true);
            HaRepacker.Program.ConfigurationManager.UserSettings.SuppressWarnings = true;
            HaRepacker.Program.ConfigurationManager.UserSettings.AutoloadRelatedWzFiles = false;

            var app = new System.Windows.Application();
            app.ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose;
            app.DispatcherUnhandledException += (s, e) => { Log("!!! DISPATCHER: " + e.Exception); e.Handled = true; };

            form = new MainForm(null, false, false);
            panel = (MainPanel)typeof(MainForm).GetField("MainPanel", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);

            // 15ms heartbeat; while watchGaps, any tick-to-tick gap is recorded. A synchronous
            // 1.5s parse on the UI thread shows up as a ~1.5s max gap.
            var heart = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(15) };
            heart.Tick += (s, e) =>
            {
                if (watchGaps && beat.IsRunning)
                {
                    long gap = beat.ElapsedMilliseconds;
                    if (gap > maxGapMs) maxGapMs = gap;
                    if (gap > 100) Log("    gap " + gap + " ms at t+" + op.ElapsedMilliseconds + " ms");
                }
                beat.Restart();
            };
            heart.Start();

            var driver = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
            driver.Tick += (s, e) => Drive(driver);
            form.Loaded += (s, e) => { clock.Start(); driver.Start(); };
            app.Run(form);
            return failed == 0 ? 0 : 1;
        }

        static int step = 0;
        static WzNode imgNode;
        static Stopwatch op = new Stopwatch();

        static void OpenFiles(params string[] paths)
        {
            MethodInfo open = typeof(MainForm).GetMethod("OpenFileInternal", BindingFlags.NonPublic | BindingFlags.Instance);
            open.Invoke(form, new object[] { paths });
        }

        static WzNode FindImgNode(string name)
        {
            if (panel.DataTree.Nodes.Count == 0) return null;
            foreach (System.Windows.Forms.TreeNode c in ((WzNode)panel.DataTree.Nodes[0]).Nodes)
                if (c is WzNode w && string.Equals(w.Text, name, StringComparison.OrdinalIgnoreCase))
                    return w;
            return null;
        }

        static void DoubleClickNode(WzNode node)
        {
            panel.DataTree.SelectedNode = node;
            MethodInfo dbl = typeof(MainPanel).GetMethod("DataTree_DoubleClick", BindingFlags.NonPublic | BindingFlags.Instance);
            dbl.Invoke(panel, new object[] { panel, EventArgs.Empty });
        }

        static void BeginWatch() { maxGapMs = 0; beat.Restart(); watchGaps = true; op.Restart(); }
        static long EndWatch() { watchGaps = false; op.Stop(); return maxGapMs; }

        static void Drive(DispatcherTimer driver)
        {
            try { DriveCore(driver); }
            catch (Exception ex) { Log("!!! THREW: " + ex); Quit(driver); }
        }

        static void DriveCore(DispatcherTimer driver)
        {
            switch (mode)
            {
                case "img": DriveImg(driver, unloadMidway: false, doubleTap: false); break;
                case "imgunload": DriveImg(driver, unloadMidway: true, doubleTap: false); break;
                case "imgdouble": DriveImg(driver, unloadMidway: false, doubleTap: true); break;
                case "ms": DriveMs(driver); break;
                default: Log("unknown mode"); Quit(driver); break;
            }
        }

        static void DriveImg(DispatcherTimer driver, bool unloadMidway, bool doubleTap)
        {
            switch (step)
            {
                case 0:
                    OpenFiles(targets[0]);
                    step = 1;
                    break;
                case 1:
                    imgNode = FindImgNode(targets[1]);
                    if (imgNode == null)
                    {
                        if (clock.ElapsedMilliseconds > 120000) { Log("timed out waiting for tree"); Quit(driver); }
                        return;
                    }
                    Log("img: " + targets[1] + "  blockSize=" + ((WzImage)imgNode.Tag).BlockSize / 1024 + " KB");
                    BeginWatch();
                    DoubleClickNode(imgNode);
                    if (doubleTap)
                    {
                        // Same image, immediately again - must not start a second parse.
                        DoubleClickNode(imgNode);
                        DoubleClickNode(imgNode);
                    }
                    if (unloadMidway)
                    {
                        // Yank the file out from under the running parse.
                        var wzFile = ((WzImage)imgNode.Tag).WzFileParent;
                        form.UnloadWzFile(wzFile);
                        Log("unloaded the file right after starting the parse");
                    }
                    step = 2;
                    break;
                case 2:
                    bool done;
                    try { done = imgNode.Tag is WzImage im && im.Parsed && imgNode.Nodes.Count > 0; }
                    catch { done = false; }

                    if (unloadMidway)
                    {
                        // Give the background parse time to finish and hit its liveness check.
                        if (op.ElapsedMilliseconds < 6000) return;
                        long gapU = EndWatch();
                        Check("no crash / no dispatcher exception after unload during load", true);
                        Check("no ghost children attached to the unloaded tree", panel.DataTree.Nodes.Count == 0);
                        Log("  maxUIgap=" + gapU + " ms");
                        Quit(driver);
                        return;
                    }

                    if (!done)
                    {
                        if (op.ElapsedMilliseconds > 120000) { Log("timed out waiting for parse"); Check("parse completed", false); Quit(driver); }
                        return;
                    }
                    long gap = EndWatch();
                    Log("RESULT total=" + op.ElapsedMilliseconds + " ms   maxUIgap=" + gap + " ms   children=" + imgNode.Nodes.Count);
                    Check("children appeared", imgNode.Nodes.Count > 0);
                    if (doubleTap)
                        Check("image parsed exactly once (no duplicate-parse corruption: children stable)",
                            imgNode.Nodes.Count == ((WzImage)imgNode.Tag).WzProperties.Count);
                    Quit(driver);
                    break;
            }
        }

        static void DriveMs(DispatcherTimer driver)
        {
            switch (step)
            {
                case 0:
                    BeginWatch();
                    OpenFiles(targets);
                    step = 1;
                    break;
                case 1:
                    if (panel.DataTree.Nodes.Count < targets.Length)
                    {
                        if (op.ElapsedMilliseconds > 300000) { Log("timed out: roots=" + panel.DataTree.Nodes.Count); Check("all .ms opened", false); Quit(driver); }
                        return;
                    }
                    long gap = EndWatch();
                    Log("RESULT files=" + targets.Length + " total=" + op.ElapsedMilliseconds + " ms   maxUIgap=" + gap + " ms   privMB=" + Process.GetCurrentProcess().PrivateMemorySize64 / 1048576);
                    Check("all .ms opened as tree roots", panel.DataTree.Nodes.Count == targets.Length);
                    // order preserved?
                    bool ordered = true;
                    for (int i = 0; i < targets.Length; i++)
                        if (!panel.DataTree.Nodes[i].Text.StartsWith(Path.GetFileNameWithoutExtension(targets[i]), StringComparison.OrdinalIgnoreCase))
                            ordered = false;
                    Check("tree order matches selection order", ordered);
                    Quit(driver);
                    break;
            }
        }

        static void Quit(DispatcherTimer driver)
        {
            driver.Stop();
            Log("");
            Log("=== passed " + passed + ", failed " + failed + " ===");
            form.Dispatcher.BeginInvoke(new Action(() => System.Windows.Application.Current.Shutdown(failed == 0 ? 0 : 1)));
        }
    }
}
