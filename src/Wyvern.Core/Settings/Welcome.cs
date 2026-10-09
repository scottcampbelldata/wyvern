namespace Wyvern.Core.Settings;

/// <summary>
/// When Wyvern shows its welcome window, which asks for the launcher's hotkey and the
/// clipboard history's: once on a machine, and only while the clipboard history has no hotkey,
/// which is what the window is there to give it.
/// </summary>
public static class Welcome
{
    /// <summary>The hotkey the window suggests for the clipboard history. The owner presses another if this one is wanted elsewhere.</summary>
    public const string SuggestedClipboardHotkey = "Ctrl+Shift+V";

    /// <summary>What the file that remembers the welcome holds.</summary>
    public const string MarkerText = "Wyvern has shown its welcome window on this PC.\n";

    /// <param name="shownBefore">True when the welcome has been shown on this machine before.</param>
    /// <param name="clipboardHotkey">The clipboard history's hotkey as settings.json has it, or nothing when it has none.</param>
    public static bool IsDue(bool shownBefore, string? clipboardHotkey) =>
        !shownBefore && string.IsNullOrWhiteSpace(clipboardHotkey);
}
