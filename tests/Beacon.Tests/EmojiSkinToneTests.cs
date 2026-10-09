using Beacon.Core.Launcher;
using Beacon.Core.Search;

namespace Beacon.Tests;

/// <summary>The skin tone the emoji picker gives the emoji of people and hands.</summary>
public sealed class EmojiSkinToneTests : IDisposable
{
    private const string EmojiForm = "\uFE0F";
    private const string VictoryAlone = "\u270C";

    private static readonly string Hand = char.ConvertFromUtf32(0x1F44D);
    private static readonly string Victory = VictoryAlone + EmojiForm;
    private static readonly string Fire = char.ConvertFromUtf32(0x1F525);
    private static readonly string MediumTone = char.ConvertFromUtf32(0x1F3FD);

    private readonly TempDir _dir = new();
    private readonly List<EmojiItem> _all =
    [
        new EmojiItem(Hand, "Thumbs Up Sign", "People") { TakesSkinTone = true },
        new EmojiItem(Victory, "Victory Hand", "People") { TakesSkinTone = true },
        new EmojiItem(Fire, "Fire", "Nature"),
    ];

    private readonly List<string> _pasted = [];
    private readonly Dictionary<string, double> _boosts = [];
    private readonly EmojiViewModel _view;

    public EmojiSkinToneTests()
    {
        _view = new EmojiViewModel(() => _all, new EmojiActions(_pasted.Add, _ => { }), id => _boosts.GetValueOrDefault(id, 1.0));
    }

    public void Dispose() => _dir.Dispose();

    private string[] Shown() => _view.Rows.SelectMany(row => row.Cells).Select(cell => cell.Emoji.Text).ToArray();

    [Theory]
    [InlineData(SkinTone.Light, 0x1F3FB)]
    [InlineData(SkinTone.MediumLight, 0x1F3FC)]
    [InlineData(SkinTone.Medium, 0x1F3FD)]
    [InlineData(SkinTone.MediumDark, 0x1F3FE)]
    [InlineData(SkinTone.Dark, 0x1F3FF)]
    public void A_tone_is_the_character_for_it_after_the_emoji(SkinTone tone, int character) =>
        Assert.Equal(Hand + char.ConvertFromUtf32(character), EmojiViewModel.Toned(Hand, tone));

    [Fact]
    public void No_tone_leaves_the_emoji_as_it_is_and_a_tone_takes_the_place_of_the_mark_for_the_emoji_form()
    {
        Assert.Equal(Victory, EmojiViewModel.Toned(Victory, SkinTone.Default));
        Assert.Equal(VictoryAlone + MediumTone, EmojiViewModel.Toned(Victory, SkinTone.Medium));
    }

    [Fact]
    public void With_a_tone_chosen_the_emoji_that_take_one_are_shown_in_it_and_the_others_as_they_are()
    {
        _view.Open();
        Assert.Equal([Hand, Victory, Fire], Shown());

        _view.Tone = SkinTone.Medium;

        Assert.Equal([Hand + MediumTone, VictoryAlone + MediumTone, Fire], Shown());
        Assert.Equal(Hand + MediumTone, _view.Selected!.Text);
        Assert.Equal(Hand, _view.Selected.Plain);
        Assert.Equal("Thumbs Up Sign", _view.Selected.Name);

        _view.Tone = SkinTone.Default;
        Assert.Equal([Hand, Victory, Fire], Shown());
        Assert.Null(_view.Selected!.Plain);
    }

    [Fact]
    public void The_tone_is_kept_when_the_picker_closes_and_is_announced_when_it_changes()
    {
        var announced = new List<string?>();
        _view.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        // Chosen while the picker is closed: nothing is listed, and the next opening has it.
        _view.Tone = SkinTone.Dark;
        Assert.Equal([nameof(EmojiViewModel.Tone)], announced);
        Assert.Empty(_view.Rows);

        _view.Open();
        Assert.Equal(Hand + char.ConvertFromUtf32(0x1F3FF), Shown()[0]);
        _view.Close();
        Assert.Equal(SkinTone.Dark, _view.Tone);

        announced.Clear();
        _view.Tone = SkinTone.Dark;
        Assert.Empty(announced);
    }

    [Fact]
    public void An_emoji_in_a_tone_is_pasted_in_the_tone_and_its_use_counts_for_the_emoji_itself()
    {
        var usage = new UsageStore(_dir.File("usage.json"), new FakeClock());
        var launcher = new LauncherViewModel(new SearchEngine([], usage), usage, emoji: _view);
        _view.Tone = SkinTone.Medium;
        _view.Open();

        launcher.RunSelected();

        Assert.Equal([Hand + MediumTone], _pasted);
        Assert.True(usage.Boost("emoji:" + Hand) > 1.0);
        Assert.Equal(1.0, usage.Boost("emoji:" + Hand + MediumTone));
    }

    [Fact]
    public void An_emoji_used_before_is_listed_first_whatever_tone_is_chosen_now()
    {
        _boosts["emoji:" + Victory] = 4.0;
        _view.Tone = SkinTone.Light;

        _view.Open();

        Assert.Equal(EmojiViewModel.FrequentGroup, _view.Rows[0].Heading);
        Assert.Equal(VictoryAlone + char.ConvertFromUtf32(0x1F3FB), _view.Rows[1].Cells[0].Emoji.Text);
    }

    [Theory]
    [InlineData(SkinTone.Default, "default")]
    [InlineData(SkinTone.Light, "light")]
    [InlineData(SkinTone.MediumLight, "medium-light")]
    [InlineData(SkinTone.Medium, "medium")]
    [InlineData(SkinTone.MediumDark, "medium-dark")]
    [InlineData(SkinTone.Dark, "dark")]
    public void A_tone_is_written_as_it_is_read(SkinTone tone, string text)
    {
        Assert.Equal(text, EmojiViewModel.ToneText(tone));
        Assert.Equal(tone, EmojiViewModel.ToneOf(text));
        Assert.Equal(tone, EmojiViewModel.ToneOf("  " + text.ToUpperInvariant() + " "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("purple")]
    public void A_text_that_names_no_tone_is_the_default(string? text) => Assert.Equal(SkinTone.Default, EmojiViewModel.ToneOf(text));
}
