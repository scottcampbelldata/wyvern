using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Threading;
using Beacon.App.Interop;
using Beacon.Core.Apps;
using Beacon.Core.AutoExpand;
using Beacon.Core.ClipboardHistory;
using Beacon.Core.Commands;
using Beacon.Core.Diagnostics;
using Beacon.Core.Files;
using Beacon.Core.Launcher;
using Beacon.Core.Library;
using Beacon.Core.Platform;
using Beacon.Core.Scripts;
using Beacon.Core.Search;
using Beacon.Core.Settings;
using Beacon.Core.Sync;

namespace Beacon.App;

/// <summary>Builds and owns everything that lives for the whole run of the app.</summary>
/// <remarks>
/// Windows keeps the text of notifications in its own history. So a notification here is
/// fixed text that points at the log, and never an item's title or what an exception says.
/// What a notification may name: a library file, a folder or the data folder
/// from settings.json, and the hotkey as written there. A script's notification is the one
/// that carries text from outside: the last line the owner's own script printed. Everything
/// said about the clipboard history is a fixed sentence: nothing that was copied, and no name
/// of a program it was copied from, ever reaches a notification or the log. Everything said
/// about sync is a fixed sentence too, with at most a count or a line number of sync.json: the
/// server's name, the user, a path, a file's name and the sync key never reach a notification
/// or the log. And everything said about snippet auto-expand is a fixed sentence or a count:
/// no key that was pressed, no character, no keyword and no snippet's name ever reaches a
/// notification or the log.
/// </remarks>
public sealed partial class BeaconShell : IDisposable
{
    // What the owner is told when something he asked for failed. The log has the rest.
    private const string ActionFailedNote = "That did not work. Details are in the log file.";

    // Added to what the owner is told when a Reload could not read settings.json while snippet
    // auto-expand is on. Everything stays as it was then, the keyboard hook included, and a
    // file edited to switch it off with a slip in it must not leave him believing it is gone.
    private const string AutoExpandStaysOnNote = "Snippet auto-expand stays on: its keyboard hook is still installed.";

    // How long closing waits for the clipboard history to finish the work it was given, and
    // then for pictures that are being saved.
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(3);

    private readonly Application _app;
    private readonly BeaconPaths _paths = BeaconPaths.Default();
    private readonly FileLog _log;
    private readonly ShellProcessLauncher _processes = new();
    private readonly AppProvider _apps;
    private readonly FileIndex _fileIndex = new();
    private readonly FileIndexService _files;
    private readonly LibraryService _library;
    private readonly ScriptRunner _scripts;
    private readonly WindowsClipboard _windowsClipboard = new();
    private readonly ForegroundWindowTarget _pasteTarget = new();
    private readonly IconCache _icons;
    private readonly LauncherWindow _launcher;
    private readonly TrayIcon _tray;
    private readonly NoteQueue _notes;
    private readonly HotkeyManager _hotkeys = new();
    private readonly HotkeyManager _clipboardHotkeys = new();

    // The hotkeys that run a command from anywhere, one for each that settings.json gives
    // one, and the launcher's own commands by their short names.
    private readonly List<HotkeyManager> _commandHotkeys = [];
    private readonly Dictionary<string, BuiltInCommand> _builtIn = new(StringComparer.OrdinalIgnoreCase);
    private readonly ClipHistoryService _history;
    private readonly ClipboardListener _clipboardListener;
    private readonly ClipboardReader _clipboardReader;
    private readonly ClipCapture _capture;
    private readonly ClipboardViewModel _clipboardView;

    // What was read in the pictures of the clipboard history, and the pinned entries it is
    // read for as well. Null when settings.json says "clipboardPictureText": false.
    private readonly ClipPictureText? _pictureText;
    private readonly ClipPins _clipPins;
    private readonly SystemClock _clock = new();
    private readonly DpapiKeyProtector _protector = new();
    private readonly RetryingClipboard _clipboard;
    private readonly LauncherViewModel _viewModel;
    private readonly SyncService _sync;

    // Snippet auto-expand. The hook is the only thing in Beacon that is told of key presses.
    // Nothing but the expander is handed it, and the expander installs it only while
    // settings.json has auto-expand switched on. The hook has a thread of its own, which
    // exists only while the hook is installed: a key press never waits for this thread.
    private readonly KeyboardHook _keyboardHook;
    private readonly AutoExpander _autoExpand;

    // The two sync windows, each open at most once, and whether one is on its way to being
    // shown (its view model reads files away from this thread first). Used on this thread only.
    private SyncSetupWindow? _setupWindow;
    private SettingsWindow? _settingsWindow;

    // A few fixed words near the bottom of the screen, for what was done after the launcher hid.
    private readonly Hud _hud = new();
    private readonly ConfettiOverlay _confetti = new();
    private readonly EmojiFont _emojiFont = new();

    // The view that shows what a script printed, and what Create SSH Key for Sync has to say.
    private OutputViewModel? _output;
    private SyncKeyWindow? _keyWindow;
    private bool _setupOpening;
    private bool _keyOpening;

    // The saves of pictures (Ctrl+S) that have been started, each on a pool thread. Used on
    // this thread only: a save is started here, and closing waits for them here.
    private readonly List<Task> _saves = [];

    // Written on this thread when settings.json has been read, and read by capture on the
    // history's background task for every change of the clipboard.
    private volatile ClipboardOptions _clipboardOptions = ClipboardOptions.Default;

    // How long, in thousandths of a second, the clipboard has to have been left alone before
    // a copy is read from it. "clipboardReadDelayMilliseconds" in settings.json; see ClipCapture.DefaultSettle.
    private volatile int _clipboardReadDelay = (int)ClipCapture.DefaultSettle.TotalMilliseconds;
    private bool _toldHistoryCannotBeWritten;
    private bool _toldListenerFailed;

    // True from the moment Beacon begins to close. Used on this thread only.
    private bool _closing;

    private FileIndexOptions? _fileOptionsInUse;
    private DataDirSetting? _dataDirInUse;
    private DataPaths? _data;
    private ScriptOptions _scriptOptions = ScriptOptions.Default;
    private SyncOptions _syncOptions = SyncOptions.Default;

    // Whether settings.json has snippet auto-expand switched on. False until the file has been
    // read and says true: a file that cannot be read never switches the keyboard hook on.
    private bool _autoExpandOn;

    public BeaconShell(Application app)
    {
        _app = app;
        // Notes that come close together are shown one after the other. A notification replaces
        // the one before it, and at startup the note about settings.json would be gone within
        // milliseconds, replaced by the library's note about files that could not be loaded.
        _notes = new NoteQueue(ShowNote, Later);
        var clock = _clock;
        _log = new FileLog(_paths.LogFile, clock);
        _icons = new IconCache((summary, error) => _log.Error(summary, error));

        _apps = new AppProvider(new ShellAppCatalog(), _processes, CopyText);

        // Another program can hold the clipboard for a moment, so each use of it is tried a few times.
        _clipboard = new RetryingClipboard(_windowsClipboard, Thread.Sleep);
        var clipboard = _clipboard;
        var paste = new PasteService(clipboard, _pasteTarget, Later, Notify, (message, error) => _log.Error(message, error));

        // Snippet auto-expand. Nothing is installed here: the keyboard hook goes in when the
        // settings are applied, at the end of ApplySettings, and only if settings.json switches
        // it on. A keyword that was typed is replaced through the same paste service, and so
        // the same marked clipboard writer, as a snippet run from the launcher: the two pastes
        // know of each other, and nothing auto-expand pastes is recorded as a clipboard entry.
        // The hook's callback runs on the hook's own thread. A keyword it has seen is handed
        // to this thread with BeginInvoke, which does not wait for this thread, so no key
        // press is ever held up by what this thread is busy with; the replacement is made
        // here, where the paste service and the clipboard are. A failure inside that callback
        // comes the same way: it is logged afterwards, by its type as every exception is, and
        // never from inside the callback. The expander's callbacks are given by the name of
        // their parameter: by position a swap of two of them would still compile.
        _keyboardHook = new KeyboardHook(
            error => _app.Dispatcher.BeginInvoke(() => _log.Error("The keyboard hook could not look at a key press", error)));
        _autoExpand = new AutoExpander(
            _keyboardHook,
            _pasteTarget,
            paste,
            clock,
            post: action => _app.Dispatcher.BeginInvoke(action),
            later: Later,
            notify: Notify,
            report: Report);

        // Clipboard history. The history does its reading and writing on a background task of
        // its own and says so from there, so everything it tells is posted back to this thread.
        // The listener only says that the clipboard changed, and the reader only copies blocks
        // of bytes: what is kept, and what is never kept, is decided in ClipCapture. Nothing is
        // listened to until Start, below, has read whether capture was paused when Beacon last ran.
        var codec = new WpfImageCodec();
        _history = new ClipHistoryService(
            clock,
            Report,
            note => _app.Dispatcher.BeginInvoke(() => Notify(note)),
            () => _app.Dispatcher.BeginInvoke(HistoryChanged),
            foldersFile: _paths.ClipboardFoldersFile);
        _clipboardListener = new ClipboardListener((message, error) => _log.Error(message, error));
        _clipboardReader = new ClipboardReader();
        _capture = new ClipCapture(
            _clipboardReader,
            _clipboardListener,
            codec,
            _history.Post,
            () => _history.IsWriter,
            () => _clipboardOptions,
            _paths.ClipboardStateFile,
            Thread.Sleep,
            Report,
            ShowCaptureState,
            settle: () => TimeSpan.FromMilliseconds(_clipboardReadDelay));
        var paster = new ClipPaster(paste, codec, Notify, Report);

        // A copy of a picture is saved away from this thread (SavePictures), so what the
        // recovery has to tell is posted back to it.
        var recovery = new ClipRecovery(PicturesFolder, _processes, note => _app.Dispatcher.BeginInvoke(() => Notify(note)));

        // A screenshot is found by a word in it: the text in each picture of the history is
        // read once, by the text reader that is part of Windows, on this PC and nowhere
        // else. What was read stays beside the settings and goes when its entry goes. With
        // "clipboardPictureText": false in settings.json nothing is read, and what was
        // kept before is removed.
        _clipPins = new ClipPins(_paths.ClipboardPinsFolder, Report);
        if (SettingsDocument.Read(_paths.SettingsFile)?.Flag("clipboardPictureText", true) ?? true)
        {
            _pictureText = new ClipPictureText(
                _paths.ClipboardPictureTextFile,
                new PictureTextReader(),
                Report,
                changed: () => _app.Dispatcher.BeginInvoke(PictureTextChanged));
        }
        else
        {
            ForgetPictureText();
        }

        _clipboardView = new ClipboardViewModel(
            () => _history.Current,
            clock,
            new ClipboardActions(paster.Paste, _history.Delete, entry => SavePictures(recovery, entry), _processes.OpenLink)
            {
                Copy = entry => CopyEntry(paster, entry),
                Say = _hud.Show,
                PasteText = paster.PasteText,
                SaveAsSnippet = entry => OpenSnippetForm(paster.Text(entry) ?? ""),
                CopyText = CopyText,
            },
            pins: _clipPins,
            pictureText: _pictureText is { } read ? read.Of : null);

        // The emoji picker and the launcher's view model are made further down, once what
        // they need is there. The two commands that open a view in the launcher reach them
        // through these.
        EmojiViewModel? emojiView = null;
        LauncherViewModel? launcherModel = null;
        var windowList = WindowSwitcher.Scope(new OpenWindows());
        BuiltInCommand[] builtIn =
        [
            .. ClipboardCommands.Create(_clipboardView.Open, _capture.Pause, _capture.Resume, _history.Clear, Notify),
            .. PcCommands.Create(new WindowsSystemActions(), _hud.Show),
            .. WindowCommands.Create(new WindowMover(() => _pasteTarget.Remembered), _hud.Show),
            new BuiltInCommand("new-snippet", "Create Snippet", "Make a snippet: a name, the text, and a keyword if you like", NewSnippet),
            new BuiltInCommand("new-quicklink", "Create Quicklink", "Make a quicklink: a name and a web address, a file or a folder", OpenQuicklinkForm),
            new BuiltInCommand("open-snippets", "Open Snippets Folder", "Show the folder that holds your snippet files", OpenSnippetsFolder),
            new BuiltInCommand("edit-quicklinks", "Edit Quicklinks", "Open quicklinks.json in your editor", EditQuicklinks),
            new BuiltInCommand("open-library", "Open Library Folder", "Show the folder that holds your quicklinks, snippets and scripts", OpenLibraryFolder),
            new BuiltInCommand("settings", "Settings", "Change the hotkeys, the folders, the clipboard history and sync", OpenSettings),
            new BuiltInCommand("open-settings", "Open Settings File", "Edit settings.json, then run Reload", OpenSettingsFile),
            new BuiltInCommand("reload", "Reload", "Re-read settings, installed apps, indexed folders and the library", Reload),
            .. SyncCommands.Create(SyncNow, OpenSyncSetup, ShowSyncKey, PausedSync, ConfirmSyncDeletions, RestoreMissingFiles, Notify),
            new BuiltInCommand("switch-windows", WindowSwitcher.Heading, "Go to a window that is open", () => launcherModel?.OpenScope(windowList), KeepOpen: true),
            new BuiltInCommand("search-emoji", EmojiViewModel.Heading, "Find an emoji or a symbol and paste it", () => emojiView?.Open(), KeepOpen: true),
            new BuiltInCommand("appearance-dark", "Use Dark Appearance", "Make Beacon's windows dark", () => UseAppearance(Appearance.Dark)),
            new BuiltInCommand("appearance-light", "Use Light Appearance", "Make Beacon's windows light", () => UseAppearance(Appearance.Light)),
            new BuiltInCommand("appearance-system", "Use Windows Appearance", "Make Beacon's windows light or dark as Windows is", () => UseAppearance(Appearance.System)),
            new BuiltInCommand("confetti", "Confetti", "Throw confetti over the screen", ThrowConfetti),
            new BuiltInCommand("create-sync-key", "Create SSH Key for Sync", "Make a key file for this PC and show the line to add on your server", ShowSyncKeyHelp, KeepOpen: true),
            new BuiltInCommand("check-updates", "Check for Updates", "Ask your update server whether there is a newer Beacon", CheckForUpdates),
            new BuiltInCommand("update-beacon", "Update Beacon", "Download the newest Beacon from your update server and install it", InstallUpdate),
            new BuiltInCommand("quit", "Quit Beacon", "Close the launcher and its tray icon", Quit),
        ];
        foreach (var command in builtIn)
        {
            _builtIn[command.Id] = command;
        }

        var commands = new CommandProvider(builtIn);

        var usage = new UsageStore(_paths.UsageFile, clock);

        _files = new FileIndexService(_fileIndex, Report);
        var files = new FileProvider(_fileIndex, _processes, () => usage.IdsStartingWith(FileProvider.IdPrefix), CopyText);

        // A note from the library names the files that could not be used, so it goes to the
        // screen and never to the log. It arrives on a background thread.
        // When a load has been put into use, the results on screen are made again: with sync,
        // library files change while nobody is typing.
        _library = new LibraryService(
            Report,
            note => _app.Dispatcher.BeginInvoke(() => Notify(note)),
            changed: () => _app.Dispatcher.BeginInvoke(LibraryChanged));
        var quicklinks = new QuicklinkProvider(() => _library.Current, _processes, EditQuicklink, DeleteQuicklink);
        var snippets = new SnippetProvider(() => _library.Current, paste, clipboard, _processes, clock, edit: EditSnippet, delete: DeleteSnippet);

        // What the runner reports is for the log: fixed words, a script's id made safe, an exit
        // code and a time. What a script printed goes to the screen only, through the provider:
        // into the output view, which the provider shares with the launcher's view model, or
        // into a notification. A script ends on a background thread, so the provider is given a
        // way to get back to this one.
        _scripts = new ScriptRunner(() => _scriptOptions, Report);
        var output = new OutputViewModel();
        _output = output;
        var scripts = new ScriptProvider(
            () => _library.Current,
            _scripts,
            _processes,
            output,
            NotifyResult,
            action => _app.Dispatcher.BeginInvoke(action));

        var engine = new SearchEngine(
            [new CalculatorProvider(CopyAnswer), _apps, commands, quicklinks, snippets, scripts, files],
            usage,
            (provider, error) => _log.Error($"Search provider {provider.GetType().Name} failed", error),
            new ItemMarks(_paths.FavoritesFile));
        // The emoji picker: the emoji Windows can show, pasted like a snippet, with the ones
        // picked most listed first.
        emojiView = new EmojiViewModel(() => _emojiFont.All, new EmojiActions(paste.Paste, CopyEmoji), usage.Boost);

        // The skin tone chosen in the picker is kept in settings.json, and written there when it is changed.
        emojiView.Tone = EmojiViewModel.ToneOf(SettingsDocument.Read(_paths.SettingsFile)?.Text("emojiSkinTone"));
        var picker = emojiView;
        picker.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(EmojiViewModel.Tone))
            {
                SaveSkinTone(picker.Tone);
            }
        };
        var viewModel = new LauncherViewModel(engine, usage, output, _clipboardView, emojiView) { Say = _hud.Show };
        launcherModel = viewModel;
        _viewModel = viewModel;

        // LauncherWindow.xaml looks these converters up by name, so they must exist before the window does.
        app.Resources["IconKeyConverter"] = new IconKeyConverter(_icons);
        app.Resources["EmojiPicture"] = new EmojiPictureConverter(_emojiFont);
        _launcher = new LauncherWindow(viewModel, new ClipPictureLoader((message, error) => _log.Error(message, error)));

        // Blurred glass behind the launcher, unless settings.json says "launcherGlass": false.
        // It is decided before the window is first shown and cannot change while Beacon runs.
        _launcher.UseGlass(SettingsDocument.Read(_paths.SettingsFile)?.Flag("launcherGlass", true) ?? true);
        _tray = new TrayIcon(
            open: Safely("Open Beacon", OpenLauncherFromTray),
            openClipboard: Safely("Clipboard History", OpenClipboardFromTray),
            openSettings: Safely("Settings", OpenSettings),
            reload: Safely("Reload", Reload),
            syncNow: Safely("Sync Now", SyncNow),
            quit: Quit,
            // The tray's text is made again from how sync stands when the menu is opened, so
            // that a time that is days old says its date. It is called on this thread.
            menuOpening: () => Guarded("refresh the tray text", ShowCaptureState));
        _hotkeys.Pressed += (_, _) => _launcher.Toggle();
        _clipboardHotkeys.Pressed += (_, _) => _launcher.ToggleClipboard();

        // The window in front when the launcher is asked to show is the one a snippet is pasted into.
        _launcher.Opening += (_, _) => _pasteTarget.Remember();

        viewModel.ActionFailed += (_, failure) =>
        {
            LogActionFailed(failure.ItemId, failure.Exception);
            Notify(ActionFailedNote);
        };

        app.DispatcherUnhandledException += (_, args) =>
        {
            _log.Error("Unhandled error on the UI thread", args.Exception);
            Notify("Something went wrong. Details are in the log file.");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _log.Error("Unhandled error", args.ExceptionObject as Exception);

        // Sync. Nothing of it runs on this thread: at the start of every run it reads sync.json
        // and the sync key on a background thread, and while there is no sync.json it makes no
        // connection at all. What it tells is posted to this thread. A run that brought
        // clipboard files in has the history read again, which also scrubs this machine's own
        // files of what was deleted on the other machine. It is made before capture is started
        // and before the settings are applied, because both say how things stand through it.
        // The three callbacks are given by the name of their parameter: two of them are an
        // Action, so by position a swap would still compile.
        _sync = new SyncService(
            _paths,
            SftpRemoteStore.Connector,
            _protector,
            clock,
            report: Report,
            notify: note => _app.Dispatcher.BeginInvoke(() => Notify(note)),
            statusChanged: () => _app.Dispatcher.BeginInvoke(ShowCaptureState),
            clipboardChanged: _history.Reload);

        // Before the settings are applied, which is what gives the history its folder: whether
        // capture is paused is known before there is anything that could be recorded.
        if (_capture.Start() is { } captureNote)
        {
            Notify(captureNote);
        }

        ApplySettings(firstLoad: true);
        _log.Info("Beacon started");
        StartUpdates();
        ShowWelcomeIfDue();
    }

    /// <summary>
    /// Shows the owner a sentence about something that happened before the shell existed,
    /// through the same queue as every other notification, so that it waits its turn behind
    /// what startup has already said. The sentence must be fixed words, as every notification
    /// is. For the UI thread.
    /// </summary>
    public void Tell(string note) => Notify(note);

    public void Dispose()
    {
        // From here on a change of the history that was posted to this thread and has not
        // run yet has nothing left to do.
        _closing = true;
        StopUpdates();

        // The keyboard hook of snippet auto-expand goes before everything else. When its
        // removal goes well, the hook is removed and its thread has ended when this returns, so
        // nothing more is handed to this thread from there, and nothing of auto-expand reaches
        // the paste service, the notifications or the log that are let go of below. When the
        // removal fails (the thread did not end in time) that is not so: the failure goes to
        // the log, and the hook may still be installed, with its thread running, until Beacon
        // closes. Its listener was closed before the removal was tried, so every key it reports
        // is dropped and no key is looked at from here on. Release catches whatever this
        // throws, so the scripts are stopped all the same.
        Release("snippet auto-expand", _autoExpand);

        // Scripts still running are stopped, with everything they started. None outlives Beacon,
        // so this comes next: whatever else fails below must not leave a script running.
        // Each of the others is let go of on its own, so that one that throws does not leave
        // the rest holding on to what they hold.
        Release("script runner", _scripts);
        Release("hotkeys", _hotkeys);
        Release("clipboard hotkey", _clipboardHotkeys);
        foreach (var commandHotkey in _commandHotkeys)
        {
            Release("command hotkey", commandHotkey);
        }

        // Sync goes before the clipboard history. A run that is under way is told to stop
        // between two files and is waited for, at most a few seconds; one that does not stop has
        // its connection closed under it. What it had synced is recorded, and an upload that
        // was cut off leaves only a temporary file on the server, never half a blob. So nothing
        // is written into the data folder any more while the history finishes its own work.
        // The two sync windows go first: they hold a key or a passphrase on screen.
        Guarded("close the sync windows", CloseSyncWindows);
        Release("sync", _sync);

        // The clipboard history is closed in this order, each step on its own.
        //   1. The listener, so that nothing more is told of what is copied.
        //   2. Capture is stopped: work it has waiting in the history's line, and a copy it is
        //      reading at this moment, do nothing more, and read no more of the clipboard or
        //      of a picture file. Whether capture is paused is not changed by this.
        //   3. A wait, with a limit, for the history's line of work to go idle. With capture
        //      stopped the captures that wait are over at once, so what the wait is for is a
        //      delete or a clear that the owner asked for and that had not had its turn: it
        //      is carried out, not dropped. Nothing the line does needs this thread, which
        //      stands still meanwhile: what it tells is posted here and never waited for.
        //   4. The history itself. It drops whatever still waits (and says how many pieces,
        //      which is none unless the wait ran out), waits for a piece of work that is
        //      inside the store, and lets go of its folder.
        //   5. The reader's window, last: only now is it certain that nothing reads through it.
        Release("clipboard listener", _clipboardListener);
        Guarded("stop clipboard capture", _capture.Stop);
        Guarded("wait for the clipboard history to finish its work", WaitForHistory);
        Release("clipboard history", _history);
        Release("clipboard reader", _clipboardReader);

        // A picture that is being saved is given the same time to be finished: the thread it
        // is copied on ends with Beacon, and half a copy would be left in the Pictures folder.
        Guarded("wait for the pictures that were being saved", WaitForSaves);

        Release("file index", _files);
        Release("library", _library);
        Release("clipboard", _windowsClipboard);
        Release("tray icon", _tray);
    }

    private void Release(string what, IDisposable item) => Guarded($"let go of the {what}", item.Dispose);

    // One step of closing. A step that throws is logged, in fixed words, and the steps after
    // it are still taken.
    private void Guarded(string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _log.Error($"Could not {step}", ex);
        }
    }

    // Called when Beacon closes, on this thread, after capture has been stopped. Drain only
    // waits: it takes no lock that the history's line needs, and its limit is counted on a
    // timer of the thread pool, so it comes back whatever the line is doing. When the limit
    // ran out, the history says in the log how many pieces of work it then drops.
    private void WaitForHistory()
    {
        if (!_history.Drain(CloseWait))
        {
            _log.Error("The clipboard history was still at work when Beacon closed, and the wait for it ran out");
        }
    }

    // Called when Beacon closes, on this thread. A save never throws (SavePictures), so the
    // only thing to tell is that the time ran out.
    private void WaitForSaves()
    {
        if (!Task.WaitAll(_saves.ToArray(), CloseWait))
        {
            _log.Error("A picture was still being saved when Beacon closed");
        }
    }

    // Tray callbacks arrive through WinForms, outside WPF's unhandled-exception handler.
    // Left alone, an exception there would raise a WinForms crash dialog.
    private Action Safely(string command, Action action) => () =>
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _log.Error($"Tray command failed: {command}", ex);
            Notify($"{command} failed. Details are in the log file.");
        }
    };

    private void Notify(string message) => _notes.Add(message);

    // How a script ended is something that happened, not how things stand. It is shown even
    // when the same words are on screen or waiting: two scripts that both end with "Done" are
    // both told.
    private void NotifyResult(string message) => _notes.Add(message, evenIfRepeated: true);

    // The one place a notification reaches the tray. Everything else goes through Notify, which queues it.
    private void ShowNote(string message) => _tray.Notify("Beacon", message);

    // What the file index, the library, the script runner, the clipboard history, sync, the
    // two sync windows and snippet auto-expand say about their progress and their problems.
    // Their messages carry counts and fixed words, never a name, never anything that was
    // copied or typed, and nothing of the sync server or its key.
    private void Report(string message, Exception? error)
    {
        if (error is null)
        {
            _log.Info(message);
        }
        else
        {
            _log.Error(message, error);
        }
    }

    // Runs the action once, after the delay, on the UI thread.
    private void Later(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, _app.Dispatcher) { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    // Posted rather than called: the tray menu is still closing when its item is clicked, and
    // the launcher hides itself when it loses focus. Showing it after the menu has gone keeps it open.
    private void OpenLauncherFromTray() => _app.Dispatcher.BeginInvoke(_launcher.ShowLauncher);

    // Posted, like the line above, so that the menu has gone before the launcher takes the focus.
    private void OpenClipboardFromTray() => _app.Dispatcher.BeginInvoke(_launcher.ToggleClipboard);

    private void OpenSettingsFile() => _processes.OpenPath(_paths.SettingsFile);

    // What Enter does on the answer to a sum: the answer goes on the clipboard, and a few
    // fixed words say so, because the launcher has hidden by then.
    private void CopyAnswer(string answer)
    {
        _clipboard.SetText(answer);
        _hud.Show("Copied Answer");
    }

    // Makes Beacon's windows light, dark or as Windows is: written to settings.json, where
    // the settings window shows it and the next start reads it, and used from now on. The
    // launcher has just hidden, and looks the new way when it next opens.
    private void UseAppearance(Appearance appearance)
    {
        if (SettingsDocument.Read(_paths.SettingsFile) is not { } document)
        {
            Notify("settings.json cannot be read, so Beacon's appearance was not changed.");
            return;
        }

        // Following Windows is what Beacon does without the setting, so it is taken out.
        document.Set(AppearanceOptions.SettingName, appearance == Appearance.System ? null : AppearanceOptions.Text(appearance));
        document.Write(_paths.SettingsFile);
        LauncherLook.Appearance = appearance;
        _hud.Show(appearance switch
        {
            Appearance.Dark => "Dark Appearance",
            Appearance.Light => "Light Appearance",
            _ => "Windows Appearance",
        });
    }

    // Confetti, on the fast window unless settings.json says "confettiClickThrough": true. The
    // file is asked each time, so a change needs no restart.
    private void ThrowConfetti() =>
        _confetti.Throw(SettingsDocument.Read(_paths.SettingsFile)?.Flag("confettiClickThrough", false) ?? false);

    // An emoji, on the clipboard without being pasted.
    private void CopyEmoji(string emoji) => CopyText(emoji);

    // The welcome window, the first time Beacon runs on a PC whose clipboard history has no
    // hotkey yet: it asks for the two hotkeys. Once it has been closed, whichever way, it is
    // not shown again. Nothing here may stop Beacon from starting.
    private void ShowWelcomeIfDue()
    {
        try
        {
            if (SettingsDocument.Read(_paths.SettingsFile) is not { } settings
                || !Welcome.IsDue(File.Exists(_paths.WelcomedFile), settings.Text("clipboardHotkey")))
            {
                return;
            }

            var welcome = new WelcomeWindow(_paths.SettingsFile, Safely("Reload", Reload));
            welcome.Closed += (_, _) =>
            {
                try
                {
                    File.WriteAllText(_paths.WelcomedFile, Welcome.MarkerText);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log.Error("That the welcome window was shown could not be written down, so it will be shown again", ex);
                }
            };
            welcome.Show();
            welcome.Activate();
        }
        catch (Exception ex)
        {
            _log.Error("The welcome window could not be shown", ex);
        }
    }

    // The emoji picker's skin tone, into settings.json. The default tone is what Beacon has
    // without the setting, so the setting is then taken out.
    private void SaveSkinTone(SkinTone tone)
    {
        try
        {
            if (SettingsDocument.Read(_paths.SettingsFile) is { } document)
            {
                document.Set("emojiSkinTone", tone == SkinTone.Default ? null : EmojiViewModel.ToneText(tone));
                document.Write(_paths.SettingsFile);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("The emoji skin tone could not be written to settings.json", ex);
        }
    }

    // Makes the SSH key file sync needs on this PC, or finds the one Beacon made before, and
    // shows in the launcher what to do with it: the public half to add on the server, and
    // the file to give Sync Setup. Nothing is sent anywhere, and nothing of the key is logged.
    private void ShowSyncKeyHelp()
    {
        if (_output is not { } output)
        {
            return;
        }

        object ticket = output.Open("Create SSH Key for Sync");
        try
        {
            var key = SshKeyMaker.Make(SyncKeyHelp.KeyFile(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
            output.Complete(ticket, key.Created ? "Key created" : "Key found", SyncKeyHelp.Steps(key.PublicKey, key.File, key.Created));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _log.Error("An SSH key for sync could not be made or read", ex);
            output.Complete(ticket, "No key", SyncKeyHelp.Failed);
        }
    }

    // A text, on the clipboard without being pasted: a file's path, an emoji.
    private void CopyText(string text)
    {
        _clipboard.SetText(text);
        _hud.Show("Copied to Clipboard");
    }

    // The form that makes a snippet, with some text in it already when a clipboard entry is
    // being saved as one. Saving writes a file into the snippets folder, which the library
    // watches: the snippet is in the launcher a moment later.
    private void OpenSnippetForm(string text)
    {
        var data = DataInUse();
        var form = LibraryFormWindow.ForSnippet(text, filled =>
        {
            var saved = LibraryWriter.SaveSnippet(data.SnippetsDir, filled.NameText, filled.KeywordText, filled.BodyText);
            if (saved.Saved)
            {
                _hud.Show("Snippet Saved");
            }

            return saved;
        });
        form.Show();
        form.Activate();
    }

    // The form that makes a quicklink. Saving adds it to quicklinks.json.
    private void OpenQuicklinkForm()
    {
        var data = DataInUse();
        var form = LibraryFormWindow.ForQuicklink(filled =>
        {
            var saved = LibraryWriter.AddQuicklink(data.QuicklinksFile, filled.NameText, filled.LinkText, filled.KeywordText);
            if (saved.Saved)
            {
                _hud.Show("Quicklink Saved");
            }

            return saved;
        });
        form.Show();
        form.Activate();
    }

    // The same form with a snippet that is there already in it. Saving writes the snippet's
    // own file anew, so it stays the item it was: its uses, its alias and its place among
    // the favorites stay with it.
    private void EditSnippet(Snippet snippet)
    {
        var data = DataInUse();
        var form = LibraryFormWindow.ForSnippetEdit(snippet, filled =>
        {
            var saved = LibraryWriter.UpdateSnippet(data.SnippetsDir, snippet, filled.NameText, filled.KeywordText, filled.BodyText);
            if (saved.Saved)
            {
                _hud.Show("Snippet Saved");
            }

            return saved;
        });
        form.Show();
        form.Activate();
    }

    // A snippet's file goes to the Recycle Bin, from where it can be had back.
    private void DeleteSnippet(Snippet snippet)
    {
        var removed = LibraryWriter.RemoveSnippet(DataInUse().SnippetsDir, snippet, RecycleBin.Send);
        if (removed.Saved)
        {
            _hud.Show("Snippet Moved to Recycle Bin");
        }
        else
        {
            Notify(removed.Note);
        }
    }

    private void EditQuicklink(Quicklink quicklink)
    {
        var data = DataInUse();
        var form = LibraryFormWindow.ForQuicklinkEdit(quicklink, filled =>
        {
            var saved = LibraryWriter.UpdateQuicklink(data.QuicklinksFile, quicklink.Name, filled.NameText, filled.LinkText, filled.KeywordText);
            if (saved.Saved)
            {
                _hud.Show("Quicklink Saved");
            }

            return saved;
        });
        form.Show();
        form.Activate();
    }

    private void DeleteQuicklink(Quicklink quicklink)
    {
        var removed = LibraryWriter.RemoveQuicklink(DataInUse().QuicklinksFile, quicklink.Name);
        if (removed.Saved)
        {
            _hud.Show("Quicklink Deleted");
        }
        else
        {
            Notify(removed.Note);
        }
    }

    // An entry of the clipboard history, on the clipboard again without being pasted.
    private void CopyEntry(ClipPaster paster, ClipEntry entry)
    {
        if (paster.Copy(entry))
        {
            _hud.Show("Copied to Clipboard");
        }
    }

    // The command hotkeys are given up before the launcher's and the clipboard history's are
    // registered, so that a key can go from a command to one of those in one Reload.
    private void ReleaseCommandHotkeys()
    {
        foreach (var held in _commandHotkeys)
        {
            held.Dispose();
        }

        _commandHotkeys.Clear();
    }

    // Registers the hotkeys settings.json gives to commands, after the launcher's and the
    // clipboard history's. What cannot be used is left out and said: counts, and the
    // hotkeys Windows refused as the owner wrote them.
    private void RegisterCommandHotkeys(System.Text.Json.JsonElement? setting, List<string> notes)
    {
        var taken = new List<Hotkey>();
        if (_hotkeys.Current is { } launcherHotkey)
        {
            taken.Add(launcherHotkey);
        }

        if (_clipboardHotkeys.Current is { } historyHotkey)
        {
            taken.Add(historyHotkey);
        }

        var options = CommandHotkeyOptions.From(
            setting,
            id => _builtIn.TryGetValue(id, out var command) ? CommandHotkeyOptions.CanRun(command) : null,
            taken);
        var refused = new List<string>();
        foreach (var one in options.Hotkeys)
        {
            var manager = new HotkeyManager();
            if (!manager.Register(one.Hotkey))
            {
                manager.Dispose();
                refused.Add(one.Text);
                continue;
            }

            var command = _builtIn[one.CommandId];
            manager.Pressed += (_, _) => RunByHotkey(command);
            _commandHotkeys.Add(manager);
        }

        if (options.Notes.Count > 0)
        {
            // The sentences are fixed words and counts. They do not quote the file.
            _log.Error("Some command hotkeys in settings.json could not be used");
            notes.AddRange(options.Notes);
        }

        if (refused.Count > 0)
        {
            _log.Error($"Command hotkeys that could not be registered: {refused.Count}");
            notes.Add($"These command hotkeys are already used by another app: {string.Join(", ", refused)}.");
        }
    }

    // What a command's hotkey does: the command's own action, as Enter on it does.
    private void RunByHotkey(BuiltInCommand command)
    {
        try
        {
            // The window in front now is the one a window command moves. With the launcher
            // open that would be the launcher's own, and the window that was in front when
            // it opened stays the one.
            if (command.KeepOpen)
            {
                // A command that shows its view in the launcher: the launcher comes first,
                // and remembers the window in front as it does whenever it opens.
                if (!_launcher.IsVisible)
                {
                    _launcher.ShowLauncher();
                }
            }
            else if (!_launcher.IsVisible)
            {
                _pasteTarget.Remember();
            }

            command.Run?.Invoke();
        }
        catch (Exception ex)
        {
            LogActionFailed("command:" + command.Id, ex);
            Notify(ActionFailedNote);
        }
    }

    // The settings window. There is one at a time: asked for again while it is open, it comes
    // to the front. What it saves is read by Reload, the same as an edit of the file by hand.
    private void OpenSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        // The commands that can have a hotkey of their own.
        var choices = CommandHotkeyOptions.Choices(_builtIn.Values);
        _settingsWindow = new SettingsWindow(
            _paths.SettingsFile,
            new SettingsActions(
                Reload: Safely("Reload", Reload),
                OpenClipboard: Safely("Clipboard History", OpenClipboardFromTray),
                NewSnippet: Safely("New Snippet", NewSnippet),
                OpenLibraryFolder: Safely("Open Library Folder", OpenLibraryFolder),
                OpenSyncSetup: Safely("Sync Setup", OpenSyncSetup),
                SyncNow: Safely("Sync Now", SyncNow),
                OpenSettingsFile: Safely("Open Settings File", OpenSettingsFile),
                OpenLogFolder: Safely("Open Log Folder", OpenLogFolder)),
            typeof(BeaconShell).Assembly.GetName().Version?.ToString(3) ?? "",
            choices);
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OpenLogFolder()
    {
        if (Path.GetDirectoryName(_paths.LogFile) is { Length: > 0 } folder)
        {
            _processes.OpenPath(folder);
        }
    }

    // Called on this thread whenever the clipboard history has changed: after a load, after
    // something was copied, deleted or cleared, and when the folder was taken or lost.
    private void HistoryChanged()
    {
        // The history posts this to the thread, so one can still be waiting when Beacon closes.
        if (_closing)
        {
            return;
        }

        _clipboardView.Refresh();

        // The pictures there are now: the text of one that has gone is dropped, and one that
        // is new is read in the background.
        _pictureText?.Follow(_history.Current.Concat(_clipPins.All));

        // Listening follows whether this copy of Beacon may write the history at all. Whether
        // it may is asked before and after: the history can change that on its own thread at
        // any moment, and only when both answers are yes did the refresh certainly want to listen.
        bool couldWrite = _history.IsWriter;
        _capture.Refresh();
        bool canWrite = _history.IsWriter;

        // Capture is not paused, this copy may write, and Beacon is still not listening: the
        // listener could not be started, which the log says with its reason. Until now only
        // the tray icon's text told the owner. Said once for each run of Beacon; every later
        // change of the history tries the listener again.
        if (couldWrite && canWrite && !_capture.Paused && !_capture.Listening && !_toldListenerFailed)
        {
            _toldListenerFailed = true;
            Notify(ClipCapture.ListenerFailedNote);
        }

        // Said once for as long as it stays so, not after every change.
        bool cannotWrite = !canWrite;
        if (cannotWrite && !_toldHistoryCannotBeWritten && !_capture.Paused)
        {
            Notify(ClipCapture.NotRecordingNote);
        }

        _toldHistoryCannotBeWritten = cannotWrite;
    }

    // The text of more pictures has been read: a history that is open and filtered is looked
    // through again, so that a picture with the word in it joins the list.
    private void PictureTextChanged()
    {
        if (!_closing)
        {
            _clipboardView.Refresh();
        }
    }

    // Reading pictures is switched off: what was read before is not kept either.
    private void ForgetPictureText()
    {
        try
        {
            File.Delete(_paths.ClipboardPictureTextFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("What was read in the pictures of the clipboard history could not be removed", ex);
        }
    }

    // The tray icon says whether what is copied is being recorded and, when sync is set up,
    // how sync stands: when it last went through, or why it does not run. Called on this
    // thread, when capture or sync has changed, and not before the tray icon exists: capture
    // first says how it stands when it is started, which is at the end of the constructor. The
    // checks for null stay for a constructor whose order is one day changed.
    private void ShowCaptureState()
    {
        if (_closing || _tray is null || _capture is null)
        {
            return;
        }

        _tray.SetText(_sync is null
            ? _capture.Tooltip
            : _sync.Status.WithCapture(_capture.Tooltip, _clock.UtcNow, LocalZone()));
    }

    // The zone the "last synced" time is given in. Beacon runs for weeks and the zone can
    // change in that time; .NET keeps the one it first read, so what it kept is cleared first.
    private static TimeZoneInfo LocalZone()
    {
        TimeZoneInfo.ClearCachedData();
        return TimeZoneInfo.Local;
    }

    // Called on this thread when the library has been loaded again.
    private void LibraryChanged()
    {
        if (!_closing)
        {
            _viewModel.Refresh();
        }

        // The keywords that snippet auto-expand watches for are those of the library as it now
        // is: also after sync brought a snippet in, with nobody typing. While auto-expand is
        // off the expander looks at none of them.
        if (!_closing)
        {
            _autoExpand.UseLibrary(_library.Current.Snippets);
        }
    }

    private void SyncNow() => _sync.SyncNow();

    // What the owner answers a paused sync with, when many files that were synced are missing
    // from the data folder. Each starts one sync run and returns at once, as Sync Now does, and
    // sync says how the run went. Confirm Sync Deletions: he deleted, moved or renamed the
    // files, and that one run may send their deletions to the other machine. Restore Missing
    // Files: he did not, and that one run fetches them back from the server. There is no
    // window. Restore Missing Files is one key press. Confirm Sync Deletions asks in the
    // launcher's input line for the typed word yes before this is called, with the count of
    // the deletions in its prompt, and starts nothing while sync is not paused (SyncCommands);
    // sync itself refuses it then as well.
    private void ConfirmSyncDeletions() => _sync.ConfirmDeletions();

    private void RestoreMissingFiles() => _sync.RestoreMissing();

    // Whether sync is paused and how many deletions are held, for that prompt. It reads what
    // sync remembers, and nothing from the disk.
    private SyncPause PausedSync() => _sync.Pause;

    // What the Sync Setup command does. The launcher has hidden itself by now. The view model
    // first reads sync.json and asks Windows whether this machine has a sync key, which is done
    // away from this thread; the window is shown when that is done. One window at a time.
    private void OpenSyncSetup()
    {
        if (_setupWindow is not null)
        {
            _setupWindow.Activate();
            return;
        }

        if (_setupOpening)
        {
            return;
        }

        _setupOpening = true;
        var viewModel = new SyncSetupViewModel(
            _paths,
            SftpRemoteStore.Connector,
            _protector,
            _clipboard,
            work => Task.Run(work),
            action => _app.Dispatcher.BeginInvoke(action),
            _sync.SetupChanged,
            Report);
        Task.Run(() =>
        {
            try
            {
                viewModel.Load();
            }
            catch (Exception ex)
            {
                _log.Error("Sync Setup could not read what is set up now", ex);
            }

            _app.Dispatcher.BeginInvoke(() =>
            {
                _setupOpening = false;
                if (_closing)
                {
                    return;
                }

                _setupWindow = new SyncSetupWindow(viewModel, Report);
                _setupWindow.Closed += (_, _) => _setupWindow = null;
                try
                {
                    _setupWindow.Show();
                }
                catch (Exception ex)
                {
                    // A window that could not be shown must not stay in the field: the command
                    // would bring it forward and open nothing until Beacon was started again.
                    _setupWindow = null;
                    viewModel.Clear();
                    _log.Error("The Sync Setup window could not be shown", ex);
                    Notify(ActionFailedNote);
                    return;
                }

                _setupWindow.Activate();
            });
        });
    }

    // What the Show Sync Key command does: the key is read and unprotected away from this
    // thread, and then shown. The window lets go of it when it closes.
    private void ShowSyncKey()
    {
        if (_keyWindow is not null)
        {
            _keyWindow.Activate();
            return;
        }

        if (_keyOpening)
        {
            return;
        }

        _keyOpening = true;
        var viewModel = new SyncKeyViewModel(new KeyStore(_paths.KeyFile, _paths.PassphraseFile, _protector), _clipboard, Report);
        Task.Run(() =>
        {
            try
            {
                viewModel.Load();
            }
            catch (Exception ex)
            {
                _log.Error("The sync key could not be read to be shown", ex);
            }

            _app.Dispatcher.BeginInvoke(() =>
            {
                _keyOpening = false;
                if (_closing)
                {
                    viewModel.Clear();
                    return;
                }

                _keyWindow = new SyncKeyWindow(viewModel, Report);
                _keyWindow.Closed += (_, _) => _keyWindow = null;
                try
                {
                    _keyWindow.Show();
                }
                catch (Exception ex)
                {
                    // As for the Sync Setup window, and the key the view model holds is let go of.
                    _keyWindow = null;
                    viewModel.Clear();
                    _log.Error("The sync key window could not be shown", ex);
                    Notify(ActionFailedNote);
                    return;
                }

                _keyWindow.Activate();
            });
        });
    }

    // Called when Beacon closes, on this thread. Closing a window makes its view model let go
    // of what it holds. Each is closed under its own guard: one that throws as it closes must
    // not leave the other open, and the window that shows the key is the one that matters.
    private void CloseSyncWindows()
    {
        Guarded("close the Sync Setup window", () => _setupWindow?.Close());
        Guarded("close the sync key window", () => _keyWindow?.Close());
    }

    // What Ctrl+S does with an entry of the clipboard history: saves a copy of its pictures.
    // That can be 200 MB to copy, so it is done on a pool thread and this returns at once; the
    // window has been hidden by then. Two saves can run at once: each takes the first names
    // that are free. Called on this thread.
    //
    // Nothing of a save touches this thread but by posting to it: what the recovery tells the
    // owner, and the sentence for a save that failed, which is the one any failed action gets,
    // with the same line in the log. So a save never throws, and closing can wait for it.
    private void SavePictures(ClipRecovery recovery, ClipEntry entry)
    {
        _saves.RemoveAll(save => save.IsCompleted);
        _saves.Add(Task.Run(() =>
        {
            try
            {
                recovery.Save(entry);
            }
            catch (Exception ex)
            {
                try
                {
                    LogActionFailed(entry.ItemId, ex);
                    _app.Dispatcher.BeginInvoke(() => Notify(ActionFailedNote));
                }
                catch (Exception)
                {
                    // The owner could not be told. Nobody is above this on a pool thread.
                }
            }
        }));
    }

    // An action that failed is logged by the type of what was thrown, never by what it says,
    // and with an item id that names nothing of the owner's. The log can be written from any thread.
    private void LogActionFailed(string itemId, Exception error) =>
        _log.Error($"Action failed for item {LogSafe.ItemId(itemId)}", error);

    // Where a recovered picture is saved. Windows can be without a Pictures folder; a copy must
    // then not be saved to wherever Beacon happened to be started from.
    private static string PicturesFolder()
    {
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return Path.IsPathFullyQualified(folder)
            ? folder
            : throw new InvalidOperationException("Windows did not say where the Pictures folder is.");
    }

    private void NewSnippet() => OpenSnippetForm("");

    private void OpenSnippetsFolder()
    {
        var data = DataInUse();
        LibraryFiles.EnsureFolders(data);
        _processes.OpenPath(data.SnippetsDir);
    }

    private void EditQuicklinks() => _processes.OpenPath(LibraryFiles.EnsureQuicklinksFile(DataInUse()));

    private void OpenLibraryFolder()
    {
        var data = DataInUse();
        LibraryFiles.EnsureFolders(data);
        _processes.OpenPath(data.LibraryDir);
    }

    // Where the data folder is comes from settings.json. Until that file has been read once
    // there is no library to create anything in, and the startup notification has said so.
    private DataPaths DataInUse() =>
        _data ?? throw new InvalidOperationException("The library is not in use because settings.json has not been read.");

    private void Quit() => _app.Shutdown();

    private void Reload() => ApplySettings(firstLoad: false);

    private void ApplySettings(bool firstLoad)
    {
        var loaded = SettingsStore.Load(_paths.SettingsFile);
        var decision = SettingsDecision.From(loaded, firstLoad);
        var notes = new List<string>(decision.Notes);

        if (loaded.Error is not null)
        {
            _log.Error("settings.json could not be loaded", loaded.Cause);

            // Nothing is read from the file then, so the switch is as it was and the hook is
            // renewed at the end of this method: it is still installed. At the first load the
            // switch is off and there is nothing to say. The sentence follows the notes the
            // decision brought, and is fixed words: nothing of the file is quoted.
            if (!firstLoad && _autoExpandOn)
            {
                _log.Error("settings.json could not be loaded while snippet auto-expand is on: it stays on");
                notes.Add(AutoExpandStaysOnNote);
            }
        }

        // Whether snippet auto-expand is on. The settings file has been read, and a file that
        // could not be read has been reported above; now, before anything else is applied, the
        // switch is set and a keyboard hook that is no longer wanted is taken out. A
        // settings.json that cannot be read says nothing about the switch: at startup it then
        // stays off, and on Reload it stays as the owner had it. When it goes from on to off the
        // hook is taken out here and now, so whatever below ends the method early cannot leave
        // a hook installed under a switch that says off. Switching on waits for the end of this
        // method, which puts the hook in.
        if (decision.AutoExpand is { } autoExpandOptions)
        {
            bool wasOn = _autoExpandOn;
            _autoExpandOn = autoExpandOptions.Enabled;
            if (wasOn && !_autoExpandOn)
            {
                _autoExpand.Configure(false, []);
            }

            if (autoExpandOptions.Notes.Count > 0)
            {
                // The sentence is fixed words. It does not quote the file.
                _log.Error("The snippetAutoExpand setting in settings.json could not be used");
                notes.AddRange(autoExpandOptions.Notes);
            }
        }

        // Light or dark. Every window of Beacon asks when it is shown, so the launcher has
        // it from the next time it opens. A settings.json that cannot be read says nothing
        // about it: the windows stay as they were.
        if (loaded.Error is null)
        {
            var look = AppearanceOptions.From(loaded.Settings.Appearance);
            LauncherLook.Appearance = look.Value;
            if (look.Notes.Count > 0)
            {
                // The sentence is fixed words. It does not quote the file.
                _log.Error("The appearance setting in settings.json could not be used");
                notes.AddRange(look.Notes);
            }
        }

        if (decision.HotkeyInvalid)
        {
            _log.Error("The hotkey in settings.json is not valid");
        }

        // The clipboard history's hotkey is given up first, when its setting has been taken out
        // or names another key than the one it has. Windows refuses a key that is registered
        // already, to Beacon's other hotkey as to another program (the page for RegisterHotKey:
        // it "fails if the keystrokes specified for the hot key have already been registered
        // for another hot key"). Without this the launcher's hotkey could not be moved onto the
        // key the clipboard history held in one Reload, nor the two exchanged. The key that
        // was given up is kept: straight after the launcher's hotkey, when Windows refuses the
        // new one, the clipboard history falls back to it, as the launcher's hotkey falls back
        // to its own.
        //
        // The three steps stand together, with nothing between them but the launcher's own
        // notes: give up, the launcher's hotkey, the clipboard history's hotkey. Whatever
        // fails further down in this method, a Reload does not end with the clipboard history's
        // hotkey given up and not registered again.
        //
        // The commands' hotkeys are given up before all of that and registered after it, so
        // that the launcher or the clipboard history can take a key a command had. A
        // settings.json that cannot be read says nothing about them: they stay as they are.
        if (loaded.Error is null)
        {
            ReleaseCommandHotkeys();
        }

        Hotkey? clipboardHotkeyBefore = null;
        if (decision.Clipboard is { HotkeyInvalid: false } clipboardSetting
            && clipboardSetting.Hotkey != _clipboardHotkeys.Current)
        {
            clipboardHotkeyBefore = _clipboardHotkeys.Current;
            _clipboardHotkeys.Clear();
        }

        if (decision.Hotkey is { } hotkey && !_hotkeys.Register(hotkey))
        {
            _log.Error($"Hotkey {decision.HotkeyText} could not be registered");
            notes.Add($"{decision.HotkeyText} is already used by another app.");
            notes.Add(_hotkeys.HasHotkey
                ? "Keeping the hotkey that was working."
                : "Open Beacon from the tray icon, or change \"hotkey\" in settings.json and run Reload.");
        }

        // The clipboard history's hotkey is registered like the launcher's, and straight after
        // it: a hotkey that cannot be used leaves the one that was working, which is the one
        // given up above when the setting named another key. That key can have gone to the
        // launcher meanwhile, when settings.json moved the launcher's hotkey onto it; the
        // clipboard history is then left without one. What the owner is told about it is added
        // further down, with the other notes about the clipboard settings, where it always was.
        // A setting that has been taken out was given up above and needs nothing more.
        bool clipboardHotkeyRefused = decision.Clipboard?.Hotkey is { } clipboardHotkey
            && !_clipboardHotkeys.Register(clipboardHotkey, clipboardHotkeyBefore);
        if (clipboardHotkeyRefused)
        {
            _log.Error("The clipboard hotkey could not be registered");
        }

        if (loaded.Error is null)
        {
            RegisterCommandHotkeys(loaded.Settings.CommandHotkeys, notes);
        }

        if (decision.StartWithWindows is { } startWithWindows)
        {
            try
            {
                StartupRegistration.Apply(startWithWindows);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
            {
                // Group policy can deny the Run key. That must not stop the launcher from starting.
                _log.Error("Could not update the start-with-Windows entry", ex);
            }
        }

        // A settings.json that cannot be read gives no folders. Reload still scans everything
        // again, with the folders in use. At startup there are none, so nothing is indexed
        // until the file is fixed.
        var fileOptions = decision.Files ?? _fileOptionsInUse;
        _fileOptionsInUse = fileOptions;
        if (fileOptions is not null)
        {
            // Starts a fresh scan in the background and returns at once.
            var unusable = fileOptions.Rejected.Concat(_files.Configure(fileOptions)).ToList();
            if (unusable.Count > 0)
            {
                // Folder paths are shown on screen but only counted in the log.
                _log.Error($"Indexed folders that could not be used: {unusable.Count}");
                notes.Add($"These indexed folders could not be used: {string.Join(", ", unusable)}.");
            }
        }

        // How scripts are run comes from settings.json as well. When the file cannot be read,
        // scripts go on being run as before. A script already running keeps the settings it
        // was started with.
        if (decision.Scripts is { } scriptOptions)
        {
            _scriptOptions = scriptOptions;
            if (scriptOptions.Notes.Count > 0)
            {
                // The sentences are fixed words and counts. They do not quote the file.
                _log.Error("Some script settings in settings.json could not be used");
                notes.AddRange(scriptOptions.Notes);
            }
        }

        // The clipboard history's settings are kept in the same way. Its hotkey was registered
        // above, straight after the launcher's; only what the owner is told about a hotkey that
        // Windows refused is added here, after the other notes about the clipboard settings.
        if (decision.Clipboard is { } clipboardOptions)
        {
            _clipboardOptions = clipboardOptions;

            ReadUpdateSetting();

            // A number from 0 to 5000; anything else, or nothing, is half a second.
            int delay = SettingsDocument.Read(_paths.SettingsFile)?.Number("clipboardReadDelayMilliseconds", -1) ?? -1;
            _clipboardReadDelay = delay is >= 0 and <= 5000 ? delay : (int)ClipCapture.DefaultSettle.TotalMilliseconds;
            if (clipboardOptions.Notes.Count > 0)
            {
                // The sentences are fixed words and counts, or quote the hotkey as written.
                _log.Error("Some clipboard settings in settings.json could not be used");
                notes.AddRange(clipboardOptions.Notes);
            }

            if (clipboardHotkeyRefused)
            {
                notes.Add($"{clipboardOptions.HotkeyText} is already used by another app.");
                notes.Add(_clipboardHotkeys.HasHotkey
                    ? "Keeping the clipboard hotkey that was working."
                    : "Clipboard History can still be run from the launcher.");
            }
        }

        // How often sync runs is the one sync setting in settings.json. Where it connects to is
        // in sync.json, which sync reads for itself at every run.
        if (decision.Sync is { } syncOptions)
        {
            _syncOptions = syncOptions;
            if (syncOptions.Notes.Count > 0)
            {
                // The sentence is fixed words and numbers. It does not quote the file.
                _log.Error("The sync setting in settings.json could not be used");
                notes.AddRange(syncOptions.Notes);
            }
        }

        // The data folder is treated the same way: a settings.json that cannot be read does not
        // say where it is. Reload still loads the library again, from the folder in use. At
        // startup there is none, so no library is loaded until the file is fixed.
        if (decision.DataDir?.Rejected is { } rejected)
        {
            // What was written is shown on screen but not logged. It is refused when it is not a
            // full path, and when it is the root of a drive or of a share, which sync never uses.
            _log.Error("The dataDir setting is not a full path to a folder, so the default data folder is used");
            notes.Add($"\"dataDir\" must be a full path to a folder, not the root of a drive or of a share. The default data folder is used instead of: {rejected}.");
        }

        var dataDir = decision.DataDir ?? _dataDirInUse;
        _dataDirInUse = dataDir;
        if (dataDir is not null)
        {
            // Creates the library folders if they are missing, starts a fresh load in the
            // background and returns at once.
            _data = _paths.Data(dataDir);
            _library.Configure(_data);

            // Takes this machine's clipboard folder and reads every machine's history in the
            // background. Called again by Reload, with the same folder or another.
            _history.Configure(_data.ClipboardDir, _clipboardOptions);

            // Sync works on the same data folder. Its first run begins a few seconds from now,
            // on a background thread; Reload also lets a sync that had stopped try once more.
            // With a settings.json that cannot be read there is no data folder, and no sync.
            _sync.Configure(_data, _clipboardOptions, _syncOptions);
        }

        RefreshAppsInBackground();

        if (notes.Count > 0)
        {
            // One notification: a second balloon would replace the first before it could be read.
            Notify(string.Join(" ", notes));
        }

        // Snippet auto-expand, last of all. Switching it ON is done here, and nowhere else: the
        // hook is installed, or taken out and put in again, with the keywords of the library as
        // it is loaded now; the load started above brings its own keywords through
        // LibraryChanged when it is done. Off: the log says that no hook is installed. A Reload
        // that switched it off has taken the hook out already, at the switch above, and the log
        // says so a second time here; one with the setting off all along says it once. It comes
        // last because nothing above depends on it. It waits a moment for the hook's thread to
        // start or to end, which holds up this thread and no key press.
        _autoExpand.Configure(_autoExpandOn, _library.Current.Snippets);
    }

    private void RefreshAppsInBackground()
    {
        // The shell COM objects used to list apps and load icons need a single-threaded apartment.
        var thread = new Thread(() =>
        {
            try
            {
                var apps = _apps.Refresh();
                _icons.Preload(apps.Select(app => app.Id));
            }
            catch (Exception ex)
            {
                _log.Error("Could not load the list of installed apps", ex);
            }
        })
        {
            IsBackground = true,
            Name = "Beacon app catalog",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
}
