using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using TokiAi.Providers;

namespace TokiAi
{
    /// <summary>
    /// Provider / key / model settings. Keys are held per provider so switching back and forth
    /// does not lose them, and the test button proves the whole path (key, model, network) before
    /// the user finds out mid-question.
    /// </summary>
    public class AiSettingsForm : Form
    {
        readonly AiSettings settings;

        ComboBox providerBox;
        TextBox keyBox;
        CheckBox showKeyBox;
        ComboBox modelBox;
        TextBox baseUrlBox;
        CheckBox allowWritesBox;
        ComboBox diskAccessBox;
        ListBox folderList;
        Panel folderPanel;
        Button addFolderButton;
        Button removeFolderButton;
        NumericUpDown roundsBox;
        NumericUpDown tokensBox;
        NumericUpDown timeoutBox;
        TextBox promptBox;
        Label testResultLabel;
        Button testButton;
        Button fetchModelsButton;

        AiProvider loadedProvider;
        bool loading;

        // Starting suggestions only - "取得清單" replaces these with whatever the key can
        // actually reach. Current Claude IDs carry no date suffix; appending one is a 404.
        static readonly string[] ClaudeModels = {
            "claude-opus-5", "claude-sonnet-5", "claude-haiku-4-5",
            "claude-opus-4-8", "claude-opus-4-7", "claude-opus-4-6", "claude-sonnet-4-6"
        };
        static readonly string[] OpenAiModels = {
            "gpt-4o", "gpt-4o-mini", "gpt-4.1", "gpt-4.1-mini"
        };
        static readonly string[] GeminiModels = {
            "gemini-2.0-flash", "gemini-2.0-flash-lite", "gemini-1.5-pro"
        };

        public AiSettingsForm(AiSettings settings)
        {
            this.settings = settings;
            loadedProvider = settings.Provider;
            BuildUi();
            LoadProviderFields(settings.Provider);
        }

        void BuildUi()
        {
            // Everything below runs before the fields hold anything. Without this guard the
            // SelectedIndexChanged fired by seeding providerBox would call StoreProviderFields
            // and write the still-empty textboxes back over the saved key and model name.
            loading = true;
            try
            {
                BuildUiCore();
            }
            finally
            {
                loading = false;
            }
        }

        void BuildUiCore()
        {
            Text = "AI 助手設定";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Microsoft JhengHei UI", 9f);
            ClientSize = new Size(560, 700);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 2;
            layout.Padding = new Padding(12);
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            providerBox = new ComboBox();
            providerBox.DropDownStyle = ComboBoxStyle.DropDownList;
            providerBox.Items.AddRange(new object[] { "Claude (Anthropic)", "OpenAI", "Gemini (Google)" });
            providerBox.Dock = DockStyle.Fill;
            providerBox.SelectedIndexChanged += ProviderBox_SelectedIndexChanged;
            AddRow(layout, "供應商", providerBox);

            keyBox = new TextBox();
            keyBox.Dock = DockStyle.Fill;
            keyBox.UseSystemPasswordChar = true;
            AddRow(layout, "API 金鑰", keyBox);

            showKeyBox = new CheckBox();
            showKeyBox.Text = "顯示金鑰";
            showKeyBox.AutoSize = true;
            showKeyBox.CheckedChanged += ShowKeyBox_CheckedChanged;
            AddRow(layout, "", showKeyBox);

            modelBox = new ComboBox();
            modelBox.DropDownStyle = ComboBoxStyle.DropDown;
            modelBox.Dock = DockStyle.Fill;

            fetchModelsButton = new Button();
            fetchModelsButton.Text = "取得清單";
            fetchModelsButton.Width = 84;
            fetchModelsButton.Dock = DockStyle.Right;
            fetchModelsButton.Click += FetchModelsButton_Click;

            Panel modelRow = new Panel();
            modelRow.Height = 26;
            modelRow.Dock = DockStyle.Fill;
            modelRow.Controls.Add(modelBox);
            modelRow.Controls.Add(fetchModelsButton);
            AddRow(layout, "模型名稱", modelRow);

            baseUrlBox = new TextBox();
            baseUrlBox.Dock = DockStyle.Fill;
            baseUrlBox.PlaceholderText = "留空 = 官方端點";
            AddRow(layout, "自訂端點", baseUrlBox);

            allowWritesBox = new CheckBox();
            allowWritesBox.Text = "允許 AI 提議修改 WZ(提議仍需你逐項確認才會套用)";
            allowWritesBox.AutoSize = true;
            AddRow(layout, "修改權限", allowWritesBox);

            diskAccessBox = new ComboBox();
            diskAccessBox.DropDownStyle = ComboBoxStyle.DropDownList;
            diskAccessBox.Items.AddRange(new object[] {
                "關閉 — AI 看不到電腦上的檔案",
                "整台電腦 — 可以瀏覽任何資料夾",
                "只限指定資料夾"
            });
            diskAccessBox.Dock = DockStyle.Fill;
            diskAccessBox.SelectedIndexChanged += DiskAccessBox_SelectedIndexChanged;
            AddRow(layout, "磁碟存取", diskAccessBox);

            Label diskNote = new Label();
            diskNote.AutoSize = false;
            diskNote.Height = 32;
            diskNote.Dock = DockStyle.Fill;
            diskNote.ForeColor = Color.FromArgb(110, 110, 110);
            diskNote.Text = "AI 只讀得到檔名、大小和圖片尺寸,讀不到任何檔案的內容。"
                + "匯入的圖片是直接從磁碟寫進 WZ,不會經過 API。";
            AddRow(layout, "", diskNote);

            folderList = new ListBox();
            folderList.Height = 70;
            folderList.Dock = DockStyle.Fill;

            FlowLayoutPanel folderButtons = new FlowLayoutPanel();
            folderButtons.Dock = DockStyle.Right;
            folderButtons.FlowDirection = FlowDirection.TopDown;
            folderButtons.Width = 80;
            addFolderButton = new Button();
            addFolderButton.Text = "新增…";
            addFolderButton.Width = 72;
            addFolderButton.Click += AddFolderButton_Click;
            removeFolderButton = new Button();
            removeFolderButton.Text = "移除";
            removeFolderButton.Width = 72;
            removeFolderButton.Click += RemoveFolderButton_Click;
            folderButtons.Controls.Add(addFolderButton);
            folderButtons.Controls.Add(removeFolderButton);

            folderPanel = new Panel();
            folderPanel.Height = 74;
            folderPanel.Dock = DockStyle.Fill;
            folderPanel.Controls.Add(folderList);
            folderPanel.Controls.Add(folderButtons);
            AddRow(layout, "允許的資料夾", folderPanel);

            roundsBox = MakeNumeric(1, 40);
            AddRow(layout, "工具輪數上限", roundsBox);

            tokensBox = MakeNumeric(256, 32000);
            tokensBox.Increment = 256;
            AddRow(layout, "單次回應上限", tokensBox);

            timeoutBox = MakeNumeric(15, 600);
            timeoutBox.Increment = 15;
            AddRow(layout, "逾時(秒)", timeoutBox);

            promptBox = new TextBox();
            promptBox.Multiline = true;
            promptBox.ScrollBars = ScrollBars.Vertical;
            promptBox.Height = 90;
            promptBox.Dock = DockStyle.Fill;
            promptBox.PlaceholderText = "留空 = 使用內建提示詞(繁中、表格輸出、先查再答、修改只提議)";
            AddRow(layout, "系統提示詞", promptBox);

            testButton = new Button();
            testButton.Text = "測試連線";
            testButton.AutoSize = true;
            testButton.Click += TestButton_Click;
            AddRow(layout, "", testButton);

            testResultLabel = new Label();
            testResultLabel.AutoSize = false;
            testResultLabel.Dock = DockStyle.Fill;
            testResultLabel.Height = 40;
            AddRow(layout, "", testResultLabel);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.Dock = DockStyle.Bottom;
            buttons.Height = 44;
            buttons.Padding = new Padding(12, 8, 12, 8);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.AutoSize = true;

            Button ok = new Button();
            ok.Text = "確定";
            ok.AutoSize = true;
            ok.Click += Ok_Click;

            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);

            Controls.Add(layout);
            Controls.Add(buttons);
            CancelButton = cancel;

            providerBox.SelectedIndex = (int)settings.Provider;
            allowWritesBox.Checked = settings.AllowWrites;
            diskAccessBox.SelectedIndex = (int)settings.DiskAccess;
            if (settings.AllowedFolders != null)
                foreach (string folder in settings.AllowedFolders)
                    folderList.Items.Add(folder);
            DiskAccessBox_SelectedIndexChanged(null, EventArgs.Empty);
            roundsBox.Value = settings.MaxToolRounds;
            tokensBox.Value = settings.MaxOutputTokens;
            timeoutBox.Value = settings.TimeoutSeconds;
            promptBox.Text = settings.SystemPromptOverride ?? "";
        }

        static NumericUpDown MakeNumeric(int min, int max)
        {
            NumericUpDown box = new NumericUpDown();
            box.Minimum = min;
            box.Maximum = max;
            box.Width = 100;
            return box;
        }

        static void AddRow(TableLayoutPanel layout, string label, Control control)
        {
            Label caption = new Label();
            caption.Text = label;
            caption.AutoSize = false;
            caption.TextAlign = ContentAlignment.MiddleLeft;
            caption.Dock = DockStyle.Fill;
            layout.Controls.Add(caption);
            layout.Controls.Add(control);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        void DiskAccessBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            // The folder list is only meaningful in "指定資料夾" mode.
            bool folders = diskAccessBox.SelectedIndex == (int)DiskAccessMode.Folders;
            folderList.Enabled = folders;
            addFolderButton.Enabled = folders;
            removeFolderButton.Enabled = folders;
        }

        void AddFolderButton_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "選一個允許 AI 存取的資料夾";
                dialog.ShowNewFolderButton = false;
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;
                if (!folderList.Items.Contains(dialog.SelectedPath))
                    folderList.Items.Add(dialog.SelectedPath);
            }
        }

        void RemoveFolderButton_Click(object sender, EventArgs e)
        {
            if (folderList.SelectedIndex >= 0)
                folderList.Items.RemoveAt(folderList.SelectedIndex);
        }

        void ShowKeyBox_CheckedChanged(object sender, EventArgs e)
        {
            keyBox.UseSystemPasswordChar = !showKeyBox.Checked;
        }

        void ProviderBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (loading)
                return;
            StoreProviderFields(loadedProvider);
            loadedProvider = (AiProvider)providerBox.SelectedIndex;
            LoadProviderFields(loadedProvider);
            testResultLabel.Text = "";
        }

        void LoadProviderFields(AiProvider provider)
        {
            loading = true;
            try
            {
                modelBox.Items.Clear();
                switch (provider)
                {
                    case AiProvider.OpenAI:
                        modelBox.Items.AddRange(OpenAiModels);
                        keyBox.Text = settings.OpenAiKey;
                        modelBox.Text = settings.OpenAiModel;
                        baseUrlBox.Text = settings.OpenAiBaseUrl;
                        break;
                    case AiProvider.Gemini:
                        modelBox.Items.AddRange(GeminiModels);
                        keyBox.Text = settings.GeminiKey;
                        modelBox.Text = settings.GeminiModel;
                        baseUrlBox.Text = settings.GeminiBaseUrl;
                        break;
                    default:
                        modelBox.Items.AddRange(ClaudeModels);
                        keyBox.Text = settings.ClaudeKey;
                        modelBox.Text = settings.ClaudeModel;
                        baseUrlBox.Text = settings.ClaudeBaseUrl;
                        break;
                }
            }
            finally
            {
                loading = false;
            }
        }

        void StoreProviderFields(AiProvider provider)
        {
            switch (provider)
            {
                case AiProvider.OpenAI:
                    settings.OpenAiKey = keyBox.Text.Trim();
                    settings.OpenAiModel = modelBox.Text.Trim();
                    settings.OpenAiBaseUrl = baseUrlBox.Text.Trim();
                    break;
                case AiProvider.Gemini:
                    settings.GeminiKey = keyBox.Text.Trim();
                    settings.GeminiModel = modelBox.Text.Trim();
                    settings.GeminiBaseUrl = baseUrlBox.Text.Trim();
                    break;
                default:
                    settings.ClaudeKey = keyBox.Text.Trim();
                    settings.ClaudeModel = modelBox.Text.Trim();
                    settings.ClaudeBaseUrl = baseUrlBox.Text.Trim();
                    break;
            }
        }

        void CollectAll()
        {
            StoreProviderFields(loadedProvider);
            settings.Provider = (AiProvider)providerBox.SelectedIndex;
            settings.AllowWrites = allowWritesBox.Checked;
            settings.DiskAccess = (DiskAccessMode)diskAccessBox.SelectedIndex;
            settings.AllowedFolders = new List<string>();
            foreach (object folder in folderList.Items)
                settings.AllowedFolders.Add(folder.ToString());
            settings.MaxToolRounds = (int)roundsBox.Value;
            settings.MaxOutputTokens = (int)tokensBox.Value;
            settings.TimeoutSeconds = (int)timeoutBox.Value;
            settings.SystemPromptOverride = promptBox.Text.Trim();
        }

        /// <summary>
        /// Replaces the dropdown's suggestions with the models this key can actually reach, so a
        /// newly released model is selectable without waiting for this program to be updated.
        /// </summary>
        void FetchModelsButton_Click(object sender, EventArgs e)
        {
            CollectAll();
            if (string.IsNullOrWhiteSpace(settings.CurrentKey))
            {
                testResultLabel.ForeColor = Color.Firebrick;
                testResultLabel.Text = "要先填 API 金鑰才能取得模型清單。";
                return;
            }

            fetchModelsButton.Enabled = false;
            testResultLabel.ForeColor = SystemColors.ControlText;
            testResultLabel.Text = "取得模型清單中…";

            AiSettings snapshot = settings;
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<string> models = null;
                string failure = null;
                try
                {
                    models = ChatProvider.Create(snapshot.Provider).ListModels(snapshot, CancellationToken.None);
                }
                catch (Exception error)
                {
                    failure = error.Message;
                }

                List<string> result = models;
                string message = failure;
                if (IsDisposed || !IsHandleCreated)
                    return;
                BeginInvoke(new Action(delegate
                {
                    fetchModelsButton.Enabled = true;
                    if (message != null)
                    {
                        testResultLabel.ForeColor = Color.Firebrick;
                        testResultLabel.Text = message;
                        return;
                    }
                    if (result == null || result.Count == 0)
                    {
                        testResultLabel.ForeColor = Color.Firebrick;
                        testResultLabel.Text = "這把金鑰沒有可用的模型。";
                        return;
                    }

                    // Keep whatever is typed: it may be a model the list endpoint does not
                    // report (a fine-tune, or a gateway-specific alias).
                    string chosen = modelBox.Text;
                    loading = true;
                    try
                    {
                        modelBox.Items.Clear();
                        modelBox.Items.AddRange(result.ToArray());
                        modelBox.Text = result.Contains(chosen) ? chosen : result[0];
                    }
                    finally
                    {
                        loading = false;
                    }
                    testResultLabel.ForeColor = Color.ForestGreen;
                    testResultLabel.Text = "取得 " + result.Count + " 個可用模型。點下拉選單挑一個。";
                    modelBox.DroppedDown = true;
                }));
            });
        }

        void TestButton_Click(object sender, EventArgs e)
        {
            CollectAll();
            if (string.IsNullOrWhiteSpace(settings.CurrentKey))
            {
                testResultLabel.ForeColor = Color.Firebrick;
                testResultLabel.Text = "還沒填 API 金鑰。";
                return;
            }

            testButton.Enabled = false;
            testResultLabel.ForeColor = SystemColors.ControlText;
            testResultLabel.Text = "測試中…";

            AiSettings snapshot = settings;
            // A short, tool-less round trip: enough to prove key, model name and network.
            ThreadPool.QueueUserWorkItem(delegate
            {
                string message;
                bool ok;
                try
                {
                    ChatProvider provider = ChatProvider.Create(snapshot.Provider);
                    List<ChatTurn> history = new List<ChatTurn>();
                    history.Add(ChatTurn.UserText("回覆 OK 兩個字即可。"));
                    ChatTurn reply = provider.Send("你是一個連線測試端點。", history, null, snapshot, CancellationToken.None);
                    message = "連線成功。模型回覆:" + Shorten(reply.JoinedText(), 80);
                    ok = true;
                }
                catch (Exception failure)
                {
                    message = failure.Message;
                    ok = false;
                }

                if (IsDisposed || !IsHandleCreated)
                    return;
                BeginInvoke(new Action(delegate
                {
                    testButton.Enabled = true;
                    testResultLabel.ForeColor = ok ? Color.ForestGreen : Color.Firebrick;
                    testResultLabel.Text = message;
                }));
            });
        }

        static string Shorten(string text, int max)
        {
            if (string.IsNullOrEmpty(text))
                return "(空)";
            text = text.Replace('\n', ' ').Trim();
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        void Ok_Click(object sender, EventArgs e)
        {
            CollectAll();
            try
            {
                settings.Save();
            }
            catch (Exception failure)
            {
                MessageBox.Show(this, "設定存檔失敗:" + failure.Message, "AI 助手",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
