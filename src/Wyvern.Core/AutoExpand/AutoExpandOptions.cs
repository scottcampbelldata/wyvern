using System.Text.Json;
using Wyvern.Core.Settings;

namespace Wyvern.Core.AutoExpand;

/// <summary>
/// The one setting of snippet auto-expand in settings.json: whether a snippet's keyword is
/// replaced by the snippet wherever the owner types it. While it is on, Wyvern has a keyboard
/// hook installed and is told of every key that is pressed, so it is off unless the file says
/// <c>true</c> and nothing else: a value that cannot be understood never switches it on.
/// </summary>
public sealed class AutoExpandOptions
{
    /// <summary>Shown when "snippetAutoExpand" holds something that is neither true nor false.</summary>
    public const string NotTrueOrFalseNote = "\"snippetAutoExpand\" must be true or false. Snippet auto-expand is off.";

    private AutoExpandOptions(bool enabled, IReadOnlyList<string> notes)
    {
        Enabled = enabled;
        Notes = notes;
    }

    /// <summary>What applies when settings.json says nothing about auto-expand: it is off.</summary>
    public static AutoExpandOptions Default { get; } = new(false, []);

    /// <summary>True when the keyboard hook is to be installed.</summary>
    public bool Enabled { get; }

    /// <summary>Sentences to show the owner about a setting that could not be used. Fixed words.</summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>
    /// A missing or null "snippetAutoExpand" means off, and so does <c>false</c>. Only the JSON
    /// value <c>true</c> means on. Anything else (the text "true", a number, a list) gives a
    /// note and off. This method never throws, whatever the JSON holds.
    /// </summary>
    public static AutoExpandOptions From(WyvernSettings settings)
    {
        if (settings.SnippetAutoExpand is not { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } written)
        {
            return Default;
        }

        return written.ValueKind switch
        {
            JsonValueKind.True => new AutoExpandOptions(true, []),
            JsonValueKind.False => Default,
            _ => new AutoExpandOptions(false, [NotTrueOrFalseNote]),
        };
    }
}
