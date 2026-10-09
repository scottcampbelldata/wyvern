using Wyvern.Core.ClipboardHistory;

namespace Wyvern.Tests;

/// <summary>
/// Every block here is made up and built in the test, byte by byte where the bytes are what is
/// being checked. Nothing is read from the real clipboard.
/// </summary>
public class ClipboardBytesTests
{
    // Characters that must not stand in this file as themselves are written as numbers: U+FFFD,
    // and the two halves of the surrogate pair that is U+1F600.
    private const char Replacement = (char)0xFFFD;
    private const char High = (char)0xD83D;
    private const char Low = (char)0xDE00;

    // U+1F600, an emoji: one character that is a whole surrogate pair.
    private static readonly string Emoji = $"{High}{Low}";

    // The zero character that ends a list of names, in UTF-16 and in single bytes.
    private static readonly byte[] WideEnd = [0, 0];
    private static readonly byte[] NarrowEnd = [0];

    /// <summary>Text as UTF-16, little endian: two bytes for each character, the low byte first.</summary>
    private static byte[] Wide(string text) => [.. text.SelectMany(c => new[] { (byte)c, (byte)(c >> 8) })];

    /// <summary>Text of ASCII characters as one byte for each.</summary>
    private static byte[] Narrow(string text) => [.. text.Select(c => (byte)c)];

    /// <summary>Each name as UTF-16 with a zero character after it. The zero character that ends the list is not added.</summary>
    private static byte[] WideNames(params string[] names) => [.. names.SelectMany(name => Wide(name + '\0'))];

    /// <summary>Each name as single bytes with a zero byte after it. The zero byte that ends the list is not added.</summary>
    private static byte[] NarrowNames(params string[] names) => [.. names.SelectMany(name => Narrow(name + '\0'))];

    /// <summary>A 32-bit number as its four bytes, the lowest first.</summary>
    private static byte[] Number(uint value) => [(byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24)];

    /// <summary>
    /// A file list block made by hand: a DROPFILES header of 20 bytes that says where the names
    /// start and whether they are UTF-16, then whatever is to follow it.
    /// </summary>
    private static byte[] FileBlock(uint namesStart, uint wide, params byte[][] rest) =>
        [.. Number(namesStart), .. new byte[12], .. Number(wide), .. rest.SelectMany(part => part)];

    [Theory]
    [InlineData("x")]
    [InlineData("plain text")]
    [InlineData("naïve café 日本")]
    public void Text_without_surrogates_comes_back_as_the_same_instance(string text)
    {
        Assert.Same(text, ClipboardBytes.WellFormed(text));
    }

    [Theory]
    [InlineData(0xD7FF)]
    [InlineData(0xE000)]
    [InlineData(0xFFFD)]
    public void The_characters_next_to_the_surrogates_and_U_FFFD_itself_are_left_alone(int code)
    {
        string text = "a" + (char)code + "b";

        Assert.Same(text, ClipboardBytes.WellFormed(text));
    }

    [Fact]
    public void Text_with_whole_pairs_comes_back_as_the_same_instance()
    {
        string text = Emoji + " at the start, " + Emoji + Emoji + " inside and at the end " + Emoji;

        Assert.Same(text, ClipboardBytes.WellFormed(text));
    }

    [Theory]
    [InlineData(0xD83D)]
    [InlineData(0xD800)]
    [InlineData(0xDBFF)]
    public void A_high_half_with_no_low_half_after_it_becomes_U_FFFD(int high)
    {
        Assert.Equal("a" + Replacement + "b", ClipboardBytes.WellFormed("a" + (char)high + "b"));
    }

    [Theory]
    [InlineData(0xDE00)]
    [InlineData(0xDC00)]
    [InlineData(0xDFFF)]
    public void A_low_half_with_no_high_half_before_it_becomes_U_FFFD(int low)
    {
        Assert.Equal("a" + Replacement + "b", ClipboardBytes.WellFormed("a" + (char)low + "b"));
    }

    [Fact]
    public void A_low_half_before_a_high_half_is_no_pair_and_both_are_replaced()
    {
        Assert.Equal("a" + Replacement + Replacement + "b", ClipboardBytes.WellFormed("a" + Low + High + "b"));
    }

    [Theory]
    [InlineData(0xD83D)]
    [InlineData(0xDE00)]
    public void A_lone_half_at_the_very_end_is_replaced(int half)
    {
        Assert.Equal("ab" + Replacement, ClipboardBytes.WellFormed("ab" + (char)half));
    }

    [Theory]
    [InlineData(0xD83D)]
    [InlineData(0xDE00)]
    public void A_lone_half_at_the_very_start_is_replaced(int half)
    {
        Assert.Equal(Replacement + "ab", ClipboardBytes.WellFormed((char)half + "ab"));
    }

    [Theory]
    [InlineData(0xD83D)]
    [InlineData(0xDE00)]
    public void Whole_pairs_on_both_sides_of_a_lone_half_are_kept(int half)
    {
        Assert.Equal(Emoji + Replacement + Emoji, ClipboardBytes.WellFormed(Emoji + (char)half + Emoji));
    }

    [Fact]
    public void Of_two_high_halves_before_a_low_half_only_the_first_is_alone()
    {
        Assert.Equal(Replacement + Emoji, ClipboardBytes.WellFormed(High + Emoji));
    }

    [Fact]
    public void Every_lone_half_is_replaced_not_only_the_first()
    {
        Assert.Equal(
            "a" + Replacement + "b" + Replacement + "c" + Replacement,
            ClipboardBytes.WellFormed("a" + High + "b" + Low + "c" + High));
    }

    [Fact]
    public void Text_ends_at_the_first_zero_character()
    {
        // "AB", a zero character, "C", a zero character.
        Assert.Equal("AB", ClipboardBytes.Text([0x41, 0, 0x42, 0, 0, 0, 0x43, 0, 0, 0]));
    }

    [Fact]
    public void Without_a_zero_character_the_text_is_the_whole_block()
    {
        Assert.Equal("AB", ClipboardBytes.Text([0x41, 0, 0x42, 0]));
    }

    [Fact]
    public void A_last_byte_that_is_only_half_a_character_is_ignored()
    {
        Assert.Equal("AB", ClipboardBytes.Text([0x41, 0, 0x42, 0, 0x43]));
    }

    [Fact]
    public void An_empty_block_holds_no_text()
    {
        Assert.Null(ClipboardBytes.Text([]));
    }

    [Fact]
    public void A_block_of_one_byte_holds_no_text()
    {
        Assert.Null(ClipboardBytes.Text([0x41]));
    }

    [Fact]
    public void A_block_that_starts_with_a_zero_character_holds_no_text()
    {
        Assert.Null(ClipboardBytes.Text([0, 0, 0x41, 0]));
    }

    [Fact]
    public void Text_is_read_little_endian()
    {
        // 41 00 is "A" and 3A 26 is U+263A.
        Assert.Equal("A" + (char)0x263A, ClipboardBytes.Text([0x41, 0x00, 0x3A, 0x26]));
    }

    [Fact]
    public void Only_two_zero_bytes_that_are_one_character_end_the_text()
    {
        // U+0100 has a zero low byte and "A" a zero high byte, and the zero bytes of "A" and of
        // U+4200 stand side by side. None of that is a zero character.
        Assert.Equal($"{(char)0x100}A{(char)0x4200}", ClipboardBytes.Text([0x00, 0x01, 0x41, 0x00, 0x00, 0x42]));
    }

    [Fact]
    public void A_lone_half_in_a_block_is_replaced_and_a_whole_pair_is_kept()
    {
        // The pair D83D DE00, then D83D by itself, then "A".
        Assert.Equal(Emoji + Replacement + "A", ClipboardBytes.Text([0x3D, 0xD8, 0x00, 0xDE, 0x3D, 0xD8, 0x41, 0x00]));
    }

    [Fact]
    public void A_text_block_is_the_characters_little_endian_and_exactly_one_zero_character()
    {
        byte[] expected = [0x41, 0x00, 0x3A, 0x26, 0x00, 0x00];

        Assert.Equal(expected, ClipboardBytes.TextBlock("A" + (char)0x263A));
    }

    [Fact]
    public void Text_with_a_zero_character_inside_is_cut_there()
    {
        byte[] expected = [0x41, 0x00, 0x00, 0x00];

        Assert.Equal(expected, ClipboardBytes.TextBlock("A" + '\0' + "B"));
    }

    [Fact]
    public void Empty_text_gives_a_block_of_two_zero_bytes()
    {
        byte[] expected = [0, 0];

        Assert.Equal(expected, ClipboardBytes.TextBlock(""));
    }

    [Theory]
    [InlineData("x")]
    [InlineData("plain text")]
    [InlineData("two\r\nlines and a\ttab")]
    [InlineData("naïve café 日本")]
    [InlineData("  spaces all round  ")]
    public void Text_put_in_a_block_is_read_back_as_it_was(string text)
    {
        Assert.Equal(text, ClipboardBytes.Text(ClipboardBytes.TextBlock(text)));
    }

    [Fact]
    public void An_emoji_put_in_a_block_is_read_back_whole()
    {
        string text = "smile " + Emoji + " and on";

        Assert.Equal(text, ClipboardBytes.Text(ClipboardBytes.TextBlock(text)));
    }

    [Fact]
    public void Empty_text_put_in_a_block_is_read_back_as_no_text()
    {
        Assert.Null(ClipboardBytes.Text(ClipboardBytes.TextBlock("")));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x3C, 0x62, 0x3E })]
    public void Bytes_without_a_zero_byte_come_back_as_the_same_array(byte[] block)
    {
        Assert.Same(block, ClipboardBytes.UpToZero(block));
    }

    [Fact]
    public void The_first_zero_byte_and_everything_after_it_are_dropped()
    {
        byte[] expected = [0x3C, 0x62];

        Assert.Equal(expected, ClipboardBytes.UpToZero([0x3C, 0x62, 0, 0x3E, 0, 0x21]));
    }

    [Fact]
    public void A_block_that_starts_with_a_zero_byte_gives_an_empty_array_not_null()
    {
        byte[] bytes = ClipboardBytes.UpToZero([0, 0x3C, 0x62]);

        Assert.NotNull(bytes);
        Assert.Empty(bytes);
    }

    [Fact]
    public void WithZero_makes_a_new_array_one_longer_that_ends_in_a_zero_byte()
    {
        byte[] bytes = [0x3C, 0x62, 0x3E];
        byte[] expected = [0x3C, 0x62, 0x3E, 0];

        byte[] block = ClipboardBytes.WithZero(bytes);

        Assert.NotSame(bytes, block);
        Assert.Equal(expected, block);
    }

    [Fact]
    public void WithZero_of_no_bytes_is_one_zero_byte()
    {
        byte[] expected = [0];

        Assert.Equal(expected, ClipboardBytes.WithZero([]));
    }

    [Fact]
    public void UpToZero_gives_back_the_bytes_that_WithZero_was_given()
    {
        byte[] bytes = [0x3C, 0x62, 0x3E];

        Assert.Equal(bytes, ClipboardBytes.UpToZero(ClipboardBytes.WithZero(bytes)));
    }

    [Fact]
    public void A_marker_that_is_not_there_does_not_say_no()
    {
        Assert.False(ClipboardBytes.SaysNo(null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_marker_shorter_than_four_bytes_says_no_whatever_it_holds(int length)
    {
        byte[] marker = [.. Enumerable.Repeat((byte)1, length)];

        Assert.True(ClipboardBytes.SaysNo(marker));
    }

    [Fact]
    public void A_marker_of_four_zero_bytes_says_no()
    {
        Assert.True(ClipboardBytes.SaysNo([0, 0, 0, 0]));
    }

    [Fact]
    public void A_marker_that_holds_exactly_the_number_1_does_not_say_no()
    {
        Assert.False(ClipboardBytes.SaysNo([1, 0, 0, 0]));
    }

    // Windows defines 0 (do not) and 1 (do) and no other number, so any other number is in
    // doubt, and in doubt the copy is left alone.
    [Theory]
    [InlineData(new byte[] { 2, 0, 0, 0 })]
    [InlineData(new byte[] { 0x30, 0, 0, 0 })]
    [InlineData(new byte[] { 0, 1, 0, 0 })]
    [InlineData(new byte[] { 0, 0, 1, 0 })]
    [InlineData(new byte[] { 0, 0, 0, 1 })]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF })]
    [InlineData(new byte[] { 1, 1, 1, 1 })]
    [InlineData(new byte[] { 1, 1, 0, 0 })]
    public void A_marker_that_holds_any_number_but_1_says_no(byte[] marker)
    {
        Assert.True(ClipboardBytes.SaysNo(marker));
    }

    [Fact]
    public void A_marker_with_no_bytes_says_no()
    {
        Assert.True(ClipboardBytes.SaysNo([]));
    }

    [Fact]
    public void The_text_0_in_UTF_16_is_not_the_number_1_and_says_no()
    {
        Assert.True(ClipboardBytes.SaysNo(Wide("0")));
        Assert.True(ClipboardBytes.SaysNo(Wide("0\0")));
    }

    [Fact]
    public void Of_a_longer_marker_only_the_first_four_bytes_count()
    {
        Assert.True(ClipboardBytes.SaysNo([0, 0, 0, 0, 1, 1, 1, 1]));
        Assert.True(ClipboardBytes.SaysNo([2, 0, 0, 0, 1, 0, 0, 0]));
        Assert.True(ClipboardBytes.SaysNo([0, 0, 0, 1, 0]));
        Assert.False(ClipboardBytes.SaysNo([1, 0, 0, 0, 0, 0, 0, 0]));
        Assert.False(ClipboardBytes.SaysNo([1, 0, 0, 0, 9, 9, 9, 9]));
        Assert.False(ClipboardBytes.SaysNo([1, 0, 0, 0, 0]));
    }

    [Fact]
    public void The_copy_effect_is_the_number_1_in_four_bytes_little_endian()
    {
        byte[] expected = [1, 0, 0, 0];

        Assert.Equal(expected, ClipboardBytes.CopyEffect());
    }

    [Fact]
    public void A_wide_block_made_by_hand_gives_its_paths()
    {
        byte[] block =
        [
            20, 0, 0, 0,                                    // the names start at byte 20
            0, 0, 0, 0, 0, 0, 0, 0,                         // a point, which is not used
            0, 0, 0, 0,                                     // a flag, which is not used
            1, 0, 0, 0,                                     // the names are UTF-16
            0x43, 0, 0x3A, 0, 0x5C, 0, 0x61, 0, 0, 0,       // C:\a
            0x44, 0, 0x3A, 0, 0x5C, 0, 0x3A, 0x26, 0, 0,    // D:\ and U+263A
            0, 0,                                           // the end of the list
        ];

        Assert.Equal([@"C:\a", @"D:\" + (char)0x263A], ClipboardBytes.FileList(block));
    }

    [Fact]
    public void A_block_of_single_bytes_made_by_hand_gives_its_paths()
    {
        byte[] block =
        [
            20, 0, 0, 0,                                    // the names start at byte 20
            0, 0, 0, 0, 0, 0, 0, 0,                         // a point, which is not used
            0, 0, 0, 0,                                     // a flag, which is not used
            0, 0, 0, 0,                                     // the names are single bytes
            0x43, 0x3A, 0x5C, 0x61, 0,                      // C:\a
            0x44, 0x3A, 0x5C, 0x62, 0x20, 0x63, 0,          // D:\b c
            0,                                              // the end of the list
        ];

        Assert.Equal([@"C:\a", @"D:\b c"], ClipboardBytes.FileList(block));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(0x100u)]
    [InlineData(0x01000000u)]
    [InlineData(uint.MaxValue)]
    public void Any_number_but_0_at_byte_16_means_the_names_are_UTF_16(uint wide)
    {
        byte[] block = FileBlock(20, wide, WideNames(@"C:\one.txt", @"D:\two.txt"), WideEnd);

        Assert.Equal([@"C:\one.txt", @"D:\two.txt"], ClipboardBytes.FileList(block));
    }

    [Fact]
    public void The_point_and_the_flag_in_the_header_are_not_read()
    {
        byte[] block = FileBlock(20, 1, WideNames(@"C:\one.txt"), WideEnd);
        block.AsSpan(4, 12).Fill(0xFF);

        Assert.Equal([@"C:\one.txt"], ClipboardBytes.FileList(block));
    }

    [Fact]
    public void The_names_start_where_the_first_number_says_not_right_after_the_header()
    {
        // Eight bytes that read as the start of a path stand between the header and the names.
        byte[] block = FileBlock(28, 1, Wide(@"Z:\q"), WideNames(@"C:\one.txt"), WideEnd);

        Assert.Equal([@"C:\one.txt"], ClipboardBytes.FileList(block));
    }

    [Theory]
    [InlineData((byte)0x80)]
    [InlineData((byte)0xE9)]
    [InlineData((byte)0xFF)]
    public void A_list_of_single_bytes_with_a_byte_outside_ASCII_is_not_read_at_all(byte outside)
    {
        // The first name is a full path in plain ASCII, and it is not given either.
        byte[] block = FileBlock(20, 0, NarrowNames(@"C:\one.txt"), Narrow(@"C:\caf"), [outside], NarrowNames(".txt"), NarrowEnd);

        Assert.Null(ClipboardBytes.FileList(block));
    }

    [Fact]
    public void The_last_byte_that_is_ASCII_is_still_read()
    {
        byte[] block = FileBlock(20, 0, Narrow(@"C:\a"), [0x7F], NarrowNames(".txt"), NarrowEnd);

        Assert.Equal([@"C:\a" + (char)0x7F + ".txt"], ClipboardBytes.FileList(block));
    }

    [Fact]
    public void A_byte_outside_ASCII_after_the_end_of_a_list_of_single_bytes_is_not_part_of_the_list()
    {
        byte[] block = FileBlock(20, 0, NarrowNames(@"C:\one.txt"), NarrowEnd, [0xE9, 0xFF]);

        Assert.Equal([@"C:\one.txt"], ClipboardBytes.FileList(block));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(19)]
    public void A_block_shorter_than_its_header_is_not_a_file_list(int length)
    {
        byte[] block = FileBlock(20, 1)[..length];

        Assert.Null(ClipboardBytes.FileList(block));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(19u)]
    [InlineData((uint)int.MaxValue)]
    [InlineData(0x80000000u)]
    [InlineData(uint.MaxValue)]
    public void Names_that_would_start_inside_the_header_or_far_beyond_the_block_give_null(uint namesStart)
    {
        byte[] block = FileBlock(namesStart, 1, WideNames(@"C:\one.txt"), WideEnd);

        Assert.Null(ClipboardBytes.FileList(block));
    }

    [Fact]
    public void Names_that_would_start_one_byte_beyond_the_block_give_null()
    {
        byte[] names = [.. WideNames(@"C:\one.txt"), .. WideEnd];
        byte[] block = FileBlock((uint)(20 + names.Length + 1), 1, names);

        Assert.Null(ClipboardBytes.FileList(block));
    }

    [Fact]
    public void Names_that_start_exactly_at_the_end_of_the_block_give_no_paths()
    {
        byte[] names = [.. WideNames(@"C:\one.txt"), .. WideEnd];
        byte[] block = FileBlock((uint)(20 + names.Length), 1, names);

        var paths = ClipboardBytes.FileList(block);

        Assert.NotNull(paths);
        Assert.Empty(paths);
    }

    [Fact]
    public void A_header_with_nothing_after_it_gives_no_paths()
    {
        var paths = ClipboardBytes.FileList(FileBlock(20, 1));

        Assert.NotNull(paths);
        Assert.Empty(paths);
    }

    [Theory]
    [InlineData("two.txt")]
    [InlineData(@"folder\two.txt")]
    [InlineData(@"..\two.txt")]
    [InlineData(@"C:two.txt")]
    [InlineData(@"\two.txt")]
    public void A_name_that_is_not_a_full_path_is_left_out_and_the_full_paths_around_it_are_kept(string name)
    {
        byte[] block = FileBlock(20, 1, WideNames(@"C:\one.txt", name, @"D:\three.txt"), WideEnd);

        Assert.Equal([@"C:\one.txt", @"D:\three.txt"], ClipboardBytes.FileList(block));
    }

    [Theory]
    [InlineData(0xD83D)]
    [InlineData(0xDE00)]
    public void A_name_with_half_a_surrogate_pair_in_it_is_left_out(int half)
    {
        byte[] block = FileBlock(20, 1, WideNames(@"C:\one.txt", @"C:\half" + (char)half + ".txt", @"D:\three.txt"), WideEnd);

        Assert.Equal([@"C:\one.txt", @"D:\three.txt"], ClipboardBytes.FileList(block));
    }

    [Fact]
    public void A_last_name_with_no_zero_character_after_it_is_left_out()
    {
        byte[] block = FileBlock(20, 1, WideNames(@"C:\one.txt"), Wide(@"D:\two.txt"));

        Assert.Equal([@"C:\one.txt"], ClipboardBytes.FileList(block));
    }

    [Fact]
    public void A_last_name_of_single_bytes_with_no_zero_byte_after_it_is_left_out()
    {
        byte[] block = FileBlock(20, 0, NarrowNames(@"C:\one.txt"), Narrow(@"D:\two.txt"));

        Assert.Equal([@"C:\one.txt"], ClipboardBytes.FileList(block));
    }

    [Fact]
    public void A_last_byte_that_is_only_half_a_character_does_not_end_a_name()
    {
        // The one zero byte at the end is not a zero character, so the second name was cut short.
        byte[] block = FileBlock(20, 1, WideNames(@"C:\one.txt"), Wide(@"D:\two.txt"), [0]);

        Assert.Equal([@"C:\one.txt"], ClipboardBytes.FileList(block));
    }

    [Fact]
    public void A_list_without_the_last_zero_character_still_gives_every_name_that_is_ended()
    {
        byte[] block = FileBlock(20, 1, WideNames(@"C:\one.txt", @"D:\two.txt"));

        Assert.Equal([@"C:\one.txt", @"D:\two.txt"], ClipboardBytes.FileList(block));
    }

    [Fact]
    public void What_follows_the_zero_character_that_ends_the_list_is_not_read()
    {
        byte[] block = FileBlock(20, 1, WideNames(@"C:\one.txt"), WideEnd, WideNames(@"D:\two.txt"), WideEnd);

        Assert.Equal([@"C:\one.txt"], ClipboardBytes.FileList(block));
    }

    [Fact]
    public void A_list_written_by_DropFiles_is_read_back_by_FileList()
    {
        string[] paths =
        [
            @"C:\Users\Sam\My Documents\report (final).txt",
            @"D:\Größe\naïve café.txt",
            @"C:\Pictures\holiday " + Emoji + ".png",
            @"\\server\share\Team Folder\notes.md",
        ];

        Assert.Equal(paths, ClipboardBytes.FileList(ClipboardBytes.DropFiles(paths)));
    }

    [Fact]
    public void DropFiles_writes_the_header_then_each_path_with_a_zero_character_then_one_more()
    {
        byte[] expected =
        [
            20, 0, 0, 0,                                    // the names start right after the header
            0, 0, 0, 0, 0, 0, 0, 0,                         // a point, left at zero
            0, 0, 0, 0,                                     // a flag, left at zero
            1, 0, 0, 0,                                     // the names are UTF-16
            0x43, 0, 0x3A, 0, 0x5C, 0, 0x61, 0, 0, 0,       // C:\a
            0x44, 0, 0x3A, 0, 0x5C, 0, 0x3A, 0x26, 0, 0,    // D:\ and U+263A
            0, 0,                                           // the end of the list
        ];

        Assert.Equal(expected, ClipboardBytes.DropFiles([@"C:\a", @"D:\" + (char)0x263A]));
    }

    [Fact]
    public void DropFiles_of_no_paths_is_the_header_and_the_zero_character_that_ends_the_list()
    {
        byte[] expected =
        [
            20, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0,
            1, 0, 0, 0,
            0, 0,
        ];

        Assert.Equal(expected, ClipboardBytes.DropFiles([]));
    }

    [Fact]
    public void A_block_of_no_paths_is_read_back_as_no_paths_not_as_null()
    {
        var paths = ClipboardBytes.FileList(ClipboardBytes.DropFiles([]));

        Assert.NotNull(paths);
        Assert.Empty(paths);
    }

    [Fact]
    public void DropFiles_leaves_out_an_empty_path()
    {
        Assert.Equal(
            ClipboardBytes.DropFiles([@"C:\one.txt", @"D:\three.txt"]),
            ClipboardBytes.DropFiles([@"C:\one.txt", "", @"D:\three.txt"]));
    }

    [Fact]
    public void DropFiles_leaves_out_a_path_with_a_zero_character_in_it()
    {
        // Written as it is, it would be read back as "C:\two", the path of another file.
        Assert.Equal(
            ClipboardBytes.DropFiles([@"C:\one.txt", @"D:\three.txt"]),
            ClipboardBytes.DropFiles([@"C:\one.txt", @"C:\two" + '\0' + ".txt", @"D:\three.txt"]));
    }

    [Fact]
    public void A_path_that_is_not_full_is_written_by_DropFiles_and_only_left_out_when_the_list_is_read()
    {
        byte[] block = ClipboardBytes.DropFiles([@"C:\one.txt", "two.txt", @"D:\three.txt"]);

        Assert.Equal(FileBlock(20, 1, WideNames(@"C:\one.txt", "two.txt", @"D:\three.txt"), WideEnd), block);
        Assert.Equal([@"C:\one.txt", @"D:\three.txt"], ClipboardBytes.FileList(block));
    }

    // A list is read only as far as it is of use: one name more than the limit is taken, so
    // that whoever reads the list can see that it was over the limit.
    [Theory]
    [InlineData(ClipLimits.MaxFiles - 1, ClipLimits.MaxFiles - 1)]
    [InlineData(ClipLimits.MaxFiles, ClipLimits.MaxFiles)]
    [InlineData(ClipLimits.MaxFiles + 1, ClipLimits.MaxFiles + 1)]
    [InlineData(ClipLimits.MaxFiles + 2, ClipLimits.MaxFiles + 1)]
    [InlineData(5 * ClipLimits.MaxFiles, ClipLimits.MaxFiles + 1)]
    public void A_list_is_read_to_one_name_more_than_the_limit_and_no_further(int names, int expected)
    {
        string[] all = [.. Enumerable.Range(0, names).Select(i => $@"C:\folder\file{i}.txt")];

        var paths = ClipboardBytes.FileList(FileBlock(20, 1, WideNames(all), WideEnd));

        Assert.NotNull(paths);
        Assert.Equal(expected, paths.Count);
        Assert.Equal(all[..expected], paths);
    }

    [Fact]
    public void A_list_of_single_bytes_is_read_to_one_name_more_than_the_limit_too()
    {
        string[] all = [.. Enumerable.Range(0, 3 * ClipLimits.MaxFiles).Select(i => $@"C:\folder\file{i}.txt")];

        var paths = ClipboardBytes.FileList(FileBlock(20, 0, NarrowNames(all), NarrowEnd));

        Assert.NotNull(paths);
        Assert.Equal(all[..(ClipLimits.MaxFiles + 1)], paths);
    }

    [Fact]
    public void Names_that_are_left_out_do_not_count_towards_the_limit()
    {
        string[] mixed = [.. Enumerable.Range(0, 3 * ClipLimits.MaxFiles).Select(i => i % 2 == 0 ? $@"C:\folder\file{i}.txt" : $"file{i}.txt")];

        var paths = ClipboardBytes.FileList(FileBlock(20, 1, WideNames(mixed), WideEnd));

        Assert.NotNull(paths);
        Assert.Equal(ClipLimits.MaxFiles + 1, paths.Count);
        Assert.All(paths, path => Assert.StartsWith(@"C:\folder\", path));
    }

    [Fact]
    public void A_block_of_the_largest_size_that_is_read_and_full_of_names_gives_one_name_more_than_the_limit()
    {
        // 700000 names of 5 bytes each: about 3.5 MB, under the largest file list block read.
        string[] names = new string[700_000];
        Array.Fill(names, @"C:\a");
        byte[] block = FileBlock(20, 0, NarrowNames(names), NarrowEnd);
        Assert.True(block.Length <= ClipLimits.MaxFileListBytes);

        var paths = ClipboardBytes.FileList(block);

        Assert.NotNull(paths);
        Assert.Equal(ClipLimits.MaxFiles + 1, paths.Count);
    }

    // The longest path Windows has is 32767 characters.
    [Fact]
    public void A_name_of_exactly_32767_characters_is_kept_and_one_character_longer_is_left_out()
    {
        string longest = @"C:\" + new string('a', 32767 - 3);
        byte[] block = FileBlock(20, 1, WideNames(@"C:\one.txt", longest + "a", longest, @"D:\three.txt"), WideEnd);

        Assert.Equal([@"C:\one.txt", longest, @"D:\three.txt"], ClipboardBytes.FileList(block));
    }

    [Fact]
    public void A_name_of_single_bytes_of_exactly_32767_characters_is_kept_and_one_longer_is_left_out()
    {
        string longest = @"C:\" + new string('a', 32767 - 3);
        byte[] block = FileBlock(20, 0, NarrowNames(@"C:\one.txt", longest + "a", longest, @"D:\three.txt"), NarrowEnd);

        Assert.Equal([@"C:\one.txt", longest, @"D:\three.txt"], ClipboardBytes.FileList(block));
    }

    [Fact]
    public void One_name_that_takes_up_the_whole_block_is_left_out_and_not_given_as_a_path()
    {
        // About 2 million characters of UTF-16 in 4 MB, as the largest block read allows.
        string huge = @"C:\" + new string('a', 2_000_000);
        byte[] block = FileBlock(20, 1, WideNames(huge), WideEnd);

        var paths = ClipboardBytes.FileList(block);

        Assert.NotNull(paths);
        Assert.Empty(paths);
    }

    [Fact]
    public void A_name_that_is_too_long_and_is_cut_off_is_left_out_with_no_zero_character_after_it()
    {
        byte[] block = FileBlock(20, 1, WideNames(@"C:\one.txt"), Wide(@"C:\" + new string('a', 40_000)));

        Assert.Equal([@"C:\one.txt"], ClipboardBytes.FileList(block));
    }

    [Theory]
    [InlineData(@"C:\x.png")]
    [InlineData(@"c:\x.png")]
    [InlineData(@"Z:\")]
    [InlineData("C:/x.png")]
    [InlineData(@"\\server\share\x.png")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\10.0.0.5\share\folder\x.png")]
    [InlineData(@"\\server.example.org\share$\x.png")]
    [InlineData(@"\\server\share\a.png")]
    [InlineData(@"//server/share/a.png")]
    [InlineData(@"\\server/share\a.png")]
    [InlineData(@"/\server\share\a.png")]
    public void A_drive_path_and_a_share_path_are_listed(string name)
    {
        byte[] block = FileBlock(20, 1, WideNames(name), WideEnd);

        Assert.Equal([name], ClipboardBytes.FileList(block));
    }

    [Theory]
    [InlineData(@"\\.\pipe\x.png")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\?\GLOBALROOT\Device\x.png")]
    [InlineData(@"\\?\C:\x.png")]
    [InlineData(@"\\?\UNC\server\share\x.png")]
    [InlineData(@"\\.")]
    [InlineData(@"\\?")]
    [InlineData(@"//./pipe/x.png")]
    [InlineData(@"//?/C:/x.png")]
    [InlineData(@"\\./pipe\made-up.png")]
    [InlineData(@"\\?/C:\made-up\a.png")]
    [InlineData(@"\\?/GLOBALROOT\Device\made-up.png")]
    [InlineData(@"\\./PhysicalDrive0\made-up.png")]
    [InlineData(@"\/.\pipe\x.png")]
    [InlineData(@"/\?\C:\x.png")]
    [InlineData(@"//?/UNC/server/share/x.png")]
    [InlineData(@"\\.//pipe\x.png")]
    [InlineData(@"\\server")]
    [InlineData(@"\\server\")]
    [InlineData(@"\\server/")]
    [InlineData(@"\\server\/x.png")]
    [InlineData(@"//server//x.png")]
    [InlineData(@"\\\share\x.png")]
    [InlineData(@"///share/x.png")]
    [InlineData(@"1:\x.png")]
    [InlineData(@"C:")]
    [InlineData(@"C:x.png")]
    [InlineData(@"CC:\x.png")]
    [InlineData(@"C|\x.png")]
    [InlineData(@":\x.png")]
    public void A_device_path_an_extended_path_and_any_other_form_is_left_out_and_the_paths_around_it_are_kept(string name)
    {
        byte[] block = FileBlock(20, 1, WideNames(@"C:\one.txt", name, @"D:\three.txt"), WideEnd);

        Assert.Equal([@"C:\one.txt", @"D:\three.txt"], ClipboardBytes.FileList(block));
    }

    [Theory]
    [InlineData(@"\\.\pipe\x.png")]
    [InlineData(@"\\?\GLOBALROOT\Device\x.png")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\./pipe\made-up.png")]
    [InlineData(@"\\?/GLOBALROOT\Device\made-up.png")]
    public void A_device_path_in_a_list_of_single_bytes_is_left_out_too(string name)
    {
        byte[] block = FileBlock(20, 0, NarrowNames(@"C:\one.txt", name, @"D:\three.txt"), NarrowEnd);

        Assert.Equal([@"C:\one.txt", @"D:\three.txt"], ClipboardBytes.FileList(block));
    }

    // Windows serves a share path whose server name holds an "@" over HTTP, through the web
    // client service: "\\host@SSL\DavWWWRoot\x.png" is a request to host over TLS, and
    // "\\host@8080\share\x.png" one to port 8080. Opening or asking about one is a connection
    // that Wyvern must never make, so it is not an ordinary path. Nothing here touches a file
    // API: the paths are text, read from a block that was built by hand.
    [Theory]
    [InlineData(@"\\host@SSL\DavWWWRoot\x.png")]
    [InlineData(@"\\host@8080\share\x.png")]
    [InlineData(@"\\host@SSL@8080\DavWWWRoot\folder\x.png")]
    [InlineData(@"\\host.example.org@SSL\DavWWWRoot\x.png")]
    [InlineData(@"\\10.0.0.5@8080\share\x.png")]
    [InlineData("//host@SSL/DavWWWRoot/x.png")]
    [InlineData(@"\\host@SSL/DavWWWRoot\x.png")]
    [InlineData(@"/\host@SSL\DavWWWRoot\x.png")]
    [InlineData(@"\\@\share\x.png")]
    [InlineData(@"\\host@\share\x.png")]
    [InlineData(@"\\@host\share\x.png")]
    [InlineData(@"\\host@SSL\share")]
    public void A_share_whose_server_name_holds_an_at_sign_is_left_out_and_the_paths_around_it_are_kept(string name)
    {
        byte[] block = FileBlock(20, 1, WideNames(@"C:\one.txt", name, @"D:\three.txt"), WideEnd);

        Assert.Equal([@"C:\one.txt", @"D:\three.txt"], ClipboardBytes.FileList(block));
    }

    [Theory]
    [InlineData(@"\\host@SSL\DavWWWRoot\x.png")]
    [InlineData(@"\\host@8080\share\x.png")]
    [InlineData("//host@SSL/DavWWWRoot/x.png")]
    public void A_share_whose_server_name_holds_an_at_sign_is_left_out_of_a_list_of_single_bytes_too(string name)
    {
        byte[] block = FileBlock(20, 0, NarrowNames(@"C:\one.txt", name, @"D:\three.txt"), NarrowEnd);

        Assert.Equal([@"C:\one.txt", @"D:\three.txt"], ClipboardBytes.FileList(block));
    }

    // A share named "DavWWWRoot" is the name that makes Windows' web client serve a path over
    // HTTP, with no "@" in the server name. It is left out for the same reason, in any case,
    // with either separator, and with the dots and spaces that Windows drops at the end of a
    // name. The paths are text, read from a block that was built by hand.
    [Theory]
    [InlineData(@"\\host\DavWWWRoot\x.png")]
    [InlineData("//host/davwwwroot/x.png")]
    [InlineData(@"\\host\DAVWWWROOT\x.png")]
    [InlineData(@"\\host\DavWWWRoot.\x.png")]
    [InlineData(@"\\host\DavWWWRoot \x.png")]
    [InlineData(@"\\host\DavWWWRoot. .\x.png")]
    [InlineData(@"\\host\DavWWWRoot")]
    [InlineData(@"\\host/DavWWWRoot\x.png")]
    [InlineData(@"//host\davwwwroot/folder/x.png")]
    [InlineData(@"/\10.0.0.5\DavWWWRoot\x.png")]
    public void A_share_named_DavWWWRoot_is_left_out_and_the_paths_around_it_are_kept(string name)
    {
        byte[] block = FileBlock(20, 1, WideNames(@"C:\one.txt", name, @"D:\three.txt"), WideEnd);

        Assert.Equal([@"C:\one.txt", @"D:\three.txt"], ClipboardBytes.FileList(block));
    }

    [Theory]
    [InlineData(@"\\host\DavWWWRoot\x.png")]
    [InlineData("//host/davwwwroot/x.png")]
    [InlineData(@"\\host\DavWWWRoot.\x.png")]
    public void A_share_named_DavWWWRoot_is_left_out_of_a_list_of_single_bytes_too(string name)
    {
        byte[] block = FileBlock(20, 0, NarrowNames(@"C:\one.txt", name, @"D:\three.txt"), NarrowEnd);

        Assert.Equal([@"C:\one.txt", @"D:\three.txt"], ClipboardBytes.FileList(block));
    }

    // Only the share is looked at, and the whole of its name. A longer or shorter name, a folder
    // or a file of that name, and a drive path are ordinary.
    [Theory]
    [InlineData(@"\\host\DavWWWRootX\x.png")]
    [InlineData(@"\\host\XDavWWWRoot\x.png")]
    [InlineData(@"\\host\DavWWW\x.png")]
    [InlineData(@"\\host\Dav WWWRoot\x.png")]
    [InlineData(@"\\host\share\DavWWWRoot\x.png")]
    [InlineData(@"\\host\share\DavWWWRoot")]
    [InlineData(@"\\DavWWWRoot\share\x.png")]
    [InlineData(@"\\host\share\x.png")]
    [InlineData(@"C:\DavWWWRoot\x.png")]
    [InlineData(@"C:\folder\DavWWWRoot")]
    public void A_name_that_only_looks_like_the_DavWWWRoot_share_is_an_ordinary_path(string name)
    {
        byte[] block = FileBlock(20, 1, WideNames(name), WideEnd);

        Assert.Equal([name], ClipboardBytes.FileList(block));
    }

    // Only the server name is looked at. An "@" in the share, in a folder or in a file name is
    // an ordinary character of an ordinary path.
    [Theory]
    [InlineData(@"\\server\share@SSL\x.png")]
    [InlineData(@"\\server\share\folder@8080\x.png")]
    [InlineData(@"\\server\share\x@y.png")]
    [InlineData(@"\\server\@share\x.png")]
    [InlineData(@"C:\folder@SSL\x.png")]
    [InlineData(@"C:\x@y.png")]
    [InlineData(@"C:/folder@8080/x.png")]
    public void An_at_sign_anywhere_but_in_the_server_name_is_no_reason_to_leave_a_path_out(string name)
    {
        byte[] block = FileBlock(20, 1, WideNames(name), WideEnd);

        Assert.Equal([name], ClipboardBytes.FileList(block));
    }

    // The names that stand for a pipe or a device, decided by the text alone. The paths are
    // made up and are never handed to a file API: nothing here opens or asks about anything.
    [Theory]
    [InlineData(@"\\server\pipe\x.png")]
    [InlineData(@"\\SERVER\PIPE\x.png")]
    [InlineData(@"\\server\Pipe\folder\x.jpg")]
    [InlineData(@"\\server\mailslot\x.png")]
    [InlineData(@"\\server\MailSlot\x.gif")]
    [InlineData("//server/pipe/x.png")]
    [InlineData(@"\\server/pipe\x.png")]
    [InlineData(@"C:\made-up\COM3.png")]
    [InlineData(@"C:\made-up\com1.jpg")]
    [InlineData(@"C:\made-up\COM9.bmp")]
    [InlineData(@"C:\made-up\LPT1.png")]
    [InlineData(@"C:\made-up\lpt9.gif")]
    [InlineData(@"C:\made-up\CON.png")]
    [InlineData(@"C:\made-up\Con.jpeg")]
    [InlineData(@"C:\made-up\PRN.png")]
    [InlineData(@"C:\made-up\AUX.png")]
    [InlineData(@"C:\made-up\NUL.png")]
    [InlineData(@"C:\made-up\nul.PNG")]
    [InlineData(@"C:\made-up\CONIN$.png")]
    [InlineData(@"C:\made-up\CONOUT$.png")]
    [InlineData(@"C:\made-up\conout$.jpg")]
    [InlineData(@"C:\made-up\COM3.tar.png")]
    [InlineData("C:\\made-up\\COM\u00B9.png")]
    [InlineData("C:\\made-up\\COM\u00B2.png")]
    [InlineData("C:\\made-up\\COM\u00B3.jpg")]
    [InlineData("C:\\made-up\\LPT\u00B9.png")]
    [InlineData("C:\\made-up\\lpt\u00B2.gif")]
    [InlineData("C:\\made-up\\LPT\u00B3 .bmp")]
    [InlineData(@"\\server\share\LPT2.png")]
    [InlineData(@"\\server\pipe.\x.png")]
    [InlineData(@"\\server\pipe \x.png")]
    [InlineData(@"\\server\pipe. .\x.png")]
    [InlineData(@"\\server\Pipe...\x.jpg")]
    [InlineData(@"\\server\mailslot..\x.png")]
    [InlineData(@"\\server\MailSlot. \x.gif")]
    [InlineData(@"\\server\IPC$\x.png")]
    [InlineData(@"\\server\ipc$\x.jpg")]
    [InlineData(@"\\server\Ipc$.\x.png")]
    [InlineData(@"\\server\IPC$ \x.png")]
    [InlineData("//server/PIPE./x.png")]
    [InlineData("//server/ipc$ ./x.png")]
    [InlineData(@"C:\made-up\COM3 .png")]
    [InlineData(@"C:\made-up\CON .jpg")]
    [InlineData(@"C:\made-up\NUL  .png")]
    [InlineData(@"C:\made-up\lpt1 .gif")]
    [InlineData(@"C:\made-up\CONIN$ .png")]
    [InlineData(@"C:\made-up\NUL.txt")]
    [InlineData(@"C:\made-up\COM1.docx")]
    [InlineData(@"C:\made-up\aux")]
    [InlineData(@"C:\made-up\nul:stream")]
    public void A_name_that_stands_for_a_pipe_or_a_device_is_told_so(string path)
    {
        Assert.True(ClipboardBytes.StandsForPipeOrDevice(path));
    }

    [Theory]
    [InlineData(@"C:\made-up\COM10.png")]
    [InlineData(@"C:\made-up\COM0.png")]
    [InlineData(@"C:\made-up\console.png")]
    [InlineData(@"C:\made-up\my-com3.png")]
    [InlineData(@"C:\made-up\aux-plug.jpg")]
    [InlineData(@"C:\made-up\pipe\x.png")]
    [InlineData(@"C:\made-up\mailslot.png")]
    [InlineData(@"\\server\pipes\x.png")]
    [InlineData(@"\\server\share\pipe\x.png")]
    [InlineData(@"\\pipe\share\x.png")]
    [InlineData(@"\\server\ipc\x.png")]
    [InlineData(@"\\server\ipc$x\x.png")]
    [InlineData(@"\\server\pipeline\x.png")]
    [InlineData(@"\\server\pipes.\x.png")]
    [InlineData(@"\\server\x-ipc$\x.png")]
    [InlineData(@"C:\made-up\IPC$\x.png")]
    [InlineData(@"C:\made-up\COM3x .png")]
    [InlineData(@"C:\made-up\report.docx")]
    [InlineData(@"C:\one.txt")]
    [InlineData(@"\\server\share\x.png")]
    [InlineData(@"D:\")]
    [InlineData("")]
    public void A_name_that_only_looks_like_one_is_not_told_to_be_one(string path)
    {
        Assert.False(ClipboardBytes.StandsForPipeOrDevice(path));
    }

    [Fact]
    public void A_letter_outside_ASCII_before_the_colon_is_not_a_drive()
    {
        // U+00E9, a letter, followed by a colon and a back slash.
        byte[] block = FileBlock(20, 1, WideNames(@"C:\one.txt", (char)0xE9 + @":\x.png"), WideEnd);

        Assert.Equal([@"C:\one.txt"], ClipboardBytes.FileList(block));
    }
}
