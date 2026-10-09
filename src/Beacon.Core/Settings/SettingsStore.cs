using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Beacon.Core.AutoExpand;
using Beacon.Core.ClipboardHistory;
using Beacon.Core.Files;
using Beacon.Core.Scripts;
using Beacon.Core.Sync;

namespace Beacon.Core.Settings;

/// <summary>Per-machine settings, stored as settings.json.</summary>
public sealed class BeaconSettings
{
    public const string DefaultHotkey = "Alt+Space";

    public string Hotkey { get; set; } = DefaultHotkey;

    public bool StartWithWindows { get; set; } = true;

    /// <summary>Full paths of the folders whose file and folder names are indexed. Null means Documents, Desktop and Downloads.</summary>
    public List<string>? IndexedFolders { get; set; }

    /// <summary>Folder names that are never indexed or entered. Null means the built-in list.</summary>
    public List<string>? ExcludedFolderNames { get; set; }

    /// <summary>
    /// Full path of the data folder, which holds the library. Null means the "data" folder
    /// beside settings.json. It is left out of the defaults file, so the default keeps
    /// following wherever settings.json is.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DataDir { get; set; }

    /// <summary>
    /// Which program runs each kind of script: an object whose keys are a script file's
    /// extension and whose values are the program followed by its arguments, as a list of text
    /// or as one text. Null, or no entry for an extension, means the built-in program for it.
    /// An entry for .cmd or .bat is never used: batch files always run on the built-in line.
    /// It is left out of the defaults file, because the built-in program for .ps1 depends on
    /// what is installed when a script runs. It is kept as the JSON that was written, and
    /// <see cref="ScriptOptions"/> works out what it means: a value of the wrong kind is noted
    /// there and does not make the whole of settings.json unreadable.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Interpreters { get; set; }

    /// <summary>
    /// How many seconds a script may run before it is stopped: a whole number from 1 to 86400.
    /// Null means 300. It is kept as the JSON that was written for the same reason as
    /// <see cref="Interpreters"/>: "60.0" and "60" are not a reason to refuse the whole file.
    /// </summary>
    public JsonElement? ScriptTimeoutSeconds { get; set; }

    /// <summary>
    /// What this machine is called in clipboard history, which is kept in one folder for each
    /// machine. Null means the machine's own name. It is left out of the defaults file, so the
    /// default keeps following the machine's name. It is kept as the JSON that was written, and
    /// <see cref="ClipboardOptions"/> works out what it means: a value of the wrong kind is
    /// noted there and does not make the whole of settings.json unreadable.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? DeviceName { get; set; }

    /// <summary>
    /// How many days of clipboard history are kept: a whole number from 1 to 3650. Null means
    /// 90. It is kept as the JSON that was written for the same reason as
    /// <see cref="DeviceName"/>: "30.0" and "30" are not a reason to refuse the whole file.
    /// </summary>
    public JsonElement? ClipboardRetentionDays { get; set; }

    /// <summary>
    /// The programs that clipboard history never records from, as a list of file names such as
    /// "keepass.exe". Null means the built-in list of password managers, and a list replaces
    /// it, even an empty one. It is kept as the JSON that was written for the same reason as
    /// <see cref="DeviceName"/>: one entry of the wrong kind is left out by
    /// <see cref="ClipboardOptions"/>, and the rest of the list and of the file are still read.
    /// </summary>
    public JsonElement? ClipboardExcludedApps { get; set; }

    /// <summary>
    /// The hotkey for clipboard history, written in the same way as <see cref="Hotkey"/>. Null
    /// means it has none. It is left out of the defaults file, because there is no hotkey for
    /// it until the owner chooses one. It is kept as the JSON that was written for the same
    /// reason as <see cref="DeviceName"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ClipboardHotkey { get; set; }

    /// <summary>
    /// How many minutes pass between two sync runs: a whole number from 1 to 1440. Null means 5.
    /// It is kept as the JSON that was written, and <see cref="SyncOptions"/> works out what it
    /// means, for the same reason as <see cref="DeviceName"/>. Where sync connects to is not
    /// here: that is in sync.json, which Sync Setup writes.
    /// </summary>
    public JsonElement? SyncIntervalMinutes { get; set; }

    /// <summary>
    /// Whether a snippet's keyword is replaced by the snippet wherever it is typed: true or
    /// false. Null means false. While it is true Beacon has a keyboard hook installed. It is
    /// kept as the JSON that was written, and <see cref="AutoExpandOptions"/> works out what it
    /// means, for the same reason as <see cref="DeviceName"/>: a value of the wrong kind is
    /// noted there, leaves auto-expand off, and does not make the whole of settings.json
    /// unreadable.
    /// </summary>
    public JsonElement? SnippetAutoExpand { get; set; }

    /// <summary>
    /// Commands and the hotkeys that run them from anywhere, such as
    /// <c>{ "window-left-half": "Ctrl+Alt+Left" }</c>. Not set for no command hotkeys; the
    /// file Beacon first writes leaves it out. <see cref="CommandHotkeyOptions"/> reads it,
    /// and it is held as it was written so that a mistake in it can be counted and said.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? CommandHotkeys { get; set; }

    /// <summary>
    /// Whether Beacon's windows are light or dark: "system", "light" or "dark". Not set for
    /// following Windows; the file Beacon first writes leaves it out. <see cref="AppearanceOptions"/>
    /// reads it, and it is held as it was written so that a mistake in it can be said.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Appearance { get; set; }
}

/// <param name="Error">
/// Null on success. Otherwise a sentence for the owner, made of fixed text and at most a line
/// number, so it is safe to show in a notification; <paramref name="Settings"/> then holds defaults.
/// </param>
/// <param name="Cause">The exception behind <paramref name="Error"/>, for the log. Its message is never shown or logged.</param>
public sealed record SettingsLoadResult(BeaconSettings Settings, string? Error, Exception? Cause = null);

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // The file is edited by hand, so "Hotkey" must work as well as "hotkey".
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        // The default encoder writes "+" as a Unicode escape, which would make "Alt+Space" unreadable in a hand-edited file.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Reads settings.json, writing a file of defaults first if none exists. The app never
    /// rewrites an existing file, so hand edits and keys from newer versions are preserved.
    /// A file that is not valid JSON, or cannot be read or created, is left untouched and
    /// reported through the result. This method never throws for a problem with the file.
    /// </summary>
    public static SettingsLoadResult Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                // The lists and the timeout are written out in full so the owner can see what to edit.
                // Auto-expand is written as false, so that the owner sees the switch and that it is off.
                var defaults = new BeaconSettings
                {
                    IndexedFolders = [.. FileIndexOptions.DefaultFolders()],
                    ExcludedFolderNames = [.. FileIndexOptions.DefaultExcludedNames],
                    ScriptTimeoutSeconds = JsonSerializer.SerializeToElement(ScriptOptions.DefaultTimeoutSeconds),
                    ClipboardRetentionDays = JsonSerializer.SerializeToElement(ClipboardOptions.DefaultRetentionDays),
                    ClipboardExcludedApps = JsonSerializer.SerializeToElement(ClipboardOptions.DefaultExcludedApps),
                    SyncIntervalMinutes = JsonSerializer.SerializeToElement(SyncOptions.DefaultIntervalMinutes),
                    SnippetAutoExpand = JsonSerializer.SerializeToElement(false),
                };
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(defaults, JsonOptions));
                return new SettingsLoadResult(defaults, null);
            }

            var settings = JsonSerializer.Deserialize<BeaconSettings>(File.ReadAllText(path), JsonOptions) ?? new BeaconSettings();
            settings.Hotkey ??= BeaconSettings.DefaultHotkey;
            return new SettingsLoadResult(settings, null);
        }
        catch (JsonException ex)
        {
            // The exception's own message quotes the file, and Windows keeps notification text
            // in its history. The line number is enough to find the mistake.
            string where = ex.LineNumber is long line ? $" (line {line + 1})" : "";
            return new SettingsLoadResult(new BeaconSettings(), "settings.json is not valid JSON" + where, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SettingsLoadResult(new BeaconSettings(), "settings.json could not be read or created", ex);
        }
    }
}
