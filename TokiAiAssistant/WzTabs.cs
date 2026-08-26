using System;
using System.Collections.Generic;
using System.Reflection;
using HaRepacker.GUI;
using HaRepacker.GUI.Panels;

namespace TokiAi
{
    /// <summary>One open editor tab and the panel that owns its WZ tree.</summary>
    public class WzTab
    {
        public int Number;          // 1-based, matching the [N] prefix used in tool paths
        public string Name;
        public MainPanel Panel;

        public override string ToString()
        {
            return "[" + Number + "] " + Name;
        }
    }

    /// <summary>
    /// Resolves the editor's open tabs. Each tab owns its own MainPanel and therefore its own
    /// WZ tree, so an assistant that held a single MainPanel could only ever see the tab it was
    /// opened from - which makes porting between two loaded versions impossible. The list is
    /// re-read on every tool call so opening or closing a tab mid-conversation is picked up.
    /// </summary>
    public static class WzTabs
    {
        public static List<WzTab> Enumerate(MainPanel anchor)
        {
            List<WzTab> tabs = new List<WzTab>();
            if (anchor == null)
                return tabs;

            try
            {
                MainForm form = anchor.MainForm;
                if (form == null)
                    return Single(anchor);

                // The TabControl is a XAML-generated field on MainForm, so it is reached by
                // reflection rather than a compile-time reference.
                FieldInfo field = typeof(MainForm).GetField("tabControl_MainPanels",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                System.Windows.Controls.TabControl control = field == null
                    ? null
                    : field.GetValue(form) as System.Windows.Controls.TabControl;
                if (control == null)
                    return Single(anchor);

                int number = 1;
                foreach (object item in control.Items)
                {
                    System.Windows.Controls.TabItem tabItem = item as System.Windows.Controls.TabItem;
                    MainPanel panel = tabItem == null ? null : tabItem.Content as MainPanel;
                    if (panel == null)
                        continue;
                    WzTab tab = new WzTab();
                    tab.Number = number++;
                    tab.Name = tabItem.Header == null ? "分頁" + tab.Number : tabItem.Header.ToString();
                    tab.Panel = panel;
                    tabs.Add(tab);
                }
            }
            catch
            {
                return Single(anchor);
            }

            return tabs.Count == 0 ? Single(anchor) : tabs;
        }

        static List<WzTab> Single(MainPanel panel)
        {
            List<WzTab> tabs = new List<WzTab>();
            WzTab tab = new WzTab();
            tab.Number = 1;
            tab.Name = "目前分頁";
            tab.Panel = panel;
            tabs.Add(tab);
            return tabs;
        }
    }
}
