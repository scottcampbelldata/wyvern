namespace Beacon.Core.Settings;

/// <summary>Where Beacon keeps its per-machine files, and where its data folder is.</summary>
public sealed class BeaconPaths
{
    /// <summary>Environment variable that replaces the default root, for development and manual testing.</summary>
    public const string HomeVariable = "BEACON_HOME";

    public BeaconPaths(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string UsageFile => Path.Combine(Root, "usage.json");

    /// <summary>The owner's favorites and the aliases given to items, on this machine. See <c>ItemMarks</c>.</summary>
    public string FavoritesFile => Path.Combine(Root, "favorites.json");

    public string LogFile => Path.Combine(Root, "logs", "beacon.log");

    /// <summary>
    /// Remembers, on this machine, whether clipboard capture is paused. The app never rewrites
    /// settings.json, so it cannot be kept there.
    /// </summary>
    public string ClipboardStateFile => Path.Combine(Root, "clipboard-state.json");

    /// <summary>
    /// Remembers, on this machine, the names of the clipboard folders this machine has written
    /// into. After "deviceName" has been changed, the folder of the earlier name is still this
    /// machine's to scrub and to sync.
    /// </summary>
    public string ClipboardFoldersFile => Path.Combine(Root, "clipboard-folders.json");

    /// <summary>There once Beacon has shown its welcome window on this machine, so that it is shown once. It holds one line that says what it is.</summary>
    public string WelcomedFile => Path.Combine(Root, "welcomed");

    /// <summary>
    /// The folder that holds the clipboard entries the owner has pinned: a list of them and
    /// the files with their content. It is beside the settings and not in the data folder,
    /// so it stays on this machine and the history's own rules do not reach it.
    /// </summary>
    public string ClipboardPinsFolder => Path.Combine(Root, "clipboard-pins");

    /// <summary>Where the setup program of an update is kept from the moment it is fetched until it is run. Emptied of older ones when Beacon starts.</summary>
    public string UpdatesFolder => Path.Combine(Root, "updates");

    /// <summary>
    /// What was read in the pictures of the clipboard history, so that a screenshot is found
    /// by a word in it. Beside the settings and not in the data folder: it stays on this
    /// machine. See <c>ClipPictureText</c>.
    /// </summary>
    public string ClipboardPictureTextFile => Path.Combine(Root, "clipboard-picture-text.json");

    /// <summary>
    /// Where sync connects to: the server, the user, the SSH key file, the folder on the server
    /// and the server's pinned host key. Sync Setup writes it, and it can be edited by hand.
    /// It is not in settings.json because the app writes it and never rewrites settings.json.
    /// </summary>
    public string SyncFile => Path.Combine(Root, "sync.json");

    /// <summary>What was synced last, file by file, so that the next run can tell what has changed since.</summary>
    public string SyncStateFile => Path.Combine(Root, "sync-state.json");

    /// <summary>
    /// The mark that sync is paused: a run would have sent the deletions of many library files,
    /// sent nothing, and waits for the owner to say which it is. Sync writes it when the pause
    /// begins and removes it when the pause ends, so that a restart does not lift a pause.
    /// Whatever is there under this name is the mark; what it holds is not read.
    /// </summary>
    public string SyncPausedFile => Path.Combine(Root, "sync-paused");

    /// <summary>The sync key, protected so that only this Windows user on this machine can read it.</summary>
    public string KeyFile => Path.Combine(Root, "key.bin");

    /// <summary>The passphrase of the SSH key file, when it has one, protected in the same way.</summary>
    public string PassphraseFile => Path.Combine(Root, "ssh-passphrase.bin");

    /// <summary>The data folder when "dataDir" is not set: "data" under the root.</summary>
    public string DefaultDataDir => Path.Combine(Root, "data");

    /// <summary>The data folder: the one "dataDir" names when it can be used, otherwise the default.</summary>
    public DataPaths Data(DataDirSetting setting) => new(setting.Folder ?? DefaultDataDir);

    /// <summary>
    /// %LOCALAPPDATA%\Beacon, unless BEACON_HOME names another folder. A BEACON_HOME that is
    /// written relative to the folder Beacon was started from is made a full path here, once:
    /// every path under the root would otherwise be relative, and a script is run with its own
    /// folder as the working folder, where a relative path to it means a different file.
    /// </summary>
    public static BeaconPaths Default()
    {
        string? home = Environment.GetEnvironmentVariable(HomeVariable);
        return string.IsNullOrWhiteSpace(home)
            ? new BeaconPaths(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Beacon"))
            : new BeaconPaths(Path.GetFullPath(home));
    }
}
