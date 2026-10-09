using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Data;
using System.Windows.Media;
using Wyvern.App.Interop;
using Wyvern.Core.Files;

namespace Wyvern.App;

/// <summary>
/// Icons for launcher rows, loaded in the background and then served from memory. App icons
/// are loaded up front by <see cref="Preload"/>. File type icons are loaded the first time a
/// row asks for one, so the icon for a new kind of file appears from the next keystroke.
/// </summary>
public sealed class IconCache
{
    private const int PixelSize = 32;

    private readonly ConcurrentDictionary<string, ImageSource> _icons = new();
    private readonly ConcurrentDictionary<string, bool> _fileTypesAsked = new();

    // The programs whose icon was asked for and could not be had, so that each is asked once.
    private readonly ConcurrentDictionary<string, bool> _programsWithout = new();
    private readonly BlockingCollection<string> _fileTypesWanted = [];
    private readonly Action<string, Exception>? _reportFailure;
    private int _workerStarted;
    private int _fileTypeFailureReported;

    /// <param name="reportFailure">
    /// Called when icons failed to load, with a summary that gives a count (never an app id, a
    /// title or a file name, since an id can be a path) and the first exception.
    /// </param>
    public IconCache(Action<string, Exception>? reportFailure = null)
    {
        _reportFailure = reportFailure;
    }

    /// <returns>The icon, or null when it has not been loaded yet or there is none.</returns>
    public ImageSource? Get(string key)
    {
        if (_icons.TryGetValue(key, out var icon))
        {
            return icon;
        }

        if (key.StartsWith(OpenWindows.IconKeyPrefix, StringComparison.Ordinal))
        {
            return ProgramIcon(key);
        }

        if (key.StartsWith(FileProvider.IconKeyPrefix, StringComparison.Ordinal) && _fileTypesAsked.TryAdd(key, true))
        {
            StartWorkerOnce();
            _fileTypesWanted.Add(key);
        }

        return null;
    }

    /// <summary>
    /// Loads app icons that are not cached yet. Call from a background STA thread. Only icons
    /// that loaded are remembered, so an app whose icon was unavailable is tried again next time.
    /// </summary>
    // The icon of a program that has a window open, read from the program's file the first
    // time a row asks for it. There are a few dozen at most, and each takes a moment.
    private ImageSource? ProgramIcon(string key)
    {
        if (_programsWithout.ContainsKey(key))
        {
            return null;
        }

        try
        {
            using var packed = System.Drawing.Icon.ExtractAssociatedIcon(key[OpenWindows.IconKeyPrefix.Length..]);
            if (packed is not null)
            {
                var icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(packed.Handle, System.Windows.Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                icon.Freeze();
                _icons[key] = icon;
                return icon;
            }
        }
        catch (Exception ex) when (IsIconFailure(ex) || ex is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // An icon is decoration: the row shows its tile.
        }

        _programsWithout[key] = true;
        return null;
    }

    public void Preload(IEnumerable<string> appIds)
    {
        int failures = 0;
        Exception? firstError = null;

        foreach (string id in appIds)
        {
            if (_icons.ContainsKey(id))
            {
                continue;
            }

            try
            {
                if (ShellIcon.Load(id, PixelSize) is { } icon)
                {
                    _icons[id] = icon;
                }
            }
            catch (Exception ex) when (IsIconFailure(ex))
            {
                // An icon is decoration: the row shows without one and the remaining icons still load.
                failures++;
                firstError ??= ex;
            }
        }

        if (firstError is not null)
        {
            _reportFailure?.Invoke($"{failures} app icon(s) could not be loaded", firstError);
        }
    }

    private void StartWorkerOnce()
    {
        if (Interlocked.Exchange(ref _workerStarted, 1) == 1)
        {
            return;
        }

        // The shell calls that produce icons need a single-threaded apartment.
        var worker = new Thread(LoadFileTypeIcons)
        {
            IsBackground = true,
            Name = "Wyvern file type icons",
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    private void LoadFileTypeIcons()
    {
        foreach (string key in _fileTypesWanted.GetConsumingEnumerable())
        {
            try
            {
                if (ShellIcon.LoadForFileType(key[FileProvider.IconKeyPrefix.Length..]) is { } icon)
                {
                    _icons[key] = icon;
                }
            }
            catch (Exception ex)
            {
                // Anything at all, because this thread has nobody above it: an exception that
                // escaped would end the process. Said once: if this is broken it is broken for
                // every file type.
                if (Interlocked.Exchange(ref _fileTypeFailureReported, 1) == 0)
                {
                    _reportFailure?.Invoke("File type icons could not be loaded", ex);
                }
            }
        }
    }

    private static bool IsIconFailure(Exception ex) =>
        ex is ExternalException or ArgumentException or InvalidOperationException or InvalidCastException or NotSupportedException;
}

/// <summary>Lets XAML turn a <c>SearchItem.IconKey</c> into an image.</summary>
public sealed class IconKeyConverter(IconCache cache) : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key ? cache.Get(key) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
