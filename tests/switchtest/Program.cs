using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MapleLib;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using SkillPreview;

// Reproduces the reported bug: "讀不到正確選擇的技能代碼" - after looking at one skill,
// clicking a different skill in the tree leaves the value table showing the FIRST skill.
class Program
{
    static StreamWriter log;
    static int passed, failed;

    [STAThread]
    static void Main()
    {
        log = new StreamWriter(Path.Combine(AppContext.BaseDirectory, "switchtest.txt"), false);
        try { Run(); }
        catch (Exception ex) { log.WriteLine("!!! " + ex); }
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
        string skillPath = Path.Combine(sandboxRoot, @"Data\Skill\Skill_000.wz");

        WzFile skillWz = Open(skillPath);
        Check("Skill_000.wz opened", skillWz != null);
        if (skillWz == null) return;

        // Grab two DIFFERENT skills that both have level tables - exactly what a user clicks
        // between in the tree.
        var found = new List<(string id, WzSubProperty skill, WzSubProperty level)>();
        foreach (WzImage img in skillWz.WzDirectory.WzImages)
        {
            try
            {
                img.ParseImage();
                if (!(img["skill"] is WzImageProperty skillList)) continue;
                foreach (WzImageProperty node in skillList.WzProperties)
                {
                    if (!(node is WzSubProperty sp)) continue;
                    if (!(sp["level"] is WzSubProperty lv)) continue;
                    var numbered = lv.WzProperties.OfType<WzSubProperty>()
                        .Where(p => int.TryParse(p.Name, out _)).ToList();
                    if (numbered.Count < 2) continue;
                    // Must carry editable scalars too, otherwise the table has no columns and
                    // the edit/save path below silently never runs.
                    if (!numbered.Any(n => n.WzProperties.Any(IsScalar))) continue;
                    found.Add((sp.Name, sp, lv));
                    if (found.Count >= 2) break;
                }
            }
            catch { }
            if (found.Count >= 2) break;
        }
        Check("found two different skills to switch between", found.Count >= 2);
        if (found.Count < 2) return;

        var first = found[0];
        var second = found[1];
        log.WriteLine("  [info] skill A = " + first.id + ", skill B = " + second.id);

        WzFileManager fileManager = new WzFileManager(sandboxRoot, false);
        fileManager.BuildWzFileList();

        var editor = new SkillValueEditor();
        const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
        FieldInfo F(string n) => typeof(SkillValueEditor).GetField(n, NP);

        // --- click skill A ---
        editor.TryLoad(first.skill, fileManager);
        var skillAfterA = F("currentSkill").GetValue(editor) as WzSubProperty;
        CheckEq("after clicking skill A the editor shows skill A", first.id, skillAfterA?.Name);

        // --- now click skill B, with NO edits made at all ---
        editor.TryLoad(second.skill, fileManager);
        var skillAfterB = F("currentSkill").GetValue(editor) as WzSubProperty;
        CheckEq("after clicking skill B the editor shows skill B", second.id, skillAfterB?.Name);

        // --- and back to A again ---
        editor.TryLoad(first.skill, fileManager);
        var skillBackToA = F("currentSkill").GetValue(editor) as WzSubProperty;
        CheckEq("clicking back to skill A shows skill A again", first.id, skillBackToA?.Name);

        // --- the level rows must belong to the skill on screen, not a stale one ---
        var levelNames = (List<string>)F("levelNames").GetValue(editor);
        int expectedLevels = first.level.WzProperties.Count(p => p is WzSubProperty && int.TryParse(p.Name, out _));
        CheckEq("the level count matches the skill actually displayed", expectedLevels, levelNames.Count);

        // --- a freshly loaded skill must not claim to have unsaved work ---
        var hasPending = typeof(SkillValueEditor).GetMethod("HasPendingEdits", NP);
        CheckEq("a just-loaded skill reports no unsaved edits", false, hasPending.Invoke(editor, null));

        // --- edit, save, then switch: saving must reset the baseline so the switch is clean ---
        var staged = (Dictionary<string, Dictionary<string, string>>)F("staged").GetValue(editor);
        var statColumns = (List<string>)F("statColumns").GetValue(editor);
        if (statColumns.Count > 0 && levelNames.Count > 0)
        {
            string col = statColumns[0];
            staged[levelNames[0]][col] = "12345";
            CheckEq("after typing a value the editor reports unsaved edits", true, hasPending.Invoke(editor, null));

            typeof(SkillValueEditor).GetMethod("SaveButton_Click", NP).Invoke(editor, new object[] { null, null });
            CheckEq("after saving, the editor reports no unsaved edits", false, hasPending.Invoke(editor, null));

            editor.TryLoad(second.skill, fileManager);
            var afterSaveSwitch = F("currentSkill").GetValue(editor) as WzSubProperty;
            CheckEq("switching straight after a save lands on the new skill", second.id, afterSaveSwitch?.Name);
        }
    }

    static bool IsScalar(WzImageProperty prop)
    {
        return prop is WzIntProperty || prop is WzLongProperty || prop is WzShortProperty
            || prop is WzFloatProperty || prop is WzDoubleProperty || prop is WzStringProperty;
    }

    static WzFile Open(string path)
    {
        foreach (WzMapleVersion v in new[] { WzMapleVersion.BMS, WzMapleVersion.GMS, WzMapleVersion.EMS, WzMapleVersion.CLASSIC })
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
