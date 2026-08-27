using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using MapleLib;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using SkillPreview;
using Xunit;
using Assert = Xunit.Assert;

namespace NodeEditorCopyPasteTests;

/// <summary>
/// Cross-source regression for SkillValueEditor's String.wz resolution - the same class of bug
/// NodeEditorPanel had (audit P0-1): the editor scanned the app-wide WzFileList with "zh_tw
/// first, else first hit", so with two Data sets open, a skill from one set read - and since the
/// name/desc/level-description boxes are writable, would have WRITTEN - the other set's
/// Skill.img. It now shares StringSourceScope with NodeEditorPanel.
///
/// Real .wz files under %TEMP%\ShuibbFixValidation, one real WzFileManager, a real
/// SkillValueEditor, both load orders.
/// </summary>
public sealed class SkillValueStringSourceTests
{
    private static void RunSta(Action action)
    {
        Exception captured = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (captured != null)
            ExceptionDispatchInfo.Capture(captured).Throw();
    }

    private static string BuildSkillWz(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using var wz = new WzFile(1, WzMapleVersion.BMS) { Name = Path.GetFileName(path) };
        var img = new WzImage("112.img");
        var skill = new WzSubProperty("1121008");
        var level = new WzSubProperty("level");
        var lv1 = new WzSubProperty("1");
        lv1.AddProperty(new WzIntProperty("damage", 100));
        level.AddProperty(lv1);
        skill.AddProperty(level);
        img.AddProperty(skill);
        wz.WzDirectory.AddImage(img);
        wz.SaveToDisk(path, false);
        return path;
    }

    private static string BuildSkillStringWz(string path, string skillName)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using var wz = new WzFile(1, WzMapleVersion.BMS) { Name = Path.GetFileName(path) };
        var img = new WzImage("Skill.img");
        var entry = new WzSubProperty("1121008");
        entry.AddProperty(new WzStringProperty("name", skillName));
        entry.AddProperty(new WzStringProperty("desc", skillName + " desc"));
        entry.AddProperty(new WzStringProperty("h1", skillName + " level1"));
        img.AddProperty(entry);
        wz.WzDirectory.AddImage(img);
        wz.SaveToDisk(path, false);
        return path;
    }

    private static WzSubProperty LevelContainer(WzFile skillWz)
        => (WzSubProperty)skillWz.WzDirectory["112.img"]["1121008"]["level"];

    private static string StringName(WzFile stringWz)
        => ((WzStringProperty)stringWz.WzDirectory["Skill.img"]["1121008"]["name"]).Value;

    [Fact]
    public void TwoSources_EachSkillLinksToItsOwnString_BothLoadOrders_AndWritesStayInside()
    {
        RunSta(() =>
        {
            string root = Path.Combine(Path.GetTempPath(), "ShuibbFixValidation",
                "svscope_" + Guid.NewGuid().ToString("N"));
            try
            {
                string aSkill = BuildSkillWz(Path.Combine(root, "srcA", "client", "Data", "Skill", "Skill_000.wz"));
                string aStr = BuildSkillStringWz(Path.Combine(root, "srcA", "client", "Data", "Lang", "zh_TW", "String", "String_000.wz"), "A_SKILL");
                string bSkill = BuildSkillWz(Path.Combine(root, "srcB", "client", "Data", "Skill", "Skill_000.wz"));
                string bStr = BuildSkillStringWz(Path.Combine(root, "srcB", "client", "Data", "String", "String_000.wz"), "B_SKILL");

                foreach (bool aFirst in new[] { true, false })
                {
                    var manager = new WzFileManager();
                    var order = aFirst ? new[] { aStr, aSkill, bStr, bSkill } : new[] { bSkill, bStr, aSkill, aStr };
                    var byPath = new Dictionary<string, WzFile>(StringComparer.OrdinalIgnoreCase);
                    foreach (string path in order)
                        byPath[path] = manager.LoadWzFile(path, WzMapleVersion.BMS);
                    Assert.All(byPath.Values, f => Assert.NotNull(f));

                    var editor = new SkillValueEditor();

                    // A skill -> A's String; the old zh_tw-first global scan already got this
                    // right, but only by accident of A's layout.
                    Assert.True(editor.TryLoad(LevelContainer(byPath[aSkill]), manager));
                    Assert.Equal(aStr, editor.LinkedStringFilePath, ignoreCase: true);
                    Assert.False(editor.IsLinkedStringReadOnly);

                    // B skill -> B's String. The bug resolved this to A (zh_tw path priority),
                    // regardless of load order.
                    var editorB = new SkillValueEditor();
                    Assert.True(editorB.TryLoad(LevelContainer(byPath[bSkill]), manager));
                    Assert.Equal(bStr, editorB.LinkedStringFilePath, ignoreCase: true);
                    Assert.False(editorB.IsLinkedStringReadOnly);

                    // Writes through the resolved entries land in their own source only.
                    ((WzStringProperty)byPath[bStr].WzDirectory["Skill.img"]["1121008"]["name"]).Value = "B_EDITED";
                    Assert.Equal("B_EDITED", StringName(byPath[bStr]));
                    Assert.Equal("A_SKILL", StringName(byPath[aStr]));

                    foreach (var f in byPath.Values)
                        f.Dispose();
                }
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        });
    }

    [Fact]
    public void WrongSourceOnlyLoaded_ResolvesReadOnlyFromOwnDiskOrNothing_NeverTheOtherSource()
    {
        RunSta(() =>
        {
            string root = Path.Combine(Path.GetTempPath(), "ShuibbFixValidation",
                "svscope_" + Guid.NewGuid().ToString("N"));
            try
            {
                // B's skill is open; only A's String is open. A's String must never be linked.
                string bSkill = BuildSkillWz(Path.Combine(root, "srcB", "client", "Data", "Skill", "Skill_000.wz"));
                string bStr = BuildSkillStringWz(Path.Combine(root, "srcB", "client", "Data", "String", "String_000.wz"), "B_SKILL");
                string aStr = BuildSkillStringWz(Path.Combine(root, "srcA", "client", "Data", "Lang", "zh_TW", "String", "String_000.wz"), "A_SKILL");

                var manager = new WzFileManager();
                var skillWz = manager.LoadWzFile(bSkill, WzMapleVersion.BMS);
                var wrongStr = manager.LoadWzFile(aStr, WzMapleVersion.BMS);

                var editor = new SkillValueEditor();
                Assert.True(editor.TryLoad(LevelContainer(skillWz), manager));

                // The loaded-but-wrong-source A String is refused; B's own on-disk String is
                // found through the source-anchored detached read, and only read-only.
                string linked = editor.LinkedStringFilePath;
                Assert.True(linked == null || linked.IndexOf("srcA", StringComparison.OrdinalIgnoreCase) < 0,
                    "linked to the wrong source: " + linked);
                if (linked != null)
                    Assert.True(editor.IsLinkedStringReadOnly, "a detached read must be read-only");

                skillWz.Dispose();
                wrongStr.Dispose();
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        });
    }

    [Fact]
    public void DetachedCache_DoesNotServeOneSourcesStringToTheOther()
    {
        RunSta(() =>
        {
            string root = Path.Combine(Path.GetTempPath(), "ShuibbFixValidation",
                "svscope_" + Guid.NewGuid().ToString("N"));
            try
            {
                // Neither String is OPEN - both resolutions go through the detached disk read,
                // exercising the cache that used to be keyed by nothing at all.
                string aSkill = BuildSkillWz(Path.Combine(root, "srcA", "client", "Data", "Skill", "Skill_000.wz"));
                BuildSkillStringWz(Path.Combine(root, "srcA", "client", "Data", "Lang", "zh_TW", "String", "String_000.wz"), "A_SKILL");
                string bSkill = BuildSkillWz(Path.Combine(root, "srcB", "client", "Data", "Skill", "Skill_000.wz"));
                BuildSkillStringWz(Path.Combine(root, "srcB", "client", "Data", "String", "String_000.wz"), "B_SKILL");

                var manager = new WzFileManager();
                var aWz = manager.LoadWzFile(aSkill, WzMapleVersion.BMS);
                var bWz = manager.LoadWzFile(bSkill, WzMapleVersion.BMS);

                var editor = new SkillValueEditor();
                Assert.True(editor.TryLoad(LevelContainer(aWz), manager));
                string linkedA = editor.LinkedStringFilePath;
                Assert.NotNull(linkedA);
                Assert.Contains("srcA", linkedA, StringComparison.OrdinalIgnoreCase);
                Assert.True(editor.IsLinkedStringReadOnly);

                // Same editor instance, other source's skill: the cached detached file from A
                // must be replaced, not reused.
                Assert.True(editor.TryLoad(LevelContainer(bWz), manager));
                string linkedB = editor.LinkedStringFilePath;
                Assert.NotNull(linkedB);
                Assert.Contains("srcB", linkedB, StringComparison.OrdinalIgnoreCase);

                aWz.Dispose();
                bWz.Dispose();
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        });
    }

    [Fact]
    public void UnloadingTheLinkedString_ThenReselecting_FallsBackSafely()
    {
        RunSta(() =>
        {
            string root = Path.Combine(Path.GetTempPath(), "ShuibbFixValidation",
                "svscope_" + Guid.NewGuid().ToString("N"));
            try
            {
                string aSkill = BuildSkillWz(Path.Combine(root, "srcA", "client", "Data", "Skill", "Skill_000.wz"));
                string aStr = BuildSkillStringWz(Path.Combine(root, "srcA", "client", "Data", "Lang", "zh_TW", "String", "String_000.wz"), "A_SKILL");

                var manager = new WzFileManager();
                var skillWz = manager.LoadWzFile(aSkill, WzMapleVersion.BMS);
                var strWz = manager.LoadWzFile(aStr, WzMapleVersion.BMS);

                var editor = new SkillValueEditor();
                Assert.True(editor.TryLoad(LevelContainer(skillWz), manager));
                Assert.Equal(aStr, editor.LinkedStringFilePath, ignoreCase: true);
                Assert.False(editor.IsLinkedStringReadOnly);

                // Close the String file, then resolve again on a fresh editor: no crash, no
                // stale writable link into a disposed file - a read-only disk fallback (or no
                // link) is the only acceptable outcome.
                manager.UnloadWzFile(strWz, aStr);
                var editor2 = new SkillValueEditor();
                Assert.True(editor2.TryLoad(LevelContainer(skillWz), manager));
                if (editor2.LinkedStringFilePath != null)
                    Assert.True(editor2.IsLinkedStringReadOnly);

                skillWz.Dispose();
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        });
    }
}
