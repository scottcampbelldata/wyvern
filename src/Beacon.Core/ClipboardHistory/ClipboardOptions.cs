using System.Globalization;
using System.Text;
using System.Text.Json;
using Beacon.Core.Platform;
using Beacon.Core.Settings;

namespace Beacon.Core.ClipboardHistory;

/// <summary>
/// The settings for clipboard history, worked out from settings.json: what this machine is
/// called in it, how many days of it are kept, which programs are never recorded from, and
/// the hotkey for it.
/// </summary>
public sealed class ClipboardOptions
{
    /// <summary>About three months.</summary>
    public const int DefaultRetentionDays = 90;

    /// <summary>About ten years.</summary>
    public const int MaxRetentionDays = 3650;

    /// <summary>
    /// The most UTF-16 characters a device name may have. The name becomes a folder's name,
    /// so it is kept short.
    /// </summary>
    public const int MaxDeviceNameLength = 40;

    /// <summary>The most characters a "clipboardRetentionDays" number may be written in.</summary>
    private const int MaxNumberLength = 28;

    private const string HotkeyIsKept = "The clipboard history keeps the hotkey it had, if any.";

    private ClipboardOptions(
        string deviceName,
        int retentionDays,
        IReadOnlyList<string> excludedApps,
        Hotkey? hotkey,
        string? hotkeyText,
        bool hotkeyInvalid,
        IReadOnlyList<string> notes)
    {
        DeviceName = deviceName;
        RetentionDays = retentionDays;
        ExcludedApps = excludedApps;
        Hotkey = hotkey;
        HotkeyText = hotkeyText;
        HotkeyInvalid = hotkeyInvalid;
        Notes = notes;
    }

    /// <summary>
    /// The programs that are never recorded from when settings.json has no list of its own:
    /// password managers, where what is copied is a password.
    /// </summary>
    public static IReadOnlyList<string> DefaultExcludedApps { get; } =
        ["1password.exe", "bitwarden.exe", "keepass.exe", "keepassxc.exe", "lastpass.exe", "dashlane.exe"];

    /// <summary>What applies when settings.json says nothing about clipboard history.</summary>
    /// <remarks>
    /// It is declared after <see cref="DefaultExcludedApps"/>, which it reads: static values
    /// are set in the order they are written.
    /// </remarks>
    public static ClipboardOptions Default { get; } = From(new BeaconSettings());

    /// <summary>
    /// What this machine is called in clipboard history. It is always a name that is safe as
    /// one folder name (see <see cref="SafeDeviceName"/>) and is never empty.
    /// </summary>
    public string DeviceName { get; }

    /// <summary>How many days of clipboard history are kept.</summary>
    public int RetentionDays { get; }

    /// <summary>
    /// The programs that are never recorded from, each as <see cref="ProgramName"/> gives it,
    /// in the order they are written and without repeats.
    /// </summary>
    public IReadOnlyList<string> ExcludedApps { get; }

    /// <summary>The hotkey to register for the clipboard history, or null when there is none to register.</summary>
    public Hotkey? Hotkey { get; }

    /// <summary>How <see cref="Hotkey"/> is written, without the white space around it, for messages. Null when it is null.</summary>
    public string? HotkeyText { get; }

    /// <summary>
    /// True when "clipboardHotkey" holds something that cannot be used. The hotkey that was
    /// registered before, if there was one, is then left as it is. When this is false and
    /// <see cref="Hotkey"/> is null, the owner asked for no hotkey.
    /// </summary>
    public bool HotkeyInvalid { get; }

    /// <summary>
    /// Sentences to show the owner about clipboard settings that could not be used. They are
    /// made of fixed words and counts, except that one may quote the hotkey text from
    /// settings.json, so they must never be logged.
    /// </summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>True when what is copied in this program must not be put in clipboard history.</summary>
    /// <param name="program">
    /// A program's file name or full path, in any case, with or without ".exe". Null or blank
    /// text is no program, and is not excluded.
    /// </param>
    public bool Excludes(string? program) => ProgramName(program) is { } name && ExcludedApps.Contains(name);

    /// <summary>
    /// A missing or null key means the machine's name, 90 days, the built-in list of password
    /// managers and no hotkey. Each value is judged by itself, so that one of the wrong kind is
    /// noted in <see cref="Notes"/> and takes nothing else with it. This method never throws,
    /// whatever the JSON holds.
    /// </summary>
    /// <remarks>
    /// <para>"deviceName" is text, and blank text means the machine's name too. Whatever is
    /// used goes through <see cref="SafeDeviceName"/>.</para>
    /// <para>"clipboardRetentionDays" is a JSON number whose value is whole and from 1 to 3650,
    /// in whatever form it is written ("30.0" is 30); anything else is replaced by 90. A
    /// number written in more than 28 characters is not accepted, whatever it holds: it is
    /// more digits than a decimal can keep, so it could be rounded to a whole number that it
    /// is not.</para>
    /// <para>"clipboardExcludedApps" is a list of program names. This setting keeps passwords
    /// out of the history, so a mistake in it must never switch that off. A list with at
    /// least one entry that <see cref="ProgramName"/> can use replaces the built-in list, and
    /// each entry that cannot be used (not text, or text that names no program) is left out
    /// and counted in a note. An empty list is the owner's way of excluding nothing, and it
    /// replaces the built-in list too, with no note. A list that has entries but none of them
    /// can be used leaves the built-in list in use, with one note saying so. A value that is
    /// not a list at all leaves the built-in list in use as well.</para>
    /// <para>"clipboardHotkey" is text such as "Ctrl+Shift+V", and blank text means no hotkey.
    /// A value that is not text, text that is not a hotkey, and the same hotkey as "hotkey"
    /// cannot be used (see <see cref="HotkeyInvalid"/>).</para>
    /// </remarks>
    /// <param name="settings">What settings.json holds.</param>
    /// <param name="machineName">The machine's name. Null means the name Windows gives it; a test passes its own.</param>
    public static ClipboardOptions From(BeaconSettings settings, string? machineName = null)
    {
        var notes = new List<string>();

        string? device = null;
        if (Written(settings.DeviceName) is { } name)
        {
            if (TryGetText(name, out string text))
            {
                device = text;
            }
            else
            {
                notes.Add("\"deviceName\" in settings.json must be text. This machine's name is used instead.");
            }
        }

        int retentionDays = DefaultRetentionDays;
        if (Written(settings.ClipboardRetentionDays) is { } days)
        {
            if (WholeDays(days) is { } given)
            {
                retentionDays = given;
            }
            else
            {
                notes.Add($"\"clipboardRetentionDays\" must be a whole number from 1 to {MaxRetentionDays}. {DefaultRetentionDays} is used instead.");
            }
        }

        IReadOnlyList<string> excludedApps = DefaultExcludedApps;
        if (Written(settings.ClipboardExcludedApps) is { } apps)
        {
            if (apps.ValueKind != JsonValueKind.Array)
            {
                notes.Add("\"clipboardExcludedApps\" must be a list of program names. The built-in list is used instead.");
            }
            else
            {
                var programs = new List<string>();
                int entries = 0;
                int unusable = 0;
                foreach (var entry in apps.EnumerateArray())
                {
                    entries++;
                    if (!TryGetText(entry, out string written) || ProgramName(written) is not { } program)
                    {
                        unusable++;
                    }
                    else if (!programs.Contains(program))
                    {
                        // The same program written twice, or in two ways, is one program.
                        programs.Add(program);
                    }
                }

                if (entries > 0 && programs.Count == 0)
                {
                    // Every entry is a mistake. Nothing is left of the owner's list, and an
                    // empty list would switch the exclusion off, so the built-in list stays.
                    // (A list with no entries at all is not this: it says to exclude nothing.)
                    notes.Add("No entry of \"clipboardExcludedApps\" in settings.json can be used. The built-in list is used instead.");
                }
                else
                {
                    excludedApps = programs.AsReadOnly();
                    if (unusable == 1)
                    {
                        notes.Add("One entry of \"clipboardExcludedApps\" in settings.json cannot be used and is ignored.");
                    }
                    else if (unusable > 1)
                    {
                        notes.Add($"{unusable} entries of \"clipboardExcludedApps\" in settings.json cannot be used and are ignored.");
                    }
                }
            }
        }

        Hotkey? hotkey = null;
        string? hotkeyText = null;
        bool hotkeyInvalid = false;
        if (Written(settings.ClipboardHotkey) is { } key)
        {
            if (!TryGetText(key, out string written))
            {
                hotkeyInvalid = true;
                notes.Add("\"clipboardHotkey\" must be text such as \"Ctrl+Shift+V\". " + HotkeyIsKept);
            }
            else if (!string.IsNullOrWhiteSpace(written))
            {
                string trimmed = written.Trim();
                if (!Settings.Hotkey.TryParse(trimmed, out var parsed))
                {
                    // The text is the owner's own, from settings.json. It is made fit for a
                    // notification: nothing invisible in it, and no longer than a hotkey needs.
                    string shown = NoteText.Shorten(NoteText.WithoutInvisibleCharacters(trimmed), 40);
                    hotkeyInvalid = true;
                    notes.Add($"\"{shown}\" is not a hotkey Beacon understands. " + HotkeyIsKept);
                }
                else if (Settings.Hotkey.TryParse(settings.Hotkey, out var main) && main == parsed)
                {
                    // The launcher has this hotkey already, and one hotkey cannot open two things.
                    hotkeyInvalid = true;
                    notes.Add("\"clipboardHotkey\" is the same as \"hotkey\", so it is not used. " + HotkeyIsKept);
                }
                else
                {
                    hotkey = parsed;
                    hotkeyText = trimmed;
                }
            }
        }

        return new ClipboardOptions(
            SafeDeviceName(device, machineName ?? ThisMachine()),
            retentionDays,
            excludedApps,
            hotkey,
            hotkeyText,
            hotkeyInvalid,
            notes.AsReadOnly());
    }

    /// <summary>
    /// Makes a name that is safe as one folder name on Windows, and the same name from the same
    /// text on every machine. White space around the text is dropped. A control character, one
    /// of the characters a name may not hold (&lt; &gt; : " / \ | ? *) and half of a surrogate
    /// pair with no partner each become "_". At most <see cref="MaxDeviceNameLength"/> UTF-16
    /// characters are kept, a whole character at a time. A dot or a space at the end becomes
    /// "_", because Windows drops those and two names would then be one folder. A name Windows
    /// keeps for a device, such as NUL or nul.txt, gets "_" in front. Case is kept as written.
    /// </summary>
    /// <param name="written">The name the owner chose. Null or blank text means the machine's name.</param>
    /// <param name="machineName">The machine's name. It is made safe in the same way, and when nothing is left of it either the name is "device".</param>
    public static string SafeDeviceName(string? written, string machineName)
    {
        string name = SafeFileName(written, MaxDeviceNameLength);
        if (name.Length == 0)
        {
            name = SafeFileName(machineName, MaxDeviceNameLength);
        }

        return name.Length == 0 ? "device" : name;
    }

    /// <summary>
    /// The name a program is known by here, so that "KeePass", "keepass.exe", "KEEPASS.EXE" and
    /// "C:\Tools\KeePass.exe" are all "keepass.exe": the file name without its folders, in lower
    /// case, ending in ".exe". White space around the text is dropped, and so is one pair of
    /// double quotes round it, which is how Explorer's "Copy as path" gives a path. A control
    /// character, or a format character (such as U+FEFF or U+200B), is taken out wherever it
    /// stands. Other characters that show nothing, such as a Hangul filler or a variation
    /// selector, are neither of those and are kept, so a name that holds one is a different
    /// name from the same name without it.
    /// </summary>
    /// <returns>
    /// Null for null or blank text; for text that ends in a separator, a dot or a space, and so
    /// names no file; for text with half of a surrogate pair that has no partner; and for text
    /// that can never be the name of a program, because it holds a character a file name may
    /// not hold (&lt; &gt; : " | ? *) or a comma or a semicolon. A comma or a semicolon is legal
    /// in a file name, but in a list of programs it almost certainly means several names
    /// written as one, and saying so is safer than a name that never matches.
    /// </returns>
    public static string? ProgramName(string? program)
    {
        if (string.IsNullOrWhiteSpace(program))
        {
            return null;
        }

        string text = program.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            text = text[1..^1].Trim();
        }

        var cleaned = new StringBuilder(text.Length);
        int at = 0;
        while (at < text.Length)
        {
            if (!Rune.TryGetRuneAt(text, at, out Rune rune))
            {
                // Half of a surrogate pair with no partner is not text that can be read.
                return null;
            }

            if (Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Control or UnicodeCategory.Format))
            {
                cleaned.Append(rune);
            }

            at += rune.Utf16SequenceLength;
        }

        // An owner may write a full path, with either kind of separator.
        string name = cleaned.ToString();
        name = name[(name.AsSpan().LastIndexOfAny('\\', '/') + 1)..];
        if (name.Length == 0 || name[^1] is '.' or ' ')
        {
            return null;
        }

        foreach (Rune rune in name.EnumerateRunes())
        {
            if (IsForbidden(rune) || rune.Value is ',' or ';')
            {
                return null;
            }
        }

        name = name.ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name : name + ".exe";
    }

    /// <returns>The value, or null when the key is missing or is written as null.</returns>
    private static JsonElement? Written(JsonElement? value) =>
        value is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } written ? written : null;

    /// <returns>The number of days when the value is a JSON number that is whole, from 1 to <see cref="MaxRetentionDays"/> and written in no more than 28 characters, in whatever form ("30", "30.0" and "3e1" are all 30). Otherwise null, so a number written in more characters than that is refused, whatever it holds.</returns>
    private static int? WholeDays(JsonElement value)
    {
        // The number is read from the text it was written as, so that its form decides nothing
        // and a number too large for any type is simply out of range.
        //
        // A decimal keeps 28 or 29 digits and rounds the rest, so 0.99999999999999999999999999999
        // would be read as 1. A number written in more than 28 characters is not accepted at
        // all, whatever it holds: retention deletes files, and a number that is not whole must
        // never be taken for one.
        if (value.ValueKind != JsonValueKind.Number
            || value.GetRawText().Length > MaxNumberLength
            || !decimal.TryParse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal days)
            || days != decimal.Truncate(days)
            || days < 1
            || days > MaxRetentionDays)
        {
            return null;
        }

        return (int)days;
    }

    private static bool TryGetText(JsonElement value, out string text)
    {
        text = "";
        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        try
        {
            text = value.GetString() ?? "";
            return true;
        }
        catch (InvalidOperationException)
        {
            // Half of a surrogate pair written as an escape ("\ud800") is valid JSON, but it is
            // not text that can be read. It is one more value that cannot be used.
            return false;
        }
    }

    private static string ThisMachine()
    {
        try
        {
            return Environment.MachineName;
        }
        catch (InvalidOperationException)
        {
            // Windows could not say what this machine is called. With nothing to go on,
            // SafeDeviceName gives the name "device".
            return "";
        }
    }

    /// <summary>
    /// The rules of <see cref="SafeDeviceName"/> for any text that is to be one file or folder
    /// name, with a limit of the caller's own on its length. A picture that is saved out of the
    /// history under the name it had is named through this: that name comes from a record,
    /// which may have been written anywhere.
    /// </summary>
    /// <returns>The safe name, or empty text when the text is null or blank.</returns>
    public static string SafeFileName(string? text, int maxLength)
    {
        string trimmed = (text ?? "").Trim();
        var name = new StringBuilder(trimmed.Length);
        int at = 0;
        while (at < trimmed.Length)
        {
            if (Rune.TryGetRuneAt(trimmed, at, out Rune rune))
            {
                name.Append(IsForbidden(rune) ? "_" : rune.ToString());
                at += rune.Utf16SequenceLength;
            }
            else
            {
                // Half of a surrogate pair with no partner. It is not a character, and a name
                // that holds one cannot be written down or sent to another machine as it is.
                name.Append('_');
                at++;
            }
        }

        // The "_" put in front of a name Windows keeps for a device makes it one character
        // longer, so it is fitted again and never goes over the limit.
        string fitted = Fitted(name.ToString(), maxLength);
        return IsReserved(fitted) ? Fitted("_" + fitted, maxLength) : fitted;
    }

    // The characters are written out here, not asked of the machine, so that every machine and
    // every version of Windows makes the same name from the same text.
    private static bool IsForbidden(Rune rune) =>
        rune.Value < 0x20 || rune.Value is 0x7F or '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*';

    /// <returns>The start of the name that fits in the limit, ending in neither a dot nor a space.</returns>
    private static string Fitted(string name, int maxLength)
    {
        // An emoji and many other characters are two UTF-16 characters. Cutting between them
        // would leave half of one, so the name is kept a whole character at a time.
        var kept = new StringBuilder();
        foreach (Rune rune in name.EnumerateRunes())
        {
            if (kept.Length + rune.Utf16SequenceLength > maxLength)
            {
                break;
            }

            kept.Append(rune);
        }

        // Windows drops the dots and spaces that end a name, so "otter." and "otter" would be
        // one folder. With "_" in its place the name ends in neither.
        if (kept.Length > 0 && kept[^1] is '.' or ' ')
        {
            kept[^1] = '_';
        }

        return kept.ToString();
    }

    /// <returns>True when Windows keeps the name for a device, in any case and with or without an extension.</returns>
    private static bool IsReserved(string name)
    {
        // Windows reads "nul.txt", "NUL.tar.gz" and "nul .txt" all as NUL: what counts is the
        // part before the first dot, without the spaces that end it.
        int dot = name.IndexOf('.');
        string stem = (dot < 0 ? name : name[..dot]).TrimEnd(' ').ToUpperInvariant();

        // Windows takes the superscript digits one, two and three for digits here as well, so
        // COM and LPT followed by one of them are kept in the same way as COM1 and LPT1.
        // CONIN$ and CONOUT$ are the console's input and output.
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (stem.Length == 4
                && stem[..3] is "COM" or "LPT"
                && stem[3] is (>= '1' and <= '9') or '\u00B9' or '\u00B2' or '\u00B3');
    }
}
