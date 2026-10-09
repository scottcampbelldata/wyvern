using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wyvern.Core.ClipboardHistory;

namespace Wyvern.App;

/// <summary>
/// Reads a picture in the clipboard history from its file for the preview, with no more pixels
/// than the preview can show. The window asks for it when its row is selected and at no other
/// time, and nothing is kept here: the window lets go of the picture when another row is
/// selected. A picture that has gone, is too large, is not one of the kinds read here or cannot
/// be read gives no picture; the preview is then empty.
/// </summary>
/// <remarks>
/// Safe to call from any thread, and meant for a pool thread: a large picture takes long enough
/// to read that the window must not wait for it. Each call makes its own objects and lets go of
/// them, and what it returns is frozen, so it can be handed to the UI thread.
/// </remarks>
/// <param name="report">Receives fixed words for the log, and an exception when there is one. Never a path.</param>
public sealed class ClipPictureLoader(Action<string, Exception?> report)
{
    /// <summary>
    /// The most pixels a picture may have and still be read. The size of the file says nothing
    /// about this: a small file can unpack into a picture of any size, and not every kind of
    /// picture can be made smaller without unpacking all of it first.
    /// </summary>
    public const long MaxPixels = 120_000_000;

    // No preview is larger than this on a side, whatever the caller says. It keeps the sums
    // below within what a whole number holds.
    private const int MaxBoxSide = 4096;

    // How many bytes of a file are looked at to tell whether it begins as a picture: enough for
    // the two letters of a BMP, the rest of its file header (14 bytes in all) and the four bytes
    // after it, which say how long the picture's own header is.
    private const int StartLength = 18;

    private int _failureReported;
    private int _wrongThreadReported;

    // How the four kinds of picture that are read begin. PNG: the eight bytes every PNG file
    // starts with. JPEG: its first three bytes. GIF: one of the two versions there are, in six
    // letters. BMP: two letters, followed by a header that is looked at as well (StartsAsABmp).
    private static ReadOnlySpan<byte> PngStart => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> JpegStart => [0xFF, 0xD8, 0xFF];

    private static ReadOnlySpan<byte> Gif87Start => "GIF87a"u8;

    private static ReadOnlySpan<byte> Gif89Start => "GIF89a"u8;

    private static ReadOnlySpan<byte> BmpStart => "BM"u8;

    /// <summary>Reads the picture in a file, made smaller to fit a box and never larger.</summary>
    /// <param name="path">The full path of the picture's file.</param>
    /// <param name="boxWidth">The width of the place the picture is shown in, in pixels of the screen.</param>
    /// <param name="boxHeight">The height of that place, in pixels of the screen.</param>
    /// <returns>The picture, frozen, or null when there is none to show. Never throws.</returns>
    public BitmapSource? Load(string path, int boxWidth, int boxHeight)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > ClipLimits.MaxImageBytes)
            {
                return null;
            }

            // Closed when this method returns, whichever way it does.
            using FileStream? stream = Open(path);
            if (stream is null || !StartsAsAPicture(stream))
            {
                return null;
            }

            // How large the picture is stands at the start of the file. With DelayCreation and
            // no cache the frame answers from there, and nothing of the picture is unpacked.
            stream.Position = 0;
            BitmapFrame whole = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            int width = whole.PixelWidth;
            int height = whole.PixelHeight;
            if (width < 1 || height < 1 || (long)width * height > MaxPixels)
            {
                return null;
            }

            // How many of its pixels the file says go to the inch, which is what tells how
            // large the picture is meant to be shown.
            double dpiX = whole.DpiX;
            double dpiY = whole.DpiY;

            var (decodeWidth, decodeHeight) = DecodeSize(
                width,
                height,
                Math.Clamp(boxWidth, 1, MaxBoxSide),
                Math.Clamp(boxHeight, 1, MaxBoxSide));

            // A picture that cannot be read is not always thrown for: the reader can raise this
            // event instead, while it reads, and carry on with an empty picture.
            Exception? failure = null;
            EventHandler<ExceptionEventArgs> failed = (_, args) => failure = args.ErrorException ?? new InvalidDataException();

            stream.Position = 0;
            var image = new BitmapImage();
            image.DecodeFailed += failed;
            image.BeginInit();

            // OnLoad: the whole picture is read before EndInit returns, so the file can be
            // closed here and deleted at any time afterwards.
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = decodeWidth;
            image.DecodePixelHeight = decodeHeight;
            image.StreamSource = stream;
            image.EndInit();
            image.DecodeFailed -= failed;
            if (failure is not null)
            {
                ReportFailure(failure);
                return null;
            }

            BitmapSource picture = image;
            if ((decodeWidth > 0 || decodeHeight > 0) && (image.PixelWidth != width || image.PixelHeight != height))
            {
                // Made smaller, by this much. The side that was asked for is the one that tells:
                // the other was rounded to a whole pixel.
                double kept = Math.Max((double)image.PixelWidth / width, (double)image.PixelHeight / height);
                picture = ShownAsLargeAsBefore(image, dpiX * kept, dpiY * kept);
            }

            picture.Freeze();
            return picture;
        }
        catch (Exception ex)
        {
            // Anything at all: a pool thread has nobody above it, and the window is waiting
            // for an answer.
            ReportFailure(ex);
            return null;
        }
    }

    /// <summary>
    /// How large the picture in a file is, in pixels, and how large the file is in bytes. It
    /// is read from the start of the file, under the same care as <see cref="Load"/>, and
    /// nothing of the picture is unpacked.
    /// </summary>
    /// <returns>The sizes, or null when they cannot be told. Never throws, and reports nothing: the picture itself says so when it cannot be shown.</returns>
    public (int Width, int Height, long Bytes)? Measure(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > ClipLimits.MaxImageBytes)
            {
                return null;
            }

            using FileStream? stream = Open(path);
            if (stream is null || !StartsAsAPicture(stream))
            {
                return null;
            }

            stream.Position = 0;
            BitmapFrame whole = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return whole.PixelWidth >= 1 && whole.PixelHeight >= 1 ? (whole.PixelWidth, whole.PixelHeight, file.Length) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Says that a picture could not be shown: fixed words and the exception, never a path.
    /// Said once for each run of Wyvern: a picture that cannot be shown would otherwise be said
    /// again every time its row is passed. Never throws.
    /// </summary>
    public void ReportFailure(Exception error)
    {
        try
        {
            if (Interlocked.Exchange(ref _failureReported, 1) == 0)
            {
                report("A picture in the clipboard history could not be shown", error);
            }
        }
        catch (Exception)
        {
            // The report itself failed, and there is nobody else to tell.
        }
    }

    /// <summary>
    /// Says that the window was told of a picture on a thread that is not its own, so that it
    /// did nothing: fixed words. Said once for each run of Wyvern. Safe to call from any thread.
    /// Never throws.
    /// </summary>
    public void ReportWrongThread()
    {
        try
        {
            if (Interlocked.Exchange(ref _wrongThreadReported, 1) == 0)
            {
                report("The clipboard preview was told of a picture on a thread that is not the window's, so it did nothing", null);
            }
        }
        catch (Exception)
        {
            // The report itself failed, and there is nobody else to tell.
        }
    }

    // Opened so that the file can be deleted while it is read: the history is free to delete an
    // entry at any time. Null when the file has gone since it was looked for, which is no
    // picture and not a failure.
    private static FileStream? Open(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    // Only a file that begins the way a PNG, a JPEG, a GIF or a BMP begins is handed on. Windows
    // picks the reader of a picture by what the file holds, from every reader installed on the
    // machine, and a file with a picture's name could be anything. A file shorter than what is
    // looked at is judged by the bytes it has.
    private static bool StartsAsAPicture(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[StartLength];
        int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        ReadOnlySpan<byte> start = buffer[..read];
        return start.StartsWith(PngStart)
            || start.StartsWith(JpegStart)
            || start.StartsWith(Gif87Start)
            || start.StartsWith(Gif89Start)
            || StartsAsABmp(start);
    }

    // Two letters are little to go by: many files that are no picture begin with "BM". So the
    // header that follows them has to be a plausible one. A BMP file begins with a file header
    // of 14 bytes (BITMAPFILEHEADER): the two letters, the size of the file in four bytes, four
    // bytes that are not used and the place of the pixels in four bytes. Straight after it, at
    // byte 14, comes the picture's own header, which begins with its own length in four bytes,
    // and there are only so many kinds: 12 (BITMAPCOREHEADER), 40 (BITMAPINFOHEADER), 52 and 56
    // (the same with colour masks), 108 (BITMAPV4HEADER) and 124 (BITMAPV5HEADER). The numbers
    // are stored lowest byte first. A file size of zero, or any other header length, is not
    // taken for a BMP.
    private static bool StartsAsABmp(ReadOnlySpan<byte> start)
    {
        if (start.Length < StartLength || !start.StartsWith(BmpStart))
        {
            return false;
        }

        uint fileSize = BinaryPrimitives.ReadUInt32LittleEndian(start[2..]);
        uint headerLength = BinaryPrimitives.ReadUInt32LittleEndian(start[14..]);
        return fileSize != 0 && headerLength is 12 or 40 or 52 or 56 or 108 or 124;
    }

    // What to ask the reader for, so that a picture larger than the box comes out fitting it
    // with its shape kept: one side only, the one that reaches the box first, and the reader
    // works out the other. Asking for both would squeeze the picture into the shape of the box.
    // A picture no larger than the box is asked for as it is, (0, 0): asking for a side would
    // make it larger. Zero means "not asked for".
    private static (int Width, int Height) DecodeSize(int width, int height, int boxWidth, int boxHeight)
    {
        if (width <= boxWidth && height <= boxHeight)
        {
            return (0, 0);
        }

        // Is the picture wider for its height than the box is? Compared crosswise, in whole
        // numbers, so that nothing is lost to rounding.
        if ((long)width * boxHeight >= (long)height * boxWidth)
        {
            // The other side would come to less than one pixel for a picture that is very long
            // and thin, and a picture of no pixels cannot be made. Then, and only then, both
            // sides are asked for, and the thin side is one pixel.
            return (long)height * boxWidth / width >= 1 ? (boxWidth, 0) : (boxWidth, 1);
        }

        return (long)width * boxHeight / height >= 1 ? (0, boxHeight) : (1, boxHeight);
    }

    // A picture says how large it is meant to be shown by how many of its pixels go to the
    // inch. One that was unpacked with fewer pixels still says what its file said, so it would
    // be shown that much smaller than before: a photograph marked 300 to the inch at a third of
    // the size its place allows. Its pixels are given to a new picture that says as many fewer
    // to the inch as it was made smaller, which is then laid out as the whole picture would
    // have been. What is copied is never larger than the box.
    private static BitmapSource ShownAsLargeAsBefore(BitmapSource smaller, double dpiX, double dpiY)
    {
        if (!(dpiX > 0 && dpiY > 0)
            || double.IsInfinity(dpiX)
            || double.IsInfinity(dpiY)
            || (Math.Abs(dpiX - smaller.DpiX) < 0.01 && Math.Abs(dpiY - smaller.DpiY) < 0.01))
        {
            // Nothing sensible to say, or it says it already.
            return smaller;
        }

        // Four bytes for each pixel, the form the screen is drawn from, so that the length of a
        // row is known whatever kind of picture it was.
        BitmapSource plain = smaller.Format == PixelFormats.Pbgra32
            ? smaller
            : new FormatConvertedBitmap(smaller, PixelFormats.Pbgra32, null, 0);
        int width = plain.PixelWidth;
        int height = plain.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        plain.CopyPixels(pixels, stride, 0);
        return BitmapSource.Create(width, height, dpiX, dpiY, PixelFormats.Pbgra32, null, pixels, stride);
    }
}
