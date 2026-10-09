using System.Diagnostics;
using Wyvern.Core.Sync;

namespace Wyvern.Tests;

/// <summary>
/// The parts of the SFTP store that can be run without a server: reading a key file, which
/// happens on this machine, the rule for one entry of a listing, which needs no listing, and
/// the refusals that come before any connection is made. No
/// test here connects to anything, and each one checks that the count of connections stayed
/// where it was. The user is made up. The server is this machine's own loopback address, on a
/// port nothing listens on: should the code under test ever be broken so that it does try to
/// connect, no name is looked up and no packet leaves the machine.
/// </summary>
/// <remarks>
/// The two private keys below were made with <c>ssh-keygen</c> for this test alone. They are
/// in no <c>authorized_keys</c> file and open nothing.
/// </remarks>
public sealed class SftpLocalTests : IDisposable
{
    private const string Fingerprint = "SHA256:AjAMBCdFlnzhT4An+L756BKl4RXyRER/GQvIadZqNKs";

    private const string PlainKey =
        """
        -----BEGIN OPENSSH PRIVATE KEY-----
        b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gtZW
        QyNTUxOQAAACBVM3CIpDA88N5DQek0DOuaGh+Es2cCDvGyZpMdqnl9mgAAAKhnosfuZ6LH
        7gAAAAtzc2gtZWQyNTUxOQAAACBVM3CIpDA88N5DQek0DOuaGh+Es2cCDvGyZpMdqnl9mg
        AAAECeR+n0pfMPYmWjW6PG2zu7ORp6AlFm4mPuN2KV82p9MlUzcIikMDzw3kNB6TQM65oa
        H4SzZwIO8bJmkx2qeX2aAAAAIWJlYWNvbi10ZXN0LWtleS1ub3QtdXNlZC1hbnl3aGVyZQ
        ECAwQ=
        -----END OPENSSH PRIVATE KEY-----

        """;

    // The same kind of key, protected with the passphrase below.
    private const string LockedKey =
        """
        -----BEGIN OPENSSH PRIVATE KEY-----
        b3BlbnNzaC1rZXktdjEAAAAACmFlczI1Ni1jdHIAAAAGYmNyeXB0AAAAGAAAABAjjewQP9
        ro/Br/h0e2HthQAAAAGAAAAAEAAAAzAAAAC3NzaC1lZDI1NTE5AAAAIEWFgPi2IExR39/l
        MHniEBETP9xXD0JeX4p4RH2HTHPqAAAAsEcXstfJsC+aaULqmUf+IG7KLooYbkW8sgxNWA
        koJqrvocrWdZUECOMMGNhdm6Soq/tYwU30tq1LzKliunT/jGJhwUzNZ5nxkzUIw47lFenU
        Taq1Dv9XOf8BbDbglWRZIpUjEbdskpYETSyJ0MZUa9B5QUwH0b5ZwytSxz8d4egjc4/eby
        zjPwojnMpkOSuRESKyDaxylZs2AkN36R6luCulmGs0BFKueJSPK0bmAc6j
        -----END OPENSSH PRIVATE KEY-----

        """;

    private const string LockedKeyPassphrase = "walrus-tusk";

    private readonly TempDir _dir = new();
    private readonly int _connectionsBefore = SftpRemoteStore.ConnectionsOpened;

    public void Dispose()
    {
        // Whatever a test did, it opened no connection.
        Assert.Equal(_connectionsBefore, SftpRemoteStore.ConnectionsOpened);
        _dir.Dispose();
    }

    private static IRemoteConnector Connector => SftpRemoteStore.Connector;

    private string KeyFile(string content, string name = "test_ed25519")
    {
        string file = _dir.File(name);
        File.WriteAllText(file, content.ReplaceLineEndings("\n"));
        return file;
    }

    private SyncConfig Config(string keyFile, string? fingerprint)
    {
        Assert.True(SyncConfig.TryCreate("127.0.0.1", "1", "walrus", keyFile, "/home/walrus/wyvern", fingerprint, out var config, out string problem), problem);
        return config!;
    }

    /// <summary>
    /// Makes a real junction. It needs no special privilege, and it is the one program a test of
    /// this class starts: cmd.exe from the Windows folder. The link and the target are both
    /// inside the test's own temporary folder.
    /// </summary>
    private static void Junction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);

        // The rule the code goes by. If this is null for a junction, the test of a link proves nothing.
        Assert.NotNull(new DirectoryInfo(link).LinkTarget);
    }

    [Fact]
    public void A_key_file_without_a_passphrase_can_be_used()
    {
        Assert.Equal(KeyFileState.Usable, Connector.CheckKeyFile(KeyFile(PlainKey), null));
        Assert.Equal(KeyFileState.Usable, Connector.CheckKeyFile(KeyFile(PlainKey), ""));
    }

    [Fact]
    public void A_key_file_with_a_passphrase_says_that_it_needs_one()
    {
        Assert.Equal(KeyFileState.NeedsPassphrase, Connector.CheckKeyFile(KeyFile(LockedKey), null));
        Assert.Equal(KeyFileState.NeedsPassphrase, Connector.CheckKeyFile(KeyFile(LockedKey), ""));
    }

    [Fact]
    public void A_key_file_with_a_passphrase_can_be_used_with_it()
    {
        Assert.Equal(KeyFileState.Usable, Connector.CheckKeyFile(KeyFile(LockedKey), LockedKeyPassphrase));
    }

    [Fact]
    public void The_wrong_passphrase_is_told_apart_from_a_key_that_needs_one()
    {
        Assert.Equal(KeyFileState.WrongPassphrase, Connector.CheckKeyFile(KeyFile(LockedKey), "seal-flipper"));
    }

    [Fact]
    public void A_file_that_is_not_a_private_key_is_unreadable()
    {
        Assert.Equal(KeyFileState.Unreadable, Connector.CheckKeyFile(KeyFile("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIFUzcIikMDzw3kNB6TQM65oaH4SzZwIO8bJmkx2qeX2a otter\n"), null));
        Assert.Equal(KeyFileState.Unreadable, Connector.CheckKeyFile(KeyFile("walrus"), null));
        Assert.Equal(KeyFileState.Unreadable, Connector.CheckKeyFile(KeyFile(""), null));
    }

    [Fact]
    public void A_key_file_that_is_not_there_or_is_a_folder_or_is_held_is_unreadable()
    {
        string held = KeyFile(PlainKey, "held");
        using var holder = new FileStream(held, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(KeyFileState.Unreadable, Connector.CheckKeyFile(_dir.File("nothing-here"), null));
        Assert.Equal(KeyFileState.Unreadable, Connector.CheckKeyFile(_dir.Path, null));
        Assert.Equal(KeyFileState.Unreadable, Connector.CheckKeyFile(held, null));
    }

    [Fact]
    public void A_file_far_too_large_to_be_a_key_is_not_read()
    {
        string file = _dir.File("large");
        File.WriteAllBytes(file, new byte[1024 * 1024 + 1]);

        Assert.Equal(KeyFileState.Unreadable, Connector.CheckKeyFile(file, null));
    }

    [Fact]
    public void A_key_path_that_is_a_link_is_refused_as_a_link_before_anything_is_opened_through_it()
    {
        // A link that is a file needs a right this test does not have. A junction needs none,
        // and it is a link by the same rule: what is at the key file's path is a link, and it
        // leads to a folder elsewhere that holds a key that could be used.
        string elsewhere = _dir.File("elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "test_ed25519"), PlainKey.ReplaceLineEndings("\n"));
        string link = _dir.File("linked_key");
        Junction(link, elsewhere);
        try
        {
            var refused = Assert.Throws<RemoteStoreException>(() => Connector.Open(Config(link, Fingerprint), null));

            // The fixed word, and not what opening it would have said: it was never opened.
            Assert.Equal(RemoteFailure.KeyFile, refused.Failure);
            Assert.Equal("link", refused.Cause);
            Assert.Equal(KeyFileState.Unreadable, Connector.CheckKeyFile(link, null));
            Assert.Equal(KeyFileState.Unreadable, Connector.CheckKeyFile(link, LockedKeyPassphrase));
        }
        finally
        {
            // The link alone, so that taking the test's folder away never goes through it.
            Directory.Delete(link);
        }
    }

    [Fact]
    public void A_key_file_in_a_folder_that_is_a_link_is_refused_as_a_link_and_nothing_behind_the_link_is_looked_at()
    {
        // A folder on the way to the key file is a junction. Behind it lie a key that could be
        // used, and another one a folder further down. Link and target are both in this test's
        // own temporary folder.
        string elsewhere = _dir.File("elsewhere");
        Directory.CreateDirectory(Path.Combine(elsewhere, "deeper"));
        File.WriteAllText(Path.Combine(elsewhere, "test_ed25519"), PlainKey.ReplaceLineEndings("\n"));
        File.WriteAllText(Path.Combine(elsewhere, "deeper", "test_ed25519"), PlainKey.ReplaceLineEndings("\n"));
        string link = _dir.File("linked_folder");
        Junction(link, elsewhere);
        try
        {
            // First of all, a name that nothing has behind the link. It is refused for the link,
            // and not as a file that is not there: nobody looked behind the link to find that out.
            var missing = Assert.Throws<RemoteStoreException>(() => Connector.Open(Config(Path.Combine(link, "nothing-here"), Fingerprint), null));
            Assert.Equal(RemoteFailure.KeyFile, missing.Failure);
            Assert.Equal("link", missing.Cause);

            // The keys behind the link are not read, directly behind it or deeper, with either
            // separator, and whatever passphrase is given.
            foreach (string key in new[]
            {
                Path.Combine(link, "test_ed25519"),
                Path.Combine(link, "deeper", "test_ed25519"),
                link + "/test_ed25519",
                Path.Combine(elsewhere, "..", "linked_folder", "test_ed25519"),
            })
            {
                Assert.Equal(KeyFileState.Unreadable, Connector.CheckKeyFile(key, null));
                Assert.Equal(KeyFileState.Unreadable, Connector.CheckKeyFile(key, LockedKeyPassphrase));
                var refused = Assert.Throws<RemoteStoreException>(() => Connector.Open(Config(key, Fingerprint), null));
                Assert.Equal(RemoteFailure.KeyFile, refused.Failure);
                Assert.Equal("link", refused.Cause);
            }

            // It is the link that is refused, and nothing else about these files: the same key
            // by its own path can be used, and so can a path that only names the link and steps
            // back out of it, which Windows never goes through.
            Assert.Equal(KeyFileState.Usable, Connector.CheckKeyFile(Path.Combine(elsewhere, "test_ed25519"), null));
            Assert.Equal(KeyFileState.Usable, Connector.CheckKeyFile(Path.Combine(elsewhere, "deeper", "test_ed25519"), null));
            Assert.Equal(KeyFileState.Usable, Connector.CheckKeyFile(Path.Combine(link, "..", "elsewhere", "test_ed25519"), null));
        }
        finally
        {
            // The link alone, so that taking the test's folder away never goes through it.
            Directory.Delete(link);
        }
    }

    // The paths of the next two tests are text and stay text: none is given to a file API.

    [Theory]
    [InlineData(@"C:\Users\otter\.ssh\id_ed25519", new[] { @"C:\Users", @"C:\Users\otter", @"C:\Users\otter\.ssh" })]
    [InlineData(@"c:/Users/otter\id_ed25519", new[] { @"c:\Users", @"c:\Users\otter" })]
    [InlineData(@"C:\id_ed25519", new string[0])]
    [InlineData(@"C:\", new string[0])]
    [InlineData(@"C:\\keys\.\\deep\id_ed25519", new[] { @"C:\keys", @"C:\keys\deep" })]
    [InlineData(@"C:\keys\..\other\id_ed25519", new[] { @"C:\other" })]
    [InlineData(@"C:\..\..\keys\id_ed25519", new[] { @"C:\keys" })]
    [InlineData(@"C:\keys\deep\..\..\id_ed25519", new string[0])]
    [InlineData(@"C:\keys. \..\other\id_ed25519", new[] { @"C:\other" })]
    [InlineData(@"C:\keys\id_ed25519. ", new[] { @"C:\keys" })]
    [InlineData(@"C:\keys\id_ed25519:stream", new[] { @"C:\keys" })]
    [InlineData(@"C:\.ssh\...keys\id", new[] { @"C:\.ssh", @"C:\.ssh\...keys" })]
    public void The_folders_on_the_way_to_a_key_file_are_worked_out_from_the_text_outermost_first(string path, string[] folders)
    {
        // The order matters: each folder is looked at only after every folder above it was
        // found to be no link, so that no look goes through one.
        Assert.Equal(folders, SftpRemoteStore.FoldersOnTheWay(path));
    }

    [Theory]
    [InlineData(@"C:\keys.\id_ed25519")]
    [InlineData(@"C:\keys..\id_ed25519")]
    [InlineData(@"C:\keys \id_ed25519")]
    [InlineData(@"C:\keys\...\id_ed25519")]
    [InlineData(@"C:\keys\.. \id_ed25519")]
    [InlineData(@"C:\keys\Z:\id_ed25519")]
    [InlineData(@"C:\keys\Z:deep\id_ed25519")]
    [InlineData(@"C:\keys\deep:stream\id_ed25519")]
    [InlineData(@"C:id_ed25519")]
    [InlineData(@"C:")]
    [InlineData(@"id_ed25519")]
    [InlineData(@"\keys\id_ed25519")]
    [InlineData(@"1:\keys\id_ed25519")]
    [InlineData("")]
    public void A_path_whose_folders_cannot_be_told_with_certainty_from_its_text_has_none_to_look_at_and_is_refused(string path)
    {
        Assert.Null(SftpRemoteStore.FoldersOnTheWay(path));
    }

    [Theory]
    [InlineData("keys.")]
    [InlineData("keys..")]
    [InlineData("keys ")]
    public void A_folder_on_the_way_whose_name_ends_in_a_period_or_a_space_cannot_be_told_from_a_link_and_is_refused(string written)
    {
        // Windows reads such a name differently at the end of a path than in the middle of one,
        // so the folder that would be looked at is not surely the folder the file is opened
        // through. The plain folder here holds a key that could be used.
        string keys = _dir.File("keys");
        Directory.CreateDirectory(keys);
        File.WriteAllText(Path.Combine(keys, "test_ed25519"), PlainKey.ReplaceLineEndings("\n"));
        string folder = _dir.File(written);

        var missing = Assert.Throws<RemoteStoreException>(() => Connector.Open(Config(Path.Combine(folder, "nothing-here"), Fingerprint), null));
        Assert.Equal("link", missing.Cause);

        string key = Path.Combine(folder, "test_ed25519");
        Assert.Equal(KeyFileState.Unreadable, Connector.CheckKeyFile(key, null));
        Assert.Equal("link", Assert.Throws<RemoteStoreException>(() => Connector.Open(Config(key, Fingerprint), null)).Cause);
        Assert.Equal(KeyFileState.Usable, Connector.CheckKeyFile(Path.Combine(keys, "test_ed25519"), null));
    }

    [Fact]
    public void A_key_file_that_is_marked_as_not_being_on_this_disk_is_refused_as_a_placeholder_before_it_is_opened()
    {
        // What a sync client's placeholder carries. The mark can be set on any file, and on a
        // file that really is on the disk it changes nothing but the answer to this question.
        string file = KeyFile(PlainKey);
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.Offline);
        try
        {
            // If the mark did not take on this disk, the test would prove nothing.
            Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.Offline));

            Assert.Equal(KeyFileState.Unreadable, Connector.CheckKeyFile(file, null));
            var refused = Assert.Throws<RemoteStoreException>(() => Connector.Open(Config(file, Fingerprint), null));
            Assert.Equal(RemoteFailure.KeyFile, refused.Failure);
            Assert.Equal("placeholder", refused.Cause);
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        // Without the mark it is the key it always was.
        Assert.Equal(KeyFileState.Usable, Connector.CheckKeyFile(file, null));
    }

    [Fact]
    public void A_listing_takes_the_files_whose_names_could_be_Wyverns_and_leaves_out_the_rest()
    {
        string blob = new('a', BlobCodec.NameLength);
        string temporary = BlobCodec.TemporaryName(blob);
        Assert.Equal(85, temporary.Length);

        Assert.True(SftpRemoteStore.ListsEntry(0, blob, isRegularFile: true));
        Assert.True(SftpRemoteStore.ListsEntry(0, temporary, isRegularFile: true));

        // Whether a name is a blob's is for the caller to judge: a file of somebody else's is
        // listed like any other, as long as its name is no longer than the longest of Wyvern's.
        Assert.True(SftpRemoteStore.ListsEntry(0, "notes.txt", isRegularFile: true));
        Assert.True(SftpRemoteStore.ListsEntry(0, new string('x', 85), isRegularFile: true));

        // Longer than any name the store ever writes: it cannot be Wyvern's, and is not kept.
        Assert.False(SftpRemoteStore.ListsEntry(0, new string('x', 86), isRegularFile: true));
        Assert.False(SftpRemoteStore.ListsEntry(0, new string('x', 100_000), isRegularFile: true));

        // Folders, links and everything else that is not a file, whatever they are called.
        Assert.False(SftpRemoteStore.ListsEntry(0, blob, isRegularFile: false));
        Assert.False(SftpRemoteStore.ListsEntry(0, "..", isRegularFile: false));
    }

    [Fact]
    public void Every_entry_of_a_listing_counts_toward_the_limit_whatever_it_is()
    {
        string blob = new('a', BlobCodec.NameLength);
        int last = SyncLimits.MaxRemoteEntries - 1;

        Assert.True(SftpRemoteStore.ListsEntry(last, blob, isRegularFile: true));
        Assert.False(SftpRemoteStore.ListsEntry(last, "folder", isRegularFile: false));

        // One entry more than is read, and it makes no difference what that entry is: a server
        // that sends folders without end is stopped like one that sends files without end.
        Assert.Equal(RemoteFailure.TooMuch, Assert.Throws<RemoteStoreException>(() => SftpRemoteStore.ListsEntry(SyncLimits.MaxRemoteEntries, blob, isRegularFile: true)).Failure);
        Assert.Equal(RemoteFailure.TooMuch, Assert.Throws<RemoteStoreException>(() => SftpRemoteStore.ListsEntry(SyncLimits.MaxRemoteEntries, "folder", isRegularFile: false)).Failure);
        Assert.Equal(RemoteFailure.TooMuch, Assert.Throws<RemoteStoreException>(() => SftpRemoteStore.ListsEntry(SyncLimits.MaxRemoteEntries, new string('x', 86), isRegularFile: true)).Failure);
        Assert.Equal(RemoteFailure.TooMuch, Assert.Throws<RemoteStoreException>(() => SftpRemoteStore.ListsEntry(int.MaxValue, ".", isRegularFile: false)).Failure);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Without_a_pinned_fingerprint_no_connection_is_made(string? fingerprint)
    {
        var config = Config(KeyFile(PlainKey), fingerprint);

        var refused = Assert.Throws<RemoteStoreException>(() => Connector.Open(config, null));

        Assert.Equal(RemoteFailure.HostKeyChanged, refused.Failure);
    }

    [Fact]
    public void A_pinned_text_that_is_not_a_fingerprint_makes_no_connection_either()
    {
        var config = Config(KeyFile(PlainKey), null) with { HostKeyFingerprint = "SHA256:not-a-fingerprint" };

        var refused = Assert.Throws<RemoteStoreException>(() => Connector.Open(config, null));

        Assert.Equal(RemoteFailure.HostKeyChanged, refused.Failure);
    }

    [Fact]
    public void A_key_file_that_cannot_be_read_stops_the_connection_before_it_is_made()
    {
        var config = Config(_dir.File("nothing-here"), Fingerprint);

        var refused = Assert.Throws<RemoteStoreException>(() => Connector.Open(config, null));

        Assert.Equal(RemoteFailure.KeyFile, refused.Failure);
        Assert.Equal("FileNotFoundException", refused.Cause);
    }

    [Fact]
    public void A_key_file_whose_passphrase_is_missing_or_wrong_stops_the_connection_before_it_is_made()
    {
        var config = Config(KeyFile(LockedKey), Fingerprint);

        Assert.Equal(RemoteFailure.KeyFile, Assert.Throws<RemoteStoreException>(() => Connector.Open(config, null)).Failure);
        Assert.Equal(RemoteFailure.KeyFile, Assert.Throws<RemoteStoreException>(() => Connector.Open(config, "seal-flipper")).Failure);
    }

    [Fact]
    public void What_is_thrown_carries_fixed_words_and_no_exception_from_underneath()
    {
        var config = Config(_dir.File("nothing-here"), Fingerprint);

        var refused = Assert.Throws<RemoteStoreException>(() => Connector.Open(config, null));

        Assert.Equal("The sync server could not be used.", refused.Message);
        Assert.Null(refused.InnerException);
        Assert.DoesNotContain("nothing-here", refused.ToString());
        Assert.DoesNotContain("127.0.0.1", refused.ToString());
    }
}
