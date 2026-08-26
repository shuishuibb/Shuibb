using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HaRepacker;
using HaRepacker.GUI;
using HaRepacker.GUI.Panels;
using MapleLib.WzLib;

namespace typeahead
{
    /// <summary>
    /// Drives the real tree type-ahead the way the user does: expand String.wz/Skill.img, then
    /// "type" 1121008 into the tree. Checks two separate things, because the report separates
    /// them - that the SELECTION lands on the right node, and that the tree actually SCROLLS to
    /// reveal it.
    ///
    /// Usage: typeahead &lt;log&gt; &lt;wzPath&gt; &lt;imgName&gt; &lt;digits&gt;
    /// </summary>
    static class Program
    {
        static string logPath, wzPath, imgName, digits;
        static MainForm form;
        static MainPanel panel;
        static int step;
        static int passed, failed;
        static readonly System.Diagnostics.Stopwatch clock = new System.Diagnostics.Stopwatch();

        [STAThread]
        static void Main(string[] args)
        {
            logPath = args.Length > 0 ? args[0] : "typeahead.txt";
            wzPath = args.Length > 1 ? args[1] : "";
            imgName = args.Length > 2 ? args[2] : "Skill.img";
            digits = args.Length > 3 ? args[3] : "1121008";

            AppDomain.CurrentDomain.UnhandledException += (s, e) => { Log("!!! UNHANDLED: " + e.ExceptionObject); __RestoreUserSettings(); Environment.Exit(2); };

            System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
            System.Windows.Forms.Application.EnableVisualStyles();
            HaRepacker.Program.PrepareApplication(true);
            __ProtectUserSettings();
            HaRepacker.Program.ConfigurationManager.UserSettings.SuppressWarnings = true;
            HaRepacker.Program.ConfigurationManager.UserSettings.AutoloadRelatedWzFiles = false;

            Log("assembly: " + typeof(WzNode).Assembly.Location);
            Log("target  : " + wzPath + " -> " + imgName + ", typing '" + digits + "'");
            Log("");

            var app = new System.Windows.Application();
            app.ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose;
            app.DispatcherUnhandledException += (s, e) => { Log("!!! DISPATCHER: " + e.Exception); e.Handled = true; };

            form = new MainForm(null, false, false);
            panel = (MainPanel)typeof(MainForm).GetField("MainPanel", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);

            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
            timer.Tick += (s, e) => Tick(timer);
            form.Loaded += (s, e) =>
            {
                MethodInfo open = typeof(MainForm).GetMethod("OpenFileInternal", BindingFlags.NonPublic | BindingFlags.Instance);
                open.Invoke(form, new object[] { new string[] { wzPath } });
                timer.Start();
                clock.Start();
            };
            app.Run(form);
        }

        static void Tick(DispatcherTimer timer)
        {
            try
            {
                if (step == 0)
                {
                    if (panel.DataTree.Nodes.Count == 0)
                    {
                        if (clock.ElapsedMilliseconds > 180000) { Log("timed out loading"); Quit(timer); }
                        return;
                    }
                    step = 1;
                    Run();
                    Quit(timer);
                    return;
                }
                Quit(timer);
            }
            catch (Exception ex) { Log("!!! THREW: " + ex); Quit(timer); }
        }

        const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;

        static void Run()
        {
            WzNode root = (WzNode)panel.DataTree.Nodes[0];
            Log("root = " + root.Text + " (" + root.Nodes.Count + " children)");

            WzNode img = null;
            foreach (System.Windows.Forms.TreeNode c in root.Nodes)
                if (c is WzNode w && string.Equals(w.Text, imgName, StringComparison.OrdinalIgnoreCase))
                    img = w;
            Check("found " + imgName, img != null);
            if (img == null) return;

            // Parse + expand it, exactly as double-clicking does.
            MethodInfo parseSel = typeof(MainPanel).GetMethod("ParseOnDataTreeSelectedItem", BindingFlags.NonPublic | BindingFlags.Static);
            parseSel.Invoke(null, new object[] { img, true });
            Log(imgName + " children = " + img.Nodes.Count);
            Check(imgName + " has the big child count this bug needs (>1000)", img.Nodes.Count > 1000, "got " + img.Nodes.Count);

            MethodInfo reveal = typeof(MainPanel).GetMethod("SelectAndRevealNativeNode", NP);
            reveal.Invoke(panel, new object[] { img });
            Pump();

            var itemsField = typeof(MainPanel).GetField("nativeTreeItems", NP);
            IDictionary items = (IDictionary)itemsField.GetValue(panel);
            if (items.Contains(img))
            {
                TreeViewItem imgItem = (TreeViewItem)items[img];
                imgItem.IsExpanded = true;
            }
            Pump();

            // The node the user is trying to reach.
            WzNode wanted = null;
            foreach (System.Windows.Forms.TreeNode c in img.Nodes)
                if (c is WzNode w && string.Equals(w.Text, digits, StringComparison.Ordinal))
                    wanted = w;
            Check("the target node " + digits + " exists under " + imgName, wanted != null);
            if (wanted == null) return;

            int indexOfWanted = img.Nodes.IndexOf(wanted);
            Log("target '" + digits + "' is child #" + indexOfWanted + " of " + img.Nodes.Count);
            Log("");

            ScrollViewer scroller = FindScrollViewer(GetTree());
            Check("found the tree's ScrollViewer", scroller != null);
            Pump();
            Log("scroller: extent=" + scroller?.ExtentHeight + " viewport=" + scroller?.ViewportHeight
                + " scrollable=" + scroller?.ScrollableHeight);
            Check("the tree is actually laid out and scrollable (otherwise this test proves nothing)",
                scroller != null && scroller.ScrollableHeight > 0,
                "scrollable=" + scroller?.ScrollableHeight);
            double before = scroller?.VerticalOffset ?? -1;
            Log("scroll offset before typing = " + before);

            // Type the digits, the same entry point the key handler uses.
            MethodInfo jump = typeof(MainPanel).GetMethod("JumpToTypeAheadMatch", NP);
            Check("JumpToTypeAheadMatch exists", jump != null);
            if (jump == null) return;
            foreach (char ch in digits)
            {
                jump.Invoke(panel, new object[] { ch });
                Pump();
            }

            // --- did the SELECTION land correctly? ---
            var selectedField = typeof(MainPanel).GetField("nativeSelectedNodes", NP);
            var selected = (List<WzNode>)selectedField.GetValue(panel);
            string selectedName = selected.Count > 0 ? selected[selected.Count - 1].Text : "(none)";
            CheckEq("type-ahead selected the node the user typed", digits, selectedName);

            // --- did the tree actually SCROLL to reveal it? ---
            Pump();
            double after = scroller?.VerticalOffset ?? -1;
            Log("scroll offset after typing  = " + after);
            Check("the tree scrolled to reveal the match (this is the reported symptom)",
                after > before, "before=" + before + " after=" + after);

            // A node that far down cannot possibly be on screen at offset 0.
            Check("the match is not still parked at the top of the list", after > 100.0,
                "offset=" + after);

            // --- control experiment: does the SEARCH BOX path scroll? ---
            // Both paths end in a deferred BringIntoView. If this one does not scroll either,
            // then deferral was never the difference and the real problem is that the target
            // TreeViewItem is virtualized away and has no visual parent to scroll toward.
            Log("");
            scroller.ScrollToVerticalOffset(0);
            Pump();
            Log("control: scroll offset reset to " + scroller.VerticalOffset);
            reveal.Invoke(panel, new object[] { wanted });
            Pump();
            Pump();
            double afterReveal = scroller.VerticalOffset;
            Log("control: offset after SelectAndRevealNativeNode = " + afterReveal);
            Check("CONTROL - the search box path scrolls to the node", afterReveal > 100.0,
                "offset=" + afterReveal);

            // --- is the matched container even realized in the visual tree? ---
            if (!items.Contains(wanted))
                return;
            TreeViewItem wantedItem = (TreeViewItem)items[wanted];
            Log("matched TreeViewItem: IsVisible=" + wantedItem.IsVisible
                + " ActualHeight=" + wantedItem.ActualHeight
                + " visualParent=" + (VisualTreeHelper.GetParent(wantedItem)?.GetType().Name ?? "(none)"));

            TreeViewItem parentItem = (TreeViewItem)items[img];
            int indexInParent = parentItem.Items.IndexOf(wantedItem);
            Log("index of match inside its parent's Items = " + indexInParent);

            // --- CANDIDATE FIX A: VirtualizingStackPanel.BringIndexIntoViewPublic ---
            Log("");
            Log("== candidate A: BringIndexIntoViewPublic on the parent's items host ==");
            scroller.ScrollToVerticalOffset(0);
            Pump();
            Panel host = FindItemsHost(parentItem);
            Log("items host = " + (host?.GetType().Name ?? "(none)"));
            var vsp = host as VirtualizingStackPanel;
            if (vsp != null && indexInParent >= 0)
            {
                try { vsp.BringIndexIntoViewPublic(indexInParent); }
                catch (Exception ex) { Log("BringIndexIntoViewPublic threw: " + ex.Message); }
                Pump();
                Pump();
            }
            double afterA = scroller.VerticalOffset;
            Log("offset after candidate A = " + afterA);
            Check("CANDIDATE A scrolls to the node", afterA > 100.0, "offset=" + afterA);
            Log("after A: IsVisible=" + wantedItem.IsVisible + " ActualHeight=" + wantedItem.ActualHeight);

            // --- CANDIDATE B: scroll by computed offset from the flat visible list ---
            Log("");
            Log("== candidate B: ScrollToVerticalOffset by position in the visible list ==");
            scroller.ScrollToVerticalOffset(0);
            Pump();
            MethodInfo getVisible = typeof(MainPanel).GetMethod("GetVisibleNativeNodes", NP);
            var visible = (List<WzNode>)getVisible.Invoke(panel, null);
            int flatIndex = visible.IndexOf(wanted);
            double rowHeight = visible.Count > 0 ? scroller.ExtentHeight / visible.Count : 0;
            Log("flat index = " + flatIndex + " of " + visible.Count + ", derived row height = " + rowHeight);
            if (flatIndex >= 0)
            {
                scroller.ScrollToVerticalOffset(flatIndex * rowHeight);
                Pump();
                Pump();
            }
            double afterB = scroller.VerticalOffset;
            Log("offset after candidate B = " + afterB);
            Check("CANDIDATE B scrolls to the node", afterB > 100.0, "offset=" + afterB);
            Log("after B: IsVisible=" + wantedItem.IsVisible + " ActualHeight=" + wantedItem.ActualHeight);
        }

        static Panel FindItemsHost(DependencyObject root)
        {
            if (root == null) return null;
            if (root is Panel panel && panel.IsItemsHost) return panel;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                Panel found = FindItemsHost(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
        }

        static ItemsControl GetTree()
        {
            var f = typeof(MainPanel).GetField("dataTreeView", BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) return f.GetValue(panel) as ItemsControl;
            var p = typeof(MainPanel).GetProperty("dataTreeView", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            return p?.GetValue(panel) as ItemsControl;
        }

        static ScrollViewer FindScrollViewer(DependencyObject start)
        {
            if (start == null) return null;
            if (start is ScrollViewer sv) return sv;
            int count = VisualTreeHelper.GetChildrenCount(start);
            for (int i = 0; i < count; i++)
            {
                ScrollViewer found = FindScrollViewer(VisualTreeHelper.GetChild(start, i));
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>Lets WPF run layout, which is the whole point of the deferred BringIntoView.</summary>
        static void Pump()
        {
            for (int i = 0; i < 3; i++)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(
                    DispatcherPriority.SystemIdle, new Action(delegate { }));
            }
        }

        static void Check(string label, bool ok, string detail = null)
        {
            if (ok) { passed++; Log("  [ok]   " + label); }
            else { failed++; Log("  [FAIL] " + label + (detail != null ? "  -> " + detail : "")); }
        }

        static void CheckEq(string label, object expected, object actual)
        {
            if (Equals(expected, actual)) { passed++; Log("  [ok]   " + label + " = " + actual); }
            else { failed++; Log("  [FAIL] " + label + " expected <" + expected + "> but got <" + actual + ">"); }
        }

        static void Quit(DispatcherTimer timer)
        {
            timer.Stop();
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
