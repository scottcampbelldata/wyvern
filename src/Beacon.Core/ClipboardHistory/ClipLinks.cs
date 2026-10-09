using System.Buffers;
using System.Globalization;
using System.Text;
using Beacon.Core.Platform;

namespace Beacon.Core.ClipboardHistory;

/// <summary>
/// Tells when what was copied is a web address, and finds the words the link had on the page
/// it was copied from. Both come from the bytes of that one copy and from nothing else.
/// </summary>
/// <remarks>
/// Beacon never fetches a copied address: no page title, no icon, no preview. Fetching it would
/// tell the site that the owner copied it, and can use up a link that works only once, such as
/// one for signing in or for resetting a password. Nothing here opens or asks for anything.
/// </remarks>
public static class ClipLinks
{
    /// <summary>The longest text that is looked at as an address. Longer text is not a link anyone copied on purpose.</summary>
    public const int MaxAddressLength = 8192;

    /// <summary>The longest label that is kept. The words of a link are short; a whole paragraph inside one is cut.</summary>
    public const int MaxLabelLength = 200;

    /// <summary>The largest HTML that is searched for a label. A link copied by itself comes with a few hundred bytes.</summary>
    public const int MaxHtmlBytes = 1024 * 1024;

    // The longest character reference that is decoded, between its "&" and its ";".
    private const int MaxReferenceLength = 10;

    /// <summary>
    /// The address, when the text is one web address and nothing else: trimmed, it starts with
    /// "http://" or "https://", holds no white space, no control or format character, none of
    /// the other characters that show nothing, and no half of a surrogate pair with no partner,
    /// and is an absolute address with a host. The text is read a whole character at a time,
    /// so a character outside the basic plane is looked at as the one character it is.
    /// </summary>
    /// <returns>The trimmed text, or null when it is not such an address.</returns>
    public static string? Address(string? text)
    {
        if (text is null)
        {
            return null;
        }

        // Everything cheap is asked first, so that a megabyte of ordinary text costs nothing.
        var trimmed = text.AsSpan().Trim();
        if (trimmed.Length == 0
            || trimmed.Length > MaxAddressLength
            || !(trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        // A character that shows nothing, or that turns the text around (U+202E), can make an
        // address read as another one. Text that holds one is not treated as a link: what
        // shows nothing is what NoteText.IsInvisible says, the one list of them, which also
        // has the code points that are not assigned to any character and those for private
        // use, neither of which can be known to show anything. The text is read a whole
        // character (a rune) at a time: many of these are outside the basic plane and are two
        // UTF-16 characters, each of which alone is only half of a pair. Half a pair with no
        // partner is no character at all, and text that holds one is not a link.
        for (int i = 0; i < trimmed.Length;)
        {
            if (Rune.DecodeFromUtf16(trimmed[i..], out Rune rune, out int length) != OperationStatus.Done
                || Rune.IsWhiteSpace(rune)
                || NoteText.IsInvisible(rune))
            {
                return null;
            }

            i += length;
        }

        string address = trimmed.ToString();
        return Uri.TryCreate(address, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.Host.Length > 0
            ? address
            : null;
    }

    /// <summary>
    /// The words a copied link had on its page. A browser that copies a link puts the address
    /// on the clipboard as text and, beside it, HTML that holds the link as it stood. When that
    /// HTML holds exactly one link and it goes to the same address, its words are the label.
    /// </summary>
    /// <param name="html">The clipboard's "HTML Format" for the same copy: a few header lines, then HTML as UTF-8.</param>
    /// <param name="address">The address that was copied as text.</param>
    /// <returns>
    /// The label, made fit to show by <see cref="ShownLabel"/>, or null: when there is no HTML
    /// or it is larger than <see cref="MaxHtmlBytes"/>, when it holds no link or more than one,
    /// when its link goes elsewhere, when the words are the address itself, and when
    /// <see cref="ShownLabel"/> says there is none: the words show nothing, or hold an address,
    /// or are one word with a dot in it, which is taken for the name of a host. A link to one
    /// site can have another site's address as its words, and so does a label that has an
    /// address among its words. The time taken is in proportion to the size of the HTML.
    /// </returns>
    /// <remarks>
    /// Links are counted by a scanner of tags that is close to the way a browser reads HTML and
    /// is not the same: a tag starts at a "&lt;" before a letter, "/", "!" or "?"; its name ends
    /// at white space, "/" or "&gt;"; and a quote inside a tag runs to the next quote of the same
    /// kind, so that a "&gt;" inside it does not end the tag. A browser opens a quoted run only
    /// at the start of an attribute's value, and this does not, so the two can disagree on odd
    /// HTML. That is why the address is always shown beside a label: the label is only a help
    /// to read it, and nothing is decided by it.
    /// </remarks>
    public static string? Label(byte[]? html, string address)
    {
        if (html is null || html.Length == 0 || html.Length > MaxHtmlBytes)
        {
            return null;
        }

        int start = HtmlStart(html);
        if (start < 0)
        {
            return null;
        }

        string page = Encoding.UTF8.GetString(html, start, html.Length - start);
        string? href = null;
        string? words = null;
        int at = 0;
        int tag;
        while ((tag = NextMarkup(page, at, out int tagEnd, out bool isComment)) >= 0)
        {
            if (isComment)
            {
                // A comment is skipped whole: a link written inside one is not on the page.
                at = tagEnd + 1;
                continue;
            }

            if (tagEnd < 0)
            {
                // A tag that is never ended.
                break;
            }

            if (IsAnchor(page, tag) && Href(page, tag + 2, tagEnd) is { } found)
            {
                if (href is not null)
                {
                    // A second link. Which of the two the text is about cannot be told.
                    return null;
                }

                href = found;
                int close = AnchorEnd(page, tagEnd + 1);
                words = page[(tagEnd + 1)..(close < 0 ? page.Length : close)];
            }

            // On from the end of this tag, not of the link, so a link inside a link is seen too.
            at = tagEnd + 1;
        }

        if (href is null || words is null || !SameAddress(Decode(href).Trim(), address))
        {
            return null;
        }

        // Words that are the address itself hold "http://" or "https://", so ShownLabel has
        // refused them already for an address that Address gave. The comparison is for a caller
        // that passes another kind of address.
        string? label = ShownLabel(PlainWords(words));
        return label is null || SameAddress(label, address) ? null : label;
    }

    /// <summary>
    /// The one rule that says whether the words of a link are fit to be shown as its label, and
    /// what is shown of them. It is applied at every end: when capture makes the label from a
    /// page's HTML (<see cref="Label"/>), when an entry is made from a record that may have been
    /// written on another machine (<see cref="ClipEntry.From"/>), and by the view that shows the
    /// label in a row and in the preview. The words are made one line and cleaned by
    /// <see cref="NoteText.OnScreen"/>: white space is one space, and what shows nothing (control
    /// and format characters, those that are not assigned to a character or are for private use,
    /// and the fillers and selectors) is taken out. They are then cut to
    /// <paramref name="maxLength"/>, and what is left is what is looked at, so that words that
    /// only look like an address once they are cut short are refused too.
    /// </summary>
    /// <remarks>
    /// A label is shown beside the address that was copied, and must not pass for another place
    /// that the link goes to. So there is no label when the words hold "http://" or "https://"
    /// anywhere, in any case and however it is stuck to what is round it, and when they are one
    /// word with a dot in it, which is taken for the name of a host. A dot in words of several
    /// words is not a reason: "Mr. Otter's page, v1.2" keeps its label, and the real address is
    /// always shown beside it.
    /// </remarks>
    /// <param name="words">The words as they were written, or null.</param>
    /// <param name="maxLength">The longest label that is shown. Left out, <see cref="MaxLabelLength"/>.</param>
    /// <returns>The label as it is shown, or null when there is none and the address stands alone.</returns>
    public static string? ShownLabel(string? words, int maxLength = MaxLabelLength)
    {
        if (words is null)
        {
            return null;
        }

        string shown = NoteText.OnScreen(words, maxLength);
        return shown.Length == 0 || HoldsScheme(shown) || LooksLikeAHostName(shown) ? null : shown;
    }

    // Where the HTML starts in the block. The header says so in "StartHTML:"; the header is
    // everything before the first "<", and is looked for only there, so that text on the page
    // cannot pass for it. Without a usable number the HTML starts at the first "<".
    private static int HtmlStart(byte[] html)
    {
        int firstTag = Array.IndexOf(html, (byte)'<');
        if (firstTag < 0)
        {
            return -1;
        }

        ReadOnlySpan<byte> header = html.AsSpan(0, firstTag);
        int key = header.IndexOf("StartHTML:"u8);
        if (key < 0)
        {
            return firstTag;
        }

        long offset = 0;
        int digits = 0;
        for (int i = key + 10; i < header.Length && header[i] is >= (byte)'0' and <= (byte)'9'; i++)
        {
            offset = (offset * 10) + (header[i] - '0');
            digits++;
            if (offset > html.Length)
            {
                return firstTag;
            }
        }

        return digits > 0 && offset < html.Length ? (int)offset : firstTag;
    }

    // The next comment or tag at or after "from": where it starts, or -1 when there is none.
    // This is the one place that says where markup is, for the search for links and for the
    // words of a link alike, so that the two cannot read the same text differently. Each "<" is
    // looked at once and the text after it is not read again. "end" is where the markup ends:
    // the last character of "-->" for a comment, which when it is never ended takes the rest of
    // the text; the ">" for a tag, or -1 when the tag is never ended. A "<" is the start of a
    // tag only when a letter, "/", "!" or "?" follows it, which is what a browser does too. Any
    // other "<" is text, and it does not swallow the tag after it: in "1 < 2 <a href=...>" the
    // link is a link. This is close to a browser's reading of a tag and is not the same (see
    // the remarks on Label).
    private static int NextMarkup(string text, int from, out int end, out bool isComment)
    {
        int tag = from;
        while ((tag = text.IndexOf('<', tag)) >= 0)
        {
            isComment = text.AsSpan(tag).StartsWith("<!--", StringComparison.Ordinal);
            if (isComment)
            {
                int close = text.IndexOf("-->", tag + 4, StringComparison.Ordinal);
                end = close < 0 ? text.Length - 1 : close + 2;
                return tag;
            }

            if (tag + 1 < text.Length && (char.IsAsciiLetter(text[tag + 1]) || text[tag + 1] is '/' or '!' or '?'))
            {
                end = TagEnd(text, tag);
                return tag;
            }

            tag++;
        }

        end = -1;
        isComment = false;
        return -1;
    }

    // The ">" that ends the tag starting at this "<". One inside a quoted value does not end it.
    private static int TagEnd(string page, int tag)
    {
        char quote = '\0';
        for (int i = tag + 1; i < page.Length; i++)
        {
            char c = page[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return i;
            }
        }

        return -1;
    }

    // "<a" in either case, followed by what ends a tag's name: white space, "/" or ">". So
    // "<a/href=...>" is a link, and "<abbr" is another tag.
    private static bool IsAnchor(string page, int tag) =>
        tag + 2 < page.Length && page[tag + 1] is 'a' or 'A' && (char.IsWhiteSpace(page[tag + 2]) || page[tag + 2] is '/' or '>');

    // The value of the href attribute of a start tag, as written, or null when it has none.
    // The attributes are walked one by one, so "data-href" is not taken for it, and neither
    // is "href=" standing inside the value of another attribute.
    private static string? Href(string page, int from, int tagEnd)
    {
        int i = from;
        while (i < tagEnd)
        {
            while (i < tagEnd && (char.IsWhiteSpace(page[i]) || page[i] == '/'))
            {
                i++;
            }

            int nameStart = i;
            while (i < tagEnd && !char.IsWhiteSpace(page[i]) && page[i] is not ('=' or '/'))
            {
                i++;
            }

            var name = page.AsSpan(nameStart, i - nameStart);
            while (i < tagEnd && char.IsWhiteSpace(page[i]))
            {
                i++;
            }

            string value = "";
            if (i < tagEnd && page[i] == '=')
            {
                i++;
                while (i < tagEnd && char.IsWhiteSpace(page[i]))
                {
                    i++;
                }

                int valueStart = i;
                if (i < tagEnd && page[i] is '"' or '\'')
                {
                    char quote = page[i];
                    valueStart = ++i;
                    while (i < tagEnd && page[i] != quote)
                    {
                        i++;
                    }

                    value = page[valueStart..i];
                    i++;
                }
                else
                {
                    while (i < tagEnd && !char.IsWhiteSpace(page[i]))
                    {
                        i++;
                    }

                    value = page[valueStart..i];
                }
            }
            else if (name.Length == 0)
            {
                // Neither a name nor a value: step on, so that nothing can hold this loop up.
                i++;
            }

            if (name.Equals("href", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    // Where the "</a" that ends a link starts, or -1 when the link is never ended.
    private static int AnchorEnd(string page, int from)
    {
        for (int at = from; ; at += 3)
        {
            at = page.IndexOf("</a", at, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return -1;
            }

            if (at + 3 >= page.Length || page[at + 3] == '>' || char.IsWhiteSpace(page[at + 3]))
            {
                return at;
            }
        }
    }

    // The same address as text, or the same once both are read as addresses, which makes
    // "https://example.org" and "https://example.org/" one.
    private static bool SameAddress(string first, string second) =>
        first == second
        || (first.Length <= MaxAddressLength
            && second.Length <= MaxAddressLength
            && Uri.TryCreate(first, UriKind.Absolute, out var one)
            && Uri.TryCreate(second, UriKind.Absolute, out var other)
            && one.AbsoluteUri == other.AbsoluteUri);

    // The words inside a link as the page shows them: without tags, with character references
    // decoded. Each tag is a gap, so that the words either side of one stay apart. Making them
    // one line and taking out what shows nothing is ShownLabel's work, which is the same
    // wherever a label is looked at.
    private static string PlainWords(string content)
    {
        var plain = new StringBuilder(content.Length);
        int at = 0;
        while (at < content.Length)
        {
            // A comment is not shown on the page, and neither is a tag. A comment that is never
            // ended takes the rest with it; a tag that is never ended is taken as words.
            int tag = NextMarkup(content, at, out int tagEnd, out _);
            if (tag < 0 || tagEnd < 0)
            {
                plain.Append(content, at, content.Length - at);
                break;
            }

            plain.Append(content, at, tag - at).Append(' ');
            at = tagEnd + 1;
        }

        return Decode(plain.ToString());
    }

    // "http://" or "https://" anywhere in the text, in any case. A label that holds one would
    // be shown beside the address that was copied and could pass for the place it goes to,
    // whatever stands in front of it: an address with a mark stuck to its front is not an
    // address by the rule for one, and reads as one all the same.
    private static bool HoldsScheme(string text) =>
        text.Contains("http://", StringComparison.OrdinalIgnoreCase)
        || text.Contains("https://", StringComparison.OrdinalIgnoreCase);

    // Text that is one word with a dot in it is taken for the name of a host. The text has been
    // through NoteText.OnScreen, so white space of every kind is a single space.
    private static bool LooksLikeAHostName(string label) => label.Contains('.') && !label.Contains(' ');

    // Decodes the character references a browser writes: the five named ones, the space that
    // does not break, and numbers in decimal or hex. Done in one pass, so that what a reference
    // decodes to is not decoded again. Anything else is left as it is written.
    private static string Decode(string text)
    {
        if (!text.Contains('&'))
        {
            return text;
        }

        var decoded = new StringBuilder(text.Length);
        int at = 0;
        while (at < text.Length)
        {
            if (text[at] == '&')
            {
                // A reference is short, so the ";" is looked for only as far as the longest one
                // goes. Looking for it to the end of the text from every "&" takes time in
                // proportion to the square of the size when the text holds nothing but "&".
                int end = text.IndexOf(';', at + 1, Math.Min(MaxReferenceLength + 1, text.Length - at - 1));
                if (end > at && Reference(text.AsSpan(at + 1, end - at - 1)) is { } character)
                {
                    decoded.Append(character);
                    at = end + 1;
                    continue;
                }
            }

            decoded.Append(text[at]);
            at++;
        }

        return decoded.ToString();
    }

    private static string? Reference(ReadOnlySpan<char> name)
    {
        switch (name)
        {
            case "amp":
                return "&";
            case "lt":
                return "<";
            case "gt":
                return ">";
            case "quot":
                return "\"";
            case "apos":
                return "'";
            case "nbsp":
                return " ";
        }

        if (name.Length < 2 || name[0] != '#')
        {
            return null;
        }

        bool hex = name[1] is 'x' or 'X';
        var digits = hex ? name[2..] : name[1..];
        return digits.Length > 0
            && int.TryParse(digits, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            && value != 0
            && Rune.IsValid(value)
            ? new Rune(value).ToString()
            : null;
    }
}
