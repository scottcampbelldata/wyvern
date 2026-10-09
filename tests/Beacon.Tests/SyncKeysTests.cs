using System.Security.Cryptography;
using System.Text;
using Beacon.Core.Sync;

namespace Beacon.Tests;

/// <summary>
/// The keys of sync. The expected bytes here were worked out with another implementation
/// (HMAC-SHA256 by hand, in Python), not with the code under test. Every key is made up.
/// </summary>
public class SyncKeysTests
{
    // The master key of every known-answer test: the bytes 00, 01, 02 ... 1f.
    private static readonly byte[] Master = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];

    private static byte[] Bytes(byte value, int count) => [.. Enumerable.Repeat(value, count)];

    private static byte[] Run(int from, int count) => [.. Enumerable.Range(from, count).Select(i => (byte)i)];

    private static string Derived(byte[] key, byte[] salt, byte[] info, int length)
    {
        byte[] output = new byte[length];
        SyncKeys.Derive(key, salt, info, output);
        return Convert.ToHexStringLower(output);
    }

    [Fact]
    public void Derive_gives_the_first_test_vector_of_RFC_5869()
    {
        Assert.Equal(
            "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865",
            Derived(Bytes(0x0b, 22), Run(0x00, 13), Run(0xf0, 10), 42));
    }

    [Fact]
    public void Derive_gives_the_second_test_vector_of_RFC_5869_with_its_longer_inputs()
    {
        Assert.Equal(
            "b11e398dc80327a1c8e7f78c596a49344f012eda2d4efad8a050cc4c19afa97c59045a99cac7827271cb41c65e590e09da3275600c2f09b8367793a9aca3db71cc30c58179ec3e87c14c01d5c1f3434f1d87",
            Derived(Run(0x00, 80), Run(0x60, 80), Run(0xb0, 80), 82));
    }

    [Fact]
    public void Derive_gives_the_third_test_vector_of_RFC_5869_which_has_no_salt_and_no_info()
    {
        Assert.Equal(
            "8da4e775a563c18f715f802a063c5a31b8a11f5c5ee1879ec3454e5f3c738d2d9d201395faa4b61a96c8",
            Derived(Bytes(0x0b, 22), [], [], 42));
    }

    // The name key as .NET's own HKDF gives it for the master key 00 ... 1f: no salt, the info
    // beacon/name/v1, 32 bytes. Nothing of SyncKeys is used, so that a test that compares with
    // it compares with something else than itself.
    private static byte[] NameKeyFromPrimitives() =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Master, 32, salt: [], info: Encoding.ASCII.GetBytes("beacon/name/v1"));

    [Fact]
    public void The_name_key_is_made_with_no_salt_and_the_info_beacon_name_v1()
    {
        // HMAC-SHA256 under the name key that HKDF gives for that info, of the path.
        using var keys = SyncKeys.FromMaster(Master);

        Assert.Equal("3586f21f7da74da9be019aaf2466c32e8b492c67ddb61f4487b3f85dc0873c00", keys.BlobName("library/snippets/otter.md"));
        Assert.Equal(
            Convert.ToHexStringLower(HMACSHA256.HashData(NameKeyFromPrimitives(), Encoding.UTF8.GetBytes("library/snippets/otter.md"))),
            keys.BlobName("library/snippets/otter.md"));
    }

    [Fact]
    public void A_path_with_non_ASCII_letters_is_named_from_its_lower_case_spelling_in_UTF_8()
    {
        using var keys = SyncKeys.FromMaster(Master);
        const string mixed = "Library/Snippets/Résumé (2).md";
        const string lower = "library/snippets/résumé (2).md";
        string expected = Convert.ToHexStringLower(HMACSHA256.HashData(NameKeyFromPrimitives(), Encoding.UTF8.GetBytes(lower)));

        Assert.Equal(expected, keys.BlobName(SyncPath.Key(mixed)));
        Assert.Equal(expected, keys.BlobName(SyncPath.Key(lower)));
        Assert.Equal("6b73638f3005ba9f1260a5028be1fd9c114a526c439c571f71810d82baa81da0", expected);
    }

    [Fact]
    public void The_check_value_is_made_from_a_zero_byte_and_beacon_check_v1_which_no_path_can_hold()
    {
        using var keys = SyncKeys.FromMaster(Master);
        byte[] marker = [0, .. Encoding.ASCII.GetBytes("beacon/check/v1")];
        string expected = Convert.ToHexStringLower(HMACSHA256.HashData(NameKeyFromPrimitives(), marker))[..16];

        Assert.Equal(16, marker.Length);
        Assert.Equal(expected, keys.Check());
        Assert.Equal("8b65148e60f75431", keys.Check());
    }

    [Fact]
    public void The_check_value_is_not_the_start_of_the_name_of_any_blob()
    {
        // The text the check used to be made from is a path sync accepts, so a blob can be named
        // by it. The zero byte that is in front of it now is a character no path can hold.
        using var keys = SyncKeys.FromMaster(Master);

        Assert.True(SyncPath.TryParse("beacon/check/v1", out _));
        Assert.False(SyncPath.TryParse("\0beacon/check/v1", out _));
        Assert.False(keys.BlobName("beacon/check/v1").StartsWith(keys.Check(), StringComparison.Ordinal));
    }

    [Fact]
    public void A_blob_name_is_64_lower_case_hex_digits_and_differs_for_another_path_and_another_key()
    {
        using var keys = SyncKeys.FromMaster(Master);
        using var other = SyncKeys.FromMaster(Bytes(7, 32));

        string name = keys.BlobName("library/quicklinks.json");

        Assert.True(BlobCodec.IsBlobName(name));
        Assert.Equal(name, keys.BlobName("library/quicklinks.json"));
        Assert.NotEqual(name, keys.BlobName("library/quicklinks.jsom"));
        Assert.NotEqual(name, other.BlobName("library/quicklinks.json"));
        Assert.NotEqual(keys.Check(), other.Check());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void A_master_key_of_any_other_length_is_refused(int length)
    {
        Assert.Throws<ArgumentException>(() => SyncKeys.FromMaster(new byte[length]));
    }

    [Fact]
    public void A_new_master_key_is_32_bytes_and_never_the_same_twice()
    {
        byte[] first = SyncKeys.NewMaster();
        byte[] second = SyncKeys.NewMaster();

        Assert.Equal(32, first.Length);
        Assert.NotEqual(first, second);
        Assert.Contains(first, value => value != 0);
    }

    [Fact]
    public void A_key_is_shown_as_44_characters_of_base64_and_read_back_from_them()
    {
        string text = SyncKeys.Format(Master);

        Assert.Equal("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=", text);
        Assert.True(SyncKeys.TryParse(text, out byte[] read));
        Assert.Equal(Master, read);
    }

    [Fact]
    public void White_space_around_a_typed_key_is_ignored()
    {
        Assert.True(SyncKeys.TryParse("  AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=\r\n", out byte[] read));
        Assert.Equal(Master, read);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a key")]
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHg==")] // 31 bytes
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8g")] // 33 bytes
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8")] // the padding is missing
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=")] // two keys
    public void Text_that_is_not_base64_of_exactly_32_bytes_is_not_a_key(string? text)
    {
        Assert.False(SyncKeys.TryParse(text, out byte[] read));
        Assert.Empty(read);
    }

    [Fact]
    public void Wipe_overwrites_a_secret_with_zeros_and_takes_null()
    {
        byte[] secret = [1, 2, 3];

        SyncKeys.Wipe(secret);
        SyncKeys.Wipe(null);

        Assert.Equal(new byte[3], secret);
    }

    [Fact]
    public void Keys_that_were_let_go_of_cannot_be_used()
    {
        var keys = SyncKeys.FromMaster(Master);
        keys.Dispose();

        Assert.Throws<ObjectDisposedException>(() => keys.BlobName("library/quicklinks.json"));
        Assert.Throws<ObjectDisposedException>(() => keys.Check());
        Assert.Throws<ObjectDisposedException>(() =>
            BlobCodec.Seal(keys, new string('a', 64), new BlobHeader("a.txt", DateTimeOffset.UnixEpoch, false, BlobCodec.Hash([])), []));
    }

    [Fact]
    public void Keys_print_nothing_of_themselves()
    {
        using var keys = SyncKeys.FromMaster(Master);

        Assert.Equal("SyncKeys", keys.ToString());
    }
}
