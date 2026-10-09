using System.Text.Json;

namespace Wyvern.Core.ClipboardHistory;

/// <summary>
/// Reads the text that stands in a picture. In the app it is the text reader that is part of
/// Windows, which works on this PC alone: no picture and no text leaves it.
/// </summary>
public interface IPictureTextReader
{
    /// <summary>
    /// The text in a picture file, or empty when there is none in it. Null when nothing can be
    /// read for now, as on a PC whose Windows has no text reader for the owner's language.
    /// Called on a background thread, one picture at a time.
    /// </summary>
    string? Read(string pictureFile);
}

/// <summary>
/// The text that stands in the pictures of the clipboard history, so that a screenshot can be
/// found by a word that is in it. Each picture is read once, in the background, and what was
/// read is kept by the name of the picture's file, which is the hash of its bytes.
/// </summary>
/// <remarks>
/// What is kept is the owner's, exactly as the pictures are. It is kept in one file beside the
/// settings, on this machine only: it is not in the data folder, so it is never synced, and
/// nothing of it is ever logged. It follows the history: the text of a picture that the
/// history no longer has is dropped at the next change, from memory and from the file, so
/// that an entry that was deleted leaves none of its words behind.
/// </remarks>
public sealed class ClipPictureText
{
    /// <summary>How much of the text of one picture is kept.</summary>
    public const int MaxTextLength = 10_000;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _file;
    private readonly IPictureTextReader _reader;
    private readonly Action<string, Exception?> _report;
    private readonly Action? _changed;
    private readonly Lock _gate = new();

    // By the picture's file name: what was read in it. Empty for a picture without text, and
    // for one that could not be read, so that neither is tried again.
    private readonly Dictionary<string, string> _texts;

    // The pictures the history has now, by name, with the file each is in.
    private Dictionary<string, string> _pictures = new(StringComparer.Ordinal);
    private Task _reading = Task.CompletedTask;
    private bool _busy;
    private bool _cannotRead;

    /// <param name="file">Where what was read is kept between runs.</param>
    /// <param name="report">Told in fixed words and counts what was done and what went wrong. Never a word of a picture.</param>
    /// <param name="changed">Told, on a background thread, when the text of more pictures is there to be found.</param>
    public ClipPictureText(string file, IPictureTextReader reader, Action<string, Exception?>? report = null, Action? changed = null)
    {
        _file = file;
        _reader = reader;
        _report = report ?? ((_, _) => { });
        _changed = changed;
        _texts = Load(file);
    }

    /// <summary>Done when nothing is being read. For tests, and for closing.</summary>
    public Task Idle
    {
        get
        {
            lock (_gate)
            {
                return _reading;
            }
        }
    }

    /// <summary>How many pictures have been looked at, with text in them or without.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _texts.Count;
            }
        }
    }

    /// <summary>What was read in the pictures of an entry, or null when nothing was, or nothing yet. Answers from memory.</summary>
    public string? Of(ClipEntry entry)
    {
        if (!entry.HasPicture)
        {
            return null;
        }

        lock (_gate)
        {
            string? found = null;
            foreach (var (name, _) in PicturesOf(entry))
            {
                if (_texts.TryGetValue(name, out string? text) && text.Length > 0)
                {
                    found = found is null ? text : found + "\n" + text;
                }
            }

            return found;
        }
    }

    /// <summary>
    /// Call with the entries there are, the pinned ones as well, whenever they have changed.
    /// The text of every picture that is no longer among them is dropped at once, and the
    /// pictures that have not been looked at yet are read in the background.
    /// </summary>
    public void Follow(IEnumerable<ClipEntry> entries)
    {
        var pictures = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            foreach (var (name, path) in PicturesOf(entry))
            {
                pictures.TryAdd(name, path);
            }
        }

        lock (_gate)
        {
            _pictures = pictures;
            int before = _texts.Count;
            foreach (string gone in _texts.Keys.Where(name => !pictures.ContainsKey(name)).ToList())
            {
                _texts.Remove(gone);
            }

            if (_texts.Count != before)
            {
                Save();
            }

            if (_busy || _cannotRead || pictures.Keys.All(_texts.ContainsKey))
            {
                return;
            }

            _busy = true;
            _reading = Task.Run(ReadWhatIsNew);
        }
    }

    // One picture after the other, for as long as the history has one that was not looked at.
    private void ReadWhatIsNew()
    {
        int read = 0;
        try
        {
            while (true)
            {
                string name;
                string path;
                lock (_gate)
                {
                    var next = _pictures.FirstOrDefault(picture => !_texts.ContainsKey(picture.Key));
                    if (next.Key is null || _cannotRead)
                    {
                        _busy = false;
                        if (read > 0)
                        {
                            Save();
                        }

                        break;
                    }

                    (name, path) = (next.Key, next.Value);
                }

                string text;
                try
                {
                    // An entry can outlive the file of its picture. There is nothing to read then, and nothing to report.
                    if (!File.Exists(path))
                    {
                        text = "";
                    }
                    else if (_reader.Read(path) is not { } found)
                    {
                        lock (_gate)
                        {
                            _cannotRead = true;
                        }

                        _report("Text in pictures is not read on this PC: Windows has no text reader for its language", null);
                        continue;
                    }
                    else
                    {
                        text = found.Trim();
                    }
                }
                catch (Exception ex)
                {
                    // A picture that cannot be read holds no text as far as the search goes, and is not tried again.
                    _report("A picture of the clipboard history could not be read for text", ex);
                    text = "";
                }

                lock (_gate)
                {
                    // The entry may have been deleted while its picture was read: its text is not kept.
                    if (_pictures.ContainsKey(name))
                    {
                        _texts[name] = text.Length > MaxTextLength ? text[..MaxTextLength] : text;
                        read++;
                    }
                    else
                    {
                        // What was not there to read is marked nowhere, so the loop cannot meet it again.
                        continue;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _busy = false;
            }

            _report("Reading the text in pictures stopped", ex);
        }

        if (read > 0)
        {
            _report(FormattableString.Invariant($"Text was looked for in {read} pictures of the clipboard history"), null);
            _changed?.Invoke();
        }
    }

    // The picture files of an entry, by name: the one from the clipboard, and those kept of copied files.
    private static IEnumerable<(string Name, string Path)> PicturesOf(ClipEntry entry)
    {
        if (entry.Image is { } image)
        {
            yield return (image, entry.PathOf(image));
        }

        foreach (var picture in entry.Pictures)
        {
            yield return (picture.File, entry.PathOf(picture.File));
        }
    }

    private static Dictionary<string, string> Load(string file)
    {
        try
        {
            if (File.Exists(file)
                && JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(file), JsonOptions) is { } stored)
            {
                return stored
                    .Where(pair => pair.Value is not null)
                    .ToDictionary(pair => pair.Key, pair => pair.Value!.Length > MaxTextLength ? pair.Value[..MaxTextLength] : pair.Value!, StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A file that cannot be read costs a second reading of the pictures, nothing more.
        }

        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    // Called with the gate held. Best effort: what cannot be written now is written at the next change.
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            string beside = _file + ".tmp";
            File.WriteAllText(beside, JsonSerializer.Serialize(_texts, JsonOptions));
            File.Move(beside, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _report("What was read in the pictures of the clipboard history could not be saved", ex);
        }
    }
}
