using HaRepacker.GUI;
using Xunit;
using Assert = Xunit.Assert;

namespace ClipboardRoutingTests;

/// <summary>
/// Targeted regression for "Ctrl+C in the 名稱 box copied the whole WZ node instead of the
/// selected text".
///
/// MainForm handles Ctrl+C/Ctrl+V on the Window, so it sees them before any text box does. It
/// used to hand the key back only for the property editor's own value boxes, which left the node
/// header's 名稱 / 值 / X / Y and the find box being swallowed by the WZ clipboards.
/// ClipboardShortcutPolicy is the priority rule MainForm now follows, and is what these tests
/// cover.
///
/// SCOPE - what these tests do NOT cover, and must not be cited as proof of:
///   * MainForm actually evaluating "Keyboard.FocusedElement is TextBox" against live focus,
///   * that returning without e.Handled really lets the TextBox receive the key,
///   * the copy/paste actions themselves (DoCopy/DoPaste, field copy/paste, confirmations).
/// Those need a real window and are verified manually.
/// </summary>
public sealed class ClipboardShortcutPolicyTests
{
    private const bool Copy = true;
    private const bool Paste = false;

    [Theory]
    [InlineData(Copy)]
    [InlineData(Paste)]
    public void TextBoxFocused_AlwaysWinsRegardlessOfFieldState(bool isCopy)
    {
        // Even with a field row selected and a field copy staged - typing beats both, so
        // selecting text in 名稱 and pressing Ctrl+C copies that text and prompts for nothing.
        Assert.Equal(ClipboardShortcutRoute.TextInput, ClipboardShortcutPolicy.Resolve(
            isTextBoxFocused: true, isCopy: isCopy,
            hasSelectedEditorFields: true, hasCopiedEditorFields: true));

        Assert.Equal(ClipboardShortcutRoute.TextInput, ClipboardShortcutPolicy.Resolve(
            isTextBoxFocused: true, isCopy: isCopy,
            hasSelectedEditorFields: false, hasCopiedEditorFields: false));
    }

    [Fact]
    public void NoTextBox_WithSelectedFields_CopyGoesToTheFieldClipboard()
    {
        Assert.Equal(ClipboardShortcutRoute.FieldCopy, ClipboardShortcutPolicy.Resolve(
            isTextBoxFocused: false, isCopy: Copy,
            hasSelectedEditorFields: true, hasCopiedEditorFields: false));
    }

    [Fact]
    public void NoTextBox_WithStagedFieldCopy_PasteGoesToTheFieldClipboard()
    {
        Assert.Equal(ClipboardShortcutRoute.FieldPaste, ClipboardShortcutPolicy.Resolve(
            isTextBoxFocused: false, isCopy: Paste,
            hasSelectedEditorFields: false, hasCopiedEditorFields: true));
    }

    [Fact]
    public void NoTextBox_NoFieldState_FallsBackToTheTreeClipboard()
    {
        Assert.Equal(ClipboardShortcutRoute.TreeClipboard, ClipboardShortcutPolicy.Resolve(
            isTextBoxFocused: false, isCopy: Copy,
            hasSelectedEditorFields: false, hasCopiedEditorFields: false));

        Assert.Equal(ClipboardShortcutRoute.TreeClipboard, ClipboardShortcutPolicy.Resolve(
            isTextBoxFocused: false, isCopy: Paste,
            hasSelectedEditorFields: false, hasCopiedEditorFields: false));
    }

    [Fact]
    public void SelectedFieldsDoNotCaptureAPaste()
    {
        // Ctrl+V is decided by the staged copy, not by what happens to be selected - selecting
        // rows on the target must not turn a tree paste into a field paste.
        Assert.Equal(ClipboardShortcutRoute.TreeClipboard, ClipboardShortcutPolicy.Resolve(
            isTextBoxFocused: false, isCopy: Paste,
            hasSelectedEditorFields: true, hasCopiedEditorFields: false));
    }

    [Fact]
    public void AStagedFieldCopyDoesNotCaptureACopy()
    {
        // Ctrl+C is decided by the current selection, not by the staged copy - with nothing
        // selected it stays a tree copy, which is what lets "last Ctrl+C wins" work.
        Assert.Equal(ClipboardShortcutRoute.TreeClipboard, ClipboardShortcutPolicy.Resolve(
            isTextBoxFocused: false, isCopy: Copy,
            hasSelectedEditorFields: false, hasCopiedEditorFields: true));
    }
}
