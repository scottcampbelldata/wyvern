using System.Globalization;
using System.Text.RegularExpressions;

namespace Beacon.Core.Search;

/// <summary>
/// Two kinds of everyday sums the launcher's calculator answers besides arithmetic and units:
/// percentages (<c>15% of 80</c>, <c>80 + 15%</c>, <c>20% off 50</c>, <c>12 as % of 80</c>) and
/// dates (<c>days until 25 dec</c>, <c>30 days from now</c>, <c>days between 1 mar and 12 jun</c>,
/// <c>what day is 2026-12-25</c>). Everything is worked out here: nothing is looked up.
/// </summary>
public static partial class QuickSums
{
    private const int MaxLength = 120;
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // A number: digits with commas for grouping and a point, and a minus in front if there is one.
    private const string Number = @"(-?(?:\d[\d,]*(?:\.\d+)?|\.\d+))";
    private const string PercentSign = @"\s*(?:%|percent|per\s+cent)";
    private const string Units = @"(day|days|week|weeks|month|months|year|years)";

    private static readonly string[] Months =
        ["january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december"];

    /// <summary>The answer to a sum with a percentage in it, as it is shown, or null when the text is not one.</summary>
    public static string? Percent(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Length > MaxLength)
        {
            return null;
        }

        try
        {
            // 15% of 80
            if (PercentOf().Match(text) is { Success: true } of && Read(of, 1, out double part) && Read(of, 2, out double whole))
            {
                return Shown(part / 100 * whole);
            }

            // 20% off 50
            if (PercentOff().Match(text) is { Success: true } off && Read(off, 1, out double less) && Read(off, 2, out double price))
            {
                return Shown(price - (less / 100 * price));
            }

            // 80 + 15%, 80 - 15%
            if (PlusPercent().Match(text) is { Success: true } plus && Read(plus, 1, out double start) && Read(plus, 3, out double change))
            {
                return Shown(start * (1 + ((plus.Groups[2].Value == "-" ? -change : change) / 100)));
            }

            // 12 as % of 80, 12 is what % of 80
            if (AsPercentOf().Match(text) is { Success: true } share && Read(share, 1, out double some) && Read(share, 2, out double all) && all != 0)
            {
                return Shown(some / all * 100) + "%";
            }

            // what % of 80 is 12
            if (WhatPercentOf().Match(text) is { Success: true } asked && Read(asked, 1, out double total) && Read(asked, 2, out double piece) && total != 0)
            {
                return Shown(piece / total * 100) + "%";
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Not answered.
        }

        return null;
    }

    /// <summary>The answer to a question about dates, as it is shown, or null when the text is not one.</summary>
    /// <param name="today">The day it is, in the owner's own time zone.</param>
    public static string? Date(string text, DateOnly today)
    {
        text = text.Trim().TrimEnd('?').Trim();
        if (text.Length == 0 || text.Length > MaxLength)
        {
            return null;
        }

        try
        {
            // days until 25 dec: a date without a year is the next one.
            if (Until().Match(text) is { Success: true } until && Day(until.Groups[2].Value, today, +1) is { } ahead)
            {
                return Count(ahead.DayNumber - today.DayNumber, until.Groups[1].Value);
            }

            // days since 1 jan: a date without a year is the last one.
            if (Since().Match(text) is { Success: true } since && Day(since.Groups[2].Value, today, -1) is { } behind)
            {
                return Count(today.DayNumber - behind.DayNumber, since.Groups[1].Value);
            }

            // days between 1 mar and 12 jun
            if (Between().Match(text) is { Success: true } between
                && Day(between.Groups[2].Value, today, 0) is { } first
                && Day(between.Groups[3].Value, today, 0) is { } second)
            {
                return Count(Math.Abs(second.DayNumber - first.DayNumber), between.Groups[1].Value);
            }

            // 30 days from now, in 30 days, 2 weeks ago
            if (FromNow().Match(text) is { Success: true } later && Whole(later.Groups[1].Value) is { } forward)
            {
                return Named(Add(today, forward, later.Groups[2].Value));
            }

            if (InSoMany().Match(text) is { Success: true } inSoMany && Whole(inSoMany.Groups[1].Value) is { } soon)
            {
                return Named(Add(today, soon, inSoMany.Groups[2].Value));
            }

            if (Ago().Match(text) is { Success: true } ago && Whole(ago.Groups[1].Value) is { } back)
            {
                return Named(Add(today, -back, ago.Groups[2].Value));
            }

            // today + 30 days, 25 dec - 2 weeks
            if (DatePlus().Match(text) is { Success: true } sum
                && Day(sum.Groups[1].Value, today, 0) is { } from
                && Whole(sum.Groups[3].Value) is { } amount)
            {
                return Named(Add(from, sum.Groups[2].Value == "-" ? -amount : amount, sum.Groups[4].Value));
            }

            // what day is 25 dec 2026
            if (WhatDay().Match(text) is { Success: true } which && Day(which.Groups[1].Value, today, +1) is { } asked)
            {
                return Named(asked);
            }
        }
        catch (Exception ex) when (ex is RegexMatchTimeoutException or ArgumentOutOfRangeException)
        {
            // Not answered: the sum took too long to read, or the date is off the calendar.
        }

        return null;
    }

    private static bool Read(Match match, int group, out double number) =>
        double.TryParse(
            match.Groups[group].Value.Replace(",", "", StringComparison.Ordinal),
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out number);

    private static int? Whole(string digits) =>
        int.TryParse(digits.Replace(",", "", StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number <= 100_000 ? number : null;

    // As the calculator shows a number: no dust from the arithmetic, and commas for thousands.
    private static string Shown(double value)
    {
        double rounded = Math.Round(value, 10);
        return (rounded == 0 ? 0 : rounded).ToString("#,0.##########", CultureInfo.InvariantCulture);
    }

    private static string Count(int days, string unit)
    {
        if (unit.StartsWith("week", StringComparison.OrdinalIgnoreCase))
        {
            int weeks = Math.Abs(days) / 7;
            int rest = Math.Abs(days) % 7;
            string words = Plural(weeks, "week") + (rest > 0 ? " " + Plural(rest, "day") : "");
            return days < 0 ? "-" + words : words;
        }

        return Plural(days, "day");
    }

    private static string Plural(int count, string word) =>
        count.ToString("#,0", CultureInfo.InvariantCulture) + " " + word + (Math.Abs(count) == 1 ? "" : "s");

    private static DateOnly Add(DateOnly day, int amount, string unit)
    {
        string kind = unit.ToLowerInvariant();
        return kind.StartsWith("day", StringComparison.Ordinal) ? day.AddDays(amount)
            : kind.StartsWith("week", StringComparison.Ordinal) ? day.AddDays(amount * 7)
            : kind.StartsWith("month", StringComparison.Ordinal) ? day.AddMonths(amount)
            : day.AddYears(amount);
    }

    // A date as an answer: Friday, 25 December 2026.
    private static string Named(DateOnly day) => day.ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>
    /// A date as it is typed: today, tomorrow, yesterday, christmas, new year, 2026-12-25,
    /// 25 dec, 25th december 2026, dec 25, december 25, 2026. Null for anything else.
    /// </summary>
    /// <param name="lean">For a date without a year: +1 for the next such day (today counts), -1 for the last one, 0 for this year's.</param>
    private static DateOnly? Day(string text, DateOnly today, int lean)
    {
        string plain = text.Trim().ToLowerInvariant();
        switch (plain)
        {
            case "today" or "now":
                return today;
            case "tomorrow":
                return today.AddDays(1);
            case "yesterday":
                return today.AddDays(-1);
            case "christmas" or "christmas day" or "xmas":
                return Leaning(12, 25, today, lean);
            case "new year" or "new years" or "new year's" or "new year's day" or "new years day":
                return Leaning(1, 1, today, lean);
        }

        if (Iso().Match(plain) is { Success: true } iso)
        {
            return Valid(int.Parse(iso.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(iso.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(iso.Groups[3].Value, CultureInfo.InvariantCulture));
        }

        var named = DayMonth().Match(plain);
        string dayText = named.Groups[1].Value;
        string monthText = named.Groups[2].Value;
        string yearText = named.Groups[3].Value;
        if (!named.Success)
        {
            named = MonthDay().Match(plain);
            if (!named.Success)
            {
                return null;
            }

            monthText = named.Groups[1].Value;
            dayText = named.Groups[2].Value;
            yearText = named.Groups[3].Value;
        }

        int month = Array.FindIndex(Months, name => name.StartsWith(monthText, StringComparison.Ordinal) && monthText.Length >= 3) + 1;
        if (month == 0 || !int.TryParse(dayText, NumberStyles.None, CultureInfo.InvariantCulture, out int dayOfMonth))
        {
            return null;
        }

        return yearText.Length > 0
            ? Valid(int.Parse(yearText, CultureInfo.InvariantCulture), month, dayOfMonth)
            : Leaning(month, dayOfMonth, today, lean);
    }

    private static DateOnly? Leaning(int month, int day, DateOnly today, int lean)
    {
        if (Valid(today.Year, month, day) is not { } thisYear)
        {
            // The 29th of February in a year that has none: the nearest year that has one, the way the question leans.
            for (int year = today.Year + (lean < 0 ? -1 : 1); Math.Abs(year - today.Year) <= 8; year += lean < 0 ? -1 : 1)
            {
                if (Valid(year, month, day) is { } found)
                {
                    return found;
                }
            }

            return null;
        }

        return lean > 0 && thisYear < today ? Valid(today.Year + 1, month, day)
            : lean < 0 && thisYear > today ? Valid(today.Year - 1, month, day)
            : thisYear;
    }

    private static DateOnly? Valid(int year, int month, int day) =>
        year is >= 1 and <= 9999 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month)
            ? new DateOnly(year, month, day)
            : null;

    [GeneratedRegex("^" + Number + PercentSign + @"\s+of\s+" + Number + "$", Options, 100)]
    private static partial Regex PercentOf();

    [GeneratedRegex("^" + Number + PercentSign + @"\s+off\s+" + Number + "$", Options, 100)]
    private static partial Regex PercentOff();

    [GeneratedRegex("^" + Number + @"\s*([+-])\s*" + Number + PercentSign + "$", Options, 100)]
    private static partial Regex PlusPercent();

    [GeneratedRegex("^" + Number + @"\s+(?:as|is)\s+(?:a\s+|what\s+)?(?:%|percent|per\s+cent)\s+of\s+" + Number + "$", Options, 100)]
    private static partial Regex AsPercentOf();

    [GeneratedRegex(@"^what\s+(?:%|percent|per\s+cent)\s+of\s+" + Number + @"\s+is\s+" + Number + "$", Options, 100)]
    private static partial Regex WhatPercentOf();

    [GeneratedRegex(@"^(days|weeks)\s+(?:until|till|til|to|before)\s+(.+)$", Options, 100)]
    private static partial Regex Until();

    [GeneratedRegex(@"^(days|weeks)\s+(?:since|from|after)\s+(.+)$", Options, 100)]
    private static partial Regex Since();

    [GeneratedRegex(@"^(days|weeks)\s+between\s+(.+?)\s+and\s+(.+)$", Options, 100)]
    private static partial Regex Between();

    [GeneratedRegex(@"^(\d[\d,]*)\s+" + Units + @"\s+(?:from\s+(?:now|today)|after\s+today|later)$", Options, 100)]
    private static partial Regex FromNow();

    [GeneratedRegex(@"^in\s+(\d[\d,]*)\s+" + Units + "$", Options, 100)]
    private static partial Regex InSoMany();

    [GeneratedRegex(@"^(\d[\d,]*)\s+" + Units + @"\s+(?:ago|before\s+today)$", Options, 100)]
    private static partial Regex Ago();

    [GeneratedRegex(@"^(.+?)\s*([+-])\s*(\d[\d,]*)\s+" + Units + "$", Options, 100)]
    private static partial Regex DatePlus();

    [GeneratedRegex(@"^(?:what\s+day\s+(?:is|was|of\s+the\s+week\s+is)|day\s+of\s+the\s+week\s+(?:for|of|on))\s+(.+)$", Options, 100)]
    private static partial Regex WhatDay();

    [GeneratedRegex(@"^(\d{4})-(\d{1,2})-(\d{1,2})$", Options, 100)]
    private static partial Regex Iso();

    [GeneratedRegex(@"^(\d{1,2})(?:st|nd|rd|th)?(?:\s+of)?\s+([a-z]{3,9})\.?,?(?:\s+(\d{4}))?$", Options, 100)]
    private static partial Regex DayMonth();

    [GeneratedRegex(@"^([a-z]{3,9})\.?\s+(\d{1,2})(?:st|nd|rd|th)?,?(?:\s+(\d{4}))?$", Options, 100)]
    private static partial Regex MonthDay();
}
