using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using MapleLib;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using SkillPreview;

class Program
{
    static StreamWriter log;
    static int passed, failed;

    [STAThread]
    static void Main()
    {
        string logPath = Path.Combine(AppContext.BaseDirectory, "svtest.txt");
        log = new StreamWriter(logPath, false);
        try
        {
            Run();
        }
        catch (Exception ex)
        {
            failed++;
            log.WriteLine("  [FAIL] the suite threw before finishing");
            log.WriteLine("!!! UNHANDLED: " + ex);
        }
        finally
        {
            log.WriteLine();
            log.WriteLine("=== passed " + passed + ", failed " + failed + " ===");
            log.Flush();
            log.Close();
        }
        Environment.Exit(failed == 0 ? 0 : 1);
    }

    static void Run()
    {
        string sandboxRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\data\skillvalue_sandbox"));
        string sandboxData = Path.Combine(sandboxRoot, "Data");
        string skillPath = Path.Combine(sandboxData, @"Skill\Skill_000.wz");

        // Open the level source the same way the real editor's tree does.
        WzFile skillWz = Open(skillPath);
        Check("Skill_000.wz opened", skillWz != null);
        if (skillWz == null) return;

        WzImage sampleImg = null;
        WzSubProperty sampleSkill = null;
        WzSubProperty sampleLevelContainer = null;
        foreach (WzImage img in skillWz.WzDirectory.WzImages)
        {
            try
            {
                img.ParseImage();
                WzImageProperty skillList = img["skill"];
                if (skillList == null) continue;
                foreach (WzImageProperty node in skillList.WzProperties)
                {
                    if (node.Name != "0001000") continue; // the exact skill the probe already inspected
                    if (!(node is WzSubProperty sp)) continue;
                    WzImageProperty level = sp["level"];
                    if (level == null) continue;
                    sampleImg = img;
                    sampleSkill = sp;
                    sampleLevelContainer = (WzSubProperty)level;
                    break;
                }
            }
            catch { }
            if (sampleSkill != null) break;
        }
        Check("found skill 0001000", sampleSkill != null);
        if (sampleSkill == null) return;

        // A real WzFileManager, pointed at the sandbox copy - exactly what the host passes in.
        // Deliberately NOT loading String_000 here: the actual bug report is "opened only
        // Skill.wz, description never shows up" - BuildWzFileList() indexes String_000's name and
        // path but never parses it, so this reproduces exactly what the user's session looked
        // like before TryLoad ever runs.
        WzFileManager fileManager = new WzFileManager(sandboxRoot, false);
        fileManager.BuildWzFileList();
        Check("sandbox has enough .wz files to trigger 64-bit-style category keys (like production)",
            WzFileManager.Detect64BitDirectoryWzFileFormat(sandboxRoot), "count=" +
            System.IO.Directory.EnumerateFiles(sandboxData, "*.wz", System.IO.SearchOption.AllDirectories).Count());
        Check("String.wz is NOT loaded yet - only Skill.wz was ever opened", fileManager.WzFileList.Count == 0);

        // ---- load ----------------------------------------------------------------------------
        var editor = new SkillValueEditor();
        bool loaded = editor.TryLoad(sampleLevelContainer["1"], fileManager);
        Check("TryLoad accepts a level-1 node and returns true", loaded);

        // Both String.wz (a near-empty stub, per the client's <Name>.wz + <Name>_000.wz layout)
        // and String_000.wz (the real content) match "string" in their path, so verification has
        // to pick the one that actually has Skill.img, same as the product code does.
        // Reading String.wz must NOT register it with the shared WzFileManager. Doing so left a
        // file loaded with no tab, which made File > Open on that same String.wz throw
        // "has already been loaded" and made any edit unsaveable.
        CheckEq("reading String.wz did NOT load it into the shared manager", 0, fileManager.WzFileList.Count);
        var detached = (WzFile)F("detachedStringWz").GetValue(editor);
        Check("it was read privately instead", detached != null);
        Check("and the text is flagged read-only, so writes are refused",
            (bool)F("stringEntryIsReadOnly").GetValue(editor));
        stringWzForVerification = detached;

        const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
        FieldInfo F(string name) => typeof(SkillValueEditor).GetField(name, NP);
        var levelNames = (System.Collections.Generic.List<string>)F("levelNames").GetValue(editor);
        var statColumns = (System.Collections.Generic.List<string>)F("statColumns").GetValue(editor);
        var staged = Staged(editor);

        Check("3 levels loaded", levelNames.Count == 3, "got " + levelNames.Count);
        CheckEq("levels in order 1,2,3", "1,2,3", string.Join(",", levelNames));
        Check("mpCon column detected", statColumns.Contains("mpCon"));
        Check("fixdamage column detected", statColumns.Contains("fixdamage"));
        Check("ball (container) is NOT a column", !statColumns.Contains("ball"));
        Check("hit (container) is NOT a column", !statColumns.Contains("hit"));

        CheckEq("row 0 mpCon matches real WZ (3)", "3", CellText(staged, "1", "mpCon"));
        CheckEq("row 1 mpCon matches real WZ (5)", "5", CellText(staged, "2", "mpCon"));
        CheckEq("row 2 mpCon matches real WZ (7)", "7", CellText(staged, "3", "mpCon"));
        CheckEq("row 0 fixdamage matches real WZ (10)", "10", CellText(staged, "1", "fixdamage"));
        CheckEq("row 2 fixdamage matches real WZ (40)", "40", CellText(staged, "3", "fixdamage"));

        // ---- description reads the real String.wz text --------------------------------------
        var descriptionBox = (System.Windows.Controls.TextBox)F("descriptionBox").GetValue(editor);
        var descriptionLabel = (System.Windows.Controls.TextBlock)F("descriptionLabel").GetValue(editor);
        CheckEq("description label shows level 1", "Lv. 1 說明", descriptionLabel.Text);
        CheckEq("description box shows the real h1 text", "MP 3後，殺傷力10", descriptionBox.Text);

        // ---- batch fill: stage mpCon = 100 + 10*i across levels, unsaved --------------------
        var startValueBox = (System.Windows.Controls.TextBox)F("startValueBox").GetValue(editor);
        var perLevelBox = (System.Windows.Controls.TextBox)F("perLevelBox").GetValue(editor);
        var statNodeBox = (System.Windows.Controls.ComboBox)F("statNodeBox").GetValue(editor);
        statNodeBox.SelectedItem = "mpCon";
        startValueBox.Text = "100";
        perLevelBox.Text = "10";
        InvokeClick(editor, "FillButton_Click");

        CheckEq("fill: level 1 = start value", "100", CellText(staged, "1", "mpCon"));
        CheckEq("fill: level 2 = start + step", "110", CellText(staged, "2", "mpCon"));
        CheckEq("fill: level 3 = start + 2*step", "120", CellText(staged, "3", "mpCon"));

        WzIntProperty liveMpConLv1 = sampleLevelContainer["1"]["mpCon"] as WzIntProperty;
        CheckEq("fill has NOT touched the live WZ object yet (staged only)", 3, liveMpConLv1?.Value ?? -1);

        // ---- template apply: writes String.wz immediately (that IS the save for text) -------
        // Applying is refused while the text is only being displayed...
        descriptionBox.Text = "消耗MP{mpCon}，造成{fixdamage}點固定傷害";
        InvokeApply(editor, descriptionBox.Text);
        WzStringProperty untouched = FindStringEntry(fileManager, "0001000")?["h1"] as WzStringProperty;
        CheckEq("apply is refused when String.wz is not open, leaving the text untouched",
            "MP 3後，殺傷力10", untouched?.Value);

        // ...and works once the user opens String.wz for real.
        WzFile userOpened = LoadInto(fileManager, "String_000");
        Check("user opened String.wz", userOpened != null);
        stringWzForVerification = userOpened;
        typeof(SkillValueEditor).GetMethod("ResolveStringEntry", NP).Invoke(editor, new object[] { fileManager });
        Check("the editor switched to the copy the user opened",
            !(bool)F("stringEntryIsReadOnly").GetValue(editor));
        descriptionBox.Text = "消耗MP{mpCon}，造成{fixdamage}點固定傷害";
        InvokeApply(editor, descriptionBox.Text);
        var statusText = (System.Windows.Controls.TextBlock)F("statusText").GetValue(editor);
        log.WriteLine("  [diag]  after ApplyTemplate_Click, statusText = " + statusText.Text);
        object internalEntry = F("currentStringEntry").GetValue(editor);
        object internalImage = F("currentStringImage").GetValue(editor);
        log.WriteLine("  [diag]  currentStringEntry null? " + (internalEntry == null));
        log.WriteLine("  [diag]  internal image path = " + (internalImage as WzImage)?.WzFileParent?.FilePath);
        log.WriteLine("  [diag]  internal image hash  = " + (internalImage?.GetHashCode()));
        log.WriteLine("  [diag]  verification image path = " + stringWzForVerification?.FilePath);
        var verifyImage = stringWzForVerification?.WzDirectory?["Skill.img"];
        log.WriteLine("  [diag]  verification image hash  = " + verifyImage?.GetHashCode());
        log.WriteLine("  [diag]  same object? " + ReferenceEquals(internalImage, verifyImage));
        log.WriteLine("  [diag]  WzFileList count = " + fileManager.WzFileList.Count);
        foreach (var wf in fileManager.WzFileList)
            log.WriteLine("  [diag]    loaded: " + wf.FilePath + "  ver=" + wf.MapleVersion);
        WzStringProperty h1 = FindStringEntry(fileManager, "0001000")?["h1"] as WzStringProperty;
        WzStringProperty h3 = FindStringEntry(fileManager, "0001000")?["h3"] as WzStringProperty;
        CheckEq("template rendered level 1 using the STAGED mpCon (100), not the live one (3)",
            "消耗MP100，造成10點固定傷害", h1?.Value);
        CheckEq("template rendered level 3 using its own staged mpCon (120)",
            "消耗MP120，造成40點固定傷害", h3?.Value);
        Check("String.wz image marked Changed after template apply", h1?.ParentImage.Changed == true);

        // ---- reset discards the staged mpCon edits -------------------------------------------
        InvokeClick(editor, "ResetButton_Click");
        var staged2 = Staged(editor);
        CheckEq("reset: grid mpCon reverts to the live WZ value (3)", "3", CellText(staged2, "1", "mpCon"));

        // ---- save: writes the staged edits into the real level nodes -------------------------
        statNodeBox.SelectedItem = "mpCon";
        startValueBox.Text = "999";
        perLevelBox.Text = "1";
        InvokeClick(editor, "FillButton_Click");
        InvokeClick(editor, "SaveButton_Click");

        WzIntProperty savedLv1 = sampleLevelContainer["1"]["mpCon"] as WzIntProperty;
        WzIntProperty savedLv2 = sampleLevelContainer["2"]["mpCon"] as WzIntProperty;
        CheckEq("save: level 1 mpCon actually written into the live WZ object", 999, savedLv1?.Value ?? -1);
        CheckEq("save: level 2 mpCon actually written into the live WZ object", 1000, savedLv2?.Value ?? -1);
        Check("save: level image marked Changed", savedLv1?.ParentImage.Changed == true);

        // ---- add / remove column --------------------------------------------------------------
        InvokeAddColumn(editor, "newTestStat");
        var statColumnsAfterAdd = (System.Collections.Generic.List<string>)F("statColumns").GetValue(editor);
        Check("add column: appears in the model", statColumnsAfterAdd.Contains("newTestStat"));
        Check("add column: NOT written to the live WZ yet (still staged)",
            sampleLevelContainer["1"]["newTestStat"] == null);

        foreach (var lvl in (System.Collections.Generic.List<string>)F("levelNames").GetValue(editor))
            Staged(editor)[lvl]["newTestStat"] = "42";
        InvokeClick(editor, "SaveButton_Click");
        Check("add column: save actually creates the node", sampleLevelContainer["1"]["newTestStat"] != null);
        CheckEq("add column: created node holds the typed value", "42",
            sampleLevelContainer["1"]["newTestStat"]?.ToString());

        // ---- Clear() leaves the editor empty, no exceptions -----------------------------------
        editor.Clear();
        var levelNamesAfterClear = (System.Collections.Generic.List<string>)F("levelNames").GetValue(editor);
        Check("Clear() empties level list", levelNamesAfterClear.Count == 0);
    }

    static WzFile stringWzForVerification;

    static WzSubProperty FindStringEntry(WzFileManager fileManager, string skillId)
    {
        // fileManager.FindWzImageByName("string", ...) is exactly the lookup proven broken on a
        // 64-bit-format data set above - verification here goes straight to the file this test
        // itself loaded, independent of whichever path SkillValueEditor took to find it.
        WzObject stringImg = stringWzForVerification?.WzDirectory?["Skill.img"];
        return (stringImg as WzImage)?[skillId] as WzSubProperty;
    }

    static System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>> Staged(object editor)
    {
        return (System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>)
            typeof(SkillValueEditor).GetField("staged", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(editor);
    }

    static string CellText(System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>> staged,
        string level, string column)
    {
        return staged.TryGetValue(level, out var row) && row.TryGetValue(column, out var value) ? value : null;
    }

    /// <summary>Runs apply the way a confirmed dialog would, deriving placeholders first.</summary>
    static void InvokeApply(object editor, string typed)
    {
        const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
        var focused = (string)typeof(SkillValueEditor).GetField("focusedLevel", NP).GetValue(editor);
        string template = typed;
        if (typed.IndexOf('{') < 0 && focused != null)
            template = (string)typeof(SkillValueEditor).GetMethod("DeriveTemplate", NP)
                .Invoke(editor, new object[] { typed, focused });

        var readOnly = (bool)typeof(SkillValueEditor).GetField("stringEntryIsReadOnly", NP).GetValue(editor);
        var entry = typeof(SkillValueEditor).GetField("currentStringEntry", NP).GetValue(editor);
        if (entry == null || readOnly)
            return;   // same refusal the click handler applies before it ever shows the dialog

        typeof(SkillValueEditor).GetMethod("ApplyTemplateToAllLevels", NP)
            .Invoke(editor, new object[] { template });
    }

    static void InvokeClick(object target, string methodName)
    {
        var m = typeof(SkillValueEditor).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        if (m == null) throw new Exception("method not found: " + methodName);
        m.Invoke(target, new object[] { null, null });
    }

    static void InvokeAddColumn(object target, string name)
    {
        // AddColumn_Click opens a modal prompt a headless test cannot drive, so this runs the
        // same model changes that a confirmed add performs.
        const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
        var statColumns = (System.Collections.Generic.List<string>)typeof(SkillValueEditor)
            .GetField("statColumns", NP).GetValue(target);
        var levelNames = (System.Collections.Generic.List<string>)typeof(SkillValueEditor)
            .GetField("levelNames", NP).GetValue(target);
        var staged = Staged(target);

        statColumns.Add(name);
        foreach (string lvl in levelNames)
            staged[lvl][name] = string.Empty;

        typeof(SkillValueEditor).GetMethod("RebuildRows", NP).Invoke(target, null);
    }

    /// <summary>Loads a wz into the manager the way File > Open does.</summary>
    static WzFile LoadInto(WzFileManager fileManager, string baseName)
    {
        foreach (WzMapleVersion v in new[] { WzMapleVersion.BMS, WzMapleVersion.GMS, WzMapleVersion.EMS, WzMapleVersion.CLASSIC })
        {
            try { return fileManager.LoadWzFile(baseName, v); }
            catch { }
        }
        return null;
    }

    static WzFile Open(string path)
    {
        WzMapleVersion[] candidates = { WzMapleVersion.BMS, WzMapleVersion.GMS, WzMapleVersion.EMS, WzMapleVersion.CLASSIC };
        foreach (WzMapleVersion v in candidates)
        {
            try
            {
                WzFile f = new WzFile(path, v);
                if (f.ParseWzFile() == WzFileParseStatus.Success && f.WzDirectory != null
                    && (f.WzDirectory.WzImages.Count > 0 || f.WzDirectory.WzDirectories.Count > 0))
                    return f;
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
        else { failed++; log.WriteLine("  [FAIL] " + label + " expected <" + expected + "> but got <" + actual + ">"); }
    }
}
