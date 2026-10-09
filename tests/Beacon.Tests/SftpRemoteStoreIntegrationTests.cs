using Beacon.Core.Sync;

namespace Beacon.Tests;

/// <summary>
/// A test that needs a real SFTP server. It runs only when the four environment variables
/// below are set, and is reported as skipped otherwise, so that an ordinary run of the suite
/// never opens a connection.
/// </summary>
public sealed class SftpFactAttribute : FactAttribute
{
    public const string HostVariable = "BEACON_TEST_SFTP_HOST";
    public const string UserVariable = "BEACON_TEST_SFTP_USER";
    public const string KeyVariable = "BEACON_TEST_SFTP_KEY";
    public const string DirVariable = "BEACON_TEST_SFTP_DIR";

    /// <summary>Optional: the port, when it is not 22.</summary>
    public const string PortVariable = "BEACON_TEST_SFTP_PORT";

    /// <summary>Optional: the passphrase of the key file, when it has one.</summary>
    public const string PassphraseVariable = "BEACON_TEST_SFTP_PASSPHRASE";

    /// <summary>Optional: the server's fingerprint as <c>ssh-keygen -lf</c> prints it. When it is set, any other key fails the test.</summary>
    public const string FingerprintVariable = "BEACON_TEST_SFTP_FINGERPRINT";

    public SftpFactAttribute()
    {
        if (new[] { HostVariable, UserVariable, KeyVariable, DirVariable }.Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
        {
            Skip = $"Needs a real SFTP server: set {HostVariable}, {UserVariable}, {KeyVariable} and {DirVariable}.";
        }
    }
}

/// <summary>
/// The SFTP store against a real server. Everything happens inside a new folder,
/// <c>beacon-test-&lt;random&gt;</c>, under the folder that <c>BEACON_TEST_SFTP_DIR</c> names (which
/// must exist), and that folder is removed again at the end. What is uploaded is made-up bytes
/// under made-up blob names: no key and nothing of the owner's.
/// </summary>
public sealed class SftpRemoteStoreIntegrationTests
{
    private static readonly string First = new('1', 64);
    private static readonly string Second = new('2', 64);

    private static string Variable(string name) => Environment.GetEnvironmentVariable(name) ?? "";

    private static string? Passphrase => Variable(SftpFactAttribute.PassphraseVariable) is { Length: > 0 } text ? text : null;

    private static SyncConfig Settings(string? fingerprint)
    {
        string folder = Variable(SftpFactAttribute.DirVariable).TrimEnd('/') + "/beacon-test-" + Guid.NewGuid().ToString("N");
        Assert.True(
            SyncConfig.TryCreate(
                Variable(SftpFactAttribute.HostVariable),
                Variable(SftpFactAttribute.PortVariable),
                Variable(SftpFactAttribute.UserVariable),
                Variable(SftpFactAttribute.KeyVariable),
                folder,
                fingerprint,
                out var config,
                out string problem),
            problem);
        return config!;
    }

    // The fingerprint to pin: the one the owner gave, or else the one the server shows now.
    private static string ServerFingerprint()
    {
        var shown = SftpRemoteStore.Connector.ReadHostKey(Settings(null));
        Assert.True(HostKeyFingerprint.IsValid(shown.Fingerprint));
        string expected = Variable(SftpFactAttribute.FingerprintVariable).Trim();
        if (expected.Length > 0)
        {
            Assert.Equal(expected, shown.Fingerprint);
        }

        return shown.Fingerprint;
    }

    [SftpFact]
    public void The_key_file_is_one_that_can_be_used()
    {
        Assert.Equal(KeyFileState.Usable, SftpRemoteStore.Connector.CheckKeyFile(Settings(null).PrivateKeyPath, Passphrase));
    }

    [SftpFact]
    public void Blobs_are_listed_uploaded_downloaded_replaced_and_deleted_and_nothing_is_left_behind()
    {
        var config = Settings(ServerFingerprint());
        using var store = (SftpRemoteStore)SftpRemoteStore.Connector.Open(config, Passphrase);
        bool passed = false;
        try
        {
            // A new folder holds nothing.
            Assert.Empty(store.List());

            // Upload, and what the server says of the blob.
            byte[] one = [1, 2, 3, 4, 5];
            var stored = store.Upload(First, one);
            Assert.Equal(First, stored.Name);
            Assert.Equal(5, stored.Size);
            var listed = Assert.Single(store.List());
            Assert.Equal(stored, listed);

            // The server's clock, and the time it gave the blob, are near this machine's.
            var now = DateTimeOffset.UtcNow;
            Assert.InRange(store.Now(), now.AddHours(-24), now.AddHours(24));
            Assert.InRange(stored.ModifiedUtc, now.AddHours(-24), now.AddHours(24));

            // Download gives the same bytes back.
            Assert.Equal(one, store.Download(First, 5));

            // A second upload under the same name replaces the blob: the rename over a file
            // that is there, which is the step a server could refuse.
            byte[] two = new byte[200_000];
            new Random(6).NextBytes(two);
            Assert.Equal(two.Length, store.Upload(First, two).Size);
            Assert.Equal(two, store.Download(First, two.Length));
            Assert.Equal(First, Assert.Single(store.List()).Name);

            // A blob larger than the caller allows is refused, whatever the server says its size is.
            Assert.Equal(RemoteFailure.TooMuch, Assert.Throws<RemoteStoreException>(() => store.Download(First, two.Length - 1)).Failure);

            // A second blob, and a blob that is not there.
            store.Upload(Second, []);
            Assert.Equal([First, Second], store.List().Select(blob => blob.Name).Order());
            Assert.Empty(store.Download(Second, 10));
            Assert.Equal(RemoteFailure.Missing, Assert.Throws<RemoteStoreException>(() => store.Download(new string('3', 64), 10)).Failure);

            // Delete, twice: a blob that is gone is not an error.
            store.Delete(First);
            store.Delete(First);
            Assert.Equal(Second, Assert.Single(store.List()).Name);

            // A name that is not a blob's name never reaches the server.
            Assert.Throws<ArgumentException>(() => store.Download("../../etc/passwd", 10));
            Assert.Throws<ArgumentException>(() => store.Upload("notes.txt", one));
            Assert.Throws<ArgumentException>(() => store.Delete(".."));
            passed = true;
        }
        finally
        {
            try
            {
                foreach (var blob in store.List())
                {
                    if (BlobCodec.IsBlobName(blob.Name) || BlobCodec.IsTemporaryName(blob.Name))
                    {
                        store.Delete(blob.Name);
                    }
                }

                store.RemoveEmptyFolders();
            }
            catch (Exception) when (!passed)
            {
                // Something above failed first, and that is what the test reports. Tidying up
                // after it can fail as well, for the same reason or because of it, and must not
                // take its place. After a test that passed, a tidying up that fails is the
                // failure: something was left behind.
            }
        }
    }

    [SftpFact]
    public void A_server_whose_key_is_not_the_pinned_one_is_refused_before_the_sign_in()
    {
        ServerFingerprint();
        var config = Settings("SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

        var refused = Assert.Throws<RemoteStoreException>(() => SftpRemoteStore.Connector.Open(config, Passphrase));

        Assert.Equal(RemoteFailure.HostKeyChanged, refused.Failure);
        Assert.Null(refused.InnerException);
    }
}
