using System.Runtime.CompilerServices;
using System.Text;
using Beacon.Core.Platform;

namespace Beacon.Core.ClipboardHistory;

/// <summary>
/// Pastes an entry of the clipboard history. Every form the entry was copied in goes back on
/// the clipboard together, so that the program pasted into takes the richest one it
/// understands. The forms go back in a fixed order, the most descriptive first (rich text,
/// HTML, the picture, the list of files, the plain text), and not in the order the program
/// they were copied from had put them in, which is not kept. A program that takes the first
/// form it knows may therefore take another one than it would have straight after the copy.
/// The entry stays on the clipboard afterwards.
/// </summary>
/// <remarks>
/// <para>This is where an entry's content is read from disk: the whole of a long text, HTML,
/// rich text and pictures are not in memory. It runs when the owner presses Enter, never while
/// they type, and on the UI thread. A form whose file has gone, holds nothing or cannot be
/// read is left out and the rest is pasted.</para>
/// <para>Whether the files an entry lists are still where they were is the one question that
/// can take long, when a path is on a share that cannot be reached. It is asked on another
/// thread and given <see cref="FileCheckLimit"/>; without an answer by then the files are left
/// out, and when nothing else of the entry can be pasted the owner is told that they could not
/// be checked in time (<see cref="FilesNotCheckedNote"/>), not that they are gone.</para>
/// <para>Nothing that is read here is logged or shown in a notification. What goes to the log
/// is that a file, or a picture, could not be read, in fixed words that name nothing.</para>
/// </remarks>
public sealed class ClipPaster
{
    /// <summary>Shown when nothing of an entry is left to paste.</summary>
    public const string NothingLeftNote = "This entry can no longer be pasted: what it held is not on this machine.";

    /// <summary>Shown when an entry was a list of files and none of them is where it was.</summary>
    public const string FilesGoneNote = "The files of this entry are no longer where they were, so there is nothing to paste.";

    /// <summary>
    /// Shown when an entry was a list of files and nothing else of it could be pasted, and the
    /// check of whether the files are there ran out of time. They may be there and only slow to
    /// answer, so this does not say they are gone.
    /// </summary>
    public const string FilesNotCheckedNote = "The files of this entry could not be checked in time, so they were left out.";

    /// <summary>
    /// How long the check of an entry's copied files may take in all. A path on a share that
    /// cannot be reached keeps Windows waiting for many seconds, and the paste must not wait
    /// with it: the launcher is hidden by then and the owner is back in another window.
    /// </summary>
    public static readonly TimeSpan FileCheckLimit = TimeSpan.FromSeconds(1.5);

    private readonly IPasteService _paste;
    private readonly IImageCodec _codec;
    private readonly Action<string> _notify;
    private readonly Action<string, Exception?> _report;
    private readonly Func<string, bool> _exists;
    private readonly TimeSpan _fileCheckLimit;
    private readonly Action<Task>? _checkStarted;

    /// <param name="notify">Shows one of this class's fixed sentences to the owner.</param>
    /// <param name="report">Receives fixed words and an exception for the log.</param>
    /// <param name="exists">
    /// Says whether a copied file or folder is still where it was. It is called on another
    /// thread than the one that pastes, once for each path. Left out, it asks the disk
    /// (<see cref="Path.Exists"/>); a test passes its own.
    /// </param>
    /// <param name="fileCheckLimit">How long the check of all of an entry's files may take. Left out, <see cref="FileCheckLimit"/>.</param>
    /// <param name="checkStarted">
    /// Handed the task that checks an entry's files as soon as it has been started, so that a
    /// test can wait for a check that was given up on and count what it asked. Left out, nobody
    /// is told.
    /// </param>
    public ClipPaster(
        IPasteService paste,
        IImageCodec codec,
        Action<string> notify,
        Action<string, Exception?>? report = null,
        Func<string, bool>? exists = null,
        TimeSpan? fileCheckLimit = null,
        Action<Task>? checkStarted = null)
    {
        _paste = paste;
        _codec = codec;
        _notify = notify;
        _report = report ?? ((_, _) => { });
        _exists = exists ?? Path.Exists;
        _fileCheckLimit = fileCheckLimit ?? FileCheckLimit;
        _checkStarted = checkStarted;
    }

    /// <summary>
    /// Puts the entry on the clipboard and pastes it into the window the owner was in. When
    /// nothing of the entry can be put on the clipboard, nothing is touched and the owner is
    /// told why.
    /// </summary>
    /// <exception cref="ClipboardBusyException">The clipboard could not be opened. Nothing has been pasted.</exception>
    public void Paste(ClipEntry entry)
    {
        var blocks = Blocks(entry, out bool filesNotChecked);
        if (blocks.Count == 0)
        {
            // A list of files whose check ran out of time is not told as files that are gone:
            // they may be only slow, and the owner is not to give up on files that are there.
            _notify(entry.Files.Count == 0 ? NothingLeftNote : filesNotChecked ? FilesNotCheckedNote : FilesGoneNote);
            return;
        }

        _paste.PasteAndKeep(blocks);
    }

    /// <summary>
    /// Puts the entry on the clipboard and leaves it there, without pasting it anywhere. When
    /// nothing of the entry can be put on the clipboard, nothing is touched and the owner is
    /// told why, in the words a paste uses.
    /// </summary>
    /// <returns>True when the entry is on the clipboard now. False when there was nothing of it to put there, which the owner has been told.</returns>
    /// <exception cref="ClipboardBusyException">The clipboard could not be opened. Nothing has changed.</exception>
    /// <summary>Shown when an entry that has no text was to be pasted as text.</summary>
    public const string NoTextNote = "This entry has no text to paste.";

    /// <summary>
    /// Pastes the entry's text and nothing else of it: no formatting, no picture, no files.
    /// The text stays on the clipboard afterwards, as a pasted entry does. An entry without
    /// text is not pasted, and the owner is told.
    /// </summary>
    public void PasteText(ClipEntry entry)
    {
        if (Text(entry) is not { Length: > 0 } text)
        {
            _notify(NoTextNote);
            return;
        }

        _paste.PasteAndKeep([new ClipboardBlock(ClipboardFormat.UnicodeText, ClipboardBytes.TextBlock(text))]);
    }

    /// <summary>The whole text of an entry, read from its file when it is a long one, or null when the entry has no text.</summary>
    public string? Text(ClipEntry entry) => WholeText(entry, new ContentFiles(entry, _report));

    public bool Copy(ClipEntry entry)
    {
        var blocks = Blocks(entry, out bool filesNotChecked);
        if (blocks.Count == 0)
        {
            _notify(entry.Files.Count == 0 ? NothingLeftNote : filesNotChecked ? FilesNotCheckedNote : FilesGoneNote);
            return false;
        }

        _paste.Keep(blocks);
        return true;
    }

    /// <summary>
    /// What goes on the clipboard for an entry, the richest form first: rich text, HTML, the
    /// picture (as PNG and as the plain picture every program reads), the list of files, and
    /// the plain text. A file list holds only the paths that still exist. When none does, or
    /// they could not be found in time, and no picture of the entry's own could be put on the
    /// clipboard, the first picture that was kept from those files and can still be read goes
    /// on the clipboard as a picture instead.
    /// </summary>
    public IReadOnlyList<ClipboardBlock> Blocks(ClipEntry entry) => Blocks(entry, out _);

    // filesNotChecked: true when the entry lists files that are asked about and the check ran
    // out of time, so that the list was left out for that reason and not because the files
    // are gone.
    private IReadOnlyList<ClipboardBlock> Blocks(ClipEntry entry, out bool filesNotChecked)
    {
        var content = new ContentFiles(entry, _report);
        var blocks = new List<ClipboardBlock>();
        if (content.Read(entry.Rtf, ClipPaths.RtfExtension, ClipLimits.MaxTextBytes) is { } rtf)
        {
            blocks.Add(new ClipboardBlock(ClipboardFormat.Rtf, ClipboardBytes.WithZero(rtf)));
        }

        if (content.Read(entry.Html, ClipPaths.HtmlExtension, ClipLimits.MaxTextBytes) is { } html)
        {
            blocks.Add(new ClipboardBlock(ClipboardFormat.Html, ClipboardBytes.WithZero(html)));
        }

        bool hasPicture = false;
        if (content.Read(entry.Image, ClipPaths.ImageExtension, ClipLimits.MaxImageBytes) is { } png && Dib(png) is { } imageDib)
        {
            blocks.Add(new ClipboardBlock(ClipboardFormat.Png, png));
            blocks.Add(new ClipboardBlock(ClipboardFormat.Dib, imageDib));
            hasPicture = true;
        }

        var files = FilesStillThere(entry.Files, out filesNotChecked);
        if (files.Count > 0)
        {
            blocks.Add(new ClipboardBlock(ClipboardFormat.FileDrop, ClipboardBytes.DropFiles(files)));
            blocks.Add(new ClipboardBlock(ClipboardFormat.DropEffect, ClipboardBytes.CopyEffect()));
        }
        else if (!hasPicture)
        {
            // What counts is whether a picture went on the clipboard above, not whether the
            // record names one: the entry's own picture file can have gone, or be past reading.
            foreach (var picture in entry.Pictures)
            {
                if (content.ReadPicture(picture.File, ClipLimits.MaxImageBytes) is { } bytes && Dib(bytes) is { } pictureDib)
                {
                    blocks.Add(new ClipboardBlock(ClipboardFormat.Dib, pictureDib));
                    break;
                }
            }
        }

        if (WholeText(entry, content) is { Length: > 0 } text)
        {
            blocks.Add(new ClipboardBlock(ClipboardFormat.UnicodeText, ClipboardBytes.TextBlock(text)));
        }

        return blocks;
    }

    // The whole text. A long one is in a file of its own; when that file has gone, the start
    // of it that the history holds is better than nothing. The file is the whole text only when
    // it holds at least as many characters as that start: one that holds fewer was cut short or
    // written over, and the start in memory is then more of the text than the file is.
    private static string? WholeText(ClipEntry entry, ContentFiles content)
    {
        if (content.Read(entry.TextFile, ClipPaths.TextExtension, ClipLimits.MaxTextBytes) is { } bytes)
        {
            string whole = Encoding.UTF8.GetString(bytes);
            if (whole.Length >= (entry.Text?.Length ?? 0))
            {
                return whole;
            }
        }

        return entry.Text;
    }

    /// <summary>
    /// The files that hold an entry's content, for the length of one paste. A name comes from a
    /// record, which another machine or anything else may have written, so a file is read only
    /// under a name the history gives to a file of the very form that is asked for: the hash
    /// and that form's extension, as the reader of day files demands. A record's rich text
    /// cannot name a picture, nor its picture a text, and no name can point outside the day's
    /// folder.
    /// </summary>
    /// <param name="report">Receives fixed words and an exception for the log.</param>
    private sealed class ContentFiles(ClipEntry entry, Action<string, Exception?> report)
    {
        // A paste says once that a file could not be read, however many could not.
        private bool _reported;

        /// <summary>The bytes of the file that holds one form of the entry.</summary>
        /// <param name="name">The name the record gives, or null when the entry does not have this form.</param>
        /// <param name="extension">The extension a file of this form has, lower case with its dot.</param>
        /// <returns>Null when the entry does not have this form, the name is not one for it, or the file gives nothing (see <see cref="ReadFile"/>).</returns>
        public byte[]? Read(string? name, string extension, long maxBytes) =>
            name is not null && ClipPaths.IsBlobName(name, extension) ? ReadFile(name, maxBytes) : null;

        /// <summary>The bytes of a picture that was kept from a copied file, under any of the extensions a kept picture has.</summary>
        public byte[]? ReadPicture(string name, long maxBytes) =>
            ClipPaths.IsPictureName(name) ? ReadFile(name, maxBytes) : null;

        /// <param name="name">A name that has been checked: one the history gives to a file of the form that is wanted.</param>
        /// <returns>
        /// The file's bytes, or null when the file has gone, holds nothing, is larger than it
        /// can be, or cannot be read. A file of no bytes counts as gone: nothing that was
        /// copied is stored as nothing, so such a file has lost what it held.
        /// </returns>
        private byte[]? ReadFile(string name, long maxBytes)
        {
            try
            {
                var file = new FileInfo(entry.PathOf(name));
                if (!file.Exists || file.Length == 0 || file.Length > maxBytes)
                {
                    return null;
                }

                // The file can have changed since its length was asked for, so what counts is
                // what was read.
                byte[] bytes = File.ReadAllBytes(file.FullName);
                return bytes.Length > 0 && bytes.Length <= maxBytes ? bytes : null;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // It went between the look and the read. That is a file that has gone.
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                // The file is there and the paste goes without it: another program holds it, or
                // Beacon may not read it. Unlike a file that has gone, that is worth a line in
                // the log. The words are fixed and hold no name and no path; the exception is
                // handed on as it is.
                if (!_reported)
                {
                    _reported = true;
                    report("A file of a clipboard entry could not be read, so it is pasted without it", ex);
                }

                return null;
            }
        }
    }

    // A picture file that cannot be decoded (it is damaged, or is not a picture at all) is left
    // out like a file that has gone.
    private byte[]? Dib(byte[] picture)
    {
        try
        {
            return _codec.ToDib(picture);
        }
        catch (Exception ex)
        {
            _report("A picture of a clipboard entry could not be read, so it is pasted without it", ex);
            return null;
        }
    }

    /// <summary>
    /// Those of the copied files and folders that are still where they were, in their order.
    /// Each path is asked about once, on another thread, and the whole list has the limit this
    /// paster was made with (<see cref="FileCheckLimit"/> in the app) to answer in. Windows can
    /// take many seconds to say that a path on a share cannot be reached, and this is called on
    /// the UI thread after the launcher has hidden: waiting that long would freeze it, and then
    /// paste into whichever window the owner has gone on to.
    /// </summary>
    /// <param name="timedOut">True when the answer did not come in time.</param>
    /// <returns>
    /// None when the answer did not come in time. The files are then left out, as when none of
    /// them exists, and the answer is ignored when it does come. The owner is told that the
    /// files could not be checked in time, which is not the same as that they are gone.
    /// </returns>
    private IReadOnlyList<string> FilesStillThere(IReadOnlyList<string> paths, out bool timedOut)
    {
        timedOut = false;
        // Only an ordinary path is asked about at all, by the rule capture has for a file list:
        // a drive path or a share path. A record can come from another machine, or from
        // anything that can write a file, and name a device, a pipe or an extended path
        // ("\\.\...", "\\?\..."), or a server that Windows reaches over HTTP ("\\host@SSL\..."):
        // even asking whether such a thing exists makes Windows go and look for it, and its
        // path must not go back on the clipboard for a program to open. A relative path, which
        // means a different file wherever a program runs, is none either. Nor is a name that
        // stands for a pipe or a device, as capture does not touch one: this asks Windows about
        // no path that capture would refuse to open.
        var candidates = paths
            .Where(path => ClipboardBytes.IsOrdinaryPath(path) && !ClipboardBytes.StandsForPipeOrDevice(path))
            .ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        // Set when the wait below gives up. The check cannot be broken off inside the question
        // it is asking, but it asks no further ones: a thousand paths on a share that is not
        // there would otherwise keep a thread busy for hours.
        var givenUp = new StrongBox<bool>();
        var exists = _exists;
        var check = Task.Run(() =>
        {
            var found = new List<string>();
            foreach (string path in candidates)
            {
                if (Volatile.Read(ref givenUp.Value))
                {
                    break;
                }

                if (IsThere(exists, path))
                {
                    found.Add(path);
                }
            }

            return found;
        });
        _checkStarted?.Invoke(check);

        // The check never throws, so the wait only says whether it finished in time.
        if (check.Wait(_fileCheckLimit))
        {
            return check.Result;
        }

        Volatile.Write(ref givenUp.Value, true);
        timedOut = true;
        return [];
    }

    // Runs on the checking thread, where nobody is left to hear of a failure once the paste has
    // stopped waiting. A question that cannot be answered counts as "not there".
    private static bool IsThere(Func<string, bool> exists, string path)
    {
        try
        {
            return exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
