using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using HaRepacker;
using HaRepacker.GUI.Panels;
using Newtonsoft.Json.Linq;

namespace TokiAi
{
    /// <summary>
    /// The assistant window. Modeless and owned by nothing, so the user can keep editing the WZ
    /// tree while a question is in flight.
    ///
    /// Threading: the provider call blocks, so it runs on a worker. Everything that touches the
    /// WZ tree - which is every tool - is marshalled back onto the UI thread through Invoke,
    /// because WzNode is a WinForms TreeNode and WzImage.ParseImage mutates shared state.
    /// </summary>
    public class AiChatForm : Form, IAiConversationHost
    {
        readonly MainPanel panel;
        readonly AiConversation conversation = new AiConversation();
        readonly PendingChangeSet pending = new PendingChangeSet();
        readonly List<PendingChange> applied = new List<PendingChange>();

        AiSettings settings;
        WzTools tools;
        CancellationTokenSource cancellation;
        bool running;

        // The memory manager is modeless so the user can keep it open and watch entries appear
        // while the assistant works.
        AiMemoryForm memoryWindow;

        RichTextBox transcript;
        TextBox inputBox;
        Button sendButton;
        Button stopButton;
        TabControl tabs;
        SplitContainer chatSplit;
        TabPage pendingTab;
        ListView pendingList;
        ListView appliedList;
        ToolStripStatusLabel statusLabel;
        ToolStripStatusLabel providerLabel;

        static readonly Color UserColor = Color.FromArgb(0, 90, 158);
        static readonly Color AssistantColor = Color.FromArgb(24, 24, 24);
        static readonly Color ToolColor = Color.FromArgb(122, 122, 122);
        static readonly Color ErrorColor = Color.FromArgb(178, 34, 34);
        static readonly Color NoticeColor = Color.FromArgb(150, 100, 0);

        public AiChatForm(MainPanel panel)
        {
            this.panel = panel;
            settings = AiSettings.Load();
            RebuildTools();
            pending.Changed += delegate { UpdatePendingUi(); };

            BuildUi();
            UpdateProviderLabel();
            ShowWelcome();
        }

        #region ui

        void BuildUi()
        {
            Text = "AI 助手 — Shui改";
            ClientSize = new Size(940, 700);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Microsoft JhengHei UI", 9f);
            MinimumSize = new Size(640, 460);

            ToolStrip toolbar = new ToolStrip();
            toolbar.GripStyle = ToolStripGripStyle.Hidden;
            toolbar.RenderMode = ToolStripRenderMode.System;

            ToolStripButton settingsButton = new ToolStripButton("設定");
            settingsButton.Click += SettingsButton_Click;
            toolbar.Items.Add(settingsButton);

            ToolStripButton newChatButton = new ToolStripButton("新對話");
            newChatButton.Click += NewChatButton_Click;
            toolbar.Items.Add(newChatButton);

            ToolStripButton memoryButton = new ToolStripButton("記憶");
            memoryButton.ToolTipText = "看 AI 到目前為止學到了什麼,可以自己改或刪掉";
            memoryButton.Click += MemoryButton_Click;
            toolbar.Items.Add(memoryButton);

            toolbar.Items.Add(new ToolStripSeparator());

            ToolStripButton copyButton = new ToolStripButton("複製全部");
            copyButton.Click += CopyButton_Click;
            toolbar.Items.Add(copyButton);

            tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;

            TabPage chatTab = new TabPage("對話");
            chatTab.Padding = new Padding(6);
            chatTab.Controls.Add(BuildChatPanel());

            pendingTab = new TabPage("待確認修改");
            pendingTab.Padding = new Padding(6);
            pendingTab.Controls.Add(BuildPendingPanel());

            tabs.TabPages.Add(chatTab);
            tabs.TabPages.Add(pendingTab);

            StatusStrip status = new StatusStrip();
            statusLabel = new ToolStripStatusLabel("就緒");
            statusLabel.Spring = true;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            providerLabel = new ToolStripStatusLabel("");
            status.Items.Add(statusLabel);
            status.Items.Add(providerLabel);

            Controls.Add(tabs);
            Controls.Add(toolbar);
            Controls.Add(status);
        }

        Control BuildChatPanel()
        {
            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            // Keep the composer a constant height and give every extra pixel to the transcript,
            // instead of letting a fixed SplitterDistance leave a third of the window as input.
            split.FixedPanel = FixedPanel.Panel2;
            split.Panel2MinSize = 80;
            split.Panel1MinSize = 120;
            chatSplit = split;

            transcript = new RichTextBox();
            transcript.Dock = DockStyle.Fill;
            transcript.ReadOnly = true;
            transcript.BorderStyle = BorderStyle.FixedSingle;
            transcript.BackColor = Color.White;
            transcript.Font = MarkdownRenderer.BaseFont;
            transcript.DetectUrls = true;
            transcript.LinkClicked += Transcript_LinkClicked;
            split.Panel1.Controls.Add(transcript);

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Fill;

            inputBox = new TextBox();
            inputBox.Multiline = true;
            inputBox.Dock = DockStyle.Fill;
            inputBox.ScrollBars = ScrollBars.Vertical;
            inputBox.Font = MarkdownRenderer.BaseFont;
            inputBox.PlaceholderText = "問點什麼…(Enter 送出,Shift+Enter 換行)";
            inputBox.KeyDown += InputBox_KeyDown;

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Right;
            buttons.FlowDirection = FlowDirection.TopDown;
            buttons.Width = 96;
            buttons.Padding = new Padding(6, 0, 0, 0);

            sendButton = new Button();
            sendButton.Text = "送出";
            sendButton.Width = 84;
            sendButton.Height = 34;
            sendButton.Click += SendButton_Click;

            stopButton = new Button();
            stopButton.Text = "停止";
            stopButton.Width = 84;
            stopButton.Height = 28;
            stopButton.Enabled = false;
            stopButton.Click += StopButton_Click;

            buttons.Controls.Add(sendButton);
            buttons.Controls.Add(stopButton);

            bottom.Controls.Add(inputBox);
            bottom.Controls.Add(buttons);
            split.Panel2.Controls.Add(bottom);

            return split;
        }

        Control BuildPendingPanel()
        {
            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            split.SplitterDistance = 340;

            // --- pending ---
            Panel top = new Panel();
            top.Dock = DockStyle.Fill;

            pendingList = new ListView();
            pendingList.Dock = DockStyle.Fill;
            pendingList.View = View.Details;
            pendingList.CheckBoxes = true;
            pendingList.FullRowSelect = true;
            pendingList.GridLines = true;
            pendingList.Columns.Add("動作", 60);
            pendingList.Columns.Add("路徑", 420);
            pendingList.Columns.Add("目前", 150);
            pendingList.Columns.Add("改成", 150);
            pendingList.DoubleClick += PendingList_DoubleClick;

            FlowLayoutPanel pendingButtons = new FlowLayoutPanel();
            pendingButtons.Dock = DockStyle.Bottom;
            pendingButtons.Height = 40;
            pendingButtons.Padding = new Padding(0, 6, 0, 0);
            pendingButtons.Controls.Add(MakeButton("全選", SelectAllPending_Click, 70));
            pendingButtons.Controls.Add(MakeButton("全不選", SelectNonePending_Click, 70));
            pendingButtons.Controls.Add(MakeButton("移除勾選", RemovePending_Click, 88));
            pendingButtons.Controls.Add(MakeButton("清空清單", ClearPending_Click, 88));
            Button applyButton = MakeButton("套用勾選項", ApplyPending_Click, 110);
            applyButton.Font = new Font(Font, FontStyle.Bold);
            pendingButtons.Controls.Add(applyButton);

            Label pendingHeader = new Label();
            pendingHeader.Dock = DockStyle.Top;
            pendingHeader.Height = 24;
            pendingHeader.Text = "AI 提議的修改 — 勾選你同意的項目,按「套用勾選項」才會真的寫入 WZ。";
            pendingHeader.ForeColor = NoticeColor;

            top.Controls.Add(pendingList);
            top.Controls.Add(pendingButtons);
            top.Controls.Add(pendingHeader);

            // --- applied ---
            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Fill;

            appliedList = new ListView();
            appliedList.Dock = DockStyle.Fill;
            appliedList.View = View.Details;
            appliedList.CheckBoxes = true;
            appliedList.FullRowSelect = true;
            appliedList.GridLines = true;
            appliedList.Columns.Add("動作", 60);
            appliedList.Columns.Add("路徑", 420);
            appliedList.Columns.Add("原本", 150);
            appliedList.Columns.Add("現在", 150);
            appliedList.DoubleClick += AppliedList_DoubleClick;

            FlowLayoutPanel appliedButtons = new FlowLayoutPanel();
            appliedButtons.Dock = DockStyle.Bottom;
            appliedButtons.Height = 40;
            appliedButtons.Padding = new Padding(0, 6, 0, 0);
            appliedButtons.Controls.Add(MakeButton("還原勾選", RevertSelected_Click, 88));
            appliedButtons.Controls.Add(MakeButton("還原全部", RevertAll_Click, 88));

            Label appliedHeader = new Label();
            appliedHeader.Dock = DockStyle.Top;
            appliedHeader.Height = 24;
            appliedHeader.Text = "已套用(尚未存檔)— 還沒按編輯器的「儲存」之前,都可以在這裡還原。";
            appliedHeader.ForeColor = NoticeColor;

            bottom.Controls.Add(appliedList);
            bottom.Controls.Add(appliedButtons);
            bottom.Controls.Add(appliedHeader);

            split.Panel1.Controls.Add(top);
            split.Panel2.Controls.Add(bottom);
            return split;
        }

        Button MakeButton(string text, EventHandler handler, int width)
        {
            Button button = new Button();
            button.Text = text;
            button.Width = width;
            button.Height = 28;
            button.Click += handler;
            return button;
        }

        void ShowWelcome()
        {
            AppendLabelled("AI 助手", NoticeColor);
            StringBuilder welcome = new StringBuilder();
            welcome.Append("我可以查詢並整理你目前開啟的 WZ 資料");
            welcome.Append(settings.AllowWrites ? ",也可以提議修改(提議一律要你確認才會套用)。\n\n" : "(目前是唯讀模式)。\n\n");
            welcome.Append("例如:\n");
            welcome.Append("- 幫我看 **Skill.wz/112.img/skill/1120017** 的 level 1 到 5,把 damage 和 mpCon 列成表\n");
            welcome.Append("- 在 **String.wz** 裡找名字含「楓葉」的道具\n");
            if (settings.AllowWrites)
                welcome.Append("- 把 1120017 每一級的 mpCon 都降低 20%\n");
            welcome.Append(DescribeMemory());
            if (string.IsNullOrWhiteSpace(settings.CurrentKey))
                welcome.Append("\n**還沒設定 API 金鑰** — 按左上角「設定」填入,才能開始使用。\n");
            MarkdownRenderer.Append(transcript, welcome.ToString(), AssistantColor);
            AppendPlain("\n");
        }

        /// <summary>
        /// A new chat starts with an empty transcript but not an empty head, and the difference
        /// matters to the user - so every fresh conversation says up front how much it still
        /// remembers and where to go and read it.
        /// </summary>
        string DescribeMemory()
        {
            if (!settings.MemoryEnabled)
                return "\n長期記憶目前是**關閉**的,這次對話結束後不會留下任何東西。"
                     + "要打開的話按上面的「記憶」。\n";
            try
            {
                int count = AiMemory.Load().Count;
                if (count == 0)
                    return "\n這是我第一次跟你合作,還沒學到任何東西。"
                         + "接下來做的事我會把有用的記下來,按上面的「記憶」隨時可以看、可以改。\n";
                return "\n我記得 **" + count + "** 件先前學到的事(節點結構、你的做法、這台電腦上的路徑…),"
                     + "這次對話會直接用上。按上面的「記憶」可以看內容,記錯的直接改掉或刪掉。\n";
            }
            catch
            {
                return "";
            }
        }

        void UpdateProviderLabel()
        {
            providerLabel.Text = AiSettings.ProviderDisplayName(settings.Provider) + " · " + settings.CurrentModel
                + (settings.AllowWrites ? "" : " · 唯讀");
        }

        #endregion

        #region transcript

        void AppendLabelled(string who, Color color)
        {
            MarkdownRenderer.AppendRun(transcript, who + "\n", MarkdownRenderer.BoldFont, color);
        }

        void AppendPlain(string text)
        {
            MarkdownRenderer.AppendRun(transcript, text, MarkdownRenderer.BaseFont, AssistantColor);
        }

        void ScrollToEnd()
        {
            transcript.SelectionStart = transcript.TextLength;
            transcript.ScrollToCaret();
        }

        void Transcript_LinkClicked(object sender, LinkClickedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.LinkText) { UseShellExecute = true });
            }
            catch
            {
                // A link the shell will not open is not worth an error dialog.
            }
        }

        #endregion

        #region sending

        /// <summary>
        /// Called when the window is opened from a node's context menu. The path goes into the
        /// input box, not into hidden state, so the user sees and can edit exactly what will be
        /// sent - and a second right-click while the window is open just retargets it.
        /// </summary>
        public void SeedContextPath(string nodePath)
        {
            if (string.IsNullOrWhiteSpace(nodePath))
                return;
            tabs.SelectedIndex = 0;
            if (running)
            {
                statusLabel.Text = "還在回覆中,無法帶入節點";
                return;
            }
            inputBox.Text = "關於 " + nodePath.Trim() + " :" + Environment.NewLine;
            inputBox.SelectionStart = inputBox.TextLength;
            inputBox.SelectionLength = 0;
            inputBox.Focus();
            statusLabel.Text = "已帶入節點路徑,接著把問題打完再送出";
        }

        /// <summary>Switches tabs from outside. Used by the test harness to capture both views.</summary>
        public void SelectTab(int index)
        {
            if (index >= 0 && index < tabs.TabPages.Count)
                tabs.SelectedIndex = index;
        }

        void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter || e.Shift)
                return;
            e.SuppressKeyPress = true;
            e.Handled = true;
            Send();
        }

        void SendButton_Click(object sender, EventArgs e)
        {
            Send();
        }

        void Send()
        {
            if (running)
                return;
            string text = inputBox.Text.Trim();
            if (text.Length == 0)
                return;

            if (string.IsNullOrWhiteSpace(settings.CurrentKey))
            {
                MessageBox.Show(this, "還沒設定 " + AiSettings.ProviderDisplayName(settings.Provider)
                    + " 的 API 金鑰。請按左上角「設定」填入。", "AI 助手",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            inputBox.Clear();
            AppendLabelled("你", UserColor);
            MarkdownRenderer.AppendRun(transcript, text + "\n\n", MarkdownRenderer.BaseFont, UserColor);
            ScrollToEnd();

            SetRunning(true);
            cancellation = new CancellationTokenSource();
            CancellationToken token = cancellation.Token;
            AiSettings snapshot = settings;

            ThreadPool.QueueUserWorkItem(delegate
            {
                string failure = null;
                bool cancelled = false;
                try
                {
                    conversation.Run(text, snapshot, true, this, token);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    // The half-finished turn would be sent again on the next question and most
                    // providers reject a tool_use with no matching result, so it is dropped.
                    conversation.RollbackFailedTurn();
                }
                catch (Exception error)
                {
                    failure = error.Message;
                    conversation.RollbackFailedTurn();
                }

                string message = failure;
                bool wasCancelled = cancelled;
                SafeInvoke(delegate
                {
                    if (wasCancelled)
                    {
                        MarkdownRenderer.AppendRun(transcript, "(已停止)\n\n", MarkdownRenderer.BaseFont, ToolColor);
                    }
                    else if (message != null)
                    {
                        AppendLabelled("錯誤", ErrorColor);
                        MarkdownRenderer.AppendRun(transcript, message + "\n\n", MarkdownRenderer.BaseFont, ErrorColor);
                    }
                    SetRunning(false);
                    statusLabel.Text = wasCancelled ? "已停止" : (message != null ? "失敗" : "就緒");
                    ScrollToEnd();
                });
            });
        }

        void StopButton_Click(object sender, EventArgs e)
        {
            if (cancellation != null)
            {
                cancellation.Cancel();
                statusLabel.Text = "停止中…";
            }
        }

        void SetRunning(bool value)
        {
            running = value;
            sendButton.Enabled = !value;
            stopButton.Enabled = value;
            inputBox.ReadOnly = value;
        }

        /// <summary>
        /// Marshals to the UI thread, tolerating a window that is closing. Without the guard a
        /// reply arriving after the user closed the window throws on a dead handle.
        /// </summary>
        void SafeInvoke(Action action)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated)
                    return;
                if (InvokeRequired)
                    Invoke(action);
                else
                    action();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        #endregion

        #region IAiConversationHost

        public void PostStatus(string text)
        {
            SafeInvoke(delegate { statusLabel.Text = text; });
        }

        public void PostAssistantText(string text)
        {
            SafeInvoke(delegate
            {
                AppendLabelled(AiSettings.ProviderDisplayName(settings.Provider), AssistantColor);
                MarkdownRenderer.Append(transcript, text, AssistantColor);
                AppendPlain("\n");
                ScrollToEnd();
            });
        }

        public void PostToolCall(string toolName, string argumentPreview)
        {
            SafeInvoke(delegate
            {
                MarkdownRenderer.AppendRun(transcript, "▸ " + toolName
                    + (string.IsNullOrEmpty(argumentPreview) ? "" : "(" + argumentPreview + ")") + "\n",
                    MarkdownRenderer.MonoFont, ToolColor);
                ScrollToEnd();
            });
        }

        public void PostToolResult(string toolName, bool isError, string preview)
        {
            SafeInvoke(delegate
            {
                MarkdownRenderer.AppendRun(transcript, "   ← " + preview + "\n",
                    MarkdownRenderer.MonoFont, isError ? ErrorColor : ToolColor);
                ScrollToEnd();
            });
        }

        /// <summary>
        /// Called from the worker. The whole body runs on the UI thread because it walks and
        /// parses the live WZ tree.
        /// </summary>
        public string ExecuteTool(string toolName, JObject input, out bool isError)
        {
            string result = null;
            bool failed = false;
            try
            {
                if (InvokeRequired)
                {
                    Invoke(new Action(delegate { result = tools.Execute(toolName, input, out failed); }));
                }
                else
                {
                    result = tools.Execute(toolName, input, out failed);
                }
            }
            catch (Exception error)
            {
                isError = true;
                return "工具執行失敗:" + error.Message;
            }
            isError = failed;
            return result;
        }

        #endregion

        #region pending changes

        void UpdatePendingUi()
        {
            SafeInvoke(delegate
            {
                pendingList.BeginUpdate();
                pendingList.Items.Clear();
                foreach (PendingChange change in pending.Items)
                {
                    ListViewItem item = new ListViewItem(change.KindText);
                    item.SubItems.Add(change.Path);
                    item.SubItems.Add(Shorten(change.OldValue));
                    item.SubItems.Add(Shorten(change.NewValue));
                    item.Checked = true;
                    item.Tag = change;
                    pendingList.Items.Add(item);
                }
                pendingList.EndUpdate();
                pendingTab.Text = pending.Count == 0 ? "待確認修改" : "待確認修改 (" + pending.Count + ")";
            });
        }

        void UpdateAppliedUi()
        {
            appliedList.BeginUpdate();
            appliedList.Items.Clear();
            foreach (PendingChange change in applied)
            {
                ListViewItem item = new ListViewItem(change.KindText);
                item.SubItems.Add(change.Path);
                item.SubItems.Add(Shorten(change.OldValue));
                item.SubItems.Add(Shorten(change.NewValue));
                item.Tag = change;
                appliedList.Items.Add(item);
            }
            appliedList.EndUpdate();
        }

        static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            string single = text.Replace('\n', ' ').Replace('\r', ' ');
            return single.Length <= 60 ? single : single.Substring(0, 60) + "…";
        }

        void SelectAllPending_Click(object sender, EventArgs e)
        {
            foreach (ListViewItem item in pendingList.Items)
                item.Checked = true;
        }

        void SelectNonePending_Click(object sender, EventArgs e)
        {
            foreach (ListViewItem item in pendingList.Items)
                item.Checked = false;
        }

        void RemovePending_Click(object sender, EventArgs e)
        {
            List<PendingChange> doomed = new List<PendingChange>();
            foreach (ListViewItem item in pendingList.Items)
                if (item.Checked && item.Tag is PendingChange change)
                    doomed.Add(change);
            foreach (PendingChange change in doomed)
                pending.Items.Remove(change);
            pending.RaiseChanged();
        }

        void ClearPending_Click(object sender, EventArgs e)
        {
            if (pending.Count == 0)
                return;
            if (MessageBox.Show(this, "清空待確認清單?這只是丟掉提議,不會動到已經套用的修改。", "AI 助手",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            pending.Clear();
        }

        void ApplyPending_Click(object sender, EventArgs e)
        {
            List<PendingChange> chosen = new List<PendingChange>();
            foreach (ListViewItem item in pendingList.Items)
                if (item.Checked && item.Tag is PendingChange change)
                    chosen.Add(change);

            if (chosen.Count == 0)
            {
                MessageBox.Show(this, "沒有勾選任何項目。", "AI 助手", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this, "確定要套用 " + chosen.Count + " 項修改到 WZ 嗎?\n\n"
                + "(套用後仍未存檔,可以在下方「已套用」清單還原。)", "AI 助手",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            int ok = 0;
            List<string> failures = new List<string>();
            List<MainPanel> touched = new List<MainPanel>();
            foreach (PendingChange change in chosen)
            {
                // A ported change writes into the tab it targets, not the one this window was
                // opened from, so the undo manager has to come from that tab's panel.
                MainPanel owner = change.Panel ?? panel;
                string error;
                if (change.Apply(owner.UndoRedoMan, out error))
                {
                    applied.Add(change);
                    pending.Items.Remove(change);
                    ok++;
                    if (!touched.Contains(owner))
                        touched.Add(owner);
                }
                else
                {
                    failures.Add(change.Path + " — " + error);
                }
            }

            pending.RaiseChanged();
            UpdateAppliedUi();
            RefreshPanels(touched);

            StringBuilder report = new StringBuilder();
            report.Append("已套用 ").Append(ok).Append(" 項修改");
            if (failures.Count > 0)
            {
                report.Append(",").Append(failures.Count).Append(" 項失敗:\n");
                foreach (string failure in failures)
                    report.Append("- ").Append(failure).Append('\n');
            }
            else
            {
                report.Append("。記得到編輯器按「儲存」才會寫回檔案。\n");
            }

            AppendLabelled("套用結果", NoticeColor);
            MarkdownRenderer.Append(transcript, report.ToString(), failures.Count > 0 ? ErrorColor : NoticeColor);
            AppendPlain("\n");
            ScrollToEnd();
            statusLabel.Text = "已套用 " + ok + " 項";
        }

        void RevertSelected_Click(object sender, EventArgs e)
        {
            List<PendingChange> chosen = new List<PendingChange>();
            foreach (ListViewItem item in appliedList.Items)
                if (item.Checked && item.Tag is PendingChange change)
                    chosen.Add(change);
            RevertChanges(chosen);
        }

        void RevertAll_Click(object sender, EventArgs e)
        {
            RevertChanges(new List<PendingChange>(applied));
        }

        void RevertChanges(List<PendingChange> chosen)
        {
            if (chosen.Count == 0)
            {
                MessageBox.Show(this, "沒有可還原的項目。", "AI 助手", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this, "還原 " + chosen.Count + " 項修改?", "AI 助手",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            // Newest first: a later change can sit inside a subtree an earlier one created.
            chosen.Reverse();
            int ok = 0;
            List<string> failures = new List<string>();
            List<MainPanel> touched = new List<MainPanel>();
            foreach (PendingChange change in chosen)
            {
                string error;
                if (change.Revert(out error))
                {
                    applied.Remove(change);
                    ok++;
                    MainPanel owner = change.Panel ?? panel;
                    if (!touched.Contains(owner))
                        touched.Add(owner);
                }
                else
                {
                    failures.Add(change.Path + " — " + error);
                }
            }

            UpdateAppliedUi();
            RefreshPanels(touched);

            StringBuilder report = new StringBuilder();
            report.Append("已還原 ").Append(ok).Append(" 項");
            if (failures.Count > 0)
            {
                report.Append(",").Append(failures.Count).Append(" 項失敗:\n");
                foreach (string failure in failures)
                    report.Append("- ").Append(failure).Append('\n');
            }
            report.Append('\n');
            AppendLabelled("還原結果", NoticeColor);
            MarkdownRenderer.Append(transcript, report.ToString(), failures.Count > 0 ? ErrorColor : NoticeColor);
            AppendPlain("\n");
            ScrollToEnd();
        }

        /// <summary>
        /// Repaints the WPF mirror of every tab an edit landed in. A port touches two tabs, and
        /// only refreshing the one this window belongs to leaves the other showing stale data.
        /// </summary>
        void RefreshPanels(List<MainPanel> panels)
        {
            if (panels == null || panels.Count == 0)
            {
                panel.RefreshNativeDataTree();
                return;
            }
            foreach (MainPanel target in panels)
                target.RefreshNativeDataTree();
        }

        void PendingList_DoubleClick(object sender, EventArgs e)
        {
            if (pendingList.SelectedItems.Count == 0)
                return;
            RevealInTree(pendingList.SelectedItems[0].Tag as PendingChange);
        }

        void AppliedList_DoubleClick(object sender, EventArgs e)
        {
            if (appliedList.SelectedItems.Count == 0)
                return;
            RevealInTree(appliedList.SelectedItems[0].Tag as PendingChange);
        }

        /// <summary>
        /// Jumps the editor's tree to the node a row refers to. MainPanel keeps its reveal helper
        /// private, so this goes through reflection and quietly does nothing if it is not there -
        /// a convenience, never a requirement.
        /// </summary>
        void RevealInTree(PendingChange change)
        {
            if (change == null)
                return;
            WzNode node = change.Kind == PendingChangeKind.Add || change.Kind == PendingChangeKind.Copy
                ? change.CreatedNode ?? change.Target
                : change.Target;
            if (node == null)
                return;
            try
            {
                MethodInfo reveal = typeof(MainPanel).GetMethod("SelectAndRevealNativeNode",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (reveal != null)
                    reveal.Invoke(change.Panel ?? panel, new object[] { node });
            }
            catch
            {
                // Not worth telling the user about.
            }
        }

        #endregion

        #region toolbar

        void SettingsButton_Click(object sender, EventArgs e)
        {
            using (AiSettingsForm dialog = new AiSettingsForm(settings))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    // The dialog edits the live object, so a cancel has to re-read from disk.
                    settings = AiSettings.Load();
                    RebuildTools();
                }
                UpdateProviderLabel();
            }
        }

        /// <summary>
        /// Rebuilding the tool set drops its event subscriptions with it, so creation and wiring
        /// stay together - otherwise cancelling the settings dialog silently stops the memory
        /// manager from refreshing itself.
        /// </summary>
        void RebuildTools()
        {
            tools = new WzTools(panel, pending, settings);
            tools.MemoryChanged += Tools_MemoryChanged;
            if (memoryWindow != null && !memoryWindow.IsDisposed)
                memoryWindow.UseSettings(settings);
        }

        void Tools_MemoryChanged(object sender, EventArgs e)
        {
            if (memoryWindow == null || memoryWindow.IsDisposed)
                return;
            try
            {
                memoryWindow.Reload();
            }
            catch (ObjectDisposedException) { }
        }

        void MemoryButton_Click(object sender, EventArgs e)
        {
            if (memoryWindow == null || memoryWindow.IsDisposed)
            {
                memoryWindow = new AiMemoryForm(settings);
                memoryWindow.FormClosed += delegate { memoryWindow = null; };
                memoryWindow.Show(this);
            }
            else
            {
                if (memoryWindow.WindowState == FormWindowState.Minimized)
                    memoryWindow.WindowState = FormWindowState.Normal;
                memoryWindow.Reload();
                memoryWindow.Activate();
            }
        }

        void NewChatButton_Click(object sender, EventArgs e)
        {
            if (running)
            {
                MessageBox.Show(this, "還在回覆中,請先按「停止」。", "AI 助手", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            conversation.Reset();
            transcript.Clear();
            ShowWelcome();
            statusLabel.Text = "就緒";
        }

        void CopyButton_Click(object sender, EventArgs e)
        {
            if (transcript.TextLength == 0)
                return;
            try
            {
                Clipboard.SetText(transcript.Text);
                statusLabel.Text = "已複製對話內容";
            }
            catch (Exception error)
            {
                MessageBox.Show(this, "複製失敗:" + error.Message, "AI 助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        #endregion

        // ---------------------------------------------------------------- keyboard interop
        //
        // This is a WinForms Form shown modelessly from a WPF application, so WinForms'
        // Application.ThreadContext message loop never runs for it. Plain typing still works
        // (WM_CHAR reaches the control's WndProc), but every *command* key - Ctrl+C, Ctrl+V,
        // Ctrl+A, Tab, Escape - is dispatched by Control.PreProcessMessage, which only gets
        // called from that loop. The result is a window where copy and paste silently do
        // nothing. Feeding WPF's message pump into PreProcessMessage restores them.

        System.Windows.Interop.ThreadMessageEventHandler keyboardHook;

        void InstallKeyboardHook()
        {
            if (keyboardHook != null)
                return;
            keyboardHook = PreprocessThreadMessage;
            System.Windows.Interop.ComponentDispatcher.ThreadPreprocessMessage += keyboardHook;
        }

        void RemoveKeyboardHook()
        {
            if (keyboardHook == null)
                return;
            System.Windows.Interop.ComponentDispatcher.ThreadPreprocessMessage -= keyboardHook;
            keyboardHook = null;
        }

        void PreprocessThreadMessage(ref System.Windows.Interop.MSG msg, ref bool handled)
        {
            if (handled)
                return;
            // WM_KEYDOWN / WM_SYSKEYDOWN only - everything else is left alone.
            if (msg.message != 0x0100 && msg.message != 0x0104)
                return;
            try
            {
                Control target = Control.FromChildHandle(msg.hwnd);
                // Scoped to this window, so the editor's own WPF shortcuts are untouched.
                if (target == null || target.FindForm() != this)
                    return;
                Message message = Message.Create(msg.hwnd, msg.message, msg.wParam, msg.lParam);
                if (target.PreProcessMessage(ref message))
                    handled = true;
            }
            catch
            {
                // Never let a keystroke take the window down.
            }
        }

        /// <summary>
        /// OnLoad, not OnShown: Form.OnShown is raised off the WinForms message loop, which never
        /// runs for a modeless form inside a WPF application, so it cannot be relied on here.
        /// OnLoad is raised synchronously by Show().
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            InstallKeyboardHook();
            ApplyInitialLayout();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            InstallKeyboardHook();
            ApplyInitialLayout();
        }

        void ApplyInitialLayout()
        {
            // SplitterDistance is meaningless until the container has its real size, so the
            // composer height is set once the window is actually laid out.
            if (chatSplit != null && chatSplit.Height > 220)
                chatSplit.SplitterDistance = chatSplit.Height - 108;
            inputBox.Focus();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (pending.Count > 0 && e.CloseReason == CloseReason.UserClosing)
            {
                if (MessageBox.Show(this, "還有 " + pending.Count + " 項提議沒有處理,關掉就會丟掉。要關閉嗎?",
                    "AI 助手", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }
            if (cancellation != null)
                cancellation.Cancel();
            RemoveKeyboardHook();
            if (memoryWindow != null && !memoryWindow.IsDisposed)
                memoryWindow.Close();
            base.OnFormClosing(e);
        }
    }
}
