using System.Text;
using Wyvern.Core.Sync;

namespace Wyvern.Tests;

/// <summary>
/// The id a data folder carries in a file of its own, so that the record of the last sync is
/// only used for the folder it was made for, as that folder stands on the disk. Every folder
/// here is in the test's own sandbox, under the machine's temporary folder.
/// </summary>
public sealed class SyncFolderIdTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly SyncSandbox _box = new();

    public void Dispose() => _box.Dispose();

    private string IdFile => Path.Combine(_box.Data, SyncFolderId.FileName);

    private static bool IsAnId(string text) => text.Length == 32 && text.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private string[] InTheDataFolder() => [.. Directory.EnumerateFileSystemEntries(_box.Data).Select(entry => Path.GetFileName(entry)).Order(StringComparer.Ordinal)];

    [Fact]
    public void An_id_is_made_once_and_read_back_the_same()
    {
        Assert.True(SyncFolderId.TryGet(_box.Data, out string id));

        Assert.True(IsAnId(id), id);
        Assert.Equal(".~beacon-sync-id", SyncFolderId.FileName);
        Assert.Equal(id, File.ReadAllText(IdFile));
        Assert.Equal(32, new FileInfo(IdFile).Length);
        Assert.Equal([SyncFolderId.FileName], InTheDataFolder());

        // An ordinary file that anyone can see: emptying the folder by hand takes it along.
        Assert.Equal((FileAttributes)0, File.GetAttributes(IdFile) & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly));

        var written = File.GetLastWriteTimeUtc(IdFile);
        Assert.True(SyncFolderId.TryGet(_box.Data, out string again));
        Assert.True(SyncFolderId.TryGet(_box.Data + Path.DirectorySeparatorChar, out string withSeparator));

        Assert.Equal(id, again);
        Assert.Equal(id, withSeparator);
        Assert.Equal(written, File.GetLastWriteTimeUtc(IdFile));
        Assert.Equal([SyncFolderId.FileName], InTheDataFolder());
    }

    [Fact]
    public void Two_folders_do_not_get_the_same_id_and_an_emptied_folder_gets_a_new_one()
    {
        string other = Path.Combine(_box.Root, "other-data");
        Directory.CreateDirectory(other);

        Assert.True(SyncFolderId.TryGet(_box.Data, out string first));
        Assert.True(SyncFolderId.TryGet(other, out string second));
        Assert.NotEqual(first, second);

        // Emptied by hand, the folder is no longer the folder the record was made for.
        File.Delete(IdFile);
        Assert.True(SyncFolderId.TryGet(_box.Data, out string afterEmptying));

        Assert.True(IsAnId(afterEmptying), afterEmptying);
        Assert.NotEqual(first, afterEmptying);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not an id")]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcdeg")]
    [InlineData("0123456789abcdef0123456789abcdef\n")]
    [InlineData("\uFEFF0123456789abcdef0123456789abcdef")]
    [InlineData(" 123456789abcdef0123456789abcdef")]
    public void An_id_file_that_holds_anything_but_an_id_is_replaced_by_a_new_id(string content)
    {
        File.WriteAllText(IdFile, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Assert.True(SyncFolderId.TryGet(_box.Data, out string id));

        Assert.True(IsAnId(id), id);
        Assert.NotEqual(content.Trim().TrimStart('\uFEFF'), id);
        Assert.Equal(id, File.ReadAllText(IdFile));
        Assert.Equal([SyncFolderId.FileName], InTheDataFolder());
        Assert.True(SyncFolderId.TryGet(_box.Data, out string again));
        Assert.Equal(id, again);
    }

    [Fact]
    public void An_id_file_that_is_far_too_long_is_replaced_without_being_read_whole()
    {
        File.WriteAllText(IdFile, new string('a', 3 * 1024 * 1024));

        Assert.True(SyncFolderId.TryGet(_box.Data, out string id));

        Assert.True(IsAnId(id), id);
        Assert.Equal(32, new FileInfo(IdFile).Length);
    }

    [Fact]
    public void A_good_id_is_the_id_whatever_made_the_file()
    {
        File.WriteAllText(IdFile, "0123456789abcdef0123456789abcdef");

        Assert.True(SyncFolderId.TryGet(_box.Data, out string id));

        Assert.Equal("0123456789abcdef0123456789abcdef", id);
    }

    [Fact]
    public void With_no_data_folder_there_is_no_id_and_no_folder_is_made()
    {
        string nowhere = Path.Combine(_box.Root, "nowhere");

        Assert.False(SyncFolderId.TryGet(nowhere, out string id));

        Assert.Equal("", id);
        Assert.False(Directory.Exists(nowhere));
    }

    [Theory]
    [InlineData("")]
    [InlineData("data")]
    [InlineData(@"..\data")]
    public void A_data_folder_that_is_not_a_full_path_has_no_id(string folder)
    {
        // Such text would mean whatever folder Wyvern happened to be started from.
        Assert.False(SyncFolderId.TryGet(folder, out string id));
        Assert.Equal("", id);
    }

    [Fact]
    public void A_data_folder_whose_name_Windows_cannot_take_has_no_id_and_nothing_is_thrown()
    {
        Assert.False(SyncFolderId.TryGet(Path.Combine(_box.Root, "no|such?folder"), out string id));
        Assert.Equal("", id);
    }

    [Fact]
    public void An_id_file_that_another_program_holds_gives_no_id_and_is_left_as_it_is()
    {
        File.WriteAllText(IdFile, "0123456789abcdef0123456789abcdef");
        using (new FileStream(IdFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(SyncFolderId.TryGet(_box.Data, out string id));
            Assert.Equal("", id);
        }

        Assert.Equal("0123456789abcdef0123456789abcdef", File.ReadAllText(IdFile));
        Assert.Equal([SyncFolderId.FileName], InTheDataFolder());
    }

    [Fact]
    public void A_damaged_id_file_that_may_not_be_written_gives_no_id_and_is_left_as_it_is()
    {
        File.WriteAllText(IdFile, "not an id");
        File.SetAttributes(IdFile, FileAttributes.ReadOnly);
        try
        {
            Assert.False(SyncFolderId.TryGet(_box.Data, out string id));
            Assert.Equal("", id);
            Assert.Equal("not an id", File.ReadAllText(IdFile));
            Assert.Equal([SyncFolderId.FileName], InTheDataFolder());
        }
        finally
        {
            File.SetAttributes(IdFile, FileAttributes.Normal);
        }
    }

    [Fact]
    public void A_folder_that_has_the_id_files_name_gives_no_id_and_stays()
    {
        Directory.CreateDirectory(IdFile);
        File.WriteAllText(Path.Combine(IdFile, "kept.txt"), "walrus");

        Assert.False(SyncFolderId.TryGet(_box.Data, out string id));

        Assert.Equal("", id);
        Assert.Equal("walrus", File.ReadAllText(Path.Combine(IdFile, "kept.txt")));
        Assert.Equal([SyncFolderId.FileName], InTheDataFolder());
    }

    [Fact]
    public void A_link_that_has_the_id_files_name_is_refused_and_nothing_is_written_through_it()
    {
        // A junction, which needs no special right. What is behind it is another place.
        string elsewhere = Path.Combine(_box.Root, "elsewhere");
        _box.Junction(IdFile, elsewhere);

        Assert.False(SyncFolderId.TryGet(_box.Data, out string id));

        Assert.Equal("", id);
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
        Assert.NotNull(new DirectoryInfo(IdFile).LinkTarget);
    }

    [Fact]
    public void An_id_file_that_is_a_symbolic_link_is_refused_where_this_machine_lets_a_test_make_one()
    {
        // Making a symbolic link to a file needs a right that most accounts do not have. Where it
        // is missing this test can show nothing, and says so by ending here.
        string target = Path.Combine(_box.Root, "id-elsewhere");
        File.WriteAllText(target, "0123456789abcdef0123456789abcdef");
        try
        {
            File.CreateSymbolicLink(IdFile, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        try
        {
            Assert.False(SyncFolderId.TryGet(_box.Data, out string id));
            Assert.Equal("", id);
            Assert.Equal("0123456789abcdef0123456789abcdef", File.ReadAllText(target));
            Assert.NotNull(new FileInfo(IdFile).LinkTarget);
        }
        finally
        {
            File.Delete(IdFile);
        }
    }

    [Fact]
    public void The_id_file_has_a_name_that_sync_never_uploads_and_never_takes_for_a_leftover()
    {
        Assert.True(SyncPath.TryParse(SyncFolderId.FileName, out _));
        Assert.False(SyncPath.IsSynced(SyncFolderId.FileName));
        Assert.False(SyncFiles.IsTemporaryName(SyncFolderId.FileName));
        Assert.StartsWith(".~", SyncFolderId.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public void The_scan_passes_over_the_id_file_and_leaves_it_there_however_old_it_is()
    {
        Assert.True(SyncFolderId.TryGet(_box.Data, out string id));
        File.SetLastWriteTimeUtc(IdFile, Now.AddDays(-400).UtcDateTime);
        _box.Write("library/snippets/sig.md", "walrus", Now.AddHours(-1));

        var view = LocalScan.Run(_box.Data, new SyncState("scope"), Now);

        Assert.NotNull(view);
        Assert.Equal(["library/snippets/sig.md"], view.Files.Keys);
        Assert.Equal(0, view.BadNames + view.Unreadable + view.LeftoversRemoved);
        Assert.True(SyncFolderId.TryGet(_box.Data, out string after));
        Assert.Equal(id, after);
    }

    [Fact]
    public void The_scope_of_a_record_takes_the_id_in_and_does_not_show_it()
    {
        const string one = "0123456789abcdef0123456789abcdef";
        const string two = "fedcba9876543210fedcba9876543210";
        string scope = SyncState.ScopeOf("remote-one", @"D:\Wyvern\data", one);

        Assert.Equal(64, scope.Length);
        Assert.Equal(scope, SyncState.ScopeOf("remote-one", @"d:\wyvern\DATA\", one));
        Assert.NotEqual(scope, SyncState.ScopeOf("remote-one", @"D:\Wyvern\data", two));
        Assert.NotEqual(scope, SyncState.ScopeOf("remote-one", @"D:\Wyvern\data", ""));
        Assert.DoesNotContain(one, scope, StringComparison.OrdinalIgnoreCase);

        // The three parts do not run into each other.
        Assert.NotEqual(SyncState.ScopeOf("remote", @"D:\data", "ab"), SyncState.ScopeOf("remote", @"D:\dataa", "b"));
    }
}
