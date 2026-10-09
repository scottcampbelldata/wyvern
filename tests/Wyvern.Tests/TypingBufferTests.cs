using System.Diagnostics;
using System.Reflection;
using Wyvern.Core.AutoExpand;
using Wyvern.Core.Library;

namespace Wyvern.Tests;

/// <summary>
/// What Wyvern remembers of typing: a few characters, and never more than the longest keyword
/// needs. Everything typed here is made up, and nothing is typed anywhere but into the buffer.
/// </summary>
public class TypingBufferTests
{
    private static Snippet Snippet(string name, string? keyword, string body = "the body") =>
        new(name + ".md", @"C:\lib\snippets\" + name + ".md", name, keyword, body);

    // The section sign, made from its number so that this file holds no character outside ASCII.
    private static readonly string Section = ((char)0xA7).ToString();

    // Two characters outside the basic plane (U+1F600 and U+1F601), each two UTF-16 units. The
    // buffer is fed one unit at a time, as typed characters arrive.
    private const string Grin = "\uD83D\uDE00";
    private const string Beam = "\uD83D\uDE01";

    private static TypingBuffer BufferFor(params Snippet[] snippets)
    {
        var buffer = new TypingBuffer();
        buffer.Use(new KeywordSet(snippets));
        return buffer;
    }

    /// <summary>Types the characters one by one and gives the names of the snippets that fired, in order.</summary>
    private static List<string> Type(TypingBuffer buffer, string text)
    {
        var fired = new List<string>();
        foreach (char character in text)
        {
            if (buffer.Add(character) is { } snippet)
            {
                fired.Add(snippet.Name);
            }
        }

        return fired;
    }

    [Fact]
    public void A_new_buffer_holds_nothing_and_keeps_nothing()
    {
        var buffer = new TypingBuffer();

        Assert.Equal(0, buffer.Capacity);
        Assert.Empty(Type(buffer, "anything at all ;sig"));
        Assert.Equal(0, buffer.Length);
        Assert.True(buffer.IsBlank);
    }

    [Fact]
    public void With_no_keywords_nothing_that_is_typed_is_kept()
    {
        var buffer = BufferFor(Snippet("plain", null));

        Assert.Empty(Type(buffer, "the-private-walrus"));

        Assert.Equal(0, buffer.Capacity);
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void A_snippet_fires_at_the_moment_the_last_character_of_its_keyword_is_typed()
    {
        var buffer = BufferFor(Snippet("signature", ";sig"));

        Assert.Null(buffer.Add(';'));
        Assert.Null(buffer.Add('s'));
        Assert.Null(buffer.Add('i'));
        Assert.Equal("signature", buffer.Add('g')?.Name);
    }

    [Fact]
    public void A_keyword_fires_in_the_middle_of_other_typing()
    {
        var buffer = BufferFor(Snippet("signature", ";sig"));

        Assert.Equal(["signature"], Type(buffer, "Dear all, ;sig and more"));
    }

    [Fact]
    public void After_firing_the_buffer_is_empty_and_blank()
    {
        var buffer = BufferFor(Snippet("signature", ";sig"));

        Type(buffer, "ab;sig");

        Assert.Equal(0, buffer.Length);
        Assert.True(buffer.IsBlank);
    }

    [Fact]
    public void A_keyword_typed_twice_fires_twice()
    {
        var buffer = BufferFor(Snippet("signature", ";sig"));

        Assert.Equal(["signature", "signature"], Type(buffer, ";sig;sig"));
    }

    [Fact]
    public void What_was_typed_before_a_keyword_fired_is_no_part_of_the_next_match()
    {
        // "ab" fires at the b. The a and b are then gone, so the c that follows does not make "abc".
        var buffer = BufferFor(Snippet("short", "ab"), Snippet("long", "abc"));

        Assert.Equal(["short"], Type(buffer, "abc"));
    }

    [Fact]
    public void A_keyword_that_is_the_start_of_another_fires_first_and_the_longer_one_never_does()
    {
        var buffer = BufferFor(Snippet("short", ";s"), Snippet("long", ";sig"));

        Assert.Equal(["short"], Type(buffer, ";sig"));
    }

    [Fact]
    public void When_one_keyword_is_the_ending_of_another_the_longer_one_fires_when_both_end_what_was_typed()
    {
        var buffer = BufferFor(Snippet("short", "sig"), Snippet("long", ";worksig"));

        Assert.Equal(["long"], Type(buffer, ";worksig"));
        Assert.Equal(["short"], Type(buffer, "my sig"));
    }

    [Fact]
    public void The_comparison_is_exact_capitals_included()
    {
        var buffer = BufferFor(Snippet("signature", ";sig"));

        Assert.Empty(Type(buffer, ";SIG ;Sig ;siG"));
    }

    [Fact]
    public void Backspace_forgets_the_newest_character_so_a_slip_can_be_put_right()
    {
        var buffer = BufferFor(Snippet("signature", ";sig"));

        Type(buffer, ";sif");
        buffer.Backspace();

        Assert.Equal(3, buffer.Length);
        Assert.Equal("signature", buffer.Add('g')?.Name);
    }

    [Fact]
    public void Backspace_on_an_empty_buffer_does_nothing()
    {
        var buffer = BufferFor(Snippet("signature", ";sig"));

        buffer.Backspace();
        buffer.Backspace();

        Assert.Equal(0, buffer.Length);
        Assert.Equal(["signature"], Type(buffer, ";sig"));
    }

    [Fact]
    public void Backspace_overwrites_the_character_it_forgets()
    {
        var buffer = BufferFor(Snippet("signature", ";sig"));

        Type(buffer, "x");
        buffer.Backspace();

        Assert.True(buffer.IsBlank);
    }

    [Fact]
    public void Backspace_takes_away_as_many_characters_as_it_is_pressed()
    {
        var buffer = BufferFor(Snippet("signature", ";sig"));

        Type(buffer, ";sxy");
        buffer.Backspace();
        buffer.Backspace();

        Assert.Equal(["signature"], Type(buffer, "ig"));
    }

    [Fact]
    public void A_break_in_the_typing_empties_the_buffer_and_overwrites_it()
    {
        var buffer = BufferFor(Snippet("signature", ";sig"));

        Type(buffer, ";si");
        Assert.False(buffer.IsBlank);
        buffer.Clear();

        Assert.Equal(0, buffer.Length);
        Assert.True(buffer.IsBlank);
        Assert.Null(buffer.Add('g'));
    }

    [Fact]
    public void The_buffer_holds_as_many_characters_as_the_longest_keyword_and_no_more()
    {
        var buffer = BufferFor(Snippet("signature", ";sig"), Snippet("address", ";address"));

        Assert.Equal(8, buffer.Capacity);
        Type(buffer, "a long sentence that goes on and on without any keyword in it");

        Assert.Equal(8, buffer.Length);
    }

    [Fact]
    public void The_buffer_never_holds_more_than_64_characters_whatever_the_keywords()
    {
        // A keyword of 65 characters takes no part, so it does not make the buffer any longer.
        var buffer = BufferFor(Snippet("longest", new string('k', 64)), Snippet("too long", new string('q', 65)));

        Assert.Equal(64, buffer.Capacity);
        Type(buffer, new string('x', 500));

        Assert.Equal(64, buffer.Length);
    }

    [Fact]
    public void A_keyword_of_64_characters_still_fires()
    {
        string keyword = new string('k', 63) + "!";
        var buffer = BufferFor(Snippet("longest", keyword));

        Assert.Equal(["longest"], Type(buffer, "before " + keyword));
    }

    [Fact]
    public void A_full_buffer_drops_its_oldest_character_and_still_matches_at_its_end()
    {
        var buffer = BufferFor(Snippet("signature", ";sig"));

        Assert.Equal(["signature"], Type(buffer, "0123456789;sig"));
    }

    [Fact]
    public void A_keyword_of_one_character_fires_at_every_such_character()
    {
        var buffer = BufferFor(Snippet("section", Section));

        Assert.Equal(1, buffer.Capacity);
        Assert.Equal(["section", "section"], Type(buffer, "a" + Section + "b" + Section));
    }

    [Fact]
    public void A_keyword_with_a_character_outside_the_basic_plane_fires_at_its_last_unit_and_the_buffer_counts_units()
    {
        var buffer = BufferFor(Snippet("grin", ";" + Grin));

        Assert.Equal(3, buffer.Capacity);
        Assert.Null(buffer.Add(';'));
        Assert.Null(buffer.Add(Grin[0]));
        Assert.Equal("grin", buffer.Add(Grin[1])?.Name);
        Assert.Equal(0, buffer.Length);

        // A keyword of 32 such characters is 64 units and fires; one of 33 is 66 units, takes
        // no part and does not make the buffer longer than 64.
        string fits = string.Concat(Enumerable.Repeat(Grin, 32));
        string tooLong = string.Concat(Enumerable.Repeat(Beam, 33));
        buffer = BufferFor(Snippet("fits", fits), Snippet("too long", tooLong));

        Assert.Equal(64, buffer.Capacity);
        Assert.Empty(Type(buffer, tooLong));
        Assert.Equal(["fits"], Type(buffer, fits));
    }

    [Fact]
    public void A_keyword_that_is_half_typed_when_the_keywords_change_can_still_be_finished()
    {
        var buffer = BufferFor(Snippet("signature", ";sig", "the old body"));

        Type(buffer, ";si");
        buffer.Use(new KeywordSet([Snippet("signature", ";sig", "the new body"), Snippet("address", ";address")]));

        var fired = buffer.Add('g');
        Assert.Equal("the new body", fired?.Body);
    }

    [Fact]
    public void A_keyword_that_has_gone_from_the_library_no_longer_fires()
    {
        var buffer = BufferFor(Snippet("signature", ";sig"), Snippet("address", ";addr"));

        Type(buffer, ";si");
        buffer.Use(new KeywordSet([Snippet("address", ";addr")]));

        Assert.Null(buffer.Add('g'));
    }

    [Fact]
    public void When_the_keywords_get_shorter_only_the_newest_characters_are_kept()
    {
        var buffer = BufferFor(Snippet("address", ";address"), Snippet("go", "go"));

        Type(buffer, "abcdefg");
        buffer.Use(new KeywordSet([Snippet("go", "go")]));

        Assert.Equal(2, buffer.Capacity);
        Assert.Equal(2, buffer.Length);

        // The two that were kept are the newest, "fg". An "o" pushes the "f" out and leaves "go".
        Assert.Equal("go", buffer.Add('o')?.Name);
    }

    [Fact]
    public void When_the_last_keyword_goes_nothing_is_held_any_more()
    {
        var buffer = BufferFor(Snippet("signature", ";sig"));

        Type(buffer, ";si");
        buffer.Use(KeywordSet.Empty);

        Assert.Equal(0, buffer.Capacity);
        Assert.Equal(0, buffer.Length);
        Assert.True(buffer.IsBlank);
        Assert.Null(buffer.Add('g'));
    }

    [Fact]
    public void When_the_keywords_get_longer_what_is_held_stays()
    {
        var buffer = BufferFor(Snippet("go", "go"));

        Type(buffer, "g");
        buffer.Use(new KeywordSet([Snippet("go", "go"), Snippet("address", ";address")]));

        Assert.Equal(8, buffer.Capacity);
        Assert.Equal(1, buffer.Length);
        Assert.Equal("go", buffer.Add('o')?.Name);
    }

    [Fact]
    public void The_same_keywords_given_again_change_nothing_that_is_held()
    {
        var buffer = BufferFor(Snippet("signature", ";sig"));

        Type(buffer, ";si");
        buffer.Use(new KeywordSet([Snippet("signature", ";sig")]));

        Assert.Equal(3, buffer.Length);
        Assert.Equal("signature", buffer.Add('g')?.Name);
    }

    [Fact]
    public void The_buffer_has_no_member_that_gives_the_characters_back()
    {
        // What is typed can be counted and matched and nothing else. The whole public surface of
        // the buffer is written out below and nothing may be added to it: a member that was
        // added later fails this test, and has to be put in the list on purpose, after someone
        // has seen that it does not give the characters back. Add answers with a snippet or
        // null (Snippet? in the code, which reflection shows as Snippet): the snippet whose
        // keyword was typed, never the typed characters.
        var members = typeof(TypingBuffer)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(member => member.DeclaringType == typeof(TypingBuffer))
            .ToList();

        // The accessors of a property are part of the property, which is described with them.
        var described = members
            .Where(member => member is not MethodInfo { IsSpecialName: true } accessor
                || !(accessor.Name.StartsWith("get_", StringComparison.Ordinal)
                    || accessor.Name.StartsWith("set_", StringComparison.Ordinal)))
            .Select(Describe)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            [
                "constructor()",
                "method Add(char): Snippet",
                "method Backspace(): void",
                "method Clear(): void",
                "method Use(KeywordSet): void",
                "property Capacity: int, get, no set",
                "property IsBlank: bool, get, no set",
                "property Length: int, get, no set",
            ],
            described);

        // No way to hand the characters out through a parameter or a returned reference either.
        Assert.DoesNotContain(members.OfType<MethodBase>().SelectMany(method => method.GetParameters()),
            parameter => parameter.ParameterType.IsByRef);
        Assert.DoesNotContain(members.OfType<MethodInfo>(), method => method.ReturnType.IsByRef);
        Assert.DoesNotContain(members.OfType<PropertyInfo>(), property => property.PropertyType.IsByRef);

        // No public field and no public nested type.
        Assert.DoesNotContain(members, member => member.MemberType is MemberTypes.Field);
        Assert.DoesNotContain(members, member => member.MemberType is MemberTypes.NestedType);

        // The list above holds public members only. An interface that the buffer implements
        // (say IEnumerable<char>, through members that are not public) would give the characters
        // away without adding a line to it, and so would a debugger display or type proxy,
        // which shows what the buffer holds to anyone who stops a program with one inside.
        Assert.Empty(typeof(TypingBuffer).GetInterfaces());
        Assert.Empty(typeof(TypingBuffer).GetCustomAttributes<DebuggerDisplayAttribute>(inherit: true));
        Assert.Empty(typeof(TypingBuffer).GetCustomAttributes<DebuggerTypeProxyAttribute>(inherit: true));

        Assert.Equal(typeof(TypingBuffer).FullName, new TypingBuffer().ToString());
    }

    /// <summary>One line for a member: its kind, name, types, and for a property whether it can be read and written.</summary>
    private static string Describe(MemberInfo member)
    {
        switch (member)
        {
            case ConstructorInfo constructor:
                return $"constructor({ParameterTypes(constructor)})";
            case MethodInfo method:
                string methodKind = method.IsStatic ? "static method" : "method";
                return $"{methodKind} {method.Name}({ParameterTypes(method)}): {TypeName(method.ReturnType)}";
            case PropertyInfo property:
                bool isStatic = property.GetMethod?.IsStatic == true || property.SetMethod?.IsStatic == true;
                string propertyKind = isStatic ? "static property" : "property";
                string read = property.GetMethod?.IsPublic == true ? "get" : "no get";
                string write = property.SetMethod?.IsPublic == true ? "set" : "no set";
                return $"{propertyKind} {property.Name}: {TypeName(property.PropertyType)}, {read}, {write}";
            default:
                return $"{member.MemberType} {member.Name}";
        }
    }

    private static string ParameterTypes(MethodBase method) =>
        string.Join(", ", method.GetParameters().Select(parameter => TypeName(parameter.ParameterType)));

    private static string TypeName(Type type) =>
        type == typeof(void) ? "void"
        : type == typeof(int) ? "int"
        : type == typeof(bool) ? "bool"
        : type == typeof(char) ? "char"
        : type.Name;
}
