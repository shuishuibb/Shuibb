using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MapleLib;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using SkillPreview;

// Drives NodeEditorPanel against item 02000012 - the exact node in the reference screenshot -
// using the real Item.wz and String.wz.
class Program
{
    static StreamWriter log;
    static int passed, failed;
    const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;

    [STAThread]
    static void Main()
    {
        log = new StreamWriter(Path.Combine(AppContext.BaseDirectory, "nodetest.txt"), false);
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
        string root = @"D:\3.私服檔案\技術谷4.0";
        WzFile consume = Open(Path.Combine(root, @"Data\Item\Consume\Consume_000.wz"));
        Check("Consume_000.wz opened", consume != null);
        if (consume == null) return;

        WzImage img = consume.WzDirectory.WzImages.FirstOrDefault(i => i.Name == "0200.img");
        img.ParseImage();
        var item = img["02000012"] as WzSubProperty;
        Check("found item 02000012 (the one in the screenshot)", item != null);
        if (item == null) return;

        // String.wz open, the way the editor requires for text editing.
        var fm = new WzFileManager(root, false);
        fm.BuildWzFileList();
        // Load BOTH locales, the way a real client folder has them - the panel has to pick
        // zh_TW rather than whichever the file manager happened to index first.
        WzFile twString = null, cnString = null;
        foreach (WzMapleVersion v in new[] { WzMapleVersion.BMS, WzMapleVersion.GMS, WzMapleVersion.EMS, WzMapleVersion.CLASSIC })
        {
            try { twString = fm.LoadWzFile(Path.Combine(root, @"Data\Lang\zh_TW\String\String_000.wz"), v); break; } catch { }
        }
        foreach (WzMapleVersion v in new[] { WzMapleVersion.BMS, WzMapleVersion.GMS, WzMapleVersion.EMS, WzMapleVersion.CLASSIC })
        {
            try { cnString = fm.LoadWzFile(Path.Combine(root, @"Data\Lang\zh_CN\String\String_000.wz"), v); break; } catch { }
        }
        Check("zh_TW String opened", twString != null);
        log.WriteLine("  zh_CN String also loaded: " + (cnString != null));

        var panel = new NodeEditorPanel();
        bool loaded = panel.TryLoad(item, fm);
        Check("the panel accepts an item node", loaded);

        FieldInfo F(string n) => typeof(NodeEditorPanel).GetField(n, NP);

        // --- String text, resolved through the leading-zero rule ---
        var entry = F("stringEntry").GetValue(panel);
        Check("it linked the item to its String.wz entry", entry != null);
        Check("the entry is writable (String.wz is open)", !(bool)F("stringIsReadOnly").GetValue(panel));

        var boxes = (Dictionary<string, System.Windows.Controls.TextBox>)F("stringBoxes").GetValue(panel);
        CheckEq("名稱 shows the real item name", "特殊藥水", boxes.TryGetValue("name", out var nb) ? nb.Text : null);
        Check("說明 shows the real item desc",
            boxes.TryGetValue("desc", out var db) && db.Text.StartsWith("傳說中的神祕藥水"), db?.Text);

        // --- groups, matching what the reference lists ---
        var groups = F("groups").GetValue(panel) as System.Collections.IList;
        var titles = new List<string>();
        var fieldsByTitle = new Dictionary<string, List<string>>();
        foreach (object g in groups)
        {
            Type gt = g.GetType();
            string title = (string)gt.GetField("Title").GetValue(g);
            var fields = (Dictionary<string, System.Windows.Controls.TextBox>)gt.GetField("Fields").GetValue(g);
            titles.Add(title);
            fieldsByTitle[title] = fields.Keys.ToList();
        }
        log.WriteLine("  groups: " + string.Join(" | ", titles.Select(t => t + "(" + fieldsByTitle[t].Count + ")")));
        Check("an 'info' group is listed", titles.Contains("info"));
        Check("a 'spec' group is listed", titles.Contains("spec"));
        Check("info exposes price", fieldsByTitle.ContainsKey("info") && fieldsByTitle["info"].Contains("price"));
        Check("info exposes slotMax", fieldsByTitle.ContainsKey("info") && fieldsByTitle["info"].Contains("slotMax"));
        Check("icon canvases are NOT listed as editable text",
            fieldsByTitle.ContainsKey("info") && !fieldsByTitle["info"].Contains("icon"));

        // --- editing a value writes only on demand ---
        var infoGroup = groups.Cast<object>().First(g => (string)g.GetType().GetField("Title").GetValue(g) == "info");
        var infoFields = (Dictionary<string, System.Windows.Controls.TextBox>)infoGroup.GetType().GetField("Fields").GetValue(infoGroup);
        var priceProp = (item["info"] as WzSubProperty)["price"] as WzIntProperty;
        int originalPrice = priceProp.Value;
        log.WriteLine("  price before = " + originalPrice);

        infoFields["price"].Text = "4321";
        CheckEq("typing does not touch the WZ yet", originalPrice, priceProp.Value);

        typeof(NodeEditorPanel).GetMethod("SaveGroup", NP).Invoke(panel, new object[] { infoGroup });
        CheckEq("儲存數值 writes the value into the WZ", 4321, priceProp.Value);
        Check("the image is marked changed", priceProp.ParentImage.Changed);

        // A value that cannot be the node's type must be reported, not silently written.
        infoFields["price"].Text = "abc";
        typeof(NodeEditorPanel).GetMethod("SaveGroup", NP).Invoke(panel, new object[] { infoGroup });
        CheckEq("a non-numeric entry leaves an int node alone", 4321, priceProp.Value);
        var status = (System.Windows.Controls.TextBlock)F("statusText").GetValue(panel);
        Check("...and says so", status.Text.Contains("型別不符"), status.Text);

        // --- String text write ---
        infoFields["price"].Text = originalPrice.ToString();
        typeof(NodeEditorPanel).GetMethod("SaveGroup", NP).Invoke(panel, new object[] { infoGroup });

        boxes["name"].Text = "改過的名字";
        typeof(NodeEditorPanel).GetMethod("SaveStringFields", NP).Invoke(panel, null);
        var live = entry.GetType().GetProperty("WzProperties") != null
            ? ((IPropertyContainer)entry)["name"] as WzStringProperty : null;
        CheckEq("儲存文字 writes the String.wz name", "改過的名字", live?.Value);

        // --- what it must refuse ---
        Check("an .img root is refused", !panel.TryLoad(img, fm));
        var skillFile = Open(Path.Combine(root, @"Data\Skill\Skill_000.wz"));
        if (skillFile != null)
        {
            WzImage skillImg = null;
            WzSubProperty skillNode = null;
            foreach (WzImage si in skillFile.WzDirectory.WzImages)
            {
                try
                {
                    si.ParseImage();
                    if (!(si["skill"] is WzImageProperty list)) continue;
                    foreach (WzImageProperty n in list.WzProperties)
                        if (n is WzSubProperty sp && sp["level"] is WzSubProperty) { skillImg = si; skillNode = sp; break; }
                }
                catch { }
                if (skillNode != null) break;
            }
            Check("a skill node is left to the skill editor", skillNode == null || !panel.TryLoad(skillNode, fm));
        }

        // --- theme switches live ---
        log.WriteLine("");
        bool startedDark = EditorTheme.Current.IsDark;
        var panel2 = new NodeEditorPanel();
        panel2.TryLoad(item, fm);
        EditorTheme.Toggle();
        CheckEq("toggling flips the theme", !startedDark, EditorTheme.Current.IsDark);
        var themeBtn = (System.Windows.Controls.Button)typeof(NodeEditorPanel)
            .GetField("themeButton", NP).GetValue(panel2);
        CheckEq("the button offers the other theme", startedDark ? "深色" : "淺色", themeBtn.Content);
        Check("the panel repainted itself to the new palette",
            ((System.Windows.Media.SolidColorBrush)panel2.Background).Color == EditorTheme.Current.PanelBackground);
        Check("an already-open panel repainted too",
            ((System.Windows.Media.SolidColorBrush)panel.Background).Color == EditorTheme.Current.PanelBackground);
        EditorTheme.SetDark(startedDark);
        CheckEq("switching back works too", startedDark, EditorTheme.Current.IsDark);
    }

    static WzFile Open(string path)
    {
        if (!File.Exists(path)) return null;
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
