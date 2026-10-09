using System.Buffers.Binary;
using Wyvern.Core.Platform;

namespace Wyvern.Core.ClipboardHistory;

/// <summary>
/// Turns a picture as the clipboard holds it into a .bmp file, and pixels into a picture as the
/// clipboard holds it.
/// </summary>
/// <remarks>
/// The clipboard's <see cref="ClipboardFormat.Dib"/> block is a "packed DIB": a
/// BITMAPINFOHEADER or one of the longer headers that came after it, then sometimes colour
/// masks and a colour table, then the pixels. A .bmp file is the same thing with a file header
/// of 14 bytes in front, and the app's picture decoder reads .bmp files. So the app reads the
/// block into an array that has 14 unused bytes in front, and the file header is written into
/// them here, without copying a large picture a second time.
/// </remarks>
public static class BmpFile
{
    /// <summary>How long the file header of a .bmp file is: the bytes to leave free in front of the block.</summary>
    public const int FileHeaderSize = 14;

    // A BITMAPINFOHEADER, the shortest header a packed DIB can start with, and where the numbers
    // that are needed stand in it, counted from the start of the file.
    private const int InfoHeaderSize = 40;
    private const int HeaderSizeAt = FileHeaderSize;
    private const int WidthAt = FileHeaderSize + 4;
    private const int HeightAt = FileHeaderSize + 8;
    private const int BitCountAt = FileHeaderSize + 14;
    private const int CompressionAt = FileHeaderSize + 16;
    private const int ColoursUsedAt = FileHeaderSize + 32;

    // Compression 0 (BI_RGB) is no compression. Compression 3 (BI_BITFIELDS) and 6
    // (BI_ALPHABITFIELDS) put colour masks after a BITMAPINFOHEADER: three of four bytes, or
    // four. The longer headers have room for the masks inside them. Any other compression is
    // run-length, JPEG or PNG: the decoder reads the pixels of those as many as the header
    // says, whatever the block holds.
    private const uint Uncompressed = 0;
    private const uint BitFields = 3;
    private const uint AlphaBitFields = 6;

    /// <summary>
    /// Makes a .bmp file of a packed DIB by filling in the file header in front of it: the
    /// letters "BM", the length of the whole file, and where in the file the pixels start. That
    /// is after the DIB's own header, its colour masks when it has them, and its colour table.
    /// </summary>
    /// <param name="file">14 bytes that are overwritten, then the packed DIB.</param>
    /// <returns>
    /// False, with the array left as it was, when what follows the 14 bytes is not a picture
    /// that can be given to the decoder: it is shorter than a header, the header's size or its
    /// number of bits for a pixel is not one that exists, its compression is not 0, 3 or 6, it
    /// claims more colours than a colour table can have, its width is under 1 or its height is
    /// 0 or int.MinValue, its pixels would start beyond the end of the array, or the pixels the
    /// width, the height and the number of bits claim are not all in the array, or the picture
    /// as 24 bits for a pixel, in the block <see cref="PackedDib"/> makes, would be larger than
    /// <see cref="ClipLimits.MaxBitmapBytes"/>. The pixels being in the array is what bounds
    /// the memory the decoder needs: it follows the size of the block, and not what the header
    /// says. The limit on the 24-bit form bounds the decoding time, which follows the number
    /// of pixels, and a picture too large for it could never be put back. A negative height,
    /// rows from the top, is fine.
    /// </returns>
    public static bool TryAddFileHeader(byte[] file)
    {
        if (file.Length < FileHeaderSize + InfoHeaderSize)
        {
            return false;
        }

        // Every number here was written by the program that made the copy and none can be
        // trusted, so the sums are done in 64 bits, where the largest values there are cannot
        // run over.
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(HeaderSizeAt));
        int bitCount = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(BitCountAt));
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(CompressionAt));
        long coloursUsed = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(ColoursUsedAt));
        int width = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(WidthAt));
        int height = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(HeightAt));

        if (headerSize is not (InfoHeaderSize or 52 or 56 or 108 or 124)
            || bitCount is not (1 or 4 or 8 or 16 or 24 or 32)
            || compression is not (Uncompressed or BitFields or AlphaBitFields)
            || coloursUsed > (bitCount <= 8 ? 256 : 65536)
            || width < 1
            || height == 0
            || height == int.MinValue)
        {
            return false;
        }

        long masks = (headerSize, compression) switch
        {
            (InfoHeaderSize, BitFields) => 12,
            (InfoHeaderSize, AlphaBitFields) => 16,
            _ => 0,
        };

        // A picture of up to 8 bits for a pixel always has a colour table. When the header does
        // not say how many colours it has, it has every colour that the bits can number.
        long colours = coloursUsed != 0 ? coloursUsed : bitCount <= 8 ? 1L << bitCount : 0;

        long pixelsAt = FileHeaderSize + headerSize + masks + colours * 4;
        if (pixelsAt > file.Length)
        {
            return false;
        }

        // The pixels that the header claims have to be there: each row takes a whole number of
        // 4-byte groups, and the rows are as many as the height, whichever way they run. The
        // width times the height times the bits can be more than 64 bits, so the claim is not
        // multiplied out. It fits when one row is no more than what is left divided by the
        // number of rows, which is the same thing for whole numbers.
        long rowBytes = (((long)width * bitCount) + 31) / 32 * 4;
        if (rowBytes > (file.Length - pixelsAt) / Math.Abs((long)height))
        {
            return false;
        }

        // The time to decode follows the number of pixels, not the size of the block: a picture
        // of 1 bit for a pixel holds eight of them in a byte. A stored picture is put back on
        // the clipboard as one of 24 bits for a pixel, laid out as PackedDib lays it out, and
        // that block is limited to ClipLimits.MaxBitmapBytes, so a picture whose block would be
        // larger could never be pasted and is not kept. Divided, not multiplied, as above.
        long rowBytes24 = ((long)width * 3 + 3) / 4 * 4;
        if (rowBytes24 > (ClipLimits.MaxBitmapBytes - InfoHeaderSize) / Math.Abs((long)height))
        {
            return false;
        }

        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(2), (uint)file.Length);
        file.AsSpan(6, 4).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(10), (uint)pixelsAt);
        return true;
    }

    /// <summary>
    /// The block for <see cref="ClipboardFormat.Dib"/> that holds a picture given as pixels: a
    /// BITMAPINFOHEADER for 24 bits for a pixel without compression, then the rows. A positive
    /// height in the header means that the rows run from the bottom of the picture to the top,
    /// so they are written in that order, and each row is filled up with zero bytes to a
    /// multiple of four.
    /// </summary>
    /// <param name="width">How many pixels wide the picture is.</param>
    /// <param name="height">How many pixels high it is.</param>
    /// <param name="bgr24TopDown">
    /// Three bytes for each pixel, blue, green and red, the top row first and nothing between
    /// the rows: exactly <paramref name="width"/> times 3 times <paramref name="height"/> bytes.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The width or the height is less than 1, the array is not exactly that long, or the block
    /// would be larger than an array can be, which is a little under 2 GB.
    /// </exception>
    public static byte[] PackedDib(int width, int height, byte[] bgr24TopDown)
    {
        if (width < 1)
        {
            throw new ArgumentException("A picture is at least one pixel wide.", nameof(width));
        }

        if (height < 1)
        {
            throw new ArgumentException("A picture is at least one pixel high.", nameof(height));
        }

        // The size is settled by dividing, before anything is multiplied: the width times the
        // height times three does not always fit in 64 bits. After this every sum fits in 32.
        long row = width * 3L;
        long stride = (row + 3) / 4 * 4;
        if (stride > (Array.MaxLength - InfoHeaderSize) / height)
        {
            throw new ArgumentException("The picture is too large for one block.");
        }

        if (bgr24TopDown.Length != row * height)
        {
            throw new ArgumentException("The pixels must be three bytes for each pixel of the picture, no more and no fewer.", nameof(bgr24TopDown));
        }

        byte[] block = new byte[InfoHeaderSize + stride * height];
        BinaryPrimitives.WriteUInt32LittleEndian(block, InfoHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(8), height);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(14), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(20), (uint)(stride * height));

        for (int line = 0; line < height; line++)
        {
            // The first row written is the last row of the picture. What is left of each row
            // after its pixels stays zero, as a new array is.
            bgr24TopDown.AsSpan((int)(row * (height - 1 - line)), (int)row)
                .CopyTo(block.AsSpan((int)(InfoHeaderSize + stride * line)));
        }

        return block;
    }
}
