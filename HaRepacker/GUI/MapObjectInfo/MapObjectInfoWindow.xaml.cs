using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace HaRepacker.GUI.MapObjectInfo
{
    /// <summary>
    /// Read-only window for "地圖物件資訊": shows the mapMark/bgm/back/tile/obj/npc/mob/reactor
    /// summary already computed by MapObjectInfoBuilder. This class only renders
    /// MapObjectInfoResult and copies it to the clipboard - it never touches a WzObject.
    /// </summary>
    public partial class MapObjectInfoWindow : ThemedDialogWindow
    {
        private readonly MapObjectInfoResult result;

        public MapObjectInfoWindow(MapObjectInfoResult result)
        {
            this.result = result ?? throw new ArgumentNullException(nameof(result));
            InitializeComponent();
            Populate();
        }

        private void Populate()
        {
            SetSection(selectedMapsText, result.SelectedMaps);
            SetSection(mapMarksText, result.MapMarks);
            SetSection(bgmsText, result.Bgms);
            SetSection(backsText, result.Backs);
            SetSection(tilesText, result.Tiles);
            SetSection(objsText, result.Objs);
            SetSection(npcsText, result.Npcs);
            SetSection(mobsText, result.Mobs);
            SetSection(reactorsText, result.Reactors);
        }

        private static void SetSection(TextBlock target, IReadOnlyList<string> values)
        {
            target.Text = MapObjectInfoResult.JoinForDisplay(values);
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
        /// HaRepacker WPF dialogs (AboutForm/OptionsForm/NewForm) opened from MainForm.
        /// </summary>
        public static void Show(MapObjectInfoResult result)
        {
            using MapObjectInfoWindow window = new MapObjectInfoWindow(result);
            window.ShowDialog();
        }
    }
}
