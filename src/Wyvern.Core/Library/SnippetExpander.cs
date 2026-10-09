using System.Globalization;
using System.Text;
using Wyvern.Core.Platform;

namespace Wyvern.Core.Library;

/// <summary>Fills in a snippet's placeholders at the moment it is used.</summary>
public static class SnippetExpander
{
    public const string ClipboardPlaceholder = "{clipboard}";
    public const string DatePlaceholder = "{date}";
    public const string TimePlaceholder = "{time}";

    /// <summary>
    /// Fills in the placeholders for this moment: what the clock says now, in the machine's own
    /// time zone. The launcher and auto-expand both go by this for {date} and {time}. They do
    /// not hand it the same clipboard, though. The launcher reads the clipboard itself.
    /// Auto-expand asks <see cref="PasteService.OwnerText"/>, because a keyword typed just after
    /// another finds the clipboard holding the first snippet, and {clipboard} is meant to be the
    /// owner's text. The launcher's way is still right: by hand no paste can overlap another,
    /// for opening the launcher, finding a snippet and choosing it takes longer than the moment
    /// after a keyword in which the clipboard holds that snippet.
    /// </summary>
    /// <param name="zone">
    /// The time zone {date} and {time} are given in. Null everywhere except in tests: it is then
    /// the machine's own, read again each time.
    /// </param>
    /// <param name="clipboardText">As for <see cref="Expand"/>.</param>
    public static string ExpandNow(string body, IClock clock, TimeZoneInfo? zone, Func<string?> clipboardText) =>
        Expand(body, TimeZoneInfo.ConvertTime(clock.UtcNow, zone ?? MachineZone()), clipboardText);

    /// <summary>
    /// Replaces {clipboard} with the clipboard's text, {date} with the date as 2026-10-04 and
    /// {time} with the time as 14:05 on the 24-hour clock. The body is gone through once, from
    /// start to end, so text that arrives from the clipboard is never looked at for
    /// placeholders of its own. Anything else in braces is left exactly as written.
    /// </summary>
    /// <param name="now">The moment the snippet is used, in the owner's local time.</param>
    /// <param name="clipboardText">
    /// Reads the clipboard's text, or null when it holds none, which puts nothing in. It is
    /// called once at most, and not at all for a body without {clipboard}.
    /// </param>
    public static string Expand(string body, DateTimeOffset now, Func<string?> clipboardText)
    {
        int brace = body.IndexOf('{');
        if (brace < 0)
        {
            return body;
        }

        var expanded = new StringBuilder(body.Length + 32);
        string? clipboard = null;
        bool clipboardRead = false;
        int position = 0;
        while (brace >= 0)
        {
            expanded.Append(body, position, brace - position);
            if (StartsAt(body, brace, ClipboardPlaceholder))
            {
                if (!clipboardRead)
                {
                    clipboard = clipboardText();
                    clipboardRead = true;
                }

                expanded.Append(clipboard);
                position = brace + ClipboardPlaceholder.Length;
            }
            else if (StartsAt(body, brace, DatePlaceholder))
            {
                expanded.Append(now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                position = brace + DatePlaceholder.Length;
            }
            else if (StartsAt(body, brace, TimePlaceholder))
            {
                expanded.Append(now.ToString("HH:mm", CultureInfo.InvariantCulture));
                position = brace + TimePlaceholder.Length;
            }
            else
            {
                expanded.Append('{');
                position = brace + 1;
            }

            brace = body.IndexOf('{', position);
        }

        expanded.Append(body, position, body.Length - position);
        return expanded.ToString();
    }

    // Wyvern runs for weeks, and the machine's time zone can change in that time (a journey, or
    // the setting). .NET keeps the zone it first read, so without clearing what it kept, {date}
    // and {time} would be wrong until Wyvern is restarted.
    private static TimeZoneInfo MachineZone()
    {
        TimeZoneInfo.ClearCachedData();
        return TimeZoneInfo.Local;
    }

    private static bool StartsAt(string body, int index, string placeholder) =>
        string.CompareOrdinal(body, index, placeholder, 0, placeholder.Length) == 0;
}
