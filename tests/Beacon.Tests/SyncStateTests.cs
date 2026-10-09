using Beacon.Core.Sync;

namespace Beacon.Tests;

/// <summary>The record of the last sync, sync-state.json. Every path and blob name is made up.</summary>
public sealed class SyncStateTests : IDisposable
{
    private const string Scope = "scope-of-the-otter-server";

    private static readonly string BlobA = new('a', 64);
    private static readonly string BlobB = new('b', 64);
    private static readonly string BlobC = new('c', 64);
    private static readonly string Hash = new('1', 64);
    private static readonly DateTimeOffset Local = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567);
    private static readonly DateTimeOffset Remote = new(2026, 10, 3, 12, 0, 5, TimeSpan.Zero);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string StateFile => _dir.File("sync-state.json");

    private static SyncedFile Live(string path, string blob) => new(path, blob, SyncedKind.Live, Hash, 7, Local, 213, Remote);

    private SyncState Reload(out SyncStateLoad how, out int skipped, string scope = Scope) => SyncState.Load(StateFile, scope, out how, out skipped);

    private void WriteRecord(string files, string bad = "") =>
        File.WriteAllText(StateFile, $$"""{"version":1,"scope":"{{Scope}}","files":[{{files}}],"bad":[{{bad}}]}""");

    private static string Entry(string path = "library/a.md", string? blob = null, string kind = "live", string? sha256 = null, string size = "7", string mtime = "2026-10-03T12:00:00.0000000Z") =>
        $$"""{"path":"{{path}}","blob":"{{blob ?? BlobA}}","kind":"{{kind}}","sha256":"{{sha256 ?? Hash}}","size":{{size}},"mtimeUtc":"{{mtime}}","remoteSize":213,"remoteMtimeUtc":"2026-10-03T12:00:05.0000000Z"}""";

    [Fact]
    public void A_new_record_is_empty_and_has_nothing_to_write()
    {
        var state = new SyncState(Scope);

        Assert.Empty(state.Files);
        Assert.Empty(state.Bad);
        Assert.False(state.Dirty);
        Assert.Equal(Scope, state.Scope);
    }

    [Fact]
    public void A_path_is_found_by_its_key()
    {
        var state = new SyncState(Scope);
        var file = Live("Library/Snippets/Sig.md", BlobA);

        state.Set(file);

        Assert.True(state.Dirty);
        Assert.Same(file, state.Find("library/snippets/sig.md"));
        Assert.Null(state.Find("Library/Snippets/Sig.md"));
        Assert.Null(state.Find("library/snippets/other.md"));
    }

    [Fact]
    public void Entering_a_path_again_replaces_what_was_said_about_it()
    {
        var state = new SyncState(Scope);
        state.Set(Live("library/a.md", BlobA));

        var tombstone = new SyncedFile("library/a.md", BlobB, SyncedKind.Tombstone, "", 0, Local, 141, Remote);
        state.Set(tombstone);

        Assert.Same(tombstone, Assert.Single(state.Files).Value);
        Assert.Same(tombstone, state.Find("library/a.md"));
    }

    [Fact]
    public void Entering_the_same_thing_again_leaves_nothing_to_write()
    {
        var state = new SyncState(Scope);
        state.Set(Live("library/a.md", BlobA));
        state.Save(StateFile);

        state.Set(Live("library/a.md", BlobA));

        Assert.False(state.Dirty);
    }

    [Fact]
    public void A_path_that_is_taken_out_is_gone()
    {
        var state = new SyncState(Scope);
        state.Set(Live("library/a.md", BlobA));

        state.Remove("library/a.md");
        state.Remove("library/never-there.md");

        Assert.Empty(state.Files);
        Assert.Null(state.Find("library/a.md"));
    }

    [Fact]
    public void A_bad_blob_is_remembered_as_the_server_described_it_until_it_is_forgotten_by_name()
    {
        var state = new SyncState(Scope);
        state.SetBad(new RemoteBlob(BlobA, 99, Remote));
        state.SetBad(new RemoteBlob(BlobB, 50, Remote));

        state.RemoveBad(BlobA);
        state.RemoveBad(BlobC);

        Assert.Equal(new RemoteBlob(BlobB, 50, Remote), Assert.Single(state.Bad).Value);
    }

    [Fact]
    public void Entering_a_path_does_not_forget_that_its_blob_could_not_be_used()
    {
        // The path's entry says how the file stood when it was last synced. That its blob has
        // since become one that cannot be used is another fact, and is kept beside it.
        var state = new SyncState(Scope);
        state.Set(Live("library/a.md", BlobA));
        state.SetBad(new RemoteBlob(BlobA, 99, Remote));

        // Entered again as a run does for a file written again with the same content, and as new.
        state.Set(Live("library/a.md", BlobA) with { MtimeUtc = Local.AddHours(1) });
        state.SetBad(new RemoteBlob(BlobB, 50, Remote));
        state.Set(Live("library/b.md", BlobB));

        Assert.Equal([BlobA, BlobB], state.Bad.Keys.Order());
        Assert.Equal(new RemoteBlob(BlobA, 99, Remote), state.Bad[BlobA]);
    }

    [Fact]
    public void A_blob_that_could_not_be_used_is_kept_beside_the_entry_of_its_path_when_the_record_is_read()
    {
        var state = new SyncState(Scope);
        state.Set(Live("library/a.md", BlobA));
        state.SetBad(new RemoteBlob(BlobA, 99, Remote));
        state.SetBad(new RemoteBlob(BlobB, 50, Remote));

        state.Save(StateFile);
        var loaded = Reload(out var how, out int skipped);

        Assert.Equal(SyncStateLoad.Loaded, how);
        Assert.Equal(0, skipped);
        Assert.Equal(Live("library/a.md", BlobA), loaded.Find("library/a.md"));
        Assert.Equal([BlobA, BlobB], loaded.Bad.Keys.Order());
        Assert.Equal(new RemoteBlob(BlobA, 99, Remote), loaded.Bad[BlobA]);
        Assert.False(loaded.Dirty);
    }

    [Fact]
    public void What_was_saved_is_what_is_loaded_to_the_tick()
    {
        var state = new SyncState(Scope);
        var live = Live("library/snippets/R\u00E9sum\u00E9.md", BlobA);
        var tombstone = new SyncedFile("library/gone.md", BlobB, SyncedKind.Tombstone, "", 0, Local.AddDays(-1), 141, Remote);
        var ignored = new SyncedFile("clipboard/SEAL-PC/2026-01-01.jsonl", BlobC, SyncedKind.Ignored, "", 0, default, 500, Remote);
        state.Set(live);
        state.Set(tombstone);
        state.Set(ignored);
        state.SetBad(new RemoteBlob(new string('d', 64), 99, Remote));

        state.Save(StateFile);
        var loaded = Reload(out var how, out int skipped);

        Assert.Equal(SyncStateLoad.Loaded, how);
        Assert.Equal(0, skipped);
        Assert.False(state.Dirty);
        Assert.False(loaded.Dirty);
        Assert.Equal(live, loaded.Find(SyncPath.Key(live.Path)));
        Assert.Equal(tombstone, loaded.Find("library/gone.md"));
        Assert.Equal(ignored, loaded.Find(SyncPath.Key(ignored.Path)));
        Assert.Equal(new RemoteBlob(new string('d', 64), 99, Remote), Assert.Single(loaded.Bad).Value);
        Assert.False(File.Exists(StateFile + ".tmp"));
    }

    [Fact]
    public void No_file_is_a_first_sync()
    {
        var loaded = Reload(out var how, out _);

        Assert.Equal(SyncStateLoad.Missing, how);
        Assert.Empty(loaded.Files);
    }

    [Fact]
    public void A_record_made_for_another_scope_is_not_used()
    {
        var state = new SyncState(Scope);
        state.Set(Live("library/a.md", BlobA));
        state.Save(StateFile);

        var loaded = Reload(out var how, out _, scope: "scope-of-another-server");

        Assert.Equal(SyncStateLoad.OtherScope, how);
        Assert.Empty(loaded.Files);
        Assert.Equal("scope-of-another-server", loaded.Scope);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"version":2,"scope":"scope-of-the-otter-server","files":[]}""")]
    [InlineData("""{"scope":"scope-of-the-otter-server","files":[]}""")]
    [InlineData("""{"version":1,"files":[]}""")]
    [InlineData("""{"version":1,"scope":7,"files":[]}""")]
    public void A_record_that_cannot_be_understood_is_not_used(string content)
    {
        File.WriteAllText(StateFile, content);

        var loaded = Reload(out var how, out _);

        Assert.Equal(SyncStateLoad.Unreadable, how);
        Assert.Empty(loaded.Files);
        Assert.False(loaded.Dirty);
    }

    [Fact]
    public void A_record_that_another_program_holds_is_unreadable_and_not_missing()
    {
        new SyncState(Scope).Save(StateFile);
        using var held = new FileStream(StateFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Reload(out var how, out _);

        Assert.Equal(SyncStateLoad.Unreadable, how);
    }

    [Theory]
    [InlineData("library/x.md", "../../outside.md")]                    // a path that leaves the data folder
    [InlineData("library/x.md", "C:/Windows/a.md")]                     // a path with a drive
    [InlineData("library/x.md", "clipboard/PC/.~lock")]                 // a file that is never synced
    [InlineData("aaaaaaaa", "not-hex-")]                                // a blob name that is not one
    [InlineData("\"kind\":\"live\"", "\"kind\":\"perhaps\"")]           // a kind this code does not know
    [InlineData("\"kind\":\"live\"", "\"kind\":\"tombstone\"")]         // a tombstone that carries a hash
    [InlineData("11111111", "1111111")]                                 // a hash that is too short
    [InlineData("\"size\":7", "\"size\":-1")]                           // a size below nothing
    [InlineData("\"size\":7", "\"size\":\"7\"")]                        // a size that is text
    [InlineData("2026-10-03T12:00:00.0000000Z", "yesterday")]           // a time that is not one
    [InlineData("\"remoteSize\":213,", "")]                             // a value that is missing
    public void An_entry_that_this_code_would_not_have_written_is_left_out_and_the_rest_is_used(string good, string bad)
    {
        string damaged = Entry("library/x.md").Replace(good, bad);
        Assert.NotEqual(Entry("library/x.md"), damaged);
        WriteRecord(damaged + "," + Entry("library/kept.md", BlobB));

        var loaded = Reload(out var how, out int skipped);

        Assert.Equal(SyncStateLoad.Loaded, how);
        Assert.Equal(1, skipped);
        Assert.Equal("library/kept.md", Assert.Single(loaded.Files).Value.Path);
    }

    [Fact]
    public void An_entry_that_is_not_an_object_is_left_out()
    {
        WriteRecord("7," + Entry("library/kept.md", BlobB) + ",null");

        var loaded = Reload(out _, out int skipped);

        Assert.Equal(2, skipped);
        Assert.Single(loaded.Files);
    }

    [Fact]
    public void A_path_or_a_blob_that_is_entered_twice_counts_the_first_time()
    {
        WriteRecord(Entry("library/a.md", BlobA) + "," + Entry("Library/A.md", BlobB) + "," + Entry("library/b.md", BlobA));

        var loaded = Reload(out _, out int skipped);

        Assert.Equal(2, skipped);
        Assert.Equal(BlobA, Assert.Single(loaded.Files).Value.Blob);
    }

    [Fact]
    public void A_bad_blob_entry_that_cannot_be_understood_is_left_out()
    {
        WriteRecord(Entry(), bad: """{"blob":"nope","size":1,"mtimeUtc":"2026-10-03T12:00:05.0000000Z"},{"blob":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","size":50,"mtimeUtc":"2026-10-03T12:00:05.0000000Z"}""");

        var loaded = Reload(out _, out int skipped);

        Assert.Equal(1, skipped);
        Assert.Equal(BlobB, Assert.Single(loaded.Bad).Key);
    }

    [Fact]
    public void A_save_that_fails_leaves_the_file_that_was_there_and_the_record_still_to_be_written()
    {
        var state = new SyncState(Scope);
        state.Set(Live("library/a.md", BlobA));
        state.Save(StateFile);
        state.Set(Live("library/b.md", BlobB));

        // The temporary file's name is taken by a folder, so the write cannot start.
        Directory.CreateDirectory(StateFile + ".tmp");
        Assert.ThrowsAny<Exception>(() => state.Save(StateFile));

        Assert.True(state.Dirty);
        Directory.Delete(StateFile + ".tmp");
        Assert.Single(Reload(out _, out _).Files);
    }

    [Fact]
    public void The_scope_is_the_same_for_the_same_server_and_data_folder_however_the_folder_is_written()
    {
        string scope = SyncState.ScopeOf("remote-one", @"D:\Beacon\data", "folder-id-one");

        // Sixty-four lower-case hex digits: a hash, so that no input is in it as text.
        Assert.Matches(@"\A[0-9a-f]{64}\z", scope);
        Assert.Equal(scope, SyncState.ScopeOf("remote-one", @"d:\beacon\DATA\", "folder-id-one"));

        // Every input counts, down to one character of it.
        Assert.NotEqual(scope, SyncState.ScopeOf("remote-one", @"D:\Beacon\data2", "folder-id-one"));
        Assert.NotEqual(scope, SyncState.ScopeOf("remote-one", @"D:\Beacon\datb", "folder-id-one"));
        Assert.NotEqual(scope, SyncState.ScopeOf("remote-two", @"D:\Beacon\data", "folder-id-one"));
        Assert.NotEqual(scope, SyncState.ScopeOf("remote-onf", @"D:\Beacon\data", "folder-id-one"));
        Assert.NotEqual(scope, SyncState.ScopeOf("remote-one", @"D:\Beacon\data", "folder-id-two"));
        Assert.NotEqual(scope, SyncState.ScopeOf("remote-one", @"D:\Beacon\data", "folder-id-onf"));
        Assert.Matches(@"\A[0-9a-f]{64}\z", SyncState.ScopeOf("remote-two", @"E:\other\folder", "folder-id-one"));
    }

    [Fact]
    public void A_record_file_over_the_size_bound_is_not_used_though_it_is_a_record_that_could_be_read()
    {
        // A whole, valid record for this scope. The only thing against it is its size.
        var state = new SyncState(Scope);
        state.Set(Live("library/a.md", BlobA));
        state.SetBad(new RemoteBlob(BlobB, 50, Remote));
        state.Save(StateFile);
        long size = new FileInfo(StateFile).Length;

        // A few bytes over the bound, and one byte over it.
        foreach (long bound in new[] { size - 3, size - 1 })
        {
            var over = SyncState.Load(StateFile, Scope, out var how, out int skipped, maxBytes: bound);

            Assert.Equal(SyncStateLoad.Unreadable, how);
            Assert.Equal(0, skipped);
            Assert.Empty(over.Files);
            Assert.Empty(over.Bad);
            Assert.False(over.Dirty);
            Assert.Equal(Scope, over.Scope);
        }

        // Exactly at the bound it is read, and so it is under the bound that is used when none is given.
        var at = SyncState.Load(StateFile, Scope, out var read, out _, maxBytes: size);
        Assert.Equal(SyncStateLoad.Loaded, read);
        Assert.Single(at.Files);
        Assert.Single(at.Bad);
        Assert.Single(Reload(out read, out _).Files);
        Assert.Equal(SyncStateLoad.Loaded, read);
        Assert.Equal(512L * 1024 * 1024, SyncState.MaxFileBytes);
    }

    [Fact]
    public void An_entry_prints_its_kind_and_not_its_path()
    {
        Assert.Equal("SyncedFile { Kind = Live }", Live("library/secret-otter.md", BlobA).ToString());
    }
}
