using System.Text;

namespace Beacon.Core.Scripts;

/// <summary>
/// Text that is added to piece by piece and keeps only its newest part once it has grown past a
/// limit. It holds what a script printed: in memory only, and never more than the limit however
/// much the script prints. One thread may add while another reads.
/// </summary>
public sealed class CappedText
{
    private readonly int _maxChars;
    private readonly StringBuilder _text = new();
    private readonly Lock _gate = new();

    /// <param name="maxChars">The most characters that are kept, counted as .NET counts them (UTF-16).</param>
    public CappedText(int maxChars)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChars);
        _maxChars = maxChars;
    }

    /// <summary>
    /// How many characters are held in memory at this moment. It is never more than twice the
    /// limit once a piece has been added, however much was added before.
    /// </summary>
    public int HeldChars
    {
        get
        {
            lock (_gate)
            {
                return _text.Length;
            }
        }
    }

    public void Append(ReadOnlySpan<char> piece)
    {
        lock (_gate)
        {
            _text.Append(piece);

            // The oldest part is dropped only once the text is twice too long, and then all at
            // once, so that a script printing without end does not have its output moved along
            // for every piece that arrives.
            if (_text.Length > 2L * _maxChars)
            {
                _text.Remove(0, _text.Length - _maxChars);
            }
        }
    }

    /// <summary>The newest part of what was added, at most the limit long.</summary>
    public override string ToString()
    {
        lock (_gate)
        {
            int start = Math.Max(0, _text.Length - _maxChars);

            // A character outside the basic plane, such as an emoji, takes two positions. When
            // the oldest part was dropped between the two, what is left starts with the second
            // half, which is not a character by itself.
            if (start < _text.Length && char.IsLowSurrogate(_text[start]))
            {
                start++;
            }

            return _text.ToString(start, _text.Length - start);
        }
    }
}
