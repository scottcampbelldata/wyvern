using Wyvern.Core.Library;

namespace Wyvern.Core.AutoExpand;

/// <summary>
/// The last few characters the owner typed, kept only to tell when they end with a snippet's
/// keyword. This is the whole of what Wyvern remembers of typing.
/// </summary>
/// <remarks>
/// <para>It holds as many characters as the longest keyword has and never more than
/// <see cref="KeywordSet.MaxKeywordLength"/>; with no keywords it holds none at all. The
/// characters are in memory only. Nothing here writes them anywhere, hands them out or puts
/// them into a text: there is no member that reads them back. Emptying it overwrites them, and
/// the array that holds them is allocated pinned, so that the garbage collector never moves it
/// and leaves no other copy of them behind.</para>
/// <para>Use from one thread only, the one the keyboard hook calls on. The class has no lock
/// and is not safe to use from two threads.</para>
/// </remarks>
public sealed class TypingBuffer
{
    private KeywordSet _keywords = KeywordSet.Empty;

    // No room to begin with, and so nothing to hold. Every array that has room is made in Use.
    private char[] _characters = [];
    private int _length;

    /// <summary>How many characters are held now.</summary>
    public int Length => _length;

    /// <summary>The most characters that are ever held with the keywords in use.</summary>
    public int Capacity => _characters.Length;

    /// <summary>
    /// True when no character is left anywhere in the memory this buffer uses, also beyond
    /// <see cref="Length"/>. For tests: it shows that emptying overwrites what was typed.
    /// </summary>
    public bool IsBlank => Array.TrueForAll(_characters, character => character == '\0');

    /// <summary>
    /// Watches for these keywords from now on. What is held stays, as far as it still fits:
    /// the newest characters are kept, so a keyword that is half typed when the library is
    /// loaded again can still be finished.
    /// </summary>
    public void Use(KeywordSet keywords)
    {
        int capacity = Math.Min(keywords.Longest, KeywordSet.MaxKeywordLength);
        if (capacity != _characters.Length)
        {
            // Allocated pinned, so that the garbage collector never moves it. A collection that
            // compacts the memory copies an ordinary array and leaves the old copy, with the
            // typed characters in it, in freed memory, where emptying this one would not reach
            // it. The array is zeroed when it is made. One with no room needs none of this.
            char[] kept = capacity == 0 ? [] : GC.AllocateArray<char>(capacity, pinned: true);
            int keep = Math.Min(_length, capacity);
            Array.Copy(_characters, _length - keep, kept, 0, keep);
            Array.Clear(_characters);
            _characters = kept;
            _length = keep;
        }

        _keywords = keywords;
    }

    /// <summary>
    /// A character was typed. When the characters held now end with a keyword, the buffer is
    /// emptied and that keyword's snippet is the answer; the longest such keyword wins.
    /// </summary>
    /// <returns>The snippet to paste in place of its keyword, or null.</returns>
    public Snippet? Add(char character)
    {
        if (_characters.Length == 0)
        {
            return null;
        }

        if (_length == _characters.Length)
        {
            // Full: the oldest character goes, which no keyword could reach any more.
            Array.Copy(_characters, 1, _characters, 0, _length - 1);
            _length--;
        }

        _characters[_length++] = character;
        var match = _keywords.Match(_characters.AsSpan(0, _length));
        if (match is not null)
        {
            Clear();
        }

        return match;
    }

    /// <summary>Backspace was pressed: the newest character is forgotten, when there is one.</summary>
    public void Backspace()
    {
        if (_length > 0)
        {
            _characters[--_length] = '\0';
        }
    }

    /// <summary>The run of typing was broken: everything held is overwritten and forgotten.</summary>
    public void Clear()
    {
        Array.Clear(_characters);
        _length = 0;
    }
}
