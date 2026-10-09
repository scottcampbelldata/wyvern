using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Beacon.Core.ClipboardHistory;
using Beacon.Core.Platform;

namespace Beacon.App.Interop;

/// <summary>
/// Turns pictures from one form into another with the picture readers and writers that come
/// with Windows. It touches no clipboard, no window and no file: bytes in, bytes out. Each call
/// makes its own objects and lets go of them, so it can be called from any thread.
/// </summary>
/// <remarks>
/// Neither method lets the decoder make a memory store of the picture
/// (<see cref="BitmapCacheOption.None"/>): the pixels are read out of the caller's bytes when
/// they are asked for, which is once, and the stream over those bytes stays open until the
/// method returns.
/// </remarks>
public sealed class WpfImageCodec : IImageCodec
{
    // How long the header is that BmpFile.PackedDib puts in front of the rows: a BITMAPINFOHEADER.
    private const int DibHeaderBytes = 40;

    public byte[] BmpToPng(byte[] bmp)
    {
        // Open to the end of the method: with BitmapCacheOption.None every request for pixels
        // is answered from the stream, and the encoder asks for them in Save, below.
        using var input = new MemoryStream(bmp, writable: false);

        // BitmapCacheOption.None: no memory store is made. OnLoad would read the whole picture
        // into a second block of its own, as large again as the one that came in, which can be
        // a quarter of a gigabyte, only to be read once and let go.
        var decoder = new BmpBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Opaque(decoder.Frames[0])));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    public byte[] ToDib(byte[] picture)
    {
        // Open to the end of the method: with BitmapCacheOption.None every request for pixels
        // is answered from the stream, and they are asked for in CopyPixels, below.
        using var input = new MemoryStream(picture, writable: false);

        // BitmapDecoder.Create looks at the bytes to tell which kind of picture it is.
        // BitmapCacheOption.None: no memory store is made and every request is answered from
        // the stream, so making the decoder and taking its first frame asks for no pixels.
        // OnLoad reads the whole picture into memory at this point, before its size is known.
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
        BitmapSource frame = decoder.Frames[0];
        int width = frame.PixelWidth;
        int height = frame.PixelHeight;
        if (width < 1 || height < 1)
        {
            throw new InvalidOperationException("The picture has no pixels.");
        }

        // A small file can unpack into a picture of any size: fifty megabytes of JPEG can be
        // hundreds of millions of pixels. So it is measured here, by the two numbers in its
        // header, and refused before a single pixel is unpacked or any memory is asked for.
        // The measure is the block that BmpFile.PackedDib makes of it: three bytes for each
        // pixel, each row filled up to a multiple of four, and the 40-byte header in front.
        // That block may be no larger than ClipLimits.MaxBitmapBytes, which is the same limit,
        // worked out the same way, that BmpFile.TryAddFileHeader holds a copied picture to.
        // Divided, not multiplied, and in 64 bits: the width times the height times three
        // does not always fit, even there.
        long row = (((long)width * 3) + 3) / 4 * 4;
        if (row > (ClipLimits.MaxBitmapBytes - DibHeaderBytes) / height)
        {
            throw new InvalidOperationException("The picture is too large to put on the clipboard.");
        }

        // Only now is the picture unpacked: converted to three bytes for a pixel if it is not
        // that already, and copied out. CopyPixels fills the array row by row from the top.
        // The stride is the length of one row in bytes: three for each pixel, with nothing
        // added. The array is smaller than the block that was measured above, so its length
        // fits in 32 bits.
        int stride = width * 3;
        byte[] pixels = new byte[stride * height];
        Opaque(frame).CopyPixels(pixels, stride, 0);
        return BmpFile.PackedDib(width, height, pixels);
    }

    // Three bytes for each pixel: blue, green, red. A picture that has transparency loses it.
    // What programs make of the fourth byte of a clipboard picture differs from one to the
    // next, and three bytes are read the same way by all of them.
    private static BitmapSource Opaque(BitmapSource picture) =>
        picture.Format == PixelFormats.Bgr24 ? picture : new FormatConvertedBitmap(picture, PixelFormats.Bgr24, null, 0);
}
