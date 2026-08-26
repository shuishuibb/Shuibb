using System;
using System.Windows.Forms;
using HaRepacker.GUI.Panels;

namespace TokiAi
{
    /// <summary>
    /// The one entry point the editor calls. WvsWzImg reaches this by reflection - it holds no
    /// compile-time reference to this assembly - so the signature here is the contract and must
    /// not change: a public static Show(object, string) on TokiAi.AiAssistantHost.
    /// </summary>
    public static class AiAssistantHost
    {
        static AiChatForm window;

        /// <param name="nodePath">
        /// The WZ path the user right-clicked, or null when opened from the Tools menu. It is
        /// seeded into the input box rather than hidden away in state, so what the model gets
        /// sent is exactly what the user can see and edit.
        /// </param>
        public static void Show(object mainPanel, string nodePath)
        {
            try
            {
                MainPanel panel = mainPanel as MainPanel;
                if (panel == null)
                {
                    MessageBox.Show("AI 助手需要一個已開啟的 WZ 分頁。請先開啟一個 WZ 檔案。",
                        "AI 助手", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                if (window == null || window.IsDisposed)
                {
                    window = new AiChatForm(panel);
                    window.FormClosed += delegate { window = null; };
                    window.Show();
                }
                else
                {
                    if (window.WindowState == FormWindowState.Minimized)
                        window.WindowState = FormWindowState.Normal;
                    window.Activate();
                    window.BringToFront();
                }

                if (!string.IsNullOrWhiteSpace(nodePath))
                    window.SeedContextPath(nodePath);
            }
            catch (Exception error)
            {
                MessageBox.Show("AI 助手啟動失敗:\n\n" + error,
                    "AI 助手", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
