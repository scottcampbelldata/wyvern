using Beacon.Core.AutoExpand;

namespace Beacon.Tests;

/// <summary>
/// What one key press means for the characters that are kept. These are the rules that decide
/// what Beacon remembers of typing, so every one of them is held here. The keys are made up:
/// nothing is read from a keyboard.
/// </summary>
public class TypedKeyTests
{
    private const nint SomeWindow = 4242;

    // The letter A, as the hook would hand it on.
    private static TypedKey Key(
        int virtualKey = 0x41,
        char character = 'a',
        bool deadKey = false,
        bool chorded = false,
        bool injected = false,
        bool inBeacon = false,
        nint window = SomeWindow) =>
        new(virtualKey, character, deadKey, chorded, injected, inBeacon, window);

    [Theory]
    [InlineData('a')]
    [InlineData('A')]
    [InlineData(';')]
    [InlineData('7')]
    [InlineData(' ')]
    [InlineData('~')]
    public void A_key_that_types_one_character_is_that_character(char character)
    {
        var key = Key(character: character);

        Assert.Equal(KeyKind.Character, key.Kind);
        Assert.Equal(character, key.Character);
    }

    // Characters outside ASCII, by their numbers so that this file holds none: an e with an
    // acute accent, the section sign, the euro sign and a Greek alpha.
    [Theory]
    [InlineData(0x00E9)]
    [InlineData(0x00A7)]
    [InlineData(0x20AC)]
    [InlineData(0x03B1)]
    public void A_character_of_any_alphabet_is_a_character(int character)
    {
        var key = Key(character: (char)character);

        Assert.Equal(KeyKind.Character, key.Kind);
        Assert.Equal((char)character, key.Character);
    }

    [Fact]
    public void Backspace_is_backspace_whatever_character_Windows_gives_for_it()
    {
        // Shift is not one of the keys that make a chord here, so Shift and Backspace arrives
        // as this same key and is still Backspace.
        Assert.Equal(KeyKind.Backspace, Key(KeyCodes.Backspace, (char)8).Kind);
        Assert.Equal(KeyKind.Backspace, Key(KeyCodes.Backspace, (char)0).Kind);
        Assert.Equal(KeyKind.Backspace, Key(KeyCodes.Backspace, 'x').Kind);
    }

    [Fact]
    public void A_dead_key_is_a_dead_key()
    {
        Assert.Equal(KeyKind.DeadKey, Key(0xDE, (char)0, deadKey: true).Kind);
        Assert.Equal(KeyKind.DeadKey, Key(0xDE, (char)0xB4, deadKey: true).Kind);
    }

    // The second number is the character Windows gives for the key, as a number so that no
    // character that cannot be shown ends up in a test's name: 13 for Enter, 27 for Esc, 9 for
    // Tab, and 0 for a key that types nothing.
    [Theory]
    [InlineData(KeyCodes.Enter, 13)]
    [InlineData(KeyCodes.Escape, 27)]
    [InlineData(KeyCodes.Tab, 9)]
    [InlineData(KeyCodes.Left, 0)]
    [InlineData(KeyCodes.Up, 0)]
    [InlineData(KeyCodes.Right, 0)]
    [InlineData(KeyCodes.Down, 0)]
    [InlineData(KeyCodes.Home, 0)]
    [InlineData(KeyCodes.End, 0)]
    [InlineData(KeyCodes.PageUp, 0)]
    [InlineData(KeyCodes.PageDown, 0)]
    [InlineData(KeyCodes.Insert, 0)]
    [InlineData(KeyCodes.Delete, 0)]
    public void The_keys_that_end_a_line_or_move_the_caret_break_the_run(int virtualKey, int character)
    {
        Assert.Equal(KeyKind.Break, Key(virtualKey, (char)character).Kind);
    }

    [Theory]
    [InlineData(KeyCodes.Enter)]
    [InlineData(KeyCodes.Escape)]
    [InlineData(KeyCodes.Tab)]
    [InlineData(KeyCodes.Left)]
    [InlineData(KeyCodes.Up)]
    [InlineData(KeyCodes.Right)]
    [InlineData(KeyCodes.Down)]
    [InlineData(KeyCodes.Home)]
    [InlineData(KeyCodes.End)]
    [InlineData(KeyCodes.PageUp)]
    [InlineData(KeyCodes.PageDown)]
    [InlineData(KeyCodes.Insert)]
    [InlineData(KeyCodes.Delete)]
    public void They_break_the_run_whatever_character_Windows_gives_for_them(int virtualKey)
    {
        // The rule goes by which key it is. Were Windows to hand a letter on for Enter, it
        // would still be a break and the letter would not be kept.
        Assert.Equal(KeyKind.Break, Key(virtualKey, 'x').Kind);
    }

    [Theory]
    [InlineData(KeyCodes.Shift)]
    [InlineData(KeyCodes.LeftShift)]
    [InlineData(KeyCodes.RightShift)]
    [InlineData(KeyCodes.Control)]
    [InlineData(KeyCodes.LeftControl)]
    [InlineData(KeyCodes.RightControl)]
    [InlineData(KeyCodes.CapsLock)]
    [InlineData(KeyCodes.NumLock)]
    [InlineData(KeyCodes.ScrollLock)]
    public void Shift_Ctrl_and_the_lock_keys_by_themselves_change_nothing(int virtualKey)
    {
        Assert.Equal(KeyKind.Modifier, Key(virtualKey, (char)0).Kind);

        // Held down, Ctrl repeats, and by then Ctrl counts as held: still nothing.
        Assert.Equal(KeyKind.Modifier, Key(virtualKey, (char)0, chorded: true).Kind);
    }

    [Theory]
    [InlineData(KeyCodes.Alt)]
    [InlineData(0xA4)]                    // the left Alt key
    [InlineData(0xA5)]                    // the right Alt key, which is AltGr on many keyboards
    [InlineData(KeyCodes.LeftWindows)]
    [InlineData(KeyCodes.RightWindows)]
    public void The_Alt_and_Windows_keys_themselves_break_the_run(int virtualKey)
    {
        // Each opens a menu when it is let go of by itself, and what is typed next goes there.
        Assert.Equal(KeyKind.Break, Key(virtualKey, (char)0).Kind);
        Assert.Equal(KeyKind.Break, Key(virtualKey, (char)0, chorded: true).Kind);
    }

    [Theory]
    [InlineData(0x70)]   // F1
    [InlineData(0x74)]   // F5
    [InlineData(0x7B)]   // F12
    [InlineData(0x5D)]   // the menu key
    [InlineData(0x2C)]   // Print Screen
    [InlineData(0xAD)]   // volume mute
    public void A_key_that_types_nothing_breaks_the_run(int virtualKey)
    {
        Assert.Equal(KeyKind.Break, Key(virtualKey, (char)0).Kind);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    [InlineData(0x16)]
    [InlineData(0x1F)]
    [InlineData(0x7F)]
    [InlineData(0x85)]
    public void A_control_character_is_not_kept(int character)
    {
        Assert.Equal(KeyKind.Break, Key(character: (char)character).Kind);
    }

    [Theory]
    [InlineData(0xD83D)]
    [InlineData(0xDE00)]
    public void Half_of_a_character_that_takes_two_positions_is_not_kept(int half)
    {
        Assert.Equal(KeyKind.Break, Key(character: (char)half).Kind);
    }

    [Fact]
    public void A_key_pressed_with_Ctrl_Alt_or_a_Windows_key_held_breaks_the_run_whatever_it_types()
    {
        Assert.Equal(KeyKind.Break, Key(0x56, 'v', chorded: true).Kind);
        Assert.Equal(KeyKind.Break, Key(0x51, '@', chorded: true).Kind);
        Assert.Equal(KeyKind.Break, Key(KeyCodes.Backspace, (char)8, chorded: true).Kind);
        Assert.Equal(KeyKind.Break, Key(0xDE, (char)0, deadKey: true, chorded: true).Kind);
    }

    [Fact]
    public void A_key_that_a_program_pressed_breaks_the_run_and_is_never_a_character()
    {
        // What another program types, a password manager for one, is never kept. And Beacon's
        // own Backspaces and Ctrl+V are a change of the text like any other.
        Assert.Equal(KeyKind.Break, Key(character: 'a', injected: true).Kind);
        Assert.Equal(KeyKind.Break, Key(KeyCodes.Backspace, (char)8, injected: true).Kind);
        Assert.Equal(KeyKind.Break, Key(KeyCodes.Control, (char)0, injected: true).Kind);
        Assert.Equal(KeyKind.Break, Key(0xE7, 'x', injected: true).Kind);
    }

    [Fact]
    public void A_key_typed_into_one_of_Beacons_own_windows_breaks_the_run_and_is_never_a_character()
    {
        Assert.Equal(KeyKind.Break, Key(character: 'a', inBeacon: true).Kind);
        Assert.Equal(KeyKind.Break, Key(KeyCodes.Backspace, (char)8, inBeacon: true).Kind);
        Assert.Equal(KeyKind.Break, Key(KeyCodes.Shift, (char)0, inBeacon: true).Kind);
    }

    [Fact]
    public void A_key_with_no_window_in_front_breaks_the_run()
    {
        Assert.Equal(KeyKind.Break, Key(character: 'a', window: 0).Kind);
        Assert.Equal(KeyKind.Break, Key(KeyCodes.Backspace, (char)8, window: 0).Kind);
    }

    [Fact]
    public void A_key_does_not_print_its_character()
    {
        var key = Key(character: 'Q', window: 987654);

        Assert.Equal("TypedKey", key.ToString());
        Assert.Equal("TypedKey", $"{key}");
    }

    [Fact]
    public void The_key_codes_are_those_of_Windows()
    {
        // From the documentation page "Virtual-Key Codes". A wrong number here would make the
        // rule for that key apply to another key.
        Assert.Equal(0x08, KeyCodes.Backspace);
        Assert.Equal(0x09, KeyCodes.Tab);
        Assert.Equal(0x0D, KeyCodes.Enter);
        Assert.Equal(0x10, KeyCodes.Shift);
        Assert.Equal(0x11, KeyCodes.Control);
        Assert.Equal(0x12, KeyCodes.Alt);
        Assert.Equal(0x14, KeyCodes.CapsLock);
        Assert.Equal(0x1B, KeyCodes.Escape);
        Assert.Equal(0x21, KeyCodes.PageUp);
        Assert.Equal(0x22, KeyCodes.PageDown);
        Assert.Equal(0x23, KeyCodes.End);
        Assert.Equal(0x24, KeyCodes.Home);
        Assert.Equal(0x25, KeyCodes.Left);
        Assert.Equal(0x26, KeyCodes.Up);
        Assert.Equal(0x27, KeyCodes.Right);
        Assert.Equal(0x28, KeyCodes.Down);
        Assert.Equal(0x2D, KeyCodes.Insert);
        Assert.Equal(0x2E, KeyCodes.Delete);
        Assert.Equal(0x5B, KeyCodes.LeftWindows);
        Assert.Equal(0x5C, KeyCodes.RightWindows);
        Assert.Equal(0x90, KeyCodes.NumLock);
        Assert.Equal(0x91, KeyCodes.ScrollLock);
        Assert.Equal(0xA0, KeyCodes.LeftShift);
        Assert.Equal(0xA1, KeyCodes.RightShift);
        Assert.Equal(0xA2, KeyCodes.LeftControl);
        Assert.Equal(0xA3, KeyCodes.RightControl);
    }
}
