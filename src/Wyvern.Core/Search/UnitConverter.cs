using System.Globalization;

namespace Wyvern.Core.Search;

/// <summary>
/// Turns an amount in one unit into another: <c>5 km in miles</c>, <c>72 f to c</c>,
/// <c>1.5 hours in minutes</c>. The launcher's calculator lists the answer as it lists the
/// answer to a sum. Everything is worked out from fixed numbers: nothing is looked up, so
/// there is no money here, whose worth changes by the day.
/// </summary>
/// <remarks>
/// What is typed is an amount, a unit, one of the words <c>in</c>, <c>to</c>, <c>as</c> or
/// <c>into</c> (or <c>=</c> or <c>-></c>), and a second unit of the same kind: length, mass,
/// temperature, volume, area, speed, time or data. Units are known by their short and their
/// long names, in the singular and the plural, whatever the case of the letters. Pints,
/// quarts, gallons, cups and spoons are the American ones, and a ton is the American short
/// ton; a tonne is a thousand kilograms. kB, MB, GB and TB go by thousands, KiB, MiB, GiB and
/// TiB by 1,024.
/// </remarks>
public static class UnitConverter
{
    private const int MaxLength = 120;

    private static readonly string[] Separators = ["in", "to", "as", "into", "=", "->"];

    private static readonly Dictionary<string, Unit> Units = Build();

    /// <summary>The answer as it is shown, such as "3.106856 mi", or null when the text is not an amount in one unit asked for in another of the same kind.</summary>
    public static string? Convert(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Length > MaxLength)
        {
            return null;
        }

        // The last of the separating words that has something after it divides the text:
        // "5 in in cm" is five inches in centimetres.
        string[] words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int at = words.Length - 2; at >= 1; at--)
        {
            if (!Separators.Contains(words[at], StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Find(string.Join(' ', words, at + 1, words.Length - at - 1)) is not { } to
                || !Amount(string.Join(' ', words, 0, at), out double amount, out string unit)
                || Find(unit) is not { } from
                || from.Kind != to.Kind)
            {
                continue;
            }

            // Into the kind's own unit and out of it again. Temperatures do not start at the
            // same nought, so they have a shift as well as a factor.
            double value = (((amount + from.Shift) * from.Factor) / to.Factor) - to.Shift;
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return null;
            }

            return Shown(value) + " " + to.Symbol;
        }

        return null;
    }

    // An amount and what follows it: "5 km", "5km", "-40 f", "1,500.5 square feet". The
    // amount is written with a point; commas between digits are grouping.
    private static bool Amount(string text, out double amount, out string unit)
    {
        int end = 0;
        if (end < text.Length && text[end] is '-' or '+')
        {
            end++;
        }

        int digits = 0;
        bool point = false;
        while (end < text.Length)
        {
            char c = text[end];
            if (char.IsAsciiDigit(c))
            {
                digits++;
            }
            else if (c == '.' && !point)
            {
                point = true;
            }
            else if (c != ',' || digits == 0 || point)
            {
                break;
            }

            end++;
        }

        unit = text[end..].Trim();
        amount = 0;
        return digits > 0
            && unit.Length > 0
            && double.TryParse(text[..end].Replace(",", "", StringComparison.Ordinal), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount);
    }

    private static Unit? Find(string name)
    {
        string plain = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        return Units.TryGetValue(plain, out var unit) ? unit : null;
    }

    // Up to seven figures after the point for what is small, fewer for what is large, and no
    // dust from the arithmetic: 0.1 + 0.2 kinds of answers come out as they should read.
    private static string Shown(double value)
    {
        double size = Math.Abs(value);
        if (size != 0 && (size < 1e-6 || size >= 1e15))
        {
            return value.ToString("G7", CultureInfo.InvariantCulture);
        }

        int places = size >= 1000 ? 2 : size >= 1 ? 6 : 7;
        double rounded = Math.Round(value, places);
        return (rounded == 0 ? 0 : rounded).ToString("#,0.#######", CultureInfo.InvariantCulture);
    }

    private sealed record Unit(string Kind, string Symbol, double Factor, double Shift = 0);

    private static Dictionary<string, Unit> Build()
    {
        var units = new Dictionary<string, Unit>(StringComparer.Ordinal);

        void Add(string kind, string symbol, double factor, params string[] names) => AddShifted(kind, symbol, factor, 0, names);

        void AddShifted(string kind, string symbol, double factor, double shift, params string[] names)
        {
            var unit = new Unit(kind, symbol, factor, shift);
            units[symbol.ToLowerInvariant()] = unit;
            foreach (string name in names)
            {
                units[name] = unit;
            }
        }

        // Length, in metres.
        Add("length", "mm", 0.001, "millimeter", "millimeters", "millimetre", "millimetres");
        Add("length", "cm", 0.01, "centimeter", "centimeters", "centimetre", "centimetres");
        Add("length", "m", 1, "meter", "meters", "metre", "metres");
        Add("length", "km", 1000, "kilometer", "kilometers", "kilometre", "kilometres");
        Add("length", "in", 0.0254, "inch", "inches", "\"");
        Add("length", "ft", 0.3048, "foot", "feet", "'");
        Add("length", "yd", 0.9144, "yard", "yards");
        Add("length", "mi", 1609.344, "mile", "miles");
        Add("length", "nmi", 1852, "nautical mile", "nautical miles");

        // Mass, in kilograms.
        Add("mass", "mg", 0.000001, "milligram", "milligrams");
        Add("mass", "g", 0.001, "gram", "grams");
        Add("mass", "kg", 1, "kilogram", "kilograms", "kilo", "kilos");
        Add("mass", "oz", 0.028349523125, "ounce", "ounces");
        Add("mass", "lb", 0.45359237, "lbs", "pound", "pounds");
        Add("mass", "st", 6.35029318, "stone", "stones");
        Add("mass", "t", 1000, "tonne", "tonnes", "metric ton", "metric tons");
        Add("mass", "ton", 907.18474, "tons", "short ton", "short tons");

        // Temperature, in kelvins: what is added before the factor is the shift.
        AddShifted("temperature", "\u00B0C", 1, 273.15, "c", "celsius", "centigrade", "degrees celsius", "degrees c");
        AddShifted("temperature", "\u00B0F", 5.0 / 9, 459.67, "f", "fahrenheit", "degrees fahrenheit", "degrees f");
        Add("temperature", "K", 1, "kelvin", "kelvins");

        // Volume, in litres. The American measures.
        Add("volume", "ml", 0.001, "milliliter", "milliliters", "millilitre", "millilitres");
        Add("volume", "l", 1, "liter", "liters", "litre", "litres");
        Add("volume", "tsp", 0.00492892159375, "teaspoon", "teaspoons");
        Add("volume", "tbsp", 0.01478676478125, "tablespoon", "tablespoons");
        Add("volume", "fl oz", 0.0295735295625, "floz", "fluid ounce", "fluid ounces");
        Add("volume", "cup", 0.2365882365, "cups");
        Add("volume", "pt", 0.473176473, "pint", "pints");
        Add("volume", "qt", 0.946352946, "quart", "quarts");
        Add("volume", "gal", 3.785411784, "gallon", "gallons");

        // Area, in square metres.
        Add("area", "m\u00B2", 1, "m2", "sqm", "sq m", "square meter", "square meters", "square metre", "square metres");
        Add("area", "km\u00B2", 1_000_000, "km2", "sq km", "square kilometer", "square kilometers", "square kilometre", "square kilometres");
        Add("area", "ft\u00B2", 0.09290304, "ft2", "sqft", "sq ft", "square foot", "square feet");
        Add("area", "in\u00B2", 0.00064516, "in2", "sq in", "square inch", "square inches");
        Add("area", "yd\u00B2", 0.83612736, "yd2", "sq yd", "square yard", "square yards");
        Add("area", "mi\u00B2", 2_589_988.110336, "mi2", "sq mi", "square mile", "square miles");
        Add("area", "acres", 4046.8564224, "acre");
        Add("area", "ha", 10_000, "hectare", "hectares");

        // Speed, in metres a second.
        Add("speed", "m/s", 1, "mps", "meters per second", "metres per second");
        Add("speed", "km/h", 1 / 3.6, "kph", "kmh", "kilometers per hour", "kilometres per hour");
        Add("speed", "mph", 0.44704, "mi/h", "miles per hour");
        Add("speed", "kn", 1852.0 / 3600, "knot", "knots");
        Add("speed", "ft/s", 0.3048, "fps", "feet per second");

        // Time, in seconds. A year is 365 days.
        Add("time", "ms", 0.001, "millisecond", "milliseconds");
        Add("time", "s", 1, "sec", "secs", "second", "seconds");
        Add("time", "min", 60, "mins", "minute", "minutes");
        Add("time", "h", 3600, "hr", "hrs", "hour", "hours");
        Add("time", "days", 86_400, "d", "day");
        Add("time", "weeks", 604_800, "wk", "wks", "week");
        Add("time", "years", 31_536_000, "yr", "yrs", "year");

        // Data, in bytes.
        Add("data", "bits", 0.125, "bit");
        Add("data", "bytes", 1, "b", "byte");
        Add("data", "kB", 1e3, "kilobyte", "kilobytes");
        Add("data", "MB", 1e6, "megabyte", "megabytes");
        Add("data", "GB", 1e9, "gigabyte", "gigabytes");
        Add("data", "TB", 1e12, "terabyte", "terabytes");
        Add("data", "KiB", 1024, "kibibyte", "kibibytes");
        Add("data", "MiB", 1024d * 1024, "mebibyte", "mebibytes");
        Add("data", "GiB", 1024d * 1024 * 1024, "gibibyte", "gibibytes");
        Add("data", "TiB", 1024d * 1024 * 1024 * 1024, "tebibyte", "tebibytes");
        return units;
    }
}
