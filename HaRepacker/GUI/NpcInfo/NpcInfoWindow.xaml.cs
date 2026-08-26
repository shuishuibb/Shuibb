using System;
using System.Windows;
using System.Windows.Controls;

namespace HaRepacker.GUI.NpcInfo
{
    /// <summary>
    /// Read-only window for "NPC 詳細資訊": shows the ID/name/String-extras/WZ path/animation
    /// summary already computed by NpcInfoBuilder. This class only renders NpcInfoResult and
    /// copies it to the clipboard - it never touches a WzObject.
    /// </summary>
    public partial class NpcInfoWindow : ThemedDialogWindow
    {
        private readonly NpcInfoResult result;

        public NpcInfoWindow(NpcInfoResult result)
        {
            this.result = result ?? throw new ArgumentNullException(nameof(result));
            InitializeComponent();
            Populate();
        }

        private void Populate()
        {
            npcIdText.Text = NpcInfoResult.DisplayScalar(result.NpcId);
            npcNameText.Text = NpcInfoResult.DisplayScalar(result.NpcName);
            stringExtrasText.Text = NpcInfoResult.JoinForDisplay(result.StringExtras);
            wzPathText.Text = NpcInfoResult.DisplayScalar(result.WzPath);
            animationsText.Text = NpcInfoResult.JoinForDisplay(result.Animations);
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
        /// HaRepacker WPF dialogs (AboutForm/OptionsForm/NewForm/MapObjectInfoWindow).
        /// </summary>
        public static void Show(NpcInfoResult result)
        {
            using NpcInfoWindow window = new NpcInfoWindow(result);
            window.ShowDialog();
        }
    }
}
