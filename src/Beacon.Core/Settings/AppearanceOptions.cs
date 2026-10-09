using System.Text.Json;

namespace Beacon.Core.Settings;

/// <summary>Whether Beacon's windows are light or dark.</summary>
public enum Appearance
{
    /// <summary>As Windows is set, and changing with it.</summary>
    System,

    /// <summary>Light, whatever Windows is set to.</summary>
    Light,

    /// <summary>Dark, whatever Windows is set to.</summary>
    Dark,
}

/// <summary>
/// The "appearance" setting: "system", "light" or "dark". Left out, Beacon follows Windows,
/// and so it does for a value that is none of the three, which is said.
/// </summary>
/// <param name="Value">What Beacon's windows are to look like.</param>
/// <param name="Notes">A sentence for the owner when the setting could not be used: fixed words.</param>
public sealed record AppearanceOptions(Appearance Value, IReadOnlyList<string> Notes)
{
    /// <summary>The setting's name in settings.json.</summary>
    public const string SettingName = "appearance";

    public const string NotOneOfThreeNote = "\"appearance\" must be \"system\", \"light\" or \"dark\". Beacon follows Windows.";

    /// <summary>Beacon follows Windows, and there is nothing to say.</summary>
    public static AppearanceOptions FollowWindows { get; } = new(Appearance.System, []);

    /// <param name="setting">The setting as it was read, or null when the file does not have it.</param>
    public static AppearanceOptions From(JsonElement? setting)
    {
        if (setting is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return FollowWindows;
        }

        return value.ValueKind == JsonValueKind.String && Parse(value.GetString()) is { } appearance
            ? new AppearanceOptions(appearance, [])
            : new AppearanceOptions(Appearance.System, [NotOneOfThreeNote]);
    }

    /// <summary>The appearance a text names, whatever the case of its letters and with spaces round it allowed, or null for any other text.</summary>
    public static Appearance? Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "system" => Appearance.System,
        "light" => Appearance.Light,
        "dark" => Appearance.Dark,
        _ => null,
    };

    /// <summary>How an appearance is written in settings.json.</summary>
    public static string Text(Appearance appearance) => appearance switch
    {
        Appearance.Light => "light",
        Appearance.Dark => "dark",
        _ => "system",
    };
}
