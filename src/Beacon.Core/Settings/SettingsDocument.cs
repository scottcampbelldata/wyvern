using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Beacon.Core.Settings;

/// <summary>
/// settings.json as something that can be changed and written back: what the settings window
/// reads its fields from and saves them to. Only the settings that are set through it change;
/// every other one in the file, also one this version of Beacon does not know, is written back
/// as it was. Comments in the file are not kept: JSON that is written has none.
/// </summary>
/// <remarks>
/// What a setting means is not decided here. The file is read again by <see cref="SettingsStore"/>
/// when Beacon reloads, and a value that cannot be used is noted there as it always was.
/// </remarks>
public sealed class SettingsDocument
{
    private static readonly JsonDocumentOptions Reading = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // Written to be read by its owner: a plus sign in a hotkey and a letter with an accent in a
    // folder's name stay as they are.
    private static readonly JsonSerializerOptions Writing = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly JsonObject _json;

    private SettingsDocument(JsonObject json) => _json = json;

    /// <summary>A document with nothing set, for a settings.json that does not exist yet.</summary>
    public static SettingsDocument Empty() => new([]);

    /// <summary>
    /// Reads the file. Null when it cannot be read or is not a JSON object: the settings window
    /// then says so and changes nothing, because writing the file would throw away whatever
    /// the owner had in it. A file that does not exist is an empty document.
    /// </summary>
    public static SettingsDocument? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Empty();
            }

            return JsonNode.Parse(File.ReadAllText(path), documentOptions: Reading) is JsonObject json ? new SettingsDocument(json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>A setting that is text, or the fallback when it is not set or is not text.</summary>
    public string Text(string key, string fallback = "") =>
        Find(key) is JsonValue value && value.TryGetValue(out string? text) && text is not null ? text : fallback;

    /// <summary>A setting that is true or false, or the fallback when it is not set or is neither.</summary>
    public bool Flag(string key, bool fallback) =>
        Find(key) is JsonValue value && value.TryGetValue(out bool flag) ? flag : fallback;

    /// <summary>A setting that is a whole number, or the fallback when it is not set or is not one.</summary>
    public int Number(string key, int fallback) =>
        Find(key) is JsonValue value && value.TryGetValue(out double number) && number == Math.Floor(number) && Math.Abs(number) <= int.MaxValue
            ? (int)number
            : fallback;

    /// <summary>A setting that is a list of texts, or null when it is not set or is not a list. Entries that are not text are left out.</summary>
    public IReadOnlyList<string>? List(string key)
    {
        if (Find(key) is not JsonArray array)
        {
            return null;
        }

        var texts = new List<string>();
        foreach (var entry in array)
        {
            if (entry is JsonValue value && value.TryGetValue(out string? text) && text is not null)
            {
                texts.Add(text);
            }
        }

        return texts;
    }

    /// <summary>A setting that is a list of names with a text for each, or null when it is not set or is not one. Names whose value is not text are left out.</summary>
    public IReadOnlyDictionary<string, string>? Map(string key)
    {
        if (Find(key) is not JsonObject named)
        {
            return null;
        }

        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in named)
        {
            if (entry.Value is JsonValue value && value.TryGetValue(out string? text) && text is not null)
            {
                texts[entry.Key] = text;
            }
        }

        return texts;
    }

    /// <summary>Sets a setting to a list of names with a text for each. Null, or a list with nothing in it, takes the setting out of the file.</summary>
    public void SetMap(string key, IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || values.Count == 0)
        {
            Remove(key);
            return;
        }

        var named = new JsonObject();
        foreach (var (name, text) in values)
        {
            named[name] = text;
        }

        _json[NameOf(key)] = named;
    }

    /// <summary>Sets a setting to a text. Null, or text with nothing in it, takes the setting out of the file, so that its default applies.</summary>
    public void Set(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Remove(key);
        }
        else
        {
            _json[NameOf(key)] = value.Trim();
        }
    }

    public void Set(string key, bool value) => _json[NameOf(key)] = value;

    public void Set(string key, int value) => _json[NameOf(key)] = value;

    /// <summary>Sets a setting to a list of texts. Null takes the setting out of the file, so that its default applies; an empty list is written as one.</summary>
    public void Set(string key, IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            Remove(key);
            return;
        }

        var array = new JsonArray();
        foreach (string value in values)
        {
            array.Add(value);
        }

        _json[NameOf(key)] = array;
    }

    /// <summary>
    /// Writes the document to the file: first beside it under another name, then in its place,
    /// so that a failure half-way leaves the file as it was.
    /// </summary>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be written.</exception>
    public void Write(string path)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(folder);
        string beside = Path.Combine(folder, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp");
        try
        {
            File.WriteAllText(beside, _json.ToJsonString(Writing));
            File.Move(beside, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(beside))
            {
                File.Delete(beside);
            }
        }
    }

    // A setting's name is read whatever the case of its letters, as SettingsStore reads it.
    private JsonNode? Find(string key) => _json.TryGetPropertyValue(NameOf(key), out var value) ? value : null;

    private void Remove(string key) => _json.Remove(NameOf(key));

    // The name as the file has it, when it has the setting under another case; otherwise as given.
    private string NameOf(string key)
    {
        foreach (var property in _json)
        {
            if (string.Equals(property.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return property.Key;
            }
        }

        return key;
    }
}
