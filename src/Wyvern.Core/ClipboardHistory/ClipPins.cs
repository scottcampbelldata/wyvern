using System.Text;
using System.Text.Json;

namespace Wyvern.Core.ClipboardHistory;

/// <summary>How pinning an entry went.</summary>
public enum PinResult
{
    /// <summary>The entry is pinned now.</summary>
    Pinned,

    /// <summary>It was pinned already. Nothing changed.</summary>
    AlreadyPinned,

    /// <summary>There are <see cref="ClipPins.MaxPins"/> pinned entries already. Nothing changed.</summary>
    TooMany,

    /// <summary>A file the entry names is no longer on this disk, so there is nothing whole to keep. Nothing changed.</summary>
    ContentGone,
}

/// <summary>
/// The entries of the clipboard history the owner has pinned: copies that stay until they are
/// unpinned, whatever becomes of the history they were taken from. An entry that is pinned is
/// written out again, with the files that hold its content, into a folder of its own beside
/// Wyvern's settings. The history's own rules do not reach there: days expire, Clear clears
/// and sync deletes without touching a pinned copy.
/// </summary>
/// <remarks>
/// The folder is on this machine only. It is not in the data folder, so sync never sends it,
/// and a pin made on one machine is not a pin on another. It holds what the owner copied:
/// nothing in it is logged, and what is reported is fixed words and counts. Use from one
/// thread only, the UI thread; pinning copies files, which for a large picture takes a moment.
/// </remarks>
public sealed class ClipPins
{
    /// <summary>The file in the folder that lists the pinned entries, one line each, the newest pin first.</summary>
    public const string ListName = "pins.jsonl";

    /// <summary>How many entries can be pinned.</summary>
    public const int MaxPins = 100;

    /// <summary>The device name in a line is at most this long. It is only ever shown.</summary>
    public const int MaxDeviceLength = 200;

    private const string NotRead = "The list of pinned clipboard entries could not be read";
    private const string LinesSkipped = "Lines of the list of pinned clipboard entries were not understood and are left out";
    private const string NotTidied = "A file of an unpinned clipboard entry could not be removed";

    private readonly string _folder;
    private readonly Action<string, Exception?>? _report;
    private List<ClipEntry>? _pins;

    /// <param name="folder">The folder the pinned copies are kept in. It is made when the first entry is pinned.</param>
    /// <param name="report">Receives fixed words, and an exception or a count, for the log. Never a name, a path or anything that was copied.</param>
    public ClipPins(string folder, Action<string, Exception?>? report = null)
    {
        _folder = folder;
        _report = report;
    }

    /// <summary>The pinned entries, the one pinned last first. Each says that its files are in the pins' folder.</summary>
    public IReadOnlyList<ClipEntry> All => Loaded();

    public bool IsPinned(string id) => Loaded().Exists(entry => entry.Id == id);

    /// <summary>
    /// Pins an entry: copies the files that hold its content into the pins' folder, and then
    /// writes it into the list. Either all of it is done or nothing is.
    /// </summary>
    /// <exception cref="IOException">A file could not be copied or the list could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The same, for want of rights.</exception>
    public PinResult Pin(ClipEntry entry)
    {
        var pins = Loaded();
        if (pins.Exists(pinned => pinned.Id == entry.Id))
        {
            return PinResult.AlreadyPinned;
        }

        if (pins.Count >= MaxPins)
        {
            return PinResult.TooMany;
        }

        var record = Record(entry);
        var names = record.FileNames().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Exists(name => !File.Exists(entry.PathOf(name))))
        {
            return PinResult.ContentGone;
        }

        Directory.CreateDirectory(_folder);
        var copied = new List<string>();
        try
        {
            foreach (string name in names)
            {
                string kept = Path.Combine(_folder, name);
                if (File.Exists(kept))
                {
                    // A file is named by the hash of what is in it: another pin has the same content.
                    continue;
                }

                // Under another name first, so that a copy that was cut short is never taken for the file.
                string unfinished = kept + ".tmp";
                File.Copy(entry.PathOf(name), unfinished, overwrite: true);
                File.Move(unfinished, kept, overwrite: true);
                copied.Add(kept);
            }

            var pinned = ClipEntry.From(record, entry.Device, entry.Day, _folder);
            var after = new List<ClipEntry>(pins.Count + 1) { pinned };
            after.AddRange(pins);
            Write(after);
            _pins = after;
            return PinResult.Pinned;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing half done is left behind: what this try copied goes again.
            foreach (string file in copied)
            {
                TryDelete(file);
            }

            throw;
        }
    }

    /// <summary>Unpins an entry: takes it out of the list, and then removes those of its files that no other pinned entry names.</summary>
    /// <returns>False when it was not pinned.</returns>
    /// <exception cref="IOException">The list could not be written. The entry is still pinned.</exception>
    /// <exception cref="UnauthorizedAccessException">The same, for want of rights.</exception>
    public bool Unpin(string id)
    {
        var pins = Loaded();
        int at = pins.FindIndex(entry => entry.Id == id);
        if (at < 0)
        {
            return false;
        }

        var gone = pins[at];
        var after = new List<ClipEntry>(pins);
        after.RemoveAt(at);
        Write(after);
        _pins = after;

        var stillNamed = new HashSet<string>(after.SelectMany(entry => Record(entry).FileNames()), StringComparer.OrdinalIgnoreCase);
        bool left = false;
        foreach (string name in Record(gone).FileNames())
        {
            if (!stillNamed.Contains(name) && !TryDelete(Path.Combine(_folder, name)))
            {
                left = true;
            }
        }

        if (left)
        {
            _report?.Invoke(NotTidied, null);
        }

        return true;
    }

    private List<ClipEntry> Loaded() => _pins ??= Read();

    private List<ClipEntry> Read()
    {
        var pins = new List<ClipEntry>();
        string list = Path.Combine(_folder, ListName);
        try
        {
            if (!File.Exists(list))
            {
                return pins;
            }

            int skipped = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in File.ReadLines(list, Encoding.UTF8))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                if (pins.Count < MaxPins && Parse(line) is { } entry && seen.Add(entry.Id))
                {
                    pins.Add(entry);
                }
                else
                {
                    skipped++;
                }
            }

            if (skipped > 0)
            {
                _report?.Invoke(FormattableString.Invariant($"{LinesSkipped}: {skipped}"), null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // What could be read is shown. The file is not written until the owner pins or
            // unpins something, and then with what is shown.
            _report?.Invoke(NotRead, ex);
        }

        return pins;
    }

    // One line: the machine the entry was copied on, and the entry as a day's file has it.
    private ClipEntry? Parse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("device", out var device)
                || device.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("entry", out var entry)
                || entry.ValueKind != JsonValueKind.Object
                || device.GetString() is not { Length: <= MaxDeviceLength } name
                || ClipLine.Parse(entry.GetRawText()) is not EntryRecord record)
            {
                return null;
            }

            return ClipEntry.From(record, name, DateOnly.FromDateTime(record.Time.UtcDateTime), _folder);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            // Not JSON, or text in it that cannot be read as text: one more line that is not understood.
            return null;
        }
    }

    private void Write(List<ClipEntry> pins)
    {
        Directory.CreateDirectory(_folder);
        var text = new StringBuilder();
        foreach (var entry in pins)
        {
            text.Append("{\"device\":")
                .Append(JsonSerializer.Serialize(entry.Device))
                .Append(",\"entry\":")
                .Append(ClipLine.Write(Record(entry)))
                .Append("}\n");
        }

        // Written beside the list and then put in its place, so that the list is never half written.
        string list = Path.Combine(_folder, ListName);
        string unfinished = list + ".tmp";
        File.WriteAllText(unfinished, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(unfinished, list, overwrite: true);
    }

    // The entry as a record again. An entry holds everything its record did, cut to what may be held.
    private static EntryRecord Record(ClipEntry entry) => new(entry.Id, entry.Time, entry.SourceApp)
    {
        Text = entry.Text,
        TextLength = entry.TextLength,
        TextFile = entry.TextFile,
        Html = entry.Html,
        Rtf = entry.Rtf,
        Image = entry.Image,
        Files = entry.Files,
        Pictures = entry.Pictures,
        Label = entry.Label,
    };

    private static bool TryDelete(string file)
    {
        try
        {
            File.Delete(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
