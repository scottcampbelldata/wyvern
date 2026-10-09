using System.Text;
using System.Text.RegularExpressions;
using Wyvern.Core.Sync;

namespace Wyvern.Tests;

/// <summary>
/// The look at the data folder that every sync run starts with. The rule that carries the
/// weight: what cannot be looked at must be unknown to the run, never missing from it, because
/// a file that is missing is reported to the other machine as deleted.
/// </summary>
public sealed class LocalScanTests : IDisposable
{
    private const string Scope = "scope";

    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Earlier = new(2026, 9, 30, 8, 15, 0, TimeSpan.Zero);
    private static readonly string Blob = new('a', 64);

    private readonly SyncSandbox _box = new();
    private readonly SyncState _state = new(Scope);

    public void Dispose() => _box.Dispose();

    private LocalView Scan() => LocalScan.Run(_box.Data, _state, Now) ?? throw new InvalidOperationException("The data folder could not be listed.");

    // Enters a file in the record as synced with this content at this time.
    private void Synced(string path, string text, DateTimeOffset mtime, string? blob = null)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        _state.Set(new SyncedFile(path, blob ?? Blob, SyncedKind.Live, BlobCodec.Hash(bytes), bytes.Length, mtime, 200, Now));
    }

    [Fact]
    public void A_data_folder_that_is_not_there_gives_no_view_at_all()
    {
        Assert.Null(LocalScan.Run(Path.Combine(_box.Root, "nowhere"), _state, Now));
        Assert.Null(LocalScan.Run("relative-folder", _state, Now));
    }

    // The scan's guard is pinned as text and never by a scan of a drive's root: were the guard
    // gone, a test that scanned the root of a drive would read that drive. These two tests read
    // one source file of the repository (found from the test assembly up to Wyvern.slnx, as the
    // other tests that read source do) and scan nothing, so there is no path in them that
    // could lead to a real root.
    private const string GuardPattern = @"if \(!SyncFiles\.IsDataFolder\(dataRoot\)\)\s*\{\s*return null;\s*\}";

    private static string LocalScanSource() => File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Wyvern.Core", "Sync", "LocalScan.cs"));

    // What Run does first: it returns null for a folder that SyncFiles.IsDataFolder refuses,
    // before anything of the folder is used.
    private static void AssertRunAsksBeforeItUsesTheFolder(string source)
    {
        int signature = source.IndexOf("LocalView? Run(string dataRoot,", StringComparison.Ordinal);
        int next = source.IndexOf("private static void List(", StringComparison.Ordinal);
        Assert.True(signature >= 0 && next > signature, "LocalScan.Run was not found where this test looks for it.");
        string run = source[signature..next];

        var guard = Regex.Match(run, GuardPattern);
        Assert.True(guard.Success, "Run does not return null for a folder that SyncFiles.IsDataFolder refuses.");

        // Everything in front of the guard, but the signature, is a comment: nothing in Run has
        // used the folder, or the disk, before the question was asked.
        string before = run[(run.IndexOf('{', StringComparison.Ordinal) + 1)..guard.Index];
        Assert.Equal("", Regex.Replace(before, @"//[^\r\n]*", "").Trim());

        // After it the folder is used, so the guard is not a statement that guards nothing.
        string after = run[(guard.Index + guard.Length)..];
        Assert.Contains("new DirectoryInfo(", after, StringComparison.Ordinal);
        Assert.Contains("dataRoot", after, StringComparison.Ordinal);
    }

    [Fact]
    public void The_scan_asks_whether_the_folder_is_a_data_folder_before_it_asks_the_disk_anything()
    {
        AssertRunAsksBeforeItUsesTheFolder(LocalScanSource());
    }

    [Fact]
    public void The_text_test_of_that_guard_fails_for_a_scan_without_it_and_for_one_that_asks_too_late()
    {
        // Copies of the text in memory, with the guard taken out, or put after the first use of
        // the folder. The file itself is read and not written.
        string source = LocalScanSource();
        string without = Regex.Replace(source, GuardPattern, "");
        Assert.NotEqual(source, without);

        const string firstUse = "string root = Path.TrimEndingDirectorySeparator(dataRoot);";
        Assert.Contains(firstUse, without, StringComparison.Ordinal);
        string late = without.Replace(firstUse, firstUse + "\n        if (!SyncFiles.IsDataFolder(dataRoot))\n        {\n            return null;\n        }", StringComparison.Ordinal);
        Assert.NotEqual(without, late);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertRunAsksBeforeItUsesTheFolder(without));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertRunAsksBeforeItUsesTheFolder(late));
    }

    private static string RepositoryRoot()
    {
        var folder = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "Wyvern.slnx")))
        {
            folder = folder.Parent;
        }

        Assert.True(folder is not null, $"Wyvern.slnx was not found above {AppContext.BaseDirectory}");
        return folder.FullName;
    }

    [Fact]
    public void An_empty_data_folder_gives_an_empty_view_that_knows_every_path()
    {
        var view = Scan();

        Assert.Empty(view.Files);
        Assert.True(view.Knows("library/anything.md"));
    }

    [Fact]
    public void Files_the_record_does_not_know_are_new_at_any_depth_hidden_ones_included()
    {
        _box.Write("library/quicklinks.json", "[]", Earlier);
        _box.Write("clipboard/OTTER-PC/2026-10-03/deep/er/x.png", "png", Earlier);
        _box.Write("library/hidden.md", "text", Earlier);
        File.SetAttributes(_box.Full("library/hidden.md"), FileAttributes.Hidden);

        var view = Scan();

        Assert.Equal(["clipboard/otter-pc/2026-10-03/deep/er/x.png", "library/hidden.md", "library/quicklinks.json"], view.Files.Keys.Order(StringComparer.Ordinal));
        var file = view.Files["clipboard/otter-pc/2026-10-03/deep/er/x.png"];
        Assert.Equal("clipboard/OTTER-PC/2026-10-03/deep/er/x.png", file.Path);
        Assert.Equal(LocalChange.New, file.Change);
        Assert.Equal(new LocalStat(3, Earlier), file.Stat);
    }

    [Fact]
    public void Temporary_files_lock_files_and_names_that_cannot_be_synced_are_left_out()
    {
        _box.Write("library/a.md", "text");
        _box.Write("library/a.md.tmp", "being written");
        _box.Write("clipboard/OTTER-PC/.~lock", "");
        _box.Write("library/~$letter.docx", "lock");
        _box.Write("library/.~work/inside.md", "text");
        _box.Write("library/ leading space.md", "text");

        var view = Scan();

        Assert.Equal(["library/a.md"], view.Files.Keys);
        Assert.Equal(1, view.BadNames);
    }

    [Theory]
    [InlineData(false)]     // neither is in the record
    [InlineData(true)]      // one of them was synced before the other appeared
    public void Two_files_in_one_folder_whose_keys_are_equal_are_both_unknown_and_both_counted_with_the_names_that_cannot_be_synced(bool oneWasSynced)
    {
        // The Kelvin sign is a letter of its own to Windows, which keeps a file named with it
        // apart from one named with a small k. Lower-cased, the two names are the same: two
        // files and one key. Which of the two the key's blob holds cannot be told from the
        // key, so neither is this run's to sync, and neither is taken for the other.
        const string plain = "library/k.md";
        const string kelvin = "library/K.md";
        _box.Write(plain, "the one with a small k", Earlier);
        _box.Write(kelvin, "the one with the Kelvin sign", Earlier);
        _box.Write("library/other.md", "text", Earlier);
        if (oneWasSynced)
        {
            Synced(plain, "the one with a small k", Earlier);
        }

        // If these do not hold, the folder does not keep the two apart and the test proves nothing.
        Assert.Equal(SyncPath.Key(plain), SyncPath.Key(kelvin));
        Assert.Equal(3, _box.Files().Count);

        var view = Scan();

        Assert.Equal(["library/other.md"], view.Files.Keys);
        Assert.False(view.Knows(SyncPath.Key(plain)));
        Assert.True(view.Knows("library/other.md"));
        Assert.Equal(2, view.BadNames);
        Assert.Equal(0, view.Unreadable + view.Links + view.TooLarge);
    }

    [Fact]
    public void Every_file_after_the_second_with_the_same_key_is_counted_as_well_and_none_of_them_is_in_the_view()
    {
        // Two folders whose names are one key (a small k, and the Kelvin sign, which is
        // lower-cased to it), each with two files whose names are one key: four files, and one
        // key for all of them.
        string[] four = ["library/k/k.md", "library/k/K.md", "library/K/k.md", "library/K/K.md"];
        foreach (string path in four)
        {
            _box.Write(path, path, Earlier);
        }

        _box.Write("library/k/other.md", "text", Earlier);

        // If these do not hold, the folder does not keep the names apart and the test proves nothing.
        Assert.Single(four.Select(SyncPath.Key).Distinct());
        Assert.Equal(5, _box.Files().Count);

        var view = Scan();

        Assert.Equal(["library/k/other.md"], view.Files.Keys);
        Assert.False(view.Knows(SyncPath.Key(four[0])));
        Assert.Equal(4, view.BadNames);
        Assert.Equal(0, view.Unreadable + view.Links + view.TooLarge);
    }

    [Theory]
    [InlineData(false)]     // the file is met first, and the link second
    [InlineData(true)]      // the link is met first
    public void Of_two_files_whose_keys_are_equal_neither_is_in_the_view_when_one_is_a_link_where_this_machine_lets_a_test_make_one(bool linkFirst)
    {
        // In the order the scan goes by, the name with a small k comes before the one with the Kelvin sign.
        const string plain = "library/k.md";
        const string kelvin = "library/K.md";
        string link = linkFirst ? plain : kelvin;
        string file = linkFirst ? kelvin : plain;
        _box.Write(file, "the one that is a file", Earlier);
        _box.Write("library/other.md", "text", Earlier);
        string target = Path.Combine(_box.Root, "elsewhere.md");
        File.WriteAllText(target, "not ours");

        // Making a symbolic link to a file needs a right that most accounts do not have. Where
        // it is missing this test can show nothing, and says so by ending here.
        try
        {
            File.CreateSymbolicLink(_box.Full(link), target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Assert.NotNull(new FileInfo(_box.Full(link)).LinkTarget);
        Assert.Equal(SyncPath.Key(plain), SyncPath.Key(kelvin));
        Assert.Equal(3, _box.Files().Count);

        var view = Scan();

        // Neither is in the view and the key is unknown, whichever of the two is the link.
        Assert.Equal(["library/other.md"], view.Files.Keys);
        Assert.False(view.Knows(SyncPath.Key(plain)));
        Assert.True(view.Knows("library/other.md"));

        // A file that is met after another with its key is counted without being looked at,
        // and the first with it. A link that is met first is counted as the link it is.
        Assert.Equal(linkFirst ? 1 : 0, view.Links);
        Assert.Equal(linkFirst ? 1 : 2, view.BadNames);
        Assert.Equal(0, view.Unreadable + view.TooLarge);
        Assert.Equal("not ours", File.ReadAllText(target));
    }

    [Fact]
    public void A_file_with_the_size_and_time_of_the_record_is_unchanged_and_is_not_read()
    {
        _box.Write("library/a.md", "walrus", Earlier);
        Synced("library/a.md", "walrus", Earlier);

        // Held so that reading it would fail: the answer comes from its size and time alone.
        using var held = new FileStream(_box.Full("library/a.md"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var view = Scan();

        var file = view.Files["library/a.md"];
        Assert.Equal(LocalChange.Unchanged, file.Change);
        Assert.False(file.Touched);
        Assert.Equal(0, view.Unreadable);
    }

    [Fact]
    public void A_file_of_another_size_has_changed_and_is_not_read_either()
    {
        _box.Write("library/a.md", "walrus and otter", Earlier);
        Synced("library/a.md", "walrus", Earlier);

        using var held = new FileStream(_box.Full("library/a.md"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var view = Scan();

        Assert.Equal(LocalChange.Changed, view.Files["library/a.md"].Change);
    }

    [Fact]
    public void A_file_written_again_with_the_same_content_is_unchanged_and_says_it_was_touched()
    {
        _box.Write("library/a.md", "walrus", Now);
        Synced("library/a.md", "walrus", Earlier);

        var file = Scan().Files["library/a.md"];

        Assert.Equal(LocalChange.Unchanged, file.Change);
        Assert.True(file.Touched);
        Assert.Equal(new LocalStat(6, Now), file.Stat);
    }

    [Fact]
    public void A_file_of_the_same_size_and_other_content_has_changed()
    {
        _box.Write("library/a.md", "walruz", Now);
        Synced("library/a.md", "walrus", Earlier);

        var file = Scan().Files["library/a.md"];

        Assert.Equal(LocalChange.Changed, file.Change);
        Assert.False(file.Touched);
    }

    [Fact]
    public void A_file_the_record_knows_keeps_the_spelling_the_record_has()
    {
        _box.Write("Library/Snippets/SIG.md", "walrus", Earlier);
        Synced("library/snippets/sig.md", "walrus", Earlier);

        var file = Assert.Single(Scan().Files);

        Assert.Equal("library/snippets/sig.md", file.Key);
        Assert.Equal("library/snippets/sig.md", file.Value.Path);
    }

    [Fact]
    public void A_file_at_a_path_the_record_knows_only_as_deleted_is_new()
    {
        _box.Write("library/a.md", "back again", Now);
        _state.Set(new SyncedFile("library/a.md", Blob, SyncedKind.Tombstone, "", 0, Earlier, 141, Earlier));

        Assert.Equal(LocalChange.New, Scan().Files["library/a.md"].Change);
    }

    [Fact]
    public void A_file_that_is_too_large_is_counted_and_unknown_and_never_missing()
    {
        using (var large = new FileStream(_box.Full("big.bin"), FileMode.CreateNew))
        {
            large.SetLength(SyncLimits.MaxFileBytes + 1);
        }

        using (var justFits = new FileStream(_box.Full("fits.bin"), FileMode.CreateNew))
        {
            justFits.SetLength(SyncLimits.MaxFileBytes);
        }

        var view = Scan();

        Assert.Equal(1, view.TooLarge);
        Assert.Equal(["fits.bin"], view.Files.Keys);
        Assert.False(view.Knows("big.bin"));
        Assert.True(view.Knows("fits.bin"));
    }

    [Fact]
    public void A_file_that_has_to_be_read_and_cannot_be_is_unknown_and_never_changed_or_missing()
    {
        _box.Write("library/a.md", "walrus", Now);
        Synced("library/a.md", "walrus", Earlier);

        using var held = new FileStream(_box.Full("library/a.md"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var view = Scan();

        Assert.Empty(view.Files);
        Assert.Equal(1, view.Unreadable);
        Assert.Equal(0, view.Links);
        Assert.False(view.Knows("library/a.md"));
        Assert.True(view.Knows("library/b.md"));
    }

    [Fact]
    public void A_folder_that_is_a_link_is_not_followed_and_everything_under_it_is_unknown()
    {
        string elsewhere = Path.Combine(_box.Root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "theirs.md"), "not ours");
        _box.Junction(_box.Full("library/Linked"), elsewhere);
        _box.Write("library/a.md", "text");

        var view = Scan();

        Assert.Equal(["library/a.md"], view.Files.Keys);
        Assert.Equal(1, view.Links);
        Assert.Equal(0, view.Unreadable);
        Assert.False(view.Knows("library/linked/theirs.md"));
        Assert.False(view.Knows("library/linked/deeper/x.md"));
        Assert.True(view.Knows("library/linked-not.md"));
        Assert.True(view.Knows("library/a.md"));
    }

    [Fact]
    public void A_folder_that_cannot_be_listed_is_unknown_with_everything_under_it_and_nothing_of_it_is_reported_deleted()
    {
        _box.Write("library/locked/a.md", "text", Earlier);
        _box.Write("library/locked/deeper/b.md", "text", Earlier);
        _box.Write("library/open/c.md", "text", Earlier);
        Synced("library/locked/a.md", "text", Earlier);
        Synced("library/locked/deeper/b.md", "text", Earlier, blob: new string('c', 64));
        Synced("library/open/c.md", "text", Earlier, blob: new string('d', 64));
        Synced("library/open/gone.md", "text", Earlier, blob: new string('b', 64));
        string locked = _box.Full("library/locked");

        _box.DenyListing(locked);
        LocalView view;
        try
        {
            view = Scan();
        }
        finally
        {
            _box.Allow(locked);
        }

        // What the record holds, that this run knows about and has no file for, is reported deleted.
        var deleted = _state.Files.Keys.Where(key => view.Knows(key) && !view.Files.ContainsKey(key));
        Assert.Equal(["library/open/gone.md"], deleted);
        Assert.Equal(["library/open/c.md"], view.Files.Keys);
        Assert.False(view.Knows("library/locked/a.md"));
        Assert.False(view.Knows("library/locked/deeper/b.md"));
        Assert.False(view.Knows("library/locked/not-yet-seen.md"));
        Assert.True(view.Knows("library/open/gone.md"));
        Assert.True(view.Knows("library/locked-not.md"));
        Assert.Equal(1, view.Unreadable);
        Assert.Equal(0, view.Links);
    }

    [Fact]
    public void A_data_folder_that_is_there_and_cannot_be_listed_gives_no_view_at_all()
    {
        _box.Write("library/a.md", "text", Earlier);
        Synced("library/a.md", "text", Earlier);

        _box.DenyListing(_box.Data);
        try
        {
            Assert.Null(LocalScan.Run(_box.Data, _state, Now));
        }
        finally
        {
            _box.Allow(_box.Data);
        }

        Assert.NotNull(LocalScan.Run(_box.Data, _state, Now));
    }

    [Fact]
    public void Links_are_counted_apart_from_what_failed_this_time()
    {
        string elsewhere = Path.Combine(_box.Root, "elsewhere");
        _box.Junction(_box.Full("library/linked"), elsewhere);
        _box.Write("library/locked/a.md", "text");
        _box.Write("library/held.md", "walrus", Now);
        Synced("library/held.md", "walrus", Earlier);
        string locked = _box.Full("library/locked");

        // The held file has another time than the record's, so that it has to be read, and cannot be.
        using var held = new FileStream(_box.Full("library/held.md"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        _box.DenyListing(locked);
        LocalView view;
        try
        {
            view = Scan();
        }
        finally
        {
            _box.Allow(locked);
        }

        Assert.Equal(1, view.Links);
        Assert.Equal(2, view.Unreadable);
        Assert.False(view.Knows("library/linked/x.md"));
        Assert.False(view.Knows("library/locked/a.md"));
        Assert.False(view.Knows("library/held.md"));
    }

    [Fact]
    public void A_leftover_temporary_file_of_sync_is_removed_and_any_other_temporary_file_is_left()
    {
        _box.Write("library/.~wyvern-sync-0123456789abcdef.tmp", "half a file", Now.AddHours(-2), created: Now.AddHours(-2));
        _box.Write("library/.~wyvern-sync-fedcba9876543210.tmp", "being written", Now.AddMinutes(-1), created: Now.AddMinutes(-1));
        _box.Write("clipboard/OTTER-PC/2026-10-03.jsonl.tmp", "the store's own", Now.AddDays(-9), created: Now.AddDays(-9));

        var view = Scan();

        Assert.Equal(1, view.LeftoversRemoved);
        Assert.Equal(["clipboard/OTTER-PC/2026-10-03.jsonl.tmp", "library/.~wyvern-sync-fedcba9876543210.tmp"], _box.Files());
        Assert.Empty(view.Files);
    }

    [Fact]
    public void A_file_prints_its_change_and_not_its_path()
    {
        Assert.Equal("LocalFile { Change = New }", new LocalFile("library/secret-otter.md", new LocalStat(1, Now), LocalChange.New).ToString());
    }
}
