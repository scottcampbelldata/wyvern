using Beacon.Core.Settings;

namespace Beacon.Core.Library;

/// <summary>
/// Reads the library folder. <see cref="LibraryLoader.Load"/> is the one Beacon uses; a test
/// passes its own to decide exactly when a load finishes.
/// </summary>
public delegate LibrarySnapshot LibraryLoad(DataPaths paths);

/// <summary>
/// Keeps what the launcher knows of the library in step with the folder on disk: loads it in
/// the background, then watches the folder and loads again once changes have stopped arriving.
/// </summary>
/// <remarks>
/// Search providers read <see cref="Current"/>, which is replaced in one step when a load
/// finishes, so a search sees either the old library or the new one and never a mixture.
/// An editor saves a file in several steps, each of which is reported as a change. Loading on
/// the first of them could read a half-written file, so every change restarts a short wait and
/// the load happens when the wait runs out.
/// </remarks>
public sealed class LibraryService : IDisposable
{
    /// <summary>How long changes must have stopped arriving before the library is loaded again.</summary>
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(500);

    // How many times a watcher that has ended is replaced for one configuration. A folder that
    // is deleted and made again once or twice is ordinary; one that keeps failing is not.
    private const int MaxWatcherReplacements = 3;

    // How many loads in a row that found a file they could not open are followed by another
    // try without anything having changed on disk.
    private const int MaxUnreadableTries = 3;

    private readonly Action<string, Exception?> _report;
    private readonly Action<string> _notify;
    private readonly Action _changed;
    private readonly TimeProvider _time;
    private readonly LibraryLoad _load;
    private readonly Lock _gate = new();
    private LibrarySnapshot _current = LibrarySnapshot.Empty;
    private HashSet<string> _badFilesTold = new(StringComparer.OrdinalIgnoreCase);
    private DataPaths? _paths;
    private FileSystemWatcher? _watcher;
    private ITimer? _quiet;
    private TaskCompletionSource? _idle;
    private int _configuration;
    private int _watcherReplacements;
    private int _unreadableTries;
    private bool _loadWanted;
    private bool _loading;
    private bool _disposed;

    /// <param name="report">
    /// Receives progress and problems for the log. The text holds counts and fixed words, never
    /// a name; the exception, when there is one, is logged by type only. A callback that
    /// throws is ignored.
    /// </param>
    /// <param name="notify">
    /// Receives a sentence for a notification. It can name library files, so it must never be
    /// logged. It can be called on any thread: a background one, or the caller's own inside
    /// <see cref="Configure"/>. A callback that throws is ignored.
    /// </param>
    /// <param name="time">Supplies the timer for the quiet period. Left out everywhere except in tests.</param>
    /// <param name="load">How the folder is read. Left out everywhere except in tests.</param>
    /// <param name="changed">
    /// Called on a background thread each time a load has been put into use, so that results
    /// which are on screen can be made again from the library as it now is. A callback that
    /// throws is ignored. Never called for a load that was thrown away.
    /// </param>
    public LibraryService(
        Action<string, Exception?>? report = null,
        Action<string>? notify = null,
        TimeProvider? time = null,
        LibraryLoad? load = null,
        Action? changed = null)
    {
        _report = report ?? ((_, _) => { });
        _notify = notify ?? (_ => { });
        _changed = changed ?? (() => { });
        _time = time ?? TimeProvider.System;
        _load = load ?? LibraryLoader.Load;
    }

    /// <summary>What the last finished load found. Empty until the first one finishes. Safe to read from any thread.</summary>
    public LibrarySnapshot Current => Volatile.Read(ref _current);

    /// <summary>Completes when no load is running or waiting to run.</summary>
    public Task Loading
    {
        get
        {
            lock (_gate)
            {
                return _idle?.Task ?? Task.CompletedTask;
            }
        }
    }

    /// <summary>
    /// Starts using the library in this data folder: creates its folders when they are missing,
    /// watches it, and loads it in the background. Returns at once. Calling it again, with the
    /// same folder or another, loads afresh; this is what the Reload command does.
    /// </summary>
    public void Configure(DataPaths paths)
    {
        Exception? notCreated = null;
        Exception? notWatched = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopCore();
            int configuration = _configuration;
            _paths = paths;
            _watcherReplacements = 0;
            _unreadableTries = 0;
            notCreated = TryCreateFolders(paths);

            if (notCreated is null)
            {
                _quiet = _time.CreateTimer(_ => OnQuiet(configuration), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _watcher = TryWatch(paths.LibraryDir, configuration, out notWatched);
            }

            // Loaded even when the folder could not be made: the load then finds nothing, which
            // replaces whatever was loaded from a folder used before.
            RequestLoadCore();
        }

        if (notCreated is not null)
        {
            Report("The library folder could not be created", notCreated);
            Notify("The library folder could not be created. Details are in the log file.");
        }

        if (notWatched is not null)
        {
            // The library is still loaded; it just will not notice later edits until Reload.
            Report("The library folder could not be watched for changes", notWatched);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                StopCore();
                _paths = null;
                _loadWanted = false;
            }
        }
    }

    // Called with the lock held. Everything started for the configuration that is ending (its
    // watcher, its timer, a load still running) carries that configuration's number, and is
    // ignored once the number has moved on.
    private void StopCore()
    {
        _configuration++;
        StopWatcherCore();

        // Each step is guarded so that one that fails does not skip the others, and so that
        // Configure or Dispose, which can be the app shutting down, does not throw to its caller.
        // There is nothing useful to say about a timer that would not stop.
        var quiet = _quiet;
        _quiet = null;
        try
        {
            quiet?.Dispose();
        }
        catch (Exception)
        {
            // Left to be cleaned up with the process.
        }
    }

    // Called with the lock held. The watcher is let go of first, so that the state is right
    // even if stopping it fails.
    private void StopWatcherCore()
    {
        var watcher = _watcher;
        _watcher = null;
        if (watcher is null)
        {
            return;
        }

        try
        {
            watcher.EnableRaisingEvents = false;
        }
        catch (Exception)
        {
            // Disposing it below stops it all the same.
        }

        try
        {
            watcher.Dispose();
        }
        catch (Exception)
        {
            // Left to be cleaned up with the process.
        }
    }

    /// <summary>Makes the library folders when they are missing, whether they never existed or something has removed them.</summary>
    /// <returns>Null when the folders exist; otherwise why they could not be made.</returns>
    private static Exception? TryCreateFolders(DataPaths paths)
    {
        try
        {
            LibraryFiles.EnsureFolders(paths);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return ex;
        }
    }

    /// <returns>Null when the folder cannot be watched; <paramref name="error"/> then says why.</returns>
    private FileSystemWatcher? TryWatch(string folder, int configuration, out Exception? error)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Changed += (_, _) => OnChange(configuration);
            watcher.Created += (_, _) => OnChange(configuration);
            watcher.Deleted += (_, _) => OnChange(configuration);
            watcher.Renamed += (_, _) => OnChange(configuration);
            watcher.Error += (sender, e) => OnWatcherError(sender, configuration, e.GetException());
            watcher.EnableRaisingEvents = true;
            error = null;
            return watcher;
        }
        catch (Exception ex)
        {
            watcher?.Dispose();
            error = ex;
            return null;
        }
    }

    // Called on a pool thread by the watcher, once for every step of a save. It only restarts
    // the wait. Which file changed is not looked at: the whole library is small and is read again.
    private void OnChange(int configuration)
    {
        try
        {
            lock (_gate)
            {
                if (configuration == _configuration)
                {
                    _quiet?.Change(QuietPeriod, Timeout.InfiniteTimeSpan);
                }
            }
        }
        catch (Exception ex)
        {
            // Nobody is above this on a pool thread: an escaped exception would end the app.
            Report("A change to the library could not be acted on", ex);
        }
    }

    // Called on a pool thread by the watcher when it cannot go on as it was.
    private void OnWatcherError(object? sender, int configuration, Exception error)
    {
        try
        {
            Report("The library folder watcher reported an error", error);

            if (error is InternalBufferOverflowException)
            {
                // Too many changes arrived at once and some were lost. The watcher carries on,
                // so all that is needed is to read the folder again.
                OnChange(configuration);
            }
            else
            {
                // Any other error ends the watcher. This is what happens when the folder it
                // watches is deleted, by the owner or by a tool that syncs the folder. Without a
                // new watcher no later change would ever be noticed, and nothing would say so.
                ReplaceWatcher(sender, configuration);
            }
        }
        catch (Exception ex)
        {
            // Nobody is above this on a pool thread: an escaped exception would end the app.
            Report("A change to the library could not be acted on", ex);
        }
    }

    // Puts a new watcher in the place of one that has ended. Each replacement is tried once: if
    // the folders cannot be made or the new watcher cannot be started, that is reported and the
    // library is no longer followed until Reload, rather than trying again and again. A watcher
    // that starts well and then fails again straight away is bounded too: after
    // MaxWatcherReplacements replacements for one configuration there are no more.
    private void ReplaceWatcher(object? ended, int configuration)
    {
        Exception? notCreated = null;
        Exception? notWatched = null;
        bool givenUp = false;
        lock (_gate)
        {
            // An error from a watcher that was already replaced, or from one of a configuration
            // that has been replaced, says nothing about the watcher that is in use now.
            if (configuration != _configuration || !ReferenceEquals(ended, _watcher) || _paths is not { } paths)
            {
                return;
            }

            // The old watcher goes first: while it holds the folder open, a deleted folder
            // cannot be made again.
            StopWatcherCore();
            if (_watcherReplacements < MaxWatcherReplacements)
            {
                _watcherReplacements++;
                notCreated = TryCreateFolders(paths);
                if (notCreated is null)
                {
                    _watcher = TryWatch(paths.LibraryDir, configuration, out notWatched);
                }
            }
            else
            {
                givenUp = true;
            }

            // Whatever changed while there was no watcher is picked up by reading the folder,
            // which also empties the library if its folder is gone for good.
            RequestLoadCore();
        }

        if (givenUp)
        {
            Report("The library is no longer watched for changes until Reload", null);
        }

        if (notCreated is not null)
        {
            Report("The library folder could not be created", notCreated);
        }

        if (notWatched is not null)
        {
            Report("The library folder could not be watched for changes", notWatched);
        }
    }

    // Called on a pool thread by the timer when the quiet period has run out.
    private void OnQuiet(int configuration)
    {
        try
        {
            lock (_gate)
            {
                if (configuration == _configuration)
                {
                    RequestLoadCore();
                }
            }
        }
        catch (Exception ex)
        {
            Report("A change to the library could not be acted on", ex);
        }
    }

    // Called with the lock held. Asking while a load is running makes it run once more when it
    // finishes, however many times it is asked.
    private void RequestLoadCore()
    {
        _loadWanted = true;
        if (!_loading)
        {
            _loading = true;
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task.Run(DoLoads);
        }
    }

    // The one place that reads the library, on one thread at a time.
    private void DoLoads()
    {
        while (true)
        {
            DataPaths paths;
            int configuration;
            lock (_gate)
            {
                if (!_loadWanted || _paths is null)
                {
                    _loading = false;
                    _idle?.TrySetResult();
                    return;
                }

                _loadWanted = false;
                paths = _paths;
                configuration = _configuration;
            }

            try
            {
                var snapshot = _load(paths);
                string? note;
                lock (_gate)
                {
                    if (configuration != _configuration)
                    {
                        // Configure or Dispose was called while this ran. What was read belongs
                        // to a folder that is no longer the one in use.
                        continue;
                    }

                    Volatile.Write(ref _current, snapshot);
                    note = NoteOnBadFilesCore(snapshot.BadFiles);
                    TryUnreadableFilesAgainCore(snapshot);
                }

                Report(
                    $"Library loaded: {snapshot.Quicklinks.Count} quicklinks, {snapshot.Snippets.Count} snippets, {snapshot.Scripts.Count} scripts, {snapshot.BadFiles.Count} files skipped",
                    null);
                if (note is not null)
                {
                    Notify(note);
                }

                Changed();
            }
            catch (Exception ex)
            {
                // Nobody is above this on a pool thread: an escaped exception would end the app.
                // What was loaded before stays in use.
                Report("The library could not be loaded", ex);
            }
        }
    }

    // Called with the lock held, for a load that is being kept. A file that could not be opened
    // (an editor has it locked, say) is not a change on disk: when the lock is let go of, nothing
    // is reported, and the file would stay missing until the next edit or Reload. So the quiet
    // period is started again, as if a change had arrived. That is done a few times in a row at
    // most, so that a file that stays locked does not keep the library loading for ever; a load
    // that finds nothing of the kind, or Configure, starts the count again.
    private void TryUnreadableFilesAgainCore(LibrarySnapshot snapshot)
    {
        if (!snapshot.HasUnreadableFiles)
        {
            _unreadableTries = 0;
        }
        else if (_unreadableTries < MaxUnreadableTries)
        {
            _unreadableTries++;
            _quiet?.Change(QuietPeriod, Timeout.InfiniteTimeSpan);
        }
    }

    // Called with the lock held. The owner is told which files are bad when that set changes,
    // not after every load: saving a good file must not bring the same notification back.
    private string? NoteOnBadFilesCore(IReadOnlyList<string> badFiles)
    {
        if (_badFilesTold.SetEquals(badFiles))
        {
            return null;
        }

        _badFilesTold = new HashSet<string>(badFiles, StringComparer.OrdinalIgnoreCase);
        return badFiles.Count > 0 ? LibraryNotes.BadFiles(badFiles) : null;
    }

    // The callbacks are the caller's code. If one threw inside the worker, the worker would end
    // with a load still wanted, and from a watcher's callback it would end the app. A report or
    // a notification that cannot be made is dropped.
    private void Report(string message, Exception? error)
    {
        try
        {
            _report(message, error);
        }
        catch (Exception)
        {
            // Nothing more can be done about a log that is broken.
        }
    }

    private void Notify(string message)
    {
        try
        {
            _notify(message);
        }
        catch (Exception)
        {
            // A notification is a courtesy; the log already has the count.
        }
    }

    private void Changed()
    {
        try
        {
            _changed();
        }
        catch (Exception)
        {
            // Whoever listens sees the library the next time it searches.
        }
    }
}
