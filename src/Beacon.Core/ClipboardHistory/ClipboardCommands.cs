using Beacon.Core.Commands;
using Beacon.Core.Search;

namespace Beacon.Core.ClipboardHistory;

/// <summary>The launcher's four commands for the clipboard history.</summary>
public static class ClipboardCommands
{
    /// <summary>What the empty input line says while Clear Clipboard History waits for its word.</summary>
    public const string ClearPrompt = "Type yes to clear";

    /// <summary>The one text that clears the history. Anything else, even "Yes" or "yes ", clears nothing.</summary>
    public const string ClearWord = "yes";

    /// <summary>Shown when Clear Clipboard History was given any other text.</summary>
    public const string NotClearedNote = "Nothing was cleared. To clear the clipboard history, type yes and press Enter.";

    /// <summary>
    /// Clipboard History, Pause Clipboard Capture, Resume Clipboard Capture and Clear Clipboard
    /// History, in that order.
    /// </summary>
    /// <param name="open">Opens the clipboard history view. The launcher stays open for it.</param>
    /// <param name="pause">Pauses capture and gives the sentence to show.</param>
    /// <param name="resume">Resumes capture and gives the sentence to show.</param>
    /// <param name="clear">Clears the history on every machine. Whoever does the clearing says how it went.</param>
    /// <param name="notify">Shows a fixed sentence to the owner.</param>
    public static IReadOnlyList<BuiltInCommand> Create(Action open, Func<string> pause, Func<string> resume, Action clear, Action<string> notify) =>
    [
        new BuiltInCommand("clipboard-history", "Clipboard History", "Find what you copied earlier and paste it again", open, KeepOpen: true),
        new BuiltInCommand("pause-clipboard", "Pause Clipboard Capture", "Stop recording what you copy, until you resume", () => notify(pause())),
        new BuiltInCommand("resume-clipboard", "Resume Clipboard Capture", "Record what you copy again", () => notify(resume())),

        // It has no action of its own, so Enter asks for the word first, as Tab does. Clearing
        // cannot be undone and reaches the owner's other machine, so one key press must not do it.
        new BuiltInCommand(
            "clear-clipboard",
            "Clear Clipboard History",
            "Removes every entry on all your machines",
            Run: null,
            Argument: new ItemArgument(ClearPrompt, typed =>
            {
                if (typed == ClearWord)
                {
                    clear();
                }
                else
                {
                    notify(NotClearedNote);
                }
            })),
    ];
}
