using System.IO;
using Beacon.Core.ClipboardHistory;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Beacon.App.Interop;

/// <summary>
/// Reads the text in a picture with the text reader that is part of Windows. It runs on this
/// PC alone: the picture is handed to Windows in memory and the text comes back the same way.
/// Nothing is sent anywhere and nothing is written here. Call from a background thread.
/// </summary>
public sealed class PictureTextReader : IPictureTextReader
{
    private OcrEngine? _engine;
    private bool _asked;

    public string? Read(string pictureFile)
    {
        if (!_asked)
        {
            // The reader for the languages the owner has set Windows up with, or none when
            // Windows has none for any of them.
            _asked = true;
            _engine = OcrEngine.TryCreateFromUserProfileLanguages();
        }

        return _engine is null ? null : ReadAsync(_engine, pictureFile).GetAwaiter().GetResult();
    }

    private static async Task<string> ReadAsync(OcrEngine engine, string pictureFile)
    {
        using var file = File.OpenRead(pictureFile);
        using var stream = file.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        uint width = decoder.PixelWidth;
        uint height = decoder.PixelHeight;
        if (width == 0 || height == 0)
        {
            return "";
        }

        // The reader takes a picture of so many pixels a side at most: a larger one is made smaller first.
        var transform = new BitmapTransform();
        uint most = OcrEngine.MaxImageDimension;
        if (width > most || height > most)
        {
            double scale = Math.Min((double)most / width, (double)most / height);
            transform.ScaledWidth = Math.Max(1, (uint)(width * scale));
            transform.ScaledHeight = Math.Max(1, (uint)(height * scale));
            transform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);
        var read = await engine.RecognizeAsync(bitmap);
        return string.Join("\n", read.Lines.Select(line => line.Text));
    }
}
