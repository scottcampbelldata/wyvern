using Wyvern.Core.Commands;
using Wyvern.Core.Search;

namespace Wyvern.Core.Sync;

/// <summary>The launcher's five commands for sync.</summary>
public static class SyncCommands
{
    /// <summary>The one text that lets Confirm Sync Deletions start its run. Anything else, even "Yes" or "yes ", starts nothing.</summary>
    public const string ConfirmWord = "yes";

    /// <summary>Shown when Confirm Sync Deletions was given any other text while sync is paused.</summary>
    public const string NotConfirmedNote = "Nothing was confirmed, and sync is still paused. To confirm the deletions, type yes and press Enter.";

    /// <summary>
    /// What the empty input line says while Confirm Sync Deletions waits for its word: what the
    /// word will do, in fixed words with the count of the deletions that are held. Without the
    /// count while sync is paused and no run has counted since (<see cref="SyncPause.Held"/>).
    /// While sync is not paused, and while it is paused with nothing held
    /// (<see cref="SyncPause.NothingHeld"/>), there is nothing to type, and the line says that.
    /// </summary>
    public static string ConfirmPrompt(SyncPause pause) =>
        !pause.Paused ? SyncService.NotPausedNote
        : pause.NothingHeld ? SyncService.NothingWaitingNote
        : pause.Held == 1 ? "Type yes to delete 1 file on your other machines too"
        : pause.Held > 1 ? FormattableString.Invariant($"Type yes to delete {pause.Held} files on your other machines too")
        : "Type yes to delete the missing files on your other machines too";

    /// <summary>
    /// Sync Now, Sync Setup, Show Sync Key, Confirm Sync Deletions and Restore Missing Files, in
    /// that order. The last two are the owner's answer to a sync that is paused because many
    /// synced files are missing from the data folder; the sentence that tells him of the pause
    /// names both by their titles (<see cref="SyncService.MissingFilesNote"/>).
    /// </summary>
    /// <remarks>
    /// Restore Missing Files is one key press and works at any time: it deletes nothing.
    /// Confirm Sync Deletions has the owner's other machines delete files, so it asks before
    /// it acts, as Clear Clipboard History does: it takes the typed word
    /// <see cref="ConfirmWord"/>, and its prompt says what the word will do, with the count as
    /// it is when the prompt is shown. And it answers something that is held, and nothing
    /// else: while sync is not paused it starts nothing, whatever is typed, and says
    /// <see cref="SyncService.NotPausedNote"/>, and while sync is paused with nothing held
    /// (the files are back, and the pause lasts until the next ordinary run has seen that) it
    /// starts nothing either and says <see cref="SyncService.NothingWaitingNote"/>. So that a
    /// deletion cannot be allowed in advance, and so that he is not asked to delete missing
    /// files when none is missing.
    /// </remarks>
    /// <param name="syncNow">Starts a sync run and returns at once. Whoever runs it says how it went.</param>
    /// <param name="openSetup">Opens the Sync Setup window.</param>
    /// <param name="showKey">Opens the window that shows the sync key.</param>
    /// <param name="pause">Whether sync is paused and how many deletions are held (<see cref="SyncService.Pause"/>). Asked each time the prompt is shown and when the word is given; it must answer at once.</param>
    /// <param name="confirmDeletions">Starts a sync run that may send any number of deletions, and returns at once (<see cref="SyncService.ConfirmDeletions"/>). Called only for the typed word, while sync is paused.</param>
    /// <param name="restoreMissing">Starts a sync run that fetches the missing files back from the server, and returns at once (<see cref="SyncService.RestoreMissing"/>).</param>
    /// <param name="notify">Shows a fixed sentence to the owner.</param>
    public static IReadOnlyList<BuiltInCommand> Create(Action syncNow, Action openSetup, Action showKey, Func<SyncPause> pause, Action confirmDeletions, Action restoreMissing, Action<string> notify) =>
    [
        new BuiltInCommand("sync-now", "Sync Now", "Send and fetch changes with your own server now", syncNow),
        new BuiltInCommand("sync-setup", "Sync Setup", "Connect Wyvern to your own server over SFTP", openSetup),
        new BuiltInCommand("show-sync-key", "Show Sync Key", "Show the key to enter on your other machine", showKey),

        // It has no action of its own, so Enter asks for the word first, as Tab does. What is
        // asked first is whether anything is held: not while sync is not paused, and not while
        // it is paused with nothing held, so no word confirms anything then. Asked once, so
        // that the answer and the word are about one moment.
        new BuiltInCommand(
            "confirm-sync-deletions",
            "Confirm Sync Deletions",
            "While sync is paused: send the deletions of files you deleted, moved or renamed",
            Run: null,
            Argument: new ItemArgument(ConfirmPrompt(SyncPause.None), typed =>
            {
                var now = pause();
                if (!now.Paused)
                {
                    notify(SyncService.NotPausedNote);
                }
                else if (now.NothingHeld)
                {
                    notify(SyncService.NothingWaitingNote);
                }
                else if (typed == ConfirmWord)
                {
                    confirmDeletions();
                }
                else
                {
                    notify(NotConfirmedNote);
                }
            })
            {
                PromptNow = () => ConfirmPrompt(pause()),
            }),
        new BuiltInCommand("restore-missing-files", "Restore Missing Files", "Sync once and fetch back the synced files that are missing here", restoreMissing),
    ];
}
