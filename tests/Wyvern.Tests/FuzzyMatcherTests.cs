using Wyvern.Core.Search;

namespace Wyvern.Tests;

public class FuzzyMatcherTests
{
    [Theory]
    [InlineData("xyz", "Notepad")]
    [InlineData("dape", "Notepad")]
    [InlineData("notepadplus", "Notepad")]
    [InlineData("", "Notepad")]
    public void Returns_null_when_query_is_not_a_subsequence(string query, string candidate)
    {
        Assert.Null(FuzzyMatcher.Score(query, candidate));
    }

    [Fact]
    public void Returns_a_positive_score_for_a_subsequence()
    {
        int? score = FuzzyMatcher.Score("ntpd", "Notepad");

        Assert.NotNull(score);
        Assert.True(score > 0);
    }

    [Theory]
    [InlineData("d", "Sound Recorder")]
    [InlineData("a", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxa")]
    public void Score_stays_positive_for_a_short_query_buried_in_a_long_candidate(string query, string candidate)
    {
        Assert.True(FuzzyMatcher.Score(query, candidate) > 0);
    }

    [Fact]
    public void Query_case_does_not_change_the_score()
    {
        Assert.Equal(FuzzyMatcher.Score("note", "Notepad"), FuzzyMatcher.Score("NOTE", "Notepad"));
    }

    [Fact]
    public void Match_at_the_start_beats_match_inside_a_word()
    {
        Assert.True(FuzzyMatcher.Score("note", "Notepad") > FuzzyMatcher.Score("note", "OneNote"));
    }

    [Fact]
    public void Earlier_match_beats_later_match()
    {
        Assert.True(FuzzyMatcher.Score("pad", "Paddle") > FuzzyMatcher.Score("pad", "Notepad"));
    }

    [Fact]
    public void Word_initials_beat_letters_buried_in_one_word()
    {
        Assert.True(FuzzyMatcher.Score("vsc", "Visual Studio Code") > FuzzyMatcher.Score("vsc", "Vascular"));
    }

    [Fact]
    public void Unbroken_run_beats_scattered_letters()
    {
        Assert.True(FuzzyMatcher.Score("term", "Terminal") > FuzzyMatcher.Score("term", "Text Editor Mode"));
    }

    [Fact]
    public void Shorter_candidate_wins_when_the_match_is_otherwise_equal()
    {
        Assert.True(FuzzyMatcher.Score("word", "Word") > FuzzyMatcher.Score("word", "WordPad"));
    }

    [Fact]
    public void Camel_case_hump_counts_as_a_word_start()
    {
        Assert.True(FuzzyMatcher.Score("np", "NotePad") > FuzzyMatcher.Score("np", "Notepad"));
    }

    [Theory]
    [InlineData("note", "Notepad")]
    [InlineData("vsc", "Visual Studio Code")]
    [InlineData("q3", "Q3_Results-2026 (final).PDF")]
    [InlineData("xyz", "Notepad")]
    [InlineData("np", "NotePad")]
    public void ScoreLowered_gives_the_same_answer_as_Score(string query, string candidate)
    {
        string lowerQuery = query.ToLowerInvariant();
        string lowerCandidate = candidate.ToLowerInvariant();

        Assert.Equal(FuzzyMatcher.Score(query, candidate), FuzzyMatcher.ScoreLowered(lowerQuery, candidate, lowerCandidate));
    }

    [Fact]
    public void A_name_too_long_for_the_stack_scores_like_a_short_one()
    {
        // 300 characters is past the size kept on the stack, so this takes the rented-buffer path.
        string padding = new('x', 290);

        int? atStart = FuzzyMatcher.Score("report", "report" + padding);
        int? atEnd = FuzzyMatcher.Score("report", padding + "report");

        Assert.NotNull(atStart);
        Assert.NotNull(atEnd);
        Assert.True(atStart > atEnd);
        Assert.Null(FuzzyMatcher.Score("reportz", "report" + padding));
    }

    [Fact]
    public void Scoring_many_names_does_not_allocate_per_call()
    {
        string lowerQuery = "report";
        string candidate = "Annual Report 2026.docx";
        string lowerCandidate = candidate.ToLowerInvariant();
        FuzzyMatcher.ScoreLowered(lowerQuery, candidate, lowerCandidate);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            FuzzyMatcher.ScoreLowered(lowerQuery, candidate, lowerCandidate);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Four arrays per call, as before this change, would be more than 4,000,000 bytes here.
        // The limit is not zero because the runtime itself can allocate a little on this
        // thread, for example when it recompiles a method that has become hot.
        Assert.True(allocated < 100_000, $"10,000 calls allocated {allocated} bytes");
    }
}
