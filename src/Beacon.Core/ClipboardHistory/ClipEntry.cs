using Beacon.Core.Platform;

namespace Beacon.Core.ClipboardHistory;

/// <summary>
/// One entry of the clipboard history as it is held in memory. What is held is bounded whatever
/// was copied: the times and names, the start of the text, the paths of copied files and the
/// names of the files on disk that hold everything else. The whole of a long text, HTML, rich
/// text and pictures stay on disk until the entry is previewed, pasted or saved.
/// </summary>
/// <param name="Device">The folder of the machine it was copied on.</param>
/// <param name="Day">The day of the file its record is in.</param>
/// <param name="Folder">The full path of that day's folder, where the files this entry names are.</param>
public sealed record ClipEntry(string Id, DateTimeOffset Time, string Device, DateOnly Day, string Folder)
{
    /// <summary>What an entry's id starts with wherever it is used as an item id, so that the log can tell it is one and withhold it.</summary>
    public const string IdPrefix = "clipboard:";

    /// <summary>How much of a text sits in its record and in memory. A longer text is whole in a file of its own.</summary>
    public const int MaxTextStart = 100_000;

    /// <summary>The longest name of a program that is kept. It is only ever shown.</summary>
    public const int MaxSourceAppLength = 100;

    /// <summary>The program it was copied from, such as "chrome.exe", or null when that is not known. It is shown in the view and never logged.</summary>
    public string? SourceApp { get; init; }

    /// <summary>The text, or its first <see cref="MaxTextStart"/> characters. Null when no text was copied.</summary>
    public string? Text { get; init; }

    /// <summary>How many characters the whole text has.</summary>
    public int TextLength { get; init; }

    /// <summary>The file that holds the whole text when it is longer than its start, otherwise null.</summary>
    public string? TextFile { get; init; }

    /// <summary>The file that holds the clipboard's HTML, or null.</summary>
    public string? Html { get; init; }

    /// <summary>The file that holds the clipboard's rich text, or null.</summary>
    public string? Rtf { get; init; }

    /// <summary>The file that holds the clipboard's picture as PNG, or null.</summary>
    public string? Image { get; init; }

    /// <summary>The full paths of the files that were copied, at most <see cref="ClipLimits.MaxFiles"/>.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>Those of the copied files that are pictures and whose content was kept, at most <see cref="ClipLimits.MaxPictures"/>.</summary>
    public IReadOnlyList<KeptPicture> Pictures { get; init; } = [];

    /// <summary>The address, when the text is one web address and nothing else. Otherwise null.</summary>
    public string? Link { get; init; }

    /// <summary>For a link, the words it had on the page it was copied from, as <see cref="ClipLinks.ShownLabel"/> makes them fit to show, or null. A record whose label fails that rule gives null.</summary>
    public string? Label { get; init; }

    /// <summary>The id as an item id, for the log, where <c>LogSafe.ItemId</c> withholds it.</summary>
    public string ItemId => IdPrefix + Id;

    /// <summary>True when there is a picture to show or to save: one from the clipboard, or one kept from a copied file.</summary>
    public bool HasPicture => Image is not null || Pictures.Count > 0;

    /// <summary>The full path of one of the files this entry names.</summary>
    public string PathOf(string name) => Path.Combine(Folder, name);

    /// <summary>
    /// A short fixed form: the type's name and how many files and pictures the entry has. The
    /// printed form of a record holds all of its members, and this one holds what the owner
    /// copied, the program and the paths, none of which may come out wherever it is formatted
    /// into a string.
    /// </summary>
    public override string ToString() =>
        FormattableString.Invariant($"{nameof(ClipEntry)} {{ Files = {Files.Count}, Pictures = {Pictures.Count} }}");

    /// <summary>
    /// The entry for a record, cut down to what may be held in memory. A record written by this
    /// code is within every bound already; one that came from elsewhere may not be.
    /// </summary>
    public static ClipEntry From(EntryRecord record, string device, DateOnly day, string folder)
    {
        string? text = record.Text is null ? null : Start(record.Text);
        string? link = record.TextFile is null ? ClipLinks.Address(text) : null;
        return new ClipEntry(record.Id, record.Time, device, day, folder)
        {
            SourceApp = Shown(record.SourceApp, MaxSourceAppLength),
            Text = text,
            TextLength = Math.Max(record.TextLength, text?.Length ?? 0),
            TextFile = record.TextFile,
            Html = record.Html,
            Rtf = record.Rtf,
            Image = record.Image,
            Files = record.Files.Count > ClipLimits.MaxFiles ? record.Files.Take(ClipLimits.MaxFiles).ToList() : record.Files,
            Pictures = record.Pictures.Count > ClipLimits.MaxPictures ? record.Pictures.Take(ClipLimits.MaxPictures).ToList() : record.Pictures,
            Link = link,
            Label = link is null ? null : ClipLinks.ShownLabel(record.Label),
        };
    }

    /// <summary>
    /// True when the other entry holds the same forms with the same content. Short text is
    /// compared as it is; everything on disk is compared by the name of its file, which is the
    /// hash of its bytes. Where and when it was copied does not count.
    /// </summary>
    public bool SameContentAs(ClipEntry other) =>
        Text == other.Text
        && TextLength == other.TextLength
        && TextFile == other.TextFile
        && Html == other.Html
        && Rtf == other.Rtf
        && Image == other.Image
        && Files.SequenceEqual(other.Files)
        && Pictures.SequenceEqual(other.Pictures);

    /// <summary>The first <see cref="MaxTextStart"/> characters, never ending on half of a character that takes two.</summary>
    public static string Start(string text)
    {
        if (text.Length <= MaxTextStart)
        {
            return text;
        }

        int keep = MaxTextStart;
        if (char.IsHighSurrogate(text[keep - 1]))
        {
            keep--;
        }

        return text[..keep];
    }

    // A program's name is shown on screen. It can come from a file another machine wrote, so it
    // is cleaned by the one cleaner of text that is shown: one line, and nothing that shows
    // nothing or turns the text around. (A link's label has a rule of its own on top of that,
    // ClipLinks.ShownLabel, which capture and the view use as well.)
    private static string? Shown(string? text, int maxLength)
    {
        if (text is null)
        {
            return null;
        }

        string shown = NoteText.OnScreen(text, maxLength);
        return shown.Length == 0 ? null : shown;
    }
}
