using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Wyvern.Core.Launcher;

namespace Wyvern.App.Interop;

/// <summary>
/// The emoji this PC can show, and a picture of each in colour. Both come from Windows itself:
/// the emoji are the characters its own emoji font has a coloured drawing for, their names
/// are the names Windows has for the characters, and a picture is the font's drawing, put
/// together from its coloured layers. Nothing is fetched and nothing is added to Wyvern for
/// it. On a PC without the font or without the names there are simply no emoji.
/// </summary>
/// <remarks>
/// Windows draws its text in one colour here (WPF does not use a font's colours), which is
/// why the layers are read from the font's file: its COLR table says which shapes lie on one
/// another for a character, and its CPAL table says what colour each has. Only single
/// characters are listed: flags and the people in other skin tones are several characters
/// that the font joins into one, which is not read. Use on the UI thread.
/// </remarks>
public sealed class EmojiFont
{
    private const string Smileys = "Smileys & People";
    private const string Animals = "Animals & Nature";
    private const string Food = "Food & Drink";
    private const string Activities = "Activities";
    private const string Travel = "Travel & Places";
    private const string Objects = "Objects";
    private const string Symbols = "Symbols";

    private static readonly string[] GroupOrder = [Smileys, Animals, Food, Activities, Travel, Objects, Symbols];

    // Which heading a character stands under, by where it is in Unicode: the first stretch
    // that holds it. Within a heading the characters stand in the order of these stretches, so
    // that the faces come first. What no stretch holds is a symbol.
    private static readonly (int From, int To, string Group)[] Stretches =
    [
        (0x1F600, 0x1F64F, Smileys), (0x1F90C, 0x1F93A, Smileys), (0x1F970, 0x1F97A, Smileys), (0x1FAE0, 0x1FAE8, Smileys),
        (0x263A, 0x263A, Smileys), (0x2639, 0x2639, Smileys), (0x2620, 0x2620, Smileys),
        (0x2763, 0x2764, Smileys), (0x1F493, 0x1F49F, Smileys), (0x1F5A4, 0x1F5A4, Smileys), (0x1F9E1, 0x1F9E1, Smileys),
        (0x1F4A2, 0x1F4AD, Smileys), (0x1F4AF, 0x1F4AF, Smileys), (0x1F48B, 0x1F48B, Smileys),
        (0x1F440, 0x1F450, Smileys), (0x261D, 0x261D, Smileys), (0x270A, 0x270D, Smileys), (0x1F590, 0x1F590, Smileys), (0x1F595, 0x1F596, Smileys),
        (0x1FAF0, 0x1FAF8, Smileys), (0x1F485, 0x1F485, Smileys), (0x1F9B5, 0x1F9B9, Smileys), (0x1F9BB, 0x1F9BB, Smileys), (0x1F9E0, 0x1F9E0, Smileys),
        (0x1FAC0, 0x1FAC5, Smileys), (0x1F464, 0x1F483, Smileys), (0x1F486, 0x1F487, Smileys), (0x1F48F, 0x1F48F, Smileys), (0x1F491, 0x1F491, Smileys),
        (0x1F574, 0x1F575, Smileys), (0x1F57A, 0x1F57A, Smileys), (0x1F5E3, 0x1F5E3, Smileys), (0x1F9CC, 0x1F9DF, Smileys),
        (0x1F9B4, 0x1F9B4, Smileys), (0x1F5E8, 0x1F5E8, Smileys), (0x1F5EF, 0x1F5EF, Smileys),

        (0x1F400, 0x1F43F, Animals), (0x1F980, 0x1F9AE, Animals), (0x1FAB0, 0x1FABF, Animals), (0x1FACE, 0x1FACF, Animals),
        (0x1F54A, 0x1F54A, Animals), (0x1F577, 0x1F578, Animals), (0x1F330, 0x1F343, Animals), (0x1F490, 0x1F490, Animals),
        (0x1F940, 0x1F940, Animals), (0x1F3F5, 0x1F3F5, Animals), (0x2618, 0x2618, Animals),

        (0x1F344, 0x1F37F, Food), (0x1F32D, 0x1F32F, Food), (0x1F950, 0x1F96F, Food), (0x1F9C0, 0x1F9CB, Food), (0x1FAD0, 0x1FADB, Food),
        (0x2615, 0x2615, Food), (0x1F52A, 0x1F52A, Food), (0x1F3FA, 0x1F3FA, Food),

        (0x1F380, 0x1F393, Activities), (0x1F396, 0x1F397, Activities), (0x1F39E, 0x1F39F, Activities), (0x1F3A0, 0x1F3D3, Activities),
        (0x1F3F8, 0x1F3F9, Activities), (0x1F93C, 0x1F94F, Activities), (0x1FA80, 0x1FA88, Activities),
        (0x26BD, 0x26BE, Activities), (0x26F3, 0x26F3, Activities), (0x26F7, 0x26F9, Activities),
        (0x1F004, 0x1F004, Activities), (0x1F0CF, 0x1F0CF, Activities), (0x265F, 0x2666, Activities), (0x2728, 0x2728, Activities),

        (0x1F680, 0x1F6FF, Travel), (0x1F3D4, 0x1F3F0, Travel), (0x1F5FA, 0x1F5FF, Travel), (0x1F300, 0x1F32C, Travel),
        (0x2600, 0x2604, Travel), (0x2614, 0x2614, Travel), (0x26A1, 0x26A1, Travel), (0x26C4, 0x26C8, Travel), (0x26E9, 0x26EA, Travel),
        (0x26F0, 0x26F5, Travel), (0x26FA, 0x26FA, Travel), (0x26FD, 0x26FD, Travel), (0x2708, 0x2708, Travel), (0x2B50, 0x2B50, Travel),
        (0x1FA90, 0x1FA90, Travel), (0x231A, 0x231B, Travel), (0x23F0, 0x23F3, Travel), (0x1F550, 0x1F567, Travel),
        (0x1F9ED, 0x1F9ED, Travel), (0x1F9F3, 0x1F9F3, Travel), (0x1F525, 0x1F525, Travel), (0x2744, 0x2744, Travel), (0x2668, 0x2668, Travel),
        (0x1F488, 0x1F488, Travel), (0x1F492, 0x1F492, Travel), (0x1F54B, 0x1F54D, Travel),

        (0x1F451, 0x1F463, Objects), (0x1F484, 0x1F484, Objects), (0x1F48C, 0x1F48E, Objects), (0x1F4A1, 0x1F4A1, Objects), (0x1F4B0, 0x1F4FF, Objects),
        (0x1F399, 0x1F39B, Objects), (0x1F50A, 0x1F517, Objects), (0x1F526, 0x1F52E, Objects), (0x1F56F, 0x1F573, Objects), (0x1F576, 0x1F576, Objects),
        (0x1F579, 0x1F579, Objects), (0x1F587, 0x1F58D, Objects), (0x1F5A5, 0x1F5A8, Objects), (0x1F5B1, 0x1F5B2, Objects), (0x1F5BC, 0x1F5BC, Objects),
        (0x1F5C2, 0x1F5C4, Objects), (0x1F5D1, 0x1F5D3, Objects), (0x1F5DC, 0x1F5DE, Objects), (0x1F5E1, 0x1F5E1, Objects), (0x1F5F3, 0x1F5F3, Objects),
        (0x1F97B, 0x1F97F, Objects), (0x1F9AF, 0x1F9AF, Objects), (0x1F9BA, 0x1F9BF, Objects), (0x1F9E2, 0x1F9FF, Objects),
        (0x1FA70, 0x1FA7C, Objects), (0x1FA91, 0x1FAAF, Objects), (0x2328, 0x2328, Objects), (0x260E, 0x260E, Objects),
        (0x2692, 0x2697, Objects), (0x2699, 0x2699, Objects), (0x26B0, 0x26B1, Objects), (0x26CF, 0x26CF, Objects), (0x26D1, 0x26D1, Objects),
        (0x26D3, 0x26D3, Objects), (0x2702, 0x2702, Objects), (0x2709, 0x2709, Objects), (0x270F, 0x270F, Objects), (0x2712, 0x2712, Objects),
        (0x1F489, 0x1F48A, Objects), (0x1F3F7, 0x1F3F7, Objects), (0x1F507, 0x1F509, Objects),
    ];

    private readonly Dictionary<string, ImageSource?> _pictures = new(StringComparer.Ordinal);
    private IReadOnlyList<EmojiItem>? _all;
    private GlyphTypeface? _face;
    private Dictionary<ushort, (int First, int Count)> _layersOf = [];
    private (ushort Glyph, ushort Colour)[] _layers = [];
    private Color[] _palette = [];

    // The glyph the font puts in the place of two that follow one another, by the two: a
    // flag for its two letters, a hand in a skin tone for the hand and the tone.
    private Dictionary<uint, ushort> _joined = [];

    /// <summary>Every emoji this PC can show, under its heading, the faces first. Empty when the font or the names are not to be had. Read once.</summary>
    public IReadOnlyList<EmojiItem> All => _all ??= Read();

    /// <summary>The picture of an emoji in colour, or null for a text that is not one of <see cref="All"/>. Made once for each.</summary>
    public ImageSource? Picture(string text)
    {
        if (_pictures.TryGetValue(text, out var known))
        {
            return known;
        }

        ImageSource? picture = null;
        try
        {
            picture = Draw(text);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or IOException)
        {
            // No picture for this one.
        }

        _pictures[text] = picture;
        return picture;
    }

    private ImageSource? Draw(string text)
    {
        _ = All;
        if (_face is not { } face || Glyph(face, text) is not { } glyph || !_layersOf.TryGetValue(glyph, out var layers))
        {
            return null;
        }

        // Drawn a hundred units to the em. Every picture has the same frame, the room the
        // font gives a character, so that a small sign is not blown up to the size of a face.
        const double Size = 100;
        var drawing = new DrawingGroup();
        double width = face.AdvanceWidths.TryGetValue(glyph, out double advance) && advance > 0 ? advance : 1;
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, -face.Baseline * Size, width * Size, face.Height * Size))));
        for (int i = layers.First; i < layers.First + layers.Count && i < _layers.Length; i++)
        {
            var (shape, colour) = _layers[i];
            Geometry outline = face.GetGlyphOutline(shape, Size, Size);
            if (outline.IsEmpty())
            {
                continue;
            }

            // 0xFFFF is "the colour of the text", which no emoji that is listed uses for much: a dark grey.
            var brush = new SolidColorBrush(colour < _palette.Length ? _palette[colour] : Color.FromRgb(0x30, 0x30, 0x30));
            brush.Freeze();
            drawing.Children.Add(new GeometryDrawing(brush, null, outline));
        }

        var picture = new DrawingImage(drawing);
        picture.Freeze();
        return picture;
    }

    // The glyph for an emoji: the glyph of its one character, or the one the font joins its
    // two characters into. The mark that asks for the emoji form (U+FE0F) is not a
    // character of its own here. Null for a text the font has no one glyph for.
    private ushort? Glyph(GlyphTypeface face, string text)
    {
        ushort? glyph = null;
        for (int i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
        {
            int code = char.ConvertToUtf32(text, i);
            if (code == VariationSelector)
            {
                continue;
            }

            if (!face.CharacterToGlyphMap.TryGetValue(code, out ushort next))
            {
                return null;
            }

            if (glyph is not { } first)
            {
                glyph = next;
            }
            else if (_joined.TryGetValue(((uint)first << 16) | next, out ushort joined))
            {
                glyph = joined;
            }
            else
            {
                return null;
            }
        }

        return glyph;
    }

    private const int VariationSelector = 0xFE0F;
    private const int FirstFlagLetter = 0x1F1E6;
    private const int FirstSkinTone = 0x1F3FB;
    private const string Flags = "Flags";

    // The flags: two letters of a flag that the font joins into one coloured glyph, named
    // by what Windows calls the country, and listed by that name.
    private List<EmojiItem> ReadFlags(GlyphTypeface face)
    {
        var flags = new List<EmojiItem>();
        for (int a = 0; a < 26; a++)
        {
            for (int b = 0; b < 26; b++)
            {
                string text = char.ConvertFromUtf32(FirstFlagLetter + a) + char.ConvertFromUtf32(FirstFlagLetter + b);
                if (Glyph(face, text) is { } glyph && _layersOf.ContainsKey(glyph) && Country("" + (char)('A' + a) + (char)('A' + b)) is { } country)
                {
                    flags.Add(new EmojiItem(text, "Flag: " + country, Flags));
                }
            }
        }

        flags.Sort((one, other) => string.Compare(one.Name, other.Name, StringComparison.OrdinalIgnoreCase));
        return flags;
    }

    // What Windows calls the country of a two-letter code, in English, or null when it has no name for it.
    private static string? Country(string code)
    {
        ushort[] name = new ushort[128];
        int length = DisplayCountry("und_" + code, "en", name, name.Length, out int error);
        if (error > 0 || length <= 0 || length > name.Length)
        {
            return null;
        }

        string country = string.Create(length, name, (span, units) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = (char)units[i];
            }
        });

        // A code Windows does not know comes back as the code itself.
        return country.Equals(code, StringComparison.OrdinalIgnoreCase) ? null : country;
    }

    private IReadOnlyList<EmojiItem> Read()
    {
        try
        {
            string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "seguiemj.ttf");
            if (!File.Exists(file) || !ReadColours(file))
            {
                return [];
            }

            var face = new GlyphTypeface(new Uri(file));
            var found = new List<(int Stretch, int Code, EmojiItem Emoji)>();
            foreach (var (code, glyph) in face.CharacterToGlyphMap)
            {
                // A character counts when the font draws it in colour, Windows says it is an
                // emoji, and it is not a piece that only makes other emoji (a digit, a skin
                // tone, a letter of a flag).
                if (code < 0x80
                    || !_layersOf.ContainsKey(glyph)
                    || HasProperty(code, EmojiProperty) == 0
                    || HasProperty(code, EmojiComponentProperty) != 0
                    || Name(code) is not { Length: > 0 } name)
                {
                    continue;
                }

                int stretch = Array.FindIndex(Stretches, one => code >= one.From && code <= one.To);
                string group = stretch >= 0 ? Stretches[stretch].Group : Symbols;

                // A character that is text unless it is told otherwise is told: the mark after
                // it (U+FE0F) asks for its emoji form wherever it is pasted.
                string text = char.ConvertFromUtf32(code) + (HasProperty(code, EmojiPresentationProperty) != 0 ? "" : "\uFE0F");

                // An emoji of a person or a hand can be had in a skin tone when Windows says so
                // and the font joins it with every one of the five tones.
                bool toned = HasProperty(code, EmojiModifierBaseProperty) != 0
                    && Enumerable.Range(0, 5).All(tone => face.CharacterToGlyphMap.TryGetValue(FirstSkinTone + tone, out ushort toneGlyph)
                        && _joined.TryGetValue(((uint)glyph << 16) | toneGlyph, out ushort tonedGlyph)
                        && _layersOf.ContainsKey(tonedGlyph));
                found.Add((stretch >= 0 ? stretch : int.MaxValue, code, new EmojiItem(text, name, group) { TakesSkinTone = toned }));
            }

            _face = face;
            return found
                .OrderBy(one => Array.IndexOf(GroupOrder, one.Emoji.Group))
                .ThenBy(one => one.Stretch)
                .ThenBy(one => one.Code)
                .Select(one => one.Emoji)
                .Concat(ReadFlags(face))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException
            or InvalidOperationException or ArgumentException or NotSupportedException or FileFormatException or UriFormatException)
        {
            // No font that can be read, or no names: no emoji.
            _face = null;
            return [];
        }
    }

    // What Windows calls the character, as a name to read: "FACE WITH TEARS OF JOY" is "Face With Tears Of Joy".
    private static string? Name(int code)
    {
        byte[] buffer = new byte[160];
        int length = CharName(code, UnicodeName, buffer, buffer.Length, out int error);
        if (error > 0 || length <= 0 || length > buffer.Length)
        {
            return null;
        }

        string name = Encoding.ASCII.GetString(buffer, 0, length).ToLowerInvariant();
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name);
    }

    // The two tables of the font that say how a character is drawn in colour. Numbers in a
    // font file have their largest byte first.
    private bool ReadColours(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] header = Bytes(stream, 0, 12);
        int tables = U16(header, 4);
        byte[] directory = Bytes(stream, 12, tables * 16);
        long colr = -1;
        long cpal = -1;
        long gsub = -1;
        int gsubLength = 0;
        for (int i = 0; i < tables; i++)
        {
            string tag = Encoding.ASCII.GetString(directory, i * 16, 4);
            if (tag == "COLR")
            {
                colr = U32(directory, (i * 16) + 8);
            }
            else if (tag == "CPAL")
            {
                cpal = U32(directory, (i * 16) + 8);
            }
            else if (tag == "GSUB")
            {
                gsub = U32(directory, (i * 16) + 8);
                gsubLength = (int)U32(directory, (i * 16) + 12);
            }
        }

        if (colr < 0 || cpal < 0)
        {
            return false;
        }

        // COLR: version, how many characters have layers, where their records are, where the
        // layers are, how many layers. A record: the glyph, its first layer, how many layers.
        // A layer: the glyph that is its shape, and which colour of the palette it has.
        byte[] colours = Bytes(stream, colr, 14);
        int bases = U16(colours, 2);
        long basesAt = colr + U32(colours, 4);
        long layersAt = colr + U32(colours, 8);
        int layerCount = U16(colours, 12);
        if (bases == 0 || layerCount == 0)
        {
            return false;
        }

        byte[] baseRecords = Bytes(stream, basesAt, bases * 6);
        var layersOf = new Dictionary<ushort, (int, int)>(bases);
        for (int i = 0; i < bases; i++)
        {
            layersOf[(ushort)U16(baseRecords, i * 6)] = (U16(baseRecords, (i * 6) + 2), U16(baseRecords, (i * 6) + 4));
        }

        byte[] layerRecords = Bytes(stream, layersAt, layerCount * 4);
        var layers = new (ushort, ushort)[layerCount];
        for (int i = 0; i < layerCount; i++)
        {
            layers[i] = ((ushort)U16(layerRecords, i * 4), (ushort)U16(layerRecords, (i * 4) + 2));
        }

        // CPAL: version, how many colours a palette has, how many palettes, how many colours
        // in all, where the colours are, and for each palette where its first colour is. The
        // first palette is used. A colour is blue, green, red and how solid it is.
        byte[] palettes = Bytes(stream, cpal, 14);
        int entries = U16(palettes, 2);
        long coloursAt = cpal + U32(palettes, 8);
        int first = U16(palettes, 12);
        byte[] records = Bytes(stream, coloursAt + (first * 4L), entries * 4);
        var palette = new Color[entries];
        for (int i = 0; i < entries; i++)
        {
            palette[i] = Color.FromArgb(records[(i * 4) + 3], records[(i * 4) + 2], records[(i * 4) + 1], records[i * 4]);
        }

        _layersOf = layersOf;
        _layers = layers;
        _palette = palette;
        _joined = gsub >= 0 && gsubLength is > 10 and < 16 * 1024 * 1024 ? Joined(Bytes(stream, gsub, gsubLength)) : [];
        return true;
    }

    // The GSUB table says which glyphs the font puts in the place of others. Of all that it
    // can say, only this is read: two glyphs that become one (a "ligature" of two). Its
    // lists of lookups are gone through, each lookup's tables of kind 4, and those reached
    // through a table of kind 7, which only points on to another. A table that does not
    // read as it should is left out: a font is trusted for nothing.
    private static Dictionary<uint, ushort> Joined(byte[] table)
    {
        var joined = new Dictionary<uint, ushort>();
        try
        {
            int lookups = U16(table, 8);
            int count = U16(table, lookups);
            for (int i = 0; i < count; i++)
            {
                int lookup = lookups + U16(table, lookups + 2 + (i * 2));
                int kind = U16(table, lookup);
                int tables = U16(table, lookup + 4);
                for (int j = 0; j < tables; j++)
                {
                    int at = lookup + U16(table, lookup + 6 + (j * 2));
                    int tableKind = kind;
                    if (tableKind == 7)
                    {
                        // An extension: the kind it stands for, and where that table is.
                        tableKind = U16(table, at + 2);
                        at += (int)U32(table, at + 4);
                    }

                    if (tableKind == 4 && U16(table, at) == 1)
                    {
                        Ligatures(table, at, joined);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            // What was read before the table stopped making sense is kept.
        }

        return joined;
    }

    // One table of ligatures: which first glyphs it is about (its "coverage"), and for each
    // of them a set of ligatures: the glyph that results, how many glyphs it is made of,
    // and the ones after the first.
    private static void Ligatures(byte[] table, int at, Dictionary<uint, ushort> joined)
    {
        var firsts = Coverage(table, at + U16(table, at + 2));
        int sets = U16(table, at + 4);
        for (int i = 0; i < sets && i < firsts.Count; i++)
        {
            int set = at + U16(table, at + 6 + (i * 2));
            int ligatures = U16(table, set);
            for (int j = 0; j < ligatures; j++)
            {
                int ligature = set + U16(table, set + 2 + (j * 2));
                if (U16(table, ligature + 2) == 2)
                {
                    joined[((uint)firsts[i] << 16) | (uint)U16(table, ligature + 4)] = (ushort)U16(table, ligature);
                }
            }
        }
    }

    // The glyphs a table is about, in the order the table counts them: a list of them, or
    // stretches of them.
    private static List<ushort> Coverage(byte[] table, int at)
    {
        var glyphs = new List<ushort>();
        int count = U16(table, at + 2);
        if (U16(table, at) == 1)
        {
            for (int i = 0; i < count; i++)
            {
                glyphs.Add((ushort)U16(table, at + 4 + (i * 2)));
            }
        }
        else if (U16(table, at) == 2)
        {
            for (int i = 0; i < count; i++)
            {
                int from = U16(table, at + 4 + (i * 6));
                int to = U16(table, at + 6 + (i * 6));
                for (int glyph = from; glyph <= to && glyph <= ushort.MaxValue; glyph++)
                {
                    glyphs.Add((ushort)glyph);
                }
            }
        }

        return glyphs;
    }

    private static byte[] Bytes(FileStream stream, long at, int count)
    {
        byte[] bytes = new byte[count];
        stream.Position = at;
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static int U16(byte[] bytes, int at) => (bytes[at] << 8) | bytes[at + 1];

    private static long U32(byte[] bytes, int at) => ((long)bytes[at] << 24) | ((long)bytes[at + 1] << 16) | ((long)bytes[at + 2] << 8) | bytes[at + 3];

    // The part of Windows that knows the characters of Unicode (icu.dll, there since Windows
    // 10 version 1903). u_charName(code, U_UNICODE_CHAR_NAME, buffer, length, error) writes a
    // character's name and answers its length; an error above zero is a failure.
    // HasProperty(code, property) answers whether a character has a property:
    // UCHAR_EMOJI is 57, UCHAR_EMOJI_PRESENTATION 58 and UCHAR_EMOJI_COMPONENT 61.
    private const int UnicodeName = 0;
    private const int EmojiProperty = 57;
    private const int EmojiPresentationProperty = 58;
    private const int EmojiModifierBaseProperty = 60;
    private const int EmojiComponentProperty = 61;

    // uloc_getDisplayCountry(locale, displayLocale, buffer, capacity, error) writes the name
    // of a locale's country in the language asked for, in UTF-16, and answers its length.
    [DllImport("icu.dll", EntryPoint = "uloc_getDisplayCountry", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern int DisplayCountry(string locale, string displayLocale, [Out] ushort[] country, int capacity, out int error);

    [DllImport("icu.dll", EntryPoint = "u_charName", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CharName(int code, int nameChoice, byte[] buffer, int bufferLength, out int error);

    [DllImport("icu.dll", EntryPoint = "u_hasBinaryProperty", CallingConvention = CallingConvention.Cdecl)]
    private static extern sbyte HasProperty(int code, int property);
}
