using Wyvern.Core.Search;

namespace Wyvern.Tests;

public class KeywordMatcherTests
{
    [Fact]
    public void Without_a_keyword_the_name_is_matched_like_any_title()
    {
        Assert.Equal(FuzzyMatcher.Score("git", "Search GitHub"), KeywordMatcher.Score("git", "Search GitHub", null));
        Assert.Null(KeywordMatcher.Score("xyz", "Search GitHub", null));
    }

    [Theory]
    [InlineData("gh")]
    [InlineData("GH")]
    [InlineData("Gh")]
    public void A_query_that_is_the_keyword_scores_above_any_name_match(string query)
    {
        Assert.Equal(KeywordMatcher.ExactKeywordScore, KeywordMatcher.Score(query, "Search GitHub", "gh"));
    }

    [Fact]
    public void The_keyword_score_beats_the_best_possible_name_match_even_after_the_frecency_boost()
    {
        // A name that is the query itself is the best a name can do. The engine can at most double it.
        string query = "AnExtremelyLongQueryThatIsAlsoTheWholeNameOfSomething";
        int bestByName = FuzzyMatcher.Score(query, query)!.Value;

        Assert.True(KeywordMatcher.ExactKeywordScore > bestByName * 2);
    }

    [Fact]
    public void A_keyword_matches_even_when_the_name_does_not()
    {
        Assert.Equal(KeywordMatcher.ExactKeywordScore, KeywordMatcher.Score(";sig", "Email signature", ";sig"));
        Assert.Null(KeywordMatcher.Score(";sig", "Email signature", null));
    }

    [Fact]
    public void The_start_of_a_keyword_counts_as_strongly_as_the_start_of_a_name()
    {
        int? score = KeywordMatcher.Score(";s", "Email signature", ";sig");

        Assert.Equal(FuzzyMatcher.Score(";s", ";sig"), score);
        Assert.NotNull(score);
        Assert.True(score < KeywordMatcher.ExactKeywordScore);
    }

    [Fact]
    public void Only_the_start_of_a_keyword_counts_not_letters_from_inside_it()
    {
        Assert.Null(KeywordMatcher.Score("sig", "Regards", ";sig"));
        Assert.Null(KeywordMatcher.Score(";g", "Regards", ";sig"));
    }

    [Fact]
    public void A_query_longer_than_the_keyword_is_matched_by_name_only()
    {
        Assert.Null(KeywordMatcher.Score("ghx", "Search", "gh"));
        Assert.Equal(FuzzyMatcher.Score("ghu", "GitHub"), KeywordMatcher.Score("ghu", "GitHub", "gh"));
    }

    [Fact]
    public void When_the_name_and_the_start_of_the_keyword_both_match_the_better_score_is_used()
    {
        // The name starts with the query and is shorter than the keyword, so it scores higher.
        int byName = FuzzyMatcher.Score("ma", "Mail")!.Value;
        int byKeyword = FuzzyMatcher.Score("ma", "mailbox")!.Value;
        Assert.True(byName > byKeyword);
        Assert.Equal(byName, KeywordMatcher.Score("ma", "Mail", "mailbox"));

        // Here the query is buried in the name and starts the keyword, so the keyword wins.
        int buried = FuzzyMatcher.Score("ma", "Team calendar")!.Value;
        int starts = FuzzyMatcher.Score("ma", "mail")!.Value;
        Assert.True(starts > buried);
        Assert.Equal(starts, KeywordMatcher.Score("ma", "Team calendar", "mail"));
    }

    [Fact]
    public void An_empty_query_matches_nothing()
    {
        Assert.Null(KeywordMatcher.Score("", "Mail", "m"));
    }
}
