using System.Globalization;
using System.Text.Json;
using Beacon.Core.Commands;

namespace Beacon.Core.Settings;

/// <summary>A command that can be given a hotkey, for the settings window: its short name and the name it is shown by.</summary>
public sealed record HotkeyChoice(string Id, string Name);

/// <summary>A hotkey that runs one of the launcher's own commands from anywhere.</summary>
/// <param name="CommandId">The command's short name, such as "window-left-half".</param>
/// <param name="Text">The hotkey as settings.json has it, for saying which one Windows refused.</param>
public sealed record CommandHotkey(string CommandId, Hotkey Hotkey, string Text);

/// <summary>
/// The "commandHotkeys" setting: commands and the hotkeys that run them, such as
/// <c>{ "window-left-half": "Ctrl+Alt+Left" }</c>. What cannot be used is left out and counted,
/// and the rest is used.
/// </summary>
/// <param name="Hotkeys">Those that can be registered, in the order the file has them.</param>
/// <param name="Notes">Sentences for the owner about what was left out: fixed words and counts.</param>
public sealed record CommandHotkeyOptions(IReadOnlyList<CommandHotkey> Hotkeys, IReadOnlyList<string> Notes)
{
    /// <summary>The setting's name in settings.json.</summary>
    public const string SettingName = "commandHotkeys";

    public const string NotAListNote =
        "\"commandHotkeys\" must be a list of commands and their hotkeys, such as { \"window-left-half\": \"Ctrl+Alt+Left\" }. No command has a hotkey.";

    /// <summary>No command has a hotkey, and there is nothing to say.</summary>
    public static CommandHotkeyOptions None { get; } = new([], []);

    /// <summary>
    /// True for a command a hotkey can run: one that does its thing at once, or that opens
    /// its view in the launcher, which the hotkey then shows. A command that asks for
    /// something first needs the launcher's input line, and the clipboard history has a
    /// hotkey setting of its own.
    /// </summary>
    public static bool CanRun(BuiltInCommand command) => command.Run is not null && command.Id != ClipboardHistoryId;

    /// <summary>The short name of the Clipboard History command, whose hotkey is the "clipboardHotkey" setting.</summary>
    public const string ClipboardHistoryId = "clipboard-history";

    /// <summary>The commands that can be given a hotkey, in the order they were given.</summary>
    public static IReadOnlyList<HotkeyChoice> Choices(IEnumerable<BuiltInCommand> commands) =>
        commands.Where(CanRun).Select(command => new HotkeyChoice(command.Id, command.Title)).ToList();

    /// <param name="setting">The setting as it was read, or null when the file does not have it.</param>
    /// <param name="runnable">
    /// Says what a command's short name is to a hotkey: true for a command a hotkey can run,
    /// false for one that asks for something first or shows what it does in the launcher, and
    /// null for a name that is no command's. Names are given as the file has them.
    /// </param>
    /// <param name="taken">The hotkeys Beacon uses already: the launcher's and the clipboard history's.</param>
    public static CommandHotkeyOptions From(JsonElement? setting, Func<string, bool?> runnable, IEnumerable<Hotkey> taken)
    {
        if (setting is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return None;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return new CommandHotkeyOptions([], [NotAListNote]);
        }

        var used = new HashSet<Hotkey>(taken);
        var commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hotkeys = new List<CommandHotkey>();
        int unknown = 0;
        int notHotkeys = 0;
        int notRunnable = 0;
        int twice = 0;
        foreach (var property in value.EnumerateObject())
        {
            string id = property.Name.Trim();

            // Empty text is a command without a hotkey, which is how one is taken away by hand.
            if (property.Value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                continue;
            }

            bool? can = runnable(id);
            if (can is null)
            {
                unknown++;
            }
            else if (can == false)
            {
                notRunnable++;
            }
            else if (property.Value.ValueKind != JsonValueKind.String || !Hotkey.TryParse(property.Value.GetString(), out var hotkey))
            {
                notHotkeys++;
            }
            else if (!commands.Add(id) || !used.Add(hotkey))
            {
                // The same key for two things, or the same command a second time.
                twice++;
            }
            else
            {
                hotkeys.Add(new CommandHotkey(id, hotkey, property.Value.GetString()!.Trim()));
            }
        }

        var notes = new List<string>();
        Count(notes, unknown, "Command hotkeys left out because Beacon has no command of that name");
        Count(notes, notRunnable, "Command hotkeys left out because the command asks for something first, or is the clipboard history, which has \"clipboardHotkey\"");
        Count(notes, notHotkeys, "Command hotkeys left out because they are not hotkeys Beacon understands");
        Count(notes, twice, "Command hotkeys left out because another hotkey of Beacon already has the key, or the command already has one");
        return new CommandHotkeyOptions(hotkeys, notes);
    }

    private static void Count(List<string> notes, int count, string words)
    {
        if (count > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{words}: {count}."));
        }
    }
}
