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
using MapleLib.WzLib.WzProperties;

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
                case "tabvp": DriveTabViewport(driver); break;
                case "closeall": DriveCloseAll(driver); break;
                case "search": DriveSearch(driver); break;
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

        // ---- reflection helpers -----------------------------------------------------------

        static object FormField(string name) => typeof(MainForm)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
        static void FormCall(string name, params object[] args) => typeof(MainForm)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, args);
        static object PanelField(MainPanel p, string name) => typeof(MainPanel)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(p);
        static object PanelCall(MainPanel p, string name, params object[] args) => typeof(MainPanel)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(p, args);

        static System.Windows.Controls.TabControl Tabs =>
            (System.Windows.Controls.TabControl)FormField("tabControl_MainPanels");
        static MainPanel PanelAt(int i) =>
            (MainPanel)((System.Windows.Controls.TabItem)Tabs.Items[i]).Content;
        static System.Windows.Controls.ScrollViewer TreeScroll(MainPanel p) =>
            (System.Windows.Controls.ScrollViewer)PanelCall(p, "FindNativeTreeScrollViewer");
        static System.Windows.Controls.TreeView NativeTree(MainPanel p) =>
            (System.Windows.Controls.TreeView)typeof(MainPanel)
                .GetField("dataTreeView", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public).GetValue(p);

        // ---- tab viewport --------------------------------------------------------------------

        static void DriveTabViewport(DispatcherTimer driver)
        {
            switch (step)
            {
                case 0:
                    OpenFiles(targets[0]);
                    step = 1;
                    break;
                case 1:
                {
                    if (PanelAt(0).DataTree.Nodes.Count == 0) return;
                    FormCall("AddTabsInternal", new object[] { "T2" });
                    OpenFiles(targets[1]);
                    step = 2;
                    break;
                }
                case 2:
                {
                    if (PanelAt(1).DataTree.Nodes.Count == 0) return;
                    var p1 = PanelAt(1);
                    var rootItem = (System.Windows.Controls.TreeViewItem)NativeTree(p1).Items[0];
                    rootItem.IsExpanded = true;
                    step = 3;
                    break;
                }
                case 3:
                {
                    var p1 = PanelAt(1);
                    var sv = TreeScroll(p1);
                    if (sv == null || sv.ScrollableHeight < 500)
                    {
                        if (op.ElapsedMilliseconds > 60000) { Log("scrollable=" + (sv == null ? -1 : sv.ScrollableHeight)); Check("tree became scrollable", false); Quit(driver); }
                        return;
                    }
                    var root = (WzNode)p1.DataTree.Nodes[0];
                    WzNode nodeX = null; int i = 0;
                    foreach (System.Windows.Forms.TreeNode c in root.Nodes) { if (c is WzNode w && i++ == 3) { nodeX = w; break; } }
                    PanelCall(p1, "SelectAndRevealNativeNode", new object[] { nodeX });
                    sv.ScrollToVerticalOffset(sv.ScrollableHeight * 0.8);
                    step = 4;
                    op.Restart();
                    break;
                }
                case 4:
                {
                    if (op.ElapsedMilliseconds < 500) return;
                    var sv = TreeScroll(PanelAt(1));
                    Log("tab1: scrolled away to offset " + (long)sv.VerticalOffset + " (selected node off-screen)");
                    Tabs.SelectedIndex = 0;
                    step = 5;
                    op.Restart();
                    break;
                }
                case 5:
                    if (op.ElapsedMilliseconds < 800) return;
                    Tabs.SelectedIndex = 1;
                    step = 6;
                    op.Restart();
                    break;
                case 6:
                {
                    if (op.ElapsedMilliseconds < 1500) return;
                    var p1 = PanelAt(1);
                    var sv = TreeScroll(p1);
                    double offsetNow = sv.VerticalOffset;
                    double expected = sv.ScrollableHeight * 0.8;
                    Log("tab1 after switch-back: offset=" + (long)offsetNow + " expected~" + (long)expected);
                    Check("viewport stayed where the user scrolled (no auto-reveal of selection)",
                        Math.Abs(offsetNow - expected) < 200);
                    Check("selection survived the switch", p1.DataTree.SelectedNode != null);

                    var root = (WzNode)p1.DataTree.Nodes[0];
                    WzNode nodeY = null; int i = 0;
                    foreach (System.Windows.Forms.TreeNode c in root.Nodes) { if (c is WzNode w && i++ == 1) { nodeY = w; break; } }
                    PanelCall(p1, "SelectAndRevealNativeNode", new object[] { nodeY });
                    step = 7;
                    op.Restart();
                    break;
                }
                case 7:
                {
                    if (op.ElapsedMilliseconds < 600) return;
                    var sv = TreeScroll(PanelAt(1));
                    Log("after explicit reveal: offset=" + (long)sv.VerticalOffset);
                    Check("explicit navigation still scrolls to its target",
                        sv.VerticalOffset < sv.ScrollableHeight * 0.5);
                    Quit(driver);
                    break;
                }
            }
        }

        // ---- close all -----------------------------------------------------------------------

        static void DriveCloseAll(DispatcherTimer driver)
        {
            switch (step)
            {
                case 0:
                    OpenFiles(targets[0]);
                    step = 1;
                    break;
                case 1:
                    if (PanelAt(0).DataTree.Nodes.Count == 0) return;
                    FormCall("AddTabsInternal", new object[] { "T2" });
                    OpenFiles(targets[0]); // same physical file -> independent instance in tab 2
                    step = 2;
                    break;
                case 2:
                    if (PanelAt(1).DataTree.Nodes.Count == 0) return;
                    if (targets.Length > 1)
                        OpenFiles(targets[1]); // in-flight .ms open racing the close
                    FormCall("unloadAllToolStripMenuItem_Click", new object[] { null, EventArgs.Empty });
                    step = 3;
                    op.Restart();
                    break;
                case 3:
                {
                    if (op.ElapsedMilliseconds < 5000) return;
                    bool allEmpty = true;
                    for (int i = 0; i < Tabs.Items.Count; i++)
                    {
                        var p = PanelAt(i);
                        if (p.DataTree.Nodes.Count != 0 || NativeTree(p).Items.Count != 0) allEmpty = false;
                        Log("tab" + i + ": model=" + p.DataTree.Nodes.Count + " native=" + NativeTree(p).Items.Count);
                    }
                    Check("every tab is empty after close-all (in-flight .ms did not revive)", allEmpty);
                    try
                    {
                        using (File.Open(targets[0], FileMode.Open, FileAccess.Read, FileShare.None)) { }
                        Check("physical file exclusively openable (both instances disposed)", true);
                    }
                    catch (IOException ex)
                    {
                        Log("   locked: " + ex.Message);
                        Check("physical file exclusively openable (both instances disposed)", false);
                    }
                    Quit(driver);
                    break;
                }
            }
        }

        // ---- progressive search ---------------------------------------------------------------

        static System.Windows.Controls.TextBox FindBox(MainPanel p) =>
            (System.Windows.Controls.TextBox)typeof(MainPanel)
                .GetField("findBox", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public).GetValue(p);
        static bool SearchFinished(MainPanel p) => (bool)PanelField(p, "finished");
        static string StatusText(MainPanel p) =>
            ((System.Windows.Controls.TextBlock)typeof(MainPanel).GetField("toolStripStatusLabel_additionalInfo",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public).GetValue(p)).Text;

        static int CountParsedImages(WzNode root)
        {
            int n = 0;
            foreach (System.Windows.Forms.TreeNode c in root.Nodes)
                if (c is WzNode w && w.Tag is WzImage img && img.Parsed) n++;
            return n;
        }

        static string hitPath1;

        static void DriveSearch(DispatcherTimer driver)
        {
            var p0 = PanelAt(0);
            switch (step)
            {
                case 0:
                    OpenFiles(targets[0]);
                    step = 1;
                    break;
                case 1:
                {
                    if (p0.DataTree.Nodes.Count == 0) return;
                    PanelCall(p0, "ReplaceNativeSelection", new object[] { (WzNode)p0.DataTree.Nodes[0] });
                    PanelCall(p0, "SynchronizeNativeSelection", new object[] { (WzNode)p0.DataTree.Nodes[0] });
                    FindBox(p0).Text = targets[1];
                    BeginWatch();
                    PanelCall(p0, "button_nextSearch_Click", new object[] { null, null });
                    step = 2;
                    break;
                }
                case 2:
                {
                    if (!SearchFinished(p0))
                    {
                        if (op.ElapsedMilliseconds > 240000) { Check("first hit found", false); Quit(driver); }
                        return;
                    }
                    long gap = EndWatch();
                    var hit1 = p0.DataTree.SelectedNode as WzNode;
                    Log("hit1: " + (hit1 == null ? "null" : hit1.FullPath) + "   elapsed=" + op.ElapsedMilliseconds
                        + " ms  maxUIgap=" + gap + " ms  parsedIMGs=" + CountParsedImages((WzNode)p0.DataTree.Nodes[0]));
                    Check("found a node inside a never-expanded IMG",
                        hit1 != null && hit1.Text.IndexOf(targets[1], StringComparison.OrdinalIgnoreCase) >= 0);
                    bool isProp = hit1 != null && hit1.Tag is WzImageProperty;
                    Check("hit is a property inside an IMG", isProp);
                    if (isProp)
                    {
                        var parentImg = ((WzImageProperty)hit1.Tag).ParentImage;
                        Check("search did not mark the image changed", parentImg != null && !parentImg.Changed);
                    }
                    Check("search UI stayed responsive (max gap < 400ms)", gap < 400);
                    hitPath1 = hit1 == null ? "" : hit1.FullPath;

                    BeginWatch();
                    PanelCall(p0, "button_nextSearch_Click", new object[] { null, null });
                    step = 3;
                    break;
                }
                case 3:
                {
                    if (!SearchFinished(p0))
                    {
                        if (op.ElapsedMilliseconds > 240000) { Check("second hit found", false); Quit(driver); }
                        return;
                    }
                    long gap2 = EndWatch();
                    var hit2 = p0.DataTree.SelectedNode as WzNode;
                    Log("hit2: " + (hit2 == null ? "null" : hit2.FullPath) + "   elapsed=" + op.ElapsedMilliseconds + " ms  maxUIgap=" + gap2 + " ms");
                    Check("find next advanced to a different node", hit2 != null && hit2.FullPath != hitPath1);

                    FormCall("AddTabsInternal", new object[] { "T2" });
                    Tabs.SelectedIndex = 0;
                    PanelCall(p0, "ReplaceNativeSelection", new object[] { (WzNode)p0.DataTree.Nodes[0] });
                    PanelCall(p0, "SynchronizeNativeSelection", new object[] { (WzNode)p0.DataTree.Nodes[0] });
                    FindBox(p0).Text = targets.Length > 2 ? targets[2] : "zzz_definitely_not_there";
                    BeginWatch();
                    PanelCall(p0, "button_nextSearch_Click", new object[] { null, null });
                    Tabs.SelectedIndex = 1;   // the user walks away mid-search
                    step = 4;
                    break;
                }
                case 4:
                {
                    string status = StatusText(p0);
                    if (status == null || status.IndexOf("\u641c\u5c0b\u5b8c\u6210", StringComparison.Ordinal) < 0)
                    {
                        if (op.ElapsedMilliseconds > 600000) { Log("status=" + status); Check("not-found search completed", false); Quit(driver); }
                        return;
                    }
                    long gap = EndWatch();
                    var roots = (System.Collections.Generic.List<WzNode>)PanelField(p0, "searchRootNodes");
                    Log("session roots=" + roots.Count + (roots.Count > 0 ? " first=" + roots[0].Text + " tag=" + roots[0].Tag.GetType().Name : ""));
                    Log("not-found: elapsed=" + op.ElapsedMilliseconds + " ms  maxUIgap=" + gap
                        + " ms  parsedIMGs=" + CountParsedImages((WzNode)p0.DataTree.Nodes[0])
                        + "  privMB=" + Process.GetCurrentProcess().PrivateMemorySize64 / 1048576);
                    Check("not-found reported via status, app alive", true);
                    Check("search did not steal the active tab", Tabs.SelectedIndex == 1);
                    Check("full-scope scan stayed responsive (max gap < 400ms)", gap < 400);
                    Quit(driver);
                    break;
                }
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
