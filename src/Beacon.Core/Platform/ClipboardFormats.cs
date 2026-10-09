namespace Beacon.Core.Platform;

/// <summary>
/// One kind of content on the Windows clipboard. A standard format has a number that never
/// changes. Any other format has a name, and Windows gives the name a number when it is asked.
/// </summary>
/// <param name="Id">The number of a standard format, or 0 for a format that goes by its name.</param>
/// <param name="Name">The name of a format that has no standard number, otherwise null.</param>
public readonly record struct ClipboardFormat(uint Id, string? Name)
{
    /// <summary>CF_UNICODETEXT: text as UTF-16, ending with a zero character.</summary>
    public static readonly ClipboardFormat UnicodeText = new(13, null);

    /// <summary>CF_DIB: a picture as a BITMAPINFO header followed by its pixels.</summary>
    public static readonly ClipboardFormat Dib = new(8, null);

    /// <summary>CF_HDROP: a list of files, as a DROPFILES structure followed by their paths.</summary>
    public static readonly ClipboardFormat FileDrop = new(15, null);

    /// <summary>A piece of a web page: a short header of offsets, then HTML as UTF-8.</summary>
    public static readonly ClipboardFormat Html = new(0, "HTML Format");

    /// <summary>Text with its formatting, as RTF.</summary>
    public static readonly ClipboardFormat Rtf = new(0, "Rich Text Format");

    /// <summary>A picture as the bytes of a PNG file.</summary>
    public static readonly ClipboardFormat Png = new(0, "PNG");

    /// <summary>Says what pasting a file list should do. It holds one 32-bit number; 1 means copy.</summary>
    public static readonly ClipboardFormat DropEffect = new(0, "Preferred DropEffect");

    /// <summary>Its presence alone says that clipboard histories and monitors must leave the content alone.</summary>
    public static readonly ClipboardFormat KeepOutOfMonitors = new(0, "ExcludeClipboardContentFromMonitorProcessing");

    /// <summary>
    /// The older way of saying the same as <see cref="KeepOutOfMonitors"/>, and the only one that
    /// several password managers set: its presence alone says that clipboard viewers must leave
    /// the content alone.
    /// </summary>
    public static readonly ClipboardFormat ViewerIgnore = new(0, "Clipboard Viewer Ignore");

    /// <summary>Holds one 32-bit number; 0 says the content must not go into a clipboard history.</summary>
    public static readonly ClipboardFormat CanIncludeInHistory = new(0, "CanIncludeInClipboardHistory");

    /// <summary>Holds one 32-bit number; 0 says the content must not be sent to another device.</summary>
    public static readonly ClipboardFormat CanUploadToCloud = new(0, "CanUploadToCloudClipboard");
}

/// <summary>The bytes to put on the clipboard for one format, exactly as the block is to hold them.</summary>
public sealed record ClipboardBlock(ClipboardFormat Format, byte[] Bytes)
{
    /// <summary>The type's name and how many bytes the block has, and neither the bytes nor the format.</summary>
    public override string ToString() => FormattableString.Invariant($"{nameof(ClipboardBlock)} {{ Bytes = {Bytes.Length} }}");
}

/// <summary>Who put the present content on the clipboard.</summary>
/// <param name="Program">
/// The name of that program's process as Windows gives it, such as "chrome", or null when it
/// cannot be told. It must never be logged or put in a notification.
/// </param>
/// <param name="IsBeacon">True when it was Beacon itself.</param>
public sealed record ClipboardOwner(string? Program, bool IsBeacon)
{
    /// <summary>The type's name only: the program's name must not come out wherever it is formatted into a string.</summary>
    public override string ToString() => nameof(ClipboardOwner);
}

/// <summary>
/// The clipboard while it is held open for reading. Nobody else can change it meanwhile, so
/// everything read through one of these belongs to the same copy. It is only good inside the
/// call that handed it out.
/// </summary>
public interface IOpenClipboard
{
    /// <summary>A number that Windows changes every time the clipboard's content changes.</summary>
    uint Sequence { get; }

    /// <summary>True when the clipboard offers this format.</summary>
    bool Has(ClipboardFormat format);

    /// <summary>
    /// Copies the block the clipboard holds for this format, whole and unchanged.
    /// </summary>
    /// <param name="maxBytes">The largest block that is copied.</param>
    /// <param name="tooLarge">True when the block is larger than that. Nothing is copied then.</param>
    /// <param name="reserve">
    /// How many zero bytes to leave in front of the block in the array that is returned, for a
    /// caller that has to put a header there and should not copy a large block a second time.
    /// </param>
    /// <returns>Null when the format is not there, cannot be read, or is too large.</returns>
    byte[]? Bytes(ClipboardFormat format, int maxBytes, out bool tooLarge, int reserve = 0);
}

/// <summary>
/// Reads the Windows clipboard. Implemented with Windows calls in the app, where it only opens
/// the clipboard and copies blocks; what is read, and what is made of it, is decided in
/// <c>Beacon.Core.ClipboardHistory</c>. A test passes its own.
/// </summary>
public interface IClipboardReader
{
    /// <summary>Who put the present content there. Never throws.</summary>
    ClipboardOwner Owner();

    /// <summary>
    /// Opens the clipboard, hands it to <paramref name="read"/> and closes it again, whatever
    /// that does. What it does should be short: every other program waits meanwhile.
    /// </summary>
    /// <exception cref="ClipboardBusyException">Another program has the clipboard open.</exception>
    T Read<T>(Func<IOpenClipboard, T> read);

    /// <summary>
    /// True while some window has the clipboard open: a program is in the middle of putting
    /// something there, or of taking it. Nothing is opened to find out. Never throws, and
    /// answers false where it cannot be told.
    /// </summary>
    bool InUse() => false;
}

/// <summary>
/// Tells when the clipboard's content has changed. Implemented in the app with a window that
/// Windows sends a message to. While it is stopped Beacon is told nothing and reads nothing.
/// Use from the UI thread.
/// </summary>
public interface IClipboardListener
{
    /// <summary>Starts telling. <paramref name="changed"/> is called on the UI thread after every change.</summary>
    void Start(Action changed);

    /// <summary>Stops telling. Does nothing when it was not started.</summary>
    void Stop();
}

/// <summary>
/// Turns a picture from one form into another. Implemented in the app, because .NET by itself
/// has no code that reads or writes picture files. Safe to call from any thread.
/// </summary>
public interface IImageCodec
{
    /// <summary>The picture in a .bmp file's bytes, as the bytes of a PNG file without transparency.</summary>
    byte[] BmpToPng(byte[] bmp);

    /// <summary>
    /// The picture in a PNG, JPEG, GIF or BMP file's bytes, as the block the clipboard's
    /// <see cref="ClipboardFormat.Dib"/> format holds.
    /// </summary>
    byte[] ToDib(byte[] picture);
}
