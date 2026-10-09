using System.Security.Cryptography;
using System.Text;
using Wyvern.Core.Platform;
using Wyvern.Core.Sync;

namespace Wyvern.Tests;

/// <summary>
/// The files that hold the sync key and the passphrase of the SSH key file. The protection of
/// Windows is stood in for by a fake; every key and passphrase is made up.
/// </summary>
public sealed class KeyStoreTests : IDisposable
{
    private const string Passphrase = "walrus tusk é 7";

    // The byte that goes in front of what a file protects, so that the file of one secret is not
    // taken for the file of the other.
    private const byte KeyTag = 1;
    private const byte PassphraseTag = 2;

    private static readonly byte[] Master = [.. Enumerable.Range(100, 32).Select(i => (byte)i)];

    // A key whose bytes are also text (the letters A to `), so that a key file put in the
    // passphrase file's place is refused for the byte in front of it and for nothing else.
    private static readonly byte[] TextMaster = [.. Enumerable.Range(0x41, 32).Select(i => (byte)i)];

    private readonly TempDir _dir = new();
    private readonly FakeKeyProtector _protector = new();
    private readonly KeyStore _store;

    public KeyStoreTests()
    {
        _store = new KeyStore(KeyFile, PassphraseFile, _protector);
    }

    public void Dispose() => _dir.Dispose();

    private string KeyFile => _dir.File("key.bin");

    private string PassphraseFile => _dir.File("ssh-passphrase.bin");

    // What a file holds when it is made by hand: the tag and the payload, protected.
    private byte[] Protected(byte tag, params byte[] payload) => _protector.Protect([tag, .. payload]);

    // Stands in for a protector whose damage shows only when a secret is read back.
    private sealed class ChangingProtector : IKeyProtector
    {
        private readonly FakeKeyProtector _inner = new();

        /// <summary>When true, what is unprotected is not what was protected.</summary>
        public bool Change { get; set; }

        public byte[] Protect(byte[] secret) => _inner.Protect(secret);

        public byte[] Unprotect(byte[] protectedBytes)
        {
            byte[] plain = _inner.Unprotect(protectedBytes);
            if (Change)
            {
                plain[^1] ^= 1;
            }

            return plain;
        }
    }

    [Fact]
    public void A_key_that_was_never_saved_is_missing()
    {
        Assert.Equal(SecretState.Missing, _store.ReadMaster(out byte[] master, out var cause));
        Assert.Empty(master);
        Assert.Null(cause);
    }

    [Fact]
    public void A_key_in_a_folder_that_is_not_there_is_missing()
    {
        var store = new KeyStore(Path.Combine(_dir.Path, "nowhere", "key.bin"), PassphraseFile, _protector);

        Assert.Equal(SecretState.Missing, store.ReadMaster(out _, out _));
    }

    [Fact]
    public void A_saved_key_is_read_back_the_same()
    {
        _store.WriteMaster(Master);

        Assert.Equal(SecretState.Read, _store.ReadMaster(out byte[] master, out var cause));
        Assert.Equal(Master, master);
        Assert.Null(cause);
    }

    [Fact]
    public void The_file_does_not_hold_the_key_in_the_clear()
    {
        _store.WriteMaster(Master);

        byte[] file = File.ReadAllBytes(KeyFile);

        Assert.True(file.AsSpan().IndexOf(Master) < 0);
        Assert.False(File.Exists(KeyFile + ".tmp"));
    }

    [Fact]
    public void Saving_makes_the_folder_and_replaces_an_earlier_key()
    {
        var store = new KeyStore(Path.Combine(_dir.Path, "root", "key.bin"), PassphraseFile, _protector);
        byte[] second = [.. Master.Reverse()];

        store.WriteMaster(Master);
        store.WriteMaster(second);

        Assert.Equal(SecretState.Read, store.ReadMaster(out byte[] master, out _));
        Assert.Equal(second, master);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(33)]
    public void A_key_of_the_wrong_length_is_not_saved(int length)
    {
        Assert.Throws<ArgumentException>(() => _store.WriteMaster(new byte[length]));
        Assert.False(File.Exists(KeyFile));
    }

    [Fact]
    public void A_key_that_Windows_will_not_protect_is_not_saved_and_the_earlier_key_stays()
    {
        _store.WriteMaster(Master);
        _protector.RefuseToProtect = true;

        Assert.Throws<CryptographicException>(() => _store.WriteMaster(new byte[32]));

        _protector.RefuseToProtect = false;
        Assert.Equal(SecretState.Read, _store.ReadMaster(out byte[] master, out _));
        Assert.Equal(Master, master);
        Assert.False(File.Exists(KeyFile + ".tmp"));
    }

    [Fact]
    public void A_key_that_cannot_be_read_back_is_not_reported_as_saved_and_the_earlier_key_stays()
    {
        _store.WriteMaster(Master);
        byte[] before = File.ReadAllBytes(KeyFile);
        _protector.RefuseToUnprotect = true;

        Assert.Throws<CryptographicException>(() => _store.WriteMaster(new byte[32]));

        Assert.Equal(before, File.ReadAllBytes(KeyFile));
        Assert.False(File.Exists(KeyFile + ".tmp"));
    }

    [Fact]
    public void A_key_file_that_this_user_did_not_protect_is_unreadable_and_not_missing()
    {
        File.WriteAllBytes(KeyFile, Master);

        Assert.Equal(SecretState.Unreadable, _store.ReadMaster(out byte[] master, out var cause));
        Assert.Empty(master);
        Assert.IsType<CryptographicException>(cause);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void A_key_file_that_holds_something_of_another_length_is_unreadable(int length)
    {
        File.WriteAllBytes(KeyFile, Protected(KeyTag, new byte[length]));

        Assert.Equal(SecretState.Unreadable, _store.ReadMaster(out byte[] master, out _));
        Assert.Empty(master);
    }

    [Fact]
    public void A_key_file_that_holds_a_key_of_the_right_length_behind_the_right_tag_is_read()
    {
        // The hand-made file the other tests build on.
        File.WriteAllBytes(KeyFile, Protected(KeyTag, Master));

        Assert.Equal(SecretState.Read, _store.ReadMaster(out byte[] master, out _));
        Assert.Equal(Master, master);
    }

    [Fact]
    public void An_empty_key_file_is_unreadable()
    {
        File.WriteAllBytes(KeyFile, []);

        Assert.Equal(SecretState.Unreadable, _store.ReadMaster(out byte[] master, out _));
        Assert.Empty(master);
    }

    [Fact]
    public void A_key_file_that_another_program_holds_is_unreadable_and_not_missing()
    {
        _store.WriteMaster(Master);
        using var held = new FileStream(KeyFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(SecretState.Unreadable, _store.ReadMaster(out byte[] master, out var cause));
        Assert.Empty(master);
        Assert.IsAssignableFrom<IOException>(cause);
    }

    [Fact]
    public void A_passphrase_that_was_never_saved_is_missing()
    {
        Assert.Equal(SecretState.Missing, _store.ReadPassphrase(out string? passphrase, out _));
        Assert.Null(passphrase);
    }

    [Fact]
    public void A_saved_passphrase_is_read_back_the_same_and_is_not_in_the_file_in_the_clear()
    {
        _store.WritePassphrase(Passphrase);

        Assert.Equal(SecretState.Read, _store.ReadPassphrase(out string? passphrase, out _));
        Assert.Equal(Passphrase, passphrase);
        Assert.True(File.ReadAllBytes(PassphraseFile).AsSpan().IndexOf(Encoding.UTF8.GetBytes("walrus")) < 0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Saving_no_passphrase_removes_the_file(string? none)
    {
        _store.WritePassphrase(Passphrase);

        _store.WritePassphrase(none);

        Assert.False(File.Exists(PassphraseFile));
        Assert.Equal(SecretState.Missing, _store.ReadPassphrase(out _, out _));
    }

    [Fact]
    public void A_passphrase_file_that_this_user_did_not_protect_is_unreadable_and_not_missing()
    {
        File.WriteAllText(PassphraseFile, Passphrase);

        Assert.Equal(SecretState.Unreadable, _store.ReadPassphrase(out string? passphrase, out var cause));
        Assert.Null(passphrase);
        Assert.IsType<CryptographicException>(cause);
    }

    [Fact]
    public void A_passphrase_file_that_holds_bytes_that_are_not_text_is_unreadable_and_its_cause_holds_none_of_the_bytes()
    {
        File.WriteAllBytes(PassphraseFile, Protected(PassphraseTag, 0x61, 0xff, 0xfe, 0x80));

        Assert.Equal(SecretState.Unreadable, _store.ReadPassphrase(out string? passphrase, out var cause));

        Assert.Null(passphrase);
        // The decoder's own message quotes the bytes it could not read: "Unable to translate
        // bytes [FF] at index 1". The file is the unprotected passphrase, so none of that may
        // be kept.
        var decoder = Assert.IsType<DecoderFallbackException>(cause);
        Assert.Null(decoder.InnerException);
        Assert.Null(decoder.BytesUnknown);
        Assert.DoesNotContain("FF", decoder.Message);
        Assert.DoesNotContain("FE", decoder.Message);
        Assert.DoesNotContain("80", decoder.Message);
        Assert.DoesNotContain("[", decoder.Message);
        Assert.Equal("The passphrase file does not hold text.", decoder.Message);

        // The same message for other bytes: it is a fixed sentence.
        File.WriteAllBytes(PassphraseFile, Protected(PassphraseTag, 0xc3, 0x28, 0xa0));
        Assert.Equal(SecretState.Unreadable, _store.ReadPassphrase(out _, out var another));
        Assert.Equal(decoder.Message, another!.Message);
    }

    [Fact]
    public void A_passphrase_file_far_too_large_is_unreadable_though_what_it_holds_is_valid_text()
    {
        // Valid text behind the right tag, protected as it should be: only its size is wrong.
        byte[] text = Encoding.UTF8.GetBytes(new string('p', 64 * 1024 + 1));
        File.WriteAllBytes(PassphraseFile, Protected(PassphraseTag, text));
        Assert.True(new FileInfo(PassphraseFile).Length > 64 * 1024);

        Assert.Equal(SecretState.Unreadable, _store.ReadPassphrase(out string? passphrase, out _));
        Assert.Null(passphrase);
    }

    [Fact]
    public void A_passphrase_file_of_exactly_64_KB_is_read()
    {
        // The mark of the fake, the tag, and then text, up to 64 KB exactly.
        int markLength = Protected(PassphraseTag).Length - 1;
        string text = new('p', 64 * 1024 - markLength - 1);
        File.WriteAllBytes(PassphraseFile, Protected(PassphraseTag, Encoding.UTF8.GetBytes(text)));
        Assert.Equal(64 * 1024, new FileInfo(PassphraseFile).Length);

        Assert.Equal(SecretState.Read, _store.ReadPassphrase(out string? passphrase, out _));
        Assert.Equal(text, passphrase);
    }

    [Fact]
    public void A_passphrase_with_a_lone_half_of_a_surrogate_pair_is_not_saved_and_the_earlier_passphrase_stays()
    {
        _store.WritePassphrase(Passphrase);
        byte[] before = File.ReadAllBytes(PassphraseFile);

        foreach (string broken in new[] { "walrus" + '\uD800' + "tusk", "walrus" + '\uDC00', '\uDC00' + "walrus", "\uDC00\uD800" })
        {
            var thrown = Assert.Throws<ArgumentException>(() => _store.WritePassphrase(broken));

            Assert.DoesNotContain("walrus", thrown.Message);
            Assert.DoesNotContain("D800", thrown.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, File.ReadAllBytes(PassphraseFile));
            Assert.False(File.Exists(PassphraseFile + ".tmp"));
        }

        Assert.Equal(SecretState.Read, _store.ReadPassphrase(out string? passphrase, out _));
        Assert.Equal(Passphrase, passphrase);
    }

    [Fact]
    public void A_passphrase_that_is_refused_for_a_lone_half_leaves_no_file_where_there_was_none()
    {
        Assert.Throws<ArgumentException>(() => _store.WritePassphrase("walrus" + '\uD800'));

        Assert.False(File.Exists(PassphraseFile));
        Assert.False(File.Exists(PassphraseFile + ".tmp"));
    }

    [Fact]
    public void A_passphrase_with_a_whole_surrogate_pair_is_saved_and_read_back_the_same()
    {
        string withPair = "walrus " + char.ConvertFromUtf32(0x1F511);

        _store.WritePassphrase(withPair);

        Assert.Equal(SecretState.Read, _store.ReadPassphrase(out string? passphrase, out _));
        Assert.Equal(withPair, passphrase);
    }

    [Fact]
    public void Each_file_holds_the_tag_of_its_secret_in_front_of_it_inside_the_protection()
    {
        _store.WriteMaster(Master);
        _store.WritePassphrase(Passphrase);

        byte[] taggedKey = [KeyTag, .. Master];
        byte[] taggedPassphrase = [PassphraseTag, .. Encoding.UTF8.GetBytes(Passphrase)];

        Assert.Equal(taggedKey, _protector.Unprotect(File.ReadAllBytes(KeyFile)));
        Assert.Equal(taggedPassphrase, _protector.Unprotect(File.ReadAllBytes(PassphraseFile)));
    }

    [Fact]
    public void The_passphrase_file_put_in_the_keys_place_is_not_a_key_even_when_the_passphrase_is_32_bytes_long()
    {
        _store.WritePassphrase(Encoding.ASCII.GetString(TextMaster));
        File.Copy(PassphraseFile, KeyFile);

        Assert.Equal(SecretState.Unreadable, _store.ReadMaster(out byte[] master, out _));
        Assert.Empty(master);
    }

    [Fact]
    public void The_key_file_put_in_the_passphrases_place_is_not_a_passphrase_even_when_the_key_is_text()
    {
        _store.WriteMaster(TextMaster);
        File.Copy(KeyFile, PassphraseFile);

        Assert.Equal(SecretState.Unreadable, _store.ReadPassphrase(out string? passphrase, out _));
        Assert.Null(passphrase);
    }

    [Fact]
    public void A_file_with_the_tag_of_the_other_secret_is_unreadable_whatever_it_holds()
    {
        File.WriteAllBytes(KeyFile, Protected(PassphraseTag, Master));
        File.WriteAllBytes(PassphraseFile, Protected(KeyTag, Encoding.UTF8.GetBytes(Passphrase)));
        Assert.Equal(SecretState.Unreadable, _store.ReadMaster(out byte[] master, out _));
        Assert.Equal(SecretState.Unreadable, _store.ReadPassphrase(out string? passphrase, out _));
        Assert.Empty(master);
        Assert.Null(passphrase);

        File.WriteAllBytes(KeyFile, Protected(0, Master));
        File.WriteAllBytes(PassphraseFile, Protected(3, Encoding.UTF8.GetBytes(Passphrase)));
        Assert.Equal(SecretState.Unreadable, _store.ReadMaster(out _, out _));
        Assert.Equal(SecretState.Unreadable, _store.ReadPassphrase(out _, out _));

        // A file made before the tag was added holds no tag, and so is no more than its first byte.
        File.WriteAllBytes(KeyFile, _protector.Protect(Master));
        Assert.Equal(SecretState.Unreadable, _store.ReadMaster(out _, out _));
    }

    [Fact]
    public void A_file_that_protects_no_bytes_at_all_is_unreadable()
    {
        File.WriteAllBytes(KeyFile, _protector.Protect([]));
        File.WriteAllBytes(PassphraseFile, _protector.Protect([]));

        Assert.Equal(SecretState.Unreadable, _store.ReadMaster(out byte[] master, out _));
        Assert.Equal(SecretState.Unreadable, _store.ReadPassphrase(out string? passphrase, out _));
        Assert.Empty(master);
        Assert.Null(passphrase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_key_file_with_no_name_is_unreadable_and_not_an_exception(string name)
    {
        var store = new KeyStore(name, PassphraseFile, _protector);

        Assert.Equal(SecretState.Unreadable, store.ReadMaster(out byte[] master, out var cause));

        Assert.Empty(master);
        var refusal = Assert.IsType<ArgumentException>(cause);
        Assert.Equal("The file of a secret needs a name.", refusal.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_passphrase_file_with_no_name_is_unreadable_and_not_an_exception(string name)
    {
        var store = new KeyStore(KeyFile, name, _protector);

        Assert.Equal(SecretState.Unreadable, store.ReadPassphrase(out string? passphrase, out var cause));

        Assert.Null(passphrase);
        var refusal = Assert.IsType<ArgumentException>(cause);
        Assert.Equal("The file of a secret needs a name.", refusal.Message);
    }

    [Fact]
    public void A_key_that_cannot_be_read_back_as_it_was_is_not_reported_as_saved_leaves_no_temporary_file_and_the_earlier_key_stays()
    {
        var protector = new ChangingProtector();
        var store = new KeyStore(KeyFile, PassphraseFile, protector);
        store.WriteMaster(Master);
        byte[] before = File.ReadAllBytes(KeyFile);
        protector.Change = true;

        var thrown = Assert.Throws<IOException>(() => store.WriteMaster([.. Master.Reverse()]));

        Assert.Equal("A secret could not be read back after it was written.", thrown.Message);
        Assert.False(File.Exists(KeyFile + ".tmp"));
        Assert.Equal(before, File.ReadAllBytes(KeyFile));
        protector.Change = false;
        Assert.Equal(SecretState.Read, store.ReadMaster(out byte[] master, out _));
        Assert.Equal(Master, master);
    }

    [Fact]
    public void A_passphrase_that_cannot_be_read_back_as_it_was_is_not_reported_as_saved_and_leaves_no_temporary_file()
    {
        var protector = new ChangingProtector { Change = true };
        var store = new KeyStore(KeyFile, PassphraseFile, protector);

        var thrown = Assert.Throws<IOException>(() => store.WritePassphrase(Passphrase));

        Assert.Equal("A secret could not be read back after it was written.", thrown.Message);
        Assert.False(File.Exists(PassphraseFile));
        Assert.False(File.Exists(PassphraseFile + ".tmp"));
    }
}
