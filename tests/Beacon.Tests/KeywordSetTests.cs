using Beacon.Core.AutoExpand;
using Beacon.Core.Library;

namespace Beacon.Tests;

/// <summary>Which snippets take part in auto-expand, and which keyword a run of typed characters ends with.</summary>
public class KeywordSetTests
{
    private static Snippet Snippet(string name, string? keyword, string body = "the body") =>
        new(name + ".md", @"C:\lib\snippets\" + name + ".md", name, keyword, body);

    private static string? Matched(KeywordSet set, string typed) => set.Match(typed)?.Name;

    // Three characters outside ASCII, made from their numbers so that this file holds none:
    // an e with an acute accent, the accent as a character that combines with the letter
    // before it, and the section sign.
    private static readonly string EAcute = ((char)0xE9).ToString();
    private static readonly string CombiningAcute = ((char)0x301).ToString();
    private static readonly string Section = ((char)0xA7).ToString();

    // A character outside the basic plane (U+1F600), which is two UTF-16 units: a high one and
    // a low one. Typed characters arrive one unit at a time.
    private const string Grin = "\uD83D\uDE00";

    // The same, with another number (U+1F601): the same high unit and another low one.
    private const string Beam = "\uD83D\uDE01";

    [Fact]
    public void A_set_without_snippets_has_no_keywords_and_matches_nothing()
    {
        var set = new KeywordSet([]);

        Assert.Equal(0, set.Count);
        Assert.Equal(0, set.Longest);
        Assert.Equal(0, set.LeftOut);
        Assert.Null(set.Match(";sig"));
        Assert.Null(set.Match(""));
        Assert.Equal(0, KeywordSet.Empty.Count);
        Assert.Equal(0, KeywordSet.Empty.Longest);
    }

    [Fact]
    public void Only_snippets_with_a_keyword_take_part()
    {
        var set = new KeywordSet([Snippet("plain", null), Snippet("signature", ";sig"), Snippet("address", ";addr")]);

        Assert.Equal(2, set.Count);
        Assert.Equal(5, set.Longest);
        Assert.Equal(0, set.LeftOut);
    }

    [Fact]
    public void An_empty_keyword_is_no_keyword()
    {
        // The file reader gives null for an empty keyword; a snippet made any other way is
        // held to the same rule, or every key press would be a match.
        var set = new KeywordSet([Snippet("empty", "")]);

        Assert.Equal(0, set.Count);
        Assert.Equal(0, set.Longest);
        Assert.Null(set.Match("a"));
    }

    [Fact]
    public void Typed_characters_match_when_they_end_with_a_keyword()
    {
        var set = new KeywordSet([Snippet("signature", ";sig")]);

        Assert.Equal("signature", Matched(set, ";sig"));
        Assert.Equal("signature", Matched(set, "Dear all ;sig"));
        Assert.Equal("signature", Matched(set, ";sig;sig"));
    }

    [Theory]
    [InlineData(";si")]
    [InlineData("sig")]
    [InlineData(";sig ")]
    [InlineData(";sigx")]
    [InlineData("; sig")]
    [InlineData(";sig big")]
    [InlineData(";sigg")]
    [InlineData("")]
    public void Typed_characters_that_do_not_end_with_the_whole_keyword_do_not_match(string typed)
    {
        Assert.Null(new KeywordSet([Snippet("signature", ";sig")]).Match(typed));
    }

    [Theory]
    [InlineData(";SIG")]
    [InlineData(";Sig")]
    [InlineData(";siG")]
    public void The_comparison_is_exact_capitals_included(string typed)
    {
        Assert.Null(new KeywordSet([Snippet("signature", ";sig")]).Match(typed));
    }

    [Fact]
    public void Keywords_that_differ_only_in_capitals_are_two_keywords()
    {
        var set = new KeywordSet([Snippet("small", ";sig"), Snippet("large", ";SIG")]);

        Assert.Equal(2, set.Count);
        Assert.Equal("small", Matched(set, ";sig"));
        Assert.Equal("large", Matched(set, ";SIG"));
    }

    [Fact]
    public void Letters_that_only_look_alike_are_not_the_same()
    {
        // An e with an accent as one character, and as an e followed by a combining accent.
        var set = new KeywordSet([Snippet("cafe", "caf" + EAcute)]);

        Assert.Equal("cafe", Matched(set, "caf" + EAcute));
        Assert.Null(set.Match("cafe" + CombiningAcute));
        Assert.Null(set.Match("cafe"));
    }

    [Fact]
    public void Letters_that_only_look_alike_are_not_the_same_even_when_the_last_character_is_the_keywords_own()
    {
        // The test above ends in another character than its keyword does, so the look-up by the
        // last character turns it away before any comparison. These end in the keyword's own
        // last character and reach the comparison itself, which must be ordinal: it must not
        // take an e and a combining accent for an e with an accent, or skip a soft hyphen.
        var accented = new KeywordSet([Snippet("cafe", "caf" + EAcute + "!")]);

        Assert.Equal("cafe", Matched(accented, "caf" + EAcute + "!"));
        Assert.Null(accented.Match("cafe" + CombiningAcute + "!"));

        var signature = new KeywordSet([Snippet("signature", ";sig")]);

        Assert.Equal("signature", Matched(signature, ";sig"));
        Assert.Null(signature.Match(";s" + "\u00AD" + "ig"));
    }

    [Fact]
    public void When_one_keyword_is_the_ending_of_another_the_longer_one_wins_when_both_end_the_text()
    {
        var set = new KeywordSet([Snippet("short", "sig"), Snippet("long", ";worksig")]);

        Assert.Equal("long", Matched(set, ";worksig"));
        Assert.Equal("short", Matched(set, "xworksig"));
        Assert.Equal("short", Matched(set, "sig"));
    }

    [Fact]
    public void The_longer_one_wins_whatever_the_order_in_the_library()
    {
        // Shortest first, the opposite of the order the set sorts them into.
        var set = new KeywordSet([Snippet("short", "sig"), Snippet("middle", "ksig"), Snippet("long", ";worksig")]);

        Assert.Equal("long", Matched(set, ";worksig"));
        Assert.Equal("middle", Matched(set, "ksig"));
        Assert.Equal("short", Matched(set, "asig"));
    }

    [Fact]
    public void When_two_snippets_have_the_same_keyword_the_first_in_the_library_is_used_and_the_other_is_counted()
    {
        var set = new KeywordSet([Snippet("first", ";sig", "one"), Snippet("second", ";sig", "two"), Snippet("third", ";sig", "three")]);

        Assert.Equal(1, set.Count);
        Assert.Equal(2, set.LeftOut);
        Assert.Equal("first", Matched(set, ";sig"));
    }

    [Fact]
    public void A_keyword_of_64_characters_takes_part_and_one_of_65_does_not()
    {
        string longest = new('k', 64);
        string tooLong = new('q', 65);
        var set = new KeywordSet([Snippet("fits", longest), Snippet("too long", tooLong)]);

        Assert.Equal(64, KeywordSet.MaxKeywordLength);
        Assert.Equal(1, set.Count);
        Assert.Equal(1, set.LeftOut);
        Assert.Equal(64, set.Longest);
        Assert.Equal("fits", Matched(set, longest));
        Assert.Null(set.Match(tooLong));
    }

    [Fact]
    public void A_keyword_that_is_too_long_does_not_make_the_longest_any_longer()
    {
        var set = new KeywordSet([Snippet("short", ";a"), Snippet("too long", new string('q', 200))]);

        Assert.Equal(2, set.Longest);
    }

    [Fact]
    public void A_keyword_of_one_character_matches_that_character()
    {
        var set = new KeywordSet([Snippet("section", Section)]);

        Assert.Equal(1, set.Longest);
        Assert.Equal("section", Matched(set, "see " + Section));
        Assert.Null(set.Match("see "));
    }

    [Fact]
    public void A_keyword_may_hold_a_space()
    {
        var set = new KeywordSet([Snippet("regards", "kind regards")]);

        Assert.Equal("regards", Matched(set, "with kind regards"));
        Assert.Null(set.Match("with kindregards"));
    }

    [Fact]
    public void Many_keywords_that_end_with_the_same_character_are_all_found()
    {
        var snippets = Enumerable.Range(1, 40).Select(i => Snippet("n" + i, new string('a', i) + "!")).ToList();
        var set = new KeywordSet(snippets);

        Assert.Equal(40, set.Count);
        Assert.Equal(41, set.Longest);

        // Each text ends with exactly the keywords of its length and shorter; the longest of them is the answer.
        for (int i = 1; i <= 40; i++)
        {
            Assert.Equal("n" + i, Matched(set, "x" + new string('a', i) + "!"));
        }
    }

    [Fact]
    public void A_keyword_with_a_character_outside_the_basic_plane_matches_only_when_its_last_unit_is_there()
    {
        string keyword = ";" + Grin;
        var set = new KeywordSet([Snippet("grin", keyword)]);

        // The keyword is three UTF-16 units, and every shorter start of it, the first unit of
        // the pair included, is not yet the keyword.
        Assert.Equal(3, keyword.Length);
        Assert.Equal(3, set.Longest);
        for (int units = 1; units < keyword.Length; units++)
        {
            Assert.Null(set.Match(keyword.AsSpan(0, units)));
        }

        Assert.Equal("grin", Matched(set, keyword));
        Assert.Equal("grin", Matched(set, "say " + keyword));

        // The last unit alone is not enough: the whole keyword is compared. The last of these
        // ends with the keyword's last unit but has another first unit in the pair.
        Assert.Null(set.Match(Grin));
        Assert.Null(set.Match(";" + Beam));
        Assert.Null(set.Match(";\uD83E\uDE00"));
    }

    [Fact]
    public void A_keyword_of_32_characters_outside_the_basic_plane_takes_part_and_one_of_33_is_left_out()
    {
        string fits = string.Concat(Enumerable.Repeat(Grin, 32));
        string tooLong = string.Concat(Enumerable.Repeat(Beam, 33));
        var set = new KeywordSet([Snippet("fits", fits), Snippet("too long", tooLong)]);

        // The limit counts UTF-16 units: 32 of these characters are 64 of them, 33 are 66.
        Assert.Equal(64, fits.Length);
        Assert.Equal(66, tooLong.Length);
        Assert.Equal(1, set.Count);
        Assert.Equal(1, set.LeftOut);
        Assert.Equal(64, set.Longest);
        Assert.Equal("fits", Matched(set, fits));
        Assert.Null(set.Match(tooLong));
    }
}
