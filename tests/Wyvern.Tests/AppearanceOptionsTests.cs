using System.Text.Json;
using Wyvern.Core.Settings;

namespace Wyvern.Tests;

/// <summary>The "appearance" setting: whether Wyvern's windows are light or dark, or as Windows is set.</summary>
public sealed class AppearanceOptionsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void A_setting_that_is_not_there_follows_windows_and_there_is_nothing_to_say()
    {
        Assert.Same(AppearanceOptions.FollowWindows, AppearanceOptions.From(null));
        Assert.Same(AppearanceOptions.FollowWindows, AppearanceOptions.From(Json("null")));
        Assert.Equal(Appearance.System, AppearanceOptions.FollowWindows.Value);
        Assert.Empty(AppearanceOptions.FollowWindows.Notes);
    }

    [Theory]
    [InlineData("\"system\"", Appearance.System)]
    [InlineData("\"light\"", Appearance.Light)]
    [InlineData("\"dark\"", Appearance.Dark)]
    [InlineData("\"Dark\"", Appearance.Dark)]
    [InlineData("\"  LIGHT \"", Appearance.Light)]
    public void Each_of_the_three_is_understood_whatever_the_case_of_its_letters(string json, Appearance appearance)
    {
        var options = AppearanceOptions.From(Json(json));

        Assert.Equal(appearance, options.Value);
        Assert.Empty(options.Notes);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"darker\"")]
    [InlineData("\"auto\"")]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("[\"dark\"]")]
    [InlineData("{ \"dark\": true }")]
    public void Anything_else_follows_windows_and_says_so_in_fixed_words(string json)
    {
        var options = AppearanceOptions.From(Json(json));

        Assert.Equal(Appearance.System, options.Value);
        Assert.Equal([AppearanceOptions.NotOneOfThreeNote], options.Notes);
        Assert.Equal("\"appearance\" must be \"system\", \"light\" or \"dark\". Wyvern follows Windows.", AppearanceOptions.NotOneOfThreeNote);
    }

    [Theory]
    [InlineData(Appearance.System, "system")]
    [InlineData(Appearance.Light, "light")]
    [InlineData(Appearance.Dark, "dark")]
    public void What_is_written_for_an_appearance_is_what_is_read_for_it(Appearance appearance, string text)
    {
        Assert.Equal(text, AppearanceOptions.Text(appearance));
        Assert.Equal(appearance, AppearanceOptions.Parse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("black")]
    public void A_text_that_names_none_of_the_three_is_no_appearance(string? text) => Assert.Null(AppearanceOptions.Parse(text));

    [Fact]
    public void The_setting_is_read_from_settings_json_as_it_was_written()
    {
        string path = _dir.File("settings.json");
        File.WriteAllText(path, """{ "Appearance": "dark" }""");

        var loaded = SettingsStore.Load(path);

        Assert.Null(loaded.Error);
        Assert.Equal(Appearance.Dark, AppearanceOptions.From(loaded.Settings.Appearance).Value);
    }

    [Fact]
    public void The_file_wyvern_first_writes_has_no_appearance_and_so_follows_windows()
    {
        string path = _dir.File("settings.json");

        var loaded = SettingsStore.Load(path);

        Assert.Null(loaded.Settings.Appearance);
        Assert.DoesNotContain("appearance", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Appearance.System, AppearanceOptions.From(loaded.Settings.Appearance).Value);
    }

    [Fact]
    public void The_settings_window_s_document_writes_the_choice_and_takes_it_out_for_following_windows()
    {
        string path = _dir.File("settings.json");
        File.WriteAllText(path, """{ "hotkey": "Alt+Space" }""");
        var document = SettingsDocument.Read(path)!;

        document.Set(AppearanceOptions.SettingName, AppearanceOptions.Text(Appearance.Dark));
        document.Write(path);
        Assert.Equal(Appearance.Dark, AppearanceOptions.From(SettingsStore.Load(path).Settings.Appearance).Value);
        Assert.Equal(Appearance.Dark, AppearanceOptions.Parse(SettingsDocument.Read(path)!.Text(AppearanceOptions.SettingName)));

        document.Set(AppearanceOptions.SettingName, (string?)null);
        document.Write(path);
        Assert.DoesNotContain("appearance", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }
}
