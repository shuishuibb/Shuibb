using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MapleLib;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using SkillPreview;

// The exact case reported: type the Lv.1 wording with real numbers into skill 1121008 and hit
// "apply to all levels". Every level must get its OWN numbers, not 30 copies of level 1's line.
class Program
{
    static StreamWriter log;
    static int passed, failed;
    const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;

    [STAThread]
    static void Main()
    {
        log = new StreamWriter(Path.Combine(AppContext.BaseDirectory, "derivetest.txt"), false);
        try { Run(); }
        catch (Exception ex)
        {
            failed++;
            log.WriteLine("  [FAIL] the suite threw before finishing");
            log.WriteLine("!!! " + ex);
        }
        finally
        {
            log.WriteLine();
            log.WriteLine("=== passed " + passed + ", failed " + failed + " ===");
            log.Flush(); log.Close();
        }
        Environment.Exit(failed == 0 ? 0 : 1);
    }

    static void Run()
    {
        string sandboxRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\data\skillvalue_sandbox"));
        WzFile skillWz = Open(Path.Combine(sandboxRoot, @"Data\Skill\Skill_000.wz"));
        Check("Skill_000.wz opened", skillWz != null);
        if (skillWz == null) return;

        WzSubProperty skill = null, levels = null;
        foreach (WzImage img in skillWz.WzDirectory.WzImages)
        {
            try
            {
                img.ParseImage();
                if (!(img["skill"] is WzImageProperty list)) continue;
                foreach (WzImageProperty n in list.WzProperties)
                    if (n.Name == "1121008" && n is WzSubProperty sp) { skill = sp; levels = sp["level"] as WzSubProperty; }
            }
            catch { }
            if (skill != null) break;
        }
        Check("found skill 1121008 (the one from the report)", skill != null && levels != null);
        if (skill == null) return;

        var fm = new WzFileManager(sandboxRoot, false);
        fm.BuildWzFileList();

        // These features write String.wz, which the editor only permits when the user actually
        // has it open - so open it here, the way File > Open would.
        WzFile stringWz = null;
        foreach (WzMapleVersion v in new[] { WzMapleVersion.BMS, WzMapleVersion.GMS, WzMapleVersion.EMS, WzMapleVersion.CLASSIC })
        {
            try { stringWz = fm.LoadWzFile("String_000", v); break; } catch { }
        }
        Check("String_000.wz opened into the editor", stringWz != null);

        var editor = new SkillValueEditor();
        editor.TryLoad(levels["1"], fm);

        FieldInfo F(string n) => typeof(SkillValueEditor).GetField(n, NP);
        var levelNames = (List<string>)F("levelNames").GetValue(editor);
        CheckEq("30 levels loaded", 30, levelNames.Count);

        var derive = typeof(SkillValueEditor).GetMethod("DeriveTemplate", NP);
        var render = typeof(SkillValueEditor).GetMethod("RenderTemplate", NP);

        // Exactly what the user typed.
        string typed = "消耗MP16，傷害152%";
        string template = (string)derive.Invoke(editor, new object[] { typed, "1" });
        log.WriteLine("typed    : " + typed);
        log.WriteLine("derived  : " + template);
        CheckEq("both numbers became placeholders", "消耗MP{mpCon}，傷害{damage}%", template);

        // Every level must render its own values - these are the real values from the WZ.
        var expected = new Dictionary<string, string>
        {
            { "1",  "消耗MP16，傷害152%" },
            { "2",  "消耗MP16，傷害154%" },
            { "10", "消耗MP16，傷害170%" },
            { "11", "消耗MP24，傷害172%" },
            { "25", "消耗MP30，傷害200%" },
            { "30", "消耗MP25，傷害210%" },
        };
        foreach (var pair in expected)
        {
            string got = (string)render.Invoke(editor, new object[] { template, pair.Key });
            CheckEq("Lv." + pair.Key + " renders its own numbers", pair.Value, got);
        }

        // Nothing may collapse to the level-1 line.
        int identical = levelNames.Count(l => (string)render.Invoke(editor, new object[] { template, l }) == typed);
        CheckEq("only level 1 still reads like level 1", 1, identical);

        // Text with no matching numbers must stay literal rather than being mangled.
        string prose = "對眼前的多數敵人，發動兩次攻擊。";
        CheckEq("prose with no matching numbers is untouched", prose,
            (string)derive.Invoke(editor, new object[] { prose, "1" }));

        // An explicit {placeholder} template still works unchanged.
        CheckEq("explicit placeholders still render",
            "消耗MP16，傷害152%",
            (string)render.Invoke(editor, new object[] { "消耗MP{mpCon}，傷害{damage}%", "1" }));

        // A constant column must not win over a varying one when both match.
        var varies = typeof(SkillValueEditor).GetMethod("ColumnVariesAcrossLevels", NP);
        Check("damage is detected as varying", (bool)varies.Invoke(editor, new object[] { "damage" }));
        Check("attackCount is detected as constant", !(bool)varies.Invoke(editor, new object[] { "attackCount" }));

        // --- the confirmation dialog itself ---
        // This is the path that shipped broken: CreateButton was handed a null Click handler and
        // WPF threw before the window ever appeared. Building it here would have caught that.
        var build = typeof(SkillValueEditor).GetMethod("BuildApplyPreviewDialog", NP);
        Check("BuildApplyPreviewDialog exists", build != null);
        if (build == null) return;

        bool acceptedFired = false;
        System.Windows.Window dialog = null;
        try
        {
            dialog = (System.Windows.Window)build.Invoke(editor,
                new object[] { template, true, (Action)(() => acceptedFired = true) });
        }
        catch (TargetInvocationException ex)
        {
            Check("building the confirm dialog does not throw", false, ex.InnerException?.Message);
            return;
        }
        Check("building the confirm dialog does not throw", true);
        Check("the dialog was actually created", dialog != null);

        // Every level must be listed, so nothing is overwritten unseen.
        int previewRows = CountRenderedRows(dialog, levelNames);
        CheckEq("the preview lists every level", levelNames.Count, previewRows);

        // The buttons must be wired - the crash was precisely a button with no handler.
        var okButton = FindButton(dialog, "確定套用");
        Check("the confirm button exists", okButton != null);
        if (okButton != null)
        {
            okButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check("clicking it reports acceptance", acceptedFired);
        }
        Check("the cancel button exists", FindButton(dialog, "取消") != null);

        RunNewFeatureTests(editor, levels);
    }

    // ---- the five requested features -------------------------------------------------------

    static void RunNewFeatureTests(SkillValueEditor editor, WzSubProperty levels)
    {
        FieldInfo F(string n) => typeof(SkillValueEditor).GetField(n, NP);
        MethodInfo M(string n) => typeof(SkillValueEditor).GetMethod(n, NP);
        var levelNames = (List<string>)F("levelNames").GetValue(editor);
        var staged = (Dictionary<string, Dictionary<string, string>>)F("staged").GetValue(editor);

        var statNodeBox = (System.Windows.Controls.ComboBox)F("statNodeBox").GetValue(editor);
        var startBox = (System.Windows.Controls.TextBox)F("startValueBox").GetValue(editor);
        var stepBox = (System.Windows.Controls.TextBox)F("perLevelBox").GetValue(editor);
        var opBox = (System.Windows.Controls.ComboBox)F("operationBox").GetValue(editor);
        var fromBox = (System.Windows.Controls.TextBox)F("fromLevelBox").GetValue(editor);
        var toBox = (System.Windows.Controls.TextBox)F("toLevelBox").GetValue(editor);

        log.WriteLine("");
        log.WriteLine("---- 1. subtraction ----");
        var ops = opBox.Items.Cast<System.Windows.Controls.ComboBoxItem>()
            .Select(i => (string)i.Tag).ToList();
        Check("the 運算 list offers subtraction", ops.Contains("sub"), string.Join(",", ops));

        statNodeBox.SelectedItem = "damage";
        opBox.SelectedIndex = ops.IndexOf("sub");
        startBox.Text = "100";
        stepBox.Text = "5";
        fromBox.Text = "";
        toBox.Text = "";
        M("FillButton_Click").Invoke(editor, new object[] { null, null });
        CheckEq("subtract: Lv.1 = 100", "100", staged["1"]["damage"]);
        CheckEq("subtract: Lv.2 = 95", "95", staged["2"]["damage"]);
        CheckEq("subtract: Lv.5 = 80", "80", staged["5"]["damage"]);

        log.WriteLine("");
        log.WriteLine("---- 2. fill only a level range ----");
        // Put a marker everywhere first, then fill 10~20 only.
        opBox.SelectedIndex = ops.IndexOf("add");
        startBox.Text = "7"; stepBox.Text = "0";
        fromBox.Text = ""; toBox.Text = "";
        M("FillButton_Click").Invoke(editor, new object[] { null, null });
        CheckEq("marker written everywhere first", "7", staged["30"]["damage"]);

        startBox.Text = "500"; stepBox.Text = "1";
        fromBox.Text = "10"; toBox.Text = "20";
        M("FillButton_Click").Invoke(editor, new object[] { null, null });
        CheckEq("range: Lv.9 untouched", "7", staged["9"]["damage"]);
        CheckEq("range: Lv.10 = start value", "500", staged["10"]["damage"]);
        CheckEq("range: Lv.11 = start + 1", "501", staged["11"]["damage"]);
        CheckEq("range: Lv.20 = start + 10", "510", staged["20"]["damage"]);
        CheckEq("range: Lv.21 untouched", "7", staged["21"]["damage"]);

        // A reversed range must still mean the same span rather than doing nothing.
        fromBox.Text = "20"; toBox.Text = "10";
        startBox.Text = "900"; stepBox.Text = "0";
        M("FillButton_Click").Invoke(editor, new object[] { null, null });
        CheckEq("reversed range still fills Lv.10", "900", staged["10"]["damage"]);
        CheckEq("reversed range still stops before Lv.21", "7", staged["21"]["damage"]);

        log.WriteLine("");
        log.WriteLine("---- 3. bulk add levels ----");
        int before = levelNames.Count;
        int createdTo = 40;
        // AddLevels_Click prompts, so drive the same model change it performs.
        AddLevelsTo(editor, createdTo);
        CheckEq("table now runs to the requested level", createdTo, levelNames.Count);
        CheckEq("the new level copied the last real level's damage",
            staged[before.ToString()]["damage"], staged[(before + 1).ToString()]["damage"]);
        var pending = (HashSet<string>)F("pendingNewLevels").GetValue(editor);
        CheckEq("the new levels are tracked as unsaved", createdTo - before, pending.Count);
        Check("the editor now reports unsaved work", (bool)M("HasPendingEdits").Invoke(editor, null));
        Check("nothing exists in the WZ yet", levels["31"] == null);

        M("SaveButton_Click").Invoke(editor, new object[] { null, null });
        Check("save created Lv.31 in the WZ", levels["31"] is WzSubProperty);
        Check("save created Lv.40 in the WZ", levels["40"] is WzSubProperty);
        var newLevel = levels["40"] as WzSubProperty;
        Check("the cloned level kept the nested 'hit' container",
            (levels["30"] as WzSubProperty)?["hit"] == null || newLevel?["hit"] != null);
        Check("the cloned level kept damage as a numeric node, not a string",
            newLevel?["damage"] is WzIntProperty || newLevel?["damage"] is WzShortProperty
            || newLevel?["damage"] is WzLongProperty);
        CheckEq("no unsaved work remains after saving", false, (bool)M("HasPendingEdits").Invoke(editor, null));

        RunStringTextTests(editor);
    }

    // ---- 4 + 5: desc shown/edited, and missing String nodes created on demand ---------------

    static void RunStringTextTests(SkillValueEditor editor)
    {
        FieldInfo F(string n) => typeof(SkillValueEditor).GetField(n, NP);
        MethodInfo M(string n) => typeof(SkillValueEditor).GetMethod(n, NP);

        log.WriteLine("");
        log.WriteLine("---- 4. desc is shown ----");
        var descBox = (System.Windows.Controls.TextBox)F("skillDescBox").GetValue(editor);
        Check("the desc field exists", descBox != null);
        CheckEq("it shows the skill's real String.wz desc",
            "對眼前的多數敵人，發動兩次攻擊。", descBox.Text);

        var entry = (WzSubProperty)F("currentStringEntry").GetValue(editor);
        Check("String.wz entry is writable here", entry != null
            && !(bool)F("stringEntryIsReadOnly").GetValue(editor));

        log.WriteLine("");
        log.WriteLine("---- 5. missing desc / h nodes get created ----");
        // Strip desc and a couple of level lines, exactly like a skill whose text was never authored.
        entry.RemoveProperty(entry["desc"]);
        entry.RemoveProperty(entry["h1"]);
        Check("desc removed for the test", entry["desc"] == null);
        Check("h1 removed for the test", entry["h1"] == null);

        descBox.Text = "自動建立測試用說明";
        M("SaveButton_Click").Invoke(editor, new object[] { null, null });
        Check("save re-created the missing desc node", entry["desc"] is WzStringProperty);
        CheckEq("...with the typed text", "自動建立測試用說明",
            (entry["desc"] as WzStringProperty)?.Value);

        M("ApplyTemplateToAllLevels").Invoke(editor, new object[] { "測試{damage}" });
        Check("apply re-created the missing h1 node", entry["h1"] is WzStringProperty);
        Check("...and created lines for the newly added levels too", entry["h40"] is WzStringProperty);

        // A skill with no String entry at all must get one rather than silently doing nothing.
        var image = (WzImage)F("currentStringImage").GetValue(editor);
        F("currentStringEntry").SetValue(editor, null);
        bool ensured = (bool)M("EnsureStringEntry").Invoke(editor, null);
        Check("EnsureStringEntry creates an entry when the skill has no text at all", ensured);
        Check("...and the editor now points at a real entry",
            F("currentStringEntry").GetValue(editor) != null);
        Check("...which really lives in String.wz", image["1121008"] != null);
    }

    static void AddLevelsTo(SkillValueEditor editor, int target)
    {
        FieldInfo F(string n) => typeof(SkillValueEditor).GetField(n, NP);
        var levelNames = (List<string>)F("levelNames").GetValue(editor);
        var statColumns = (List<string>)F("statColumns").GetValue(editor);
        var staged = (Dictionary<string, Dictionary<string, string>>)F("staged").GetValue(editor);
        var original = (Dictionary<string, Dictionary<string, string>>)F("original").GetValue(editor);
        var pending = (HashSet<string>)F("pendingNewLevels").GetValue(editor);

        int highest = levelNames.Select(n => int.Parse(n)).Max();
        string source = levelNames[levelNames.Count - 1];
        for (int number = highest + 1; number <= target; number++)
        {
            string name = number.ToString();
            var row = new Dictionary<string, string>();
            foreach (string c in statColumns)
                row[c] = staged[source].TryGetValue(c, out string v) ? v : "";
            staged[name] = row;
            original[name] = new Dictionary<string, string>();
            levelNames.Add(name);
            pending.Add(name);
        }
        typeof(SkillValueEditor).GetMethod("RebuildRows", NP).Invoke(editor, null);
    }

    static int CountRenderedRows(System.Windows.DependencyObject root, List<string> levelNames)
    {
        var wanted = new HashSet<string>(levelNames.Select(l => "Lv. " + l));
        int found = 0;
        foreach (var tb in Descendants(root).OfType<System.Windows.Controls.TextBlock>())
            if (wanted.Contains(tb.Text)) found++;
        return found;
    }

    static System.Windows.Controls.Button FindButton(System.Windows.DependencyObject root, string content)
    {
        return Descendants(root).OfType<System.Windows.Controls.Button>()
            .FirstOrDefault(b => (b.Content as string) == content);
    }

    /// <summary>
    /// Walks the LOGICAL tree - the dialog is never shown, so it has no visual tree to walk.
    /// </summary>
    static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
    {
        if (root == null) yield break;
        foreach (object child in System.Windows.LogicalTreeHelper.GetChildren(root))
        {
            if (!(child is System.Windows.DependencyObject node)) continue;
            yield return node;
            foreach (var deeper in Descendants(node)) yield return deeper;
        }
    }

    static WzFile Open(string path)
    {
        foreach (WzMapleVersion v in new[] { WzMapleVersion.BMS, WzMapleVersion.GMS, WzMapleVersion.EMS, WzMapleVersion.CLASSIC })
        {
            try
            {
                WzFile f = new WzFile(path, v);
                if (f.ParseWzFile() == WzFileParseStatus.Success && f.WzDirectory != null
                    && (f.WzDirectory.WzImages.Count > 0 || f.WzDirectory.WzDirectories.Count > 0)) return f;
                f.Dispose();
            }
            catch { }
        }
        return null;
    }

    static void Check(string label, bool ok, string detail = null)
    {
        if (ok) { passed++; log.WriteLine("  [ok]   " + label); }
        else { failed++; log.WriteLine("  [FAIL] " + label + (detail != null ? "  -> " + detail : "")); }
    }

    static void CheckEq(string label, object expected, object actual)
    {
        if (Equals(expected, actual)) { passed++; log.WriteLine("  [ok]   " + label + " = " + actual); }
        else { failed++; log.WriteLine("  [FAIL] " + label + "\n           expected <" + expected + ">\n           got      <" + actual + ">"); }
    }
}
