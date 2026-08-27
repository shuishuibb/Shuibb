using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Threading;
using HaRepacker;
using HaRepacker.GUI;
using HaRepacker.GUI.Panels;
using MapleLib;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace LinkRepairPerfHarness
{
    /// <summary>
    /// Dispatcher-integration harness for the "_inlink/_outlink 補圖" round: measures how long the
    /// UI message pump actually stalls during a repair, how many times the WPF tree is rebuilt for
    /// one user action, whether the viewport moves, and how long a cold _Canvas section takes to
    /// load at a given degree of parallelism.
    ///
    /// Drives the real MainForm/MainPanel through their own methods - no mouse automation.
    ///
    /// Usage:
    ///   linkrepairperf &lt;log&gt; probe    &lt;wz&gt; [maxImages]
    ///   linkrepairperf &lt;log&gt; repairsync &lt;wz&gt; &lt;maxImages&gt;      (baseline: old synchronous path)
    ///   linkrepairperf &lt;log&gt; repair   &lt;wz&gt; &lt;maxImages&gt; [noprogress]
    ///   linkrepairperf &lt;log&gt; treemut  &lt;wz&gt; &lt;imgName&gt;
    ///   linkrepairperf &lt;log&gt; canvas   &lt;wz&gt; &lt;category&gt; &lt;dop&gt;
    ///   linkrepairperf &lt;log&gt; progress &lt;wz&gt; &lt;maxImages&gt;        (progress lifecycle checks)
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
        static double lastSampledOffset = -1;
        static readonly Stopwatch op = new Stopwatch();

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
            // Structural copy/paste/delete now confirm through Warning.ConfirmRequired, which
            // SuppressWarnings deliberately cannot bypass - every harness mode scripts the
            // answer through the test seam instead.
            InstallConfirmHook();
            HaRepacker.Program.ConfigurationManager.UserSettings.AutoloadRelatedWzFiles = false;

            var app = new System.Windows.Application();
            app.ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose;
            app.DispatcherUnhandledException += (s, e) => { Log("!!! DISPATCHER: " + e.Exception); e.Handled = true; };

            form = new MainForm(null, false, false);
            panel = (MainPanel)typeof(MainForm).GetField("MainPanel", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);

            var heart = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(15) };
            heart.Tick += (s, e) =>
            {
                if (watchGaps && beat.IsRunning)
                {
                    long gap = beat.ElapsedMilliseconds;
                    if (gap > maxGapMs) maxGapMs = gap;
                    if (gap > 150) Log("    gap " + gap + " ms at t+" + op.ElapsedMilliseconds + " ms");
                    if (mode.StartsWith("treemut") && op.ElapsedMilliseconds < 900)
                    {
                        double off = TreeVerticalOffset();
                        if (Math.Abs(off - lastSampledOffset) > 0.5)
                        {
                            Log("    offΔ t+" + op.ElapsedMilliseconds + "ms " + TreeScrollDebug());
                            lastSampledOffset = off;
                        }
                    }
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

        static int step;

        static void OpenFiles(params string[] paths)
        {
            MethodInfo open = typeof(MainForm).GetMethod("OpenFileInternal", BindingFlags.NonPublic | BindingFlags.Instance);
            open.Invoke(form, new object[] { paths });
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
                case "probe": DriveProbe(driver); break;
                case "repairsync": DriveRepair(driver, useAsync: false, progress: true); break;
                case "repair": DriveRepair(driver, useAsync: true, progress: true); break;
                case "repairnoprog": DriveRepair(driver, useAsync: true, progress: false); break;
                case "treemut": DriveTreeMutation(driver); break;
                case "treemut2": DriveTreeMutation(driver); break;
                case "canvas": DriveCanvas(driver); break;
                case "progress": DriveProgress(driver); break;
                case "lifecycle": DriveLifecycle(driver); break;
                case "batch": DriveBatch(driver); break;
                case "rerepair": DriveReRepair(driver); break;
                case "duprate": DriveDupRate(driver); break;
                case "visualstab": DriveVisualStability(driver); break;
                case "profile": DriveProfile(driver); break;
                default: Log("unknown mode " + mode); Quit(driver); break;
            }
        }

        static void Quit(DispatcherTimer driver)
        {
            driver.Stop();
            Log("passed=" + passed + " failed=" + failed);
            try { form.Close(); } catch { }
            System.Windows.Application.Current?.Shutdown();
        }

        // ---------------------------------------------------------------- helpers

        static WzNode RootNode() => panel.DataTree.Nodes.Count > 0 ? (WzNode)panel.DataTree.Nodes[0] : null;

        static bool TreeReady(DispatcherTimer driver)
        {
            if (RootNode() != null && RootNode().Nodes.Count > 0)
                return true;
            if (clock.ElapsedMilliseconds > 180000) { Log("timed out waiting for the tree"); Quit(driver); }
            return false;
        }

        /// <summary>Every WzImage node under the opened file, in tree order.</summary>
        static List<WzNode> AllImageNodes(WzNode root, int max)
        {
            var found = new List<WzNode>();
            var stack = new Stack<WzNode>();
            stack.Push(root);
            while (stack.Count > 0 && found.Count < max)
            {
                WzNode n = stack.Pop();
                if (n.Tag is WzImage) { found.Add(n); continue; }
                for (int i = n.Nodes.Count - 1; i >= 0; i--)
                    if (n.Nodes[i] is WzNode c) stack.Push(c);
            }
            return found;
        }

        static void CountLinks(WzImageProperty prop, ref int inlink, ref int outlink, ref int canvases)
        {
            if (prop is WzCanvasProperty canvas)
            {
                canvases++;
                if (canvas.ContainsInlinkProperty()) inlink++;
                if (canvas.ContainsOutlinkProperty()) outlink++;
                return; // repair never descends into canvas children
            }
            if (prop is not MapleLib.WzLib.IPropertyContainer) return; // UOLs resolve their target - out of scope
            var children = prop.WzProperties;
            if (children == null) return;
            foreach (WzImageProperty child in children)
                CountLinks(child, ref inlink, ref outlink, ref canvases);
        }

        static (int inlink, int outlink, int canvases) CountLinksInImage(WzImage img)
        {
            int i = 0, o = 0, c = 0;
            foreach (WzImageProperty p in img.WzProperties)
                CountLinks(p, ref i, ref o, ref c);
            return (i, o, c);
        }

        static void SelectNodes(IEnumerable<WzNode> nodes)
        {
            var list = new System.Collections.ArrayList();
            foreach (WzNode n in nodes) list.Add(n);
            panel.DataTree.SelectedNodes = list;
            if (list.Count > 0) panel.DataTree.SelectedNode = (WzNode)list[0];
        }

        static long PrivateMb() => Process.GetCurrentProcess().PrivateMemorySize64 / 1048576;

        // ---------------------------------------------------------------- probe

        static void DriveProbe(DispatcherTimer driver)
        {
            switch (step)
            {
                case 0: OpenFiles(targets[0]); step = 1; break;
                case 1:
                    if (!TreeReady(driver)) return;
                    step = 2;
                    break;
                case 2:
                    int max = targets.Length > 1 ? int.Parse(targets[1]) : 200;
                    List<WzNode> imgs = AllImageNodes(RootNode(), max);
                    Log("image nodes examined: " + imgs.Count);
                    var sw = Stopwatch.StartNew();
                    int totalIn = 0, totalOut = 0, totalCanvas = 0, withLinks = 0;
                    var perImage = new List<(string name, int inl, int outl)>();
                    foreach (WzNode n in imgs)
                    {
                        var img = (WzImage)n.Tag;
                        try { if (!img.Parsed) img.ParseImage(); }
                        catch (Exception ex) { Log("  parse failed " + n.Text + ": " + ex.Message); continue; }
                        var (i, o, c) = CountLinksInImage(img);
                        totalIn += i; totalOut += o; totalCanvas += c;
                        if (i + o > 0) { withLinks++; perImage.Add((n.Text, i, o)); }
                    }
                    sw.Stop();
                    Log("parse+scan " + sw.ElapsedMilliseconds + " ms   canvases=" + totalCanvas
                        + "  _inlink=" + totalIn + "  _outlink=" + totalOut + "  imagesWithLinks=" + withLinks
                        + "  privMB=" + PrivateMb());
                    foreach (var e in perImage.OrderByDescending(x => x.inl + x.outl).Take(25))
                        Log("   " + e.name + "  in=" + e.inl + " out=" + e.outl);
                    Quit(driver);
                    break;
            }
        }

        // ---------------------------------------------------------------- repair

        static List<WzNode> repairTargets;
        static int refreshBefore;
        static object asyncResult;
        static double vpBefore;
        static int wzFilesBefore;
        static int syncRepaired, syncFailed;

        static void DriveRepair(DispatcherTimer driver, bool useAsync, bool progress)
        {
            switch (step)
            {
                case 0: OpenFiles(targets[0]); step = 1; break;
                case 1:
                    if (!TreeReady(driver)) return;
                    step = 2;
                    break;
                case 2:
                    {
                        int max = int.Parse(targets[1]);
                        repairTargets = AllImageNodes(RootNode(), max);
                        wzFilesBefore = HaRepacker.Program.WzFileManager?.WzFileList?.Count ?? 0;
                        Log("mode=" + mode + "  selected image nodes: " + repairTargets.Count
                            + "   privMB(before)=" + PrivateMb() + "   wzFilesLoaded(before)=" + wzFilesBefore);
                        SelectNodes(repairTargets);
                        refreshBefore = panel.NativeTreeRefreshCount;
                        vpBefore = TreeVerticalOffset();
                        BeginWatch();
                        if (useAsync)
                        {
                            asyncResult = null;
                            StartAsyncRepair(progress);
                        }
                        else
                        {
                            syncRepaired = 0; syncFailed = 0;
                            foreach (WzNode n in repairTargets)
                                MainPanel.CheckImageNodeRecursively_linkRepair(n, ref syncRepaired, ref syncFailed);
                            op.Stop();
                            // Do NOT stop watching yet: the dispatcher was blocked for the whole
                            // operation, so the stall only shows up on the next heartbeat tick.
                            step = 9;
                            return;
                        }
                        step = 3;
                        break;
                    }
                case 9:
                    {
                        long gapSync = EndWatch();
                        ReportRepair("BASELINE(sync)", syncRepaired, syncFailed, gapSync);
                        Quit(driver);
                        return;
                    }
                case 3:
                    {
                        if (asyncResult == null)
                        {
                            if (op.ElapsedMilliseconds > 600000) { Log("timed out waiting for the async repair"); Check("repair completed", false); Quit(driver); }
                            return;
                        }
                        long gap = EndWatch();
                        var t = asyncResult.GetType();
                        if (asyncResult is string) { Quit(driver); return; }
                        int repaired = (int)t.GetProperty("Repaired").GetValue(asyncResult);
                        int failedLinks = (int)t.GetProperty("Failed").GetValue(asyncResult);
                        ReportRepair(progress ? "ASYNC(progress on)" : "ASYNC(progress off)", repaired, failedLinks, gap);
                        Log("   phases: " + t.GetProperty("PhaseTimings").GetValue(asyncResult));
                        Quit(driver);
                        break;
                    }
            }
        }

        static void ReportRepair(string label, int repaired, int failedLinks, long gap)
        {
            int refreshes = panel.NativeTreeRefreshCount - refreshBefore;
            Log("RESULT " + label
                + "  total=" + op.ElapsedMilliseconds + " ms"
                + "  maxUIgap=" + gap + " ms"
                + "  treeRefreshes=" + refreshes
                + "  repaired=" + repaired
                + "  failed=" + failedLinks
                + "  wzFilesLoaded=" + wzFilesBefore + "->" + (HaRepacker.Program.WzFileManager?.WzFileList?.Count ?? 0)
                + "  privMB=" + PrivateMb());
        }

        static double TreeVerticalOffset()
        {
            MethodInfo m = typeof(MainPanel).GetMethod("FindNativeTreeScrollViewer", BindingFlags.NonPublic | BindingFlags.Instance);
            var viewer = m?.Invoke(panel, null) as System.Windows.Controls.ScrollViewer;
            return viewer?.VerticalOffset ?? -1;
        }

        static string TreeScrollDebug()
        {
            MethodInfo m = typeof(MainPanel).GetMethod("FindNativeTreeScrollViewer", BindingFlags.NonPublic | BindingFlags.Instance);
            var viewer = m?.Invoke(panel, null) as System.Windows.Controls.ScrollViewer;
            if (viewer == null) return "noviewer";
            return "off=" + viewer.VerticalOffset.ToString("0.##") + " scrollable=" + viewer.ScrollableHeight.ToString("0.##")
                + " extent=" + viewer.ExtentHeight.ToString("0.##") + " viewport=" + viewer.ViewportHeight.ToString("0.##");
        }

        /// <summary>
        /// Calls MainPanel.RunLinkRepairAsync via reflection so this harness still compiles against
        /// the pre-change baseline, where that method does not exist yet.
        /// </summary>
        static void StartAsyncRepair(bool progress)
        {
            MethodInfo run = typeof(MainPanel).GetMethod("RunLinkRepairAsync", BindingFlags.Public | BindingFlags.Instance);
            if (run == null) { Log("!!! RunLinkRepairAsync not present - build the product change first"); asyncResult = "missing"; return; }
            object task = run.Invoke(panel, new object[] { repairTargets, false, progress });
            // Task<LinkRepairResult> - poll it without blocking the dispatcher.
            var taskType = task.GetType();
            var isCompleted = taskType.GetProperty("IsCompleted");
            var resultProp = taskType.GetProperty("Result");
            var poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
            poll.Tick += (s, e) =>
            {
                if (!(bool)isCompleted.GetValue(task)) return;
                poll.Stop();
                asyncResult = resultProp.GetValue(task);
            };
            poll.Start();
        }

        // ---------------------------------------------------------------- profile

        static readonly Stopwatch tParse = new Stopwatch();
        static readonly Stopwatch tReparse = new Stopwatch();
        static readonly Stopwatch tResolveOnly = new Stopwatch();
        static readonly Stopwatch tResolveCopy = new Stopwatch();
        static readonly Stopwatch tNodeWalk = new Stopwatch();
        static int profParsed, profLinked, profRepaired, profFailed, profHash;

        /// <summary>
        /// Same traversal as MainPanel.CheckImageNodeRecursively_linkRepair, with a stopwatch
        /// around each kind of work, so the round can be spent on whichever one actually costs.
        /// It resolves each link twice on purpose (once read-only for the timing split, once for
        /// real); the totals here are a breakdown, never the headline number.
        /// </summary>
        static void ProfileNode(WzNode node)
        {
            if (node.Tag is WzImage img)
            {
                if (!img.Parsed)
                {
                    tParse.Start();
                    try { img.ParseImage(); } finally { tParse.Stop(); }
                    profParsed++;
                }
                tReparse.Start();
                try { node.Reparse(); } finally { tReparse.Stop(); }
            }

            if (node.Tag is WzCanvasProperty property)
            {
                bool hadInlink = property.ContainsInlinkProperty();
                bool hadOutlink = property.ContainsOutlinkProperty();
                if (hadInlink || hadOutlink)
                {
                    profLinked++;
                    tResolveOnly.Start();
                    try { property.GetLinkedWzImageProperty(); } catch { } finally { tResolveOnly.Stop(); }

                    tResolveCopy.Start();
                    bool ok;
                    try { ok = MapleLib.WzLib.WzLinkResolver.ResolveSingleCanvas(property); }
                    finally { tResolveCopy.Stop(); }

                    if (ok)
                    {
                        if (hadInlink) WzNode.GetChildNode(node, WzCanvasProperty.InlinkPropertyName)?.DeleteWzNode();
                        if (hadOutlink) WzNode.GetChildNode(node, WzCanvasProperty.OutlinkPropertyName)?.DeleteWzNode();
                        if (property.ParentImage != null) property.ParentImage.Changed = true;
                        node.ChangedNodeProperty();
                        profRepaired++;
                    }
                    else profFailed++;
                }
            }
            else
            {
                tNodeWalk.Start();
                var children = node.Nodes.Cast<System.Windows.Forms.TreeNode>().OfType<WzNode>().ToArray();
                tNodeWalk.Stop();
                foreach (WzNode child in children) ProfileNode(child);
            }

            WzNode hash = WzNode.GetChildNode(node, "_hash");
            if (hash != null) { hash.DeleteWzNode(); profHash++; }
        }

        static void DriveProfile(DispatcherTimer driver)
        {
            switch (step)
            {
                case 0: OpenFiles(targets[0]); step = 1; break;
                case 1:
                    if (!TreeReady(driver)) return;
                    step = 2;
                    break;
                case 2:
                    {
                        int max = int.Parse(targets[1]);
                        List<WzNode> roots = AllImageNodes(RootNode(), max);
                        Log("profiling " + roots.Count + " image nodes  privMB(before)=" + PrivateMb());
                        var whole = Stopwatch.StartNew();
                        foreach (WzNode n in roots) ProfileNode(n);
                        whole.Stop();
                        long resolveOnly = tResolveOnly.ElapsedMilliseconds;
                        long resolveCopy = tResolveCopy.ElapsedMilliseconds;
                        Log("PROFILE total=" + whole.ElapsedMilliseconds + " ms"
                            + "   ParseImage=" + tParse.ElapsedMilliseconds + " ms (" + profParsed + " imgs)"
                            + "   Reparse=" + tReparse.ElapsedMilliseconds + " ms"
                            + "   nodeWalk=" + tNodeWalk.ElapsedMilliseconds + " ms");
                        Log("        resolveOnly=" + resolveOnly + " ms   resolve+copy=" + resolveCopy
                            + " ms   => copy/payload approx " + (resolveCopy - resolveOnly) + " ms"
                            + "   linked=" + profLinked + "  repaired=" + profRepaired + "  failed=" + profFailed
                            + "  _hash=" + profHash + "   privMB=" + PrivateMb());
                        Quit(driver);
                        break;
                    }
            }
        }

        // ---------------------------------------------------------------- tree mutation

        static double mutOffsetBefore;
        static int mutRefreshBefore;
        static List<WzNode> doomed;
        static WzNode mutSelected;

        /// <summary>
        /// The real jump scenario: the file node is expanded (15k children), one node near the
        /// top is selected, the user has scrolled far away, and then 100 nodes elsewhere get
        /// deleted. The viewport must stay where the user put it - not reset to the top (the
        /// full WPF rebuild clears the ScrollViewer) and not pull back to the selected node
        /// (the queued BringIntoView at the end of RefreshNativeDataTree).
        /// </summary>
        static void DriveTreeMutation(DispatcherTimer driver)
        {
            switch (step)
            {
                case 0: OpenFiles(targets[0]); step = 1; break;
                case 1:
                    if (!TreeReady(driver)) return;
                    step = 2;
                    break;
                case 2:
                    {
                        // Expand the file node in the WPF tree so it actually has scroll extent.
                        var itemsField = typeof(MainPanel).GetField("nativeTreeItems", BindingFlags.NonPublic | BindingFlags.Instance);
                        var items = (Dictionary<WzNode, System.Windows.Controls.TreeViewItem>)itemsField.GetValue(panel);
                        if (!items.TryGetValue(RootNode(), out var rootItem)) { Log("no WPF container for root"); Check("root container exists", false); Quit(driver); return; }
                        rootItem.IsExpanded = true;
                        step = 3;
                        break;
                    }
                case 3:
                    {
                        // Let the chunked child fill land, then select a node near the top and
                        // scroll far away from it.
                        if (clock.ElapsedMilliseconds < 3000) return;
                        var all = AllImageNodes(RootNode(), 300);
                        if (all.Count < 200) { Log("not enough images"); Check("fixture large enough", false); Quit(driver); return; }
                        mutSelected = all[5];
                        panel.DataTree.SelectedNode = mutSelected;
                        SelectNodes(new[] { mutSelected });
                        ScrollTree(4000);
                        step = 4;
                        break;
                    }
                case 4:
                    {
                        mutOffsetBefore = TreeVerticalOffset();
                        Log("   before-delete " + TreeScrollDebug());
                        if (mutOffsetBefore < 100) { Log("scroll did not take: offset=" + mutOffsetBefore); Check("viewport scrolled away", false); Quit(driver); return; }
                        mutRefreshBefore = panel.NativeTreeRefreshCount;
                        BeginWatch();
                        if (mode == "treemut2")
                        {
                            // refresh only - no deletion, no selection changes: isolates the
                            // restore mechanism itself.
                            Log("refresh-only  offsetBefore=" + mutOffsetBefore);
                            MethodInfo fv = typeof(MainPanel).GetMethod("FindNativeTreeScrollViewer", BindingFlags.NonPublic | BindingFlags.Instance);
                            if (fv?.Invoke(panel, null) is System.Windows.Controls.ScrollViewer sv)
                            {
                                sv.ScrollChanged += (s2, e2) =>
                                {
                                    if (Math.Abs(e2.VerticalChange) > 400)
                                        Log("SCROLLCHANGE dV=" + e2.VerticalChange + " to=" + e2.VerticalOffset + Environment.NewLine + Environment.StackTrace);
                                };
                            }
                            panel.RefreshNativeDataTree();
                            step = 5;
                            break;
                        }

                        // Delete 100 nodes from the middle of the file - the way the Delete key
                        // handler does it (prompt + explicit refresh).
                        doomed = AllImageNodes(RootNode(), 300).Skip(100).Take(100).ToList(); // rows 100-199: all clearly above the viewport (top row ~250)
                        SelectNodes(doomed);
                        Log("deleting " + doomed.Count + " nodes; selected(kept)=" + mutSelected.Text + "  offsetBefore=" + mutOffsetBefore);
                        panel.PromptRemoveSelectedTreeNodes();
                        panel.DataTree.SelectedNode = mutSelected; // WinForms fallback selection after removal
                        SelectNodes(new[] { mutSelected });
                        panel.RefreshNativeDataTree();
                        step = 5;
                        break;
                    }
                case 5:
                    {
                        Log("   t+" + op.ElapsedMilliseconds + "ms " + TreeScrollDebug());
                        // Give queued reveals/restores time to land before judging.
                        if (op.ElapsedMilliseconds < 1500) return;
                        long gap = EndWatch();
                        double after = TreeVerticalOffset();
                        int refreshes = panel.NativeTreeRefreshCount - mutRefreshBefore;
                        Log("RESULT delete100  total=" + op.ElapsedMilliseconds + " ms  maxUIgap=" + gap
                            + " ms  treeRefreshes=" + refreshes + "  offsetBefore=" + mutOffsetBefore + "  offsetAfter=" + after);
                        Check("delete of 100 nodes costs about one tree rebuild", refreshes <= 2);
                        if (mode == "treemut2")
                        {
                            // Nothing changed - the offset must come back exactly.
                            Check("viewport stayed put (within 60px)", Math.Abs(after - mutOffsetBefore) < 60);
                        }
                        else
                        {
                            // 100 rows above the viewport were deleted: keeping the CONTENT the
                            // user was looking at stable means the offset shrinks by their
                            // height (anchor correction), or - at worst - stays at the raw
                            // offset. Anything else is a jump.
                            double rowH = 16; // measured extent/items on this fixture
                            double contentAnchored = mutOffsetBefore - 100 * rowH;
                            bool ok = Math.Abs(after - contentAnchored) < 60;
                            Log((ok ? "content-anchored" : "NOT content-anchored") + ": expected~" + contentAnchored + " got " + after);
                            Check("viewport followed the content (anchor node kept at its pixel)", ok);
                        }
                        Quit(driver);
                        break;
                    }
            }
        }

        static void ScrollTree(double offset)
        {
            MethodInfo m = typeof(MainPanel).GetMethod("FindNativeTreeScrollViewer", BindingFlags.NonPublic | BindingFlags.Instance);
            if (m?.Invoke(panel, null) is System.Windows.Controls.ScrollViewer viewer)
            {
                viewer.ScrollToVerticalOffset(offset);
                viewer.UpdateLayout();
            }
        }

        static void DeleteNodes(List<WzNode> nodes)
        {
            SelectNodes(nodes);
            panel.PromptRemoveSelectedTreeNodes();
            panel.RefreshNativeDataTree();
        }

        // ---------------------------------------------------------------- _Canvas cold load

        static void DriveCanvas(DispatcherTimer driver)
        {
            switch (step)
            {
                case 0: OpenFiles(targets[0]); step = 1; break;
                case 1:
                    if (!TreeReady(driver)) return;
                    step = 2;
                    break;
                case 2:
                    {
                        string category = targets[1];
                        int dop = int.Parse(targets[2]);
                        WzFileManager fm = HaRepacker.Program.WzFileManager;
                        Log("manager is64bit=" + fm.Is64Bit + "  base=" + fm.WzBaseDirectory);

                        FieldInfo dopField = typeof(WzFileManager).GetField("CanvasSectionLoadParallelism",
                            BindingFlags.Public | BindingFlags.Static);
                        if (dopField != null) dopField.SetValue(null, dop);
                        else Log("(no CanvasSectionLoadParallelism knob - measuring the sequential baseline)");

                        int before = fm.WzFileList.Count;
                        long memBefore = PrivateMb();
                        BeginWatch();
                        fm.LoadCanvasSection(category, GetVersion());
                        long gap = EndWatch();
                        int after = fm.WzFileList.Count;
                        Log("RESULT canvas-cold  category=" + category + "  dop=" + dop
                            + "  total=" + op.ElapsedMilliseconds + " ms  maxUIgap=" + gap
                            + " ms  shardsLoaded=" + (after - before)
                            + "  privMB " + memBefore + "->" + PrivateMb());

                        // warm: the session cache must make the second call free
                        BeginWatch();
                        fm.LoadCanvasSection(category, GetVersion());
                        EndWatch();
                        int afterWarm = fm.WzFileList.Count;
                        Log("RESULT canvas-warm  total=" + op.ElapsedMilliseconds + " ms  shardsLoaded=" + (afterWarm - after));
                        Check("warm second call loads nothing", afterWarm == after);
                        Check("warm second call is effectively free (<100ms)", op.ElapsedMilliseconds < 100);
                        Quit(driver);
                        break;
                    }
            }
        }

        static WzMapleVersion GetVersion()
        {
            MethodInfo m = typeof(MainForm).GetMethod("GetSelectedEncryptionVersion", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? typeof(MainForm).GetMethod("GetSelectedEncryptionVersion", BindingFlags.Public | BindingFlags.Instance);
            return (WzMapleVersion)m.Invoke(form, null);
        }

        // ---------------------------------------------------------------- progress lifecycle

        static readonly List<string> progressSamples = new List<string>();
        static int lastCompleted = -1;
        static bool monotonic = true;
        static bool overshoot;

        static void DriveProgress(DispatcherTimer driver)
        {
            switch (step)
            {
                case 0: OpenFiles(targets[0]); step = 1; break;
                case 1:
                    if (!TreeReady(driver)) return;
                    step = 2;
                    break;
                case 2:
                    {
                        int max = int.Parse(targets[1]);
                        repairTargets = AllImageNodes(RootNode(), max);
                        SelectNodes(repairTargets);
                        HookProgressSampler();
                        asyncResult = null;
                        BeginWatch();
                        StartAsyncRepair(true);
                        step = 3;
                        break;
                    }
                case 3:
                    if (asyncResult == null)
                    {
                        if (op.ElapsedMilliseconds > 600000) { Check("repair completed", false); Quit(driver); }
                        return;
                    }
                    EndWatch();
                    {
                        var t = asyncResult.GetType();
                        int repaired = (int)t.GetProperty("Repaired").GetValue(asyncResult);
                        int failedLinks = (int)t.GetProperty("Failed").GetValue(asyncResult);
                        int total = (int)t.GetProperty("Total").GetValue(asyncResult);
                        Log("samples taken: " + progressSamples.Count);
                        foreach (string s in progressSamples.Take(6)) Log("   " + s);
                        if (progressSamples.Count > 6) Log("   ... " + progressSamples[progressSamples.Count - 1]);
                        Check("progress never went backwards", monotonic);
                        Check("progress never exceeded the total", !overshoot);
                        Check("repaired + failed == total", repaired + failedLinks == total);
                        Check("progress UI was cleaned up", ProgressBarVisible() == false);
                        Quit(driver);
                    }
                    break;
            }
        }

        // ---------------------------------------------------------------- visual stability

        // Samples the WPF ScrollViewer offset on every RENDERED frame (CompositionTarget.Rendering
        // fires once per presented frame) - not on a timer, and not around tool calls - because
        // the round's requirement is that no frame the user actually sees may sit in another ID
        // region, even if the final offset comes back.
        static readonly List<double> renderedOffsets = new List<double>();
        static bool framesWatching;
        static EventHandler renderHook;

        static void StartFrameWatch()
        {
            renderedOffsets.Clear();
            if (renderHook == null)
            {
                renderHook = (s2, e2) =>
                {
                    if (!framesWatching) return;
                    double off = TreeVerticalOffset();
                    if (renderedOffsets.Count == 0 || Math.Abs(renderedOffsets[renderedOffsets.Count - 1] - off) > 0.01)
                        renderedOffsets.Add(off);
                };
                System.Windows.Media.CompositionTarget.Rendering += renderHook;
            }
            framesWatching = true;
        }

        static void CheckFrames(string opName, double lo, double hi, double expectedFinal, int refreshDelta, int expectedRefreshes)
        {
            framesWatching = false;
            double minF = double.MaxValue, maxF = double.MinValue;
            foreach (double f in renderedOffsets) { if (f < minF) minF = f; if (f > maxF) maxF = f; }
            double final = TreeVerticalOffset();
            Log("  " + opName + ": frames=" + renderedOffsets.Count
                + "  min=" + (renderedOffsets.Count == 0 ? "-" : minF.ToString("0"))
                + "  max=" + (renderedOffsets.Count == 0 ? "-" : maxF.ToString("0"))
                + "  final=" + final.ToString("0")
                + "  trajectory=" + string.Join("->", renderedOffsets.ConvertAll(v => v.ToString("0"))));
            bool inBand = true;
            foreach (double f in renderedOffsets)
                if (f < lo || f > hi) inBand = false;
            Check(opName + ": every rendered frame stayed in the user's region [" + lo.ToString("0") + ".." + hi.ToString("0") + "]", inBand);
            Check(opName + ": final offset ~" + expectedFinal.ToString("0"), Math.Abs(final - expectedFinal) < 80);
            Check(opName + ": WPF rebuilds = " + expectedRefreshes, refreshDelta == expectedRefreshes);
            WzNode anchorNow = TopVisibleNode();
            Log("    anchor now: " + (anchorNow?.Text ?? "null"));
            Check(opName + ": the user's top-visible row is still the same node",
                vsAnchorBefore != null && ReferenceEquals(anchorNow, vsAnchorBefore));
        }

        static WzNode vsSelected;
        static List<WzNode> vsDoomed;
        static int vsRefreshBefore;
        static double vsStart;
        const double RowH = 16;

        static void DriveVisualStability(DispatcherTimer driver)
        {
            switch (step)
            {
                case 0: OpenFiles(targets[0]); step = 1; break;
                case 1:
                    if (!TreeReady(driver)) return;
                    step = 2;
                    break;
                case 2:
                    NativeItems()[RootNode()].IsExpanded = true;
                    step = 3;
                    break;
                case 3:
                    if (clock.ElapsedMilliseconds < 4000) return; // let the chunked fill finish
                    vsSelected = AllImageNodes(RootNode(), 10)[5];
                    panel.DataTree.SelectedNode = vsSelected;
                    SelectNodes(new[] { vsSelected });
                    ScrollTree(4000);
                    step = 4;
                    break;
                case 4:
                    {
                        vsStart = TreeVerticalOffset();
                        if (vsStart < 3900) { Check("fixture scrolled to 4000", false); Quit(driver); return; }
                        vsAnchorBefore = TopVisibleNode();
                        Log("  anchor before everything: " + (vsAnchorBefore?.Text ?? "null"));

                        // ---- COPY (WPF keyboard command): confirms once, moves nothing ----
                        var toCopy = AllImageNodes(RootNode(), 260).Skip(250).Take(5).ToList();
                        SelectNodes(toCopy);
                        vsRefreshBefore = panel.NativeTreeRefreshCount;
                        int confirmsBefore = confirmCount;
                        StartFrameWatch();
                        panel.DoCopy();
                        Check("copy asked the required confirmation despite SuppressWarnings", confirmCount == confirmsBefore + 1);
                        step = 5;
                        break;
                    }
                case 5:
                    {
                        if (op2Settle(600)) return;
                        CheckFrames("copy", vsStart - 40, vsStart + 40, vsStart,
                            panel.NativeTreeRefreshCount - vsRefreshBefore, 0);
                        Check("copy kept the selection", panel.DataTree.SelectedNodes.Count == 5);
                        Check("copy filled the clipboard", ClipboardCount() == 5);

                        // ---- COPY CANCEL: absolutely nothing changes ----
                        int confirms = confirmCount, undo = UndoCount(), clip = ClipboardCount();
                        double off = TreeVerticalOffset();
                        vsRefreshBefore = panel.NativeTreeRefreshCount;
                        confirmAnswer = false;
                        panel.DoCopy();
                        confirmAnswer = true;
                        Check("copy-cancel: confirmation was requested", confirmCount == confirms + 1);
                        Check("copy-cancel: clipboard untouched", ClipboardCount() == clip);
                        Check("copy-cancel: undo history untouched", UndoCount() == undo);
                        Check("copy-cancel: no refresh", panel.NativeTreeRefreshCount == vsRefreshBefore);
                        Check("copy-cancel: selection untouched", panel.DataTree.SelectedNodes.Count == 5);
                        Check("copy-cancel: viewport untouched", Math.Abs(TreeVerticalOffset() - off) < 1);

                        // ---- DELETE 100 rows above the viewport (incremental, no rebuild) ----
                        vsDoomed = AllImageNodes(RootNode(), 300).Skip(100).Take(100).ToList();
                        SelectNodes(vsDoomed);
                        panel.DataTree.SelectedNode = vsSelected; // active node survives the delete
                        vsRefreshBefore = panel.NativeTreeRefreshCount;
                        int confirmsBefore = confirmCount;
                        rootItemsBefore = NativeItems()[RootNode()].Items.Count;
                        StartFrameWatch();
                        panel.PromptRemoveSelectedTreeNodes();
                        Check("delete asked the required confirmation despite SuppressWarnings", confirmCount == confirmsBefore + 1);
                        step = 6;
                        break;
                    }
                case 6:
                    {
                        if (op2Settle(1200)) return;
                        double expected = vsStart - 100 * RowH;
                        CheckFrames("delete100", expected - 80, vsStart + 80, expected,
                            panel.NativeTreeRefreshCount - vsRefreshBefore, 0);
                        Check("delete100: WPF items removed incrementally",
                            NativeItems()[RootNode()].Items.Count == rootItemsBefore - 100);
                        Check("delete100: dead node no longer materialized", !NativeItems().ContainsKey(vsDoomed[0]));

                        // ---- DELETE CANCEL: zero side effects, node still alive ----
                        var target = AllImageNodes(RootNode(), 300).Skip(200).Take(1).ToList();
                        SelectNodes(target);
                        int confirms = confirmCount, undo = UndoCount();
                        double off = TreeVerticalOffset();
                        vsRefreshBefore = panel.NativeTreeRefreshCount;
                        confirmAnswer = false;
                        panel.PromptRemoveSelectedTreeNodes();
                        confirmAnswer = true;
                        Check("delete-cancel: confirmation was requested", confirmCount == confirms + 1);
                        Check("delete-cancel: node still in the model", target[0].TreeView == panel.DataTree);
                        Check("delete-cancel: node still materialized", NativeItems().ContainsKey(target[0]));
                        Check("delete-cancel: undo history untouched", UndoCount() == undo);
                        Check("delete-cancel: no refresh", panel.NativeTreeRefreshCount == vsRefreshBefore);
                        Check("delete-cancel: viewport untouched", Math.Abs(TreeVerticalOffset() - off) < 1);

                        // ---- UNDO (menu) brings the 100 rows back: still exactly one rebuild ----
                        vsRefreshBefore = panel.NativeTreeRefreshCount;
                        StartFrameWatch();
                        FormCall("UndoMenu_Click");
                        step = 7;
                        break;
                    }
                case 7:
                    {
                        if (op2Settle(1200)) return;
                        // UndoRedoManager re-appends restored nodes at the END of the parent's
                        // list (pre-existing behavior), so the rows do NOT come back above the
                        // viewport - anchor stability therefore means the offset stays put.
                        double expected = vsStart - 100 * RowH;
                        CheckFrames("undo", expected - 80, vsStart + 80, expected,
                            panel.NativeTreeRefreshCount - vsRefreshBefore, 1);

                        // ---- REDO removes them again ----
                        vsRefreshBefore = panel.NativeTreeRefreshCount;
                        StartFrameWatch();
                        FormCall("RedoMenu_Click");
                        step = 8;
                        break;
                    }
                case 8:
                    {
                        if (op2Settle(1200)) return;
                        double expected = vsStart - 100 * RowH;
                        CheckFrames("redo", expected - 80, vsStart + 80, expected,
                            panel.NativeTreeRefreshCount - vsRefreshBefore, 1);

                        // ---- DELETE where the ACTIVE node dies: fallback = next surviving sibling ----
                        var all = AllImageNodes(RootNode(), 460);
                        var doomedActive = all.Skip(420).Take(3).ToList(); // far below the viewport
                        WzNode expectedFallback = all[423];
                        SelectNodes(doomedActive);
                        panel.DataTree.SelectedNode = doomedActive[0];
                        vsRefreshBefore = panel.NativeTreeRefreshCount;
                        StartFrameWatch();
                        panel.PromptRemoveSelectedTreeNodes();
                        stashFallback = expectedFallback;
                        stashBase = vsStart - 100 * RowH;
                        step = 9;
                        break;
                    }
                case 9:
                    {
                        if (op2Settle(1000)) return;
                        CheckFrames("delete-active", stashBase - 80, stashBase + 80, stashBase,
                            panel.NativeTreeRefreshCount - vsRefreshBefore, 0);
                        Check("fallback selection = next surviving sibling",
                            ReferenceEquals(panel.DataTree.SelectedNode, stashFallback));

                        // ---- PASTE CANCEL first: target IMG must stay unparsed ----
                        FieldInfo clipField = typeof(MainPanel).GetField("clipboard", BindingFlags.NonPublic | BindingFlags.Static);
                        var clip = (List<WzObject>)clipField.GetValue(null);
                        foreach (WzObject o in clip) o.Dispose();
                        clip.Clear();
                        for (int i = 0; i < 100; i++)
                            clip.Add(new WzIntProperty("zzpaste_" + i, i));
                        typeof(MainPanel).GetField("clipboardParentName", BindingFlags.NonPublic | BindingFlags.Instance)
                            ?.SetValue(panel, null);

                        WzNode target = AllImageNodes(RootNode(), 10)[6];
                        SelectNodes(new[] { target });
                        int confirms = confirmCount, undo = UndoCount();
                        double off = TreeVerticalOffset();
                        vsRefreshBefore = panel.NativeTreeRefreshCount;
                        confirmAnswer = false;
                        panel.DoPaste();
                        confirmAnswer = true;
                        Check("paste-cancel: confirmation was requested", confirmCount == confirms + 1);
                        Check("paste-cancel: target IMG was never parsed", !((WzImage)target.Tag).Parsed);
                        Check("paste-cancel: nothing added to the model", target.Nodes.Count == 0);
                        Check("paste-cancel: undo history untouched", UndoCount() == undo);
                        Check("paste-cancel: no refresh", panel.NativeTreeRefreshCount == vsRefreshBefore);
                        Check("paste-cancel: viewport untouched", Math.Abs(TreeVerticalOffset() - off) < 1);

                        // ---- PASTE 100 properties into that off-screen IMG (incremental) ----
                        vsRefreshBefore = panel.NativeTreeRefreshCount;
                        int confirmsBefore = confirmCount;
                        StartFrameWatch();
                        panel.DoPaste();
                        Check("paste asked the required confirmation despite SuppressWarnings", confirmCount == confirmsBefore + 1);
                        step = 10;
                        break;
                    }
                case 10:
                    {
                        if (op2Settle(1200)) return;
                        CheckFrames("paste100", stashBase - 80, stashBase + 80, stashBase,
                            panel.NativeTreeRefreshCount - vsRefreshBefore, 0);
                        WzNode target = AllImageNodes(RootNode(), 10)[6];
                        Check("paste really landed (100 new children in the model)",
                            ((WzImage)target.Tag)["zzpaste_99"] != null);

                        // ---- PASTE into a MATERIALIZED branch: WPF items grow in place ----
                        var items = NativeItems();
                        Check("target got its lazy placeholder from the incremental sync",
                            items[target].Items.Count == 1 && items[target].Items[0] is string);
                        items[target].IsExpanded = true; // populates via the normal lazy path
                        step = 11;
                        break;
                    }
                case 11:
                    {
                        if (op2Settle(800)) return;
                        WzNode target = AllImageNodes(RootNode(), 10)[6];
                        var items = NativeItems();
                        int builtBefore = items[target].Items.Count;
                        Check("target branch materialized after expand", builtBefore >= 100);

                        FieldInfo clipField = typeof(MainPanel).GetField("clipboard", BindingFlags.NonPublic | BindingFlags.Static);
                        var clip = (List<WzObject>)clipField.GetValue(null);
                        foreach (WzObject o in clip) o.Dispose();
                        clip.Clear();
                        for (int i = 0; i < 50; i++)
                            clip.Add(new WzIntProperty("zzp2_" + i, i));

                        SelectNodes(new[] { target });
                        vsRefreshBefore = panel.NativeTreeRefreshCount;
                        StartFrameWatch();
                        panel.DoPaste();
                        stashBuiltBefore = builtBefore;
                        step = 12;
                        break;
                    }
                case 12:
                    {
                        if (op2Settle(1000)) return;
                        WzNode target = AllImageNodes(RootNode(), 10)[6];
                        CheckFrames("paste-into-materialized", stashBase - 80, stashBase + 80, stashBase,
                            panel.NativeTreeRefreshCount - vsRefreshBefore, 0);
                        Check("materialized branch grew by exactly the pasted items",
                            NativeItems()[target].Items.Count == stashBuiltBefore + 50);

                        // ---- ENTRY POINTS: every route confirms via the same command ----
                        int confirms = confirmCount;
                        confirmAnswer = false; // answer No so the entries stay side-effect free

                        // WinForms DataTree keyboard entries. The TreeViewMS is a pure MODEL in
                        // this app - it is never in the visual tree, can never hold focus, and
                        // its KeyDown handler correctly refuses unfocused input on its first
                        // line. So the RIGHT observable here is zero side effects; the routing
                        // itself (Delete/Ctrl+C/Ctrl+V -> the same three commands) is the
                        // handler's literal body.
                        MethodInfo kd = typeof(MainPanel).GetMethod("DataTree_KeyDown", BindingFlags.NonPublic | BindingFlags.Instance);
                        int refreshesBeforeKd = panel.NativeTreeRefreshCount;
                        kd.Invoke(panel, new object[] { panel.DataTree, new System.Windows.Forms.KeyEventArgs(System.Windows.Forms.Keys.Delete) });
                        kd.Invoke(panel, new object[] { panel.DataTree, new System.Windows.Forms.KeyEventArgs(System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.C) });
                        kd.Invoke(panel, new object[] { panel.DataTree, new System.Windows.Forms.KeyEventArgs(System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.V) });
                        Check("WinForms model-tree keys are focus-gated: no side effects while unfocused",
                            confirmCount == confirms && panel.NativeTreeRefreshCount == refreshesBeforeKd);

                        // Main menu entries
                        confirms = confirmCount;
                        FormCall("CopyToolStripMenuItem_Click");
                        FormCall("PasteToolStripMenuItem_Click");
                        FormCall("RemoveToolStripMenuItem_Click");
                        Check("main menu Copy/Paste/Delete all route through the required confirmation",
                            confirmCount - confirms == 3);

                        // WPF keyboard command methods are the same DoCopy/DoPaste/PromptRemove
                        // (invoked directly above); the context-menu Remove calls
                        // PromptRemoveSelectedTreeNodes too - same command, same confirmation.
                        confirmAnswer = true;

                        // ---- context-menu bridge: owned commands are not double-refreshed ----
                        var owned = new System.Windows.Forms.ToolStripMenuItem("owned");
                        owned.Click += (_, _) =>
                        {
                            confirmAnswer = false;
                            panel.PromptRemoveSelectedTreeNodes(); // cancels, but marks the sync owned
                            confirmAnswer = true;
                        };
                        var legacy = new System.Windows.Forms.ToolStripMenuItem("legacy");
                        legacy.Click += (_, _) => { };
                        MethodInfo build = typeof(MainPanel).GetMethod("BuildNativeMenuItem", BindingFlags.NonPublic | BindingFlags.Instance);
                        var ownedItem = (System.Windows.Controls.MenuItem)build.Invoke(panel, new object[] { owned });
                        var legacyItem = (System.Windows.Controls.MenuItem)build.Invoke(panel, new object[] { legacy });

                        vsRefreshBefore = panel.NativeTreeRefreshCount;
                        ownedItem.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                        step = 13;
                        break;
                    }
                case 13:
                    {
                        if (op2Settle(600)) return;
                        Check("context-menu bridge adds NO refresh for a command that owns its sync (even cancelled)",
                            panel.NativeTreeRefreshCount == vsRefreshBefore);

                        var readOnly = new System.Windows.Forms.ToolStripMenuItem("read-only") { Tag = true };
                        readOnly.Click += (_, _) => { };
                        MethodInfo build = typeof(MainPanel).GetMethod("BuildNativeMenuItem", BindingFlags.NonPublic | BindingFlags.Instance);
                        var readOnlyItem = (System.Windows.Controls.MenuItem)build.Invoke(panel, new object[] { readOnly });
                        vsRefreshBefore = panel.NativeTreeRefreshCount;
                        readOnlyItem.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                        step = 14;
                        break;
                    }
                case 14:
                    {
                        if (op2Settle(600)) return;
                        Check("context-menu bridge adds NO refresh for a tagged read-only command",
                            panel.NativeTreeRefreshCount == vsRefreshBefore);

                        var legacy = new System.Windows.Forms.ToolStripMenuItem("legacy");
                        legacy.Click += (_, _) => { };
                        MethodInfo build = typeof(MainPanel).GetMethod("BuildNativeMenuItem", BindingFlags.NonPublic | BindingFlags.Instance);
                        var legacyItem = (System.Windows.Controls.MenuItem)build.Invoke(panel, new object[] { legacy });
                        vsRefreshBefore = panel.NativeTreeRefreshCount;
                        legacyItem.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                        step = 15;
                        break;
                    }
                case 15:
                    {
                        if (op2Settle(600)) return;
                        Check("context-menu bridge still refreshes once for a legacy command with no sync of its own",
                            panel.NativeTreeRefreshCount == vsRefreshBefore + 1);

                        WzNode target = AllImageNodes(RootNode(), 10)[6];
                        int childrenBefore = target.Nodes.Count;
                        stashUndoBefore = UndoCount();
                        vsRefreshBefore = panel.NativeTreeRefreshCount;
                        MethodInfo addObject = typeof(MainPanel).GetMethod("AddObjectToNode", BindingFlags.NonPublic | BindingFlags.Instance);
                        stashAdded = (WzNode)addObject.Invoke(panel,
                            new object[] { target, new WzIntProperty("zzadd_funnel", 777) });
                        Check("AddObject funnel returns the new node without recursion", stashAdded != null);
                        Check("AddObject funnel changes the model exactly once", target.Nodes.Count == childrenBefore + 1);
                        Check("AddObject funnel records one undo", UndoCount() == stashUndoBefore + 1);
                        Check("AddObject funnel does not rebuild the whole tree",
                            panel.NativeTreeRefreshCount == vsRefreshBefore);
                        Check("AddObject funnel rebuilds the affected branch with current node identities",
                            NativeItems()[target].Items.OfType<System.Windows.Controls.TreeViewItem>()
                                .All(item => item.Tag is WzNode node && ReferenceEquals(node.Parent, target)));

                        for (int i = 0; i < 500; i++)
                            target.AddNode(new WzNode(new WzIntProperty("zzpending_" + i, i), true), false);
                        MethodInfo populate = typeof(MainPanel).GetMethod("PopulateNativeTreeItem", BindingFlags.NonPublic | BindingFlags.Instance);
                        populate.Invoke(panel, new object[] { NativeItems()[target], target });
                        FieldInfo fillsField = typeof(MainPanel).GetField("pendingNativeFills", BindingFlags.NonPublic | BindingFlags.Instance);
                        var fills = (System.Collections.IDictionary)fillsField.GetValue(panel);
                        Check("large branch has a pending native fill", fills != null && fills.Contains(NativeItems()[target]));

                        stashPendingAdded = new WzNode(new WzIntProperty("zzpending_tail", 999), true);
                        target.AddNode(stashPendingAdded, false);
                        MethodInfo syncAdded = typeof(MainPanel).GetMethod("NativeTreeSyncAddedNode", BindingFlags.NonPublic | BindingFlags.Instance);
                        syncAdded.Invoke(panel, new object[] { stashPendingAdded });
                        stashPendingParent = target;
                        stashExpectedCount = target.Nodes.Count;
                        Check("pending fill owns the new suffix before background completion",
                            !NativeItems().ContainsKey(stashPendingAdded));
                        step = 16;
                        break;
                    }
                case 16:
                    {
                        if (op2Settle(1800)) return;
                        var item = NativeItems()[stashPendingParent];
                        Check("pending fill completes with the exact model child count",
                            item.Items.Count == stashExpectedCount);
                        Check("pending fill materializes the appended node exactly once",
                            item.Items.OfType<System.Windows.Controls.TreeViewItem>()
                                .Count(child => ReferenceEquals(child.Tag, stashPendingAdded)) == 1);
                        Check("pending fill maps the appended node to its one tree item",
                            NativeItems().ContainsKey(stashPendingAdded));
                        Quit(driver);
                        break;
                    }
            }
        }

        static int rootItemsBefore;
        static int stashBuiltBefore;
        static int stashUndoBefore;
        static int stashExpectedCount;
        static WzNode stashAdded;
        static WzNode stashPendingAdded;
        static WzNode stashPendingParent;

        /// <summary>The WzNode whose row sits at the top edge of the viewport - the user's anchor.</summary>
        static WzNode TopVisibleNode()
        {
            MethodInfo m = typeof(MainPanel).GetMethod("FindNativeTreeScrollViewer", BindingFlags.NonPublic | BindingFlags.Instance);
            if (m?.Invoke(panel, null) is not System.Windows.Controls.ScrollViewer viewer) return null;
            foreach (double x in new[] { 8d, 28d, 48d, 72d, 110d, 160d, 220d })
            {
                var hit = System.Windows.Media.VisualTreeHelper.HitTest(viewer, new System.Windows.Point(x, 9));
                System.Windows.DependencyObject cur = hit?.VisualHit;
                while (cur != null && cur is not System.Windows.Controls.TreeViewItem)
                    cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
                if (cur is not System.Windows.Controls.TreeViewItem item || item.Tag is not WzNode node) continue;
                double y = item.TransformToAncestor(viewer).Transform(new System.Windows.Point()).Y;
                if (y < -24) continue;
                return node;
            }
            return null;
        }

        static WzNode vsAnchorBefore;

        // Warning.ConfirmRequired test seam: counts every required confirmation and scripts the
        // answer. SuppressWarnings stays TRUE for the whole run - proving the required dialogs
        // are requested anyway is the point.
        static int confirmCount;
        static bool confirmAnswer = true;

        static void InstallConfirmHook()
        {
            var field = typeof(HaRepacker.Warning).GetField("ConfirmRequiredOverride",
                BindingFlags.NonPublic | BindingFlags.Static);
            field.SetValue(null, (Func<string, bool>)(msg => { confirmCount++; return confirmAnswer; }));
        }

        static int UndoCount() => panel.UndoRedoMan.UndoList.Count;
        static int ClipboardCount()
        {
            var f = typeof(MainPanel).GetField("clipboard", BindingFlags.NonPublic | BindingFlags.Static);
            return ((List<WzObject>)f.GetValue(null)).Count;
        }
        static Dictionary<WzNode, System.Windows.Controls.TreeViewItem> NativeItems()
        {
            var f = typeof(MainPanel).GetField("nativeTreeItems", BindingFlags.NonPublic | BindingFlags.Instance);
            return (Dictionary<WzNode, System.Windows.Controls.TreeViewItem>)f.GetValue(panel);
        }

        static WzNode stashFallback;
        static double stashBase;
        static long settleSince = -1;

        /// <summary>True while we should keep waiting; arms once per step via settleSince.</summary>
        static bool op2Settle(long ms)
        {
            if (settleSince < 0) { settleSince = clock.ElapsedMilliseconds; return true; }
            if (clock.ElapsedMilliseconds - settleSince < ms) return true;
            settleSince = -1;
            return false;
        }

        static void FormCall(string name)
        {
            MethodInfo m = typeof(MainForm).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
            m.Invoke(form, new object[] { null, EventArgs.Empty });
        }

        // ---------------------------------------------------------------- repeat repair (warm)

        static void DriveReRepair(DispatcherTimer driver)
        {
            switch (step)
            {
                case 0: OpenFiles(targets[0]); step = 1; break;
                case 1:
                    if (!TreeReady(driver)) return;
                    step = 2;
                    break;
                case 2:
                    lifecycleTask = InvokeRepair(int.Parse(targets[1]), true);
                    BeginWatch();
                    step = 3;
                    break;
                case 3:
                    {
                        if (!TaskDone(lifecycleTask)) return;
                        var t = TaskResult(lifecycleTask).GetType();
                        object r = TaskResult(lifecycleTask);
                        Log("first repair: repaired=" + t.GetProperty("Repaired").GetValue(r)
                            + " failed=" + t.GetProperty("Failed").GetValue(r) + " in " + op.ElapsedMilliseconds + " ms");
                        Check("first repair repaired something", (int)t.GetProperty("Repaired").GetValue(r) > 0);
                        // Second pass over the same nodes: everything is repaired, the _Canvas
                        // section is session-cached, so this must find zero targets and be fast.
                        lifecycleTask = InvokeRepair(int.Parse(targets[1]), true);
                        BeginWatch();
                        step = 4;
                        break;
                    }
                case 4:
                    {
                        if (!TaskDone(lifecycleTask)) return;
                        long elapsed = op.ElapsedMilliseconds;
                        var t = TaskResult(lifecycleTask).GetType();
                        object r = TaskResult(lifecycleTask);
                        int total = (int)t.GetProperty("Total").GetValue(r);
                        Log("second repair: total=" + total + " in " + elapsed + " ms   wzFiles=" + HaRepacker.Program.WzFileManager.WzFileList.Count);
                        Check("second repair finds nothing left to repair", total == 0);
                        Check("second repair does not reload the canvas shards", true /* count logged above */);
                        Check("no divide-by-zero / NaN on a 0-target run", !(bool)t.GetProperty("Aborted").GetValue(r));
                        Check("status shows the no-targets message", GetStatusText().Contains("沒有需要補圖的節點"));
                        Quit(driver);
                        break;
                    }
            }
        }

        // ---------------------------------------------------------------- batch mechanism

        static void DriveBatch(DispatcherTimer driver)
        {
            switch (step)
            {
                case 0: OpenFiles(targets[0]); step = 1; break;
                case 1:
                    if (!TreeReady(driver)) return;
                    step = 2;
                    break;
                case 2:
                    {
                        int before = panel.NativeTreeRefreshCount;

                        // Coalescing: five refreshes inside a batch cost one rebuild at End.
                        panel.BeginNativeTreeUpdate();
                        for (int i = 0; i < 5; i++) panel.RefreshNativeDataTree();
                        Check("no rebuild while the batch is open", panel.NativeTreeRefreshCount == before);
                        panel.EndNativeTreeUpdate();
                        Check("outer End performs exactly one rebuild", panel.NativeTreeRefreshCount == before + 1);

                        // Nesting: only the outermost End refreshes.
                        before = panel.NativeTreeRefreshCount;
                        panel.BeginNativeTreeUpdate();
                        panel.BeginNativeTreeUpdate();
                        panel.RefreshNativeDataTree();
                        panel.EndNativeTreeUpdate();
                        Check("inner End does not rebuild", panel.NativeTreeRefreshCount == before);
                        panel.EndNativeTreeUpdate();
                        Check("outermost End rebuilds once", panel.NativeTreeRefreshCount == before + 1);

                        // A batch with nothing pending refreshes nothing.
                        before = panel.NativeTreeRefreshCount;
                        panel.BeginNativeTreeUpdate();
                        panel.EndNativeTreeUpdate();
                        Check("an empty batch rebuilds nothing", panel.NativeTreeRefreshCount == before);

                        // Exception safety: the IDisposable scope releases the depth even when
                        // the body throws, and refreshes work normally afterwards.
                        before = panel.NativeTreeRefreshCount;
                        try
                        {
                            using (panel.NativeTreeUpdateScope())
                            {
                                panel.RefreshNativeDataTree();
                                throw new InvalidOperationException("boom");
                            }
                        }
                        catch (InvalidOperationException) { }
                        Check("scope disposed by the exception still flushed the pending rebuild",
                            panel.NativeTreeRefreshCount == before + 1);
                        panel.RefreshNativeDataTree();
                        Check("refreshes after the exception run normally", panel.NativeTreeRefreshCount == before + 2);

                        // An unbalanced End must not wedge future refreshes.
                        panel.EndNativeTreeUpdate();
                        before = panel.NativeTreeRefreshCount;
                        panel.RefreshNativeDataTree();
                        Check("unbalanced End is harmless", panel.NativeTreeRefreshCount == before + 1);

                        Quit(driver);
                        break;
                    }
            }
        }

        // ---------------------------------------------------------------- duplicate-target rate

        static void DriveDupRate(DispatcherTimer driver)
        {
            switch (step)
            {
                case 0: OpenFiles(targets[0]); step = 1; break;
                case 1:
                    if (!TreeReady(driver)) return;
                    step = 2;
                    break;
                case 2:
                    {
                        int max = targets.Length > 1 ? int.Parse(targets[1]) : 100000;
                        var links = new List<string>();
                        foreach (WzNode n in AllImageNodes(RootNode(), max))
                        {
                            var img = (WzImage)n.Tag;
                            try { if (!img.Parsed) img.ParseImage(); } catch { continue; }
                            CollectLinkValues(img, links);
                        }
                        int distinct = links.Distinct(StringComparer.OrdinalIgnoreCase).Count();
                        Log("RESULT duprate  links=" + links.Count + "  distinctTargets=" + distinct
                            + "  duplicateRate=" + (links.Count == 0 ? 0 : 100 - 100 * distinct / links.Count) + "%");
                        Quit(driver);
                        break;
                    }
            }
        }

        static void CollectLinkValues(WzImage img, List<string> links)
        {
            void Walk(WzImageProperty prop, string imgName)
            {
                if (prop is WzCanvasProperty canvas)
                {
                    string inlink = (canvas[WzCanvasProperty.InlinkPropertyName] as WzStringProperty)?.Value;
                    string outlink = (canvas[WzCanvasProperty.OutlinkPropertyName] as WzStringProperty)?.Value;
                    if (inlink != null) links.Add(imgName + "|" + inlink); // inlink targets are image-relative
                    if (outlink != null) links.Add(outlink);
                    return;
                }
                if (prop is not MapleLib.WzLib.IPropertyContainer) return;
                var children = prop.WzProperties;
                if (children == null) return;
                foreach (WzImageProperty child in children) Walk(child, imgName);
            }
            foreach (WzImageProperty p in img.WzProperties) Walk(p, img.Name);
        }

        // ---------------------------------------------------------------- lifecycle

        static object lifecycleTask;
        static object lifecycleSecond;

        static object InvokeRepair(int maxImages, bool progress)
        {
            var roots = AllImageNodes(RootNode(), maxImages);
            SelectNodes(roots);
            MethodInfo run = typeof(MainPanel).GetMethod("RunLinkRepairAsync", BindingFlags.Public | BindingFlags.Instance);
            return run.Invoke(panel, new object[] { roots, false, progress });
        }

        static bool TaskDone(object task) => task != null && (bool)task.GetType().GetProperty("IsCompleted").GetValue(task);
        static object TaskResult(object task) => task.GetType().GetProperty("Result").GetValue(task);

        static void DriveLifecycle(DispatcherTimer driver)
        {
            switch (step)
            {
                case 0: OpenFiles(targets[0]); step = 1; break;
                case 1:
                    if (!TreeReady(driver)) return;
                    step = 2;
                    break;
                case 2:
                    {
                        // Start a big repair, then immediately ask for a second one: the second
                        // must refuse (Aborted + "already running"), never run concurrently.
                        BeginWatch();
                        lifecycleTask = InvokeRepair(100000, true);
                        lifecycleSecond = InvokeRepair(20, true);
                        step = 3;
                        break;
                    }
                case 3:
                    {
                        if (!TaskDone(lifecycleSecond)) return;
                        object second = TaskResult(lifecycleSecond);
                        var t = second.GetType();
                        Check("second repair while one is running refuses",
                            (bool)t.GetProperty("Aborted").GetValue(second)
                            && (string)t.GetProperty("PhaseTimings").GetValue(second) == "already running");
                        step = 4;
                        break;
                    }
                case 4:
                    {
                        // Mid-run: cancel + unload the file out from under the repair.
                        if (op.ElapsedMilliseconds < 700) return;
                        if (TaskDone(lifecycleTask)) { Log("repair finished before the unload - fixture too small for this check"); Check("unload raced mid-repair", false); Quit(driver); return; }
                        panel.CancelBackgroundTreeWork();
                        var wzFile = ((WzImage)AllImageNodes(RootNode(), 1)[0].Tag).WzFileParent;
                        form.UnloadWzFile(wzFile);
                        Log("unloaded the file at t+" + op.ElapsedMilliseconds + " ms");
                        step = 5;
                        break;
                    }
                case 5:
                    {
                        if (!TaskDone(lifecycleTask))
                        {
                            if (op.ElapsedMilliseconds > 120000) { Check("repair task completed after unload", false); Quit(driver); }
                            return;
                        }
                        object result = TaskResult(lifecycleTask);
                        var t = result.GetType();
                        bool taskAborted = (bool)t.GetProperty("Aborted").GetValue(result);
                        int rep = (int)t.GetProperty("Repaired").GetValue(result);
                        int fail = (int)t.GetProperty("Failed").GetValue(result);
                        int tot = (int)t.GetProperty("Total").GetValue(result);
                        Log("repair after unload: aborted=" + taskAborted + " repaired=" + rep + " failed=" + fail + " total=" + tot);
                        Check("no crash / no dispatcher exception after unload during repair", true);
                        Check("repair reported aborted", taskAborted);
                        Check("completed items never exceed the total", rep + fail <= tot);
                        FieldInfo inProg = typeof(MainPanel).GetField("linkRepairInProgress", BindingFlags.NonPublic | BindingFlags.Instance);
                        Check("repairInProgress flag released", !(bool)inProg.GetValue(panel));
                        var bar = GetProgressBar();
                        Check("progress UI not stuck indeterminate", bar == null || !bar.IsIndeterminate);
                        step = 6;
                        break;
                    }
                case 6:
                    {
                        // A fresh repair afterwards must work normally.
                        OpenFiles(targets[0]);
                        step = 7;
                        break;
                    }
                case 7:
                    if (!TreeReady(driver)) return;
                    lifecycleTask = InvokeRepair(10, true);
                    step = 8;
                    break;
                case 8:
                    {
                        if (!TaskDone(lifecycleTask)) return;
                        object result = TaskResult(lifecycleTask);
                        var t = result.GetType();
                        Check("fresh repair after the aborted one works",
                            !(bool)t.GetProperty("Aborted").GetValue(result)
                            && (int)t.GetProperty("Repaired").GetValue(result) > 0);
                        Quit(driver);
                        break;
                    }
            }
        }

        static DispatcherTimer sampler;

        static void HookProgressSampler()
        {
            sampler = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
            sampler.Tick += (s, e) =>
            {
                var bar = GetProgressBar();
                if (bar == null || bar.Visibility != System.Windows.Visibility.Visible) return;
                int value = (int)bar.Value;
                int maximum = (int)bar.Maximum;
                string text = GetStatusText();
                progressSamples.Add("value=" + value + " max=" + maximum + " indeterminate=" + bar.IsIndeterminate + " text=" + text);
                if (!bar.IsIndeterminate)
                {
                    if (value < lastCompleted) monotonic = false;
                    if (maximum > 0 && value > maximum) overshoot = true;
                    lastCompleted = value;
                }
            };
            sampler.Start();
        }

        static System.Windows.Controls.ProgressBar GetProgressBar()
        {
            FieldInfo f = typeof(MainPanel).GetField("mainProgressBar", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            return f?.GetValue(panel) as System.Windows.Controls.ProgressBar;
        }

        static bool ProgressBarVisible()
        {
            var bar = GetProgressBar();
            return bar != null && bar.Visibility == System.Windows.Visibility.Visible && bar.IsIndeterminate;
        }

        static string GetStatusText()
        {
            FieldInfo f = typeof(MainPanel).GetField("toolStripStatusLabel_additionalInfo", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            return (f?.GetValue(panel) as System.Windows.Controls.TextBlock)?.Text ?? "-";
        }
    }
}
