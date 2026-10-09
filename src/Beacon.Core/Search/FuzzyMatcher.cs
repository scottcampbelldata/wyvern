using System.Buffers;

namespace Beacon.Core.Search;

/// <summary>
/// Scores how well a typed query matches a candidate string. The query must appear in the
/// candidate as a case-insensitive subsequence. Matches at the start of the candidate, at the
/// start of a word, and in unbroken runs score higher.
/// </summary>
public static class FuzzyMatcher
{
    private const int MatchBase = 10;
    private const int FirstCharBonus = 40;
    private const int WordStartBonus = 30;
    private const int ContiguousBonus = 20;
    private const int MaxLeadingPenalty = 5;
    private const int MaxLengthPenalty = 20;

    // Added to every score so the two penalties can never cancel the points for a match.
    // Scores must stay positive: the search engine multiplies them by a frecency boost,
    // and a boost applied to a negative score would push a favourite item down.
    private const int ScoreFloor = MaxLeadingPenalty + MaxLengthPenalty;

    private const int Unreachable = int.MinValue / 2;

    // Working space up to this many characters or cells lives on the stack; anything longer is rented.
    private const int StackLimit = 256;

    /// <returns>A positive score, or null when the query is empty or is not a subsequence of the candidate.</returns>
    public static int? Score(string query, string candidate)
    {
        if (query.Length == 0 || query.Length > candidate.Length)
        {
            return null;
        }

        Span<char> lowerQuery = query.Length <= StackLimit ? stackalloc char[query.Length] : new char[query.Length];
        Span<char> lowerCandidate = candidate.Length <= StackLimit ? stackalloc char[candidate.Length] : new char[candidate.Length];
        ToLower(query, lowerQuery);
        ToLower(candidate, lowerCandidate);
        return ScoreLowered(lowerQuery, candidate, lowerCandidate);
    }

    /// <summary>
    /// The same scoring for a caller that already holds lower-cased copies. The file index keeps
    /// one per name so that a keystroke does not lower-case hundreds of thousands of names again.
    /// Allocates nothing on the heap.
    /// </summary>
    /// <param name="lowerQuery">The query, lower-cased with <see cref="ToLower"/>.</param>
    /// <param name="candidate">The candidate as written; its capitals mark camel-case word starts.</param>
    /// <param name="lowerCandidate">The candidate, lower-cased with <see cref="ToLower"/>. Same length as <paramref name="candidate"/>.</param>
    public static int? ScoreLowered(ReadOnlySpan<char> lowerQuery, ReadOnlySpan<char> candidate, ReadOnlySpan<char> lowerCandidate)
    {
        int n = lowerQuery.Length;
        int m = candidate.Length;
        if (n == 0 || n > m || !IsSubsequence(lowerQuery, lowerCandidate))
        {
            return null;
        }

        // Four rows of m cells. For the query character being processed:
        //   matchAt[j]  = best score with that character matched exactly at candidate[j]
        //   bestUpTo[j] = best score with that character matched at or before candidate[j]
        // The "previous" rows hold the same values for the preceding query character. Every cell
        // is written before it is read, so the rows need no clearing.
        int[]? rented = null;
        Span<int> rows = m * 4 <= StackLimit ? stackalloc int[m * 4] : (rented = ArrayPool<int>.Shared.Rent(m * 4));
        try
        {
            Span<int> previousMatchAt = rows.Slice(0, m);
            Span<int> previousBestUpTo = rows.Slice(m, m);
            Span<int> matchAt = rows.Slice(2 * m, m);
            Span<int> bestUpTo = rows.Slice(3 * m, m);

            for (int i = 0; i < n; i++)
            {
                char wanted = lowerQuery[i];
                for (int j = 0; j < m; j++)
                {
                    int score = Unreachable;
                    if (lowerCandidate[j] == wanted)
                    {
                        if (i == 0)
                        {
                            score = MatchBase + PositionBonus(candidate, j) - Math.Min(j, MaxLeadingPenalty);
                        }
                        else if (j > 0)
                        {
                            int viaRun = previousMatchAt[j - 1] > Unreachable
                                ? previousMatchAt[j - 1] + ContiguousBonus
                                : Unreachable;
                            int best = Math.Max(viaRun, previousBestUpTo[j - 1]);
                            if (best > Unreachable)
                            {
                                score = best + MatchBase + PositionBonus(candidate, j);
                            }
                        }
                    }

                    matchAt[j] = score;
                    bestUpTo[j] = j == 0 ? score : Math.Max(score, bestUpTo[j - 1]);
                }

                Span<int> swap = previousMatchAt;
                previousMatchAt = matchAt;
                matchAt = swap;

                swap = previousBestUpTo;
                previousBestUpTo = bestUpTo;
                bestUpTo = swap;
            }

            return ScoreFloor + previousBestUpTo[m - 1] - Math.Min(m - n, MaxLengthPenalty);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<int>.Shared.Return(rented);
            }
        }
    }

    /// <summary>Lower-cases character by character, so the result always has the length of the source.</summary>
    public static void ToLower(ReadOnlySpan<char> source, Span<char> destination)
    {
        for (int i = 0; i < source.Length; i++)
        {
            destination[i] = char.ToLowerInvariant(source[i]);
        }
    }

    private static bool IsSubsequence(ReadOnlySpan<char> lowerQuery, ReadOnlySpan<char> lowerCandidate)
    {
        int i = 0;
        foreach (char c in lowerCandidate)
        {
            if (c == lowerQuery[i] && ++i == lowerQuery.Length)
            {
                return true;
            }
        }

        return false;
    }

    private static int PositionBonus(ReadOnlySpan<char> candidate, int index)
    {
        if (index == 0)
        {
            return FirstCharBonus;
        }

        char previous = candidate[index - 1];
        bool afterSeparator = previous is ' ' or '-' or '_' or '.' or '/' or '\\' or '(' or '[';
        bool camelHump = char.IsLower(previous) && char.IsUpper(candidate[index]);
        return afterSeparator || camelHump ? WordStartBonus : 0;
    }
}
