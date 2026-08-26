using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace TokiAi
{
    /// <summary>
    /// The window where the user sees, edits and deletes everything the assistant has learned.
    ///
    /// This exists because the memory is written automatically. Anything a program accumulates on
    /// its own behind the user's back is a liability, so every entry is listed here in full, in
    /// the order it was learned, and can be corrected or thrown away in one click.
    /// </summary>
    public class AiMemoryForm : Form
    {
        AiSettings settings;
        AiMemory memory;

        ListView list;
        CheckBox enabledBox;
        Label summary;

        static readonly Color NoticeColor = Color.FromArgb(150, 100, 0);
        static readonly Color UserEntryColor = Color.FromArgb(0, 90, 158);

        public AiMemoryForm(AiSettings settings)
        {
            this.settings = settings ?? new AiSettings();
            BuildUi();
            Reload();
        }

        #region ui

        void BuildUi()
        {
            Text = "AI 長期記憶 — Shui改";
            ClientSize = new Size(880, 520);
            MinimumSize = new Size(620, 380);
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Microsoft JhengHei UI", 9f);
            ShowInTaskbar = false;
            MinimizeBox = false;

            Label header = new Label();
            header.Dock = DockStyle.Top;
            header.Height = 42;
            header.Padding = new Padding(4, 4, 4, 0);
            header.ForeColor = NoticeColor;
            header.Text = "AI 每次做完事會把「以後還用得到」的事實記在這裡,關掉編輯器也留著,下次開新對話會自動帶上。"
                        + "\n記錯的可以直接改或刪掉 —— 這份清單就是它記得的全部,沒有別的。";

            list = new ListView();
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.GridLines = true;
            list.MultiSelect = true;
            list.HideSelection = false;
            list.Columns.Add("編號", 54);
            list.Columns.Add("分類", 60);
            list.Columns.Add("記憶內容", 500);
            list.Columns.Add("來源", 64);
            list.Columns.Add("記下的時間", 130);
            list.DoubleClick += Edit_Click;

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Bottom;
            buttons.Height = 42;
            buttons.Padding = new Padding(0, 6, 0, 0);
            buttons.Controls.Add(MakeButton("新增一筆", Add_Click, 84));
            buttons.Controls.Add(MakeButton("編輯", Edit_Click, 64));
            buttons.Controls.Add(MakeButton("刪除", Delete_Click, 64));
            buttons.Controls.Add(MakeButton("全部清空", ClearAll_Click, 84));
            buttons.Controls.Add(MakeButton("重新整理", Refresh_Click, 84));
            buttons.Controls.Add(MakeButton("開啟檔案位置", OpenFolder_Click, 108));

            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 30;

            enabledBox = new CheckBox();
            enabledBox.AutoSize = true;
            enabledBox.Location = new Point(4, 6);
            enabledBox.Text = "開啟長期記憶(關掉後 AI 不會讀也不會寫,已經記下的內容會原封不動留著)";
            enabledBox.Checked = settings.MemoryEnabled;
            enabledBox.CheckedChanged += EnabledBox_CheckedChanged;

            summary = new Label();
            summary.Dock = DockStyle.Bottom;
            summary.Height = 22;
            summary.ForeColor = Color.Gray;

            top.Controls.Add(enabledBox);

            Panel body = new Panel();
            body.Dock = DockStyle.Fill;
            body.Padding = new Padding(8);
            body.Controls.Add(list);
            body.Controls.Add(summary);
            body.Controls.Add(buttons);

            Controls.Add(body);
            Controls.Add(top);
            Controls.Add(header);
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

        #endregion

        #region data

        /// <summary>
        /// Points this window at a different settings object. Cancelling the settings dialog
        /// re-reads settings from disk into a new instance, and a manager still writing to the
        /// discarded one would put stale values back on the next toggle.
        /// </summary>
        public void UseSettings(AiSettings replacement)
        {
            if (replacement == null)
                return;
            settings = replacement;
            enabledBox.Checked = settings.MemoryEnabled;
        }

        public void Reload()
        {
            memory = AiMemory.Load();
            list.BeginUpdate();
            list.Items.Clear();
            foreach (MemoryEntry entry in memory.Entries)
            {
                ListViewItem item = new ListViewItem(entry.Id.ToString());
                item.SubItems.Add(entry.Category);
                item.SubItems.Add(entry.Text);
                item.SubItems.Add(entry.FromUser ? "你交代的" : "AI 學到的");
                item.SubItems.Add(entry.Created.ToString("yyyy-MM-dd HH:mm"));
                item.Tag = entry.Id;
                if (entry.FromUser)
                    item.ForeColor = UserEntryColor;
                list.Items.Add(item);
            }
            list.EndUpdate();

            int chars = memory.TotalChars();
            string text = "目前 " + memory.Count + " 筆,約 " + chars + " 字"
                        + "(每次對話都會整份帶給 AI,所以上限是 " + AiMemory.HardCharBudget + " 字)。";
            if (memory.NearCapacity())
                text += "  快滿了,建議清掉過時的。";
            summary.Text = text;
            summary.ForeColor = memory.NearCapacity() ? NoticeColor : Color.Gray;
        }

        int SelectedId()
        {
            if (list.SelectedItems.Count == 0)
                return 0;
            return (int)list.SelectedItems[0].Tag;
        }

        #endregion

        #region actions

        void EnabledBox_CheckedChanged(object sender, EventArgs e)
        {
            settings.MemoryEnabled = enabledBox.Checked;
            try
            {
                settings.Save();
            }
            catch (Exception error)
            {
                MessageBox.Show(this, "設定存不起來:" + error.Message, "AI 長期記憶",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void Add_Click(object sender, EventArgs e)
        {
            MemoryEntry draft = new MemoryEntry();
            draft.Category = "偏好";
            if (!MemoryEntryDialog.Edit(this, "新增一筆記憶", draft))
                return;

            AiMemory current = AiMemory.Load();
            MemoryEntry duplicate;
            string refusal;
            // FromUser is true here: the user typed it, so it outranks anything the model inferred
            // and the list marks it so they can tell the two apart later.
            MemoryEntry added = current.Add(draft.Category, draft.Text, true, out duplicate, out refusal);
            if (duplicate != null)
            {
                MessageBox.Show(this, "已經有一筆一樣的了:\n\n#" + duplicate.Id + "  " + duplicate.Text,
                    "AI 長期記憶", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (added == null)
            {
                MessageBox.Show(this, refusal ?? "沒有新增。", "AI 長期記憶",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            current.Save();
            Reload();
        }

        void Edit_Click(object sender, EventArgs e)
        {
            int id = SelectedId();
            if (id == 0)
                return;

            AiMemory current = AiMemory.Load();
            MemoryEntry entry = current.Find(id);
            if (entry == null)
            {
                Reload();
                return;
            }

            MemoryEntry draft = entry.Copy();
            if (!MemoryEntryDialog.Edit(this, "編輯記憶 #" + id, draft))
                return;
            if (!current.Update(id, draft.Text, draft.Category))
                return;
            current.Save();
            Reload();
        }

        void Delete_Click(object sender, EventArgs e)
        {
            if (list.SelectedItems.Count == 0)
                return;
            if (MessageBox.Show(this, "刪掉選取的 " + list.SelectedItems.Count + " 筆記憶?",
                    "AI 長期記憶", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            AiMemory current = AiMemory.Load();
            foreach (ListViewItem item in list.SelectedItems)
                current.Remove((int)item.Tag);
            current.Save();
            Reload();
        }

        void ClearAll_Click(object sender, EventArgs e)
        {
            if (memory.Count == 0)
                return;
            if (MessageBox.Show(this,
                    "清空全部 " + memory.Count + " 筆記憶?AI 會完全忘記目前學到的東西,而且救不回來。",
                    "AI 長期記憶", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            AiMemory current = AiMemory.Load();
            current.Clear();
            current.Save();
            Reload();
        }

        void Refresh_Click(object sender, EventArgs e)
        {
            Reload();
        }

        void OpenFolder_Click(object sender, EventArgs e)
        {
            try
            {
                string path = AiMemory.MemoryPath;
                if (File.Exists(path))
                    Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"")
                    { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo(AiSettings.SettingsDirectory)
                    { UseShellExecute = true });
            }
            catch (Exception error)
            {
                MessageBox.Show(this, "開不起來:" + error.Message, "AI 長期記憶",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        #endregion
    }

    /// <summary>The add/edit box. Small enough that a whole designer file would be noise.</summary>
    public class MemoryEntryDialog : Form
    {
        ComboBox categoryBox;
        TextBox textBox;

        MemoryEntryDialog(string title, MemoryEntry entry)
        {
            Text = title;
            ClientSize = new Size(600, 210);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Microsoft JhengHei UI", 9f);
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;

            Label categoryLabel = new Label();
            categoryLabel.Text = "分類";
            categoryLabel.Location = new Point(12, 15);
            categoryLabel.Size = new Size(40, 20);

            categoryBox = new ComboBox();
            categoryBox.DropDownStyle = ComboBoxStyle.DropDownList;
            categoryBox.Location = new Point(56, 12);
            categoryBox.Width = 140;
            foreach (string category in AiMemory.Categories)
                categoryBox.Items.Add(category);
            categoryBox.SelectedItem = AiMemory.NormaliseCategory(entry.Category);
            if (categoryBox.SelectedIndex < 0)
                categoryBox.SelectedIndex = 0;

            Label hint = new Label();
            hint.Location = new Point(206, 15);
            hint.Size = new Size(380, 20);
            hint.ForeColor = Color.Gray;
            hint.Text = "一句話講完,具體到以後單獨看也看得懂。";

            textBox = new TextBox();
            textBox.Location = new Point(12, 44);
            textBox.Size = new Size(574, 100);
            textBox.Multiline = true;
            textBox.ScrollBars = ScrollBars.Vertical;
            textBox.MaxLength = AiMemory.MaxEntryLength;
            textBox.Text = entry.Text ?? "";

            Button ok = new Button();
            ok.Text = "確定";
            ok.DialogResult = DialogResult.OK;
            ok.Location = new Point(414, 158);
            ok.Size = new Size(84, 28);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Location = new Point(502, 158);
            cancel.Size = new Size(84, 28);

            Controls.Add(categoryLabel);
            Controls.Add(categoryBox);
            Controls.Add(hint);
            Controls.Add(textBox);
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        /// <summary>Returns true and writes back into <paramref name="entry"/> when confirmed.</summary>
        public static bool Edit(IWin32Window owner, string title, MemoryEntry entry)
        {
            using (MemoryEntryDialog dialog = new MemoryEntryDialog(title, entry))
            {
                if (dialog.ShowDialog(owner) != DialogResult.OK)
                    return false;
                if (string.IsNullOrWhiteSpace(dialog.textBox.Text))
                    return false;
                entry.Category = dialog.categoryBox.SelectedItem as string ?? AiMemory.DefaultCategory;
                entry.Text = dialog.textBox.Text.Trim();
                return true;
            }
        }
    }
}
