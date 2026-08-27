using System;
using System.Windows.Forms;

namespace HaRepacker
{
    public static class Warning
    {
        public static bool Warn(string text)
        {
            return Program.ConfigurationManager.UserSettings.SuppressWarnings || MessageBox.Show(text, Properties.Resources.Warning, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
        }

        /// <summary>
        /// Test seam for <see cref="ConfirmRequired"/>: harnesses that cannot click a modal
        /// dialog install a delegate here to observe that the confirmation was requested and to
        /// script the answer. Never set in the product itself.
        /// </summary>
        internal static Func<string, bool> ConfirmRequiredOverride;

        /// <summary>
        /// A confirmation the user must always see. Unlike <see cref="Warn"/>, the
        /// SuppressWarnings setting does NOT bypass it - structural node operations
        /// (copy/paste/delete) confirm every time, because a silently skipped delete
        /// confirmation is a data-loss hazard, not a warning.
        /// </summary>
        public static bool ConfirmRequired(string text)
        {
            Func<string, bool> testOverride = ConfirmRequiredOverride;
            if (testOverride != null)
                return testOverride(text);
            return MessageBox.Show(text, Properties.Resources.Warning, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
        }

        public static void Error(string text)
        {
            MessageBox.Show(text, Properties.Resources.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
