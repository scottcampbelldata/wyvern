using Beacon.Core.Platform;

namespace Beacon.Tests;

/// <summary>
/// Stands in for the app's picture code, which the tests cannot reference. It does not read
/// pictures at all: it wraps the bytes it is given so that a test can see what went in and
/// what came out.
/// </summary>
public sealed class FakeImageCodec : IImageCodec
{
    /// <summary>What every "PNG" this makes starts with.</summary>
    public static readonly byte[] PngMark = [0x89, (byte)'P', (byte)'N', (byte)'G'];

    /// <summary>What every "DIB" this makes starts with.</summary>
    public static readonly byte[] DibMark = [(byte)'D', (byte)'I', (byte)'B', 0];

    /// <summary>When set, both calls throw it.</summary>
    public Exception? Fails { get; set; }

    /// <summary>When set, <see cref="BmpToPng"/> gives this many zero bytes after the mark, however small the picture was.</summary>
    public int? PngLength { get; set; }

    /// <summary>Every .bmp file that was handed in, in order.</summary>
    public List<byte[]> Bitmaps { get; } = [];

    /// <summary>Every picture file that was handed in to be made a DIB, in order.</summary>
    public List<byte[]> Pictures { get; } = [];

    /// <summary>The "PNG" for a .bmp file: the mark, then the file's bytes.</summary>
    public static byte[] PngOf(byte[] bmp) => [.. PngMark, .. bmp];

    /// <summary>The "DIB" for a picture file: the mark, then the file's bytes.</summary>
    public static byte[] DibOf(byte[] picture) => [.. DibMark, .. picture];

    public byte[] BmpToPng(byte[] bmp)
    {
        if (Fails is not null)
        {
            throw Fails;
        }

        Bitmaps.Add(bmp);
        return PngLength is { } length ? [.. PngMark, .. new byte[length]] : PngOf(bmp);
    }

    public byte[] ToDib(byte[] picture)
    {
        if (Fails is not null)
        {
            throw Fails;
        }

        Pictures.Add(picture);
        return DibOf(picture);
    }
}
