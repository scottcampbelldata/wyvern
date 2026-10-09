using Beacon.Core.Search;

namespace Beacon.Tests;

/// <summary>The sums the launcher works out, and what it leaves alone.</summary>
public class CalculatorProviderTests
{
    [Theory]
    [InlineData("2+2", "4")]
    [InlineData("12 * (3 + 4)", "84")]
    [InlineData("10 / 4", "2.5")]
    [InlineData("2 ^ 10", "1,024")]
    [InlineData("2 ^ 3 ^ 2", "512")]
    [InlineData("7 % 4", "3")]
    [InlineData("-3 + 5", "2")]
    [InlineData("3 - -2", "5")]
    [InlineData("0.1 + 0.2", "0.3")]
    [InlineData("1,000 * 3", "3,000")]
    [InlineData("3 x 4", "12")]
    [InlineData("  (1 + 2) * (3 + 4)  ", "21")]
    [InlineData("1 / 3", "0.333333333333")]
    [InlineData("5 - 5", "0")]
    [InlineData(".5 + .5", "1")]
    public void A_sum_is_worked_out(string sum, string answer)
    {
        Assert.Equal(answer, CalculatorProvider.Answer(sum));
    }

    [Theory]
    [InlineData("")]
    [InlineData("42")]
    [InlineData("-42")]
    [InlineData("excel")]
    [InlineData("notepad")]
    [InlineData("2 +")]
    [InlineData("+ 2")]
    [InlineData("(2 + 3")]
    [InlineData("2 + 3)")]
    [InlineData("1 / 0")]
    [InlineData("5 % 0")]
    [InlineData("2 + two")]
    [InlineData("1.2.3 + 1")]
    [InlineData("a-b")]
    [InlineData("10 ^ 10000")]
    public void What_is_not_a_whole_sum_gives_no_answer(string text)
    {
        Assert.Null(CalculatorProvider.Answer(text));
    }

    [Fact]
    public void Brackets_deeper_than_the_limit_and_a_text_longer_than_the_limit_give_no_answer()
    {
        Assert.Null(CalculatorProvider.Answer(new string('(', 40) + "1+1" + new string(')', 40)));
        Assert.Null(CalculatorProvider.Answer(string.Join("+", Enumerable.Repeat("1", 150))));
        Assert.Equal("5", CalculatorProvider.Answer("((((1+1))))+3"));
    }

    [Fact]
    public void The_answer_is_one_row_that_ranks_first_and_copies_the_answer()
    {
        var copied = new List<string>();
        var provider = new CalculatorProvider(copied.Add);

        var results = provider.Search(" 6 * 7 ");

        var result = Assert.Single(results);
        Assert.Equal(CalculatorProvider.Score, result.Score);
        Assert.Equal(CalculatorProvider.ItemId, result.Item.Id);
        Assert.Equal("42", result.Item.Title);
        Assert.Equal("6 * 7", result.Item.Subtitle);
        Assert.Equal(ItemKind.Calculation, result.Item.Kind);

        result.Item.Run!();

        Assert.Equal(["42"], copied);
    }

    [Fact]
    public void A_word_lists_nothing()
    {
        Assert.Empty(new CalculatorProvider(_ => { }).Search("chrome"));
    }
}
