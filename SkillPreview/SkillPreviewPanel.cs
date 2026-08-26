using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MapleLib;
using MapleLib.WzLib;

namespace SkillPreview
{
    /// <summary>
    /// Inline panel showing a skill's hit range per level and its effect animation. The host
    /// drops one of these into its own layout and calls <see cref="TryLoad"/> whenever the
    /// tree selection changes.
    ///
    /// The whole UI is built in code, so this assembly ships no BAML of its own and needs no
    /// resource plumbing from the host.
    /// </summary>
    public sealed class SkillPreviewPanel : UserControl
    {
        private static readonly Color PanelBackground = Color.FromRgb(17, 24, 39);
        private static readonly Color CardBackground = Color.FromRgb(24, 24, 24);
        private static readonly Color CardBorder = Color.FromRgb(75, 85, 99);
        private static readonly Color TextColor = Color.FromRgb(229, 231, 235);
        private static readonly Color MutedTextColor = Color.FromRgb(148, 163, 184);
        private static readonly Color AccentBackground = Color.FromRgb(37, 99, 235);
        private static readonly Color AccentBorder = Color.FromRgb(29, 78, 216);
        private static readonly Color InactiveBackground = Color.FromRgb(31, 41, 55);

        private readonly RangeRenderer rangeRenderer = new RangeRenderer();
        private readonly EffectRenderer effectRenderer = new EffectRenderer();
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private readonly SkillValueEditor valueEditor = new SkillValueEditor();

        private SkillContext skill;

        private TextBlock skillIdText;
        private TextBlock skillSummaryText;
        private Canvas drawSurface;
        private UIElement drawSurfaceCard;
        private UIElement zoomFooter;
        private Button rangeTabButton;
        private Button effectTabButton;
        private Button valuesTabButton;
        private WrapPanel levelButtons;
        private ScrollViewer levelScroller;
        private Panel rangeOptions;
        private Panel effectOptions;
        private WrapPanel effectGroupButtons;
        private CheckBox showCharacterBox;
        private CheckBox mirrorRangeBox;
        private Button backgroundToggle;
        private Slider zoomSlider;
        private TextBlock zoomLabel;
        private TextBlock infoText;

        private bool showingEffect;
        private bool showingValues;
        private bool hasLoadedSkill;
        private DateTime lastTickUtc;

        public SkillPreviewPanel()
        {
            Background = new SolidColorBrush(PanelBackground);
            Content = BuildLayout();

            timer.Interval = TimeSpan.FromMilliseconds(16.0);
            timer.Tick += Timer_Tick;
        }

        /// <summary>
        /// Points the panel at the skill containing <paramref name="selected"/>. Returns false
        /// when the selection is not part of a skill, in which case the host should keep the
        /// panel hidden and nothing here is touched.
        /// </summary>
        public bool TryLoad(WzObject selected, WzFileManager fileManager)
        {
            SkillContext context;
            try
            {
                context = SkillContext.Resolve(selected);
            }
            catch
            {
                context = null;
            }

            if (context == null)
                return false;

            // Reloading the same skill would restart playback and lose the current tab on every
            // tree click, so an unchanged selection is left alone.
            if (skill != null && ReferenceEquals(skill.SkillNode, context.SkillNode))
                return true;

            skill = context;
            rangeRenderer.Skill = context;
            rangeRenderer.SelectedSource = context.RangeSources.Count > 0
                ? context.RangeSources[0].Label
                : null;

            try
            {
                effectRenderer.Load(context.SkillNode, fileManager);
            }
            catch
            {
                effectRenderer.Load(null, null);
            }

            try
            {
                valueEditor.TryLoad(context.SkillNode, fileManager);
            }
            catch
            {
                valueEditor.Clear();
            }

            skillIdText.Text = context.SkillId;
            skillSummaryText.Text = context.BuildSummary();

            RebuildLevelButtons();
            RebuildEffectGroupButtons();

            // Clicking through skills in the tree must not yank the user off the tab they are
            // working on - only the very first skill picks a tab, and after that whichever tab
            // is open stays open (falling back when the new skill has nothing to show there).
            bool firstSkill = !hasLoadedSkill;
            hasLoadedSkill = true;

            if (!firstSkill && showingValues)
                ShowValues();
            else if (!firstSkill && !showingEffect)
                ShowRange();
            else if (effectRenderer.GroupSelections.Count > 0)
                ShowEffect();
            else
                ShowRange();
            return true;
        }

        /// <summary>Halts animation, for when the host hides the panel.</summary>
        public void StopPlayback()
        {
            timer.Stop();
        }

        // ---- layout ---------------------------------------------------------------------

        private UIElement BuildLayout()
        {
            Grid root = new Grid { Margin = new Thickness(10.0) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            root.Children.Add(BuildHeader());

            UIElement selectors = BuildSelectors();
            Grid.SetRow(selectors, 1);
            root.Children.Add(selectors);

            UIElement surface = BuildDrawSurface();
            drawSurfaceCard = surface;
            Grid.SetRow(surface, 2);
            root.Children.Add(surface);

            Grid.SetRow(valueEditor, 2);
            valueEditor.Visibility = Visibility.Collapsed;
            root.Children.Add(valueEditor);

            UIElement footer = BuildFooter();
            zoomFooter = footer;
            Grid.SetRow(footer, 3);
            root.Children.Add(footer);

            return root;
        }

        private UIElement BuildHeader()
        {
            StackPanel header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
            };

            skillIdText = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 18.0,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0.0, 0.0, 14.0, 0.0)
            };
            header.Children.Add(skillIdText);

            skillSummaryText = new TextBlock
            {
                Foreground = new SolidColorBrush(MutedTextColor),
                FontSize = 12.0,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0.0, 0.0, 20.0, 0.0)
            };
            header.Children.Add(skillSummaryText);

            effectTabButton = CreateButton("特效動畫", EffectTab_Click);
            effectTabButton.MinWidth = 92.0;
            header.Children.Add(effectTabButton);

            rangeTabButton = CreateButton("技能範圍", RangeTab_Click);
            rangeTabButton.MinWidth = 92.0;
            header.Children.Add(rangeTabButton);

            valuesTabButton = CreateButton("技能數值", ValuesTab_Click);
            valuesTabButton.MinWidth = 92.0;
            header.Children.Add(valuesTabButton);

            return header;
        }

        private UIElement BuildSelectors()
        {
            StackPanel container = new StackPanel { Margin = new Thickness(0.0, 0.0, 0.0, 8.0) };

            levelButtons = new WrapPanel();
            levelScroller = new ScrollViewer
            {
                Content = levelButtons,
                MaxHeight = 76.0,
                Margin = new Thickness(0.0, 0.0, 0.0, 6.0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            container.Children.Add(levelScroller);

            rangeOptions = new StackPanel { Orientation = Orientation.Horizontal };
            showCharacterBox = CreateCheckBox("顯示角色", true);
            mirrorRangeBox = CreateCheckBox("左右鏡像", false);
            rangeOptions.Children.Add(showCharacterBox);
            rangeOptions.Children.Add(mirrorRangeBox);
            container.Children.Add(rangeOptions);

            effectOptions = new StackPanel { Orientation = Orientation.Horizontal };
            backgroundToggle = CreateButton("切換白底", BackgroundToggle_Click);
            backgroundToggle.MinWidth = 88.0;
            effectOptions.Children.Add(backgroundToggle);
            effectGroupButtons = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            effectOptions.Children.Add(effectGroupButtons);
            container.Children.Add(effectOptions);

            return container;
        }

        private UIElement BuildDrawSurface()
        {
            drawSurface = new Canvas
            {
                Width = PreviewCanvas.Width,
                Height = PreviewCanvas.Height,
                Background = new SolidColorBrush(CardBackground)
            };

            Border card = new Border
            {
                BorderBrush = new SolidColorBrush(CardBorder),
                BorderThickness = new Thickness(1.0),
                CornerRadius = new CornerRadius(10.0),
                Background = new SolidColorBrush(CardBackground),
                ClipToBounds = true,
                Child = new Viewbox
                {
                    Stretch = Stretch.Uniform,
                    Child = drawSurface
                }
            };

            infoText = new TextBlock
            {
                Foreground = new SolidColorBrush(TextColor),
                FontSize = 12.0,
                Margin = new Thickness(10.0, 8.0, 10.0, 8.0),
                TextWrapping = TextWrapping.Wrap
            };

            Border infoCard = new Border
            {
                BorderBrush = new SolidColorBrush(CardBorder),
                BorderThickness = new Thickness(1.0),
                CornerRadius = new CornerRadius(8.0),
                Background = new SolidColorBrush(Color.FromArgb(210, 17, 24, 39)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(14.0),
                Child = infoText
            };

            Grid layered = new Grid();
            layered.Children.Add(card);
            layered.Children.Add(infoCard);
            return layered;
        }

        private UIElement BuildFooter()
        {
            StackPanel footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0.0, 8.0, 0.0, 0.0)
            };

            footer.Children.Add(new TextBlock
            {
                Text = "縮放",
                Foreground = new SolidColorBrush(TextColor),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0.0, 0.0, 8.0, 0.0)
            });

            zoomSlider = new Slider
            {
                Minimum = 0.25,
                Maximum = 3.0,
                Value = 1.0,
                Width = 240.0,
                VerticalAlignment = VerticalAlignment.Center,
                IsSnapToTickEnabled = true,
                TickFrequency = 0.05
            };
            zoomSlider.ValueChanged += Zoom_ValueChanged;
            footer.Children.Add(zoomSlider);

            zoomLabel = new TextBlock
            {
                Text = "100%",
                Foreground = new SolidColorBrush(TextColor),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10.0, 0.0, 0.0, 0.0),
                MinWidth = 44.0
            };
            footer.Children.Add(zoomLabel);

            return footer;
        }

        private void RebuildLevelButtons()
        {
            levelButtons.Children.Clear();
            foreach (SkillRangeSource source in skill.RangeSources)
            {
                Button button = CreateButton(source.Label, LevelButton_Click);
                button.Tag = source.Label;
                button.MinWidth = 40.0;
                levelButtons.Children.Add(button);
            }
        }

        private void RebuildEffectGroupButtons()
        {
            effectGroupButtons.Children.Clear();
            foreach (KeyValuePair<string, string> selection in effectRenderer.GroupSelections)
            {
                Button button = CreateButton(selection.Value, EffectGroupButton_Click);
                button.Tag = selection.Key;
                button.MinWidth = 56.0;
                effectGroupButtons.Children.Add(button);
            }
        }

        private Button CreateButton(string content, RoutedEventHandler onClick)
        {
            Button button = new Button
            {
                Content = content,
                Height = 28.0,
                Padding = new Thickness(10.0, 0.0, 10.0, 0.0),
                Margin = new Thickness(0.0, 0.0, 6.0, 4.0),
                FontWeight = FontWeights.SemiBold,
                Background = new SolidColorBrush(InactiveBackground),
                Foreground = new SolidColorBrush(TextColor),
                BorderBrush = new SolidColorBrush(CardBorder),
                BorderThickness = new Thickness(1.0)
            };
            button.Click += onClick;
            return button;
        }

        private CheckBox CreateCheckBox(string content, bool isChecked)
        {
            CheckBox box = new CheckBox
            {
                Content = content,
                IsChecked = isChecked,
                Foreground = new SolidColorBrush(TextColor),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0.0, 0.0, 14.0, 0.0)
            };
            box.Checked += Option_Changed;
            box.Unchecked += Option_Changed;
            return box;
        }

        private void SetButtonSelected(Button button, bool selected)
        {
            if (button == null)
                return;
            button.Background = new SolidColorBrush(selected ? AccentBackground : InactiveBackground);
            button.Foreground = selected ? Brushes.White : new SolidColorBrush(TextColor);
            button.BorderBrush = new SolidColorBrush(selected ? AccentBorder : CardBorder);
        }

        // ---- mode switching --------------------------------------------------------------

        private void ShowRange()
        {
            showingEffect = false;
            showingValues = false;
            timer.Stop();

            SetButtonSelected(rangeTabButton, true);
            SetButtonSelected(effectTabButton, false);
            SetButtonSelected(valuesTabButton, false);
            levelScroller.Visibility = Visibility.Visible;
            rangeOptions.Visibility = Visibility.Visible;
            effectOptions.Visibility = Visibility.Collapsed;
            drawSurfaceCard.Visibility = Visibility.Visible;
            valueEditor.Visibility = Visibility.Collapsed;
            zoomFooter.Visibility = Visibility.Visible;

            UpdateLevelButtonState();
            Redraw();
        }

        private void ShowEffect()
        {
            showingEffect = true;
            showingValues = false;

            SetButtonSelected(rangeTabButton, false);
            SetButtonSelected(effectTabButton, true);
            SetButtonSelected(valuesTabButton, false);
            levelScroller.Visibility = Visibility.Collapsed;
            rangeOptions.Visibility = Visibility.Collapsed;
            effectOptions.Visibility = Visibility.Visible;
            drawSurfaceCard.Visibility = Visibility.Visible;
            valueEditor.Visibility = Visibility.Collapsed;
            zoomFooter.Visibility = Visibility.Visible;

            UpdateEffectGroupButtonState();
            Redraw();
            RestartPlayback();
        }

        private void ShowValues()
        {
            showingEffect = false;
            showingValues = true;
            timer.Stop();

            SetButtonSelected(rangeTabButton, false);
            SetButtonSelected(effectTabButton, false);
            SetButtonSelected(valuesTabButton, true);
            levelScroller.Visibility = Visibility.Collapsed;
            rangeOptions.Visibility = Visibility.Collapsed;
            effectOptions.Visibility = Visibility.Collapsed;
            drawSurfaceCard.Visibility = Visibility.Collapsed;
            valueEditor.Visibility = Visibility.Visible;
            // 縮放 controls the range/effect canvas zoom and does nothing on this tab - leaving it
            // visible just below 儲存數值 read as part of the value editor instead of the
            // unrelated viewport control it actually is, so it is hidden here rather than merely
            // pushed further away.
            zoomFooter.Visibility = Visibility.Collapsed;
        }

        private void RestartPlayback()
        {
            timer.Stop();
            if (effectRenderer.HasContent)
            {
                lastTickUtc = DateTime.UtcNow;
                timer.Start();
            }
        }

        private void UpdateLevelButtonState()
        {
            foreach (Button button in levelButtons.Children.OfType<Button>())
            {
                bool selected = string.Equals(button.Tag as string, rangeRenderer.SelectedSource,
                    StringComparison.OrdinalIgnoreCase);
                SetButtonSelected(button, selected);
            }
        }

        private void UpdateEffectGroupButtonState()
        {
            foreach (Button button in effectGroupButtons.Children.OfType<Button>())
            {
                bool selected = string.Equals(button.Tag as string, effectRenderer.SelectedGroup,
                    StringComparison.Ordinal);
                SetButtonSelected(button, selected);
            }
        }

        private void Redraw()
        {
            if (drawSurface == null || skill == null)
                return;

            double zoom = zoomSlider == null ? 1.0 : zoomSlider.Value;
            if (zoomLabel != null)
                zoomLabel.Text = Math.Round(zoom * 100.0) + "%";

            if (showingEffect)
            {
                effectRenderer.Zoom = zoom;
                effectRenderer.Draw(drawSurface);
                infoText.Text = effectRenderer.InfoText;
            }
            else
            {
                rangeRenderer.Zoom = zoom;
                rangeRenderer.ShowCharacter = showCharacterBox.IsChecked == true;
                rangeRenderer.MirrorRange = mirrorRangeBox.IsChecked == true;
                rangeRenderer.Draw(drawSurface);
                infoText.Text = rangeRenderer.InfoText;
            }
        }

        // ---- events -----------------------------------------------------------------------

        private void Timer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (!effectRenderer.HasContent)
                {
                    timer.Stop();
                    return;
                }

                DateTime nowUtc = DateTime.UtcNow;
                int delta = (int)(nowUtc - lastTickUtc).TotalMilliseconds;
                lastTickUtc = nowUtc;

                effectRenderer.Advance(delta);
                Redraw();
            }
            catch
            {
                timer.Stop();
            }
        }

        private void RangeTab_Click(object sender, RoutedEventArgs e)
        {
            ShowRange();
        }

        private void EffectTab_Click(object sender, RoutedEventArgs e)
        {
            ShowEffect();
        }

        private void ValuesTab_Click(object sender, RoutedEventArgs e)
        {
            ShowValues();
        }

        private void LevelButton_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            if (button == null)
                return;
            rangeRenderer.SelectedSource = button.Tag as string;
            UpdateLevelButtonState();
            Redraw();
        }

        private void EffectGroupButton_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            if (button == null)
                return;

            effectRenderer.SelectGroup(button.Tag as string);
            UpdateEffectGroupButtonState();
            Redraw();
            RestartPlayback();
        }

        private void BackgroundToggle_Click(object sender, RoutedEventArgs e)
        {
            effectRenderer.WhiteBackground = !effectRenderer.WhiteBackground;
            backgroundToggle.Content = effectRenderer.WhiteBackground ? "切換黑底" : "切換白底";
            Redraw();
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            Redraw();
        }

        private void Zoom_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            Redraw();
        }
    }
}
