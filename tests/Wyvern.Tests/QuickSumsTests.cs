using Wyvern.Core.Search;

namespace Wyvern.Tests;

/// <summary>Percentages and dates, as the launcher's calculator answers them. Today is Wednesday, 7 October 2026.</summary>
public class QuickSumsTests
{
    private static readonly DateOnly Today = new(2026, 10, 7);

    [Theory]
    [InlineData("15% of 80", "12")]
    [InlineData("15 % of 80", "12")]
    [InlineData("15 percent of 80", "12")]
    [InlineData("15 per cent of 80", "12")]
    [InlineData("7.5% of 1,200", "90")]
    [InlineData("200% of 3", "6")]
    [InlineData("0% of 50", "0")]
    [InlineData("20% off 50", "40")]
    [InlineData("12.5% off 1,999", "1,749.125")]
    [InlineData("80 + 15%", "92")]
    [InlineData("80+15%", "92")]
    [InlineData("80 - 15%", "68")]
    [InlineData("1,000 + 8.25 %", "1,082.5")]
    [InlineData("100 - 100%", "0")]
    [InlineData("12 as % of 80", "15%")]
    [InlineData("12 as a % of 80", "15%")]
    [InlineData("12 as a percent of 80", "15%")]
    [InlineData("12 is what % of 80", "15%")]
    [InlineData("what % of 80 is 12", "15%")]
    [InlineData("1 as % of 3", "33.3333333333%")]
    [InlineData("  15%   OF   80  ", "12")]
    public void Percentages(string typed, string answer) => Assert.Equal(answer, QuickSums.Percent(typed));

    [Theory]
    [InlineData("")]
    [InlineData("15%")]
    [InlineData("15% of")]
    [InlineData("of 80")]
    [InlineData("15% of eighty")]
    [InlineData("15 of 80")]
    [InlineData("12 as % of 0")]
    [InlineData("what % of 0 is 12")]
    [InlineData("80 * 15%")]
    [InlineData("15% of 80 please")]
    [InlineData("percent")]
    public void What_is_not_a_sum_with_a_percentage_has_no_answer(string typed) => Assert.Null(QuickSums.Percent(typed));

    [Theory]
    [InlineData("days until 25 dec", "79 days")]
    [InlineData("days until christmas", "79 days")]
    [InlineData("days to 8 oct", "1 day")]
    [InlineData("days until today", "0 days")]
    [InlineData("days until 7 oct", "0 days")]
    [InlineData("days until 6 oct", "364 days")]
    [InlineData("days until 1 jan", "86 days")]
    [InlineData("days until new year", "86 days")]
    [InlineData("days until 2027-01-01", "86 days")]
    [InlineData("days until 25 December 2026", "79 days")]
    [InlineData("days until dec 25", "79 days")]
    [InlineData("days until December 25, 2026", "79 days")]
    [InlineData("days until 25th of december", "79 days")]
    [InlineData("days until 1 jan 2026", "-279 days")]
    [InlineData("weeks until 25 dec", "11 weeks 2 days")]
    [InlineData("weeks until 21 oct", "2 weeks")]
    [InlineData("weeks until 8 oct", "0 weeks 1 day")]
    public void How_long_until_a_day(string typed, string answer) => Assert.Equal(answer, QuickSums.Date(typed, Today));

    [Theory]
    [InlineData("days since 1 jan", "279 days")]
    [InlineData("days since yesterday", "1 day")]
    [InlineData("days since 25 dec", "286 days")]
    [InlineData("days since 2024-10-07", "730 days")]
    [InlineData("weeks since 1 jan", "39 weeks 6 days")]
    [InlineData("days between 1 mar and 12 jun", "103 days")]
    [InlineData("days between 12 jun and 1 mar", "103 days")]
    [InlineData("days between 2026-01-01 and 2027-01-01", "365 days")]
    [InlineData("weeks between today and 25 dec", "11 weeks 2 days")]
    public void How_long_since_a_day_and_between_two(string typed, string answer) => Assert.Equal(answer, QuickSums.Date(typed, Today));

    [Theory]
    [InlineData("30 days from now", "Friday, 6 November 2026")]
    [InlineData("30 days from today", "Friday, 6 November 2026")]
    [InlineData("in 30 days", "Friday, 6 November 2026")]
    [InlineData("1 day later", "Thursday, 8 October 2026")]
    [InlineData("2 weeks ago", "Wednesday, 23 September 2026")]
    [InlineData("3 months from now", "Thursday, 7 January 2027")]
    [InlineData("1 year ago", "Tuesday, 7 October 2025")]
    [InlineData("today + 30 days", "Friday, 6 November 2026")]
    [InlineData("today+1 week", "Wednesday, 14 October 2026")]
    [InlineData("tomorrow - 2 days", "Tuesday, 6 October 2026")]
    [InlineData("25 dec + 10 days", "Monday, 4 January 2027")]
    [InlineData("2026-12-25 - 2 weeks", "Friday, 11 December 2026")]
    [InlineData("what day is 25 dec", "Friday, 25 December 2026")]
    [InlineData("what day is 25 dec?", "Friday, 25 December 2026")]
    [InlineData("what day was 4 jul 1776", "Thursday, 4 July 1776")]
    [InlineData("what day is 1 jan", "Friday, 1 January 2027")]
    public void A_day_so_long_from_another_and_which_day_of_the_week_a_date_is(string typed, string answer) =>
        Assert.Equal(answer, QuickSums.Date(typed, Today));

    [Theory]
    [InlineData("")]
    [InlineData("days")]
    [InlineData("days until")]
    [InlineData("days until someday")]
    [InlineData("days until 32 dec")]
    [InlineData("days until 30 feb")]
    [InlineData("days until 25 smarch")]
    [InlineData("days until 2026-13-01")]
    [InlineData("hours until 25 dec")]
    [InlineData("30 from now")]
    [InlineData("thirty days from now")]
    [InlineData("in 30")]
    [InlineData("today + 30")]
    [InlineData("christmas")]
    [InlineData("25 dec")]
    [InlineData("what day is it")]
    [InlineData("days between 1 mar")]
    [InlineData("note to self")]
    public void What_is_not_a_question_about_dates_has_no_answer(string typed) => Assert.Null(QuickSums.Date(typed, Today));

    [Fact]
    public void A_day_that_leaves_the_calendar_has_no_answer()
    {
        Assert.Null(QuickSums.Date("100000 years from now", Today));
        Assert.Null(QuickSums.Date("99999 years from now", Today));
        Assert.NotNull(QuickSums.Date("7000 years from now", Today));
    }

    [Fact]
    public void The_29th_of_february_is_the_next_or_the_last_there_is()
    {
        Assert.Equal("Tuesday, 29 February 2028", QuickSums.Date("what day is 29 feb", Today));
        Assert.Equal("951 days", QuickSums.Date("days since 29 feb", Today));
    }

    [Fact]
    public void The_month_gets_its_last_day_when_the_day_asked_for_is_not_in_it()
    {
        Assert.Equal("Monday, 30 November 2026", QuickSums.Date("1 month from now", new DateOnly(2026, 10, 31)));
    }

    [Fact]
    public void A_text_that_is_too_long_is_not_looked_at()
    {
        Assert.Null(QuickSums.Percent("15% of " + new string('8', 200)));
        Assert.Null(QuickSums.Date("days until " + new string('x', 200), Today));
    }

    [Fact]
    public void The_calculator_lists_these_answers_as_its_one_row()
    {
        var copied = new List<string>();
        var calculator = new CalculatorProvider(copied.Add, () => Today);

        Assert.Equal("12", Assert.Single(calculator.Search("15% of 80")).Item.Title);
        var days = Assert.Single(calculator.Search("days until 25 dec"));
        Assert.Equal("79 days", days.Item.Title);
        Assert.Equal(ItemKind.Calculation, days.Item.Kind);
        days.Item.Run!();
        Assert.Equal(["79 days"], copied);

        // The remainder sign is still the remainder in a sum.
        Assert.Equal("2", Assert.Single(calculator.Search("12 % 5")).Item.Title);
    }
}
