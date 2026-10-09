using Wyvern.Core.Sync;

namespace Wyvern.Tests;

/// <summary>
/// sync.json and the host key fingerprint. No test here opens a connection or asks Windows
/// about a path: these are rules about text and about one small file. The server, the user and
/// the paths are made up.
/// </summary>
public sealed class SyncConfigTests : IDisposable
{
    private const string Host = "otter.example.org";
    private const string User = "walrus";
    private const string KeyPath = @"C:\Keys\otter_ed25519";
    private const string RemoteDir = "/home/walrus/wyvern";

    // What "ssh-keygen -lf" printed for the public key below, which was made for this test and
    // is used nowhere.
    private const string PublicKey = "AAAAC3NzaC1lZDI1NTE5AAAAIFUzcIikMDzw3kNB6TQM65oaH4SzZwIO8bJmkx2qeX2a";
    private const string Fingerprint = "SHA256:AjAMBCdFlnzhT4An+L756BKl4RXyRER/GQvIadZqNKs";

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string SyncFile => _dir.File("sync.json");

    private static SyncConfig Config(string? fingerprint = Fingerprint)
    {
        Assert.True(SyncConfig.TryCreate(Host, "22", User, KeyPath, RemoteDir, fingerprint, out var config, out string problem), problem);
        return config!;
    }

    private SyncConfigLoad LoadText(string json)
    {
        File.WriteAllText(SyncFile, json);
        return SyncConfig.Load(SyncFile);
    }

    private static string Json(string host = Host, string port = "22", string user = User, string keyPath = @"C:\\Keys\\otter_ed25519", string remoteDir = RemoteDir, string? fingerprint = Fingerprint) =>
        $$"""
        {
          "host": "{{host}}",
          "port": {{port}},
          "user": "{{user}}",
          "privateKeyPath": "{{keyPath}}",
          "remoteDir": "{{remoteDir}}"{{(fingerprint is null ? "" : $",\n  \"hostKeyFingerprint\": \"{fingerprint}\"")}}
        }
        """;

    private static void AssertUnreadable(SyncConfigLoad loaded, string sentence)
    {
        Assert.Equal(SyncConfigState.Unreadable, loaded.State);
        Assert.Null(loaded.Config);
        Assert.Equal("sync.json cannot be used. " + sentence, loaded.Problem);
    }

    [Fact]
    public void The_fingerprint_of_a_host_key_is_what_ssh_keygen_prints_for_it()
    {
        Assert.Equal(Fingerprint, HostKeyFingerprint.Of(Convert.FromBase64String(PublicKey)));
    }

    [Fact]
    public void A_pinned_fingerprint_matches_its_own_key_and_no_other()
    {
        byte[] key = Convert.FromBase64String(PublicKey);
        byte[] other = [.. key];
        other[^1] ^= 1;

        Assert.True(HostKeyFingerprint.Matches(Fingerprint, key));
        Assert.False(HostKeyFingerprint.Matches(Fingerprint, other));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SHA256:")]
    [InlineData("AjAMBCdFlnzhT4An+L756BKl4RXyRER/GQvIadZqNKs")]             // no prefix
    [InlineData("sha256:AjAMBCdFlnzhT4An+L756BKl4RXyRER/GQvIadZqNKs")]      // the prefix in lower case
    [InlineData("SHA256:AjAMBCdFlnzhT4An+L756BKl4RXyRER/GQvIadZqNK")]       // 42 characters
    [InlineData("SHA256:AjAMBCdFlnzhT4An+L756BKl4RXyRER/GQvIadZqNKsA")]     // 44 characters, without the padding
    [InlineData("SHA256:AjAMBCdFlnzhT4An+L756BKl4RXyRER/GQvIadZqNKs=")]     // with the padding
    [InlineData("SHA256:AjAMBCdFlnzhT4An+L756BKl4RXyRER/GQvIadZqNK*")]      // not base64
    [InlineData("SHA256:AjAMBCdFlnzhT4An+L756BKl4RXyRER/GQvIadZqNKB")]      // 43 characters of base64 that no hash gives: the last one has bits that 32 bytes do not have
    [InlineData("MD5:97:70:33:82:fd:29:3a:73:39:af:6a:07:ad:f8:80:49")]
    public void Text_that_is_not_a_fingerprint_is_not_valid_and_matches_no_key(string? text)
    {
        Assert.False(HostKeyFingerprint.IsValid(text));
        Assert.False(HostKeyFingerprint.Matches(text, Convert.FromBase64String(PublicKey)));
    }

    [Fact]
    public void Every_fingerprint_that_a_key_gives_is_valid()
    {
        // Enough keys for each of the sixteen last characters that a fingerprint can end in.
        for (int i = 0; i < 400; i++)
        {
            string fingerprint = HostKeyFingerprint.Of(BitConverter.GetBytes(i));

            Assert.True(HostKeyFingerprint.IsValid(fingerprint), fingerprint);
            Assert.True(HostKeyFingerprint.Matches(fingerprint, BitConverter.GetBytes(i)), fingerprint);
        }
    }

    [Fact]
    public void Settings_that_were_typed_are_trimmed_and_taken()
    {
        Assert.True(SyncConfig.TryCreate("  " + Host + " ", " 2222 ", " " + User + " ", "  \"" + KeyPath + "\" ", RemoteDir + "///", " " + Fingerprint + " ", out var config, out string problem));

        Assert.Equal("", problem);
        Assert.Equal(new SyncConfig(Host, 2222, User, KeyPath, RemoteDir, Fingerprint), config);
        Assert.Equal("/home/walrus/wyvern/blobs", config!.BlobsDir);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_port_is_22_and_a_blank_fingerprint_is_none(string? blank)
    {
        Assert.True(SyncConfig.TryCreate(Host, blank, User, KeyPath, RemoteDir, blank, out var config, out _));

        Assert.Equal(22, config!.Port);
        Assert.Null(config.HostKeyFingerprint);
    }

    [Theory]
    [InlineData("100.64.0.5")]
    [InlineData("fd7a:115c:a1e0::5")]
    [InlineData("ubuntu")]
    [InlineData("my_server-2.lan")]
    [InlineData("::1")]                       // two colons: an address written without brackets
    public void A_name_or_an_address_is_a_host(string host)
    {
        Assert.True(SyncConfig.TryCreate(host, "22", User, KeyPath, RemoteDir, null, out var config, out _));
        Assert.Equal(host, config!.Host);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("otter example")]
    [InlineData("walrus@otter.example.org")]
    [InlineData("otter.example.org/path")]
    [InlineData("\\\\otter\\share")]
    [InlineData("otter\n.example.org")]
    [InlineData("ssh://otter.example.org")]
    [InlineData("-oProxyCommand")]            // would be an option, if a host ever reached a command line
    [InlineData("-otter.example.org")]
    [InlineData("-")]
    [InlineData(".")]                         // no letter or digit
    [InlineData("_")]
    [InlineData("...")]
    [InlineData(":")]
    [InlineData("::")]
    [InlineData(":::")]
    [InlineData("._-:")]
    public void Anything_else_is_not_a_host(string? host)
    {
        Assert.False(SyncConfig.TryCreate(host, "22", User, KeyPath, RemoteDir, null, out var config, out string problem));
        Assert.Null(config);
        Assert.Equal(SyncConfig.HostProblem, problem);
    }

    [Theory]
    [InlineData("otter.example.org:2222")]
    [InlineData("100.64.0.5:2222")]
    [InlineData("ssh:otter.example.org")]
    [InlineData("otter.example.org:")]
    public void A_host_with_one_colon_has_a_port_after_it_which_has_a_field_of_its_own(string host)
    {
        Assert.False(SyncConfig.TryCreate(host, "22", User, KeyPath, RemoteDir, null, out var config, out string problem));
        Assert.Null(config);
        Assert.Equal(SyncConfig.HostWithPortProblem, problem);
        Assert.Contains("port", problem);
        Assert.NotEqual(SyncConfig.HostProblem, problem);
    }

    [Theory]
    [InlineData(0x043E)]    // a Cyrillic o, which looks like a Latin one
    [InlineData(0x200B)]    // a zero-width space
    [InlineData(0xFEFF)]    // a byte order mark
    [InlineData(0xFF11)]    // a full-width digit one
    [InlineData(0x00E9)]    // an e with an accent
    public void A_host_with_a_look_alike_or_an_invisible_character_is_not_a_host(int code)
    {
        foreach (string host in new[] { "ot" + (char)code + "ter.example.org", "otter.example.org" + (char)code, (char)code + "otter.example.org" })
        {
            Assert.False(SyncConfig.TryCreate(host, "22", User, KeyPath, RemoteDir, null, out var config, out string problem));
            Assert.Null(config);
            Assert.Equal(SyncConfig.HostProblem, problem);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-22")]
    [InlineData("22.0")]
    [InlineData("ssh")]
    [InlineData("99999999999999999999")]
    public void A_port_is_a_whole_number_from_1_to_65535(string port)
    {
        Assert.False(SyncConfig.TryCreate(Host, port, User, KeyPath, RemoteDir, null, out _, out string problem));
        Assert.Equal(SyncConfig.PortProblem, problem);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("wal rus")]
    [InlineData("walrus\tadmin")]
    public void A_user_name_is_filled_in_and_has_no_white_space(string? user)
    {
        Assert.False(SyncConfig.TryCreate(Host, "22", user, KeyPath, RemoteDir, null, out _, out string problem));
        Assert.Equal(SyncConfig.UserProblem, problem);
    }

    // Each of these is a character in the middle of a name, and, when it is not white space that
    // the trimming takes off, in front of it and behind it. A lone half of a surrogate pair is
    // given by its number, since it cannot be text on its own.
    [Theory]
    [InlineData(0x0001)]    // a control character
    [InlineData(0x007F)]
    [InlineData(0x200B)]    // a zero-width space
    [InlineData(0x202E)]    // right-to-left override
    [InlineData(0xFEFF)]    // a byte order mark
    [InlineData(0x2028)]    // a line separator
    [InlineData(0x2029)]    // a paragraph separator
    [InlineData(0xD800)]    // a lone half of a surrogate pair
    [InlineData(0xDC00)]
    public void A_user_name_holds_no_control_or_invisible_character_and_no_lone_surrogate(int code)
    {
        char c = (char)code;
        var users = new List<string> { "wal" + c + "rus" };
        if (!char.IsWhiteSpace(c))
        {
            users.Add(c + "walrus");
            users.Add("walrus" + c);
        }

        foreach (string user in users)
        {
            Assert.False(SyncConfig.TryCreate(Host, "22", user, KeyPath, RemoteDir, null, out var config, out string problem));
            Assert.Null(config);
            Assert.Equal(SyncConfig.UserProblem, problem);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("otter_ed25519")]                         // not a full path
    [InlineData(@"Keys\otter_ed25519")]
    [InlineData(@"C:otter_ed25519")]
    [InlineData(@"C:\Keys\")]                             // a folder
    [InlineData(@"C:\Keys\%NO_SUCH_WYVERN_VARIABLE%\key")] // a variable that does not exist
    [InlineData(@"\\.\pipe\otter")]                       // a pipe
    [InlineData(@"\\?\C:\Keys\otter_ed25519")]            // the extended form
    [InlineData(@"\\otter@SSL\DavWWWRoot\key")]           // Windows would fetch it over HTTP
    [InlineData(@"\\otter\pipe\key")]
    [InlineData(@"C:\Keys\NUL")]                          // a device
    [InlineData(@"C:\Keys\ot*er")]
    [InlineData(@"C:\Keys\ot<er")]
    [InlineData(@"C:\Keys\ot>er")]
    [InlineData(@"C:\Keys\ot|er")]
    [InlineData(@"C:\Keys\ot?er")]
    [InlineData("C:\\Keys\\ot\"er")]
    [InlineData("C:\\Keys\\ot\u0001er")]                  // a control character
    [InlineData("C:\\Keys\\ot\ter")]
    // Only a drive path is taken. A path on a share gives Windows a second server to contact
    // when the key file is read, and sync.json names one server only.
    [InlineData(@"\\fileserver\share\key")]
    [InlineData(@"\\100.64.0.9\c$\key")]
    [InlineData("//fileserver/share/key")]
    [InlineData(@"\\fileserver\share")]
    [InlineData(@"\\;WebDavRedirector\fileserver\share\key")]
    [InlineData(@"\\.\C:\Keys\otter_ed25519")]            // the device form
    [InlineData(@"\\?\UNC\fileserver\share\key")]         // the extended form of a share
    [InlineData(@"\Keys\otter_ed25519")]                  // a separator and nothing else
    [InlineData("/Keys/otter_ed25519")]
    [InlineData(@".\otter_ed25519")]                      // relative
    [InlineData(@"..\Keys\otter_ed25519")]
    public void A_key_file_is_a_full_path_to_an_ordinary_file(string? keyPath)
    {
        Assert.False(SyncConfig.TryCreate(Host, "22", User, keyPath, RemoteDir, null, out var config, out string problem));
        Assert.Null(config);
        Assert.Equal(SyncConfig.KeyPathProblem, problem);
    }

    [Theory]
    [InlineData(@"C:\Users\someone\.ssh\id_ed25519")]
    [InlineData("C:/Users/someone/.ssh/id_ed25519")]
    [InlineData(@"d:\keys\my key")]
    public void A_key_file_on_a_drive_is_taken_as_it_was_written(string keyPath)
    {
        Assert.True(SyncConfig.TryCreate(Host, "22", User, keyPath, RemoteDir, null, out var config, out string problem), problem);
        Assert.Equal(keyPath, config!.PrivateKeyPath);
    }

    [Fact]
    public void A_key_file_with_a_lone_half_of_a_surrogate_pair_is_refused_and_one_with_a_whole_pair_is_taken()
    {
        foreach (char half in new[] { '\uD800', '\uDC00' })
        {
            Assert.False(SyncConfig.TryCreate(Host, "22", User, @"C:\Keys\ot" + half + "er", RemoteDir, null, out var config, out string problem));
            Assert.Null(config);
            Assert.Equal(SyncConfig.KeyPathProblem, problem);
        }

        // A pair in the wrong order is two lone halves.
        Assert.False(SyncConfig.TryCreate(Host, "22", User, @"C:\Keys\ot" + "\uDC00\uD800" + "er", RemoteDir, null, out _, out _));

        string withPair = @"C:\Keys\ot" + char.ConvertFromUtf32(0x1F511) + "er";
        Assert.True(SyncConfig.TryCreate(Host, "22", User, withPair, RemoteDir, null, out var kept, out _));
        Assert.Equal(withPair, kept!.PrivateKeyPath);
    }

    [Fact]
    public void A_variable_in_the_key_files_path_is_expanded()
    {
        string profile = Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
        Assert.NotEqual("", profile);

        Assert.True(SyncConfig.TryCreate(Host, "22", User, @"%USERPROFILE%\made-up-folder\otter_ed25519", RemoteDir, null, out var config, out _));

        Assert.Equal(Path.Combine(profile, "made-up-folder", "otter_ed25519"), config!.PrivateKeyPath);
    }

    [Theory]
    [InlineData("/home/walrus/wyvern", "/home/walrus/wyvern", "/home/walrus/wyvern/blobs")]
    [InlineData("/home/walrus/wyvern/", "/home/walrus/wyvern", "/home/walrus/wyvern/blobs")]
    [InlineData("/home/someone/wyvern", "/home/someone/wyvern", "/home/someone/wyvern/blobs")]
    [InlineData("/srv/wyvern/", "/srv/wyvern", "/srv/wyvern/blobs")]
    [InlineData("/b", "/b", "/b/blobs")]
    [InlineData("  /srv/wyvern  ", "/srv/wyvern", "/srv/wyvern/blobs")]
    [InlineData("/backups/wyvern data", "/backups/wyvern data", "/backups/wyvern data/blobs")]
    [InlineData("/home/walrus/.wyvern/...", "/home/walrus/.wyvern/...", "/home/walrus/.wyvern/.../blobs")]    // names of dots that are not "." or ".."
    [InlineData("/", "/", "/blobs")]
    public void A_server_folder_is_an_absolute_path_that_loses_the_slashes_at_its_end_and_blobs_go_inside_it(string written, string folder, string blobs)
    {
        Assert.True(SyncConfig.TryCreate(Host, "22", User, KeyPath, written, null, out var config, out string problem), problem);

        Assert.Equal(folder, config!.RemoteDir);
        Assert.Equal(blobs, config.BlobsDir);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/home/walrus/../otter")]
    [InlineData("/home/someone/../x")]
    [InlineData("../wyvern")]
    [InlineData("/..")]
    [InlineData("/.")]
    [InlineData("/home/./wyvern")]
    [InlineData("/home//wyvern")]
    [InlineData("/home//x")]
    [InlineData("//home/wyvern")]
    [InlineData("C:\\wyvern")]
    [InlineData("/home\\x")]
    [InlineData("/home/walrus/bea\ncon")]
    // Not an absolute path: the server would take each of these as relative to wherever its
    // session happens to start, or as a name with a "~" or a "$" in it.
    [InlineData("wyvern")]
    [InlineData("backups/wyvern data")]
    [InlineData("~/wyvern")]
    [InlineData("$HOME/wyvern")]
    [InlineData("./wyvern")]
    [InlineData("C:/Program Files/Git/home/someone/wyvern")]
    public void A_server_folder_that_is_not_absolute_or_has_dots_for_names_empty_names_or_backslashes_is_refused(string? remoteDir)
    {
        Assert.False(SyncConfig.TryCreate(Host, "22", User, KeyPath, remoteDir, null, out var config, out string problem));
        Assert.Null(config);
        Assert.Equal(SyncConfig.RemoteDirProblem, problem);
    }

    [Fact]
    public void The_sentence_for_a_server_folder_asks_for_the_full_path_on_the_server_starting_with_a_slash()
    {
        Assert.Contains("full path", SyncConfig.RemoteDirProblem);
        Assert.Contains("starting with /", SyncConfig.RemoteDirProblem);
    }

    [Theory]
    [InlineData(0x0001)]    // a control character
    [InlineData(0x200B)]    // a zero-width space
    [InlineData(0x202E)]    // right-to-left override
    [InlineData(0xFEFF)]    // a byte order mark
    [InlineData(0x2028)]    // a line separator
    [InlineData(0x2029)]    // a paragraph separator
    [InlineData(0xD800)]    // a lone half of a surrogate pair
    [InlineData(0xDC00)]
    public void A_server_folder_holds_no_control_or_invisible_character_and_no_lone_surrogate(int code)
    {
        char c = (char)code;
        var folders = new List<string> { "/home/wal" + c + "rus/wyvern" };
        if (!char.IsWhiteSpace(c))
        {
            folders.Add("/home/walrus/wyvern" + c);
        }

        foreach (string folder in folders)
        {
            Assert.False(SyncConfig.TryCreate(Host, "22", User, KeyPath, folder, null, out var config, out string problem));
            Assert.Null(config);
            Assert.Equal(SyncConfig.RemoteDirProblem, problem);
        }
    }

    [Fact]
    public void A_fingerprint_of_the_wrong_shape_is_refused_and_never_taken_for_none()
    {
        Assert.False(SyncConfig.TryCreate(Host, "22", User, KeyPath, RemoteDir, "SHA256:tooshort", out var config, out string problem));
        Assert.Null(config);
        Assert.Equal(SyncConfig.FingerprintProblem, problem);
    }

    [Fact]
    public void A_fingerprint_that_no_key_can_have_is_refused()
    {
        Assert.False(SyncConfig.TryCreate(Host, "22", User, KeyPath, RemoteDir, "SHA256:AjAMBCdFlnzhT4An+L756BKl4RXyRER/GQvIadZqNKB", out var config, out string problem));
        Assert.Null(config);
        Assert.Equal(SyncConfig.FingerprintProblem, problem);
    }

    [Fact]
    public void The_length_of_a_host_a_user_and_a_server_folder_may_reach_its_limit_and_not_pass_it()
    {
        // The host: 253 characters.
        Assert.True(SyncConfig.TryCreate(new string('a', 253), "22", User, KeyPath, RemoteDir, null, out _, out _));
        Assert.False(SyncConfig.TryCreate(new string('a', 254), "22", User, KeyPath, RemoteDir, null, out _, out string hostProblem));
        Assert.Equal(SyncConfig.HostProblem, hostProblem);

        // The user: 64.
        Assert.True(SyncConfig.TryCreate(Host, "22", new string('u', 64), KeyPath, RemoteDir, null, out _, out _));
        Assert.False(SyncConfig.TryCreate(Host, "22", new string('u', 65), KeyPath, RemoteDir, null, out _, out string userProblem));
        Assert.Equal(SyncConfig.UserProblem, userProblem);

        // The server folder: 1024.
        Assert.True(SyncConfig.TryCreate(Host, "22", User, KeyPath, "/" + new string('d', 1023), null, out _, out _));
        Assert.False(SyncConfig.TryCreate(Host, "22", User, KeyPath, "/" + new string('d', 1024), null, out _, out string folderProblem));
        Assert.Equal(SyncConfig.RemoteDirProblem, folderProblem);
    }

    [Fact]
    public void Pinned_makes_the_same_settings_with_a_fingerprint_that_is_saved_and_loaded_as_ready()
    {
        var pinned = Config(fingerprint: null).Pinned(Fingerprint);

        Assert.Equal(Fingerprint, pinned.HostKeyFingerprint);
        Assert.Equal("SyncConfig { Pinned = True }", pinned.ToString());
        pinned.Save(SyncFile);
        var loaded = SyncConfig.Load(SyncFile);
        Assert.Equal(SyncConfigState.Ready, loaded.State);
        Assert.Equal(pinned, loaded.Config);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a fingerprint")]
    [InlineData("SHA256:tooshort")]
    [InlineData("SHA256:AjAMBCdFlnzhT4An+L756BKl4RXyRER/GQvIadZqNKB")]   // no key gives it
    [InlineData(" SHA256:AjAMBCdFlnzhT4An+L756BKl4RXyRER/GQvIadZqNKs")]  // not trimmed: Pinned is not for typed text
    public void Pinned_refuses_text_that_is_not_a_fingerprint_so_that_a_pinned_configuration_always_is_one(string text)
    {
        var unpinned = Config(fingerprint: null);

        var thrown = Assert.Throws<ArgumentException>(() => unpinned.Pinned(text));

        Assert.StartsWith(SyncConfig.FingerprintProblem, thrown.Message);
        Assert.Null(unpinned.HostKeyFingerprint);
    }

    [Fact]
    public void Pinned_refuses_null()
    {
        Assert.Throws<ArgumentException>(() => Config(fingerprint: null).Pinned(null!));
    }

    [Fact]
    public void No_file_means_sync_was_never_set_up()
    {
        var loaded = SyncConfig.Load(SyncFile);

        Assert.Equal(SyncConfigState.Missing, loaded.State);
        Assert.Null(loaded.Config);
        Assert.Null(loaded.Problem);
    }

    [Fact]
    public void What_was_saved_is_what_is_loaded()
    {
        Config().Save(SyncFile);

        var loaded = SyncConfig.Load(SyncFile);

        Assert.Equal(SyncConfigState.Ready, loaded.State);
        Assert.Equal(Config(), loaded.Config);
        Assert.False(File.Exists(SyncFile + ".tmp"));
    }

    [Fact]
    public void The_saved_file_is_one_a_person_can_read_and_edit()
    {
        Config().Save(SyncFile);

        Assert.Equal(
            """
            {
              "host": "otter.example.org",
              "port": 22,
              "user": "walrus",
              "privateKeyPath": "C:\\Keys\\otter_ed25519",
              "remoteDir": "/home/walrus/wyvern",
              "hostKeyFingerprint": "SHA256:AjAMBCdFlnzhT4An+L756BKl4RXyRER/GQvIadZqNKs"
            }
            """.ReplaceLineEndings("\n"),
            File.ReadAllText(SyncFile).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Settings_without_a_fingerprint_are_saved_without_one_and_load_as_unpinned()
    {
        Config(fingerprint: null).Save(SyncFile);

        var loaded = SyncConfig.Load(SyncFile);

        Assert.DoesNotContain("hostKeyFingerprint", File.ReadAllText(SyncFile));
        Assert.Equal(SyncConfigState.Unpinned, loaded.State);
        Assert.Null(loaded.Config!.HostKeyFingerprint);
    }

    [Fact]
    public void A_hand_written_file_may_have_comments_a_trailing_comma_other_case_and_no_port()
    {
        var loaded = LoadText(
            """
            {
              // my own server
              "Host": "otter.example.org",
              "USER": "walrus",
              "privateKeyPath": "C:\\Keys\\otter_ed25519",
              "remoteDir": "/home/walrus/wyvern/",
              "hostKeyFingerprint": "SHA256:AjAMBCdFlnzhT4An+L756BKl4RXyRER/GQvIadZqNKs",
              "note": "keys of a later version are ignored",
            }
            """);

        Assert.Equal(SyncConfigState.Ready, loaded.State);
        Assert.Equal(Config(), loaded.Config);
    }

    [Fact]
    public void A_file_that_starts_with_a_byte_order_mark_is_read()
    {
        File.WriteAllBytes(SyncFile, [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes(Json())]);

        Assert.Equal(SyncConfigState.Ready, SyncConfig.Load(SyncFile).State);
    }

    [Fact]
    public void A_fingerprint_written_as_null_or_as_blank_text_is_unpinned()
    {
        Assert.Equal(SyncConfigState.Unpinned, LoadText(Json(fingerprint: null).Replace("\n}", ",\n  \"hostKeyFingerprint\": null\n}")).State);
        Assert.Equal(SyncConfigState.Unpinned, LoadText(Json(fingerprint: "  ")).State);
    }

    [Fact]
    public void A_file_that_is_not_JSON_is_unreadable_and_says_which_line()
    {
        var loaded = LoadText("{\n  \"host\": \"otter.example.org\",\n  \"port\": twenty-two\n}");

        AssertUnreadable(loaded, "It is not valid JSON (line 3).");
        Assert.NotNull(loaded.Cause);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"otter.example.org\"")]
    [InlineData("null")]
    public void A_file_that_is_not_one_object_is_unreadable(string json)
    {
        AssertUnreadable(LoadText(json), "It must be one JSON object.");
    }

    [Fact]
    public void An_empty_file_is_unreadable_and_not_missing()
    {
        Assert.Equal(SyncConfigState.Unreadable, LoadText("").State);
    }

    [Fact]
    public void A_key_that_is_written_twice_makes_the_file_unreadable()
    {
        var loaded = LoadText(Json().Replace("\"user\": \"walrus\"", "\"user\": \"walrus\", \"HOST\": \"seal.example.org\""));

        AssertUnreadable(loaded, "A key is written twice.");
    }

    [Theory]
    [InlineData("\"22\"")]
    [InlineData("22.5")]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void A_port_that_is_not_a_whole_number_in_range_makes_the_file_unreadable(string port)
    {
        AssertUnreadable(LoadText(Json(port: port)), SyncConfig.PortProblem);
    }

    [Fact]
    public void A_value_of_the_wrong_kind_makes_the_file_unreadable()
    {
        var loaded = LoadText(Json().Replace("\"user\": \"walrus\"", "\"user\": 7"));

        AssertUnreadable(loaded, "\"host\", \"user\", \"privateKeyPath\", \"remoteDir\" and \"hostKeyFingerprint\" must each be text.");
    }

    // The file as Json() writes it, with one key's value written as the given JSON instead.
    private static string JsonWithValue(string key, string literal)
    {
        var values = new Dictionary<string, string>
        {
            ["host"] = $"\"{Host}\"",
            ["port"] = "22",
            ["user"] = $"\"{User}\"",
            ["privateKeyPath"] = "\"C:\\\\Keys\\\\otter_ed25519\"",
            ["remoteDir"] = $"\"{RemoteDir}\"",
            ["hostKeyFingerprint"] = $"\"{Fingerprint}\"",
        };
        values[key] = literal;
        return "{ " + string.Join(", ", values.Select(pair => $"\"{pair.Key}\": {pair.Value}")) + " }";
    }

    [Fact]
    public void The_file_that_the_wrong_kind_tests_start_from_is_ready()
    {
        Assert.Equal(Config(), LoadText(JsonWithValue("port", "22")).Config);
    }

    [Theory]
    [InlineData("host", "7")]
    [InlineData("host", "true")]
    [InlineData("host", "[]")]
    [InlineData("host", "{}")]
    [InlineData("user", "7")]
    [InlineData("user", "false")]
    [InlineData("user", "[\"walrus\"]")]
    [InlineData("privateKeyPath", "7")]
    [InlineData("privateKeyPath", "true")]
    [InlineData("privateKeyPath", "[]")]
    [InlineData("privateKeyPath", "{}")]
    [InlineData("remoteDir", "7")]
    [InlineData("remoteDir", "false")]
    [InlineData("remoteDir", "[]")]
    [InlineData("remoteDir", "{}")]
    [InlineData("hostKeyFingerprint", "7")]
    [InlineData("hostKeyFingerprint", "false")]
    [InlineData("hostKeyFingerprint", "[]")]
    [InlineData("hostKeyFingerprint", "{}")]
    public void A_text_key_that_holds_another_kind_of_value_makes_the_file_unreadable(string key, string literal)
    {
        var loaded = LoadText(JsonWithValue(key, literal));

        AssertUnreadable(loaded, "\"host\", \"user\", \"privateKeyPath\", \"remoteDir\" and \"hostKeyFingerprint\" must each be text.");
    }

    [Theory]
    [InlineData("\"22\"")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("\"\"")]
    public void A_port_of_another_kind_than_a_number_makes_the_file_unreadable(string literal)
    {
        AssertUnreadable(LoadText(JsonWithValue("port", literal)), SyncConfig.PortProblem);
    }

    [Fact]
    public void A_file_with_an_invalid_UTF8_byte_in_a_value_is_unreadable_and_what_is_kept_as_the_cause_holds_none_of_the_file()
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(Json(user: "wal#rus"));
        bytes[Array.IndexOf(bytes, (byte)'#')] = 0xFF;
        File.WriteAllBytes(SyncFile, bytes);

        var loaded = SyncConfig.Load(SyncFile);

        AssertUnreadable(loaded, "It holds text that cannot be read.");
        Assert.True(loaded.Cause is InvalidOperationException or ArgumentException, loaded.Cause?.GetType().Name);
        Assert.Null(loaded.Cause!.InnerException);
        Assert.Equal("The file holds text that cannot be read.", loaded.Cause.Message);
    }

    [Fact]
    public void A_file_with_a_lone_surrogate_written_as_an_escape_is_unreadable_and_what_is_kept_as_the_cause_holds_none_of_the_file()
    {
        var loaded = LoadText(Json(user: "wal\\ud800rus"));

        AssertUnreadable(loaded, "It holds text that cannot be read.");
        Assert.True(loaded.Cause is InvalidOperationException or ArgumentException, loaded.Cause?.GetType().Name);
        Assert.Null(loaded.Cause!.InnerException);
        Assert.Equal("The file holds text that cannot be read.", loaded.Cause.Message);
    }

    [Fact]
    public void What_is_kept_as_the_cause_of_a_file_that_is_not_JSON_holds_none_of_the_file()
    {
        // The parser's own message names the character it stopped at and the path of the value.
        var loaded = LoadText("{ \"user\": \"secret-walrus\" oops }");

        AssertUnreadable(loaded, "It is not valid JSON (line 1).");
        var cause = Assert.IsType<System.Text.Json.JsonException>(loaded.Cause);
        Assert.Null(cause.InnerException);
        Assert.Equal("The file is not valid JSON.", cause.Message);
    }

    [Fact]
    public void A_missing_value_makes_the_file_unreadable_with_the_sentence_for_that_value()
    {
        AssertUnreadable(LoadText("{ }"), SyncConfig.HostProblem);
        AssertUnreadable(LoadText("""{ "host": "otter.example.org" }"""), SyncConfig.UserProblem);
    }

    [Fact]
    public void A_fingerprint_that_was_damaged_makes_the_file_unreadable_and_never_unpinned()
    {
        var loaded = LoadText(Json(fingerprint: "SHA256:AjAMBCdFlnzhT4An"));

        AssertUnreadable(loaded, SyncConfig.FingerprintProblem);
    }

    [Fact]
    public void A_file_far_too_large_is_unreadable()
    {
        File.WriteAllText(SyncFile, Json() + new string(' ', 64 * 1024));

        AssertUnreadable(SyncConfig.Load(SyncFile), "It is far too large to be that file.");
    }

    [Fact]
    public void A_file_of_exactly_64_KB_is_read_and_one_byte_more_is_too_large()
    {
        string json = Json();
        File.WriteAllText(SyncFile, json + new string(' ', 64 * 1024 - json.Length));
        Assert.Equal(64 * 1024, new FileInfo(SyncFile).Length);
        Assert.Equal(SyncConfigState.Ready, SyncConfig.Load(SyncFile).State);

        File.WriteAllText(SyncFile, json + new string(' ', 64 * 1024 - json.Length + 1));
        AssertUnreadable(SyncConfig.Load(SyncFile), "It is far too large to be that file.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_path_that_is_empty_or_blank_is_unreadable_and_not_an_exception(string? path)
    {
        var loaded = SyncConfig.Load(path!);

        AssertUnreadable(loaded, "There is no file name to read it from.");
    }

    [Fact]
    public void A_save_that_fails_leaves_no_temporary_file_and_the_earlier_file_stays()
    {
        Config(fingerprint: null).Save(SyncFile);

        using (new FileStream(SyncFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var thrown = Record.Exception(() => Config().Save(SyncFile));

            Assert.True(thrown is IOException or UnauthorizedAccessException, thrown?.GetType().Name);
            Assert.False(File.Exists(SyncFile + ".tmp"));
        }

        Assert.Equal(SyncConfigState.Unpinned, SyncConfig.Load(SyncFile).State);
    }

    [Fact]
    public void Settings_with_a_lone_half_of_a_surrogate_pair_are_not_saved_and_never_saved_as_another_character()
    {
        Config().Save(SyncFile);
        string before = File.ReadAllText(SyncFile);

        foreach (var broken in new[]
        {
            Config() with { Host = "otter" + '\uD800' + ".example.org" },
            Config() with { User = "wal" + '\uDC00' + "rus" },
            Config() with { PrivateKeyPath = @"C:\Keys\ot" + '\uD800' + "er" },
            Config() with { RemoteDir = "/home/wal" + '\uD800' + "rus/wyvern" },
            Config() with { HostKeyFingerprint = Fingerprint[..^1] + '\uD800' },
        })
        {
            var thrown = Assert.Throws<ArgumentException>(() => broken.Save(SyncFile));

            Assert.DoesNotContain("D800", thrown.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("walrus", thrown.Message);
            Assert.Equal(before, File.ReadAllText(SyncFile));
            Assert.False(File.Exists(SyncFile + ".tmp"));
        }
    }

    [Fact]
    public void Settings_with_a_whole_surrogate_pair_in_the_key_path_are_saved_and_loaded_as_they_were()
    {
        Assert.True(SyncConfig.TryCreate(Host, "22", User, @"C:\Keys\ot" + char.ConvertFromUtf32(0x1F511) + "er", RemoteDir, Fingerprint, out var config, out string problem), problem);
        config!.Save(SyncFile);

        var loaded = SyncConfig.Load(SyncFile);

        Assert.Equal(SyncConfigState.Ready, loaded.State);
        Assert.Equal(config, loaded.Config);
    }

    [Fact]
    public void A_file_that_another_program_holds_is_unreadable_and_not_missing()
    {
        Config().Save(SyncFile);
        using var held = new FileStream(SyncFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var loaded = SyncConfig.Load(SyncFile);

        AssertUnreadable(loaded, "The file could not be read.");
        Assert.IsAssignableFrom<IOException>(loaded.Cause);
    }

    [Fact]
    public void No_problem_sentence_quotes_what_the_file_holds()
    {
        var loaded = LoadText(Json(host: "secret-otter-host with a space", user: "secret-walrus"));

        Assert.Equal(SyncConfigState.Unreadable, loaded.State);
        Assert.DoesNotContain("secret", loaded.Problem);
        Assert.DoesNotContain("secret", loaded.ToString());
    }

    [Fact]
    public void The_settings_print_nothing_of_the_server_the_user_or_the_key_file()
    {
        Assert.Equal("SyncConfig { Pinned = True }", Config().ToString());
        Assert.Equal("SyncConfig { Pinned = False }", Config(fingerprint: null).ToString());
        Assert.Equal("SyncConfigLoad { State = Missing }", SyncConfig.Load(SyncFile).ToString());
    }

    [Fact]
    public void The_remote_id_is_the_same_for_the_same_server_folder_user_and_key_and_differs_otherwise()
    {
        string id = Config().RemoteId("0123456789abcdef");

        Assert.Equal(64, id.Length);
        Assert.Equal(id, Config().RemoteId("0123456789abcdef"));
        Assert.Equal(id, (Config() with { Host = "OTTER.example.org" }).RemoteId("0123456789abcdef"));
        Assert.Equal(id, Config(fingerprint: null).RemoteId("0123456789abcdef"));
        Assert.NotEqual(id, Config().RemoteId("fedcba9876543210"));
        Assert.NotEqual(id, (Config() with { RemoteDir = "/home/walrus/other" }).RemoteId("0123456789abcdef"));
        Assert.NotEqual(id, (Config() with { Port = 2222 }).RemoteId("0123456789abcdef"));
        Assert.DoesNotContain("otter", id);
    }

    [Fact]
    public void Pinning_keeps_everything_else()
    {
        var pinned = Config(fingerprint: null).Pinned(Fingerprint);

        Assert.Equal(Config(), pinned);
    }
}
