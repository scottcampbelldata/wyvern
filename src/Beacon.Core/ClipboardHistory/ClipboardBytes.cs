using System.Buffers.Binary;
using System.Text;
using Beacon.Core.Platform;

namespace Beacon.Core.ClipboardHistory;

/// <summary>
/// Reads the blocks of bytes that Windows keeps on the clipboard, and makes such blocks. The
/// app only copies a block in or out; what its bytes mean is decided here. A block comes from
/// whatever program made the copy and nothing has checked it, so nothing here trusts a number
/// that stands in a block or reads past the block's end.
/// </summary>
public static class ClipboardBytes
{
    // A DROPFILES structure: where the names start (4 bytes), a point (8), a flag that is not
    // used here (4), and whether the names are UTF-16 (4).
    private const int DropFilesHeaderSize = 20;
    private const int DropFilesWideAt = 16;

    // The longest path Windows has, in characters. A longer name is not a path.
    private const int MaxNameLength = 32767;

    // U+FFFD, the character that stands for one that could not be read. Written as a number so
    // that no editor or tool can mistake it for a mistake.
    private const char Replacement = (char)0xFFFD;

    /// <summary>
    /// The text with each half of a surrogate pair that has no partner replaced by U+FFFD.
    /// Windows does not check clipboard text, and text with half a pair in it cannot be written
    /// as JSON or as UTF-8 without loss.
    /// </summary>
    /// <returns>The same text, not a copy, when nothing in it has to be replaced.</returns>
    public static string WellFormed(string text)
    {
        int first = FirstHalfPair(text);
        if (first < 0)
        {
            return text;
        }

        char[] characters = text.ToCharArray();
        for (int i = first; i < characters.Length; i++)
        {
            if (char.IsHighSurrogate(characters[i]) && i + 1 < characters.Length && char.IsLowSurrogate(characters[i + 1]))
            {
                // A whole pair. Step over its second half as well.
                i++;
            }
            else if (char.IsSurrogate(characters[i]))
            {
                characters[i] = Replacement;
            }
        }

        return new string(characters);
    }

    /// <summary>
    /// The text in a CF_UNICODETEXT block. It is UTF-16, little endian, and ends at the first
    /// zero character, or at the end of the block when there is none. A last byte that is only
    /// half a character is ignored. The text is made <see cref="WellFormed"/>.
    /// </summary>
    /// <returns>Null when the block holds no characters.</returns>
    public static string? Text(byte[] block)
    {
        int length = 0;
        while (2 * length + 1 < block.Length && (block[2 * length] | block[2 * length + 1]) != 0)
        {
            length++;
        }

        if (length == 0)
        {
            return null;
        }

        string text = string.Create(length, block, static (characters, bytes) =>
        {
            for (int i = 0; i < characters.Length; i++)
            {
                characters[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2 * i));
            }
        });
        return WellFormed(text);
    }

    /// <summary>
    /// The block that puts text on the clipboard: its characters as UTF-16, little endian, and
    /// one zero character after them. Whoever reads the block stops at the first zero character,
    /// so text that holds one is cut there and what is on the clipboard is what will be pasted.
    /// </summary>
    public static byte[] TextBlock(string text)
    {
        int zero = text.IndexOf('\0');
        ReadOnlySpan<char> characters = zero < 0 ? text : text.AsSpan(0, zero);

        byte[] block = new byte[(characters.Length + 1) * 2];
        for (int i = 0; i < characters.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(2 * i), characters[i]);
        }

        return block;
    }

    /// <summary>
    /// The bytes before the first zero byte. HTML and rich text are on the clipboard as text of
    /// single bytes that ends with a zero, and what follows the zero is padding.
    /// </summary>
    /// <returns>The same array when it holds no zero byte. An empty array, never null, when it starts with one.</returns>
    public static byte[] UpToZero(byte[] block)
    {
        int zero = Array.IndexOf(block, (byte)0);
        return zero < 0 ? block : block[..zero];
    }

    /// <summary>A new array that holds the bytes and one zero byte after them: the block for HTML or rich text.</summary>
    public static byte[] WithZero(byte[] bytes)
    {
        byte[] block = new byte[bytes.Length + 1];
        bytes.CopyTo(block, 0);
        return block;
    }

    /// <summary>
    /// What a marker that holds one 32-bit number says, for
    /// <see cref="ClipboardFormat.CanIncludeInHistory"/> and
    /// <see cref="ClipboardFormat.CanUploadToCloud"/>. Windows defines two numbers: 0, do not,
    /// and 1, do. A marker that is there says "may keep" only when it holds at least four bytes
    /// and the first four, little endian, are exactly 1. Anything else, a block shorter than
    /// four bytes (an empty one is what stands for a marker that is there but could not be
    /// read), the number 0, or any number that is neither 0 nor 1, is in doubt: the program
    /// meant to mark its copy, and when in doubt the copy is left alone. A block longer than
    /// four bytes counts by its first four, as Windows may hand back a block that is longer.
    /// </summary>
    /// <param name="marker">The marker's block, or null when the clipboard does not have the format. Null is false: with no marker there is nothing that says no.</param>
    /// <returns>True when the copy is to be left out of the history.</returns>
    public static bool SaysNo(byte[]? marker) =>
        marker is not null && (marker.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(marker) != 1);

    /// <summary>
    /// The block for <see cref="ClipboardFormat.DropEffect"/> that says a pasted file
    /// list is to be copied, not moved: the number 1 (DROPEFFECT_COPY) as 32 bits, little endian.
    /// </summary>
    public static byte[] CopyEffect() => [1, 0, 0, 0];

    /// <summary>
    /// The paths in a CF_HDROP block, which is a DROPFILES structure. Its first 32-bit number
    /// says where the names start, and the 32-bit number at byte 16 says whether they are
    /// UTF-16, little endian (anything but 0), or one byte for each character (0). The names
    /// follow one after the other, each ended by a zero character, and one more zero character
    /// or the end of the block ends the list.
    /// </summary>
    /// <remarks>
    /// <para>Only an ordinary path is kept: a drive path, which is a letter, a colon and a
    /// separator ("C:\..."), or a share path whose server is neither "." nor "?" and holds no
    /// "@" ("\\server\share\..."). A separator is "\" or "/" wherever one stands, so
    /// "\\./pipe\x" is the device form too. The device and extended forms ("\\.\pipe\...",
    /// "\\.\PhysicalDrive0", "\\?\GLOBALROOT\...") name things that are not files, and a
    /// program that opens the files that are listed must not be led to one. A server whose
    /// name holds an "@" ("\\host@SSL\DavWWWRoot\...", "\\host@8080\share\...") is a web
    /// address that Windows serves over HTTP, and is left out for the same reason, as is a share
    /// named "DavWWWRoot" ("\\host\DavWWWRoot\..."). A name of more than
    /// 32767 characters, the longest path Windows has, is left out, a name with half a
    /// surrogate pair in it is left out, and so are characters at the end of the block that no
    /// zero character ends: a path that was cut short can be the path of another file.</para>
    /// <para>The list is read only until <see cref="ClipLimits.MaxFiles"/> + 1 paths have been
    /// taken, one more than the limit so that whoever reads the list can see that it was over
    /// the limit. A block of the largest size that is read can hold a million names.</para>
    /// <para>In a list of single bytes only ASCII is read. Any other byte depends on the code
    /// page the list was written in, which is not known, so a list that holds one is not read
    /// at all.</para>
    /// </remarks>
    /// <returns>
    /// The paths, which can be none. Null when the block is not a file list that can be read:
    /// it is shorter than its header, the names would start inside the header or beyond the end
    /// of the block, or it is a list of single bytes with a byte outside ASCII among the names
    /// that are read.
    /// </returns>
    public static IReadOnlyList<string>? FileList(byte[] block)
    {
        if (block.Length < DropFilesHeaderSize)
        {
            return null;
        }

        // Compared as the unsigned number it is, so that an offset of four thousand million is
        // beyond the end and not a negative position.
        uint start = BinaryPrimitives.ReadUInt32LittleEndian(block);
        if (start < DropFilesHeaderSize || start > (uint)block.Length)
        {
            return null;
        }

        bool wide = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(DropFilesWideAt)) != 0;
        int width = wide ? 2 : 1;
        ReadOnlySpan<byte> names = block.AsSpan((int)start);

        var paths = new List<string>();
        var name = new StringBuilder();
        bool tooLong = false;
        for (int at = 0; at + width <= names.Length && paths.Count <= ClipLimits.MaxFiles; at += width)
        {
            int character = wide ? BinaryPrimitives.ReadUInt16LittleEndian(names[at..]) : names[at];
            if (character != 0)
            {
                if (!wide && character >= 0x80)
                {
                    return null;
                }

                // Only as many characters as a path can have are kept. A name that goes on
                // beyond them is left out when it ends, and does not grow the memory used.
                if (name.Length < MaxNameLength)
                {
                    name.Append((char)character);
                }
                else
                {
                    tooLong = true;
                }

                continue;
            }

            if (name.Length == 0)
            {
                // A name with nothing in it is the zero character that ends the list.
                break;
            }

            string path = name.ToString();
            bool keep = !tooLong && FirstHalfPair(path) < 0 && IsOrdinaryPath(path);
            name.Clear();
            tooLong = false;
            if (keep)
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    /// <summary>
    /// The block that puts a file list on the clipboard: a DROPFILES header of 20 bytes that
    /// says the names start right after it and are UTF-16, then each path as UTF-16, little
    /// endian, with a zero character after it, then one more zero character. Reading it with
    /// <see cref="FileList"/> gives back every path that is an ordinary one, up to the limit
    /// on the number of paths.
    /// </summary>
    /// <remarks>
    /// A path that is empty or holds a zero character is left out. It cannot be written in this
    /// form: an empty name ends the list, and a zero character inside a name makes two names of
    /// it.
    /// </remarks>
    public static byte[] DropFiles(IReadOnlyList<string> paths)
    {
        long size = DropFilesHeaderSize + 2;
        foreach (string path in paths)
        {
            if (CanBeListed(path))
            {
                size += (path.Length + 1L) * 2;
            }
        }

        byte[] block = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(block, DropFilesHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(DropFilesWideAt), 1);

        int at = DropFilesHeaderSize;
        foreach (string path in paths)
        {
            if (!CanBeListed(path))
            {
                continue;
            }

            foreach (char character in path)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(at), character);
                at += 2;
            }

            // The zero character that ends the name is there already: a new array is all zeros.
            at += 2;
        }

        return block;
    }

    private static bool CanBeListed(string path) => path.Length > 0 && !path.Contains('\0');

    // A drive path, "C:\..." or "C:/...": an ASCII letter, a colon and a separator. Or a share
    // path, "\\server\share" and what follows: two separators, a server that is neither "." nor
    // "?" and holds no "@", then a share with at least one character. Windows takes "/" for "\"
    // everywhere in these, so each separator, the two at the start and the one that ends the
    // server, may be either, and "\\./pipe" is the device form as much as "\\.\pipe" is.
    // "\\.\..." and "\\?\..." are the forms for devices, pipes and the extended paths, and are
    // not ordinary files. A server with an "@" in its name ("\\host@SSL\DavWWWRoot\...",
    // "\\host@8080\share\...") is reached over HTTP by Windows' web client, and that is a
    // connection Beacon never makes, so such a path is not listed, not asked about and not put
    // back. So is a share named "DavWWWRoot" ("\\host\DavWWWRoot\..."), which Windows serves
    // over HTTP with no "@" at all. That rule is here, and not beside the rule for pipes and
    // devices, because this is what the file list and the paster both ask: the one for pipes
    // and devices is asked after it, by whoever is about to touch a path, and a list still
    // names a pipe (it only names it), while it must not name a web share. No other form is full. Pasting an entry asks this too, before it looks for a copied
    // file on disk or puts its path back on the clipboard, and then asks
    // <see cref="StandsForPipeOrDevice"/> as capture does.
    internal static bool IsOrdinaryPath(string path)
    {
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && IsSeparator(path[2]))
        {
            return true;
        }

        if (path.Length < 2 || !IsSeparator(path[0]) || !IsSeparator(path[1]))
        {
            return false;
        }

        int serverLength = path.AsSpan(2).IndexOfAny('\\', '/');
        if (serverLength <= 0)
        {
            // The server has no end, or nothing in it.
            return false;
        }

        var server = path.AsSpan(2, serverLength);
        int share = 2 + serverLength + 1;
        return !server.SequenceEqual(".")
            && !server.SequenceEqual("?")

            // A server name with an "@" in it ("host@SSL", "host@8080", "host@SSL@8080") is a
            // request to the web client service, and Windows serves that path over HTTP.
            && !server.Contains('@')
            && share < path.Length
            && !IsSeparator(path[share])
            && !IsWebShare(path.AsSpan(share));
    }

    // True when the share, the part of the text up to the next separator, is "DavWWWRoot": the
    // name that makes Windows' web client serve a path over HTTP, with no "@" in the server
    // name. Windows drops the dots and spaces that end a name, so "DavWWWRoot." and
    // "DavWWWRoot " are the same share, and the name is compared without them, in any case.
    private static bool IsWebShare(ReadOnlySpan<char> fromShare)
    {
        int end = fromShare.IndexOfAny('\\', '/');
        var share = (end < 0 ? fromShare : fromShare[..end]).TrimEnd(". ");
        return share.Equals("DavWWWRoot", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSeparator(char character) => character is '\\' or '/';

    // Two separators in front: a share, which is on another machine.
    private static bool IsSharePath(string path) => path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]);

    // The names Windows keeps for devices. They stand for the device whatever extension they
    // have, in front of any dot or colon, with or without spaces after them. (Before Windows 11
    // a file with such a name opens the device.)
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM\u00B9", "COM\u00B2", "COM\u00B3",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
    };

    /// <summary>
    /// True when the name stands for a pipe or a device and not for a file on a disk. Decided by
    /// the text alone, without touching what it names: a share whose name is "pipe", "mailslot"
    /// or "IPC$" (where the pipes of another machine are reached) is another program's pipe or
    /// mailslot, not a folder, and a file name that is a device's name (CON, NUL, COM1 and the
    /// rest, with any extension, and with a colon or spaces after it) is that device. Windows
    /// drops the dots and spaces at the end of a path's segment, so "pipe." and "pipe " are
    /// "pipe", and a name is compared without them.
    /// </summary>
    /// <remarks>
    /// This is the one rule, for everything that is about to ask Windows about a path from the
    /// clipboard or from a record: capture, before it opens a picture file, and the paster,
    /// before it asks whether a copied file is still there. A read from a pipe or a device can
    /// wait for ever, and even the question makes Windows go and look for the thing. A path
    /// that this says "no" to is no promise of an ordinary file; <see cref="FileList"/> keeps
    /// such a path in the list, which only names it, and <see cref="IsOrdinaryPath"/> decides
    /// which forms the list holds at all.
    /// </remarks>
    public static bool StandsForPipeOrDevice(string path)
    {
        if (IsSharePath(path))
        {
            string[] parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                string share = parts[1].TrimEnd('.', ' ');
                if (share.Equals("pipe", StringComparison.OrdinalIgnoreCase)
                    || share.Equals("mailslot", StringComparison.OrdinalIgnoreCase)
                    || share.Equals("IPC$", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        string name = path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
        int end = name.IndexOfAny(['.', ':']);
        string stem = (end < 0 ? name : name[..end]).TrimEnd('.', ' ');
        return DeviceNames.Contains(stem);
    }

    /// <returns>Where the first half of a surrogate pair with no partner stands, or -1 when the text has none.</returns>
    private static int FirstHalfPair(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                // A whole pair. Step over its second half as well.
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                return i;
            }
        }

        return -1;
    }
}
