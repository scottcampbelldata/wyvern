using Wyvern.Core.ClipboardHistory;

namespace Wyvern.Tests;

public sealed class ClipboardStateTests : IDisposable
{
    private static readonly ClipboardState Running = new(Paused: false, Unreadable: false);
    private static readonly ClipboardState PausedByOwner = new(Paused: true, Unreadable: false);
    private static readonly ClipboardState PausedBecauseUnreadable = new(Paused: true, Unreadable: true);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string StateFile => _dir.File("clipboard-state.json");

    private ClipboardState Load(string content)
    {
        File.WriteAllText(StateFile, content);
        return ClipboardState.Load(StateFile);
    }

    [Fact]
    public void No_file_means_capture_is_on()
    {
        Assert.Equal(Running, ClipboardState.Load(StateFile));
    }

    [Theory]
    [InlineData("""{ "capturePaused": true }""")]
    [InlineData("""{"capturePaused":true}""")]
    [InlineData("""{ "other": 1, "capturePaused": true, }""")]
    [InlineData("""
        // written by hand
        { "capturePaused": true }
        """)]
    public void A_file_that_says_paused_means_paused(string content)
    {
        Assert.Equal(PausedByOwner, Load(content));
    }

    [Fact]
    public void A_file_written_with_a_byte_order_mark_as_Windows_PowerShell_writes_it_is_read_all_the_same()
    {
        byte[] mark = [0xEF, 0xBB, 0xBF];
        File.WriteAllBytes(StateFile, [.. mark, .. "{ \"capturePaused\": true }\r\n"u8]);

        Assert.Equal(PausedByOwner, ClipboardState.Load(StateFile));
    }

    [Theory]
    [InlineData("""{ "capturePaused": false }""")]
    [InlineData("""{ "other": 1, "capturePaused": false }""")]
    public void A_file_that_says_not_paused_means_capture_is_on(string content)
    {
        Assert.Equal(Running, Load(content));
    }

    [Theory]
    [InlineData("")]
    [InlineData("paused")]
    [InlineData("""{ "capturePaused": tru""")]
    [InlineData("""{ "capturePaused": "true" }""")]
    [InlineData("""{ "capturePaused": 1 }""")]
    [InlineData("""{ "capturePaused": null }""")]
    [InlineData("true")]
    [InlineData("[true]")]
    public void A_file_that_cannot_be_understood_means_paused_and_says_so(string content)
    {
        Assert.Equal(PausedBecauseUnreadable, Load(content));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "somethingElse": true }""")]
    [InlineData("""{ "CapturePaused": true }""")]
    [InlineData("""{ "capturepaused": true }""")]
    [InlineData("""{ "capture_paused": true }""")]
    [InlineData("""{ "capturePaused": false, "capturePaused": true }""")]
    [InlineData("""{ "capturePaused": true, "capturePaused": true }""")]
    public void A_file_that_does_not_have_the_exact_key_once_cannot_be_understood_and_means_paused_and_says_so(string content)
    {
        Assert.Equal(PausedBecauseUnreadable, Load(content));
    }

    [Fact]
    public void A_state_file_that_is_a_folder_cannot_be_understood_and_is_not_taken_for_no_file()
    {
        Directory.CreateDirectory(StateFile);

        Assert.Equal(PausedBecauseUnreadable, ClipboardState.Load(StateFile));
    }

    [Fact]
    public void A_folder_that_is_not_there_means_no_file_and_capture_is_on()
    {
        string file = Path.Combine(_dir.Path, "not-there-yet", "clipboard-state.json");

        Assert.Equal(Running, ClipboardState.Load(file));
    }

    [Fact]
    public void A_path_that_cannot_be_opened_at_all_cannot_be_understood()
    {
        string file = Path.Combine(_dir.Path, "bad\0name.json");

        Assert.Equal(PausedBecauseUnreadable, ClipboardState.Load(file));
    }

    [Fact]
    public void A_temporary_file_left_beside_the_state_file_means_a_save_did_not_finish_and_capture_is_paused_and_it_says_so()
    {
        File.WriteAllText(StateFile, "{ \"capturePaused\": false }");
        File.WriteAllText(StateFile + ".tmp", "{ \"capturePaused\": true }");

        Assert.Equal(PausedBecauseUnreadable, ClipboardState.Load(StateFile));
    }

    [Fact]
    public void A_temporary_file_left_where_there_is_no_state_file_means_the_same()
    {
        File.WriteAllText(StateFile + ".tmp", "{ \"capturePaused\": true }");

        Assert.Equal(PausedBecauseUnreadable, ClipboardState.Load(StateFile));
    }

    [Fact]
    public void A_later_save_that_succeeds_removes_the_temporary_file_that_was_left()
    {
        File.WriteAllText(StateFile + ".tmp", "left behind");

        ClipboardState.Save(StateFile, paused: false);

        Assert.Equal(Running, ClipboardState.Load(StateFile));
        Assert.Equal(["clipboard-state.json"], Directory.GetFiles(_dir.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void A_file_far_larger_than_this_file_ever_is_cannot_be_understood()
    {
        Assert.Equal(PausedBecauseUnreadable, Load("{ \"capturePaused\": false }" + new string(' ', 70_000)));
    }

    [Fact]
    public void A_file_that_cannot_be_opened_means_paused_and_says_so()
    {
        File.WriteAllText(StateFile, "{ \"capturePaused\": false }");
        using var held = new FileStream(StateFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(PausedBecauseUnreadable, ClipboardState.Load(StateFile));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void What_is_saved_is_what_is_loaded(bool paused)
    {
        ClipboardState.Save(StateFile, paused);

        Assert.Equal(new ClipboardState(paused, Unreadable: false), ClipboardState.Load(StateFile));
    }

    [Fact]
    public void Saving_replaces_what_was_there_and_leaves_no_temporary_file()
    {
        ClipboardState.Save(StateFile, paused: true);
        ClipboardState.Save(StateFile, paused: false);

        Assert.Equal(Running, ClipboardState.Load(StateFile));
        Assert.Equal(["clipboard-state.json"], Directory.GetFiles(_dir.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void Saving_makes_the_folder_when_it_is_missing()
    {
        string file = Path.Combine(_dir.Path, "not-there-yet", "clipboard-state.json");

        ClipboardState.Save(file, paused: true);

        Assert.Equal(PausedByOwner, ClipboardState.Load(file));
    }

    /// <summary>A source file of the repository, read from disk as the privacy rule's test reads the app's.</summary>
    private static string SourceFile(params string[] pathFromTheRepositoryRoot)
    {
        var folder = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "Wyvern.slnx")))
        {
            folder = folder.Parent;
        }

        Assert.NotNull(folder);
        string source = File.ReadAllText(Path.Combine([folder.FullName, .. pathFromTheRepositoryRoot]));
        Assert.NotEmpty(source);
        return source;
    }

    [Theory]
    [InlineData("ClipboardState.cs")]
    [InlineData("ClipCapture.cs")]
    [InlineData("ClipboardBytes.cs")]
    public void No_invisible_or_other_character_outside_ASCII_is_written_in_the_source_of_the_gate(string file)
    {
        // A byte order mark inside a string literal cannot be seen in an editor or a review. The
        // source is read from disk.
        string source = SourceFile("src", "Wyvern.Core", "ClipboardHistory", file);

        var offences = source
            .Select((character, index) => (character, index))
            .Where(item => item.character > '~' || (item.character < ' ' && item.character is not ('\r' or '\n' or '\t')))
            .Select(item => $"U+{(int)item.character:X4} at {item.index}")
            .ToList();
        Assert.Empty(offences);
    }

    [Theory]
    [InlineData("src/Wyvern.Core/ClipboardHistory/ClipboardBytes.cs")]
    [InlineData("tests/Wyvern.Tests/ClipboardBytesTests.cs")]
    public void The_superscript_digits_of_the_device_names_are_escapes_and_not_literal_characters(string path)
    {
        // The test file holds other letters outside ASCII on purpose (names with accents), so it
        // is not held to ASCII as a whole. These three look like plain digits in an editor, and
        // they are the ones that came across as characters once: they are written as escapes.
        string source = SourceFile(path.Split('/'));

        Assert.DoesNotContain((char)0xB9, source);
        Assert.DoesNotContain((char)0xB2, source);
        Assert.DoesNotContain((char)0xB3, source);
    }

    [Fact]
    public void A_byte_order_mark_that_is_the_whole_of_the_content_cannot_be_understood()
    {
        File.WriteAllBytes(StateFile, [0xEF, 0xBB, 0xBF]);

        Assert.Equal(PausedBecauseUnreadable, ClipboardState.Load(StateFile));
    }

    [Fact]
    public void A_byte_order_mark_in_front_of_a_file_that_says_not_paused_is_read_all_the_same()
    {
        File.WriteAllBytes(StateFile, [0xEF, 0xBB, 0xBF, .. "{ \"capturePaused\": false }"u8]);

        Assert.Equal(Running, ClipboardState.Load(StateFile));
    }

    [Fact]
    public void The_file_is_one_a_person_can_read_and_write_by_hand()
    {
        ClipboardState.Save(StateFile, paused: true);

        Assert.Equal("{ \"capturePaused\": true }\r\n", File.ReadAllText(StateFile));
    }
}
