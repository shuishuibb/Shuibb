namespace HaRepacker.GUI
{
    /// <summary>What a Ctrl+C / Ctrl+V pressed over the main window should act on.</summary>
    public enum ClipboardShortcutRoute
    {
        /// <summary>
        /// Leave it to the focused text box - ordinary Windows text copy/paste. The window-level
        /// handler must return without marking the key handled so the TextBox still receives it.
        /// </summary>
        TextInput,

        /// <summary>Copy the property editor's selected field rows.</summary>
        FieldCopy,

        /// <summary>Paste the staged field copy onto the selected tree nodes.</summary>
        FieldPaste,

        /// <summary>The tree's own whole-node clipboard (DoCopy / DoPaste).</summary>
        TreeClipboard
    }

    /// <summary>
    /// Picks which of the three clipboards a Ctrl+C / Ctrl+V belongs to.
    ///
    /// MainForm handles these keys on the Window itself, because PreviewKeyDown tunnels
    /// root-to-leaf and therefore reaches it before the tree or any field row, whatever holds
    /// focus. That is what makes field paste work after the user has clicked a different item in
    /// the tree - but it also means a plain text box would never see Ctrl+C unless this rule
    /// hands the key back to it, which is why text input outranks everything else.
    ///
    /// Split out as a plain static so the priority order can be tested without a window.
    /// </summary>
    public static class ClipboardShortcutPolicy
    {
        /// <param name="isTextBoxFocused">Keyboard focus is inside an editable text box.</param>
        /// <param name="isCopy">True for Ctrl+C, false for Ctrl+V.</param>
        /// <param name="hasSelectedEditorFields">The property editor has selected field rows.</param>
        /// <param name="hasCopiedEditorFields">A field copy is staged.</param>
        public static ClipboardShortcutRoute Resolve(bool isTextBoxFocused, bool isCopy,
            bool hasSelectedEditorFields, bool hasCopiedEditorFields)
        {
            // Typing beats everything: selecting text in the 名稱 / 值 / X / Y boxes or the find
            // box must copy that text, never a WZ node - and never raise a confirmation prompt.
            if (isTextBoxFocused)
                return ClipboardShortcutRoute.TextInput;

            if (isCopy)
                return hasSelectedEditorFields ? ClipboardShortcutRoute.FieldCopy : ClipboardShortcutRoute.TreeClipboard;

            return hasCopiedEditorFields ? ClipboardShortcutRoute.FieldPaste : ClipboardShortcutRoute.TreeClipboard;
        }
    }
}
