using System.Globalization;
using System.Text.Json;
using Beacon.Core.Settings;

namespace Beacon.Core.Sync;

/// <summary>
/// The one setting sync has in settings.json: how often it runs. Where it connects to is in
/// sync.json (<see cref="SyncConfig"/>), which the app writes itself.
/// </summary>
public sealed class SyncOptions
{
    /// <summary>Every five minutes.</summary>
    public const int DefaultIntervalMinutes = 5;

    /// <summary>Once a day.</summary>
    public const int MaxIntervalMinutes = 1440;

    // The most characters the number may be written in, for the reason ClipboardOptions gives
    // for "clipboardRetentionDays": a decimal rounds what it cannot keep.
    private const int MaxNumberLength = 28;

    private SyncOptions(TimeSpan interval, IReadOnlyList<string> notes)
    {
        Interval = interval;
        Notes = notes;
    }

    /// <summary>What applies when settings.json says nothing about sync.</summary>
    public static SyncOptions Default { get; } = new(TimeSpan.FromMinutes(DefaultIntervalMinutes), []);

    /// <summary>How long after one sync run the next one starts.</summary>
    public TimeSpan Interval { get; }

    /// <summary>Sentences to show the owner about a setting that could not be used. Fixed words.</summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>
    /// A missing or null "syncIntervalMinutes" means 5. It is a JSON number whose value is whole
    /// and from 1 to 1440, in whatever form it is written ("10.0" is 10); anything else gives a
    /// note and 5. This method never throws, whatever the JSON holds.
    /// </summary>
    public static SyncOptions From(BeaconSettings settings)
    {
        if (settings.SyncIntervalMinutes is not { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } written)
        {
            return Default;
        }

        if (written.ValueKind == JsonValueKind.Number
            && written.GetRawText().Length <= MaxNumberLength
            && decimal.TryParse(written.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal minutes)
            && minutes == decimal.Truncate(minutes)
            && minutes >= 1
            && minutes <= MaxIntervalMinutes)
        {
            return new SyncOptions(TimeSpan.FromMinutes((int)minutes), []);
        }

        return new SyncOptions(
            TimeSpan.FromMinutes(DefaultIntervalMinutes),
            [$"\"syncIntervalMinutes\" must be a whole number from 1 to {MaxIntervalMinutes}. {DefaultIntervalMinutes} is used instead."]);
    }
}
