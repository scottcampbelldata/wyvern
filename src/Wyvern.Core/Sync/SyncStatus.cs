using System.Globalization;

namespace Wyvern.Core.Sync;

/// <summary>How sync stands, in the few kinds the tray icon tells apart.</summary>
public enum SyncCondition
{
    /// <summary>Sync is not set up: there is no sync.json. Wyvern makes no connection, and the tray says nothing about sync.</summary>
    Off,

    /// <summary>
    /// Sync is set up, and the first run that goes to the server has not ended yet. Shown from
    /// the moment that run opens its connection: a run that ends before that (sync is stopped,
    /// or the run does nothing) never shows it.
    /// </summary>
    Waiting,

    /// <summary>The last run did everything there was to do.</summary>
    Ok,

    /// <summary>
    /// The last run did not do everything: some files could not be synced this time, or the run
    /// did nothing, or it broke off in a way nobody expected. It is tried again.
    /// </summary>
    Incomplete,

    /// <summary>The last run finished, and the server holds files that cannot be read. They were skipped.</summary>
    Degraded,

    /// <summary>No connection could be made, or it was lost, or the server did not answer in time. It is tried again, less and less often.</summary>
    Unreachable,

    /// <summary>
    /// The server was reached and could not be used: its folder could not be made, listed or
    /// written, it refused what it was asked, or it holds more than Wyvern reads. It is tried
    /// again, less and less often. With <see cref="SyncStop.ServerFolder"/> it was the folder,
    /// when the connection was made.
    /// </summary>
    ServerUnusable,

    /// <summary>Sync does not run until the owner has done something; <see cref="SyncStatus.Stop"/> says what is wrong.</summary>
    Stopped,
}

/// <summary>
/// Why sync is stopped. Three values are not stops, and say why a run did nothing or could not
/// use the server while sync goes on trying: <see cref="ClipboardFoldersUnreadable"/>,
/// <see cref="FolderIdUnusable"/> and <see cref="ServerFolder"/>. <see cref="DeletionsHeld"/>
/// is a pause: every run looks again, and none sends anything until the owner has answered or
/// the files are back.
/// </summary>
public enum SyncStop
{
    None,

    /// <summary>The server's host key is not the pinned one.</summary>
    HostKeyChanged,

    /// <summary>The server refused the sign-in.</summary>
    Refused,

    /// <summary>
    /// The SSH key file cannot be read or its passphrase is missing or wrong. A key file that is
    /// not on a drive of this machine, is reached through a link, or is a placeholder for a
    /// file that is kept elsewhere is not opened at all, and counts as this too.
    /// </summary>
    KeyFile,

    /// <summary>sync.json is there and cannot be used.</summary>
    ConfigUnusable,

    /// <summary>sync.json holds no confirmed host key: Sync Setup was not finished.</summary>
    SetupUnfinished,

    /// <summary>This machine has no sync key.</summary>
    KeyMissing,

    /// <summary>The sync key cannot be read back on this machine.</summary>
    KeyUnreadable,

    /// <summary>This machine's clock and the server's are more than a day apart. It is tried again by itself.</summary>
    ClockWrong,

    /// <summary>The saved passphrase of the SSH key file cannot be read back on this machine.</summary>
    PassphraseUnreadable,

    /// <summary>
    /// The data folder is the folder that holds Wyvern's own files (the sync key, sync.json,
    /// settings.json, the log), or a folder that one lies under. Syncing it would send those
    /// files to the server and write the other machine's over them.
    /// </summary>
    DataFolderHoldsWyvern,

    /// <summary>The SSH key file that sync.json names lies in the data folder, where it would be synced.</summary>
    KeyFileInDataFolder,

    /// <summary>
    /// Not a stop: clipboard-folders.json is there and cannot be understood, so a run did
    /// nothing. With it <see cref="SyncStatus.Condition"/> is <see cref="SyncCondition.Incomplete"/>,
    /// and the next run looks again: a file that was only held for a moment is unreadable too.
    /// </summary>
    ClipboardFoldersUnreadable,

    /// <summary>
    /// Not a stop: the data folder's id file can be neither read nor made, so a run did nothing.
    /// With it <see cref="SyncStatus.Condition"/> is <see cref="SyncCondition.Incomplete"/>, and
    /// the next run looks again.
    /// </summary>
    FolderIdUnusable,

    /// <summary>
    /// Not a stop: the folder on the server could not be made or listed when the connection was
    /// made. With it <see cref="SyncStatus.Condition"/> is <see cref="SyncCondition.ServerUnusable"/>.
    /// It is tried again like any other failure of the server, and it is said once, because a
    /// folder that sync.json names wrongly does not come right by itself.
    /// </summary>
    ServerFolder,

    /// <summary>
    /// Sync is paused by the brake on mass deletion: many library files that were synced are no
    /// longer in the data folder, and the last run sent nothing and changed nothing rather than
    /// send their deletions. With it <see cref="SyncStatus.Condition"/> is
    /// <see cref="SyncCondition.Stopped"/>. It waits for the owner, who says which it is with
    /// Confirm Sync Deletions or Restore Missing Files. The pause is kept from one run to the
    /// next, and over a restart, by a mark on the disk and not by this value: while it lasts,
    /// a run is held for a single missing library file, and sync goes on by itself only once
    /// a run finds nothing to delete. This value says how the last run ended. A run that
    /// ended some other way meanwhile (a server that could not be reached, say) shows that,
    /// and the pause is still there.
    /// </summary>
    DeletionsHeld,
}

/// <summary>
/// Whether sync is paused by the brake on mass deletion, as far as the runs of this Wyvern have
/// seen, and how many deletions are held. This is apart from <see cref="SyncStatus"/>, which
/// says how the last run ended: a pause lasts until the owner has answered it, whatever the
/// runs in between ran into. Counts only: it holds nothing of the server or of a file.
/// </summary>
/// <remarks>
/// "Paused" and "something is held" are two things. Paused is that the mark of a pause is
/// there: the next run is held for the deletion of a single library file, and it is over only
/// when a run finds nothing to delete or the owner's Confirm Sync Deletions run completes.
/// Something held is that files the owner may not have deleted are missing, which is what
/// Confirm Sync Deletions answers. A pause begins with something held, and lasts until
/// something held is answered. But it can outlast it: after a Restore Missing Files run that
/// completed, the files are back and nothing is held, and the pause is still there until the
/// ordinary run that follows has seen that, which can be an interval later. Three states are
/// told apart. Not paused. Paused with something held, with its count when a run has counted
/// it (<see cref="Held"/>), and without when none has since: after a restart, after a Restore
/// Missing Files run that was cut off, after a run that could not look at the whole library.
/// And paused with nothing held (<see cref="NothingHeld"/>), which is known only when a run
/// went through its plan and the files are back. Confirm Sync Deletions answers the second
/// only. In the third it must not ask the owner to delete missing files when none is missing,
/// and it must not be a permission given in advance for whatever he deletes before the next
/// run. The tray does not say "paused" for the third either: it says how the last run ended,
/// which was well.
/// </remarks>
/// <param name="Paused">
/// True from the run that was held, or from the first run that found the mark of a pause on
/// the disk, until the pause is over; also from a Restore Missing Files run that began one.
/// Before any run of this Wyvern has read the mark, the mark is looked at when this is asked
/// (<see cref="SyncService.Pause"/>).
/// </param>
/// <param name="Held">
/// How many deletions of library files the last run held back, when the last run that got as
/// far as its plan was held. Zero when sync is not paused, and also while it is paused and no
/// run has counted since: after a restart, after a run that restored files and was cut off,
/// after a run with the owner's word that was cut off. Zero does not say that nothing is held:
/// <see cref="NothingHeld"/> does.
/// </param>
/// <param name="NothingHeld">
/// True when sync is paused and the last run that went through its plan found that nothing is
/// held: the library files that were synced are all here, or were fetched back, and no
/// deletion is waiting for the owner's word. The pause itself is there until the next
/// ordinary run has seen it too. Never true when sync is not paused.
/// </param>
public sealed record SyncPause(bool Paused, int Held = 0, bool NothingHeld = false)
{
    /// <summary>Sync is not paused.</summary>
    public static SyncPause None { get; } = new(false);

    /// <summary>The type's name, whether sync is paused, the count and whether nothing is held.</summary>
    public override string ToString() => FormattableString.Invariant($"{nameof(SyncPause)} {{ Paused = {Paused}, Held = {Held}, NothingHeld = {NothingHeld} }}");
}

/// <summary>How sync stands. It holds nothing of the server or of a file.</summary>
/// <param name="LastSynced">When a run last finished with everything it could read synced, or null when none has since Wyvern started.</param>
public sealed record SyncStatus(SyncCondition Condition, SyncStop Stop = SyncStop.None, DateTimeOffset? LastSynced = null)
{
    /// <summary>The longest text <see cref="Tooltip"/> gives. The tray icon's whole text may be 127 characters.</summary>
    public const int MaxTooltipLength = 90;

    // No advice to delete it here: the file may only be held for a moment, and deleting a good
    // one makes this machine's earlier clipboard folders another machine's. The sentence the
    // owner is told has room to say when deleting is safe.
    private const string FoldersLine = "Sync did nothing: clipboard-folders.json cannot be understood. Correct it";

    private const string FolderIdLine = "Sync did nothing: Wyvern could not read or write its id file in the data folder";

    public static SyncStatus Off { get; } = new(SyncCondition.Off);

    /// <summary>
    /// What the tray icon says about sync: fixed words and a time, on one line, or nothing when
    /// sync is not set up. The time is given in the owner's zone, with the date unless it is today.
    /// </summary>
    public string Tooltip(DateTimeOffset now, TimeZoneInfo zone)
    {
        string last = LastSynced is { } time ? "Last synced " + Local(time, now, zone) : "Not synced yet";
        return Condition switch
        {
            SyncCondition.Off => "",
            SyncCondition.Waiting => "Sync has not run yet",
            SyncCondition.Ok => last,
            SyncCondition.Incomplete when Stop == SyncStop.ClipboardFoldersUnreadable => FoldersLine,
            SyncCondition.Incomplete when Stop == SyncStop.FolderIdUnusable => FolderIdLine,
            SyncCondition.Incomplete => "Sync incomplete. " + last,
            SyncCondition.Degraded => "Sync degraded: some server files cannot be read. " + last,
            SyncCondition.Unreachable => "Sync server unreachable. " + last,
            SyncCondition.ServerUnusable => "Sync could not use the server. " + last,
            _ => Stop switch
            {
                SyncStop.HostKeyChanged => "Sync stopped: the server's key changed. Run Sync Setup",
                SyncStop.Refused => "Sync stopped: the server refused the sign-in. Run Sync Setup",
                SyncStop.KeyFile => "Sync stopped: the SSH key file cannot be used. Run Sync Setup",
                SyncStop.ConfigUnusable => "Sync stopped: sync.json cannot be used",
                SyncStop.SetupUnfinished => "Sync is not set up completely. Run Sync Setup",
                SyncStop.KeyMissing => "Sync stopped: this machine has no sync key. Run Sync Setup",
                SyncStop.KeyUnreadable => "Sync stopped: the sync key cannot be read. Run Sync Setup",
                SyncStop.ClockWrong => "Sync stopped: this machine's clock and the server's differ",
                SyncStop.PassphraseUnreadable => "Sync stopped: the SSH key's saved passphrase cannot be read. Run Sync Setup",
                SyncStop.DataFolderHoldsWyvern => "Sync stopped: the data folder holds Wyvern's own files. Choose another data folder",
                SyncStop.KeyFileInDataFolder => "Sync stopped: the SSH key file is inside the data folder. Move it elsewhere",
                SyncStop.ClipboardFoldersUnreadable => FoldersLine,
                SyncStop.FolderIdUnusable => FolderIdLine,
                SyncStop.DeletionsHeld => "Sync paused: synced files are missing here",
                _ => "Sync stopped",
            },
        };
    }

    /// <summary>
    /// The tray icon's whole text: what it says about clipboard capture, and after it what it
    /// says about sync when sync is set up. Never longer than the icon can hold.
    /// </summary>
    public string WithCapture(string captureTooltip, DateTimeOffset now, TimeZoneInfo zone)
    {
        string sync = Tooltip(now, zone);
        return sync.Length == 0 ? captureTooltip : captureTooltip + ". " + sync;
    }

    /// <summary>The type's name, the condition and the reason.</summary>
    public override string ToString() => $"{nameof(SyncStatus)} {{ Condition = {Condition}, Stop = {Stop} }}";

    private static string Local(DateTimeOffset time, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(time, zone);
        bool today = local.Date == TimeZoneInfo.ConvertTime(now, zone).Date;
        return local.ToString(today ? "HH:mm" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }
}
