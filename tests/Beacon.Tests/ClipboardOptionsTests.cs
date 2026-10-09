using System.Text.Json;
using Beacon.Core.ClipboardHistory;
using Beacon.Core.Settings;

namespace Beacon.Tests;

public sealed class ClipboardOptionsTests : IDisposable
{
    // A made-up machine, so that no test depends on the name of the one it runs on.
    private const string Machine = "OTTER-PC";

    private const string DeviceNameNote = "\"deviceName\" in settings.json must be text. This machine's name is used instead.";
    private const string RetentionNote = "\"clipboardRetentionDays\" must be a whole number from 1 to 3650. 90 is used instead.";
    private const string NotAListNote = "\"clipboardExcludedApps\" must be a list of program names. The built-in list is used instead.";
    private const string OneEntryNote = "One entry of \"clipboardExcludedApps\" in settings.json cannot be used and is ignored.";
    private const string NoUsableEntryNote = "No entry of \"clipboardExcludedApps\" in settings.json can be used. The built-in list is used instead.";
    private const string HotkeyNotTextNote = "\"clipboardHotkey\" must be text such as \"Ctrl+Shift+V\". The clipboard history keeps the hotkey it had, if any.";
    private const string SameHotkeyNote = "\"clipboardHotkey\" is the same as \"hotkey\", so it is not used. The clipboard history keeps the hotkey it had, if any.";

    private static readonly string[] BuiltInApps = ["1password.exe", "bitwarden.exe", "keepass.exe", "keepassxc.exe", "lastpass.exe", "dashlane.exe"];

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string SettingsPath => _dir.File("settings.json");

    /// <summary>Reads settings the way the app does: from a settings.json holding this text.</summary>
    private ClipboardOptions FromFile(string json)
    {
        File.WriteAllText(SettingsPath, json);
        var loaded = SettingsStore.Load(SettingsPath);
        Assert.Null(loaded.Error);
        return ClipboardOptions.From(loaded.Settings, Machine);
    }

    private static void AssertDefaults(ClipboardOptions options)
    {
        Assert.Equal(Machine, options.DeviceName);
        Assert.Equal(90, options.RetentionDays);
        Assert.Equal(BuiltInApps, options.ExcludedApps);
        Assert.Null(options.Hotkey);
        Assert.Null(options.HotkeyText);
        Assert.False(options.HotkeyInvalid);
        Assert.Empty(options.Notes);
    }

    private static string NotAHotkeyNote(string shown) =>
        $"\"{shown}\" is not a hotkey Beacon understands. The clipboard history keeps the hotkey it had, if any.";

    [Fact]
    public void Without_the_keys_the_machines_name_90_days_the_built_in_list_and_no_hotkey_apply()
    {
        AssertDefaults(FromFile("{ }"));
    }

    [Fact]
    public void Keys_written_as_null_mean_the_same_as_no_keys()
    {
        AssertDefaults(FromFile("""{ "deviceName": null, "clipboardRetentionDays": null, "clipboardExcludedApps": null, "clipboardHotkey": null }"""));
    }

    [Fact]
    public void The_built_in_list_is_six_password_managers_in_this_order()
    {
        Assert.Equal(BuiltInApps, ClipboardOptions.DefaultExcludedApps);
    }

    [Fact]
    public void The_default_options_are_the_same_as_an_empty_file_on_this_machine()
    {
        var options = ClipboardOptions.Default;

        // Whatever this machine is called, it is the name that is used when none is passed.
        Assert.Equal(ClipboardOptions.SafeDeviceName(null, Environment.MachineName), options.DeviceName);
        Assert.Equal(90, options.RetentionDays);
        Assert.Equal(BuiltInApps, options.ExcludedApps);
        Assert.Null(options.Hotkey);
        Assert.Null(options.HotkeyText);
        Assert.False(options.HotkeyInvalid);
        Assert.Empty(options.Notes);
    }

    [Theory]
    [InlineData("Laptop")]
    [InlineData("Scott's laptop (work)")]
    [InlineData("otter-pc")]
    public void A_device_name_is_used_as_written(string name)
    {
        var options = FromFile($$"""{ "deviceName": "{{name}}" }""");

        Assert.Equal(name, options.DeviceName);
        Assert.Empty(options.Notes);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void A_blank_device_name_means_the_machines_name(string value)
    {
        var options = FromFile($$"""{ "deviceName": {{value}} }""");

        Assert.Equal(Machine, options.DeviceName);
        Assert.Empty(options.Notes);
    }

    [Theory]
    [InlineData("7")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("""["Laptop"]""")]
    [InlineData("""{ "name": "Laptop" }""")]
    [InlineData("\"\\ud800\"")]
    public void A_device_name_that_is_not_text_is_noted_and_the_machines_name_is_used(string value)
    {
        // The last row is half of a surrogate pair: valid JSON, but not text that can be read.
        var options = FromFile($$"""{ "deviceName": {{value}} }""");

        Assert.Equal(Machine, options.DeviceName);
        Assert.Equal([DeviceNameNote], options.Notes);
    }

    [Fact]
    public void A_device_name_is_made_safe_as_a_folder_name()
    {
        var options = FromFile("""{ "deviceName": "  Work: PC?  " }""");

        Assert.Equal("Work_ PC_", options.DeviceName);
        Assert.Empty(options.Notes);
    }

    [Fact]
    public void The_machines_name_is_made_safe_as_a_folder_name_too()
    {
        Assert.Equal("_NUL", ClipboardOptions.From(new BeaconSettings(), "NUL").DeviceName);
    }

    [Fact]
    public void A_machine_with_no_usable_name_is_called_device()
    {
        Assert.Equal("device", ClipboardOptions.From(new BeaconSettings(), "   ").DeviceName);
    }

    [Theory]
    [InlineData("OTTER-PC")]
    [InlineData("Scott's Laptop (2)")]
    [InlineData("MiXeD_case")]
    [InlineData("Büro-Rechner")]
    [InlineData("Ot ter")]
    [InlineData("a.b")]
    [InlineData(".hidden")]
    [InlineData("~x~")]
    [InlineData("_")]
    public void An_ordinary_name_is_kept_as_written(string name)
    {
        Assert.Equal(name, ClipboardOptions.SafeDeviceName(name, Machine));
    }

    [Theory]
    [InlineData("  Otter  ")]
    [InlineData("\tOtter\r\n")]
    [InlineData("\u00A0Otter\u2003")]
    public void White_space_around_a_name_is_dropped(string written)
    {
        Assert.Equal("Otter", ClipboardOptions.SafeDeviceName(written, Machine));
    }

    [Theory]
    [InlineData('<')]
    [InlineData('>')]
    [InlineData(':')]
    [InlineData('"')]
    [InlineData('/')]
    [InlineData('\\')]
    [InlineData('|')]
    [InlineData('?')]
    [InlineData('*')]
    public void Each_character_a_folder_name_may_not_hold_becomes_an_underscore(char forbidden)
    {
        Assert.Equal("Ot_ter", ClipboardOptions.SafeDeviceName($"Ot{forbidden}ter", Machine));
    }

    [Fact]
    public void Every_control_character_becomes_an_underscore()
    {
        // Everything below U+0020, and U+007F. They are inside the name, where trimming does not reach.
        foreach (int code in Enumerable.Range(0, 0x20).Append(0x7F))
        {
            Assert.Equal("Ot_ter", ClipboardOptions.SafeDeviceName($"Ot{(char)code}ter", Machine));
        }
    }

    [Fact]
    public void Half_of_a_surrogate_pair_with_no_partner_becomes_an_underscore()
    {
        Assert.Equal("Ot_ter", ClipboardOptions.SafeDeviceName("Ot\ud83dter", Machine));
        Assert.Equal("Ot_ter", ClipboardOptions.SafeDeviceName("Ot\ude00ter", Machine));
        Assert.Equal("Otter_", ClipboardOptions.SafeDeviceName("Otter\ud83d", Machine));

        // The two halves of an emoji the wrong way round are two halves, not one character.
        Assert.Equal("Ot__ter", ClipboardOptions.SafeDeviceName("Ot\ude00\ud83dter", Machine));
    }

    [Fact]
    public void A_whole_emoji_is_kept()
    {
        Assert.Equal("Otter \U0001F600", ClipboardOptions.SafeDeviceName("Otter \U0001F600", Machine));
    }

    [Fact]
    public void A_replacement_character_that_was_really_written_is_kept()
    {
        // U+FFFD is what half of a pair is often read as, but written out it is a character
        // like any other, and a folder name may hold it.
        Assert.Equal("Ot\uFFFDter", ClipboardOptions.SafeDeviceName("Ot\uFFFDter", Machine));
    }

    [Theory]
    [InlineData("Otter.", "Otter_")]
    [InlineData("Otter. ", "Otter_")]
    [InlineData("Otter..", "Otter._")]
    [InlineData("Otter .", "Otter _")]
    [InlineData("Otter. .", "Otter. _")]
    [InlineData(".", "_")]
    [InlineData("..", "._")]
    [InlineData("...", ".._")]
    public void A_dot_or_a_space_at_the_end_becomes_an_underscore_because_windows_would_drop_it(string written, string expected)
    {
        Assert.Equal(expected, ClipboardOptions.SafeDeviceName(written, Machine));
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("con", "_con")]
    [InlineData("Prn", "_Prn")]
    [InlineData("aUx", "_aUx")]
    [InlineData("NUL", "_NUL")]
    [InlineData("COM1", "_COM1")]
    [InlineData("com5", "_com5")]
    [InlineData("Com9", "_Com9")]
    [InlineData("LPT1", "_LPT1")]
    [InlineData("lpt9", "_lpt9")]
    [InlineData("COM\u00B9", "_COM\u00B9")]
    [InlineData("lpt\u00B3", "_lpt\u00B3")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("NUL.tar.gz", "_NUL.tar.gz")]
    [InlineData("Com3.backup", "_Com3.backup")]
    [InlineData("nul .txt", "_nul .txt")]
    [InlineData("conin$", "_conin$")]
    [InlineData("CONOUT$", "_CONOUT$")]
    [InlineData("ConIn$.txt", "_ConIn$.txt")]
    [InlineData("conout$", "_conout$")]
    public void A_name_windows_keeps_for_a_device_gets_an_underscore_in_front(string written, string expected)
    {
        Assert.Equal(expected, ClipboardOptions.SafeDeviceName(written, Machine));
    }

    [Theory]
    [InlineData("CONIN")]
    [InlineData("CONOUT")]
    [InlineData("my$conin$")]
    [InlineData("CONIN$$")]
    public void A_name_that_only_looks_like_the_console_devices_is_kept(string name)
    {
        Assert.Equal(name, ClipboardOptions.SafeDeviceName(name, Machine));
    }

    [Fact]
    public void A_file_name_is_made_safe_by_the_same_rules_with_a_limit_of_the_callers_own()
    {
        Assert.Equal(new string('x', 120), ClipboardOptions.SafeFileName(new string('x', 200), 120));
        Assert.Equal(new string('x', 120), ClipboardOptions.SafeFileName(new string('x', 120), 120));
        Assert.Equal(new string('x', 119), ClipboardOptions.SafeFileName(new string('x', 119), 120));
        Assert.Equal("xxxxx", ClipboardOptions.SafeFileName("xxxxxxxxxx", 5));
    }

    [Fact]
    public void A_file_name_with_a_limit_of_nothing_is_empty()
    {
        Assert.Equal("", ClipboardOptions.SafeFileName("picture.png", 0));
        Assert.Equal("", ClipboardOptions.SafeFileName("nul", 0));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void A_blank_file_name_is_empty_text_and_is_not_replaced_by_anything(string? text)
    {
        // SafeDeviceName falls back on the machine's name. This does not: the caller decides.
        Assert.Equal("", ClipboardOptions.SafeFileName(text, 120));
    }

    [Theory]
    [InlineData(@"..\..\x", ".._.._x")]
    [InlineData("../../x", ".._.._x")]
    [InlineData(@"C:\Windows\x.png", "C__Windows_x.png")]
    [InlineData("  holiday: 2025?.png  ", "holiday_ 2025_.png")]
    [InlineData("photo.png.", "photo.png_")]
    public void A_file_name_can_never_reach_another_folder(string text, string expected)
    {
        Assert.Equal(expected, ClipboardOptions.SafeFileName(text, 120));
    }

    [Fact]
    public void A_reserved_file_name_still_fits_the_limit_after_its_underscore_is_put_in_front()
    {
        // The "_" takes a place, so the name is cut by one character more.
        Assert.Equal("_NU", ClipboardOptions.SafeFileName("NUL", 3));

        // And when the cut leaves a dot at the end, that becomes "_" in the same way.
        Assert.Equal("_nul_", ClipboardOptions.SafeFileName("nul.x", 5));

        string kept = ClipboardOptions.SafeFileName("nul." + new string('x', 200), 120);
        Assert.Equal("_nul." + new string('x', 115), kept);
        Assert.Equal(120, kept.Length);
    }

    [Theory]
    [InlineData("CONSOLE")]
    [InlineData("NULL")]
    [InlineData("COM")]
    [InlineData("COM0")]
    [InlineData("COM10")]
    [InlineData("LPT")]
    [InlineData("LPT0")]
    [InlineData("PRN1")]
    [InlineData("my.nul")]
    [InlineData("_NUL")]
    public void A_name_that_only_looks_like_a_device_name_is_kept(string name)
    {
        Assert.Equal(name, ClipboardOptions.SafeDeviceName(name, Machine));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(40)]
    [InlineData(41)]
    [InlineData(60)]
    [InlineData(500)]
    public void A_name_is_kept_up_to_40_characters_and_no_further(int length)
    {
        string kept = ClipboardOptions.SafeDeviceName(new string('x', length), Machine);

        Assert.Equal(new string('x', Math.Min(length, 40)), kept);
    }

    [Fact]
    public void A_name_is_never_cut_in_the_middle_of_a_character()
    {
        // The emoji is two UTF-16 characters. It fits when it ends at the fortieth, and is left
        // out whole when only its first half would fit.
        Assert.Equal(
            new string('x', 38) + "\U0001F600",
            ClipboardOptions.SafeDeviceName(new string('x', 38) + "\U0001F600" + "tail", Machine));
        Assert.Equal(
            new string('x', 39),
            ClipboardOptions.SafeDeviceName(new string('x', 39) + "\U0001F600" + "tail", Machine));
    }

    [Fact]
    public void A_dot_or_a_space_that_a_cut_leaves_at_the_end_becomes_an_underscore_too()
    {
        Assert.Equal(new string('x', 39) + "_", ClipboardOptions.SafeDeviceName(new string('x', 39) + ". and more", Machine));
        Assert.Equal(new string('x', 39) + "_", ClipboardOptions.SafeDeviceName(new string('x', 39) + " and more", Machine));
    }

    [Fact]
    public void The_underscore_in_front_of_a_device_name_does_not_take_the_name_over_40_characters()
    {
        string kept = ClipboardOptions.SafeDeviceName("nul." + new string('x', 36), Machine);

        Assert.Equal("_nul." + new string('x', 35), kept);
        Assert.Equal(40, kept.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void No_name_or_a_blank_one_means_the_machines_name(string? written)
    {
        Assert.Equal(Machine, ClipboardOptions.SafeDeviceName(written, Machine));
    }

    [Theory]
    [InlineData("Otter:PC", "Otter_PC")]
    [InlineData("  otter-pc  ", "otter-pc")]
    [InlineData("nul", "_nul")]
    [InlineData("Otter.", "Otter_")]
    public void The_machines_name_goes_through_the_same_rules(string machineName, string expected)
    {
        Assert.Equal(expected, ClipboardOptions.SafeDeviceName(null, machineName));
    }

    [Fact]
    public void A_long_machine_name_is_cut_to_40_characters_too()
    {
        Assert.Equal(new string('m', 40), ClipboardOptions.SafeDeviceName("", new string('m', 41)));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    [InlineData(null, "\t")]
    public void With_no_usable_name_from_either_the_name_is_device(string? written, string machineName)
    {
        Assert.Equal("device", ClipboardOptions.SafeDeviceName(written, machineName));
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("30", 30)]
    [InlineData("3650", 3650)]
    [InlineData("30.0", 30)]
    [InlineData("30.000", 30)]
    [InlineData("3e1", 30)]
    [InlineData("3E1", 30)]
    [InlineData("0.3e2", 30)]
    [InlineData("1.0", 1)]
    [InlineData("3650.0", 3650)]
    [InlineData("3.65e3", 3650)]
    public void A_whole_number_of_days_is_used_in_whatever_form_it_is_written(string number, int days)
    {
        var options = FromFile($$"""{ "clipboardRetentionDays": {{number}} }""");

        Assert.Equal(days, options.RetentionDays);
        Assert.Empty(options.Notes);
    }

    [Theory]
    [InlineData("\"30\"")]
    [InlineData("\"\"")]
    [InlineData("1.5")]
    [InlineData("30.5")]
    [InlineData("0.5")]
    [InlineData("0")]
    [InlineData("0.0")]
    [InlineData("-5")]
    [InlineData("3651")]
    [InlineData("3650.5")]
    [InlineData("99999999999")]
    [InlineData("1e999")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("[30]")]
    [InlineData("""{ "days": 30 }""")]
    public void Retention_that_is_not_a_whole_number_from_1_to_3650_is_noted_and_90_days_are_kept(string value)
    {
        var options = FromFile($$"""{ "clipboardRetentionDays": {{value}} }""");

        Assert.Equal(90, options.RetentionDays);
        Assert.Equal([RetentionNote], options.Notes);
    }

    /// <summary>A number as JSON text that is exactly this long: the start, then zeros, then the end.</summary>
    private static string Padded(string start, string end, int length) =>
        start + new string('0', length - start.Length - end.Length) + end;

    [Fact]
    public void A_number_written_with_very_many_digits_is_not_accepted_as_a_whole_number_of_days()
    {
        // The first is not a whole number, but a decimal cannot hold that many digits and would
        // round it to 1. Retention deletes files, so a number written in more than 28
        // characters is out of range, whatever it holds.
        string[] values =
        [
            "0." + new string('9', 29),
            Padded("1.", "", 29),
            Padded("30.", "", 29),
            Padded("30.", "", 60),
            Padded("0.3", "e2", 29),
        ];

        foreach (string value in values)
        {
            Assert.True(value.Length > 28);

            var options = FromFile($$"""{ "clipboardRetentionDays": {{value}} }""");

            Assert.Equal(90, options.RetentionDays);
            Assert.Equal([RetentionNote], options.Notes);
        }
    }

    [Fact]
    public void A_number_written_in_28_characters_is_still_judged_by_its_value()
    {
        var days = new (string Value, int Days)[]
        {
            (Padded("1.", "", 28), 1),
            (Padded("30.", "", 28), 30),
            (Padded("0.3", "e2", 28), 30),
        };

        foreach ((string value, int expected) in days)
        {
            Assert.Equal(28, value.Length);

            var options = FromFile($$"""{ "clipboardRetentionDays": {{value}} }""");

            Assert.Equal(expected, options.RetentionDays);
            Assert.Empty(options.Notes);
        }
    }

    [Fact]
    public void A_list_replaces_the_built_in_list()
    {
        var options = FromFile("""{ "clipboardExcludedApps": ["signal.exe", "notepad.exe"] }""");

        Assert.Equal(["signal.exe", "notepad.exe"], options.ExcludedApps);
        Assert.Empty(options.Notes);
    }

    [Fact]
    public void An_empty_list_means_that_no_program_is_excluded()
    {
        var options = FromFile("""{ "clipboardExcludedApps": [] }""");

        Assert.Empty(options.ExcludedApps);
        Assert.False(options.Excludes("keepass.exe"));
        Assert.Empty(options.Notes);
    }

    [Fact]
    public void Listed_programs_are_kept_in_the_order_written_under_the_names_they_are_known_by()
    {
        var options = FromFile("""{ "clipboardExcludedApps": ["Zebra", "C:\\Tools\\Signal.EXE", "  apple.exe  ", "D:/Apps/Mango"] }""");

        Assert.Equal(["zebra.exe", "signal.exe", "apple.exe", "mango.exe"], options.ExcludedApps);
        Assert.Empty(options.Notes);
    }

    [Fact]
    public void A_program_listed_twice_or_in_two_ways_is_kept_once_and_is_not_counted_as_a_mistake()
    {
        var options = FromFile("""{ "clipboardExcludedApps": ["KeePass", "signal", "keepass.exe", "KEEPASS.EXE", "C:\\Tools\\KeePass.exe"] }""");

        Assert.Equal(["keepass.exe", "signal.exe"], options.ExcludedApps);
        Assert.Empty(options.Notes);
    }

    [Theory]
    [InlineData("7")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("""["keepass"]""")]
    [InlineData("""{ "name": "keepass" }""")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("\"\\ud800\"")]
    [InlineData("\"C:\\\\Tools\\\\\"")]
    [InlineData("\"/\"")]
    public void An_entry_that_cannot_be_used_is_left_out_and_counted_and_the_others_are_kept(string entry)
    {
        // "\ud800" is half of a surrogate pair: valid JSON, but not text that can be read. The
        // two rows after it end in a separator, so they name a folder and no program.
        var options = FromFile($$"""{ "clipboardExcludedApps": [{{entry}}, "signal"] }""");

        Assert.Equal(["signal.exe"], options.ExcludedApps);
        Assert.Equal([OneEntryNote], options.Notes);
    }

    [Fact]
    public void Several_unusable_entries_are_counted_together()
    {
        var options = FromFile("""{ "clipboardExcludedApps": [7, "signal", "", null, "notepad"] }""");

        Assert.Equal(["signal.exe", "notepad.exe"], options.ExcludedApps);
        Assert.Equal(["3 entries of \"clipboardExcludedApps\" in settings.json cannot be used and are ignored."], options.Notes);
    }

    [Theory]
    [InlineData("[7]")]
    [InlineData("[5, true]")]
    [InlineData("""["", "   "]""")]
    [InlineData("""[null, [], { }]""")]
    [InlineData("""["C:\\Tools\\", "/"]""")]
    [InlineData("""["*keepass*"]""")]
    [InlineData("""["keepass.exe, bitwarden.exe"]""")]
    public void A_list_with_entries_none_of_which_can_be_used_leaves_the_built_in_list_in_use(string list)
    {
        // This setting keeps passwords out of the history, so a mistake in it must not switch
        // that off. Only the one note is given, not a count of the entries as well.
        var options = FromFile($$"""{ "clipboardExcludedApps": {{list}} }""");

        Assert.Equal(BuiltInApps, options.ExcludedApps);
        Assert.True(options.Excludes("KeePass.exe"));
        Assert.Equal([NoUsableEntryNote], options.Notes);
    }

    [Fact]
    public void A_list_with_one_usable_entry_replaces_the_built_in_list_and_the_others_are_counted()
    {
        var options = FromFile("""{ "clipboardExcludedApps": [5, "signal", true] }""");

        Assert.Equal(["signal.exe"], options.ExcludedApps);
        Assert.False(options.Excludes("keepass.exe"));
        Assert.Equal(["2 entries of \"clipboardExcludedApps\" in settings.json cannot be used and are ignored."], options.Notes);
    }

    [Fact]
    public void A_program_listed_twice_is_a_usable_entry_and_so_the_list_replaces_the_built_in_one()
    {
        var options = FromFile("""{ "clipboardExcludedApps": ["signal", "Signal.exe", 7] }""");

        Assert.Equal(["signal.exe"], options.ExcludedApps);
        Assert.Equal([OneEntryNote], options.Notes);
    }

    /// <summary>
    /// Text that can never be the name of a program: it is null for ProgramName, so an entry of
    /// this kind is one that cannot be used. Some are made with casts, so that characters a tool
    /// might change or drop are not in this file.
    /// </summary>
    public static TheoryData<string> NotProgramNames => new()
    {
        "*keepass*",
        "keepass.exe:",
        "C:",
        "C:keepass.exe",
        "keepass.",
        "keepass.exe.",
        "a,b",
        "a;b",
        "keepass.exe, bitwarden.exe",
        "keepass.exe;bitwarden.exe",
        "<keepass>",
        "keep|pass",
        "keep?pass",
        "keep\"pass",
        "\"keepass.exe",
        "keepass.exe\"",
        "\"",
        "\"\"",
        "\"   \"",
        "\"C:\\Tools\\\"",
        "" + (char)0xFEFF,
        "" + (char)0x200B + (char)7,
        "keepass " + (char)0x200B,
        "keepass." + (char)0x200B,
    };

    [Theory]
    [MemberData(nameof(NotProgramNames))]
    public void Text_that_can_never_be_the_name_of_a_program_is_an_entry_that_cannot_be_used(string entry)
    {
        var options = FromFile("{ \"clipboardExcludedApps\": [" + JsonSerializer.Serialize(entry) + ", \"signal\"] }");

        Assert.Equal(["signal.exe"], options.ExcludedApps);
        Assert.Equal([OneEntryNote], options.Notes);
    }

    [Fact]
    public void A_path_copied_with_quotes_round_it_or_with_an_invisible_character_in_front_is_a_usable_entry()
    {
        var options = FromFile(
            "{ \"clipboardExcludedApps\": [" + JsonSerializer.Serialize("\"C:\\Program Files\\KeePass\\KeePass.exe\"") + ", "
            + JsonSerializer.Serialize((char)0xFEFF + "signal.exe") + ", "
            + JsonSerializer.Serialize((char)0x200B + "Notepad") + "] }");

        Assert.Equal(["keepass.exe", "signal.exe", "notepad.exe"], options.ExcludedApps);
        Assert.Empty(options.Notes);
    }

    [Theory]
    [InlineData("\"keepass.exe\"")]
    [InlineData("\"\"")]
    [InlineData("7")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("""{ "keepass.exe": true }""")]
    public void Excluded_programs_that_are_not_a_list_are_noted_and_the_built_in_list_stays_in_use(string value)
    {
        // This setting keeps passwords out of the history, so a mistake must not switch it off.
        var options = FromFile($$"""{ "clipboardExcludedApps": {{value}} }""");

        Assert.Equal(BuiltInApps, options.ExcludedApps);
        Assert.True(options.Excludes("KeePass.exe"));
        Assert.Equal([NotAListNote], options.Notes);
    }

    [Theory]
    [InlineData("KeePass")]
    [InlineData("keepass.exe")]
    [InlineData("KEEPASS.EXE")]
    [InlineData("KeePass.Exe")]
    [InlineData("  KeePass  ")]
    [InlineData(@"C:\Tools\KeePass.exe")]
    [InlineData("C:/Tools/KeePass.exe")]
    [InlineData(@"C:\Tools/sub\KeePass")]
    [InlineData(@"\\server\share\KeePass.exe")]
    [InlineData(@"""C:\Program Files\KeePass\KeePass.exe""")]
    [InlineData(@"  ""C:\Program Files\KeePass\KeePass.exe""  ")]
    [InlineData(@"""KeePass""")]
    [InlineData(@"""  KeePass  """)]
    [InlineData(@"  ""  KeePass  ""  ")]
    public void A_program_is_known_by_its_file_name_in_lower_case_ending_in_exe(string program)
    {
        // The last rows are what Explorer's "Copy as path" gives: the path with quotes round it.
        Assert.Equal("keepass.exe", ClipboardOptions.ProgramName(program));
    }

    [Fact]
    public void A_character_that_shows_nothing_is_dropped_wherever_it_stands()
    {
        // U+FEFF (a byte order mark), U+200B (a zero width space), U+200E (a direction mark) and
        // a control character. Trim keeps them. They are written as casts, not as escapes or as
        // the characters themselves, so that no tool changes or drops them in this file.
        char bom = (char)0xFEFF;
        char zeroWidthSpace = (char)0x200B;
        char directionMark = (char)0x200E;
        char bell = (char)7;

        Assert.Equal("keepass.exe", ClipboardOptions.ProgramName(bom + "keepass.exe"));
        Assert.Equal("keepass.exe", ClipboardOptions.ProgramName(zeroWidthSpace + "KeePass"));
        Assert.Equal("keepass.exe", ClipboardOptions.ProgramName("kee" + zeroWidthSpace + "pass" + directionMark + ".exe"));
        Assert.Equal("keepass.exe", ClipboardOptions.ProgramName("keepass.exe" + bom));
        Assert.Equal("keepass.exe", ClipboardOptions.ProgramName("keepass" + bell + ".exe"));
        Assert.Equal("keepass.exe", ClipboardOptions.ProgramName("C:\\Tools" + zeroWidthSpace + "\\" + bom + "KeePass.exe"));
        Assert.Equal("keepass.exe", ClipboardOptions.ProgramName("  " + bom + "KeePass  "));
    }

    [Fact]
    public void Half_of_a_surrogate_pair_with_no_partner_makes_the_text_no_program()
    {
        Assert.Null(ClipboardOptions.ProgramName("keep\ud83dpass"));
        Assert.Null(ClipboardOptions.ProgramName("\ude00keepass.exe"));
        Assert.Null(ClipboardOptions.ProgramName("keepass.exe\ud83d"));

        // A whole pair is a character like any other.
        Assert.Equal("keep\U0001F600.exe", ClipboardOptions.ProgramName("keep\U0001F600"));
    }

    [Theory]
    [MemberData(nameof(NotProgramNames))]
    public void Text_that_can_never_be_the_name_of_a_program_names_no_program(string program)
    {
        Assert.Null(ClipboardOptions.ProgramName(program));
    }

    [Theory]
    [InlineData("1Password", "1password.exe")]
    [InlineData("my.tool", "my.tool.exe")]
    [InlineData("edit.com", "edit.com.exe")]
    [InlineData("exe", "exe.exe")]
    [InlineData(".exe", ".exe")]
    [InlineData("Büro", "büro.exe")]
    [InlineData(@"C:\KeePass.exe\notepad", "notepad.exe")]
    public void Exe_is_added_to_a_name_that_does_not_end_in_it(string program, string expected)
    {
        Assert.Equal(expected, ClipboardOptions.ProgramName(program));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\Tools\")]
    [InlineData("C:/Tools/")]
    [InlineData(@"  C:\Tools\  ")]
    [InlineData(@"\")]
    [InlineData("/")]
    public void Nothing_blank_text_and_text_that_ends_in_a_separator_name_no_program(string? program)
    {
        Assert.Null(ClipboardOptions.ProgramName(program));
    }

    [Theory]
    [InlineData("1Password.exe")]
    [InlineData("Bitwarden.exe")]
    [InlineData("KeePass.exe")]
    [InlineData("KeePassXC.exe")]
    [InlineData("LastPass.exe")]
    [InlineData("Dashlane.exe")]
    public void Each_built_in_password_manager_is_excluded_when_nothing_is_written(string program)
    {
        Assert.True(ClipboardOptions.From(new BeaconSettings(), Machine).Excludes(program));
    }

    [Theory]
    [InlineData("keepass.exe")]
    [InlineData("KeePass")]
    [InlineData("KEEPASS.EXE")]
    [InlineData("  keepass.exe  ")]
    [InlineData(@"C:\Program Files\KeePass Password Safe 2\KeePass.exe")]
    [InlineData("C:/Tools/keepass.exe")]
    public void A_program_is_excluded_however_its_name_is_given(string program)
    {
        Assert.True(ClipboardOptions.From(new BeaconSettings(), Machine).Excludes(program));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("notepad.exe")]
    [InlineData("keepass2.exe")]
    [InlineData("keepass.exe.bak")]
    [InlineData(@"C:\KeePass\notepad.exe")]
    [InlineData(@"C:\Tools\KeePass.exe\")]
    public void No_program_and_a_program_that_is_not_listed_are_not_excluded(string? program)
    {
        Assert.False(ClipboardOptions.From(new BeaconSettings(), Machine).Excludes(program));
    }

    [Fact]
    public void A_program_in_the_list_is_excluded_when_it_is_given_as_a_path_with_quotes_round_it()
    {
        var builtIn = ClipboardOptions.From(new BeaconSettings(), Machine);
        var owners = FromFile("""{ "clipboardExcludedApps": ["Signal"] }""");

        Assert.True(builtIn.Excludes(@"""C:\Program Files\KeePass Password Safe 2\KeePass.exe"""));
        Assert.True(owners.Excludes(@"""C:\Apps\Signal.exe"""));
        Assert.True(builtIn.Excludes((char)0xFEFF + "keepass.exe"));
    }

    [Theory]
    [MemberData(nameof(NotProgramNames))]
    public void Text_that_can_never_be_the_name_of_a_program_is_not_excluded(string program)
    {
        // Whatever the list holds, such text is no program and so matches nothing.
        Assert.False(ClipboardOptions.From(new BeaconSettings(), Machine).Excludes(program));
    }

    [Fact]
    public void The_owners_list_decides_what_is_excluded()
    {
        var options = FromFile("""{ "clipboardExcludedApps": ["Signal"] }""");

        Assert.True(options.Excludes(@"C:\Apps\Signal.exe"));
        Assert.False(options.Excludes("KeePass.exe"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_hotkey_means_no_hotkey_and_is_not_a_mistake(string written)
    {
        var options = FromFile($$"""{ "clipboardHotkey": "{{written}}" }""");

        Assert.Null(options.Hotkey);
        Assert.Null(options.HotkeyText);
        Assert.False(options.HotkeyInvalid);
        Assert.Empty(options.Notes);
    }

    [Theory]
    [InlineData("Ctrl+Shift+V", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x56u, "Ctrl+Shift+V")]
    [InlineData("  ctrl + shift + v  ", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x56u, "ctrl + shift + v")]
    [InlineData("Win+F9", HotkeyModifiers.Win, 0x78u, "Win+F9")]
    [InlineData("F9", HotkeyModifiers.None, 0x78u, "F9")]
    public void A_hotkey_is_used_and_its_text_is_kept_without_the_white_space_around_it(
        string written,
        HotkeyModifiers modifiers,
        uint virtualKey,
        string text)
    {
        var options = FromFile($$"""{ "clipboardHotkey": "{{written}}" }""");

        Assert.Equal(new Hotkey(modifiers, virtualKey), options.Hotkey);
        Assert.Equal(text, options.HotkeyText);
        Assert.False(options.HotkeyInvalid);
        Assert.Empty(options.Notes);
    }

    [Theory]
    [InlineData("7")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("""["Ctrl+Shift+V"]""")]
    [InlineData("""{ "key": "Ctrl+Shift+V" }""")]
    [InlineData("\"\\ud800\"")]
    public void A_hotkey_that_is_not_text_cannot_be_used_and_is_noted(string value)
    {
        // The last row is half of a surrogate pair: valid JSON, but not text that can be read.
        var options = FromFile($$"""{ "clipboardHotkey": {{value}} }""");

        Assert.Null(options.Hotkey);
        Assert.Null(options.HotkeyText);
        Assert.True(options.HotkeyInvalid);
        Assert.Equal([HotkeyNotTextNote], options.Notes);
    }

    [Theory]
    [InlineData("Banana", "Banana")]
    [InlineData("  Banana  ", "Banana")]
    [InlineData("V", "V")]
    [InlineData("Ctrl+", "Ctrl+")]
    [InlineData("Ban\\u202Eana", "Banana")]
    [InlineData("Ba\\tna\\u0000na", "Banana")]
    public void Text_that_is_not_a_hotkey_cannot_be_used_and_the_note_quotes_what_can_be_seen_of_it(string written, string shown)
    {
        // The last two rows hold characters that show nothing or turn the text around, written
        // as JSON escapes. They are left out of the note.
        var options = FromFile($$"""{ "clipboardHotkey": "{{written}}" }""");

        Assert.Null(options.Hotkey);
        Assert.Null(options.HotkeyText);
        Assert.True(options.HotkeyInvalid);
        Assert.Equal([NotAHotkeyNote(shown)], options.Notes);
    }

    [Fact]
    public void Long_text_that_is_not_a_hotkey_is_shortened_in_the_note()
    {
        string written = "Ctrl+" + new string('x', 60);

        var options = FromFile($$"""{ "clipboardHotkey": "{{written}}" }""");

        Assert.True(options.HotkeyInvalid);
        Assert.Equal([NotAHotkeyNote("Ctrl+" + new string('x', 34) + "…")], options.Notes);
    }

    [Fact]
    public void The_text_in_the_note_is_never_cut_in_the_middle_of_a_character()
    {
        // The emoji is two UTF-16 characters and only its first half would fit.
        string written = new string('x', 38) + "\U0001F600" + "yyyy";

        var options = FromFile($$"""{ "clipboardHotkey": "{{written}}" }""");

        Assert.True(options.HotkeyInvalid);
        Assert.Equal([NotAHotkeyNote(new string('x', 38) + "…")], options.Notes);
    }

    [Theory]
    [InlineData("Ctrl+Shift+K", "Ctrl+Shift+K")]
    [InlineData("Ctrl+Shift+K", "  shift + control + k  ")]
    [InlineData("F12", "f12")]
    public void The_same_hotkey_as_the_main_one_cannot_be_used_however_either_is_written(string main, string clipboard)
    {
        var options = FromFile($$"""{ "hotkey": "{{main}}", "clipboardHotkey": "{{clipboard}}" }""");

        Assert.Null(options.Hotkey);
        Assert.Null(options.HotkeyText);
        Assert.True(options.HotkeyInvalid);
        Assert.Equal([SameHotkeyNote], options.Notes);
    }

    [Fact]
    public void The_main_hotkey_that_applies_when_none_is_written_cannot_be_used_either()
    {
        var options = FromFile("""{ "clipboardHotkey": "Alt+Space" }""");

        Assert.Null(options.Hotkey);
        Assert.True(options.HotkeyInvalid);
        Assert.Equal([SameHotkeyNote], options.Notes);
    }

    [Fact]
    public void A_main_hotkey_that_is_not_a_hotkey_does_not_stand_in_the_way()
    {
        var options = FromFile("""{ "hotkey": "Banana", "clipboardHotkey": "Ctrl+Shift+V" }""");

        Assert.Equal(new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x56), options.Hotkey);
        Assert.Equal("Ctrl+Shift+V", options.HotkeyText);
        Assert.False(options.HotkeyInvalid);
        Assert.Empty(options.Notes);
    }

    [Fact]
    public void Notes_come_in_a_fixed_order_whatever_the_order_of_the_keys()
    {
        var options = FromFile("""
            {
              "clipboardHotkey": "Banana",
              "clipboardExcludedApps": [7, "signal", null],
              "clipboardRetentionDays": 0,
              "deviceName": []
            }
            """);

        Assert.Equal(
            [
                DeviceNameNote,
                RetentionNote,
                "2 entries of \"clipboardExcludedApps\" in settings.json cannot be used and are ignored.",
                NotAHotkeyNote("Banana"),
            ],
            options.Notes);
    }

    [Fact]
    public void A_good_setting_is_used_beside_a_bad_one()
    {
        var options = FromFile("""{ "deviceName": "Laptop", "clipboardRetentionDays": "soon", "clipboardExcludedApps": ["signal"], "clipboardHotkey": 7 }""");

        Assert.Equal("Laptop", options.DeviceName);
        Assert.Equal(90, options.RetentionDays);
        Assert.Equal(["signal.exe"], options.ExcludedApps);
        Assert.Null(options.Hotkey);
        Assert.True(options.HotkeyInvalid);
        Assert.Equal([RetentionNote, HotkeyNotTextNote], options.Notes);
    }

    [Fact]
    public void A_note_quotes_nothing_but_the_hotkey_text()
    {
        var options = FromFile("""{ "deviceName": ["walrus"], "clipboardRetentionDays": -77, "clipboardExcludedApps": [["otter"], 123456, "signal"], "clipboardHotkey": 4242 }""");

        Assert.Equal(4, options.Notes.Count);
        Assert.All(options.Notes, note =>
        {
            Assert.DoesNotContain("walrus", note);
            Assert.DoesNotContain("77", note);
            Assert.DoesNotContain("otter", note);
            Assert.DoesNotContain("123456", note);
            Assert.DoesNotContain("signal", note);
            Assert.DoesNotContain("4242", note);
        });
    }

    [Fact]
    public void The_lists_cannot_be_changed_by_a_caller_who_casts_them()
    {
        // The capture code reads these on another thread, so nobody may be able to change them.
        var written = FromFile("""{ "clipboardExcludedApps": ["signal"], "clipboardRetentionDays": 0 }""");
        var builtIn = FromFile("""{ "clipboardExcludedApps": [7] }""");
        var empty = FromFile("""{ "clipboardExcludedApps": [] }""");
        var lists = new IReadOnlyList<string>[]
        {
            written.ExcludedApps,
            written.Notes,
            builtIn.ExcludedApps,
            builtIn.Notes,
            empty.ExcludedApps,
            empty.Notes,
            ClipboardOptions.Default.ExcludedApps,
            ClipboardOptions.Default.Notes,
            ClipboardOptions.DefaultExcludedApps,
        };

        Assert.Single(written.Notes);
        Assert.Single(builtIn.Notes);
        foreach (var list in lists)
        {
            Assert.IsNotType<List<string>>(list);
            Assert.IsNotType<string[]>(list);
            Assert.True(Assert.IsAssignableFrom<ICollection<string>>(list).IsReadOnly);
        }
    }

    [Fact]
    public void Clipboard_settings_of_the_wrong_kind_leave_every_other_setting_readable()
    {
        File.WriteAllText(
            SettingsPath,
            """
            {
              "hotkey": "Ctrl+Shift+K",
              "startWithWindows": false,
              "indexedFolders": ["D:\\Work"],
              "scriptTimeoutSeconds": 45,
              "deviceName": 7,
              "clipboardRetentionDays": "30",
              "clipboardExcludedApps": "keepass.exe",
              "clipboardHotkey": ["Ctrl+Shift+V"]
            }
            """);

        var loaded = SettingsStore.Load(SettingsPath);
        var options = ClipboardOptions.From(loaded.Settings, Machine);

        Assert.Null(loaded.Error);
        Assert.Equal("Ctrl+Shift+K", loaded.Settings.Hotkey);
        Assert.False(loaded.Settings.StartWithWindows);
        Assert.Equal([@"D:\Work"], loaded.Settings.IndexedFolders);
        Assert.Equal(45, loaded.Settings.ScriptTimeoutSeconds?.GetInt32());
        Assert.Equal(Machine, options.DeviceName);
        Assert.Equal(90, options.RetentionDays);
        Assert.Equal(BuiltInApps, options.ExcludedApps);
        Assert.Null(options.Hotkey);
        Assert.True(options.HotkeyInvalid);
        Assert.Equal([DeviceNameNote, RetentionNote, NotAListNote, HotkeyNotTextNote], options.Notes);
    }

    [Theory]
    [InlineData("""{ "clipboardRetentionDays": 30 """)]
    [InlineData("""{ "clipboardExcludedApps": ["keepass.exe" }""")]
    [InlineData("""{ "clipboardRetentionDays": 3x }""")]
    [InlineData("""{ "deviceName": Laptop }""")]
    public void A_file_that_is_not_valid_json_is_still_reported_as_unreadable(string json)
    {
        File.WriteAllText(SettingsPath, json);

        var loaded = SettingsStore.Load(SettingsPath);

        Assert.StartsWith("settings.json is not valid JSON", loaded.Error);
        Assert.IsAssignableFrom<JsonException>(loaded.Cause);
    }

    [Fact]
    public void The_defaults_file_shows_the_retention_and_the_excluded_programs_and_leaves_the_device_name_and_the_hotkey_out()
    {
        var loaded = SettingsStore.Load(SettingsPath);

        string written = File.ReadAllText(SettingsPath);
        Assert.Contains("\"clipboardRetentionDays\": 90", written);
        Assert.Contains("\"clipboardExcludedApps\": [", written);
        Assert.All(BuiltInApps, app => Assert.Contains($"\"{app}\"", written));
        Assert.DoesNotContain("deviceName", written);
        Assert.DoesNotContain("clipboardHotkey", written);
        Assert.Null(loaded.Settings.DeviceName);
        Assert.Null(loaded.Settings.ClipboardHotkey);
    }

    [Fact]
    public void The_defaults_file_reads_back_to_the_defaults()
    {
        var created = SettingsStore.Load(SettingsPath);
        var reread = SettingsStore.Load(SettingsPath);

        // What was written is what is used, both when the file is made and when it is read again.
        Assert.Null(created.Error);
        Assert.Null(reread.Error);
        AssertDefaults(ClipboardOptions.From(created.Settings, Machine));
        AssertDefaults(ClipboardOptions.From(reread.Settings, Machine));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_decision_carries_the_clipboard_settings(bool firstLoad)
    {
        var loaded = new SettingsLoadResult(new BeaconSettings { ClipboardRetentionDays = JsonSerializer.SerializeToElement(30) }, null);

        var decision = SettingsDecision.From(loaded, firstLoad);

        Assert.False(decision.HotkeyInvalid);
        Assert.NotNull(decision.Clipboard);
        Assert.Equal(30, decision.Clipboard.RetentionDays);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_decision_carries_them_when_the_main_hotkey_is_invalid_too(bool firstLoad)
    {
        var loaded = new SettingsLoadResult(new BeaconSettings { Hotkey = "Banana", ClipboardRetentionDays = JsonSerializer.SerializeToElement(30) }, null);

        var decision = SettingsDecision.From(loaded, firstLoad);

        Assert.True(decision.HotkeyInvalid);
        Assert.NotNull(decision.Clipboard);
        Assert.Equal(30, decision.Clipboard.RetentionDays);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_unreadable_file_leaves_the_clipboard_settings_as_they_are(bool firstLoad)
    {
        var failed = new SettingsLoadResult(new BeaconSettings(), "settings.json is not valid JSON (line 3)", new FormatException("line 3"));

        Assert.Null(SettingsDecision.From(failed, firstLoad).Clipboard);
    }

    [Fact]
    public void Clipboard_history_is_kept_in_the_data_folder()
    {
        Assert.Equal(@"D:\Elsewhere\clipboard", new DataPaths(@"D:\Elsewhere").ClipboardDir);
    }

    [Fact]
    public void Whether_capture_is_paused_is_kept_beside_settings_json()
    {
        Assert.Equal(@"C:\Data\Beacon\clipboard-state.json", new BeaconPaths(@"C:\Data\Beacon").ClipboardStateFile);
    }
}
