using System;
using System.Windows;
using System.Windows.Controls;

namespace HaRepacker.GUI.EquipmentStringInfo
{
    /// <summary>
    /// Read-only window for "裝備 String 資訊": shows the item id/equip WZ path/String.wz source
    /// breakdown already computed by EquipmentStringInfoBuilder. This class only renders
    /// EquipmentStringInfoResult and copies it to the clipboard - it never touches a WzObject.
    ///
    /// One source card per EquipmentStringInfoResult.Sources entry is built here in code, since
    /// the count varies (String.wz not loaded / one loaded / several loaded with differing
    /// results) - the static part of the layout stays in the .xaml.
    /// </summary>
    public partial class EquipmentStringInfoWindow : ThemedDialogWindow
    {
        private readonly EquipmentStringInfoResult result;

        public EquipmentStringInfoWindow(EquipmentStringInfoResult result)
        {
            this.result = result ?? throw new ArgumentNullException(nameof(result));
            InitializeComponent();
            Populate();
        }

        private void Populate()
        {
            itemIdText.Text = EquipmentStringInfoResult.DisplayScalar(result.ItemId);
            wzPathText.Text = EquipmentStringInfoResult.DisplayScalar(result.EquipWzPath);

            bool showSourceHeader = result.Sources.Count > 1;
            for (int i = 0; i < result.Sources.Count; i++)
            {
                sourcesPanel.Children.Add(BuildSourceCard(result.Sources[i], i + 1, showSourceHeader));
            }
        }

        private Border BuildSourceCard(EquipmentStringSourceResult source, int index, bool showSourceHeader)
        {
            Style cardStyle = (Style)FindResource("HareCardStyle");
            Style titleStyle = (Style)FindResource("HareSectionTitleStyle");

            StackPanel content = new StackPanel();
            bool wroteHeaderLine = false;

            if (!source.IsPlaceholder)
            {
                if (showSourceHeader)
                {
                    content.Children.Add(new TextBlock { Text = "來源 " + index, Style = titleStyle });
                }
                content.Children.Add(new TextBlock
                {
                    Text = string.Join(", ", source.SourceFileNames),
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, showSourceHeader ? 4 : 0, 0, 0)
                });
                wroteHeaderLine = true;
            }

            AddField(content, titleStyle, "名稱", EquipmentStringInfoResult.DisplayScalar(source.Name), isFirst: !wroteHeaderLine);
            AddField(content, titleStyle, "String.wz logical path", EquipmentStringInfoResult.DisplayScalar(source.LogicalPath), isFirst: false);
            AddField(content, titleStyle, "String 額外資訊", EquipmentStringInfoResult.JoinForDisplay(source.Extras), isFirst: false);

            return new Border { Style = cardStyle, Margin = new Thickness(0, 0, 0, 10), Child = content };
        }

        private static void AddField(StackPanel panel, Style titleStyle, string label, string value, bool isFirst)
        {
            panel.Children.Add(new TextBlock { Text = label, Style = titleStyle, Margin = new Thickness(0, isFirst ? 0 : 10, 0, 0) });
            panel.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(result.ToClipboardText());
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(UiLocalization.Translate("An error occurred: {0}"), ex.Message),
                    UiLocalization.Translate("Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// Opens the window modally for the given summary. No explicit owner, same as the other
        /// HaRepacker WPF dialogs (AboutForm/OptionsForm/NewForm/MapObjectInfoWindow/NpcInfoWindow).
        /// </summary>
        public static void Show(EquipmentStringInfoResult result)
        {
            using EquipmentStringInfoWindow window = new EquipmentStringInfoWindow(result);
            window.ShowDialog();
        }
    }
}
