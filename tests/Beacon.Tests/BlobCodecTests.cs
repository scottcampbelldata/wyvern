using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Beacon.Core.Sync;
using SystemPath = System.IO.Path;

namespace Beacon.Tests;

/// <summary>
/// The blob format. The known answers were made with another implementation of AES-256-GCM and
/// HMAC-SHA256 (Python's cryptography package), from the key, the nonce and the plain bytes
/// that are written out here. Every key and every file is made up.
/// </summary>
public sealed class BlobCodecTests : IDisposable
{
    private const string Path = "library/snippets/otter.md";

    // HMAC-SHA256 of the path under the name key of the master key 00 01 02 ... 1f.
    private const string Name = "3586f21f7da74da9be019aaf2466c32e8b492c67ddb61f4487b3f85dc0873c00";

    private const string BodyHash = "64990fc2d6ecf64947506ae8c9d836845bd8db1e5a18afd784a7bd44f60c1056";

    // The blob of the file "walrus\n" at that path, written at noon on 3 October 2026, under
    // the nonce a0 a1 ... ab. Its plain bytes are aa 00 00 00, then the 170 bytes of
    // {"path":"library/snippets/otter.md","mtimeUtc":"2026-10-03T12:00:00.0000000Z","deleted":false,"sha256":"6499...1056"}
    // and then the seven bytes of the file.
    private const string KnownBlob =
        "42434e31a0a1a2a3a4a5a6a7a8a9aaab46b35545cd46f2a3ac958aee20ee214b"
        + "43ddf672b1ea73a64fc9c3c9710027549590b696139d97a11b0452ddf37abf54"
        + "528ed1a744a5770dbd7d3d6be16504a82cfc5abde8e7ad7448e32b13ed445482"
        + "bd8257ce1ca30caabb98efb35ee720d9617f7ce0094e54746cf1e73f3bd03fa8"
        + "a1f50ed14f39ec98df535799ddf3d03bde7d017f9370de3f92f2c2681d1d95ce"
        + "607056f54535781c2bcedab9eeac126a0daeecc73fe2f770d784af21800b6539"
        + "ed3406b3911ea2c80df8a941f96cdc5bac4df5b5bb";

    // The tombstone of the same path, noticed at 08:30:00.1234567 on 4 October 2026, under the
    // same nonce. Its plain bytes are 69 00 00 00 and the 105 bytes of
    // {"path":"library/snippets/otter.md","mtimeUtc":"2026-10-04T08:30:00.1234567Z","deleted":true,"sha256":""}
    private const string KnownTombstone =
        "42434e31a0a1a2a3a4a5a6a7a8a9aaab85b35545cd46f2a3ac958aee20ee214b"
        + "43ddf672b1ea73a64fc9c3c9710027549590b696139d97a11b0452ddf37abf54"
        + "528ed1a744a5770dbd7d3d6be16204a926fc59bde8e7ad7449e12817e8425382"
        + "bd8257ce1ca30caabb98efb34cf439cf28712dfb001d537778e9ff3f704db6be"
        + "7fc9dd0d79f0c8ddb996592adc";

    private static readonly byte[] Master = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("walrus\n");
    private static readonly DateTimeOffset Noon = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly BlobHeader Header = new(Path, Noon, Deleted: false, BodyHash);

    // The nonce of the known answers: a0 a1 ... ab.
    private static readonly byte[] KnownNonce = [.. Enumerable.Range(0, 12).Select(i => (byte)(0xa0 + i))];

    private readonly SyncKeys _keys = SyncKeys.FromMaster(Master);

    public void Dispose() => _keys.Dispose();

    private byte[] Sealed() => BlobCodec.Seal(_keys, Name, Header, Body);

    private void AssertNotOpened(byte[] blob, BlobProblem expected, string name = Name, SyncKeys? keys = null)
    {
        Assert.Null(BlobCodec.Open(keys ?? _keys, name, blob, out var problem));
        Assert.Equal(expected, problem);
    }

    // A blob that authenticates and whose plain bytes are whatever the test wants, as a newer or
    // a broken version of Beacon could write it.
    private byte[] SealedRaw(string name, byte[] plain)
    {
        byte[] content = new byte[32];
        SyncKeys.Derive(Master, [], "beacon/content/v1"u8, content);
        byte[] blob = [.. "BCN1"u8, .. new byte[12], .. plain, .. new byte[16]];
        using var aes = new AesGcm(content, 16);
        aes.Encrypt(blob.AsSpan(4, 12), plain, blob.AsSpan(16, plain.Length), blob.AsSpan(blob.Length - 16), Encoding.ASCII.GetBytes(name));
        return blob;
    }

    private byte[] SealedWithHeader(string name, string headerJson, byte[]? content = null) =>
        SealedWithHeaderBytes(name, Encoding.UTF8.GetBytes(headerJson), content);

    // The same, for a header that is not text: bytes that no UTF-8 writer would make.
    private byte[] SealedWithHeaderBytes(string name, byte[] header, byte[]? content = null)
    {
        byte[] length = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)header.Length);
        return SealedRaw(name, [.. length, .. header, .. content ?? []]);
    }

    private static string HeaderJson(string path, string sha256, string mtime = "2026-10-03T12:00:00.0000000Z", string deleted = "false") =>
        $$"""{"path":"{{path}}","mtimeUtc":"{{mtime}}","deleted":{{deleted}},"sha256":"{{sha256}}"}""";

    [Fact]
    public void A_file_sealed_with_a_known_key_and_nonce_gives_the_known_bytes()
    {
        byte[] blob = BlobCodec.SealWithKnownNonce(_keys, Name, Header, Body, KnownNonce);

        Assert.Equal(KnownBlob, Convert.ToHexStringLower(blob));
    }

    [Fact]
    public void A_tombstone_sealed_with_a_known_key_and_nonce_gives_the_known_bytes()
    {
        var header = new BlobHeader(Path, new DateTimeOffset(2026, 10, 4, 8, 30, 0, TimeSpan.Zero).AddTicks(1234567), Deleted: true, "");

        byte[] blob = BlobCodec.SealWithKnownNonce(_keys, Name, header, [], KnownNonce);

        Assert.Equal(KnownTombstone, Convert.ToHexStringLower(blob));
    }

    [Fact]
    public void Seal_takes_four_arguments_and_no_nonce_among_them()
    {
        var seals = typeof(BlobCodec).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(method => method.Name == nameof(BlobCodec.Seal)).ToList();

        var seal = Assert.Single(seals);
        Assert.Equal(["keys", "blobName", "header", "content"], seal.GetParameters().Select(parameter => parameter.Name));
        Assert.DoesNotContain(seal.GetParameters(), parameter => parameter.IsOptional);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(13)]
    [InlineData(32)]
    public void A_known_nonce_must_be_exactly_12_bytes(int length)
    {
        Assert.Throws<ArgumentException>(() => BlobCodec.SealWithKnownNonce(_keys, Name, Header, Body, new byte[length]));
    }

    [Fact]
    public void A_known_nonce_is_the_nonce_in_the_blob_and_is_left_as_it_was_given()
    {
        byte[] nonce = [.. KnownNonce];

        byte[] blob = BlobCodec.SealWithKnownNonce(_keys, Name, Header, Body, nonce);

        Assert.Equal(KnownNonce, blob[4..16]);
        Assert.Equal(KnownNonce, nonce);
    }

    [Fact]
    public void Nothing_under_src_but_the_codec_itself_names_the_way_to_seal_under_a_known_nonce()
    {
        string root = RepositoryRoot();
        var offences = new List<string>();
        int files = 0;
        foreach (string file in Directory.EnumerateFiles(SystemPath.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            string relative = SystemPath.GetRelativePath(root, file).Replace(SystemPath.DirectorySeparatorChar, '/');
            if (relative.Contains("/obj/") || relative.Contains("/bin/"))
            {
                continue;
            }

            files++;
            if (relative != "src/Beacon.Core/Sync/BlobCodec.cs" && File.ReadAllText(file).Contains(nameof(BlobCodec.SealWithKnownNonce), StringComparison.Ordinal))
            {
                offences.Add(relative);
            }
        }

        // Proves the files are really being read, so the check cannot pass on an empty list.
        Assert.True(files > 100, $"only {files} source files were found under {root}");
        Assert.True(offences.Count == 0, "A nonce that is given, outside the codec. Look at: " + string.Join(", ", offences));
    }

    private static string RepositoryRoot()
    {
        var folder = new DirectoryInfo(SystemPath.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        while (folder is not null && !File.Exists(SystemPath.Combine(folder.FullName, "Beacon.slnx")))
        {
            folder = folder.Parent;
        }

        Assert.True(folder is not null, $"Beacon.slnx was not found above {AppContext.BaseDirectory}");
        return folder.FullName;
    }

    [Fact]
    public void The_known_blob_opens_to_the_file_and_its_header()
    {
        var opened = BlobCodec.Open(_keys, Name, Convert.FromHexString(KnownBlob), out var problem);

        Assert.Equal(BlobProblem.None, problem);
        Assert.NotNull(opened);
        Assert.Equal(Header, opened.Header);
        Assert.Equal(Body, opened.Content.ToArray());
    }

    [Fact]
    public void The_known_tombstone_opens_to_a_header_that_says_deleted_and_no_content()
    {
        var opened = BlobCodec.Open(_keys, Name, Convert.FromHexString(KnownTombstone), out var problem);

        Assert.Equal(BlobProblem.None, problem);
        Assert.NotNull(opened);
        Assert.True(opened.Header.Deleted);
        Assert.Equal("", opened.Header.Sha256);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 8, 30, 0, TimeSpan.Zero).AddTicks(1234567), opened.Header.MtimeUtc);
        Assert.Equal(0, opened.Content.Length);
    }

    [Fact]
    public void A_blob_is_four_letters_a_nonce_the_ciphertext_and_a_tag()
    {
        byte[] blob = Sealed();

        // 4 + 12 + (4 + 170 + 7) + 16.
        Assert.Equal(213, blob.Length);
        Assert.Equal("BCN1"u8.ToArray(), blob[..4]);
        Assert.Equal(BlobCodec.Overhead, 4 + BlobCodec.NonceLength + BlobCodec.TagLength);
    }

    [Fact]
    public void Two_blobs_of_the_same_file_differ_in_their_nonce_and_in_every_part_after_it()
    {
        byte[] first = Sealed();
        byte[] second = Sealed();

        Assert.NotEqual(first[4..16], second[4..16]);
        Assert.NotEqual(first[16..^16], second[16..^16]);
        Assert.NotEqual(first[^16..], second[^16..]);
        Assert.NotNull(BlobCodec.Open(_keys, Name, first, out _));
        Assert.NotNull(BlobCodec.Open(_keys, Name, second, out _));
    }

    [Fact]
    public void Nothing_of_the_path_or_the_content_can_be_read_in_a_blob()
    {
        byte[] body = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("walrus walrus walrus ", 20)));
        byte[] blob = BlobCodec.Seal(_keys, Name, Header with { Sha256 = BlobCodec.Hash(body) }, body);
        string seen = Encoding.Latin1.GetString(blob);

        Assert.DoesNotContain("walrus", seen);
        Assert.DoesNotContain("otter", seen);
        Assert.DoesNotContain("library", seen);
        Assert.DoesNotContain("sha256", seen);
    }

    [Fact]
    public void A_large_file_goes_through_whole()
    {
        byte[] body = new byte[3 * 1024 * 1024 + 17];
        new Random(6).NextBytes(body);
        var header = Header with { Sha256 = BlobCodec.Hash(body) };

        var opened = BlobCodec.Open(_keys, Name, BlobCodec.Seal(_keys, Name, header, body), out var problem);

        Assert.Equal(BlobProblem.None, problem);
        Assert.True(opened!.Content.Span.SequenceEqual(body));
    }

    [Fact]
    public void An_empty_file_is_not_a_tombstone()
    {
        var header = Header with { Sha256 = BlobCodec.Hash([]) };

        var opened = BlobCodec.Open(_keys, Name, BlobCodec.Seal(_keys, Name, header, []), out _);

        Assert.NotNull(opened);
        Assert.False(opened.Header.Deleted);
        Assert.Equal(0, opened.Content.Length);
    }

    [Theory]
    [InlineData(0)]     // the first of the four letters
    [InlineData(3)]     // the last of them
    public void A_blob_whose_four_letters_were_changed_is_not_a_blob(int at)
    {
        byte[] blob = Sealed();
        blob[at] ^= 0x01;

        AssertNotOpened(blob, BlobProblem.NotABlob);
    }

    [Theory]
    [InlineData(4)]     // the first byte of the nonce
    [InlineData(15)]    // its last
    [InlineData(16)]    // the first byte of the ciphertext: the header's length
    [InlineData(100)]   // inside the header
    [InlineData(193)]   // inside the file
    [InlineData(197)]   // the first byte of the tag
    [InlineData(212)]   // its last
    public void A_blob_with_one_bit_changed_anywhere_after_the_letters_does_not_authenticate(int at)
    {
        byte[] blob = Sealed();
        blob[at] ^= 0x01;

        AssertNotOpened(blob, BlobProblem.NotAuthentic);
    }

    [Fact]
    public void A_blob_made_with_another_key_does_not_authenticate()
    {
        using var other = SyncKeys.FromMaster([.. Enumerable.Repeat((byte)9, 32)]);

        AssertNotOpened(Sealed(), BlobProblem.NotAuthentic, keys: other);
    }

    [Fact]
    public void A_blob_moved_to_another_name_does_not_authenticate()
    {
        string elsewhere = _keys.BlobName("library/snippets/seal.md");

        AssertNotOpened(Sealed(), BlobProblem.NotAuthentic, name: elsewhere);
    }

    [Fact]
    public void A_blob_cut_short_or_with_bytes_added_does_not_authenticate()
    {
        byte[] blob = Sealed();

        AssertNotOpened(blob[..^1], BlobProblem.NotAuthentic);
        AssertNotOpened([.. blob, 0], BlobProblem.NotAuthentic);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(35)]    // one byte short of the letters, a nonce, a header length and a tag
    public void Bytes_too_few_to_be_a_blob_are_not_a_blob(int length)
    {
        AssertNotOpened(Sealed()[..length], BlobProblem.NotABlob);
    }

    [Theory]
    [InlineData("")]
    [InlineData("3586f21f7da74da9be019aaf2466c32e8b492c67ddb61f4487b3f85dc0873c0")]    // 63 digits
    [InlineData("3586F21F7DA74DA9BE019AAF2466C32E8B492C67DDB61F4487B3F85DC0873C00")]   // upper case
    [InlineData("../../../../../../../../../../../../../../../../../../../../etc/pw")]   // 64 characters that are no name
    public void A_name_that_is_not_a_blobs_name_opens_nothing_and_seals_nothing(string name)
    {
        AssertNotOpened(Sealed(), BlobProblem.NotABlob, name: name);
        Assert.Throws<ArgumentException>(() => BlobCodec.Seal(_keys, name, Header, Body));
    }

    [Fact]
    public void A_tombstone_cannot_be_given_content()
    {
        Assert.Throws<ArgumentException>(() => BlobCodec.Seal(_keys, Name, new BlobHeader(Path, Noon, Deleted: true, ""), Body));
    }

    [Fact]
    public void An_authentic_blob_whose_header_length_reaches_past_its_end_is_refused()
    {
        byte[] plain = [0xff, 0xff, 0xff, 0x7f, .. "{}"u8];

        AssertNotOpened(SealedRaw(Name, plain), BlobProblem.BadContent);
    }

    [Fact]
    public void An_authentic_blob_whose_header_is_larger_than_the_limit_is_refused()
    {
        string padding = new(' ', SyncLimits.MaxHeaderBytes);

        AssertNotOpened(SealedWithHeader(Name, HeaderJson(Path, BlobCodec.Hash([])) + padding), BlobProblem.BadContent);
    }

    [Theory]
    [InlineData("""not json""")]
    [InlineData("""[]""")]
    [InlineData("""{}""")]
    [InlineData("""{"path":"library/snippets/otter.md","mtimeUtc":"2026-10-03T12:00:00.0000000Z","deleted":false}""")]
    [InlineData("""{"path":7,"mtimeUtc":"2026-10-03T12:00:00.0000000Z","deleted":false,"sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}""")]
    [InlineData("""{"path":"library/snippets/otter.md","mtimeUtc":"yesterday","deleted":false,"sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}""")]
    [InlineData("""{"path":"library/snippets/otter.md","mtimeUtc":"2026-10-03T12:00:00.0000000Z","deleted":"no","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}""")]
    [InlineData("""{"path":"library/snippets/otter.md","mtimeUtc":"2026-10-03T12:00:00.0000000Z","deleted":false,"sha256":"E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855"}""")]
    [InlineData("""{"path":"library/snippets/otter.md","path":"library/snippets/otter.md","mtimeUtc":"2026-10-03T12:00:00.0000000Z","deleted":false,"sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}""")]
    [InlineData("""{"path":"library/snippets/otter.md","mtimeUtc":"2026-10-03T12:00:00.0000000Z","mtimeUtc":"2026-10-03T12:00:00.0000000Z","deleted":false,"sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}""")]
    [InlineData("""{"path":"library/snippets/otter.md","mtimeUtc":"2026-10-03T12:00:00.0000000Z","deleted":false,"deleted":false,"sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}""")]
    [InlineData("""{"path":"library/snippets/otter.md","mtimeUtc":"2026-10-03T12:00:00.0000000Z","deleted":false,"sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}""")]
    [InlineData("""{"path":"library/snippets/otter.md","mtimeUtc":"2026-10-03T12:00:00.0000000Z","deleted":false,"sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"} {}""")]
    public void An_authentic_blob_whose_header_is_not_the_four_values_once_each_is_refused(string header)
    {
        AssertNotOpened(SealedWithHeader(Name, header), BlobProblem.BadContent);
    }

    [Theory]
    [InlineData("2026-10-03T12:00:00Z")]                  // no fraction
    [InlineData("2026-10-03T12:00:00.000Z")]              // three digits of fraction
    [InlineData("2026-10-03T12:00:00.00000000Z")]         // eight
    [InlineData("2026-10-03T12:00:00.0000000+00:00")]     // an offset in place of Z
    [InlineData("2026-10-03T12:00:00.0000000z")]          // a lower-case z
    [InlineData("2026-10-03 12:00:00.0000000Z")]          // a space in place of the T
    [InlineData(" 2026-10-03T12:00:00.0000000Z")]         // a space before
    [InlineData("2026-10-03T12:00:00.0000000Z ")]         // and after
    [InlineData("0000-10-03T12:00:00.0000000Z")]          // year 0000
    [InlineData("2026-02-30T12:00:00.0000000Z")]          // 30 February
    [InlineData("2026-10-03T24:00:00.0000000Z")]          // hour 24
    [InlineData("2026-10-03T12:00:60.0000000Z")]          // second 60
    public void An_authentic_blob_whose_time_is_not_the_one_form_this_code_writes_is_refused(string mtime)
    {
        AssertNotOpened(SealedWithHeader(Name, HeaderJson(Path, BlobCodec.Hash([]), mtime)), BlobProblem.BadContent);
    }

    [Fact]
    public void The_same_header_with_the_one_form_of_time_is_opened()
    {
        // The control of the two tests above: the header that is refused for its time is otherwise whole.
        Assert.NotNull(BlobCodec.Open(_keys, Name, SealedWithHeader(Name, HeaderJson(Path, BlobCodec.Hash([]))), out var problem));
        Assert.Equal(BlobProblem.None, problem);
    }

    [Fact]
    public void An_authentic_blob_whose_path_holds_half_of_a_surrogate_pair_written_as_an_escape_is_refused_and_throws_nothing()
    {
        // The JSON text holds a backslash and u d 8 0 0: it is valid JSON, and not valid text.
        AssertNotOpened(SealedWithHeader(Name, HeaderJson("library/snippets/ot\\ud800ter.md", BlobCodec.Hash([]))), BlobProblem.BadContent);
        AssertNotOpened(SealedWithHeader(Name, HeaderJson("library/snippets/ot\\udc00ter.md", BlobCodec.Hash([]))), BlobProblem.BadContent);
    }

    [Fact]
    public void An_authentic_blob_whose_path_holds_a_byte_that_is_not_UTF_8_is_refused_and_throws_nothing()
    {
        byte[] header =
        [
            .. Encoding.UTF8.GetBytes("""{"path":"library/snippets/ot"""),
            0xff,
            .. Encoding.UTF8.GetBytes($$"""ter.md","mtimeUtc":"2026-10-03T12:00:00.0000000Z","deleted":false,"sha256":"{{BlobCodec.Hash([])}}"}"""),
        ];

        AssertNotOpened(SealedWithHeaderBytes(Name, header), BlobProblem.BadContent);
    }

    [Fact]
    public void A_failed_authentication_leaves_nothing_of_the_plain_bytes_in_the_array()
    {
        byte[] changedTag = Sealed();
        changedTag[^1] ^= 0x01;
        byte[] otherName = Sealed();

        AssertNotOpened(changedTag, BlobProblem.NotAuthentic);
        AssertNotOpened(otherName, BlobProblem.NotAuthentic, name: _keys.BlobName("library/snippets/seal.md"));

        // The part between the nonce and the tag, where the ciphertext was decrypted in place.
        Assert.False(changedTag.AsSpan(16, changedTag.Length - 32).ContainsAnyExcept((byte)0));
        Assert.False(otherName.AsSpan(16, otherName.Length - 32).ContainsAnyExcept((byte)0));
    }

    [Fact]
    public void White_space_after_the_header_inside_its_length_is_allowed_up_to_the_limit_and_changes_nothing()
    {
        // This pins the room a later version has to pad a blob, so that tombstones need not be
        // told apart by their size: a header of any length up to the limit that ends in white
        // space is the same header, and the content is the same content.
        string json = HeaderJson(Path, BodyHash);
        int room = SyncLimits.MaxHeaderBytes - Encoding.UTF8.GetByteCount(json);

        foreach (int padding in new[] { 1, 100, room })
        {
            var opened = BlobCodec.Open(_keys, Name, SealedWithHeader(Name, json + new string(' ', padding), Body), out var problem);

            Assert.Equal(BlobProblem.None, problem);
            Assert.NotNull(opened);
            Assert.Equal(Header, opened.Header);
            Assert.Equal(Body, opened.Content.ToArray());
        }
    }

    [Fact]
    public void A_header_may_hold_values_this_version_does_not_know()
    {
        string header = """{"path":"library/snippets/otter.md","mtimeUtc":"2026-10-03T12:00:00.0000000Z","deleted":false,"sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855","mode":420}""";

        Assert.NotNull(BlobCodec.Open(_keys, Name, SealedWithHeader(Name, header), out var problem));
        Assert.Equal(BlobProblem.None, problem);
    }

    [Theory]
    [InlineData("../../Windows/System32/drivers/etc/hosts")]
    [InlineData("C:/Users/otter/Startup/run.cmd")]
    [InlineData("library/snippets/otter.md:hidden")]
    [InlineData("//server/share/otter.md")]
    [InlineData("library/NUL")]
    [InlineData("library\\\\snippets\\\\otter.md")]
    public void An_authentic_blob_whose_path_is_not_a_safe_relative_path_is_refused(string path)
    {
        // Named as the path would be named, so that only the path's own shape can refuse it.
        string name = _keys.BlobName(SyncPath.Key(path.Replace("\\\\", "\\")));

        AssertNotOpened(SealedWithHeader(name, HeaderJson(path, BlobCodec.Hash([]))), BlobProblem.BadContent, name: name);
    }

    [Theory]
    [InlineData("clipboard/OTTER-PC/.~lock")]
    [InlineData("library/snippets/otter.md.tmp")]
    [InlineData("library/~$otter.docx")]
    public void An_authentic_blob_for_a_file_that_is_never_synced_is_refused(string path)
    {
        string name = _keys.BlobName(SyncPath.Key(path));

        AssertNotOpened(SealedWithHeader(name, HeaderJson(path, BlobCodec.Hash([]))), BlobProblem.BadContent, name: name);
    }

    [Fact]
    public void An_authentic_blob_stored_under_the_name_of_another_path_is_refused()
    {
        // Sealed for the name of seal.md, so it authenticates there, and it says it is otter.md.
        string elsewhere = _keys.BlobName("library/snippets/seal.md");

        AssertNotOpened(SealedWithHeader(elsewhere, HeaderJson(Path, BodyHash), Body), BlobProblem.BadContent, name: elsewhere);
    }

    [Fact]
    public void A_path_spelt_in_other_case_belongs_under_the_same_name()
    {
        byte[] blob = SealedWithHeader(Name, HeaderJson("Library/Snippets/Otter.md", BodyHash), Body);

        var opened = BlobCodec.Open(_keys, Name, blob, out _);

        Assert.NotNull(opened);
        Assert.Equal("Library/Snippets/Otter.md", opened.Header.Path);
    }

    [Fact]
    public void An_authentic_blob_whose_content_does_not_match_its_hash_is_refused()
    {
        AssertNotOpened(SealedWithHeader(Name, HeaderJson(Path, BlobCodec.Hash([1])), Body), BlobProblem.BadContent);
    }

    [Fact]
    public void An_authentic_tombstone_that_carries_content_or_a_hash_is_refused()
    {
        AssertNotOpened(SealedWithHeader(Name, HeaderJson(Path, "", deleted: "true"), Body), BlobProblem.BadContent);
        AssertNotOpened(SealedWithHeader(Name, HeaderJson(Path, BodyHash, deleted: "true")), BlobProblem.BadContent);
    }

    [Theory]
    [InlineData("3586f21f7da74da9be019aaf2466c32e8b492c67ddb61f4487b3f85dc0873c00", true)]
    [InlineData("3586f21f7da74da9be019aaf2466c32e8b492c67ddb61f4487b3f85dc0873c0", false)]
    [InlineData("3586f21f7da74da9be019aaf2466c32e8b492c67ddb61f4487b3f85dc0873c000", false)]
    [InlineData("3586f21f7da74da9be019aaf2466c32e8b492c67ddb61f4487b3f85dc0873c0g", false)]
    [InlineData("3586F21F7DA74DA9BE019AAF2466C32E8B492C67DDB61F4487B3F85DC0873C00", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_blob_name_is_exactly_64_lower_case_hex_digits(string? name, bool expected)
    {
        Assert.Equal(expected, BlobCodec.IsBlobName(name));
    }

    [Fact]
    public void A_temporary_name_is_the_blobs_name_a_mark_and_16_hex_digits_and_is_new_each_time()
    {
        string first = BlobCodec.TemporaryName(Name);
        string second = BlobCodec.TemporaryName(Name);

        Assert.StartsWith(Name + ".tmp-", first);
        Assert.Equal(64 + 5 + 16, first.Length);
        Assert.True(BlobCodec.IsTemporaryName(first));
        Assert.False(BlobCodec.IsBlobName(first));
        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("3586f21f7da74da9be019aaf2466c32e8b492c67ddb61f4487b3f85dc0873c00.tmp-0123456789abcdef", true)]
    [InlineData("3586f21f7da74da9be019aaf2466c32e8b492c67ddb61f4487b3f85dc0873c00.tmp-0123456789abcde", false)]
    [InlineData("3586f21f7da74da9be019aaf2466c32e8b492c67ddb61f4487b3f85dc0873c00.tmp-0123456789ABCDEF", false)]
    [InlineData("3586f21f7da74da9be019aaf2466c32e8b492c67ddb61f4487b3f85dc0873c00.TMP-0123456789abcdef", false)]
    [InlineData("3586f21f7da74da9be019aaf2466c32e8b492c67ddb61f4487b3f85dc0873c00", false)]
    [InlineData("zz86f21f7da74da9be019aaf2466c32e8b492c67ddb61f4487b3f85dc0873c00.tmp-0123456789abcdef", false)]
    [InlineData(null, false)]
    public void A_temporary_name_is_known_by_its_exact_shape(string? name, bool expected)
    {
        Assert.Equal(expected, BlobCodec.IsTemporaryName(name));
    }

    [Fact]
    public void A_header_and_an_opened_blob_print_nothing_of_the_owners()
    {
        var opened = BlobCodec.Open(_keys, Name, Sealed(), out _);

        Assert.Equal("BlobHeader { Deleted = False }", Header.ToString());
        Assert.Equal("OpenedBlob { Bytes = 7 }", opened!.ToString());
    }
}
