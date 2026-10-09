namespace Beacon.Core.ClipboardHistory;

/// <summary>
/// How much of one copy the clipboard history reads and keeps. Any program can put anything on
/// the clipboard, of any size, and what the history keeps is written to disk and goes to the
/// owner's other machine. Each limit is there so that one copy cannot fill the disk or the
/// memory.
/// </summary>
public static class ClipLimits
{
    /// <summary>
    /// The most bytes of text that are kept for one copy, counted as UTF-8. HTML and rich text
    /// have the same limit, each by itself. A long book is a tenth of this. Without the limit,
    /// selecting all of a large log file and copying it would put the whole file into the
    /// history.
    /// </summary>
    public const int MaxTextBytes = 10 * 1024 * 1024;

    /// <summary>
    /// The most bytes of one picture that is kept: a copied picture as the PNG file it is stored
    /// as, and each picture file that is kept with a file list. Without it one screenshot of
    /// several large screens, or one photograph straight from a camera, could take hundreds of
    /// megabytes.
    /// </summary>
    public const int MaxImageBytes = 50 * 1024 * 1024;

    /// <summary>
    /// The most paths in one file list. Copying a folder's whole content in Explorer puts every
    /// path on the clipboard, and nobody pastes more files than this from a history.
    /// </summary>
    public const int MaxFiles = 1000;

    /// <summary>
    /// The most picture files that are kept for one entry. A file list can name a thousand
    /// photographs, and copying them all would fill the disk for something the owner may never
    /// paste.
    /// </summary>
    public const int MaxPictures = 20;

    /// <summary>
    /// The most bytes that all the pictures kept for one entry take together, so that twenty
    /// pictures that are each just under <see cref="MaxImageBytes"/> do not come to a gigabyte.
    /// </summary>
    public const long MaxPicturesBytes = 200L * 1024 * 1024;

    /// <summary>
    /// The largest picture block that is read from the clipboard at all. A picture on the
    /// clipboard is not compressed, so it is several times larger than the PNG made from it, and
    /// it has to be held in memory whole before it can be made smaller. A larger block is left
    /// where it is.
    /// </summary>
    public const int MaxBitmapBytes = 256 * 1024 * 1024;

    /// <summary>
    /// The largest file list block that is read. It is room for <see cref="MaxFiles"/> paths
    /// many times over, and it stops a block that only claims to be a file list from being
    /// copied into memory whatever its size.
    /// </summary>
    public const int MaxFileListBytes = 4 * 1024 * 1024;

    /// <summary>
    /// How much larger than its content a block may be. Windows rounds the size of a block up,
    /// so a block whose content is exactly at a limit can be a little over it, and it must not
    /// be refused for that.
    /// </summary>
    public const int BlockSlack = 4096;

    /// <summary>
    /// The largest day file that is read. A day's records are a few megabytes at most, and a line
    /// is read whole, so a damaged file with no line break in it, a multi-gigabyte file of zeros
    /// say, would have to be held in memory as one line. A larger file is treated as one that
    /// cannot be read.
    /// </summary>
    public const long MaxDayFileBytes = 256L * 1024 * 1024;
}
