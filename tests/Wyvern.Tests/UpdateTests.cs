using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Wyvern.Core.Settings;
using Wyvern.Core.Update;

namespace Wyvern.Tests;

/// <summary>
/// The updater: where it may look, what it trusts, what it fetches and what it keeps. The
/// server is a table the test fills and the signing key is made for the test: nothing here
/// goes near a network.
/// </summary>
public sealed class UpdateTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ECDsa _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _publicKey;
    private readonly Server _server = new();
    private readonly List<string> _reported = [];

    public UpdateTests()
    {
        var point = _signer.ExportParameters(includePrivateParameters: false).Q;
        _publicKey = [.. point.X!, .. point.Y!];
    }

    public void Dispose()
    {
        _signer.Dispose();
        _dir.Dispose();
    }

    private const string Address = "http://100.64.0.108:8795/";

    private static readonly byte[] Installer = Encoding.ASCII.GetBytes("a made-up setup program, forty-one bytes.");

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static byte[] Manifest(string version = "1.8.1", string? file = null, long? size = null, string? sha256 = null) =>
        Encoding.UTF8.GetBytes($$"""{"version":"{{version}}","file":"{{file ?? "WyvernSetup-" + version + ".exe"}}","size":{{size ?? Installer.Length}},"sha256":"{{sha256 ?? Hash(Installer)}}"}""");

    private byte[] Sign(byte[] data, ECDsa? with = null) =>
        Encoding.ASCII.GetBytes(Convert.ToBase64String((with ?? _signer).SignData(data, HashAlgorithmName.SHA256)) + "\n");

    private sealed class Server : IUpdateSource
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

        public List<string> Asked { get; } = [];

        public Exception? Broken { get; set; }

        /// <summary>Called after each small file has been handed out, with its address: a test changes what the server holds between two looks.</summary>
        public Action<string>? WhenAsked { get; set; }

        public byte[]? Fetch(string url, int maxBytes)
        {
            Asked.Add(url);
            try
            {
                return Answer(url, maxBytes);
            }
            finally
            {
                WhenAsked?.Invoke(url);
            }
        }

        private byte[]? Answer(string url, int maxBytes)
        {
            if (Broken is not null)
            {
                throw Broken;
            }

            return Files.TryGetValue(url, out byte[]? bytes) && bytes.Length <= maxBytes ? bytes : null;
        }

        public bool Download(string url, string toFile, long maxBytes)
        {
            Asked.Add(url);
            if (!Files.TryGetValue(url, out byte[]? bytes) || bytes.Length > maxBytes)
            {
                return false;
            }

            File.WriteAllBytes(toFile, bytes);
            return true;
        }
    }

    private string Folder => _dir.File("updates");

    private UpdateService Service(string running = "1.8.0") =>
        new(new UpdateChannel(Address, _publicKey), Version.Parse(running), _server, Folder, (words, _) => _reported.Add(words));

    private void Publish(byte[] manifest, byte[]? installer = null, string version = "1.8.1")
    {
        _server.Files[Address + "latest.json"] = manifest;
        _server.Files[Address + "latest.json.sig"] = Sign(manifest);
        _server.Files[Address + "WyvernSetup-" + version + ".exe"] = installer ?? Installer;
    }

    // Where it may look

    [Theory]
    [InlineData("http://100.64.0.108:8795/", "http://100.64.0.108:8795/")]
    [InlineData("http://100.64.0.108:8795", "http://100.64.0.108:8795/")]
    [InlineData("http://100.64.0.1/", "http://100.64.0.1/")]
    [InlineData("http://100.127.255.255:80/wyvern/updates", "http://100.127.255.255:80/wyvern/updates/")]
    [InlineData("http://100.100.100.100:65535/a-b_c.1/", "http://100.100.100.100:65535/a-b_c.1/")]
    public void An_address_inside_the_private_network_is_taken_and_ends_in_a_slash(string written, string address)
    {
        Assert.True(UpdateChannel.TryAddress(written, out string taken));
        Assert.Equal(address, taken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://100.64.0.108:8795/")]
    [InlineData("HTTP://100.64.0.108:8795/")]
    [InlineData("ftp://100.64.0.108/")]
    [InlineData("http://my-server:8795/")]
    [InlineData("http://example.com/")]
    [InlineData("http://192.168.50.25:8795/")]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://127.0.0.1:8795/")]
    [InlineData("http://100.63.255.255/")]
    [InlineData("http://100.128.0.1/")]
    [InlineData("http://101.64.0.108/")]
    [InlineData("http://100.64.0/")]
    [InlineData("http://100.64.0.108.5/")]
    [InlineData("http://100.64.0.256/")]
    [InlineData("http://100.64.0.1080/")]
    [InlineData("http://100.64.0.-1/")]
    [InlineData("http://100.64.0.108:0/")]
    [InlineData("http://100.64.0.108:65536/")]
    [InlineData("http://100.64.0.108:/")]
    [InlineData("http://100.64.0.108:87a5/")]
    [InlineData("http://user@100.64.0.108:8795/")]
    [InlineData("http://100.64.0.108@example.com/")]
    [InlineData("http://100.64.0.108:8795/?to=http://example.com")]
    [InlineData("http://100.64.0.108:8795/a/../b/")]
    [InlineData("http://100.64.0.108:8795/a b/")]
    [InlineData("http://100.64.0.108:8795/#x")]
    [InlineData("http://[::1]:8795/")]
    [InlineData("http:// 100.64.0.108/")]
    [InlineData("100.64.0.108:8795")]
    public void Anything_else_is_no_address_for_updates(string? written)
    {
        Assert.False(UpdateChannel.TryAddress(written, out string taken));
        Assert.Equal("", taken);
    }

    [Fact]
    public void The_channel_that_is_built_in_is_an_address_and_a_key()
    {
        string key = Convert.ToBase64String(_publicKey);

        var channel = UpdateChannel.Parse($$"""{ "url": "http://100.64.0.108:8795", "publicKey": "{{key}}" }""");

        Assert.Equal(Address, channel!.Address);
        Assert.Equal(_publicKey, channel.PublicKey);
        Assert.Equal(Address + "latest.json", channel.UrlOf("latest.json"));
        Assert.Equal("UpdateChannel", channel.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "url": "http://100.64.0.108:8795/" }""")]
    [InlineData("""{ "publicKey": "AAAA" }""")]
    [InlineData("""{ "url": "http://example.com/", "publicKey": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==" }""")]
    [InlineData("""{ "url": "http://100.64.0.108:8795/", "publicKey": "AAAA" }""")]
    [InlineData("""{ "url": "http://100.64.0.108:8795/", "publicKey": "not base64!" }""")]
    [InlineData("""{ "url": 5, "publicKey": 6 }""")]
    public void A_channel_that_cannot_be_used_is_no_channel(string? json) => Assert.Null(UpdateChannel.Parse(json));

    [Theory]
    [InlineData("")]
    [InlineData("../latest.json")]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData(".hidden")]
    [InlineData("-o")]
    [InlineData("latest.json?x=1")]
    public void Only_a_plain_file_name_is_asked_of_the_server(string name)
    {
        var channel = new UpdateChannel(Address, _publicKey);

        Assert.Throws<ArgumentException>(() => channel.UrlOf(name));
    }

    // What it trusts

    [Fact]
    public void A_signed_manifest_of_a_higher_version_is_an_offer()
    {
        byte[] manifest = Manifest();

        var check = UpdateManifest.Read(manifest, Sign(manifest), _publicKey, new Version(1, 8, 0));

        Assert.Equal(UpdateVerdict.Newer, check.Verdict);
        Assert.Equal(new UpdateOffer(new Version(1, 8, 1), "WyvernSetup-1.8.1.exe", Installer.Length, Hash(Installer)), check.Offer);
        Assert.Equal("1.8.1", check.Offer!.VersionText);
    }

    [Theory]
    [InlineData("1.8.1", "1.8.1")]
    [InlineData("1.8.1", "1.8.1.0")]
    [InlineData("1.8.1", "1.8.2")]
    [InlineData("1.8.1", "1.10.0")]
    [InlineData("1.8.1", "2.0.0")]
    public void The_same_version_or_an_older_one_is_never_offered(string offered, string running)
    {
        byte[] manifest = Manifest(offered);

        var check = UpdateManifest.Read(manifest, Sign(manifest), _publicKey, Version.Parse(running));

        Assert.Equal(UpdateVerdict.UpToDate, check.Verdict);
        Assert.Null(check.Offer);
    }

    [Theory]
    [InlineData("1.8.2", "1.8.1")]
    [InlineData("1.10.0", "1.9.9")]
    [InlineData("2.0.0", "1.99.99")]
    [InlineData("1.8.1", "1.8.0.0")]
    public void A_higher_version_is_told_by_its_numbers_and_not_by_its_letters(string offered, string running)
    {
        byte[] manifest = Manifest(offered);

        Assert.Equal(UpdateVerdict.Newer, UpdateManifest.Read(manifest, Sign(manifest), _publicKey, Version.Parse(running)).Verdict);
    }

    [Fact]
    public void A_manifest_that_was_changed_after_it_was_signed_is_refused()
    {
        byte[] manifest = Manifest();
        byte[] signature = Sign(manifest);
        byte[] changed = Manifest(sha256: Hash(Encoding.ASCII.GetBytes("another program")));

        Assert.Equal(UpdateVerdict.NotSigned, UpdateManifest.Read(changed, signature, _publicKey, new Version(1, 8, 0)).Verdict);

        // One byte is enough.
        byte[] oneByte = [.. manifest];
        oneByte[^2] ^= 1;
        Assert.Equal(UpdateVerdict.NotSigned, UpdateManifest.Read(oneByte, signature, _publicKey, new Version(1, 8, 0)).Verdict);
    }

    [Fact]
    public void A_manifest_signed_with_another_key_is_refused()
    {
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] manifest = Manifest("9.9.9");

        var check = UpdateManifest.Read(manifest, Sign(manifest, stranger), _publicKey, new Version(1, 8, 0));

        Assert.Equal(UpdateVerdict.NotSigned, check.Verdict);
        Assert.Null(check.Offer);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 at all!")]
    [InlineData("AAAA")]
    public void A_signature_that_is_none_is_refused(string signature)
    {
        Assert.Equal(UpdateVerdict.NotSigned, UpdateManifest.Read(Manifest(), Encoding.ASCII.GetBytes(signature), _publicKey, new Version(1, 8, 0)).Verdict);
    }

    [Fact]
    public void A_manifest_or_a_signature_that_is_too_large_is_not_looked_at()
    {
        byte[] large = Encoding.ASCII.GetBytes("{" + new string(' ', UpdateManifest.MaxBytes) + "}");
        byte[] manifest = Manifest();

        Assert.Equal(UpdateVerdict.NotSigned, UpdateManifest.Read(large, Sign(large), _publicKey, new Version(1, 8, 0)).Verdict);
        Assert.Equal(UpdateVerdict.NotSigned, UpdateManifest.Read(manifest, new byte[UpdateManifest.MaxSignatureBytes + 1], _publicKey, new Version(1, 8, 0)).Verdict);
        Assert.Equal(UpdateVerdict.NotSigned, UpdateManifest.Read([], Sign([]), _publicKey, new Version(1, 8, 0)).Verdict);
    }

    [Fact]
    public void A_key_that_is_no_key_verifies_nothing()
    {
        byte[] manifest = Manifest();
        byte[] signature = _signer.SignData(manifest, HashAlgorithmName.SHA256);

        Assert.True(UpdateManifest.Verify(manifest, signature, _publicKey));
        Assert.False(UpdateManifest.Verify(manifest, signature, new byte[64]));
        Assert.False(UpdateManifest.Verify(manifest, signature, new byte[63]));
        Assert.False(UpdateManifest.Verify(manifest, signature[..63], _publicKey));
        Assert.False(UpdateManifest.Verify(manifest, [], _publicKey));
    }

    public static TheoryData<string> BadManifests() =>
    [
        "not json",
        "[]",
        "{}",
        """{"version":"1.8.1","file":"WyvernSetup-1.8.1.exe","size":41}""",
        """{"version":"1.8","file":"WyvernSetup-1.8.exe","size":41,"sha256":"H"}""",
        """{"version":"1.8.1.0","file":"WyvernSetup-1.8.1.0.exe","size":41,"sha256":"H"}""",
        """{"version":"1.8.x","file":"WyvernSetup-1.8.x.exe","size":41,"sha256":"H"}""",
        """{"version":"1.8.12345","file":"WyvernSetup-1.8.12345.exe","size":41,"sha256":"H"}""",
        """{"version":1.8,"file":"WyvernSetup-1.8.1.exe","size":41,"sha256":"H"}""",
        """{"version":"1.8.1","file":"WyvernSetup-1.8.2.exe","size":41,"sha256":"H"}""",
        """{"version":"1.8.1","file":"..\\WyvernSetup-1.8.1.exe","size":41,"sha256":"H"}""",
        """{"version":"1.8.1","file":"cmd.exe","size":41,"sha256":"H"}""",
        """{"version":"1.8.1","file":"wyvernsetup-1.8.1.exe","size":41,"sha256":"H"}""",
        """{"version":"1.8.1","file":"WyvernSetup-1.8.1.exe","size":0,"sha256":"H"}""",
        """{"version":"1.8.1","file":"WyvernSetup-1.8.1.exe","size":-5,"sha256":"H"}""",
        """{"version":"1.8.1","file":"WyvernSetup-1.8.1.exe","size":41.5,"sha256":"H"}""",
        """{"version":"1.8.1","file":"WyvernSetup-1.8.1.exe","size":"41","sha256":"H"}""",
        """{"version":"1.8.1","file":"WyvernSetup-1.8.1.exe","size":99999999999,"sha256":"H"}""",
        """{"version":"1.8.1","file":"WyvernSetup-1.8.1.exe","size":41,"sha256":"abc"}""",
        """{"version":"1.8.1","file":"WyvernSetup-1.8.1.exe","size":41,"sha256":"HU"}""",
        """{"version":"1.8.1","file":"WyvernSetup-1.8.1.exe","size":41,"sha256":5}""",
    ];

    [Theory]
    [MemberData(nameof(BadManifests))]
    public void A_signed_manifest_that_is_not_in_its_plain_form_is_not_used(string text)
    {
        // "H" stands for a proper hash and "HU" for the same in capitals.
        string proper = Hash(Installer);
        byte[] manifest = Encoding.UTF8.GetBytes(text.Replace("\"HU\"", "\"" + proper.ToUpperInvariant() + "\"").Replace("\"H\"", "\"" + proper + "\""));

        var check = UpdateManifest.Read(manifest, Sign(manifest), _publicKey, new Version(1, 8, 0));

        Assert.Equal(UpdateVerdict.Unreadable, check.Verdict);
        Assert.Null(check.Offer);
    }

    [Fact]
    public void A_file_is_the_setup_program_only_with_the_size_and_the_hash_that_were_signed()
    {
        var offer = new UpdateOffer(new Version(1, 8, 1), "WyvernSetup-1.8.1.exe", Installer.Length, Hash(Installer));
        string right = _dir.File("right.exe");
        string changed = _dir.File("changed.exe");
        string longer = _dir.File("longer.exe");
        File.WriteAllBytes(right, Installer);
        byte[] other = [.. Installer];
        other[5] ^= 1;
        File.WriteAllBytes(changed, other);
        File.WriteAllBytes(longer, [.. Installer, 0]);

        Assert.True(UpdateManifest.FileMatches(right, offer));
        Assert.False(UpdateManifest.FileMatches(changed, offer));
        Assert.False(UpdateManifest.FileMatches(longer, offer));
        Assert.False(UpdateManifest.FileMatches(_dir.File("missing.exe"), offer));
        Assert.False(UpdateManifest.FileMatches(_dir.Path, offer));
    }

    // What it fetches with

    [Fact]
    public void Curl_is_started_to_fetch_one_address_into_one_file_and_for_nothing_else()
    {
        var arguments = UpdateDownload.CurlArguments(Address + "latest.json", @"C:\made-up\updates\x.fetch", 4096, 20);

        Assert.Equal(
            [
                "-q", "--silent", "--fail", "--ipv4", "--noproxy", "*", "--proto", "=http", "--max-redirs", "0",
                "--connect-timeout", "8", "--max-time", "20", "--max-filesize", "4096", "--user-agent", "Wyvern",
                "--output", @"C:\made-up\updates\x.fetch", "--url", Address + "latest.json",
            ],
            arguments);
    }

    [Fact]
    public void No_argument_of_curl_could_send_anything()
    {
        var arguments = UpdateDownload.CurlArguments(Address + "WyvernSetup-1.8.1.exe", @"C:\made-up\x.part", 9_000_000, 900);
        string[] sending =
        [
            "-d", "--data", "--data-binary", "--data-raw", "--data-urlencode", "-F", "--form", "--form-string", "-T", "--upload-file",
            "-X", "--request", "-H", "--header", "-b", "--cookie", "-u", "--user", "-K", "--config", "-L", "--location", "-x", "--proxy", "--json",
            "-e", "--referer", "--next",
        ];

        Assert.DoesNotContain(arguments, argument => sending.Contains(argument, StringComparer.Ordinal));
        Assert.Equal("-q", arguments[0]);
    }

    // The service

    [Fact]
    public void A_look_asks_for_the_two_small_files_and_finds_the_newer_version()
    {
        Publish(Manifest());

        var check = Service().Check();

        Assert.Equal(UpdateVerdict.Newer, check.Verdict);
        Assert.Equal("1.8.1", check.Offer!.VersionText);
        Assert.Equal([Address + "latest.json", Address + "latest.json.sig"], _server.Asked);
        Assert.Empty(_reported);
    }

    [Fact]
    public void A_server_that_is_not_there_or_has_nothing_is_unreachable_and_that_is_no_failure()
    {
        Assert.Equal(UpdateVerdict.Unreachable, Service().Check().Verdict);

        // The manifest without its signature is as good as nothing.
        _server.Files[Address + "latest.json"] = Manifest();
        Assert.Equal(UpdateVerdict.Unreachable, Service().Check().Verdict);
        Assert.Empty(_reported);
    }

    [Fact]
    public void A_look_that_throws_is_reported_and_counts_as_unreachable()
    {
        _server.Broken = new InvalidOperationException("no");

        Assert.Equal(UpdateVerdict.Unreachable, Service().Check().Verdict);
        Assert.Equal(["Looking for an update failed"], _reported);
    }

    [Fact]
    public void A_look_that_falls_between_the_two_files_of_a_new_update_looks_once_more()
    {
        // The new manifest is there, its signature is still the one of the manifest before.
        byte[] before = Manifest("1.8.1");
        byte[] after = Manifest("1.8.2");
        _server.Files[Address + "latest.json"] = after;
        _server.Files[Address + "latest.json.sig"] = Sign(before);
        int looks = 0;
        _server.WhenAsked = url =>
        {
            // By the second look the signature has arrived as well.
            if (url.EndsWith(".sig", StringComparison.Ordinal) && ++looks == 1)
            {
                _server.Files[Address + "latest.json.sig"] = Sign(after);
            }
        };

        var check = Service().Check();

        Assert.Equal(UpdateVerdict.Newer, check.Verdict);
        Assert.Equal("1.8.2", check.Offer!.VersionText);
        Assert.Equal(4, _server.Asked.Count);
    }

    [Fact]
    public void What_is_not_signed_is_asked_for_twice_and_no_more()
    {
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] manifest = Manifest("9.9.9");
        _server.Files[Address + "latest.json"] = manifest;
        _server.Files[Address + "latest.json.sig"] = Sign(manifest, stranger);

        Assert.Equal(UpdateVerdict.NotSigned, Service().Check().Verdict);
        Assert.Equal(4, _server.Asked.Count);
    }

    [Fact]
    public void The_running_version_is_never_offered_to_itself()
    {
        Publish(Manifest());

        Assert.Equal(UpdateVerdict.UpToDate, Service(running: "1.8.1.0").Check().Verdict);
        Assert.Equal(new Version(1, 8, 1), Service(running: "1.8.1.0").Running);
    }

    [Fact]
    public void The_setup_program_is_fetched_into_the_updates_folder_and_kept_when_it_is_the_one_that_was_signed()
    {
        Publish(Manifest());
        var service = Service();
        var offer = service.Check().Offer!;

        string? file = service.Download(offer);

        Assert.Equal(Path.Combine(Folder, "WyvernSetup-1.8.1.exe"), file);
        Assert.Equal(Installer, File.ReadAllBytes(file!));
        Assert.Equal(["WyvernSetup-1.8.1.exe"], Directory.GetFiles(Folder).Select(Path.GetFileName));

        // Asked for again, it is not fetched again.
        int asked = _server.Asked.Count;
        Assert.Equal(file, service.Download(offer));
        Assert.Equal(asked, _server.Asked.Count);
    }

    [Fact]
    public void A_setup_program_that_is_not_the_one_that_was_signed_is_deleted_and_said_so()
    {
        Publish(Manifest(), installer: Encoding.ASCII.GetBytes("a made-up setup program, forty-one byteZ."));
        var service = Service();
        var offer = service.Check().Offer!;

        Assert.Null(service.Download(offer));

        Assert.Empty(Directory.GetFiles(Folder));
        Assert.Equal(["The setup program of version 1.8.1 was refused: it is not the file that was signed"], _reported);
    }

    [Fact]
    public void A_setup_program_that_cannot_be_fetched_leaves_nothing_behind()
    {
        Publish(Manifest());
        var service = Service();
        var offer = service.Check().Offer!;
        _server.Files.Remove(Address + "WyvernSetup-1.8.1.exe");

        Assert.Null(service.Download(offer));

        Assert.Empty(Directory.GetFiles(Folder));
        Assert.Empty(_reported);
    }

    [Fact]
    public void A_file_of_the_right_name_that_is_not_the_setup_program_is_fetched_over()
    {
        Publish(Manifest());
        var service = Service();
        var offer = service.Check().Offer!;
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "WyvernSetup-1.8.1.exe"), "left by something else");

        string? file = service.Download(offer);

        Assert.Equal(Installer, File.ReadAllBytes(file!));
    }

    [Fact]
    public void What_earlier_updates_left_is_removed_and_nothing_else_in_the_folder_is_touched()
    {
        Directory.CreateDirectory(Folder);
        foreach (string name in new[] { "WyvernSetup-1.7.0.exe", "WyvernSetup-1.7.1.exe.part", "ab12.fetch", "notes.txt", "Other-1.0.exe" })
        {
            File.WriteAllText(Path.Combine(Folder, name), "x");
        }

        Service().Tidy();

        Assert.Equal(["Other-1.0.exe", "notes.txt"], Directory.GetFiles(Folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        // With no folder at all there is nothing to do.
        Directory.Delete(Folder, recursive: true);
        Service().Tidy();
        Assert.False(Directory.Exists(Folder));
    }

    // The setting

    [Theory]
    [InlineData("{}", UpdateMode.Ask)]
    [InlineData("""{ "updates": "ask" }""", UpdateMode.Ask)]
    [InlineData("""{ "updates": "auto" }""", UpdateMode.Auto)]
    [InlineData("""{ "updates": " AUTO " }""", UpdateMode.Auto)]
    [InlineData("""{ "updates": "off" }""", UpdateMode.Off)]
    [InlineData("""{ "updates": false }""", UpdateMode.Off)]
    [InlineData("""{ "updates": true }""", UpdateMode.Ask)]
    [InlineData("""{ "updates": "sometimes" }""", UpdateMode.Ask)]
    [InlineData("""{ "updates": 5 }""", UpdateMode.Ask)]
    public void The_setting_says_whether_to_ask_to_install_by_itself_or_never_to_look(string json, UpdateMode mode)
    {
        string file = _dir.File("settings.json");
        File.WriteAllText(file, json);

        Assert.Equal(mode, UpdateOptions.From(SettingsDocument.Read(file)));
    }

    [Fact]
    public void Without_settings_it_asks()
    {
        Assert.Equal(UpdateMode.Ask, UpdateOptions.From(null));
        Assert.True(UpdateOptions.FirstLook < UpdateOptions.Between);
    }

    // Where curl is named

    [Fact]
    public void One_file_of_the_app_alone_starts_curl_and_takes_its_arguments_from_the_one_list()
    {
        var folder = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "Wyvern.slnx")))
        {
            folder = folder.Parent;
        }

        Assert.NotNull(folder);
        string src = Path.Combine(folder.FullName, "src");
        var naming = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(@"\obj\", StringComparison.Ordinal) && !file.Contains(@"\bin\", StringComparison.Ordinal))
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"curl\.exe"))
            .Select(file => Path.GetRelativePath(src, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["Wyvern.App/Interop/UpdateFiles.cs"], naming);
        string code = File.ReadAllText(Path.Combine(src, "Wyvern.App", "Interop", "UpdateFiles.cs"));
        Assert.Contains("UpdateDownload.CurlArguments(", code);
        Assert.DoesNotContain("ArgumentList.Add(\"-", code);
    }
}
