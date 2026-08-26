using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        /// <summary>
        /// Stands in for the card built from a node's own loose scalar properties. That card's
        /// Title is the node's own unique name (e.g. a Consume item's "2040000"), so matching by
        /// Title could never line two different items up; every loose card sharing this one key
        /// is what makes "copy 2040000's own fields, paste onto 2040001" work. Not a legal WZ
        /// property name, so it can never collide with a real card Title.
        /// </summary>
        private const string LooseMatchKey = "\0LOOSE";

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

        // Cross-node field copy/paste (plain Ctrl+C / Ctrl+V). Deliberately NOT reset by Rebuild -
        // the whole point is copy on node A, select node B (which rebuilds everything), paste
        // into B's matching card. A confirmed paste writes straight into the WZ; see PasteFields.
        private List<(string Name, string Value)> copiedFields;
        private string copiedFieldsSourceMatchKey;
        private string copiedFieldsSourceDisplayTitle;

        private sealed class GroupBinding
        {
            public IPropertyContainer Container;
            public string Title;

            // True for the one card built from the selected node's own loose properties (see
            // Rebuild: BuildGroupCard(theme, currentNode, currentNodeName, loose)) - its Title is
            // the *item's own unique name* (e.g. "2040000"), not a category name, so two
            // different items' loose cards would never Title-match each other. MatchKey collapses
            // every loose card to one shared key instead, so "copy 2040000's own fields, paste
            // onto 2040001" actually works - the bug reported for Consume items, whose fields sit
            // directly on the item instead of under a shared sub-container like Equip's "info".
            public bool IsLooseFieldsCard;
            public string MatchKey => IsLooseFieldsCard ? LooseMatchKey : Title;

            public readonly Dictionary<string, TextBox> Fields = new Dictionary<string, TextBox>();

            // Selection state for this card's rows, and the row Border each field's label lives
            // in (so ToggleFieldSelection can repaint it). Both are per-card and naturally reset
            // every Rebuild, since a fresh GroupBinding is created each time.
            public readonly HashSet<string> SelectedFieldNames = new HashSet<string>();
            public readonly Dictionary<string, Border> RowBorders = new Dictionary<string, Border>();
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
                content.Children.Add(BuildGroupCard(theme, currentNode, currentNodeName, loose, isLooseFieldsCard: true));

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
            List<WzImageProperty> fields, bool isLooseFieldsCard = false)
        {
            var binding = new GroupBinding { Container = container, Title = title, IsLooseFieldsCard = isLooseFieldsCard };
            groups.Add(binding);

            var header = new DockPanel { Margin = new Thickness(0.0, 0.0, 0.0, 10.0) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            DockPanel.SetDock(buttons, Dock.Right);

            var add = PlainButton(theme, "新增");
            add.Click += delegate { AddFieldTo(binding); };
            buttons.Children.Add(add);

            // Field copy/paste has no buttons of its own by design: multi-select rows (click a
            // label, Ctrl+click to add/remove more) and use plain Ctrl+C / Ctrl+V, which route
            // here through MainForm.MainWindow_PreviewKeyDown - see the shortcut API below.
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
                // binding.Fields stays keyed by the real property name - SaveGroup looks values
                // up by this key when writing back, so the Chinese text below is display-only and
                // never affects what gets saved.
                binding.Fields[field.Name] = box;
                stack.Children.Add(LabelledRow(theme, binding, field.Name, PropertyDisplayName.GetDisplayName(field.Name), box));
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

        /// <summary>
        /// Plain, non-selectable row - used only by BuildStringCard (String.wz name/desc), which
        /// has no GroupBinding to select against. Unchanged from before this feature.
        /// </summary>
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

        /// <summary>
        /// propertyName is the real WZ key (used for selection tracking and as the paste
        /// target lookup); displayLabel is what's actually painted (may be the same string, or
        /// PropertyDisplayName's Chinese translation of it) - purely cosmetic, never touches
        /// binding.Fields or anything that gets saved.
        /// </summary>
        private UIElement LabelledRow(EditorTheme theme, GroupBinding binding, string propertyName, string displayLabel, TextBox box)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelColumnWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });

            var text = new TextBlock
            {
                Text = displayLabel,
                Foreground = theme.Muted,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = displayLabel,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0.0, 0.0, 10.0, 0.0)
            };
            grid.Children.Add(text);
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);

            var rowBorder = new Border
            {
                Child = grid,
                Padding = new Thickness(4.0, 3.0, 4.0, 3.0),
                Margin = new Thickness(-4.0, 0.0, -4.0, 6.0),
                CornerRadius = new CornerRadius(4.0),
                BorderThickness = new Thickness(1.0),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                // Focusable so picking a field can take keyboard focus off whatever TextBox had
                // it - see the click handler below. No focus rectangle: the row already shows
                // its selection with the accent highlight.
                Focusable = true,
                FocusVisualStyle = null
            };

            // Click target is just the label, not the whole row, so clicking into the value box
            // to type still behaves exactly as before - selection never intercepts that.
            //
            // Ctrl+C/Ctrl+V themselves are NOT handled here. WPF tunnels PreviewKeyDown from the
            // Window down, and MainForm's own Window-level handler decides which clipboard the
            // key belongs to (ClipboardShortcutPolicy) before this row ever sees it. That rule
            // gives a focused TextBox priority, so text selected in 名稱 / 值 / X / Y copies as
            // text - which means picking a field here has to move focus off the TextBox, or the
            // following Ctrl+C would still be treated as a text copy and the field clipboard
            // would silently keep its previous contents.
            text.MouseLeftButtonDown += delegate (object sender, MouseButtonEventArgs e)
            {
                ToggleFieldSelection(binding, propertyName, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
                rowBorder.Focus();
                Keyboard.Focus(rowBorder);
                e.Handled = true;
            };

            binding.RowBorders[propertyName] = rowBorder;
            return rowBorder;
        }

        /// <summary>
        /// Plain click selects only this field; Ctrl+click toggles it in/out of the current
        /// selection so several fields can be picked before copying.
        /// </summary>
        private static void ToggleFieldSelection(GroupBinding binding, string propertyName, bool additive)
        {
            if (additive)
            {
                if (!binding.SelectedFieldNames.Add(propertyName))
                    binding.SelectedFieldNames.Remove(propertyName);
            }
            else
            {
                bool wasOnlySelected = binding.SelectedFieldNames.Count == 1 && binding.SelectedFieldNames.Contains(propertyName);
                binding.SelectedFieldNames.Clear();
                if (!wasOnlySelected)
                    binding.SelectedFieldNames.Add(propertyName);
            }

            EditorTheme theme = EditorTheme.Current;
            foreach (KeyValuePair<string, Border> pair in binding.RowBorders)
                ApplyRowSelectionHighlight(theme, pair.Value, binding.SelectedFieldNames.Contains(pair.Key));
        }

        private static void ApplyRowSelectionHighlight(EditorTheme theme, Border rowBorder, bool selected)
        {
            if (selected)
            {
                Color accent = theme.AccentBackground;
                rowBorder.Background = new SolidColorBrush(Color.FromArgb(48, accent.R, accent.G, accent.B));
                rowBorder.BorderBrush = theme.AccentEdge;
            }
            else
            {
                rowBorder.Background = Brushes.Transparent;
                rowBorder.BorderBrush = Brushes.Transparent;
            }
        }

        /// <summary>
        /// binding.Title for the loose-fields card is the *item's own unique name* (e.g.
        /// "2040000" for a Consume entry - see Rebuild), not a shared category name like "info",
        /// so it's never fit to show as if it meant "this kind of card". Everything shown to the
        /// user goes through this instead of Title directly.
        /// </summary>
        private static string DisplayTitleFor(GroupBinding binding) =>
            binding.IsLooseFieldsCard ? "此節點本身的欄位" : binding.Title;

        /// <summary>
        /// Copies the selected fields' current (possibly unsaved) text, tagged with which kind of
        /// card they came from via MatchKey (not the raw Title - see GroupBinding.MatchKey for
        /// why) - paste only accepts them back into a matching card, so copying "info" fields
        /// can't land in an unrelated "icon" card, or a different item's own loose fields, by
        /// mistake.
        /// </summary>
        private void CopySelectedFields(GroupBinding binding)
        {
            if (binding.SelectedFieldNames.Count == 0)
            {
                statusText.Text = "請先選取要複製的欄位——點欄位名稱可選取，Ctrl+點擊可多選。";
                return;
            }

            copiedFields = binding.SelectedFieldNames
                .Where(name => binding.Fields.ContainsKey(name))
                .Select(name => (Name: name, Value: binding.Fields[name].Text))
                .ToList();
            copiedFieldsSourceMatchKey = binding.MatchKey;
            copiedFieldsSourceDisplayTitle = DisplayTitleFor(binding);
            statusText.Text = "已複製「" + copiedFieldsSourceDisplayTitle + "」的 " + copiedFields.Count + " 個欄位，可以到另一個節點的同類卡片貼上。";
        }

        /// <summary>
        /// Finds the container on <paramref name="targetObject"/> that the staged copy belongs
        /// in, by the same MatchKey the cards use: a loose-fields copy goes onto the target's own
        /// scalar properties, a named card's copy goes into the target's same-named sub-container
        /// (so an "info" copy can only ever land in another item's "info", never in spec or icon).
        /// Null when this target has no such container.
        /// </summary>
        private IPropertyContainer ResolveMatchingCardOn(WzObject targetObject)
        {
            IPropertyContainer editable = ResolveEditableNode(targetObject, out _);
            if (editable == null)
                return null;

            if (string.Equals(copiedFieldsSourceMatchKey, LooseMatchKey, StringComparison.Ordinal))
                return editable;

            return ((WzImageProperty)editable)[copiedFieldsSourceMatchKey] as IPropertyContainer;
        }

        /// <summary>
        /// Writes the staged copy into one container, straight into the WZ - a confirmed Ctrl+V is
        /// the commit, so 儲存數值 is not needed afterwards.
        ///
        /// Deliberately walks <see cref="copiedFields"/> rather than every property on the
        /// container (which is what <see cref="SaveGroup"/> does): only the fields this paste
        /// actually carries may be written. Any *other* value the user had typed into a box but
        /// not yet saved stays staged and untouched - a paste must never commit edits the user
        /// hasn't confirmed.
        ///
        /// Adds exactly the properties it really wrote to <paramref name="changedProperties"/> -
        /// the caller marks those, and only those, as changed in the tree. A value the property's
        /// type rejected is not added, so a half-successful paste only reddens the half that
        /// landed.
        /// </summary>
        private void ApplyCopiedFieldsTo(IPropertyContainer card, List<WzImageProperty> changedProperties,
            ref int missing, ref int failed)
        {
            foreach ((string name, string value) in copiedFields)
            {
                WzImageProperty target = ((WzImageProperty)card)[name];
                if (target == null)
                {
                    missing++;
                    continue;
                }

                // Same writer 儲存數值 uses - one type parser for both paths. A value the target
                // property's type can't hold leaves that property completely alone.
                if (!ApplyScalarValue(target, value))
                {
                    failed++;
                    continue;
                }

                // The whole .img still has to be marked dirty so the file saves correctly, even
                // though only this leaf property is what the tree will show in red.
                target.ParentImage.Changed = true;
                changedProperties.Add(target);
            }
        }

        /// <summary>
        /// Mirrors what was just written into the boxes the user can actually see. Only the node
        /// currently on screen has boxes at all - the other targets of a batch paste are edited
        /// straight in the WZ with no UI built for them.
        /// </summary>
        private void SyncDisplayedBoxes(IPropertyContainer card)
        {
            GroupBinding displayed = groups.FirstOrDefault(g => ReferenceEquals(g.Container, card));
            if (displayed == null)
                return;

            foreach ((string name, string value) in copiedFields)
            {
                if (displayed.Fields.TryGetValue(name, out TextBox box))
                    box.Text = value;
            }
        }

        // ---- global Ctrl+C / Ctrl+V routing (MainForm.MainWindow_PreviewKeyDown) ----------------
        //
        // WPF tunnels PreviewKeyDown from the Window down to the focused element, so MainForm's
        // own Window-level PreviewKeyDown handler always runs - and marks the event Handled -
        // before any handler further down the tree (dataTreeView's, or a field row's) ever sees
        // the key. Field copy/paste therefore cannot be implemented down here; it has to be a
        // decision MainForm makes. These members are the state MainForm asks about, and the two
        // actions it invokes once the user has answered the confirmation prompt.
        //
        // The confirmation prompts themselves (Warning.Warn / Properties.Resources.MainConfirm*)
        // deliberately stay in MainForm: they live in HaRepacker, which this project must not
        // reference (SkillPreview -> MapleLib only; HaRepacker -> SkillPreview). So the split is:
        // MainForm decides *whether* to act and asks the user, this panel only reports state and
        // does the work.

        /// <summary>
        /// True when at least one card currently has selected field rows, i.e. a Ctrl+C should
        /// copy those fields rather than the tree's selected WZ node.
        /// </summary>
        public bool HasSelectedFields => groups.Any(g => g.SelectedFieldNames.Count > 0);

        /// <summary>
        /// True while a field copy is staged and hasn't been superseded by a tree-level copy
        /// (see <see cref="ClearCopiedFields"/>), i.e. a Ctrl+V should paste those field values
        /// rather than the tree clipboard's WZ nodes.
        /// </summary>
        public bool HasCopiedFields => copiedFields != null && copiedFields.Count > 0;

        /// <summary>
        /// True when keyboard focus is inside one of this panel's editable value boxes, so
        /// Ctrl+C/Ctrl+V must be left alone as ordinary WPF text copy/paste of the selected text -
        /// neither a field copy nor a tree copy, and no confirmation prompt.
        /// </summary>
        public bool IsValueTextBoxFocused => IsKeyboardFocusWithin && Keyboard.FocusedElement is TextBox;

        /// <summary>
        /// Discards any staged field copy. Called from MainPanel.DoCopy() right before it copies
        /// a whole tree node, so "the last Ctrl+C wins": copying a tree node always cancels a
        /// pending field copy, and the next Ctrl+V then goes back to the tree's normal whole-node
        /// paste instead of applying stale field values.
        /// </summary>
        public void ClearCopiedFields()
        {
            copiedFields = null;
            copiedFieldsSourceMatchKey = null;
            copiedFieldsSourceDisplayTitle = null;
        }

        /// <summary>
        /// Copies the currently selected fields. Only call after <see cref="HasSelectedFields"/>
        /// is true and the user has confirmed - this does no prompting of its own.
        /// </summary>
        public void CopySelectedFieldsShortcut()
        {
            GroupBinding source = groups.FirstOrDefault(g => g.SelectedFieldNames.Count > 0);
            if (source == null)
                return;

            CopySelectedFields(source);
        }

        /// <summary>
        /// Applies the staged field copy onto every given target, writing each value straight
        /// into its WZ property. One call handles the whole tree selection, so the caller prompts
        /// once and pastes into all of them - targets other than the one on screen are edited
        /// directly on their WzObject, with no panel rebuild, no selection change and no UI built
        /// for them.
        ///
        /// Each target is matched by the same MatchKey the cards use, so a Consume item's own
        /// loose fields land on another item's loose fields, and an "info" copy only ever lands in
        /// another item's "info". Only call after <see cref="HasCopiedFields"/> is true and the
        /// user has confirmed - this does no prompting of its own, and never falls back to
        /// anything tree-level.
        /// </summary>
        /// <returns>
        /// The WZ properties this paste actually wrote, across all targets - empty when nothing
        /// changed (no staged copy, no matching card anywhere, or every value was rejected by its
        /// property's type). The caller marks exactly these leaf properties' tree nodes as
        /// changed; returning the properties rather than a count is what keeps the red off their
        /// parents.
        /// </returns>
        public IReadOnlyList<WzImageProperty> PasteCopiedFieldsToTargets(IReadOnlyList<WzObject> targets)
        {
            if (!HasCopiedFields || targets == null || targets.Count == 0)
                return Array.Empty<WzImageProperty>();

            var changedProperties = new List<WzImageProperty>();
            int missing = 0;
            int failed = 0;
            int targetsWithCard = 0;
            int targetsWritten = 0;

            foreach (WzObject target in targets)
            {
                if (target == null)
                    continue;

                IPropertyContainer card = ResolveMatchingCardOn(target);
                if (card == null)
                    continue;

                targetsWithCard++;
                int before = changedProperties.Count;
                ApplyCopiedFieldsTo(card, changedProperties, ref missing, ref failed);
                if (changedProperties.Count > before)
                    targetsWritten++;

                // No-op unless this target happens to be the node currently on screen.
                SyncDisplayedBoxes(card);
            }

            if (changedProperties.Count > 0)
                NodeChanged?.Invoke(this, EventArgs.Empty);

            statusText.Text = SummarizePaste(targets.Count, targetsWithCard, targetsWritten, missing, failed);
            return changedProperties;
        }

        /// <summary>
        /// A clean paste says nothing - the new values showing up is the feedback. Anything
        /// skipped is reported once for the whole batch, never per target.
        /// </summary>
        private string SummarizePaste(int targetCount, int targetsWithCard, int targetsWritten, int missing, int failed)
        {
            // Nothing even had somewhere to put the values - a different message from "the field
            // was there but the value didn't fit", which the notes below cover.
            if (targetsWithCard == 0)
                return "複製的欄位來自「" + copiedFieldsSourceDisplayTitle + "」，選取的節點沒有同類卡片可以貼上。";

            if (missing == 0 && failed == 0 && targetsWritten == targetCount)
                return string.Empty;

            var notes = new List<string>();
            if (targetCount > 1)
                notes.Add(targetsWritten + " 個節點已寫入，" + (targetCount - targetsWritten) + " 個略過");
            if (failed > 0)
                notes.Add("有 " + failed + " 個欄位因型別不符沒有寫入");
            if (missing > 0)
                notes.Add("有 " + missing + " 個欄位找不到同名欄位，已略過");
            return notes.Count > 0 ? string.Join("，", notes) + "。" : string.Empty;
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
