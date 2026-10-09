using Beacon.Core.Search;

namespace Beacon.Tests;

/// <summary>An amount in one unit, asked for in another, as the launcher's calculator answers it.</summary>
public class UnitConverterTests
{
    private const string Degree = "\u00B0";
    private const string Squared = "\u00B2";

    [Theory]
    [InlineData("5 km in miles", "3.106856 mi")]
    [InlineData("5 km in mi", "3.106856 mi")]
    [InlineData("1 mile in km", "1.609344 km")]
    [InlineData("1 mi to ft", "5,280 ft")]
    [InlineData("12 inches in cm", "30.48 cm")]
    [InlineData("6 ft in m", "1.8288 m")]
    [InlineData("100 yards as meters", "91.44 m")]
    [InlineData("1 nautical mile in km", "1.852 km")]
    [InlineData("2500 mm into m", "2.5 m")]
    public void Lengths(string typed, string answer) => Assert.Equal(answer, UnitConverter.Convert(typed));

    [Theory]
    [InlineData("10 lb to kg", "4.535924 kg")]
    [InlineData("100 kg in lbs", "220.462262 lb")]
    [InlineData("16 oz in lb", "1 lb")]
    [InlineData("1 stone in kg", "6.350293 kg")]
    [InlineData("2 tonnes in kg", "2,000 kg")]
    [InlineData("1 ton in kg", "907.18474 kg")]
    [InlineData("500 g in kg", "0.5 kg")]
    public void Masses(string typed, string answer) => Assert.Equal(answer, UnitConverter.Convert(typed));

    [Theory]
    [InlineData("72 f in c", "22.222222 " + Degree + "C")]
    [InlineData("100 c to f", "212 " + Degree + "F")]
    [InlineData("0 c in f", "32 " + Degree + "F")]
    [InlineData("-40 f in c", "-40 " + Degree + "C")]
    [InlineData("0 c in kelvin", "273.15 K")]
    [InlineData("0 k in c", "-273.15 " + Degree + "C")]
    [InlineData("98.6 fahrenheit to celsius", "37 " + Degree + "C")]
    public void Temperatures(string typed, string answer) => Assert.Equal(answer, UnitConverter.Convert(typed));

    [Theory]
    [InlineData("1 gallon in litres", "3.785412 l")]
    [InlineData("2 cups in ml", "473.176473 ml")]
    [InlineData("1 tbsp in tsp", "3 tsp")]
    [InlineData("8 fl oz in cups", "1 cup")]
    [InlineData("1 pint in fluid ounces", "16 fl oz")]
    [InlineData("750 ml in l", "0.75 l")]
    public void Volumes(string typed, string answer) => Assert.Equal(answer, UnitConverter.Convert(typed));

    [Theory]
    [InlineData("1 acre in square feet", "43,560 ft" + Squared)]
    [InlineData("100 sq ft in m2", "9.290304 m" + Squared)]
    [InlineData("1 hectare in acres", "2.471054 acres")]
    [InlineData("1 km2 in ha", "100 ha")]
    public void Areas(string typed, string answer) => Assert.Equal(answer, UnitConverter.Convert(typed));

    [Theory]
    [InlineData("100 mph in km/h", "160.9344 km/h")]
    [InlineData("100 kph to mph", "62.137119 mph")]
    [InlineData("10 m/s in km/h", "36 km/h")]
    [InlineData("20 knots in mph", "23.015589 mph")]
    public void Speeds(string typed, string answer) => Assert.Equal(answer, UnitConverter.Convert(typed));

    [Theory]
    [InlineData("1.5 hours in minutes", "90 min")]
    [InlineData("90 min in h", "1.5 h")]
    [InlineData("2 weeks in days", "14 days")]
    [InlineData("1 day in seconds", "86,400 s")]
    [InlineData("1 year in days", "365 days")]
    [InlineData("250 ms in s", "0.25 s")]
    public void Times(string typed, string answer) => Assert.Equal(answer, UnitConverter.Convert(typed));

    [Theory]
    [InlineData("1 gb in mb", "1,000 MB")]
    [InlineData("1 GiB in MiB", "1,024 MiB")]
    [InlineData("1 tb in gib", "931.322575 GiB")]
    [InlineData("1 byte in bits", "8 bits")]
    [InlineData("2048 kib in mib", "2 MiB")]
    public void Data(string typed, string answer) => Assert.Equal(answer, UnitConverter.Convert(typed));

    [Theory]
    [InlineData("5km in mi", "3.106856 mi")]
    [InlineData("  5   KM   IN   Miles  ", "3.106856 mi")]
    [InlineData("5 km = miles", "3.106856 mi")]
    [InlineData("5 km -> miles", "3.106856 mi")]
    [InlineData("1,500.5 m in km", "1.5005 km")]
    [InlineData("+3 ft in in", "36 in")]
    [InlineData("5 in in cm", "12.7 cm")]
    [InlineData("5' in cm", "152.4 cm")]
    [InlineData(".5 kg in g", "500 g")]
    public void However_it_is_written(string typed, string answer) => Assert.Equal(answer, UnitConverter.Convert(typed));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("5 km")]
    [InlineData("km in miles")]
    [InlineData("5 km in")]
    [InlineData("in miles")]
    [InlineData("5 km in kg")]
    [InlineData("5 km in parsecs")]
    [InlineData("5 furlongs in km")]
    [InlineData("five km in miles")]
    [InlineData("5 km in miles please")]
    [InlineData("note to self")]
    [InlineData("10 to 5")]
    [InlineData("1.2.3 km in m")]
    [InlineData("5 usd in eur")]
    [InlineData("in to as")]
    public void What_is_not_an_amount_in_one_unit_asked_for_in_another_of_its_kind_has_no_answer(string typed) =>
        Assert.Null(UnitConverter.Convert(typed));

    [Fact]
    public void A_text_that_is_too_long_is_not_looked_at()
    {
        Assert.Null(UnitConverter.Convert("5 km in " + new string(' ', 200) + "miles"));
    }

    [Theory]
    [InlineData("1 inch in km", "0.0000254 km")]
    [InlineData("1 mm in mi", "6.213712E-07 mi")]
    [InlineData("1000000 km in mm", "1,000,000,000,000 mm")]
    [InlineData("0 km in mi", "0 mi")]
    public void Very_small_and_very_large_answers_are_still_read_easily(string typed, string answer) =>
        Assert.Equal(answer, UnitConverter.Convert(typed));

    [Fact]
    public void The_calculator_lists_the_answer_as_its_one_row_and_enter_copies_it()
    {
        var copied = new List<string>();
        var calculator = new CalculatorProvider(copied.Add);

        var row = Assert.Single(calculator.Search(" 5 km in miles "));

        Assert.Equal(CalculatorProvider.ItemId, row.Item.Id);
        Assert.Equal("3.106856 mi", row.Item.Title);
        Assert.Equal("5 km in miles", row.Item.Subtitle);
        Assert.Equal(ItemKind.Calculation, row.Item.Kind);
        Assert.Equal(CalculatorProvider.Score, row.Score);
        row.Item.Run!();
        Assert.Equal(["3.106856 mi"], copied);

        // A sum is still a sum, and what is neither lists nothing.
        Assert.Equal("84", Assert.Single(calculator.Search("12 * (3 + 4)")).Item.Title);
        Assert.Empty(calculator.Search("kilometres"));
    }
}
