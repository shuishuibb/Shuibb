using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MapleLib;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace SkillPreview
{
    /// <summary>
    /// The inline editor for an ordinary WZ entity - an item code, a mob, an npc. Selecting the
    /// node lists everything editable underneath it as labelled fields grouped by their container
    /// (info, spec, …), with the matching String.wz name/desc on top.
    ///
    /// Values are staged and only written when the group's 儲存數值 is pressed, matching how
    /// <see cref="SkillValueEditor"/> behaves - nothing here changes the WZ as a side effect of
    /// browsing.
    /// </summary>
    public sealed class NodeEditorPanel : UserControl
    {
        private const double LabelColumnWidth = 118.0;
        private const double FieldHeight = 28.0;
        private const double CardCorner = 8.0;

        private ScrollViewer scroller;
        private StackPanel content;
        private TextBlock headerText;
        private TextBlock statusText;
        private Button themeButton;

        private IPropertyContainer currentNode;
        private string currentNodeName = string.Empty;

        private WzImage stringImage;
        private IPropertyContainer stringEntry;
        private bool stringIsReadOnly;
        private WzFile detachedStringWz;

        // group container -> (field name -> the box showing it). One dictionary per card so a
        // 儲存數值 only writes the group it belongs to.
        private readonly List<GroupBinding> groups = new List<GroupBinding>();
        private readonly Dictionary<string, TextBox> stringBoxes = new Dictionary<string, TextBox>();

        private sealed class GroupBinding
        {
            public IPropertyContainer Container;
            public string Title;
            public readonly Dictionary<string, TextBox> Fields = new Dictionary<string, TextBox>();
        }

        public event EventHandler NodeChanged;

        public NodeEditorPanel()
        {
            BuildLayout();
            ApplyTheme();
            EditorTheme.CurrentChanged += delegate { ApplyTheme(); Rebuild(); };
        }

        // ---- what this panel is willing to edit -------------------------------------------------

        /// <summary>
        /// Accepts a node that behaves like an entity: a container holding scalar values, or
        /// groups of them. Deliberately refuses .img roots and directories - selecting a whole
        /// file should not fill the panel with thousands of fields.
        /// </summary>
        public bool TryLoad(WzObject selected, WzFileManager fileManager)
        {
            IPropertyContainer container = ResolveEditableNode(selected, out string nodeName);
            if (container == null)
            {
                Clear();
                return false;
            }

            currentNode = container;
            currentNodeName = nodeName;
            ResolveStringEntry(fileManager, nodeName);
            Rebuild();
            return true;
        }

        private static IPropertyContainer ResolveEditableNode(WzObject selected, out string nodeName)
        {
            nodeName = null;
            if (selected is WzImage || selected is WzDirectory || selected is WzFile)
                return null;
            if (!(selected is WzSubProperty sub))
                return null;

            // A skill level table has its own dedicated editor; leave it to that one.
            if (LooksLikeSkillLevels(sub))
                return null;

            bool hasSomethingEditable = sub.WzProperties.Any(IsEditableScalar)
                || sub.WzProperties.OfType<IPropertyContainer>().Any(c => c.WzProperties.Any(IsEditableScalar));
            if (!hasSomethingEditable)
                return null;

            nodeName = sub.Name;
            return sub;
        }

        private static bool LooksLikeSkillLevels(WzSubProperty node)
        {
            if (node["level"] is WzSubProperty level
                && level.WzProperties.Any(p => p is WzSubProperty && int.TryParse(p.Name, out _)))
                return true;
            return node.WzProperties.Count(p => p is WzSubProperty && int.TryParse(p.Name, out _)) >= 3;
        }

        private static bool IsEditableScalar(WzImageProperty prop)
        {
            return prop is WzIntProperty or WzLongProperty or WzShortProperty
                or WzFloatProperty or WzDoubleProperty or WzStringProperty
                or WzUOLProperty or WzVectorProperty;
        }

        public void Clear()
        {
            currentNode = null;
            currentNodeName = string.Empty;
            stringImage = null;
            stringEntry = null;
            stringIsReadOnly = false;
            groups.Clear();
            stringBoxes.Clear();
            content.Children.Clear();
            headerText.Text = string.Empty;
            statusText.Text = string.Empty;
        }

        // ---- String.wz ---------------------------------------------------------------------------

        /// <summary>
        /// Item text is keyed without the node's leading zeros - the tree shows 02000012 while
        /// String.wz/Consume.img calls it 2000012 - and equipment nests the id two levels down
        /// under Eqp/&lt;category&gt;. Both are handled here; a miss just means no text card.
        /// </summary>
        private void ResolveStringEntry(WzFileManager fileManager, string nodeName)
        {
            stringImage = null;
            stringEntry = null;
            stringIsReadOnly = false;
            if (fileManager == null || string.IsNullOrEmpty(nodeName))
                return;

            string key = nodeName.TrimStart('0');
            if (key.Length == 0)
                key = nodeName;

            try
            {
                foreach (WzImage image in EnumerateStringImages(fileManager, out bool readOnly))
                {
                    IPropertyContainer found = FindStringEntry(image, key);
                    if (found == null)
                        continue;
                    stringImage = image;
                    stringEntry = found;
                    stringIsReadOnly = readOnly;
                    return;
                }
            }
            catch
            {
                stringImage = null;
                stringEntry = null;
            }
        }

        private IEnumerable<WzImage> EnumerateStringImages(WzFileManager fileManager, out bool readOnly)
        {
            var result = new List<WzImage>();
            readOnly = false;

            // zh_TW first. A client that ships both locales has the same ids in both files, so
            // whichever happens to be enumerated first would otherwise decide the language -
            // and worse, decide which file an edit gets written into.
            var files = new List<WzFile>();
            foreach (WzFile file in fileManager.WzFileList)
            {
                string path = file?.FilePath ?? string.Empty;
                if (path.IndexOf("string", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (file.WzDirectory == null)
                    continue;
                files.Add(file);
            }
            foreach (WzFile file in files.OrderByDescending(
                f => f.FilePath.IndexOf("zh_tw", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                foreach (WzImage image in file.WzDirectory.WzImages)
                    result.Add(image);
            }
            if (result.Count > 0)
                return result;

            // Nothing open - read privately so the text can at least be shown. Never registered
            // with the manager: a file the user never opened would block File > Open on it and
            // could never be saved.
            readOnly = true;
            if (detachedStringWz?.WzDirectory != null)
                return detachedStringWz.WzDirectory.WzImages.ToList();
            return result;
        }

        private static IPropertyContainer FindStringEntry(WzImage image, string key)
        {
            try
            {
                if (!image.Parsed)
                    image.ParseImage();
            }
            catch
            {
                return null;
            }

            if (image[key] is IPropertyContainer direct && HasText(direct))
                return direct;

            // Eqp.img / Etc.img wrap their ids in one or two naming levels.
            foreach (WzImageProperty first in image.WzProperties)
            {
                if (!(first is IPropertyContainer firstLevel))
                    continue;
                if (firstLevel is WzImageProperty named && int.TryParse(named.Name, out _))
                    continue;
                if (((WzImageProperty)firstLevel)[key] is IPropertyContainer second && HasText(second))
                    return second;

                foreach (WzImageProperty middle in firstLevel.WzProperties)
                {
                    if (!(middle is IPropertyContainer secondLevel))
                        continue;
                    if (int.TryParse(middle.Name, out _))
                        continue;
                    if (middle[key] is IPropertyContainer third && HasText(third))
                        return third;
                }
            }
            return null;
        }

        private static bool HasText(IPropertyContainer entry)
        {
            foreach (WzImageProperty prop in entry.WzProperties)
                if (prop is WzStringProperty && (prop.Name == "name" || prop.Name == "desc"))
                    return true;
            return false;
        }

        // ---- building the cards -------------------------------------------------------------------

        private void Rebuild()
        {
            groups.Clear();
            stringBoxes.Clear();
            content.Children.Clear();
            if (currentNode == null)
                return;

            EditorTheme theme = EditorTheme.Current;
            headerText.Text = currentNodeName;

            if (stringEntry != null)
                content.Children.Add(BuildStringCard(theme));

            // The node's own loose values first, then one card per group, in declaration order.
            var loose = currentNode.WzProperties.Where(IsEditableScalar).ToList();
            if (loose.Count > 0)
                content.Children.Add(BuildGroupCard(theme, currentNode, currentNodeName, loose));

            foreach (WzImageProperty child in currentNode.WzProperties)
            {
                if (!(child is IPropertyContainer group))
                    continue;
                var fields = group.WzProperties.Where(IsEditableScalar).ToList();
                if (fields.Count == 0)
                    continue;
                content.Children.Add(BuildGroupCard(theme, group, child.Name, fields));
            }

            int fieldCount = groups.Sum(g => g.Fields.Count);
            statusText.Text = groups.Count + " 組,共 " + fieldCount + " 個可編輯欄位"
                + (stringEntry == null ? "" : stringIsReadOnly ? "  ·  String.wz 未開啟,文字唯讀" : "  ·  已連結 String.wz");
        }

        private UIElement BuildStringCard(EditorTheme theme)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = "STRING 文字",
                Foreground = theme.Muted,
                FontWeight = FontWeights.Bold,
                FontSize = 11.0,
                Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
            });

            foreach (string field in new[] { "name", "desc" })
            {
                if (!(stringEntry[field] is WzStringProperty text))
                    continue;
                bool multiline = field == "desc";
                TextBox box = FieldBox(theme, text.Value ?? string.Empty, multiline);
                box.IsEnabled = !stringIsReadOnly;
                stringBoxes[field] = box;
                stack.Children.Add(LabelledRow(theme, field == "name" ? "名稱" : "說明", box));
            }

            var save = AccentButton(theme, "儲存文字");
            save.Click += delegate { SaveStringFields(); };
            save.IsEnabled = !stringIsReadOnly;
            save.HorizontalAlignment = HorizontalAlignment.Right;
            save.Margin = new Thickness(0.0, 4.0, 0.0, 0.0);
            stack.Children.Add(save);

            return Card(theme, stack);
        }

        private UIElement BuildGroupCard(EditorTheme theme, IPropertyContainer container, string title,
            List<WzImageProperty> fields)
        {
            var binding = new GroupBinding { Container = container, Title = title };
            groups.Add(binding);

            var header = new DockPanel { Margin = new Thickness(0.0, 0.0, 0.0, 10.0) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            DockPanel.SetDock(buttons, Dock.Right);

            var add = PlainButton(theme, "新增");
            add.Click += delegate { AddFieldTo(binding); };
            buttons.Children.Add(add);

            var save = AccentButton(theme, "儲存數值");
            save.Margin = new Thickness(0.0);
            save.Click += delegate { SaveGroup(binding); };
            buttons.Children.Add(save);

            header.Children.Add(buttons);
            header.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = theme.Strong,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            });

            var stack = new StackPanel();
            stack.Children.Add(header);
            foreach (WzImageProperty field in fields)
            {
                TextBox box = FieldBox(theme, DescribeValue(field), false);
                binding.Fields[field.Name] = box;
                stack.Children.Add(LabelledRow(theme, field.Name, box));
            }

            return Card(theme, stack);
        }

        private static string DescribeValue(WzImageProperty prop)
        {
            if (prop is WzVectorProperty vector)
                return vector.X.Value + "," + vector.Y.Value;
            return prop.ToString() ?? string.Empty;
        }

        // ---- writing ------------------------------------------------------------------------------

        private void SaveStringFields()
        {
            if (stringEntry == null || stringIsReadOnly)
            {
                statusText.Text = "String.wz 沒有開啟,文字改不了 —— 請先用「檔案 > 開啟」把 String 檔開起來。";
                return;
            }

            int written = 0;
            foreach (var pair in stringBoxes)
            {
                if (!(stringEntry[pair.Key] is WzStringProperty existing))
                    continue;
                if (existing.Value == pair.Value.Text)
                    continue;
                existing.Value = pair.Value.Text;
                existing.ParentImage.Changed = true;
                written++;
            }
            NodeChanged?.Invoke(this, EventArgs.Empty);
            statusText.Text = written == 0 ? "文字沒有變更。" : "已更新 " + written + " 個文字欄位。";
        }

        private void SaveGroup(GroupBinding binding)
        {
            int written = 0;
            int failed = 0;
            foreach (var pair in binding.Fields)
            {
                WzImageProperty target = ((WzImageProperty)binding.Container)[pair.Key];
                if (target == null)
                    continue;
                if (DescribeValue(target) == pair.Value.Text)
                    continue;
                if (ApplyScalarValue(target, pair.Value.Text))
                {
                    target.ParentImage.Changed = true;
                    written++;
                }
                else
                {
                    failed++;
                }
            }
            NodeChanged?.Invoke(this, EventArgs.Empty);
            statusText.Text = "「" + binding.Title + "」已更新 " + written + " 個數值"
                + (failed > 0 ? "," + failed + " 個型別不符沒有寫入" : "") + "。";
        }

        private void AddFieldTo(GroupBinding binding)
        {
            string name = PromptForText("新增欄位", "節點名稱:");
            if (string.IsNullOrWhiteSpace(name))
                return;
            name = name.Trim();
            if (((WzImageProperty)binding.Container)[name] != null)
            {
                statusText.Text = "「" + name + "」已經存在了。";
                return;
            }

            binding.Container.AddProperty(new WzStringProperty(name, string.Empty));
            ((WzImageProperty)binding.Container).ParentImage.Changed = true;
            NodeChanged?.Invoke(this, EventArgs.Empty);
            Rebuild();
            statusText.Text = "已在「" + binding.Title + "」新增「" + name + "」。";
        }

        private static bool ApplyScalarValue(WzImageProperty prop, string text)
        {
            switch (prop)
            {
                case WzStringProperty s: s.Value = text; return true;
                case WzIntProperty i when int.TryParse(text, out int iv): i.Value = iv; return true;
                case WzLongProperty l when long.TryParse(text, out long lv): l.Value = lv; return true;
                case WzShortProperty sh when short.TryParse(text, out short shv): sh.Value = shv; return true;
                case WzFloatProperty f when float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float fv):
                    f.Value = fv; return true;
                case WzDoubleProperty d when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv):
                    d.Value = dv; return true;
                case WzUOLProperty u: u.Value = text; return true;
                case WzVectorProperty vec:
                    string[] parts = text.Split(',');
                    if (parts.Length != 2 || !int.TryParse(parts[0].Trim(), out int x) || !int.TryParse(parts[1].Trim(), out int y))
                        return false;
                    vec.X.Value = x;
                    vec.Y.Value = y;
                    return true;
                default: return false;
            }
        }

        // ---- layout ---------------------------------------------------------------------------------

        private void BuildLayout()
        {
            var root = new DockPanel { Margin = new Thickness(12.0) };

            var header = new DockPanel { Margin = new Thickness(2.0, 0.0, 2.0, 10.0) };
            DockPanel.SetDock(header, Dock.Top);

            themeButton = new Button
            {
                Content = "淺色",
                Width = 62.0,
                Height = 26.0,
                HorizontalAlignment = HorizontalAlignment.Right,
                ToolTip = "切換淺色 / 深色,立即生效"
            };
            themeButton.Click += delegate { EditorTheme.Toggle(); };
            DockPanel.SetDock(themeButton, Dock.Right);
            header.Children.Add(themeButton);

            headerText = new TextBlock
            {
                FontSize = 16.0,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            header.Children.Add(headerText);
            root.Children.Add(header);

            statusText = new TextBlock
            {
                Margin = new Thickness(2.0, 8.0, 2.0, 0.0),
                TextWrapping = TextWrapping.Wrap
            };
            DockPanel.SetDock(statusText, Dock.Bottom);
            root.Children.Add(statusText);

            content = new StackPanel();
            scroller = new ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            root.Children.Add(scroller);

            Content = root;
        }

        private void ApplyTheme()
        {
            EditorTheme theme = EditorTheme.Current;
            Background = theme.Panel;
            headerText.Foreground = theme.Strong;
            statusText.Foreground = theme.Muted;
            themeButton.Content = theme.IsDark ? "淺色" : "深色";
            themeButton.Background = theme.Field;
            themeButton.Foreground = theme.Text;
            themeButton.BorderBrush = theme.FieldEdge;
        }

        private static Border Card(EditorTheme theme, UIElement child)
        {
            return new Border
            {
                Background = theme.Card,
                BorderBrush = theme.Border,
                BorderThickness = new Thickness(1.0),
                CornerRadius = new CornerRadius(CardCorner),
                Padding = new Thickness(12.0),
                Margin = new Thickness(0.0, 0.0, 0.0, 10.0),
                Child = child
            };
        }

        private static UIElement LabelledRow(EditorTheme theme, string label, TextBox box)
        {
            var grid = new Grid { Margin = new Thickness(0.0, 0.0, 0.0, 6.0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelColumnWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });

            var text = new TextBlock
            {
                Text = label,
                Foreground = theme.Muted,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = label,
                Margin = new Thickness(0.0, 0.0, 10.0, 0.0)
            };
            grid.Children.Add(text);
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
            return grid;
        }

        private static TextBox FieldBox(EditorTheme theme, string value, bool multiline)
        {
            return new TextBox
            {
                Text = value,
                Background = theme.Field,
                Foreground = theme.Text,
                BorderBrush = theme.FieldEdge,
                BorderThickness = new Thickness(1.0),
                Padding = new Thickness(8.0, 4.0, 8.0, 4.0),
                MinHeight = FieldHeight,
                VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                AcceptsReturn = multiline,
                MaxHeight = multiline ? 90.0 : FieldHeight,
                VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden
            };
        }

        private static Button PlainButton(EditorTheme theme, string content)
        {
            return new Button
            {
                Content = content,
                Height = 26.0,
                MinWidth = 62.0,
                Padding = new Thickness(10.0, 0.0, 10.0, 0.0),
                Margin = new Thickness(0.0, 0.0, 8.0, 0.0),
                Background = theme.Field,
                Foreground = theme.Text,
                BorderBrush = theme.FieldEdge,
                BorderThickness = new Thickness(1.0)
            };
        }

        private static Button AccentButton(EditorTheme theme, string content)
        {
            return new Button
            {
                Content = content,
                Height = 26.0,
                MinWidth = 84.0,
                Padding = new Thickness(10.0, 0.0, 10.0, 0.0),
                Margin = new Thickness(0.0, 0.0, 8.0, 0.0),
                FontWeight = FontWeights.Bold,
                Background = theme.Accent,
                Foreground = Brushes.White,
                BorderBrush = theme.AccentEdge,
                BorderThickness = new Thickness(1.0)
            };
        }

        private string PromptForText(string title, string label)
        {
            EditorTheme theme = EditorTheme.Current;
            var dialog = new Window
            {
                Title = title,
                Width = 340,
                Height = 150,
                WindowStyle = WindowStyle.ToolWindow,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                Background = theme.Panel
            };
            var stack = new StackPanel { Margin = new Thickness(14.0) };
            stack.Children.Add(new TextBlock { Text = label, Foreground = theme.Text, Margin = new Thickness(0.0, 0.0, 0.0, 8.0) });
            var input = FieldBox(theme, string.Empty, false);
            stack.Children.Add(input);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0.0, 14.0, 0.0, 0.0)
            };
            string result = null;
            var ok = AccentButton(theme, "確定");
            ok.IsDefault = true;
            ok.Click += delegate { result = input.Text; dialog.Close(); };
            var cancel = PlainButton(theme, "取消");
            cancel.Margin = new Thickness(0.0);
            cancel.IsCancel = true;
            cancel.Click += delegate { dialog.Close(); };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            stack.Children.Add(buttons);

            dialog.Content = stack;
            input.Focus();
            dialog.ShowDialog();
            return result;
        }
    }
}
