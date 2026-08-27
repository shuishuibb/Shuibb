using HaRepacker.GUI.Panels;
using MapleLib.Configuration;
using MapleLib.MapleCryptoLib;
using MapleLib.WzLib;
using MapleLib.WzLib.MSFile;
using MapleLib.WzLib.Util;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Windows.Forms;

namespace HaRepacker.GUI
{
    public partial class SaveForm : ThemedDialogWindow
    {
        private readonly WzNode wzNode;

        private readonly WzFile wzf; // it can either be a WzImage or a WzFile only.
        private readonly WzImage wzImg; // it can either be a WzImage or a WzFile only.

        private readonly bool IsRegularWzFile = false; // or data.wz

        public string path;
        private readonly MainPanel _mainPanel;
        private int defaultVersionIndex;

        private bool bIsLoaded = false;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="panel"></param>
        /// <param name="wzNode"></param>
        public SaveForm(MainPanel panel, WzNode wzNode)
        {
            InitializeComponent();
            ApplyLocalizedText();

            WzEncryptionUiShared.Populate(encryptionBox);

            this.wzNode = wzNode;
            if (wzNode.Tag is WzImage image) // Data.wz hotfix file
            {
                this.wzImg = image;
                this.IsRegularWzFile = false;

                // Data.wz uses BMS encryption... no sepcific version indicated
                SetWzEncryptionBoxSelectionByWzMapleVersion(WzMapleVersion.BMS);

                versionBox.IsEnabled = false; // disable, not necessary
                checkBox_64BitFile.IsEnabled = false; // disable, not necessary
            }
            else
            {
                this.wzf = (WzFile)wzNode.Tag;
                this.IsRegularWzFile = true;

                SetWzEncryptionBoxSelectionByWzMapleVersion(wzf.MapleVersion);

                versionBox.Text = wzf.Version.ToString();
                versionBox.IsEnabled = !wzf.Is64BitWzFile;
                checkBox_64BitFile.IsChecked = wzf.Is64BitWzFile;
            }
            this._mainPanel = panel;

            defaultVersionIndex = encryptionBox.SelectedIndex;
            Closed += (sender, args) => encryptionBox.SelectedIndex = defaultVersionIndex;

            bIsLoaded = true;
        }

        private string LocalizedText(string key, string fallback) => WpfDialogSupport.Text(typeof(SaveForm), key, fallback);

        private void ApplyLocalizedText()
        {
            Title = LocalizedText("$this.Text", "Save");
            formatHeader.Text = LocalizedText("groupBox1.Text", "File format selection:");
            radioButton_wzFile.Content = LocalizedText("radioButton_wzFile.Text", "Save as .wz file");
            radioButton1.Content = LocalizedText("radioButton1.Text", "Save as .ms file (encrypted. v220++)");
            versionLabel.Text = LocalizedText("label1.Text", "Version");
            encryptionLabel.Text = LocalizedText("label2.Text", "Encryption");
            checkBox_64BitFile.Content = LocalizedText("checkBox_64BitFile.Text", "No version number");
            saveButton.Content = LocalizedText("saveButton.Text", "Save");
        }

        /// <summary>
        /// --- Helper function to keep UI synchronized ---
        /// </summary>
        private void UpdateUIState()
        {
            if (groupBox_wzSaveSelection == null || versionBox == null)
                return;

            // The WZ Options group box is only enabled if the WZ Radio button is checked.
            groupBox_wzSaveSelection.IsEnabled = radioButton_wzFile.IsChecked == true;

            versionBox.IsEnabled = checkBox_64BitFile.IsChecked != true;
        }


        /// <summary>
        /// On encryption box selection changed
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void encryptionBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (!bIsLoaded)
                return;

            EncryptionKey selectedEncryption = (EncryptionKey)encryptionBox.SelectedItem;
            if (selectedEncryption.MapleVersion == WzMapleVersion.CUSTOM)
            {
                Program.ConfigurationManager.SetCustomWzUserKeyFromConfig();
            }
            else
            {
                MapleCryptoConstants.UserKey_WzLib = MapleCryptoConstants.MAPLESTORY_USERKEY_DEFAULT.ToArray();
            }
        }

        private void SetWzEncryptionBoxSelectionByWzMapleVersion(WzMapleVersion versionSelected)
        {
            encryptionBox.SelectedIndex = MainForm.GetIndexByWzMapleVersion(versionSelected);
            if (versionSelected == WzMapleVersion.CUSTOM)
            {
                Program.ConfigurationManager.SetCustomWzUserKeyFromConfig();
            }
        }

        /// <summary>
        /// On save button clicked
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void SaveButton_Click(object sender, EventArgs e)
        {
            int version = WpfDialogSupport.ParseInteger(versionBox.Text, -1);
            if (version < 0)
            {
                Warning.Error(Properties.Resources.SaveVersionError);
                return;
            }

            bool bSaveAsWzFile = radioButton_wzFile.IsChecked == true;

            if (bSaveAsWzFile)
            {
                using (SaveFileDialog dialog = new()
                {
                    Title = Properties.Resources.SelectOutWz,
                    FileName = wzNode.Text,
                    Filter = string.Format("{0}|*.wz",
                    Properties.Resources.WzFilter)
                })
                {
                    if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                        return;

                    bool bSaveAs64BitWzFile = checkBox_64BitFile.IsChecked == true; // no version number
                    WzMapleVersion wzMapleVersionSelected = ((EncryptionKey)encryptionBox.SelectedItem).MapleVersion; // new encryption selected
                    var wzFileManager = Program.EnsureWzFileManager();
                    if (this.IsRegularWzFile)
                    {

                        if (wzf.MapleVersion != wzMapleVersionSelected)
                        {
                            PrepareAllImgs(wzf.WzDirectory);
                        }
                        wzf.Version = (short)version;
                        wzf.MapleVersion = wzMapleVersionSelected;

                        // Always written beside the target first, then committed - the original
                        // is never deleted before the new bytes are known to be complete.
                        string wzTempPath = dialog.FileName + "$tmp";
                        try
                        {
                            wzf.SaveToDisk(wzTempPath, bSaveAs64BitWzFile, wzMapleVersionSelected);
                        }
                        catch (Exception ex)
                        {
                            // Nothing was unloaded and the original was not touched: report and
                            // leave the dialog open so another location can be tried at once.
                            // Without this catch the exception (no write permission, disk full)
                            // used to escape to the global handler and close the whole app.
                            try { if (File.Exists(wzTempPath)) File.Delete(wzTempPath); } catch { }
                            ShowSaveError(dialog.FileName, ex.Message, null, null);
                            return;
                        }

                        // The original must be released before it can be moved aside.
                        string originalPath = wzf.FilePath;
                        _mainPanel.MainForm.UnloadWzFile(wzf);

                        SaveCommitResult commit = SaveFileCommit.Replace(wzTempPath, dialog.FileName);
                        if (!commit.Success)
                        {
                            ShowSaveError(dialog.FileName, commit.Error, commit.TempPathKept, commit.OriginalMovedTo);
                        }

                        // Reload whatever survived: the new file on success, the restored original
                        // after a failed overwrite - and when saving to a different path failed
                        // before anything was created there, the untouched original itself, so the
                        // unload above never silently costs the tree its file.
                        string reloadPath = File.Exists(dialog.FileName) ? dialog.FileName
                            : File.Exists(originalPath) ? originalPath
                            : null;
                        if (reloadPath != null)
                        {
                            WzFile loadedWzFile = wzFileManager.LoadWzFile(reloadPath, wzMapleVersionSelected);
                            if (loadedWzFile != null)
                            {
                                _mainPanel.MainForm.AddLoadedWzObjectToMainPanel(loadedWzFile);
                            }
                        }
                        if (!commit.Success)
                            return;
                    }
                    else
                    {
                        byte[] WzIv = WzTool.GetIvByMapleVersion(wzMapleVersionSelected);

                        // Save file
                        string tmpFilePath = dialog.FileName + ".tmp";
                        string targetFilePath = dialog.FileName;

                        try
                        {
                            // FileMode.Create, not OpenOrCreate: a stale, longer .tmp from an
                            // earlier attempt would otherwise keep its tail beyond what this
                            // write produces.
                            using (FileStream oldfs = File.Open(tmpFilePath, FileMode.Create))
                            {
                                using (WzBinaryWriter wzWriter = new WzBinaryWriter(oldfs, WzIv))
                                {
                                    wzImg.SaveImage(wzWriter, true); // Write to temp folder
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // The image is untouched and its node stays in the tree - report and
                            // let the user pick another location. This used to be swallowed (or,
                            // for UnauthorizedAccessException, mis-reported as an open failure)
                            // while the node was deleted anyway, so a failed save looked done and
                            // the edits were gone.
                            try { if (File.Exists(tmpFilePath)) File.Delete(tmpFilePath); } catch { }
                            ShowSaveError(targetFilePath, ex.Message, null, null);
                            return;
                        }

                        SaveCommitResult imgCommit = SaveFileCommit.Replace(tmpFilePath, targetFilePath);
                        if (!imgCommit.Success)
                        {
                            ShowSaveError(targetFilePath, imgCommit.Error, imgCommit.TempPathKept, imgCommit.OriginalMovedTo);
                            return;
                        }

                        // Only after the bytes are confirmed at the target may the old node go.
                        wzNode.DeleteWzNode(); // this is a WzImage, and cannot be unloaded by _mainPanel.MainForm.UnloadWzFile

                        // Reload the new file
                        WzImage img = wzFileManager.LoadDataWzHotfixFile(dialog.FileName, wzMapleVersionSelected);
                        if (img == null)
                        {
                            MessageBox.Show(Properties.Resources.MainFileOpenFail, HaRepacker.Properties.Resources.Error);
                            return;
                        }
                        _mainPanel.MainForm.AddLoadedWzObjectToMainPanel(img);
                    }
                }
            } else
            {
                // save as .ms file
                using (SaveFileDialog dialog = new()
                {
                    Title = Properties.Resources.SelectOutWz,
                    FileName = wzNode.Text.Replace("wz", "ms"),
                    Filter = string.Format("{0}|*.ms",
                    Properties.Resources.MsFilter)
                })
                {
                    if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                        return;

                    // Same shape as the .wz path: build beside the target, then commit, so a
                    // failure never leaves the target half-written. FileMode.Create also stops a
                    // longer pre-existing file from keeping its tail past the new data, which the
                    // old direct OpenOrCreate write could produce.
                    string msTempPath = dialog.FileName + "$tmp";
                    try
                    {
                        using (var memoryStream = new MemoryStream())
                        {
                            var msFile = new WzMsFile(memoryStream, Path.GetFileName(dialog.FileName), dialog.FileName, true, isSavingFile: true);
                            var savedStream = msFile.Save(wzf);

                            using (var fileStream = new FileStream(msTempPath, FileMode.Create))
                            {
                                savedStream.CopyTo(fileStream);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        try { if (File.Exists(msTempPath)) File.Delete(msTempPath); } catch { }
                        ShowSaveError(dialog.FileName, ex.Message, null, null);
                        return;
                    }

                    SaveCommitResult msCommit = SaveFileCommit.Replace(msTempPath, dialog.FileName);
                    if (!msCommit.Success)
                    {
                        ShowSaveError(dialog.FileName, msCommit.Error, msCommit.TempPathKept, msCommit.OriginalMovedTo);
                        return;
                    }
                }
            }

            Close();
        }


        /// <summary>
        /// One shape for every save failure: what could not be written, why, and where any
        /// recoverable copies are. Never rethrows - a failed save must end in a message, not in
        /// the global handler closing the app.
        /// </summary>
        private static void ShowSaveError(string targetPath, string reason, string tempPathKept, string originalMovedTo)
        {
            string message = UiLocalization.Translate("存檔失敗")
                + "\n\n" + UiLocalization.Translate("目標檔案：") + "\n" + targetPath
                + "\n\n" + UiLocalization.Translate("原因：") + "\n" + reason;
            if (!string.IsNullOrEmpty(tempPathKept))
                message += "\n\n" + UiLocalization.Translate("已寫好的暫存檔保留在：") + "\n" + tempPathKept;
            if (!string.IsNullOrEmpty(originalMovedTo))
                message += "\n\n" + UiLocalization.Translate("原始檔案已移至：") + "\n" + originalMovedTo;
            MessageBox.Show(message, Properties.Resources.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void PrepareAllImgs(WzDirectory dir)
        {
            foreach (WzImage img in dir.WzImages)
            {
                img.Changed = true;
            }
            foreach (WzDirectory subdir in dir.WzDirectories)
            {
                PrepareAllImgs(subdir);
            }
        }

        /// <summary>
        /// On checkBox_64BitFile checked changed
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void checkBox_64BitFile_CheckedChanged(object sender, EventArgs e)
        {
            if (!bIsLoaded)
                return;

            UpdateUIState();
        }

        /// <summary>
        /// Selection between saving as .wz or .ms
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void FileFormat_CheckedChanged(object sender, EventArgs e)
        {
            // When either radio button changes state, update the UI.
            UpdateUIState();
        }
    }
}
