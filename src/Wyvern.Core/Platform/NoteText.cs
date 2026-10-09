using System.Globalization;
using System.Text;

namespace Wyvern.Core.Platform;

/// <summary>
/// Makes text that came from outside Wyvern (a file name, a line a script printed) fit to be
/// shown in a notification.
/// </summary>
public static class NoteText
{
    /// <summary>
    /// Text can hold characters that show nothing or change how the text around them is laid
    /// out (U+202E turns it around). A notification must show the text as it is written, so
    /// control and format characters are taken out. The text is read a whole character (a rune)
    /// at a time: some of these, such as U+E0001, are outside the basic plane and are two UTF-16
    /// characters, which a look at one UTF-16 character at a time cannot recognise. A half of a
    /// pair with no partner is read as U+FFFD.
    /// </summary>
    public static string WithoutInvisibleCharacters(string text)
    {
        var visible = new StringBuilder(text.Length);
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Control or UnicodeCategory.Format))
            {
                visible.Append(rune);
            }
        }

        return visible.ToString();
    }

    /// <summary>
    /// True for a character that shows nothing, or that cannot be known to show anything: a
    /// control or format character (the one that turns text around, U+202E, is a format
    /// character); a code point that is not assigned to any character; one for private use,
    /// which stands for whatever the font in use says; and the characters that are none of
    /// those and show nothing or only a gap all the same: the combining grapheme joiner
    /// (U+034F), the Hangul fillers (U+115F, U+1160, U+3164, U+FFA0), the Khmer vowels that are
    /// not shown (U+17B4, U+17B5), the Mongolian variation selectors (U+180B to U+180F), the
    /// blank Braille pattern (U+2800), the variation selectors (U+FE00 to U+FE0F, and U+E0100
    /// to U+E01EF), the Khitan small script filler (U+16FE4) and the musical null notehead
    /// (U+1D159). This is the one list of them: what is shown, and what is taken for a web
    /// address, are judged by it. White space is not asked here; a tab is a control character,
    /// and whoever cleans text looks at white space first.
    /// </summary>
    public static bool IsInvisible(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.OtherNotAssigned or UnicodeCategory.PrivateUse
        || rune.Value is 0x034F or 0x115F or 0x1160 or 0x17B4 or 0x17B5
            or (>= 0x180B and <= 0x180F) or 0x2800 or 0x3164 or 0xFFA0
            or (>= 0xFE00 and <= 0xFE0F) or 0x16FE4 or 0x1D159 or (>= 0xE0100 and <= 0xE01EF);

    /// <summary>
    /// The one cleaner of a line of text that came from outside (a clipboard, a file another
    /// machine wrote) and is shown on screen, as a row's title, a link's label, a program's name
    /// or a path in a preview. White space of every kind (a tab, a no-break space, a line or
    /// paragraph separator) is a space, and a run of spaces is one, with none at either end;
    /// then every character <see cref="IsInvisible"/> says shows nothing is left out, a whole
    /// character at a time, so that what is shown reads as it is written and the words either
    /// side of a tab do not run together. The text is then cut with <see cref="Shorten"/> to
    /// <paramref name="maxLength"/> when that is given.
    /// </summary>
    /// <remarks>
    /// Only as much of the text is read as the cut needs: a text of a million characters costs
    /// no more than one of <paramref name="maxLength"/>, and the result is the same.
    /// </remarks>
    public static string OnScreen(string text, int maxLength = int.MaxValue)
    {
        var line = new StringBuilder((int)Math.Clamp((long)maxLength + 1, 0, text.Length));
        bool spaceWaits = false;
        foreach (Rune rune in text.EnumerateRunes())
        {
            // White space is looked at first: a tab is a control character too, and would be
            // left out with them, joining the two words it was between. A space is only written
            // once something follows it, so there is none at either end, and one is enough
            // between two words even when characters that show nothing sit in the gap.
            if (Rune.IsWhiteSpace(rune))
            {
                spaceWaits = true;
            }
            else if (!IsInvisible(rune))
            {
                if (spaceWaits && line.Length > 0)
                {
                    line.Append(' ');
                }

                spaceWaits = false;
                line.Append(rune);
                if (line.Length > maxLength)
                {
                    // What follows is cut off in any case.
                    break;
                }
            }
        }

        return Shorten(line.ToString(), maxLength);
    }

    /// <summary>
    /// The text as it is when it is no longer than <paramref name="maxLength"/>; otherwise its
    /// start, ending in "…", and no longer than that either. An emoji and many other characters
    /// are two UTF-16 characters. Cutting between them would leave half of one, which is shown
    /// as a broken box, so the text is kept a whole character at a time.
    /// </summary>
    public static string Shorten(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var kept = new StringBuilder();
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (kept.Length + rune.Utf16SequenceLength > maxLength - 1)
            {
                break;
            }

            kept.Append(rune);
        }

        return kept + "…";
    }
}
