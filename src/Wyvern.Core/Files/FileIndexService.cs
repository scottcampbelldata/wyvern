namespace Wyvern.Core.Files;

/// <summary>
/// Keeps a <see cref="FileIndex"/> in step with the folders on disk: scans them in the
/// background, then watches them for files appearing, disappearing and being renamed.
/// </summary>
/// <remarks>
/// Every change to the index (a scan, a rescan, one file event) is a piece of work on a single
/// queue, done one at a time in the order it arrived. A file event that arrives while a scan is
/// running therefore waits and is applied after the scan's result, never before it, so the scan
/// can neither lose something that was created meanwhile nor bring back something that was
/// deleted. It also means a watcher's callback only queues the work and returns, which keeps the
/// watcher's own buffer from filling.
/// </remarks>
public sealed class FileIndexService : IDisposable
{
    // The largest buffer Windows allows. A burst of changes that overflows it loses events,
    // and the folder then has to be scanned again.
    private const int WatcherBufferBytes = 64 * 1024;

    private readonly FileIndex _index;
    private readonly Action<string, Exception?> _report;
    private readonly FolderScan? _scan;
    private readonly Lock _gate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Queue<Action> _work = new();
    private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _rescansWaiting = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _cancel = new();
    private FileIndexUpdater? _updater;
    private TaskCompletionSource? _idle;
    private bool _working;
    private bool _disposed;

    /// <param name="report">
    /// Receives progress and problems for the log. The text never contains a file or folder
    /// name; the exception, when there is one, is logged by type only. A callback that throws
    /// is ignored.
    /// </param>
    /// <param name="scan">How a folder is listed. Left out everywhere except in tests.</param>
    public FileIndexService(FileIndex index, Action<string, Exception?>? report = null, FolderScan? scan = null)
    {
        _index = index;
        _report = report ?? ((_, _) => { });
        _scan = scan;
    }

    /// <summary>
    /// Completes when the queue is empty: the scan started by the latest <see cref="Configure"/>,
    /// any rescans, and every file change received so far have all been applied.
    /// </summary>
    public Task Scanning
    {
        get
        {
            lock (_gate)
            {
                return _idle?.Task ?? Task.CompletedTask;
            }
        }
    }

    /// <summary>Starts indexing the given folders afresh. Returns at once; the scan runs in the background.</summary>
    /// <returns>The folders that do not exist. They are not indexed.</returns>
    public IReadOnlyList<string> Configure(FileIndexOptions options)
    {
        var missing = new List<string>();
        var notWatched = new List<Exception>();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopCore();

            var cancel = _cancel = new CancellationTokenSource();
            var updater = _updater = new FileIndexUpdater(_index, options, _scan);
            var present = new List<string>();
            foreach (string folder in options.Folders)
            {
                if (!Directory.Exists(folder))
                {
                    missing.Add(folder);
                    continue;
                }

                present.Add(folder);
                _folders.Add(folder);
                // Watch before scanning, so nothing that changes during the scan is missed.
                if (TryWatch(folder, updater, cancel.Token, out var error) is { } watcher)
                {
                    _watchers.Add(watcher);
                }
                else if (error is not null)
                {
                    notWatched.Add(error);
                }
            }

            // Emptying the index is queued too, so it happens after any piece of work from the
            // previous configuration that is still running, and cannot be undone by it.
            EnqueueCore(() =>
            {
                _index.Clear();
                ScanAll(present, updater, cancel.Token);
            });
        }

        foreach (var error in notWatched)
        {
            // The folder is still scanned; it just will not notice later changes.
            Report("An indexed folder could not be watched for changes", error);
        }

        return missing;
    }

    /// <summary>
    /// Queues a fresh scan of one indexed folder. This is what happens when a watcher reports an
    /// error, for example when a burst of changes overflows it and some of its events are lost.
    /// </summary>
    /// <returns>
    /// False when nothing was queued: a scan of that folder is already waiting, the folder is
    /// not one of the indexed folders, or the service is not running.
    /// </returns>
    public bool Rescan(string folder)
    {
        lock (_gate)
        {
            return _updater is { } updater
                && _folders.Contains(Path.TrimEndingDirectorySeparator(folder))
                && RescanCore(Path.TrimEndingDirectorySeparator(folder), updater, _cancel.Token);
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
            }
        }
    }

    // Called with the lock held.
    private void StopCore()
    {
        _cancel.Cancel();
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();
        _work.Clear();
        _folders.Clear();
        _rescansWaiting.Clear();
        _updater = null;
    }

    // Called with the lock held. The updater and the token belong to one configuration; once
    // that configuration has been replaced, its token is cancelled and nothing is queued.
    private bool RescanCore(string folder, FileIndexUpdater updater, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested || !_rescansWaiting.Add(folder))
        {
            return false;
        }

        EnqueueCore(() =>
        {
            lock (_gate)
            {
                // Taken off the list before the scan starts, so an overflow during this scan
                // queues another one. A scan left over from a replaced configuration leaves
                // the list alone: it now belongs to the new configuration.
                if (!cancellation.IsCancellationRequested)
                {
                    _rescansWaiting.Remove(folder);
                }
            }

            TryRescan(folder, updater, cancellation);
        });
        return true;
    }

    // Called with the lock held.
    private void EnqueueCore(Action work)
    {
        _work.Enqueue(work);
        if (!_working)
        {
            _working = true;
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task.Run(DoQueuedWork);
        }
    }

    // The one place that changes the index, on one thread at a time.
    private void DoQueuedWork()
    {
        while (true)
        {
            Action work;
            lock (_gate)
            {
                if (_work.Count == 0)
                {
                    _working = false;
                    _idle?.TrySetResult();
                    return;
                }

                work = _work.Dequeue();
            }

            try
            {
                work();
            }
            catch (OperationCanceledException)
            {
                // Reload or shutdown cancelled the scan of a folder that was moved in. That is
                // what was asked for, not a failure, so nothing is reported.
            }
            catch (Exception ex)
            {
                // Nobody is above this on a pool thread: an escaped exception would end the app.
                Report("A change to the file index could not be applied", ex);
            }
        }
    }

    private void ScanAll(IReadOnlyList<string> folders, FileIndexUpdater updater, CancellationToken cancellation)
    {
        foreach (string folder in folders)
        {
            if (!TryRescan(folder, updater, cancellation))
            {
                return;
            }
        }

        if (!cancellation.IsCancellationRequested)
        {
            Report($"File index ready: {_index.Count} entries from {folders.Count} folders", null);
        }
    }

    /// <returns>False when cancelled.</returns>
    private bool TryRescan(string folder, FileIndexUpdater updater, CancellationToken cancellation)
    {
        try
        {
            updater.Rescan(folder, cancellation);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            // One folder that cannot be scanned must not stop the others from being indexed.
            Report("An indexed folder could not be scanned", ex);
            return true;
        }
    }

    /// <returns>Null when the folder cannot be watched; <paramref name="error"/> then says why.</returns>
    private FileSystemWatcher? TryWatch(
        string folder, FileIndexUpdater updater, CancellationToken cancellation, out Exception? error)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                InternalBufferSize = WatcherBufferBytes,
            };
            watcher.Created += (_, e) => Queue(() => updater.Created(e.FullPath, cancellation), cancellation);
            watcher.Deleted += (_, e) => Queue(() => updater.Deleted(e.FullPath), cancellation);
            watcher.Renamed += (_, e) => Queue(() => updater.Renamed(e.OldFullPath, e.FullPath, cancellation), cancellation);
            watcher.Error += (_, e) => OnWatcherError(folder, e.GetException(), updater, cancellation);
            watcher.EnableRaisingEvents = true;
            error = null;
            return watcher;
        }
        catch (Exception ex)
        {
            // Whatever went wrong, the other folders are still watched and this one is still scanned.
            watcher?.Dispose();
            error = ex;
            return null;
        }
    }

    // Called on a pool thread by a watcher. It only queues the change, so it returns at once.
    private void Queue(Action change, CancellationToken cancellation)
    {
        lock (_gate)
        {
            if (!cancellation.IsCancellationRequested)
            {
                EnqueueCore(change);
            }
        }
    }

    private void OnWatcherError(string folder, Exception error, FileIndexUpdater updater, CancellationToken cancellation)
    {
        // Whatever the error was, events may have been lost, so the folder is scanned again. If
        // the folder has gone, the scan finds nothing and empties it, which is the truth.
        lock (_gate)
        {
            RescanCore(folder, updater, cancellation);
        }

        // Too many changes at once is expected and is put right by the scan; anything else is said.
        if (error is not InternalBufferOverflowException && !cancellation.IsCancellationRequested)
        {
            Report("Watching an indexed folder failed", error);
        }
    }

    // Every report goes through here. The callback is the caller's code. If it threw inside the
    // worker, the worker would end with the queue never emptied again, and from a watcher's
    // callback it would end the app. A report that cannot be made is dropped.
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
}
