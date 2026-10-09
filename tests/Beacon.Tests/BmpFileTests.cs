using System.Buffers.Binary;
using Beacon.Core.ClipboardHistory;

namespace Beacon.Tests;

/// <summary>
/// The pictures here are made up: a header with the numbers a test needs and bytes that only
/// stand for pixels. None of them is ever decoded.
/// </summary>
public class BmpFileTests
{
    // The file header of 14 bytes and a header of 40 bytes: where the pixels start when nothing
    // else stands before them.
    private const int Plain = 54;

    /// <summary>
    /// A picture as the app reads it from the clipboard: 14 zero bytes, then a header, then
    /// bytes that stand for whatever follows it, <paramref name="length"/> bytes in all. The
    /// header is a BITMAPINFOHEADER for 2 by 2 pixels of 24 bits unless a test says otherwise.
    /// The rows of 2 pixels take 16 bytes in all at 24 or 32 bits and 8 bytes at 1, 4, 8 or 16
    /// bits, and the default length leaves exactly 16 after the header.
    /// </summary>
    private static byte[] Picture(
        int length = Plain + 16, uint headerSize = 40, int bitCount = 24, uint compression = 0, uint coloursUsed = 0, int width = 2, int height = 2)
    {
        byte[] file = new byte[length];

        // Nothing after the 14 bytes is left at zero by chance, so that a change there is seen.
        file.AsSpan(14).Fill(0x55);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(14), headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(22), height);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(28), (ushort)bitCount);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(30), compression);
        file.AsSpan(34, 12).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(46), coloursUsed);
        file.AsSpan(50, 4).Clear();
        return file;
    }

    /// <summary>Asserts that the picture is accepted and that its file header says the pixels start here.</summary>
    private static void AssertPixelsAt(int expected, byte[] file)
    {
        Assert.True(BmpFile.TryAddFileHeader(file));
        Assert.Equal((uint)expected, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(10)));
    }

    /// <summary>Asserts that the picture is refused and that not one byte of the array was changed.</summary>
    private static void AssertRefused(byte[] file)
    {
        byte[] before = [.. file];

        Assert.False(BmpFile.TryAddFileHeader(file));
        Assert.Equal(before, file);
    }

    /// <summary>Pixels that are all different, so that a row or a byte in the wrong place is seen.</summary>
    private static byte[] Pixels(int count) => [.. Enumerable.Range(1, count).Select(i => (byte)i)];

    [Fact]
    public void The_file_header_is_14_bytes_long()
    {
        Assert.Equal(14, BmpFile.FileHeaderSize);
    }

    [Fact]
    public void A_24_bit_picture_gets_BM_its_whole_length_zeros_and_the_offset_54()
    {
        byte[] file = Picture(length: 70);
        byte[] expected = [0x42, 0x4D, 70, 0, 0, 0, 0, 0, 0, 0, 54, 0, 0, 0];

        Assert.True(BmpFile.TryAddFileHeader(file));

        Assert.Equal(expected, file[..14]);
    }

    [Fact]
    public void The_14_bytes_are_overwritten_whatever_they_held()
    {
        byte[] file = Picture(length: 70);
        file.AsSpan(0, 14).Fill(0xEE);
        byte[] expected = [0x42, 0x4D, 70, 0, 0, 0, 0, 0, 0, 0, 54, 0, 0, 0];

        Assert.True(BmpFile.TryAddFileHeader(file));

        Assert.Equal(expected, file[..14]);
    }

    [Fact]
    public void Nothing_after_the_14_bytes_is_changed()
    {
        byte[] file = Picture(length: 70);
        byte[] before = [.. file];

        Assert.True(BmpFile.TryAddFileHeader(file));

        Assert.Equal(before[14..], file[14..]);
    }

    [Fact]
    public void An_8_bit_picture_that_does_not_say_how_many_colours_has_256_of_them_before_its_pixels()
    {
        byte[] file = Picture(length: Plain + 1024 + 18, bitCount: 8);

        // 1096 bytes in all is 0x448, and 54 + 1024 is 0x436. Both stand with the low byte first.
        byte[] expected = [0x42, 0x4D, 0x48, 0x04, 0, 0, 0, 0, 0, 0, 0x36, 0x04, 0, 0];

        Assert.True(BmpFile.TryAddFileHeader(file));

        Assert.Equal(expected, file[..14]);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(4, 16)]
    [InlineData(8, 256)]
    public void Up_to_8_bits_without_a_number_of_colours_means_every_colour_the_bits_can_number(int bitCount, int colours)
    {
        AssertPixelsAt(Plain + colours * 4, Picture(length: Plain + 1024 + 16, bitCount: bitCount));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void More_than_8_bits_without_a_number_of_colours_means_no_colour_table(int bitCount)
    {
        AssertPixelsAt(Plain, Picture(bitCount: bitCount));
    }

    [Theory]
    [InlineData(8, 16)]
    [InlineData(8, 256)]
    [InlineData(4, 3)]
    [InlineData(1, 2)]
    [InlineData(24, 3)]
    [InlineData(32, 65536)]
    public void A_number_of_colours_in_the_header_is_how_many_the_colour_table_has(int bitCount, int coloursUsed)
    {
        // Each colour in the table takes four bytes. For 8 bits and 16 colours that is 54 + 64.
        byte[] file = Picture(length: Plain + coloursUsed * 4 + 16, bitCount: bitCount, coloursUsed: (uint)coloursUsed);

        AssertPixelsAt(Plain + coloursUsed * 4, file);
    }

    [Theory]
    [InlineData(16, 3u, 12)]
    [InlineData(32, 3u, 12)]
    [InlineData(32, 6u, 16)]
    public void Colour_masks_after_a_header_of_40_bytes_are_stepped_over(int bitCount, uint compression, int masks)
    {
        AssertPixelsAt(Plain + masks, Picture(length: 100, bitCount: bitCount, compression: compression));
    }

    // Run-length, JPEG and PNG forms are read by the decoder from what the header claims, and
    // the claim is not checked against the block for them, so they are refused.
    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(4u)]
    [InlineData(5u)]
    [InlineData(7u)]
    [InlineData(uint.MaxValue)]
    public void Any_compression_but_0_3_and_6_is_refused_and_the_array_left_as_it_was(uint compression)
    {
        AssertRefused(Picture(length: 100, bitCount: 32, compression: compression));
    }

    [Theory]
    [InlineData(1u, 8)]
    [InlineData(2u, 4)]
    public void Run_length_compression_is_refused_with_the_bits_it_is_made_for(uint compression, int bitCount)
    {
        AssertRefused(Picture(length: Plain + 1024 + 16, bitCount: bitCount, compression: compression));
    }

    [Theory]
    [InlineData(52u, 0u)]
    [InlineData(52u, 3u)]
    [InlineData(56u, 0u)]
    [InlineData(56u, 3u)]
    [InlineData(56u, 6u)]
    [InlineData(108u, 0u)]
    [InlineData(108u, 3u)]
    [InlineData(124u, 0u)]
    [InlineData(124u, 3u)]
    [InlineData(124u, 6u)]
    public void A_longer_header_has_its_masks_inside_it_so_the_pixels_start_right_after_it(uint headerSize, uint compression)
    {
        AssertPixelsAt(14 + (int)headerSize, Picture(length: 200, headerSize: headerSize, bitCount: 32, compression: compression));
    }

    [Fact]
    public void Masks_and_a_colour_table_are_both_stepped_over()
    {
        AssertPixelsAt(Plain + 12 + 20, Picture(length: 100, bitCount: 16, compression: 3, coloursUsed: 5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(49)]
    [InlineData(53)]
    public void A_block_shorter_than_a_header_is_refused_and_left_as_it_was(int length)
    {
        AssertRefused(Picture()[..length]);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(12u)]
    [InlineData(39u)]
    [InlineData(41u)]
    [InlineData(64u)]
    [InlineData(125u)]
    [InlineData(uint.MaxValue)]
    public void A_header_size_that_does_not_exist_is_refused_and_the_array_left_as_it_was(uint headerSize)
    {
        AssertRefused(Picture(length: 200, headerSize: headerSize));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(15)]
    [InlineData(48)]
    [InlineData(64)]
    [InlineData(0xFFFF)]
    public void A_number_of_bits_that_does_not_exist_is_refused_and_the_array_left_as_it_was(int bitCount)
    {
        AssertRefused(Picture(length: Plain + 1024 + 16, bitCount: bitCount));
    }

    [Theory]
    [InlineData(1, 257u)]
    [InlineData(4, 257u)]
    [InlineData(8, 257u)]
    [InlineData(8, 0x40000000u)]
    [InlineData(8, uint.MaxValue)]
    public void More_than_256_colours_for_up_to_8_bits_is_refused_and_the_array_left_as_it_was(int bitCount, uint coloursUsed)
    {
        // The array has room for 257 colours, so it is the number itself that is refused.
        // 0x40000000 times four is 0 in 32 bits: a sum that ran over would put the pixels at 54.
        AssertRefused(Picture(length: Plain + 257 * 4 + 16, bitCount: bitCount, coloursUsed: coloursUsed));
    }

    [Theory]
    [InlineData(16, 65537u)]
    [InlineData(24, 65537u)]
    [InlineData(32, 65537u)]
    [InlineData(24, 0x40000000u)]
    [InlineData(24, uint.MaxValue)]
    public void More_than_65536_colours_for_more_than_8_bits_is_refused_and_the_array_left_as_it_was(int bitCount, uint coloursUsed)
    {
        AssertRefused(Picture(length: Plain + 65537 * 4 + 16, bitCount: bitCount, coloursUsed: coloursUsed));
    }

    [Theory]
    [InlineData(Plain + 1023, 40u, 8, 0u, 0u)]
    [InlineData(Plain + 63, 40u, 8, 0u, 16u)]
    [InlineData(Plain + 11, 40u, 32, 3u, 0u)]
    [InlineData(Plain + 15, 40u, 32, 6u, 0u)]
    [InlineData(14 + 123, 124u, 32, 3u, 0u)]
    public void Pixels_that_would_start_beyond_the_end_are_refused_and_the_array_left_as_it_was(
        int length, uint headerSize, int bitCount, uint compression, uint coloursUsed)
    {
        AssertRefused(Picture(length, headerSize, bitCount, compression, coloursUsed));
    }

    // The pixels the header claims have to be in the block, so a block whose pixels would start
    // at its very end has none of them and is refused.
    [Theory]
    [InlineData(Plain, 40u, 24, 0u, 0u)]
    [InlineData(Plain + 1024, 40u, 8, 0u, 0u)]
    [InlineData(Plain + 64, 40u, 8, 0u, 16u)]
    [InlineData(Plain + 12, 40u, 32, 3u, 0u)]
    [InlineData(Plain + 16, 40u, 32, 6u, 0u)]
    [InlineData(14 + 124, 124u, 32, 3u, 0u)]
    public void Pixels_that_start_exactly_at_the_end_are_refused_as_none_of_the_claimed_pixels_are_there(
        int length, uint headerSize, int bitCount, uint compression, uint coloursUsed)
    {
        AssertRefused(Picture(length, headerSize, bitCount, compression, coloursUsed));
    }

    // 2 by 2 pixels take 16 bytes at 24 and 32 bits and 8 at 8 bits, and the block ends with them.
    [Theory]
    [InlineData(Plain + 16, Plain, 40u, 24, 0u, 0u)]
    [InlineData(Plain + 1024 + 8, Plain + 1024, 40u, 8, 0u, 0u)]
    [InlineData(Plain + 64 + 8, Plain + 64, 40u, 8, 0u, 16u)]
    [InlineData(Plain + 12 + 16, Plain + 12, 40u, 32, 3u, 0u)]
    [InlineData(Plain + 16 + 16, Plain + 16, 40u, 32, 6u, 0u)]
    [InlineData(14 + 124 + 16, 14 + 124, 124u, 32, 3u, 0u)]
    public void Pixels_that_fill_the_rest_of_the_block_exactly_are_accepted(
        int length, int pixelsAt, uint headerSize, int bitCount, uint compression, uint coloursUsed)
    {
        AssertPixelsAt(pixelsAt, Picture(length, headerSize, bitCount, compression, coloursUsed));
    }

    [Fact]
    public void A_header_of_nothing_but_the_largest_numbers_is_refused_already_for_its_size_without_an_error()
    {
        // The size of the header, 0xFFFFFFFF, is not one that exists, which settles it before
        // anything else is read. The next tests are the ones that reach the sums.
        byte[] file = Picture(length: 200);
        file.AsSpan(14, 40).Fill(0xFF);

        AssertRefused(file);
    }

    // For these the size of the header, the number of bits and the compression are valid, so it
    // is the width and the height that are worked out. Multiplied out, the claim does not fit
    // in 32 bits for any of them, and for the first three not even in 64: a sum that ran over
    // could come out small enough to pass.
    [Theory]
    [InlineData(32, int.MaxValue, int.MaxValue)]
    [InlineData(32, int.MaxValue, -int.MaxValue)]
    [InlineData(24, int.MaxValue, int.MaxValue)]
    [InlineData(1, int.MaxValue, int.MaxValue)]
    [InlineData(1, 1, int.MaxValue)]
    [InlineData(32, int.MaxValue, 1)]
    [InlineData(32, 0x10000, 0x10000)]
    public void The_largest_width_and_height_are_refused_without_an_error_and_the_array_left_as_it_was(int bitCount, int width, int height)
    {
        AssertRefused(Picture(length: 200, bitCount: bitCount, width: width, height: height));
    }

    [Fact]
    public void The_fields_that_are_not_needed_can_hold_the_largest_numbers_and_do_not_matter()
    {
        // The planes, the size of the pixels, the resolution and the colours that matter are
        // never read, so nothing is made of what they say.
        byte[] file = Picture(length: 200);
        file.AsSpan(26, 2).Fill(0xFF);
        file.AsSpan(34, 12).Fill(0xFF);
        file.AsSpan(50, 4).Fill(0xFF);

        AssertPixelsAt(Plain, file);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void A_width_under_1_in_the_header_is_refused_and_the_array_left_as_it_was(int width)
    {
        AssertRefused(Picture(length: 200, width: width));
    }

    [Fact]
    public void A_height_of_0_in_the_header_is_refused_and_the_array_left_as_it_was()
    {
        AssertRefused(Picture(length: 200, height: 0));
    }

    [Fact]
    public void A_picture_0_by_0_is_refused_and_the_array_left_as_it_was()
    {
        AssertRefused(Picture(length: 200, width: 0, height: 0));
    }

    [Fact]
    public void A_height_of_int_MinValue_is_refused_and_the_array_left_as_it_was()
    {
        // It has no positive number to match it, and it is one that no picture has.
        AssertRefused(Picture(length: 200, height: int.MinValue));
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-1)]
    public void A_negative_height_means_the_rows_run_from_the_top_and_is_accepted(int height)
    {
        // Two rows, or one, of 8 bytes: the block holds 16 after the header.
        AssertPixelsAt(Plain, Picture(length: Plain + 16, height: height));
    }

    [Fact]
    public void A_claim_one_row_larger_than_the_block_is_refused_and_the_array_left_as_it_was()
    {
        // 2 by 3 pixels of 24 bits are three rows of 8 bytes. The block holds two.
        AssertRefused(Picture(length: Plain + 16, height: 3));
    }

    [Fact]
    public void A_claim_one_row_larger_than_the_block_is_refused_whichever_way_the_rows_run()
    {
        AssertRefused(Picture(length: Plain + 16, height: -3));
    }

    [Fact]
    public void The_same_claim_with_exactly_enough_bytes_is_accepted()
    {
        AssertPixelsAt(Plain, Picture(length: Plain + 24, height: 3));
        AssertPixelsAt(Plain, Picture(length: Plain + 24, height: -3));
    }

    [Fact]
    public void One_byte_less_than_the_claim_is_refused_and_the_claim_itself_is_accepted()
    {
        AssertRefused(Picture(length: Plain + 24 - 1, height: 3));
        AssertPixelsAt(Plain, Picture(length: Plain + 24, height: 3));
    }

    [Theory]
    [InlineData(1, 1, 1, 4)]
    [InlineData(1, 32, 1, 4)]
    [InlineData(1, 33, 1, 8)]
    [InlineData(4, 8, 1, 4)]
    [InlineData(4, 9, 1, 8)]
    [InlineData(8, 4, 1, 4)]
    [InlineData(8, 5, 1, 8)]
    [InlineData(16, 2, 1, 4)]
    [InlineData(16, 3, 1, 8)]
    [InlineData(24, 1, 1, 4)]
    [InlineData(24, 3, 1, 12)]
    [InlineData(24, 3, 2, 24)]
    [InlineData(32, 3, 2, 24)]
    public void A_row_takes_a_whole_number_of_4_byte_groups_and_the_claim_is_that_many_rows(int bitCount, int width, int height, int bytes)
    {
        // Up to 8 bits the colour table comes first, with every colour the bits can number.
        int pixelsAt = bitCount <= 8 ? Plain + (1 << bitCount) * 4 : Plain;

        AssertRefused(Picture(length: pixelsAt + bytes - 1, bitCount: bitCount, width: width, height: height));
        AssertPixelsAt(pixelsAt, Picture(length: pixelsAt + bytes, bitCount: bitCount, width: width, height: height));
    }

    [Fact]
    public void An_8_bit_picture_with_its_colour_table_and_exactly_its_pixels_is_accepted()
    {
        // 2 by 2 pixels of 8 bits are two rows of 4 bytes after the table of 256 colours.
        AssertPixelsAt(Plain + 1024, Picture(length: Plain + 1024 + 8, bitCount: 8));
    }

    [Fact]
    public void An_8_bit_picture_whose_pixels_are_not_all_after_the_colour_table_is_refused()
    {
        AssertRefused(Picture(length: Plain + 1024 + 7, bitCount: 8));
    }

    [Fact]
    public void A_picture_made_by_PackedDib_is_accepted_for_every_size_tried_and_the_pixels_start_at_54()
    {
        // Widths of 1 to 4 pixels need 1 to 4 padding bytes in a row of 24-bit pixels, or none.
        foreach (var (width, height) in new[] { (1, 1), (1, 3), (2, 2), (3, 1), (3, 2), (4, 4), (5, 4), (7, 3), (100, 50), (101, 13) })
        {
            byte[] dib = BmpFile.PackedDib(width, height, new byte[width * 3 * height]);
            byte[] file = [.. new byte[BmpFile.FileHeaderSize], .. dib];

            AssertPixelsAt(Plain, file);
        }
    }

    [Fact]
    public void A_2_by_2_picture_is_a_header_of_40_bytes_then_its_rows_from_the_bottom_up_each_filled_to_8_bytes()
    {
        byte[] pixels =
        [
            1, 2, 3, 4, 5, 6,           // the top row: two pixels of blue, green and red
            7, 8, 9, 10, 11, 12,        // the bottom row
        ];
        byte[] expected =
        [
            40, 0, 0, 0,                // the size of the header
            2, 0, 0, 0,                 // the width
            2, 0, 0, 0,                 // the height, positive: the rows run from the bottom up
            1, 0,                       // planes, always 1
            24, 0,                      // bits for a pixel
            0, 0, 0, 0,                 // no compression
            16, 0, 0, 0,                // the size of the rows: two of 8 bytes
            0, 0, 0, 0, 0, 0, 0, 0,     // the resolution, not said
            0, 0, 0, 0, 0, 0, 0, 0,     // the colours, not said
            7, 8, 9, 10, 11, 12, 0, 0,  // the bottom row first
            1, 2, 3, 4, 5, 6, 0, 0,     // then the top row
        ];

        Assert.Equal(expected, BmpFile.PackedDib(2, 2, pixels));
    }

    [Fact]
    public void A_3_by_1_picture_is_a_header_of_40_bytes_then_one_row_of_9_bytes_filled_to_12()
    {
        byte[] pixels = [1, 2, 3, 4, 5, 6, 7, 8, 9];
        byte[] expected =
        [
            40, 0, 0, 0,                // the size of the header
            3, 0, 0, 0,                 // the width
            1, 0, 0, 0,                 // the height
            1, 0,                       // planes, always 1
            24, 0,                      // bits for a pixel
            0, 0, 0, 0,                 // no compression
            12, 0, 0, 0,                // the size of the rows: one of 12 bytes
            0, 0, 0, 0, 0, 0, 0, 0,     // the resolution, not said
            0, 0, 0, 0, 0, 0, 0, 0,     // the colours, not said
            1, 2, 3, 4, 5, 6, 7, 8, 9, 0, 0, 0,
        ];

        Assert.Equal(expected, BmpFile.PackedDib(3, 1, pixels));
    }

    [Fact]
    public void Three_rows_are_written_last_row_first()
    {
        // One pixel in each row, so each row of three bytes is filled up with one zero byte.
        byte[] expected = [7, 8, 9, 0, 4, 5, 6, 0, 1, 2, 3, 0];

        byte[] block = BmpFile.PackedDib(1, 3, Pixels(9));

        Assert.Equal(expected, block[40..]);
    }

    [Fact]
    public void A_row_that_is_a_multiple_of_four_already_gets_no_zero_bytes()
    {
        byte[] pixels = Pixels(12);

        byte[] block = BmpFile.PackedDib(4, 1, pixels);

        Assert.Equal(pixels, block[40..]);
    }

    [Fact]
    public void The_numbers_in_the_header_are_little_endian()
    {
        // 300 is 0x12C and 258 is 0x102. A row is 900 bytes, so the rows take 232200, 0x38B08.
        byte[] block = BmpFile.PackedDib(300, 258, new byte[300 * 3 * 258]);

        byte[] width = [0x2C, 0x01, 0, 0];
        byte[] height = [0x02, 0x01, 0, 0];
        byte[] size = [0x08, 0x8B, 0x03, 0];
        Assert.Equal(width, block[4..8]);
        Assert.Equal(height, block[8..12]);
        Assert.Equal(size, block[20..24]);
        Assert.Equal(40 + 232200, block.Length);
    }

    [Fact]
    public void A_block_from_PackedDib_with_14_bytes_in_front_gets_a_file_header()
    {
        byte[] dib = BmpFile.PackedDib(3, 2, Pixels(18));
        byte[] file = [.. new byte[BmpFile.FileHeaderSize], .. dib];

        AssertPixelsAt(Plain, file);
        Assert.Equal(dib, file[14..]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void A_width_under_1_is_refused(int width)
    {
        // For a width of 0 an empty array is even the right number of pixels. The complaint
        // names the width, so it is the width that was refused and not something else.
        Assert.Throws<ArgumentException>("width", () => BmpFile.PackedDib(width, 1, []));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void A_height_under_1_is_refused(int height)
    {
        Assert.Throws<ArgumentException>("height", () => BmpFile.PackedDib(1, height, []));
    }

    [Theory]
    [InlineData(11)]
    [InlineData(13)]
    public void Pixels_one_byte_short_or_one_byte_long_are_refused(int length)
    {
        Assert.Throws<ArgumentException>("bgr24TopDown", () => BmpFile.PackedDib(2, 2, new byte[length]));
    }

    [Theory]
    [InlineData(100000, 100000)]
    [InlineData(int.MaxValue, int.MaxValue)]
    [InlineData(int.MaxValue, 1)]
    public void A_picture_too_large_for_one_block_is_refused_before_anything_is_allocated(int width, int height)
    {
        var refusal = Assert.Throws<ArgumentException>(() => BmpFile.PackedDib(width, height, []));

        // An empty array is also the wrong number of pixels, which is refused in the same way.
        // Only what the complaint names tells the two apart, and this one is not about the array.
        Assert.NotEqual("bgr24TopDown", refusal.ParamName);
    }

    [Fact]
    public void The_limit_is_the_largest_array_there_can_be()
    {
        // One pixel wide, a row takes four bytes. This is the tallest picture whose block of
        // 40 bytes and its rows still fits in one array; one row more does not.
        int tallest = (Array.MaxLength - 40) / 4;

        var fits = Assert.Throws<ArgumentException>(() => BmpFile.PackedDib(1, tallest, []));
        var tooLarge = Assert.Throws<ArgumentException>(() => BmpFile.PackedDib(1, tallest + 1, []));

        // Neither can be made here, as its pixels alone would take over a gigabyte. The one that
        // fits is refused only for the pixels it was not given, the other for its size.
        Assert.Equal("bgr24TopDown", fits.ParamName);
        Assert.NotEqual("bgr24TopDown", tooLarge.ParamName);
    }

    // The time to decode follows the number of pixels, and a picture of 1 bit for a pixel holds
    // eight in a byte. A picture is put back on the clipboard as one of 24 bits, and that form
    // is limited to ClipLimits.MaxBitmapBytes, so one that is larger could never be pasted.
    [Fact]
    public void A_1_bit_picture_whose_24_bit_form_is_over_the_largest_block_is_refused_with_all_its_pixels_there()
    {
        // 16000 by 16000 pixels of 1 bit: 2000 bytes a row for 16000 rows, 32 MB with the 62
        // bytes before the pixels. Made once, and not copied: the array is checked by hand.
        // The 24-bit form of it is 48000 bytes a row for 16000 rows, 768 MB.
        const int side = 16000;
        byte[] file = Picture(length: Plain + 2 * 4 + (side / 8) * side, bitCount: 1, width: side, height: side);

        Assert.False(BmpFile.TryAddFileHeader(file));
        Assert.All(file.Take(BmpFile.FileHeaderSize), value => Assert.Equal(0, value));
    }

    [Fact]
    public void A_1_bit_picture_of_4000_by_4000_with_all_its_pixels_is_accepted()
    {
        // The 24-bit form of it is 48 MB, under the largest block.
        const int side = 4000;

        AssertPixelsAt(Plain + 2 * 4, Picture(length: Plain + 2 * 4 + (side / 8) * side, bitCount: 1, width: side, height: side));
    }

    [Fact]
    public void The_largest_width_and_height_do_not_make_the_size_of_the_24_bit_form_run_over()
    {
        // The pixels these claim are not in the block either. Whichever sum is made first, none
        // may run over to a small number and let the picture through.
        AssertRefused(Picture(length: 200, bitCount: 1, width: int.MaxValue, height: -int.MaxValue));
        AssertRefused(Picture(length: 200, bitCount: 1, width: 1, height: int.MaxValue));
    }
}
