using Beacon.Core.Files;
using Beacon.Core.Settings;

namespace Beacon.Tests;

/// <summary>
/// These use the real file system and real change notifications, which arrive a moment after
/// the change. Each check therefore waits, up to a generous limit, for the index to catch up.
/// </summary>
public sealed class FileIndexServiceTests : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    // Far longer than a change notification takes to arrive. A test that waits this long while
    // a scan is held can take it that the notifications are in.
    private static readonly TimeSpan NotificationsAreIn = TimeSpan.FromMilliseconds(400);

    private readonly TempDir _dir = new();
    private readonly FileIndex _index = new();
    private readonly List<string> _reports = [];
    private readonly ManualResetEventSlim _scanHasListedTheFolder = new();
    private readonly ManualResetEventSlim _scanMayFinish = new(initialState: true);
    private readonly FileIndexService _service;

    public FileIndexServiceTests()
    {
        _service = new FileIndexService(
            _index,
            (message, _) =>
            {
                lock (_reports)
                {
                    _reports.Add(message);
                }
            },
            (folder, excludedNames, cancellation) =>
            {
                // The real scan, with a place for a test to hold it: after it has listed the
                // folder, before what it found is put in the index.
                var found = FileScanner.Scan(folder, excludedNames, cancellation);
                _scanHasListedTheFolder.Set();
                _scanMayFinish.Wait(Limit);
                return found;
            });
    }

    public void Dispose()
    {
        _scanMayFinish.Set();
        _service.Dispose();
        _dir.Dispose();
    }

    private string Root => _dir.Path;

    private static FileIndexOptions OptionsFor(params string[] folders) =>
        FileIndexOptions.From(new BeaconSettings { IndexedFolders = [.. folders] });

    private string Touch(params string[] relative)
    {
        string path = Path.Combine([Root, .. relative]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    private async Task StartAndWaitForScan(params string[] folders)
    {
        _service.Configure(OptionsFor(folders));
        await _service.Scanning.WaitAsync(Limit);
    }

    /// <summary>Starts indexing and returns once the scan has listed the folder and is being held there.</summary>
    private void StartAndHoldTheScan(params string[] folders)
    {
        _scanMayFinish.Reset();
        _service.Configure(OptionsFor(folders));
        Assert.True(_scanHasListedTheFolder.Wait(Limit), "Timed out waiting for the scan to list the folder");
    }

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.True(condition(), $"Timed out waiting for: {what}");
    }

    /// <summary>True when the index holds exactly the files and folders that are under the root now.</summary>
    private bool IndexMatchesTheFolder()
    {
        var onDisk = Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories).ToList();
        return onDisk.Count == _index.Count && onDisk.TrueForAll(path => _index.TryGet(path, out _));
    }

    [Fact]
    public async Task Indexes_what_is_already_in_the_folder()
    {
        Touch("report.docx");
        Touch("photos", "beach.jpg");

        await StartAndWaitForScan(Root);

        Assert.True(_index.TryGet(Path.Combine(Root, "report.docx"), out _));
        Assert.True(_index.TryGet(Path.Combine(Root, "photos"), out _));
        Assert.True(_index.TryGet(Path.Combine(Root, "photos", "beach.jpg"), out _));
        Assert.Equal(3, _index.Count);
    }

    [Fact]
    public async Task Reports_progress_without_naming_any_file_or_folder()
    {
        Touch("private-diary.txt");

        await StartAndWaitForScan(Root);

        lock (_reports)
        {
            Assert.Contains("File index ready: 1 entries from 1 folders", _reports);
            Assert.DoesNotContain(_reports, message => message.Contains("diary") || message.Contains(Root));
        }
    }

    [Fact]
    public async Task Picks_up_a_file_created_after_the_scan()
    {
        await StartAndWaitForScan(Root);

        string created = Touch("new-file.txt");

        await Eventually(() => _index.TryGet(created, out _), "the created file to be indexed");
    }

    [Fact]
    public async Task Picks_up_a_rename_and_a_delete()
    {
        string original = Touch("draft.txt");
        await StartAndWaitForScan(Root);
        string renamed = Path.Combine(Root, "final.txt");

        File.Move(original, renamed);
        await Eventually(() => _index.TryGet(renamed, out _) && !_index.TryGet(original, out _), "the rename to be applied");

        File.Delete(renamed);
        await Eventually(() => _index.Count == 0, "the delete to be applied");
    }

    [Fact]
    public async Task Changes_made_while_a_scan_is_running_are_applied_after_it()
    {
        string doomed = Touch("doomed.txt");
        string draft = Touch("draft.txt");
        Touch("kept.txt");

        // The scan has listed the three files but has not put them in the index yet.
        StartAndHoldTheScan(Root);
        File.Delete(doomed);
        File.Move(draft, Path.Combine(Root, "final.txt"));
        Touch("arrived.txt");
        Touch("new-folder", "inside.txt");

        // The way most programs save: write a temporary file, then rename it into place.
        File.Move(Touch("saving.tmp"), Path.Combine(Root, "saved.txt"));

        await Task.Delay(NotificationsAreIn);
        _scanMayFinish.Set();

        // What the scan listed is out of date by now. The changes have to win.
        await Eventually(
            () => _service.Scanning.IsCompleted && IndexMatchesTheFolder(),
            "the index to hold exactly what is in the folder");
        Assert.False(_index.TryGet(doomed, out _));
        Assert.True(_index.TryGet(Path.Combine(Root, "saved.txt"), out _));
        Assert.Equal(6, _index.Count);
    }

    [Fact]
    public async Task A_folder_that_does_not_exist_is_returned_and_the_others_are_still_indexed()
    {
        Touch("kept.txt");
        string absent = Path.Combine(Path.GetTempPath(), "beacon-tests-absent-" + Guid.NewGuid().ToString("N"));

        var missing = _service.Configure(OptionsFor(Root, absent));
        await _service.Scanning.WaitAsync(Limit);

        Assert.Equal([absent], missing);
        Assert.True(_index.TryGet(Path.Combine(Root, "kept.txt"), out _));
    }

    [Fact]
    public async Task Configuring_again_replaces_what_was_indexed_before()
    {
        using var other = new TempDir();
        File.WriteAllText(other.File("second.txt"), "");
        Touch("first.txt");
        await StartAndWaitForScan(Root);

        await StartAndWaitForScan(other.Path);

        Assert.False(_index.TryGet(Path.Combine(Root, "first.txt"), out _));
        Assert.True(_index.TryGet(other.File("second.txt"), out _));
        Assert.Equal(1, _index.Count);

        // The first folder is no longer watched.
        Touch("ignored.txt");
        await Task.Delay(500);
        Assert.Equal(1, _index.Count);
    }

    [Fact]
    public async Task Configuring_again_while_a_scan_is_running_leaves_only_the_new_folder()
    {
        using var other = new TempDir();
        File.WriteAllText(other.File("second.txt"), "");
        Touch("first.txt");

        // The first scan has its list and is about to put it in the index.
        StartAndHoldTheScan(Root);
        _service.Configure(OptionsFor(other.Path));
        _scanMayFinish.Set();
        await _service.Scanning.WaitAsync(Limit);

        Assert.False(_index.TryGet(Path.Combine(Root, "first.txt"), out _));
        Assert.True(_index.TryGet(other.File("second.txt"), out _));
        Assert.Equal(1, _index.Count);
    }

    [Fact]
    public async Task Rescan_puts_back_what_the_index_has_lost()
    {
        // Stands in for a watcher that overflowed: the index no longer matches the folder.
        Touch("report.docx");
        await StartAndWaitForScan(Root);
        _index.Clear();

        Assert.True(_service.Rescan(Root));
        await _service.Scanning.WaitAsync(Limit);

        Assert.True(_index.TryGet(Path.Combine(Root, "report.docx"), out _));
        Assert.Equal(1, _index.Count);
    }

    [Fact]
    public async Task Asking_for_a_rescan_that_is_already_waiting_queues_nothing()
    {
        Touch("report.docx");

        // While the first scan is held, anything queued has to wait behind it.
        StartAndHoldTheScan(Root);
        Assert.True(_service.Rescan(Root));
        Assert.False(_service.Rescan(Root));
        _scanMayFinish.Set();
        await _service.Scanning.WaitAsync(Limit);

        // Once the waiting one has run, the folder can be queued again.
        Assert.True(_service.Rescan(Root));
        await _service.Scanning.WaitAsync(Limit);
        Assert.Equal(1, _index.Count);
    }

    [Fact]
    public async Task Rescan_of_a_folder_that_is_not_being_indexed_does_nothing()
    {
        using var other = new TempDir();
        Assert.False(_service.Rescan(Root)); // nothing has been configured yet

        await StartAndWaitForScan(Root);
        Assert.False(_service.Rescan(other.Path)); // not one of the indexed folders

        _service.Dispose();
        Assert.False(_service.Rescan(Root)); // stopped
    }

    [Fact]
    public async Task A_report_callback_that_throws_does_not_stop_the_index()
    {
        Touch("report.docx");
        var index = new FileIndex();
        using var service = new FileIndexService(index, (_, _) => throw new InvalidOperationException("the log is broken"));

        // The report of the finished scan throws, and so does the report of that failure.
        service.Configure(OptionsFor(Root));
        await service.Scanning.WaitAsync(Limit);
        Assert.True(index.TryGet(Path.Combine(Root, "report.docx"), out _));

        // The queue still works afterwards.
        Assert.True(service.Rescan(Root));
        await service.Scanning.WaitAsync(Limit);
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void Configuring_after_dispose_is_an_error()
    {
        _service.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _service.Configure(OptionsFor(Root)));
    }
}
