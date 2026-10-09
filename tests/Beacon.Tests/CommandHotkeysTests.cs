using System.Text.Json;
using Beacon.Core.Commands;
using Beacon.Core.Settings;

namespace Beacon.Tests;

/// <summary>The hotkeys that run a command from anywhere: the keys they can use, and how the "commandHotkeys" setting is read.</summary>
public sealed class CommandHotkeysTests : IDisposable
{
    private static readonly Hotkey Launcher = new(HotkeyModifiers.Alt, 0x20);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // Three commands: two a hotkey can run, and one that asks first.
    private static bool? Known(string id) => id.ToLowerInvariant() switch
    {
        "window-left-half" or "window-right-half" or "lock-screen" => true,
        "restart" or "clipboard-history" => false,
        _ => null,
    };

    [Theory]
    [InlineData("Ctrl+Alt+Left", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x25u)]
    [InlineData("Ctrl+Alt+Up", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x26u)]
    [InlineData("Ctrl+Alt+Right", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x27u)]
    [InlineData("ctrl+alt+down", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x28u)]
    [InlineData("Ctrl+Alt+Enter", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x0Du)]
    [InlineData("Win+Shift+Return", HotkeyModifiers.Win | HotkeyModifiers.Shift, 0x0Du)]
    [InlineData("Ctrl+Alt+Home", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x24u)]
    [InlineData("Ctrl+Alt+End", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x23u)]
    [InlineData("Ctrl+Alt+PageUp", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x21u)]
    [InlineData("Ctrl+Alt+PageDown", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x22u)]
    public void The_keys_that_have_names_can_be_hotkeys(string text, HotkeyModifiers modifiers, uint key)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(new Hotkey(modifiers, key), hotkey);
    }

    [Theory]
    [InlineData("Left")]
    [InlineData("Enter")]
    [InlineData("PageDown")]
    [InlineData("Ctrl+Alt+Middle")]
    [InlineData("Ctrl+Alt+Page Up")]
    [InlineData("Ctrl+Alt+Delete")]
    [InlineData("Ctrl+Alt+Tab")]
    public void A_named_key_needs_a_modifier_and_other_names_are_not_keys(string text) =>
        Assert.False(Hotkey.TryParse(text, out _));

    [Fact]
    public void A_setting_that_is_not_there_gives_no_hotkeys_and_nothing_to_say()
    {
        Assert.Same(CommandHotkeyOptions.None, CommandHotkeyOptions.From(null, Known, []));
        Assert.Same(CommandHotkeyOptions.None, CommandHotkeyOptions.From(Json("null"), Known, []));
        Assert.Empty(CommandHotkeyOptions.None.Hotkeys);
        Assert.Empty(CommandHotkeyOptions.None.Notes);
    }

    [Theory]
    [InlineData("[\"window-left-half\"]")]
    [InlineData("\"Ctrl+Alt+Left\"")]
    [InlineData("7")]
    [InlineData("true")]
    public void A_setting_that_is_not_a_list_of_names_gives_none_and_says_so(string json)
    {
        var options = CommandHotkeyOptions.From(Json(json), Known, []);

        Assert.Empty(options.Hotkeys);
        Assert.Equal([CommandHotkeyOptions.NotAListNote], options.Notes);
    }

    [Fact]
    public void Each_command_gets_its_hotkey_in_the_order_of_the_file()
    {
        var options = CommandHotkeyOptions.From(
            Json("""{ "window-right-half": " Ctrl+Alt+Right ", "Window-Left-Half": "ctrl+alt+left", "lock-screen": "F9" }"""),
            Known,
            [Launcher]);

        Assert.Empty(options.Notes);
        Assert.Equal(
            [
                new CommandHotkey("window-right-half", new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x27), "Ctrl+Alt+Right"),
                new CommandHotkey("Window-Left-Half", new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x25), "ctrl+alt+left"),
                new CommandHotkey("lock-screen", new Hotkey(HotkeyModifiers.None, 0x78), "F9"),
            ],
            options.Hotkeys);
    }

    [Fact]
    public void What_cannot_be_used_is_left_out_and_counted_by_its_reason_and_the_rest_is_used()
    {
        var options = CommandHotkeyOptions.From(
            Json("""
                {
                  "window-left-half": "Ctrl+Alt+Left",
                  "no-such-command": "Ctrl+Alt+N",
                  "another-that-is-not": "Ctrl+Alt+M",
                  "restart": "Ctrl+Alt+R",
                  "clipboard-history": "Ctrl+Shift+V",
                  "lock-screen": "Ctrl+Alt+Banana",
                  "window-right-half": 7
                }
                """),
            Known,
            [Launcher]);

        Assert.Equal(["window-left-half"], options.Hotkeys.Select(one => one.CommandId));
        Assert.Equal(
            [
                "Command hotkeys left out because Beacon has no command of that name: 2.",
                "Command hotkeys left out because the command asks for something first, or is the clipboard history, which has \"clipboardHotkey\": 2.",
                "Command hotkeys left out because they are not hotkeys Beacon understands: 2.",
            ],
            options.Notes);
    }

    [Fact]
    public void A_key_is_used_once_and_a_command_has_one_hotkey()
    {
        var options = CommandHotkeyOptions.From(
            Json("""
                {
                  "window-left-half": "Ctrl+Alt+Left",
                  "window-right-half": "Control+Alt+LEFT",
                  "lock-screen": "Alt+Space",
                  "WINDOW-LEFT-HALF": "Ctrl+Alt+Up"
                }
                """),
            Known,
            [Launcher]);

        Assert.Equal(["window-left-half"], options.Hotkeys.Select(one => one.CommandId));
        Assert.Equal(
            ["Command hotkeys left out because another hotkey of Beacon already has the key, or the command already has one: 3."],
            options.Notes);
    }

    [Fact]
    public void A_command_whose_hotkey_is_empty_text_has_none_and_nothing_is_said()
    {
        var options = CommandHotkeyOptions.From(Json("""{ "window-left-half": "", "no-such-command": "  " }"""), Known, []);

        Assert.Empty(options.Hotkeys);
        Assert.Empty(options.Notes);
    }

    [Fact]
    public void The_notes_never_quote_the_file()
    {
        var options = CommandHotkeyOptions.From(
            Json("""{ "harbour-seal": "Ctrl+Alt+Herring", "lock-screen": "grey heron" }"""),
            Known,
            []);

        Assert.All(options.Notes, note =>
        {
            Assert.DoesNotContain("seal", note, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("herring", note, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("heron", note, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(2, options.Notes.Count);
    }

    [Fact]
    public void A_hotkey_can_run_a_command_that_does_its_thing_at_once_and_no_other()
    {
        var atOnce = new BuiltInCommand("lock-screen", "Lock Screen", "", () => { });
        var asksFirst = new BuiltInCommand("restart", "Restart", "", Run: null, Argument: new Beacon.Core.Search.ItemArgument("Press Enter", _ => { }));
        var inTheLauncher = new BuiltInCommand("search-emoji", "Search Emoji & Symbols", "", () => { }, KeepOpen: true);
        var history = new BuiltInCommand("clipboard-history", "Clipboard History", "", () => { }, KeepOpen: true);

        Assert.True(CommandHotkeyOptions.CanRun(atOnce));
        Assert.False(CommandHotkeyOptions.CanRun(asksFirst));

        // One that opens its view in the launcher can: the hotkey shows the launcher. The
        // clipboard history has a setting of its own.
        Assert.True(CommandHotkeyOptions.CanRun(inTheLauncher));
        Assert.False(CommandHotkeyOptions.CanRun(history));
        Assert.Equal(
            [new HotkeyChoice("lock-screen", "Lock Screen"), new HotkeyChoice("search-emoji", "Search Emoji & Symbols")],
            CommandHotkeyOptions.Choices([asksFirst, atOnce, inTheLauncher, history]));
    }

    [Fact]
    public void The_window_and_system_commands_that_do_not_ask_first_can_all_have_hotkeys()
    {
        var choices = CommandHotkeyOptions.Choices(
        [
            .. WindowCommands.Create(new NoMover()),
            .. PcCommands.Create(new NoPc()),
        ]);

        Assert.Contains(choices, choice => choice.Id == "window-left-half");
        Assert.Contains(choices, choice => choice.Id == "window-next-display");
        Assert.Contains(choices, choice => choice.Id == "lock-screen");
        Assert.Contains(choices, choice => choice.Id == "toggle-mute");
        Assert.DoesNotContain(choices, choice => choice.Id is "restart" or "shut-down" or "sign-out" or "empty-recycle-bin" or "set-volume");
    }

    [Fact]
    public void The_setting_is_read_from_settings_json_as_it_was_written()
    {
        string path = _dir.File("settings.json");
        File.WriteAllText(path, """{ "CommandHotkeys": { "window-left-half": "Ctrl+Alt+Left" } }""");

        var loaded = SettingsStore.Load(path);

        Assert.Null(loaded.Error);
        var options = CommandHotkeyOptions.From(loaded.Settings.CommandHotkeys, Known, []);
        Assert.Equal("window-left-half", Assert.Single(options.Hotkeys).CommandId);
    }

    [Fact]
    public void The_file_beacon_first_writes_has_no_command_hotkeys()
    {
        string path = _dir.File("settings.json");

        var loaded = SettingsStore.Load(path);

        Assert.Null(loaded.Settings.CommandHotkeys);
        Assert.DoesNotContain("commandHotkeys", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_settings_window_s_document_reads_and_writes_the_list_of_names()
    {
        string path = _dir.File("settings.json");
        File.WriteAllText(path, """{ "hotkey": "Alt+Space", "COMMANDHOTKEYS": { "window-left-half": "Ctrl+Alt+Left", "lock-screen": 7 } }""");
        var document = SettingsDocument.Read(path)!;

        var map = document.Map("commandHotkeys")!;
        Assert.Equal("Ctrl+Alt+Left", Assert.Single(map).Value);
        Assert.Equal("Ctrl+Alt+Left", map["WINDOW-LEFT-HALF"]);
        Assert.Null(document.Map("hotkey"));
        Assert.Null(document.Map("notThere"));

        document.SetMap("commandHotkeys", new Dictionary<string, string> { ["window-left-half"] = "Ctrl+Alt+Left", ["window-right-half"] = "Ctrl+Alt+Right" });
        document.Write(path);

        string written = File.ReadAllText(path);
        Assert.Contains("\"COMMANDHOTKEYS\"", written);
        Assert.Contains("\"window-right-half\": \"Ctrl+Alt+Right\"", written);
        var loaded = SettingsStore.Load(path);
        Assert.Equal(2, CommandHotkeyOptions.From(loaded.Settings.CommandHotkeys, Known, []).Hotkeys.Count);

        // An empty list, like none, takes the setting out.
        document.SetMap("commandHotkeys", new Dictionary<string, string>());
        document.Write(path);
        Assert.DoesNotContain("COMMANDHOTKEYS", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class NoMover : IWindowMover
    {
        public WindowMove Place(WindowLayout layout) => WindowMove.Done;
    }

    private sealed class NoPc : ISystemActions
    {
        public void Lock()
        {
        }

        public void Sleep()
        {
        }

        public void Hibernate()
        {
        }

        public void Restart()
        {
        }

        public void ShutDown()
        {
        }

        public void SignOut()
        {
        }

        public bool EmptyRecycleBin() => false;

        public void OpenRecycleBin()
        {
        }

        public bool? ToggleMute() => null;

        public int? ChangeVolume(int by) => null;

        public int? SetVolume(int percent) => null;

        public void Media(MediaKey key)
        {
        }

        public bool ToggleDarkMode() => false;

        public void ShowDesktop()
        {
        }
    }
}
