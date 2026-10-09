using Wyvern.Core.Files;
using Wyvern.Core.Settings;

namespace Wyvern.Tests;

public sealed class FileIndexUpdaterTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FileIndex _index = new();
    private readonly FileIndexUpdater _updater;

    public FileIndexUpdaterTests()
    {
        var options = FileIndexOptions.From(new WyvernSettings
        {
            IndexedFolders = [Root],
            ExcludedFolderNames = ["node_modules"],
        });
        _updater = new FileIndexUpdater(_index, options);
    }

    public void Dispose() => _dir.Dispose();

    private string Root => _dir.Path;

    private string Touch(params string[] relative)
    {
        string path = Path.Combine([Root, .. relative]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    private bool Has(params string[] relative) => _index.TryGet(Path.Combine([Root, .. relative]), out _);

    [Fact]
    public void A_created_file_is_added()
    {
        _updater.Created(Touch("docs", "report.docx"));

        Assert.True(_index.TryGet(Path.Combine(Root, "docs", "report.docx"), out var entry));
        Assert.Equal(new FileEntry(Path.Combine(Root, "docs"), "report.docx", IsDirectory: false), entry);
    }

    [Fact]
    public void A_created_folder_brings_its_contents_with_it()
    {
        // What a folder moved in from elsewhere looks like: one event, contents already there.
        Touch("moved", "a.txt");
        Touch("moved", "sub", "b.txt");
        Touch("moved", "node_modules", "skipped.js");

        _updater.Created(Path.Combine(Root, "moved"));

        Assert.True(Has("moved"));
        Assert.True(Has("moved", "a.txt"));
        Assert.True(Has("moved", "sub"));
        Assert.True(Has("moved", "sub", "b.txt"));
        Assert.False(Has("moved", "node_modules"));
        Assert.Equal(4, _index.Count);
    }

    [Fact]
    public void The_scan_of_a_folder_moved_in_stops_when_it_is_cancelled()
    {
        Touch("moved", "a.txt");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => _updater.Created(Path.Combine(Root, "moved"), cancelled.Token));
    }

    [Fact]
    public void Things_that_should_not_be_indexed_are_ignored()
    {
        using var elsewhere = new TempDir();
        string outside = elsewhere.File("outside.txt");
        File.WriteAllText(outside, "");
        string hidden = Touch("hidden.txt");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

        _updater.Created(outside);
        _updater.Created(hidden);
        _updater.Created(Touch("node_modules", "lib.js"));
        _updater.Created(Path.Combine(Root, "node_modules"));
        _updater.Created(Path.Combine(Root, "already-gone.txt"));
        _updater.Created(Root);

        Assert.Equal(0, _index.Count);
    }

    [Fact]
    public void Nothing_inside_a_hidden_folder_is_indexed_however_deep()
    {
        // The scanner never enters a hidden folder, so events from inside one must be ignored too.
        string direct = Touch("cache", "direct.txt");
        string deeper = Touch("cache", "sub", "deeper.txt");
        new DirectoryInfo(Path.Combine(Root, "cache")).Attributes |= FileAttributes.Hidden;

        _updater.Created(direct);
        _updater.Created(deeper);
        _updater.Created(Path.Combine(Root, "cache", "sub"));
        _updater.Created(Path.Combine(Root, "cache"));
        _updater.Renamed(Path.Combine(Root, "cache", "old.txt"), direct);

        Assert.Equal(0, _index.Count);
    }

    [Fact]
    public void A_deleted_file_is_removed_and_a_deleted_folder_takes_its_contents()
    {
        _updater.Created(Touch("keep.txt"));
        _updater.Created(Touch("gone.txt"));
        Touch("folder", "inside.txt");
        _updater.Created(Path.Combine(Root, "folder"));

        _updater.Deleted(Path.Combine(Root, "gone.txt"));
        _updater.Deleted(Path.Combine(Root, "folder"));
        _updater.Deleted(Path.Combine(Root, "never-indexed.txt"));

        Assert.True(Has("keep.txt"));
        Assert.Equal(1, _index.Count);
    }

    [Fact]
    public void A_renamed_file_keeps_one_entry_under_the_new_name()
    {
        string oldPath = Touch("draft.txt");
        _updater.Created(oldPath);
        string newPath = Path.Combine(Root, "final.txt");
        File.Move(oldPath, newPath);

        _updater.Renamed(oldPath, newPath);

        Assert.False(Has("draft.txt"));
        Assert.True(Has("final.txt"));
        Assert.Equal(1, _index.Count);
    }

    [Fact]
    public void A_folder_renamed_to_an_excluded_name_leaves_the_index()
    {
        Touch("packages", "lib.js");
        _updater.Created(Path.Combine(Root, "packages"));
        Directory.Move(Path.Combine(Root, "packages"), Path.Combine(Root, "node_modules"));

        _updater.Renamed(Path.Combine(Root, "packages"), Path.Combine(Root, "node_modules"));

        Assert.Equal(0, _index.Count);
    }

    [Fact]
    public void A_folder_renamed_from_an_excluded_name_joins_the_index_with_its_contents()
    {
        Touch("node_modules", "lib.js");
        _updater.Created(Path.Combine(Root, "node_modules"));
        Assert.Equal(0, _index.Count);
        Directory.Move(Path.Combine(Root, "node_modules"), Path.Combine(Root, "packages"));

        _updater.Renamed(Path.Combine(Root, "node_modules"), Path.Combine(Root, "packages"));

        Assert.True(Has("packages"));
        Assert.True(Has("packages", "lib.js"));
    }

    [Fact]
    public void Rescan_makes_the_index_match_the_folder_again()
    {
        _updater.Created(Touch("stale.txt"));
        File.Delete(Path.Combine(Root, "stale.txt"));
        Touch("fresh.txt");

        _updater.Rescan(Root, CancellationToken.None);

        Assert.False(Has("stale.txt"));
        Assert.True(Has("fresh.txt"));
        Assert.Equal(1, _index.Count);
    }
}
