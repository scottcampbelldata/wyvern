using System.Globalization;

namespace Beacon.Core.Search;

/// <summary>
/// Works out a sum that is typed into the launcher and lists the answer as the first row:
/// <c>12 * (3 + 4)</c> gives <c>84</c>. Enter copies the answer. Only something that reads as a
/// whole sum and has an operator in it is worked out, so a plain number or a word lists nothing.
/// </summary>
/// <remarks>
/// It knows plus, minus, times (<c>*</c> or <c>x</c>), divided by (<c>/</c>), remainder
/// (<c>%</c>), to the power of (<c>^</c>), brackets, and a minus in front of a number. Numbers
/// are written with a point; a comma or a space between digits is taken as grouping and left
/// out. Nothing is looked up and nothing leaves the machine.
/// </remarks>
/// <param name="copy">Puts the answer on the clipboard.</param>
/// <param name="today">The day it is, for sums with dates. Left out everywhere except in tests: the machine's own date.</param>
public sealed class CalculatorProvider(Action<string> copy, Func<DateOnly>? today = null) : ISearchProvider
{
    /// <summary>The id of the answer's row. There is only ever one.</summary>
    public const string ItemId = "calculator:answer";

    /// <summary>Above every other match, so that the answer is the first row.</summary>
    public const int Score = 1_000_000;

    // The longest text that is looked at, and how deep brackets may go.
    private const int MaxLength = 200;
    private const int MaxDepth = 32;

    public IReadOnlyList<SearchResult> Search(string query)
    {
        // A sum, an amount in one unit asked for in another ("5 km in miles"), a percentage
        // ("15% of 80") or a question about dates ("days until 25 dec").
        string? found = Answer(query)
            ?? UnitConverter.Convert(query)
            ?? QuickSums.Percent(query)
            ?? QuickSums.Date(query, today?.Invoke() ?? DateOnly.FromDateTime(DateTime.Now));
        if (found is not { } answer)
        {
            return [];
        }

        return
        [
            new SearchResult(
                new SearchItem(ItemId, answer, query.Trim(), ItemKind.Calculation, () => copy(answer)),
                Score),
        ];
    }

    /// <summary>The answer to a sum as it is shown, or null when the text is not a sum that can be worked out.</summary>
    public static string? Answer(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Length > MaxLength || !HasOperator(text))
        {
            return null;
        }

        var reader = new Reader(text);
        if (!reader.Sum(0, out double value) || !reader.AtEnd || double.IsNaN(value) || double.IsInfinity(value))
        {
            return null;
        }

        // Fifteen figures are what a double holds exactly; rounding to twelve places hides the
        // dust that sums of decimals leave behind (0.1 + 0.2).
        double shown = Math.Round(value, 12);
        return shown.ToString(Math.Abs(shown) is >= 1e15 or (> 0 and < 1e-9) ? "G12" : "#,0.############", CultureInfo.InvariantCulture);
    }

    // A sum has an operator between two things. A minus in front of a single number is not one.
    private static bool HasOperator(string text)
    {
        for (int i = 1; i < text.Length; i++)
        {
            if (text[i] is '+' or '-' or '*' or '/' or '%' or '^' or 'x' or 'X')
            {
                return true;
            }
        }

        return false;
    }

    private ref struct Reader(string text)
    {
        private int _at;

        public bool AtEnd
        {
            get
            {
                SkipSpace();
                return _at == text.Length;
            }
        }

        // Plus and minus, which bind least.
        public bool Sum(int depth, out double value)
        {
            if (!Product(depth, out value))
            {
                return false;
            }

            while (true)
            {
                SkipSpace();
                if (Take('+'))
                {
                    if (!Product(depth, out double right))
                    {
                        return false;
                    }

                    value += right;
                }
                else if (Take('-'))
                {
                    if (!Product(depth, out double right))
                    {
                        return false;
                    }

                    value -= right;
                }
                else
                {
                    return true;
                }
            }
        }

        private bool Product(int depth, out double value)
        {
            if (!Power(depth, out value))
            {
                return false;
            }

            while (true)
            {
                SkipSpace();
                if (Take('*') || Take('x') || Take('X'))
                {
                    if (!Power(depth, out double right))
                    {
                        return false;
                    }

                    value *= right;
                }
                else if (Take('/'))
                {
                    if (!Power(depth, out double right) || right == 0)
                    {
                        return false;
                    }

                    value /= right;
                }
                else if (Take('%'))
                {
                    if (!Power(depth, out double right) || right == 0)
                    {
                        return false;
                    }

                    value %= right;
                }
                else
                {
                    return true;
                }
            }
        }

        // To the power of, which binds most and to the right: 2 ^ 3 ^ 2 is 2 ^ 9.
        private bool Power(int depth, out double value)
        {
            if (!Signed(depth, out value))
            {
                return false;
            }

            SkipSpace();
            if (Take('^'))
            {
                if (!Power(depth, out double exponent))
                {
                    return false;
                }

                value = Math.Pow(value, exponent);
            }

            return true;
        }

        private bool Signed(int depth, out double value)
        {
            SkipSpace();
            if (Take('-'))
            {
                if (!Signed(depth, out value))
                {
                    return false;
                }

                value = -value;
                return true;
            }

            if (Take('+'))
            {
                return Signed(depth, out value);
            }

            if (Take('('))
            {
                if (depth >= MaxDepth || !Sum(depth + 1, out value))
                {
                    value = 0;
                    return false;
                }

                SkipSpace();
                return Take(')');
            }

            return Number(out value);
        }

        // Digits with at most one point. A comma between digits is grouping and is left out.
        private bool Number(out double value)
        {
            Span<char> digits = stackalloc char[40];
            int length = 0;
            bool point = false;
            bool any = false;
            while (_at < text.Length && length < digits.Length)
            {
                char c = text[_at];
                if (char.IsAsciiDigit(c))
                {
                    digits[length++] = c;
                    any = true;
                }
                else if (c == '.' && !point)
                {
                    digits[length++] = c;
                    point = true;
                }
                else if (c == ',' && any && _at + 1 < text.Length && char.IsAsciiDigit(text[_at + 1]))
                {
                    // Grouping: 1,000.
                }
                else
                {
                    break;
                }

                _at++;
            }

            value = 0;
            return any && double.TryParse(digits[..length], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
        }

        private bool Take(char c)
        {
            if (_at < text.Length && text[_at] == c)
            {
                _at++;
                return true;
            }

            return false;
        }

        private void SkipSpace()
        {
            while (_at < text.Length && text[_at] == ' ')
            {
                _at++;
            }
        }
    }
}
