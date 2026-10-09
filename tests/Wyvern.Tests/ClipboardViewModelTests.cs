using System.Globalization;
using Wyvern.Core.ClipboardHistory;
using Wyvern.Core.Launcher;

namespace Wyvern.Tests;

/// <summary>
/// The clipboard history view by itself. The history is a list the test fills, the clock stands
/// still, and times are shown thirteen hours ahead of UTC, so that a time worked out in UTC or
/// in the machine's own zone is seen to be wrong.
/// </summary>
public class ClipboardViewModelTests
{
    private const string Folder = @"C:\made-up\clipboard\OTTER-PC\2026-10-03";

    // The clock stands at 2026-10-03 12:00 UTC, which is 01:00 on 4 October in this zone.
    private static readonly TimeZoneInfo Ahead =
        TimeZoneInfo.CreateCustomTimeZone("Wyvern test ahead", TimeSpan.FromHours(13), "Wyvern test ahead", "Wyvern test ahead");

    // Half an hour before the clock.
    private static readonly DateTimeOffset Copied = new(2026, 10, 3, 11, 30, 0, TimeSpan.Zero);

    // A seal: one character that takes two UTF-16 characters.
    private static readonly string Seal = char.ConvertFromUtf32(0x1F9AD);

    private readonly List<ClipEntry> _history = [];
    private readonly FakeClock _clock = new();
    private readonly List<string?> _announced = [];
    private readonly ClipboardViewModel _view;

    public ClipboardViewModelTests()
    {
        _view = new ClipboardViewModel(() => _history, _clock, zone: Ahead);
        _view.PropertyChanged += (_, changed) => _announced.Add(changed.PropertyName);
    }

    private static ClipEntry Clip(string id, DateTimeOffset? time = null) =>
        new(id, time ?? Copied, "OTTER-PC", new DateOnly(2026, 10, 3), Folder);

    private static ClipEntry TextClip(string id, string text) => Clip(id) with { Text = text, TextLength = text.Length };

    // A copied address. Its text is the address, as it is in the history.
    private static ClipEntry LinkClip(string id, string address, string? label = null) =>
        TextClip(id, address) with { Link = address, Label = label };

    // Picture files copied in Explorer whose content was kept: a file list and the kept pictures, with no picture of the clipboard's own.
    private static ClipEntry PictureFiles(string id, int count) => Clip(id) with
    {
        Files = Enumerable.Range(1, count).Select(i => $@"C:\harbour\seal-{i}.png").ToList(),
        Pictures = Enumerable.Range(1, count).Select(i => new KeptPicture($"seal-{i}.png", $"kept-{i}.png")).ToList(),
    };

    // Text in which no stretch is the same as the stretch beside it, so that its start can be told from any other part.
    private static string Letters(int length) => string.Create(length, 0, (span, _) =>
    {
        for (int i = 0; i < span.Length; i++)
        {
            span[i] = (char)('a' + (i % 26));
        }
    });

    private static void AssertWholeCharacters(string text)
    {
        // Half of a pair that was read by itself is turned into this character.
        Assert.DoesNotContain((char)0xFFFD, text);
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                Assert.True(i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]), "The text holds the first half of a character without its second.");
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(text[i]), "The text holds the second half of a character without its first.");
            }
        }
    }

    private void Open(params ClipEntry[] entries)
    {
        _history.AddRange(entries);
        _view.Open();
    }

    private ClipRow RowOf(ClipEntry entry)
    {
        Open(entry);
        return Assert.Single(_view.Rows);
    }

    private List<string> Ids() => _view.Rows.Select(row => row.Entry.Id).ToList();

    [Fact]
    public void It_starts_closed_and_shows_nothing()
    {
        _history.Add(TextClip("a", "walrus"));

        Assert.False(_view.IsOpen);
        Assert.Equal("", _view.Title);
        Assert.Equal("", _view.Prompt);
        Assert.Equal("", _view.Filter);
        Assert.Empty(_view.Rows);
        Assert.Equal("", _view.Status);
        Assert.Equal(-1, _view.SelectedIndex);
        Assert.Null(_view.Selected);
        Assert.Equal(ClipPreviewKind.None, _view.PreviewKind);
        Assert.Equal("", _view.PreviewText);
        Assert.Null(_view.PreviewImage);
        Assert.Equal("", _view.PreviewNote);
    }

    [Fact]
    public void Refresh_does_nothing_while_the_view_is_closed()
    {
        _history.Add(TextClip("a", "walrus"));

        _view.Refresh();

        Assert.Empty(_view.Rows);
        Assert.Equal(-1, _view.SelectedIndex);
        Assert.Empty(_announced);
    }

    [Fact]
    public void Moving_the_selection_does_nothing_while_the_view_is_closed()
    {
        _history.Add(TextClip("a", "walrus"));
        _history.Add(TextClip("b", "otter"));

        _view.MoveSelection(1);
        _view.MoveSelection(-1);

        Assert.Equal(-1, _view.SelectedIndex);
        Assert.Null(_view.Selected);
        Assert.Empty(_announced);
    }

    [Fact]
    public void Closing_a_view_that_is_not_open_returns_false_and_announces_nothing()
    {
        Assert.False(_view.Close());

        Assert.Empty(_announced);
    }

    [Fact]
    public void Removing_from_a_view_that_is_not_open_returns_null()
    {
        _history.Add(TextClip("a", "walrus"));

        Assert.Null(_view.Remove());

        Assert.Empty(_announced);
    }

    [Fact]
    public void A_filter_set_while_the_view_is_closed_changes_nothing_that_is_shown()
    {
        _history.Add(TextClip("a", "walrus"));

        _view.Filter = "walrus";

        Assert.False(_view.IsOpen);
        Assert.Empty(_view.Rows);
        Assert.Equal("", _view.Status);
        Assert.Equal(-1, _view.SelectedIndex);
        Assert.Empty(_announced);
    }

    [Fact]
    public void Opening_forgets_a_filter_that_was_set_while_the_view_was_closed()
    {
        _view.Filter = "walrus";

        Open(TextClip("a", "walrus"), TextClip("b", "otter"));

        Assert.Equal("", _view.Filter);
        Assert.Equal(["a", "b"], Ids());
    }

    [Fact]
    public void Opening_shows_the_heading_the_prompt_and_the_history_in_its_order_with_the_first_row_selected()
    {
        var newest = TextClip("a", "walrus");
        _history.AddRange([newest, TextClip("b", "otter"), TextClip("c", "seal")]);

        _view.Open();

        Assert.True(_view.IsOpen);
        Assert.Equal(ClipboardViewModel.Heading, _view.Title);
        Assert.Equal(ClipboardViewModel.FilterPrompt, _view.Prompt);
        Assert.Equal("", _view.Filter);
        Assert.Equal(["a", "b", "c"], Ids());
        Assert.Equal(0, _view.SelectedIndex);
        Assert.Same(newest, _view.Selected);
    }

    [Fact]
    public void Opening_announces_that_it_is_open_its_title_its_prompt_and_the_selection()
    {
        _history.Add(TextClip("a", "walrus"));

        _view.Open();

        Assert.Contains(nameof(ClipboardViewModel.IsOpen), _announced);
        Assert.Contains(nameof(ClipboardViewModel.Title), _announced);
        Assert.Contains(nameof(ClipboardViewModel.Prompt), _announced);
        Assert.Contains(nameof(ClipboardViewModel.SelectedIndex), _announced);
    }

    [Fact]
    public void Opening_a_view_that_is_already_open_starts_it_afresh()
    {
        Open(TextClip("a", "walrus"), TextClip("b", "otter"), TextClip("c", "otter pup"));
        _view.Filter = "otter";
        _view.MoveSelection(1);

        _view.Open();

        Assert.True(_view.IsOpen);
        Assert.Equal("", _view.Filter);
        Assert.Equal(["a", "b", "c"], Ids());
        Assert.Equal(0, _view.SelectedIndex);
    }

    [Fact]
    public void An_empty_history_says_that_nothing_has_been_copied_yet()
    {
        _view.Open();

        Assert.Empty(_view.Rows);
        Assert.Equal(ClipboardViewModel.EmptyStatus, _view.Status);
        Assert.Equal(-1, _view.SelectedIndex);
        Assert.Null(_view.Selected);
        Assert.Equal(ClipPreviewKind.None, _view.PreviewKind);
    }

    [Fact]
    public void A_filter_that_matches_nothing_says_so()
    {
        Open(TextClip("a", "walrus"));

        _view.Filter = "narwhal";

        Assert.Empty(_view.Rows);
        Assert.Equal(ClipboardViewModel.NoMatchStatus, _view.Status);
        Assert.Equal(-1, _view.SelectedIndex);
        Assert.Equal(ClipPreviewKind.None, _view.PreviewKind);
    }

    [Fact]
    public void There_is_no_status_while_there_are_rows()
    {
        Open(TextClip("a", "walrus"));

        Assert.Equal("", _view.Status);

        _view.Filter = "narwhal";
        _view.Filter = "wal";

        Assert.Equal("", _view.Status);
    }

    [Fact]
    public void The_status_is_announced_when_the_filter_stops_matching()
    {
        Open(TextClip("a", "walrus"));
        _announced.Clear();

        _view.Filter = "narwhal";

        Assert.Contains(nameof(ClipboardViewModel.Status), _announced);
    }

    [Fact]
    public void A_filter_of_spaces_alone_is_no_filter()
    {
        _view.Open();

        _view.Filter = "   ";

        Assert.Equal(ClipboardViewModel.EmptyStatus, _view.Status);

        _history.Add(TextClip("a", "walrus"));
        _view.Refresh();

        Assert.Equal(["a"], Ids());
    }

    [Fact]
    public void A_text_is_titled_with_its_first_line_that_is_not_blank_trimmed()
    {
        var row = RowOf(TextClip("a", "\r\n   \t\r\n   walrus tusks  \r\nsecond line"));

        Assert.Equal("walrus tusks", row.Title);
    }

    [Fact]
    public void A_first_line_longer_than_the_longest_title_is_shortened_and_ends_in_an_ellipsis()
    {
        var row = RowOf(TextClip("a", new string('w', 200) + "\nsecond line"));

        Assert.Equal(ClipboardViewModel.MaxTitleLength, row.Title.Length);
        Assert.Equal(new string('w', ClipboardViewModel.MaxTitleLength - 1) + "…", row.Title);
    }

    [Fact]
    public void A_first_line_as_long_as_the_longest_title_is_shown_whole()
    {
        string line = new('w', ClipboardViewModel.MaxTitleLength);

        var row = RowOf(TextClip("a", line));

        Assert.Equal(line, row.Title);
    }

    // The seal sits where a cut by count alone would fall between its two halves, or just
    // before or after it. It is kept whole when it fits and left out whole when it does not.
    [Theory]
    [InlineData(77, true)]
    [InlineData(78, false)]
    [InlineData(79, false)]
    public void A_shortened_title_never_ends_on_half_of_a_character_that_takes_two(int lettersBefore, bool sealFits)
    {
        string line = new string('w', lettersBefore) + Seal + new string('w', 50);

        var row = RowOf(TextClip("a", line));

        Assert.Equal(new string('w', lettersBefore) + (sealFits ? Seal : "") + "…", row.Title);
        Assert.True(row.Title.Length <= ClipboardViewModel.MaxTitleLength);
    }

    // Only the start of a very long line is looked at. Wherever that start ends, it ends on a
    // whole character: half of one would be shown as a broken box. The seal is moved along the
    // line one place at a time, behind characters that show nothing, so that it meets the end
    // of the start wherever that is.
    [Fact]
    public void A_title_holds_whole_characters_wherever_in_a_very_long_line_a_character_that_takes_two_sits()
    {
        _view.Open();
        for (int before = 0; before < 1000; before++)
        {
            _history.Clear();
            _history.Add(TextClip("a", new string((char)0x202E, before) + "walrus" + Seal + new string('w', 2000)));
            _view.Refresh();

            AssertWholeCharacters(Assert.Single(_view.Rows).Title);
        }
    }

    [Fact]
    public void A_character_that_shows_nothing_is_left_out_of_a_title()
    {
        var row = RowOf(TextClip("a", "wal" + (char)0x202E + "rus"));

        Assert.Equal("walrus", row.Title);
    }

    [Fact]
    public void A_line_of_nothing_but_characters_that_show_nothing_is_passed_over()
    {
        var row = RowOf(TextClip("a", new string((char)0x202E, 3) + "\notter"));

        Assert.Equal("otter", row.Title);
    }

    // White space inside a line is a space, not nothing: a copy from a spreadsheet is
    // "Name", a tab and "Amount", and a row must not read "NameAmount".

    [Fact]
    public void A_tab_between_two_words_of_a_title_is_a_space()
    {
        var row = RowOf(TextClip("a", "Name\tAmount"));

        Assert.Equal("Name Amount", row.Title);
    }

    [Fact]
    public void Two_tabs_and_a_space_between_two_words_of_a_title_are_one_space()
    {
        var row = RowOf(TextClip("a", "Name\t\t Amount"));

        Assert.Equal("Name Amount", row.Title);
    }

    // Each of these is white space that is a control character, or a separator of lines or
    // paragraphs, or a space of another width. A label is one line however it was copied, so
    // it shows each of them at work.
    [Theory]
    [InlineData(0x09)]
    [InlineData(0x0B)]
    [InlineData(0x0C)]
    [InlineData(0x85)]
    [InlineData(0xA0)]
    [InlineData(0x2003)]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    [InlineData(0x3000)]
    public void Any_white_space_between_two_words_of_a_label_is_one_space(int whiteSpace)
    {
        var row = RowOf(LinkClip("a", "https://example.org/walrus", "Name" + (char)whiteSpace + "Amount"));

        Assert.Equal("Name Amount", row.Title);
    }

    [Fact]
    public void White_space_at_the_start_and_the_end_of_a_label_is_trimmed_away()
    {
        string label = "\t" + (char)0xA0 + "Walrus facts\t" + (char)0x2028;

        var row = RowOf(LinkClip("a", "https://example.org/walrus", label));

        Assert.Equal("Walrus facts", row.Title);
    }

    [Fact]
    public void A_character_that_shows_nothing_between_spaces_does_not_leave_two_spaces()
    {
        var row = RowOf(LinkClip("a", "https://example.org/walrus", "Name " + (char)0x200B + " Amount"));

        Assert.Equal("Name Amount", row.Title);
    }

    [Fact]
    public void White_space_inside_a_file_name_is_one_space_too()
    {
        var row = RowOf(Clip("a") with { Files = [@"C:\harbour\tusks" + "\t\t" + "and  pond.txt"] });

        Assert.Equal("tusks and pond.txt", row.Title);
    }

    // The first line is found as it always was: the line breaks of the text divide it, not the
    // tabs inside a line.
    [Fact]
    public void The_first_line_is_still_found_by_the_line_breaks_of_the_text()
    {
        var row = RowOf(TextClip("a", "\r\n\t\r\n\tName\tAmount\t\r\nsecond\tline"));

        Assert.Equal("Name Amount", row.Title);
    }

    [Fact]
    public void An_entry_that_is_only_a_picture_is_titled_Image()
    {
        var row = RowOf(Clip("a") with { Image = "walrus.png" });

        Assert.Equal("Image", row.Title);
    }

    [Theory]
    [InlineData(@"C:\harbour\ice\tusks.txt")]
    [InlineData("C:/harbour/ice/tusks.txt")]
    [InlineData("tusks.txt")]
    public void A_single_copied_file_is_titled_with_its_name_and_not_its_folder(string path)
    {
        var row = RowOf(Clip("a") with { Files = [path] });

        Assert.Equal("tusks.txt", row.Title);
    }

    [Fact]
    public void Three_copied_files_are_titled_with_the_first_name_and_2_more()
    {
        var row = RowOf(Clip("a") with { Files = [@"C:\harbour\tusks.txt", @"C:\harbour\pond.txt", @"C:\den\holt.txt"] });

        Assert.Equal("tusks.txt and 2 more", row.Title);
    }

    [Fact]
    public void A_character_that_shows_nothing_is_left_out_of_a_file_name_too()
    {
        var row = RowOf(Clip("a") with { Files = [@"C:\harbour\tus" + (char)0x202E + "ks.txt"] });

        Assert.Equal("tusks.txt", row.Title);
    }

    [Fact]
    public void A_long_file_name_is_shortened_to_the_longest_title()
    {
        var row = RowOf(Clip("a") with { Files = [@"C:\harbour\" + new string('w', 120) + ".txt"] });

        Assert.Equal(new string('w', ClipboardViewModel.MaxTitleLength - 1) + "…", row.Title);
    }

    // A file list always gives a title. When the first path has no name (a root, or a name of
    // nothing but characters that show nothing), the path stands in for it.

    [Theory]
    [InlineData(@"C:\")]
    [InlineData("C:/")]
    public void A_copied_root_is_titled_with_its_path(string path)
    {
        var row = RowOf(Clip("a") with { Files = [path] });

        Assert.Equal(path, row.Title);
        Assert.Equal("", row.Marks);
    }

    [Fact]
    public void Several_copied_files_the_first_of_which_has_no_name_are_titled_with_its_path_and_the_count()
    {
        var row = RowOf(Clip("a") with { Files = [@"C:\", @"D:\"] });

        Assert.Equal(@"C:\ and 1 more", row.Title);
    }

    [Fact]
    public void A_file_whose_name_is_nothing_but_characters_that_show_nothing_is_titled_with_its_path_cleaned()
    {
        var row = RowOf(Clip("a") with { Files = [@"C:\harbour" + (char)0x202E + @"\" + (char)0x202E + (char)0x200B] });

        Assert.Equal(@"C:\harbour\", row.Title);
    }

    [Fact]
    public void A_path_that_stands_in_for_a_name_is_shortened_like_any_title()
    {
        var row = RowOf(Clip("a") with { Files = [@"C:\" + new string('w', 120) + @"\"] });

        Assert.Equal(ClipboardViewModel.MaxTitleLength, row.Title.Length);
        Assert.Equal(@"C:\" + new string('w', ClipboardViewModel.MaxTitleLength - 4) + "…", row.Title);
    }

    [Fact]
    public void A_path_that_stands_in_for_a_name_leaves_room_for_the_count()
    {
        var row = RowOf(Clip("a") with { Files = [@"C:\" + new string('w', 120) + @"\", @"D:\"] });

        Assert.True(row.Title.Length <= ClipboardViewModel.MaxTitleLength);
        Assert.EndsWith(" and 1 more", row.Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_file_list_of_nothing_that_can_be_shown_is_titled_Empty_text(string path)
    {
        var row = RowOf(Clip("a") with { Files = [path + (char)0x202E] });

        Assert.Equal("Empty text", row.Title);
        Assert.Equal("Files", row.Marks);
    }

    [Fact]
    public void A_file_list_of_nothing_that_can_be_shown_is_titled_by_the_picture_that_came_with_it()
    {
        var row = RowOf(Clip("a") with { Files = [""], Image = "walrus.png" });

        Assert.Equal("Image", row.Title);
    }

    // MaxTitleLength is "the longest title of a row", whatever the title is made from. The next
    // three are the titles that are not a line of text or one file's name.

    [Fact]
    public void The_title_of_several_files_is_no_longer_than_the_longest_title_however_long_the_first_name()
    {
        var row = RowOf(Clip("a") with { Files = [@"C:\harbour\" + new string('w', 120) + ".txt", @"C:\harbour\pond.txt", @"C:\den\holt.txt"] });

        Assert.True(
            row.Title.Length <= ClipboardViewModel.MaxTitleLength,
            $"The title is {row.Title.Length} characters long and the longest title is {ClipboardViewModel.MaxTitleLength}.");
        Assert.EndsWith("and 2 more", row.Title);
    }

    [Fact]
    public void An_address_shown_as_a_title_is_no_longer_than_the_longest_title()
    {
        var row = RowOf(LinkClip("a", "https://example.org/" + new string('w', 300)));

        Assert.True(
            row.Title.Length <= ClipboardViewModel.MaxTitleLength,
            $"The title is {row.Title.Length} characters long and the longest title is {ClipboardViewModel.MaxTitleLength}.");
        Assert.StartsWith("https://example.org/www", row.Title);
    }

    [Fact]
    public void A_label_shown_as_a_title_is_no_longer_than_the_longest_title()
    {
        var row = RowOf(LinkClip("a", "https://example.org/walrus", "Walrus facts " + new string('w', 180)));

        Assert.True(
            row.Title.Length <= ClipboardViewModel.MaxTitleLength,
            $"The title is {row.Title.Length} characters long and the longest title is {ClipboardViewModel.MaxTitleLength}.");
        Assert.StartsWith("Walrus facts www", row.Title);
    }

    // A copied address can hold a character that turns the text after it around: it is neither
    // white space nor a control character, so the address is still taken for a link. A row
    // shows text as it is written, and an address is text that came from the clipboard too.

    [Fact]
    public void A_character_that_turns_text_around_is_left_out_of_an_address_shown_as_a_title()
    {
        var row = RowOf(LinkClip("a", "https://example.org/" + (char)0x202E + "walrus"));

        Assert.Equal("https://example.org/walrus", row.Title);
    }

    [Fact]
    public void A_character_that_turns_text_around_is_left_out_of_an_address_shown_as_a_detail()
    {
        var row = RowOf(LinkClip("a", "https://example.org/" + (char)0x202E + "walrus", "Walrus facts"));

        Assert.Equal("https://example.org/walrus", row.Detail);
    }

    [Theory]
    [InlineData(true, false, "HTML")]
    [InlineData(false, true, "RTF")]
    [InlineData(true, true, "HTML  RTF")]
    public void An_entry_that_is_only_formatted_text_is_titled_so_and_marked_with_its_forms(bool html, bool rtf, string marks)
    {
        var row = RowOf(Clip("a") with { Html = html ? "walrus.html" : null, Rtf = rtf ? "walrus.rtf" : null });

        Assert.Equal("Formatted text", row.Title);
        Assert.Equal(marks, row.Marks);
    }

    [Fact]
    public void A_link_with_a_label_shows_the_label_as_title_and_the_address_as_detail()
    {
        var row = RowOf(LinkClip("a", "https://example.org/walrus", "Walrus facts"));

        Assert.Equal("Walrus facts", row.Title);
        Assert.Equal("https://example.org/walrus", row.Detail);
    }

    [Fact]
    public void A_link_without_a_label_shows_the_address_as_title_and_has_no_detail()
    {
        var row = RowOf(LinkClip("a", "https://example.org/walrus"));

        Assert.Equal("https://example.org/walrus", row.Title);
        Assert.Equal("", row.Detail);
    }

    [Fact]
    public void An_entry_that_is_not_a_link_has_no_detail()
    {
        var row = RowOf(TextClip("a", "walrus"));

        Assert.Equal("", row.Detail);
    }

    [Fact]
    public void Plain_text_has_no_marks()
    {
        var row = RowOf(TextClip("a", "walrus"));

        Assert.Equal("", row.Marks);
    }

    [Fact]
    public void A_link_is_marked_Link()
    {
        var row = RowOf(LinkClip("a", "https://example.org/walrus", "Walrus facts"));

        Assert.Equal("Link", row.Marks);
    }

    [Fact]
    public void Text_that_came_with_HTML_and_rich_text_is_marked_HTML_and_RTF()
    {
        var row = RowOf(TextClip("a", "walrus") with { Html = "walrus.html", Rtf = "walrus.rtf" });

        Assert.Equal("walrus", row.Title);
        Assert.Equal("HTML  RTF", row.Marks);
    }

    [Fact]
    public void Text_that_came_with_a_picture_is_marked_Image()
    {
        var row = RowOf(TextClip("a", "walrus") with { Image = "walrus.png" });

        Assert.Equal("walrus", row.Title);
        Assert.Equal("Image", row.Marks);
    }

    [Fact]
    public void A_picture_that_is_already_titled_Image_is_not_marked_Image_as_well()
    {
        var row = RowOf(Clip("a") with { Image = "walrus.png" });

        Assert.Equal("", row.Marks);
    }

    [Fact]
    public void Text_that_came_with_files_is_marked_Files()
    {
        var row = RowOf(TextClip("a", "walrus") with { Files = [@"C:\harbour\tusks.txt"] });

        Assert.Equal("walrus", row.Title);
        Assert.Equal("Files", row.Marks);
    }

    [Fact]
    public void Files_that_give_the_title_are_not_marked_Files_as_well()
    {
        var row = RowOf(Clip("a") with { Files = [@"C:\harbour\tusks.txt", @"C:\harbour\pond.txt"] });

        Assert.Equal("", row.Marks);
    }

    [Fact]
    public void Copied_files_whose_pictures_were_kept_are_titled_by_the_files_and_marked_Image()
    {
        var row = RowOf(PictureFiles("a", 2));

        Assert.Equal("seal-1.png and 1 more", row.Title);
        Assert.Equal("Image", row.Marks);
    }

    [Fact]
    public void The_marks_are_joined_by_two_spaces_in_the_order_Link_HTML_RTF_Image_Files()
    {
        var row = RowOf(LinkClip("a", "https://example.org/walrus") with
        {
            Html = "walrus.html",
            Rtf = "walrus.rtf",
            Image = "walrus.png",
            Files = [@"C:\harbour\tusks.txt"],
        });

        Assert.Equal("Link  HTML  RTF  Image  Files", row.Marks);
    }

    [Fact]
    public void A_row_shows_the_program_the_entry_was_copied_from()
    {
        var row = RowOf(TextClip("a", "walrus") with { SourceApp = "otter.exe" });

        Assert.Equal("otter.exe", row.Source);
    }

    // The program's name came from outside too, and may be from another machine's file. It is
    // cleaned the way every other line of a row is.

    [Fact]
    public void A_program_name_with_a_line_separator_is_shown_on_one_line()
    {
        var row = RowOf(TextClip("a", "walrus") with { SourceApp = "otter" + (char)0x2028 + "holt.exe" });

        Assert.Equal("otter holt.exe", row.Source);
    }

    [Fact]
    public void A_program_name_is_shown_without_characters_that_show_nothing_or_turn_text_around()
    {
        var row = RowOf(TextClip("a", "walrus") with { SourceApp = "ot" + (char)0x202E + "ter" + (char)0x200B + ".exe" });

        Assert.Equal("otter.exe", row.Source);
    }

    [Fact]
    public void A_program_name_with_tabs_and_line_breaks_is_shown_on_one_line()
    {
        var row = RowOf(TextClip("a", "walrus") with { SourceApp = "\totter\r\n\tholt.exe " });

        Assert.Equal("otter holt.exe", row.Source);
    }

    [Fact]
    public void A_row_of_an_entry_from_a_program_that_is_not_known_has_an_empty_source()
    {
        var row = RowOf(TextClip("a", "walrus"));

        Assert.Equal("", row.Source);
    }

    // The clock's day thirteen hours ahead is 4 October, which started at 11:00 UTC on 3 October.
    // In UTC each of these would show the UTC time of day instead.
    [Theory]
    [InlineData("2026-10-03T11:30:00+00:00", "00:30")]
    [InlineData("2026-10-03T11:00:00+00:00", "00:00")]
    [InlineData("2026-10-03T06:30:00-05:00", "00:30")]
    public void An_entry_of_the_clocks_own_day_in_the_given_zone_shows_the_time_alone(string copied, string expected)
    {
        var row = RowOf(TextClip("a", "walrus") with { Time = DateTimeOffset.Parse(copied, CultureInfo.InvariantCulture) });

        Assert.Equal(expected, row.Time);
    }

    // The first two are of the clock's own day in UTC, where they would show the time alone.
    [Theory]
    [InlineData("2026-10-03T10:59:00+00:00", "2026-10-03 23:59")]
    [InlineData("2026-10-03T00:05:00+00:00", "2026-10-03 13:05")]
    [InlineData("2026-09-30T20:15:00+00:00", "2026-10-01 09:15")]
    [InlineData("2026-10-03T19:59:00+09:00", "2026-10-03 23:59")]
    public void An_entry_of_an_earlier_day_in_the_given_zone_shows_the_date_as_well(string copied, string expected)
    {
        var row = RowOf(TextClip("a", "walrus") with { Time = DateTimeOffset.Parse(copied, CultureInfo.InvariantCulture) });

        Assert.Equal(expected, row.Time);
    }

    [Fact]
    public void An_afternoon_is_shown_on_the_24_hour_clock()
    {
        // 21:00 on 4 October thirteen hours ahead, and an entry copied at 15:05 that day.
        _clock.UtcNow = new DateTimeOffset(2026, 10, 4, 8, 0, 0, TimeSpan.Zero);

        var row = RowOf(TextClip("a", "walrus") with { Time = new DateTimeOffset(2026, 10, 4, 2, 5, 0, TimeSpan.Zero) });

        Assert.Equal("15:05", row.Time);
    }

    // The same entry and the same clock in another zone, four hours behind UTC. No machine's own
    // zone can give both this answer and the one thirteen hours ahead.
    [Fact]
    public void Times_are_shown_in_the_zone_the_view_was_given()
    {
        var behind = TimeZoneInfo.CreateCustomTimeZone("Wyvern test behind", TimeSpan.FromHours(-4), "Wyvern test behind", "Wyvern test behind");
        var view = new ClipboardViewModel(() => _history, _clock, zone: behind);
        _history.Add(TextClip("a", "walrus"));

        view.Open();
        _view.Open();

        Assert.Equal("07:30", Assert.Single(view.Rows).Time);
        Assert.Equal("00:30", Assert.Single(_view.Rows).Time);
    }

    [Fact]
    public void What_counts_as_today_is_worked_out_again_when_the_rows_are_made_again()
    {
        var row = RowOf(TextClip("a", "walrus"));
        Assert.Equal("00:30", row.Time);

        _clock.Advance(TimeSpan.FromDays(1));
        _view.Refresh();

        Assert.Equal("2026-10-04 00:30", Assert.Single(_view.Rows).Time);
    }

    [Fact]
    public void The_filter_matches_text_whatever_the_case_of_the_letters()
    {
        Open(TextClip("a", "The WALRUS sleeps"), TextClip("b", "otter"), TextClip("c", "first line\nthen a Walrus"));

        _view.Filter = "walrus";

        Assert.Equal(["a", "c"], Ids());
    }

    // In Turkish the capital of "i" is a dotted letter and the small form of "I" is a dotless
    // one, so a match by the machine's own rules would not find "TITLE" with "title". The
    // filter goes by the plain rule of letters, the same on every machine.
    [Theory]
    [InlineData("TITLE", "title")]
    [InlineData("title", "TITLE")]
    public void The_filter_goes_by_the_plain_rule_of_letters_under_the_Turkish_culture(string text, string filter)
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
        try
        {
            Open(TextClip("a", text), TextClip("b", "otter"));

            _view.Filter = filter;

            Assert.Equal(["a"], Ids());
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void The_filter_matches_the_name_of_any_copied_file_whatever_the_case_of_the_letters()
    {
        Open(
            Clip("a") with { Files = [@"C:\harbour\ice\pond.txt", @"C:\harbour\ice\Tusks.txt"] },
            Clip("b") with { Files = [@"C:\harbour\ice\holt.txt"] });

        _view.Filter = "TUSKS";

        Assert.Equal(["a"], Ids());
    }

    [Theory]
    [InlineData("harbour")]
    [InlineData("ice")]
    [InlineData(@"ice\Tusks")]
    [InlineData("C:")]
    public void The_filter_does_not_match_a_folder_in_the_path_of_a_copied_file(string filter)
    {
        Open(Clip("a") with { Files = [@"C:\harbour\ice\Tusks.txt"] });

        _view.Filter = filter;

        Assert.Empty(_view.Rows);
        Assert.Equal(ClipboardViewModel.NoMatchStatus, _view.Status);
    }

    [Fact]
    public void The_filter_matches_the_program_an_entry_was_copied_from_whatever_the_case_of_the_letters()
    {
        Open(TextClip("a", "walrus") with { SourceApp = "Otter.exe" }, TextClip("b", "seal") with { SourceApp = "puffin.exe" });

        _view.Filter = "otter.EXE";

        Assert.Equal(["a"], Ids());
    }

    [Fact]
    public void The_filter_matches_the_label_of_a_link_whatever_the_case_of_the_letters()
    {
        Open(LinkClip("a", "https://example.org/a", "Walrus Facts"), LinkClip("b", "https://example.org/b", "Otter news"));

        _view.Filter = "FACTS";

        Assert.Equal(["a"], Ids());
    }

    [Fact]
    public void Spaces_around_the_filter_are_not_part_of_it()
    {
        Open(TextClip("a", "walrus"), TextClip("b", "otter"));

        _view.Filter = "  walrus \t";

        Assert.Equal(["a"], Ids());
    }

    // The row shows "Name Amount" for a text that holds a tab, so what is read on the screen is
    // what is typed to find it: a space in the filter matches white space of any kind and any
    // length in the text.

    [Fact]
    public void A_space_in_the_filter_finds_a_tab_in_the_text()
    {
        Open(TextClip("a", "Name\tAmount"), TextClip("b", "otter"));

        _view.Filter = "name amount";

        Assert.Equal(["a"], Ids());
    }

    [Theory]
    [InlineData(0x09)]
    [InlineData(0x0A)]
    [InlineData(0x0D)]
    [InlineData(0x0B)]
    [InlineData(0x85)]
    [InlineData(0xA0)]
    [InlineData(0x2003)]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    [InlineData(0x3000)]
    public void A_space_in_the_filter_finds_white_space_of_any_kind_in_the_text(int whiteSpace)
    {
        Open(TextClip("a", "Name" + (char)whiteSpace + "Amount"), TextClip("b", "otter"));

        _view.Filter = "name amount";

        Assert.Equal(["a"], Ids());
    }

    [Fact]
    public void A_space_in_the_filter_finds_a_run_of_white_space_and_line_breaks_in_the_text()
    {
        Open(TextClip("a", "first line\r\n\r\n  \tName \t Amount\r\nlast line"));

        _view.Filter = "name amount";

        Assert.Equal(["a"], Ids());
    }

    [Theory]
    [InlineData("name   amount")]
    [InlineData("name\tamount")]
    [InlineData("  name \t amount  ")]
    [InlineData("NAME AMOUNT")]
    public void However_the_gap_in_the_filter_is_typed_it_finds_one_space_in_the_text(string filter)
    {
        Open(TextClip("a", "Name Amount"), TextClip("b", "otter"));

        _view.Filter = filter;

        Assert.Equal(["a"], Ids());
    }

    [Fact]
    public void Every_gap_in_a_filter_of_several_words_is_matched_and_the_last_word_may_be_a_start()
    {
        Open(TextClip("a", "Name\tAmount\r\nDue by Friday"), TextClip("b", "Name Amount Paid"));

        _view.Filter = "amount due by fri";

        Assert.Equal(["a"], Ids());
    }

    [Fact]
    public void The_words_of_the_filter_must_follow_one_another_with_a_gap_between_and_nothing_else()
    {
        Open(
            TextClip("a", "NameAmount"),
            TextClip("b", "Name, Amount"),
            TextClip("c", "Name then Amount"),
            TextClip("d", "Amount\tName"),
            TextClip("e", "Name\u200BAmount"));

        _view.Filter = "name amount";

        Assert.Empty(_view.Rows);
        Assert.Equal(ClipboardViewModel.NoMatchStatus, _view.Status);
    }

    [Fact]
    public void A_gap_in_the_text_that_is_not_where_the_filter_has_one_does_not_match()
    {
        Open(TextClip("a", "Na me\tAmount"));

        _view.Filter = "name amount";

        Assert.Empty(_view.Rows);
    }

    [Fact]
    public void The_match_is_found_at_a_later_place_when_the_first_word_stands_earlier_without_the_second_after_it()
    {
        Open(TextClip("a", "name name name\tamount"), TextClip("b", "name name"));

        _view.Filter = "name amount";

        Assert.Equal(["a"], Ids());
    }

    [Fact]
    public void A_space_in_the_filter_finds_a_tab_in_a_program_a_label_and_the_name_of_a_file()
    {
        Open(
            TextClip("a", "walrus") with { SourceApp = "sea\totter.exe" },
            LinkClip("b", "https://example.org/b", "Walrus\tfacts"),
            Clip("c") with { Files = [@"C:\harbour\ice\tusks" + "\t" + "and pond.txt"] },
            TextClip("d", "seal"));

        _view.Filter = "sea otter";
        Assert.Equal(["a"], Ids());

        _view.Filter = "walrus facts";
        Assert.Equal(["b"], Ids());

        _view.Filter = "tusks and";
        Assert.Equal(["c"], Ids());
    }

    [Fact]
    public void A_filter_of_one_word_matches_as_it_always_did()
    {
        Open(TextClip("a", "The WALRUS sleeps"), TextClip("b", "Name\tAmount"), TextClip("c", "otter"));

        _view.Filter = "ame";
        Assert.Equal(["b"], Ids());

        _view.Filter = "walrus";
        Assert.Equal(["a"], Ids());
    }

    // The filter is worked out at each keystroke, on the UI thread, over every entry's start of
    // up to 100,000 characters. Neither the text of an entry nor a copy of it may be made for
    // each: a copy of two hundred such texts would be tens of megabytes at each key.
    [Fact]
    public void Filtering_a_long_history_for_words_with_a_gap_makes_no_copy_of_the_text_of_any_entry()
    {
        string text = string.Concat(Enumerable.Repeat("word\t", ClipEntry.MaxTextStart / 5));
        Assert.Equal(ClipEntry.MaxTextStart, text.Length);
        for (int i = 0; i < 200; i++)
        {
            _history.Add(TextClip("id-" + i.ToString(CultureInfo.InvariantCulture), text));
        }

        _view.Open();

        // Once to have everything compiled and warmed up, so that only what the filter does is counted.
        _view.Filter = "word zzz";
        long before = GC.GetAllocatedBytesForCurrentThread();
        _view.Filter = "word yyy";
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Empty(_view.Rows);
        Assert.True(allocated < 200_000, $"Setting the filter allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void The_rows_that_match_keep_the_order_of_the_history()
    {
        Open(TextClip("a", "walrus one"), TextClip("b", "otter"), TextClip("c", "walrus two"), TextClip("d", "seal"), TextClip("e", "walrus three"));

        _view.Filter = "walrus";

        Assert.Equal(["a", "c", "e"], Ids());
    }

    [Fact]
    public void Emptying_the_filter_brings_every_row_back()
    {
        Open(TextClip("a", "walrus"), TextClip("b", "otter"));
        _view.Filter = "otter";

        _view.Filter = "";

        Assert.Equal(["a", "b"], Ids());
    }

    [Fact]
    public void No_more_than_the_most_rows_are_listed_and_they_are_the_newest()
    {
        for (int i = 0; i < ClipboardViewModel.MaxRows + 5; i++)
        {
            _history.Add(TextClip("id-" + i.ToString(CultureInfo.InvariantCulture), "walrus"));
        }

        _view.Open();

        Assert.Equal(ClipboardViewModel.MaxRows, _view.Rows.Count);
        Assert.Equal("id-0", _view.Rows[0].Entry.Id);
        Assert.Equal("id-" + (ClipboardViewModel.MaxRows - 1).ToString(CultureInfo.InvariantCulture), _view.Rows[^1].Entry.Id);
    }

    [Fact]
    public void The_most_rows_are_counted_among_the_entries_that_match()
    {
        for (int i = 0; i < ClipboardViewModel.MaxRows + 5; i++)
        {
            string number = i.ToString(CultureInfo.InvariantCulture);
            _history.Add(TextClip("otter-" + number, "otter"));
            _history.Add(TextClip("walrus-" + number, "walrus"));
        }

        _view.Open();
        _view.Filter = "walrus";

        Assert.Equal(ClipboardViewModel.MaxRows, _view.Rows.Count);
        Assert.All(_view.Rows, row => Assert.Equal("walrus", row.Entry.Text));
        Assert.Equal("walrus-0", _view.Rows[0].Entry.Id);
        Assert.Equal("walrus-" + (ClipboardViewModel.MaxRows - 1).ToString(CultureInfo.InvariantCulture), _view.Rows[^1].Entry.Id);
    }

    // The row that was selected still matches, and is not the first: the filter does not keep it selected.
    [Fact]
    public void Setting_the_filter_selects_the_first_row()
    {
        Open(TextClip("a", "walrus one"), TextClip("b", "otter"), TextClip("c", "walrus two"), TextClip("d", "walrus three"));
        _view.MoveSelection(3);

        _view.Filter = "walrus";

        Assert.Equal(["a", "c", "d"], Ids());
        Assert.Equal(0, _view.SelectedIndex);
        Assert.Equal("a", _view.Selected?.Id);
    }

    [Fact]
    public void Moving_the_selection_stops_at_both_ends()
    {
        Open(TextClip("a", "walrus"), TextClip("b", "otter"), TextClip("c", "seal"));

        _view.MoveSelection(1);
        Assert.Equal(1, _view.SelectedIndex);

        _view.MoveSelection(5);
        Assert.Equal(2, _view.SelectedIndex);

        _view.MoveSelection(1);
        Assert.Equal(2, _view.SelectedIndex);

        _view.MoveSelection(-9);
        Assert.Equal(0, _view.SelectedIndex);

        _view.MoveSelection(-1);
        Assert.Equal(0, _view.SelectedIndex);
    }

    [Fact]
    public void Moving_the_selection_with_no_rows_does_nothing()
    {
        _view.Open();
        _announced.Clear();

        _view.MoveSelection(1);
        _view.MoveSelection(-1);

        Assert.Equal(-1, _view.SelectedIndex);
        Assert.Empty(_announced);
    }

    [Fact]
    public void The_selected_entry_follows_the_selection()
    {
        var first = TextClip("a", "walrus");
        var second = TextClip("b", "otter");
        var third = TextClip("c", "seal");
        Open(first, second, third);

        Assert.Same(first, _view.Selected);

        _view.MoveSelection(2);
        Assert.Same(third, _view.Selected);

        _view.SelectedIndex = 1;
        Assert.Same(second, _view.Selected);
    }

    [Fact]
    public void The_preview_follows_the_selection()
    {
        Open(TextClip("a", "walrus"), TextClip("b", "otter"), LinkClip("c", "https://example.org/seal"));
        Assert.Equal("walrus", _view.PreviewText);

        _view.MoveSelection(1);
        Assert.Equal(ClipPreviewKind.Text, _view.PreviewKind);
        Assert.Equal("otter", _view.PreviewText);

        _view.SelectedIndex = 2;
        Assert.Equal(ClipPreviewKind.Link, _view.PreviewKind);
        Assert.Equal("https://example.org/seal", _view.PreviewText);
    }

    [Fact]
    public void Moving_the_selection_announces_the_selection_and_the_preview()
    {
        Open(TextClip("a", "walrus"), TextClip("b", "otter"));
        _announced.Clear();

        _view.MoveSelection(1);

        Assert.Contains(nameof(ClipboardViewModel.SelectedIndex), _announced);
        Assert.Contains(nameof(ClipboardViewModel.PreviewKind), _announced);
        Assert.Contains(nameof(ClipboardViewModel.PreviewText), _announced);
        Assert.Contains(nameof(ClipboardViewModel.PreviewImage), _announced);
        Assert.Contains(nameof(ClipboardViewModel.PreviewNote), _announced);
    }

    // A list control sets -1 when it has no selection. The entry on show before has a picture
    // and a note, so each part of the preview is seen to be let go of.
    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(99)]
    public void With_nothing_selected_there_is_no_entry_and_nothing_is_previewed(int index)
    {
        Open(Clip("a") with { Image = "walrus.png" }, TextClip("b", "otter"));
        Assert.NotNull(_view.PreviewImage);

        _view.SelectedIndex = index;

        Assert.Null(_view.Selected);
        Assert.Equal(ClipPreviewKind.None, _view.PreviewKind);
        Assert.Equal("", _view.PreviewText);
        Assert.Null(_view.PreviewImage);
        Assert.Equal("", _view.PreviewNote);
    }

    [Fact]
    public void A_short_text_is_previewed_whole_with_no_note()
    {
        Open(TextClip("a", "walrus\r\n  otter"));

        Assert.Equal(ClipPreviewKind.Text, _view.PreviewKind);
        Assert.Equal("walrus\r\n  otter", _view.PreviewText);
        Assert.Null(_view.PreviewImage);
        Assert.Equal("", _view.PreviewNote);
    }

    [Fact]
    public void A_long_text_is_previewed_by_its_start_and_no_more()
    {
        string text = Letters(100_000);

        Open(Clip("a") with { Text = text, TextLength = 250_000, TextFile = "whole.txt" });

        Assert.Equal(ClipPreviewKind.Text, _view.PreviewKind);
        Assert.Equal(ClipboardViewModel.MaxPreviewChars, _view.PreviewText.Length);
        Assert.Equal(text[..ClipboardViewModel.MaxPreviewChars], _view.PreviewText);
    }

    [Fact]
    public void The_preview_of_a_text_that_is_not_all_shown_says_how_much_of_it_is()
    {
        Open(Clip("a") with { Text = Letters(100_000), TextLength = 250_000, TextFile = "whole.txt" });

        Assert.Equal("Showing the first 20,000 of 250,000 characters.", _view.PreviewNote);
    }

    [Fact]
    public void A_text_that_just_fits_the_preview_is_shown_whole_with_no_note()
    {
        string text = Letters(ClipboardViewModel.MaxPreviewChars);

        Open(TextClip("a", text));

        Assert.Equal(text, _view.PreviewText);
        Assert.Equal("", _view.PreviewNote);
    }

    // The seal sits where a cut by count alone would fall between its two halves.
    [Fact]
    public void The_preview_of_a_long_text_never_ends_on_half_of_a_character_that_takes_two()
    {
        string start = new('w', ClipboardViewModel.MaxPreviewChars - 1);

        Open(TextClip("a", start + Seal + new string('w', 100)));

        Assert.Equal(start, _view.PreviewText);
    }

    [Fact]
    public void A_link_with_a_label_is_previewed_as_the_label_and_under_it_the_address()
    {
        Open(LinkClip("a", "https://example.org/walrus", "Walrus facts"));

        Assert.Equal(ClipPreviewKind.Link, _view.PreviewKind);
        Assert.Equal("Walrus facts\nhttps://example.org/walrus", _view.PreviewText);
        Assert.Null(_view.PreviewImage);
        Assert.Equal(ClipboardViewModel.LinkNote, _view.PreviewNote);
    }

    [Fact]
    public void A_link_without_a_label_is_previewed_as_the_address_alone()
    {
        Open(LinkClip("a", "https://example.org/walrus"));

        Assert.Equal(ClipPreviewKind.Link, _view.PreviewKind);
        Assert.Equal("https://example.org/walrus", _view.PreviewText);
        Assert.Equal(ClipboardViewModel.LinkNote, _view.PreviewNote);
    }

    // What the preview shows of a link or a file list came from outside as much as what a row
    // shows, and is cleaned in the same way. Only a text is shown as it was copied.

    [Fact]
    public void The_label_and_the_address_of_a_link_are_previewed_without_characters_that_show_nothing_or_turn_text_around()
    {
        Open(LinkClip("a", "https://example.org/" + (char)0x202E + "walrus", "Walrus" + (char)0x202E + " facts"));

        Assert.Equal("Walrus facts\nhttps://example.org/walrus", _view.PreviewText);
    }

    [Fact]
    public void The_label_of_a_link_is_previewed_on_one_line_so_that_the_address_stays_under_it()
    {
        Open(LinkClip("a", "https://example.org/walrus", "Walrus\r\nfacts" + (char)0x2028 + "today"));

        Assert.Equal("Walrus facts today\nhttps://example.org/walrus", _view.PreviewText);
    }

    [Fact]
    public void The_address_of_a_long_link_is_previewed_whole()
    {
        string address = "https://example.org/" + new string('w', 300);

        Open(LinkClip("a", address, "Walrus facts"));

        Assert.Equal("Walrus facts\n" + address, _view.PreviewText);
    }

    [Fact]
    public void A_path_in_a_file_list_is_previewed_without_characters_that_show_nothing_or_turn_text_around_and_the_row_title_agrees()
    {
        // Left as it is, this reads as "invoiceexe.txt".
        var entry = Clip("a") with { Files = [@"C:\harbour\invoice" + (char)0x202E + "txt.exe"] };

        Open(entry);

        Assert.Equal(@"C:\harbour\invoicetxt.exe", _view.PreviewText);
        Assert.Equal("invoicetxt.exe", Assert.Single(_view.Rows).Title);
    }

    [Fact]
    public void Every_path_in_a_file_list_is_previewed_on_a_line_of_its_own_even_when_one_holds_a_line_break()
    {
        Open(Clip("a") with { Files = ["C:\\harbour\\tu\nsks.txt", @"C:\harbour\pond" + (char)0x2028 + ".txt", @"C:\den\holt.txt"] });

        Assert.Equal("C:\\harbour\\tu sks.txt\nC:\\harbour\\pond .txt\nC:\\den\\holt.txt", _view.PreviewText);
    }

    [Fact]
    public void A_long_path_in_a_file_list_is_previewed_whole()
    {
        string path = @"C:\harbour\" + new string('w', 300) + ".txt";

        Open(Clip("a") with { Files = [path] });

        Assert.Equal(path, _view.PreviewText);
    }

    [Fact]
    public void A_text_is_previewed_as_it_was_copied_with_its_line_breaks_and_whatever_characters_it_holds()
    {
        string text = "Name\tAmount\r\n  walrus" + (char)0x202E + "\n\n" + (char)0x2028 + "otter  ";

        Open(TextClip("a", text));

        Assert.Equal(text, _view.PreviewText);
    }

    [Fact]
    public void An_entry_with_text_and_a_picture_is_previewed_by_its_text()
    {
        Open(TextClip("a", "walrus") with { Image = "walrus.png" });

        Assert.Equal(ClipPreviewKind.Text, _view.PreviewKind);
        Assert.Equal("walrus", _view.PreviewText);
        Assert.Null(_view.PreviewImage);
        Assert.Equal("", _view.PreviewNote);
    }

    // A label is the page's own words for the link, and set on the entry directly it can be
    // anything, including another address. A label that is not fit to show (one rule, the same
    // as capture makes it by) is not shown, and the real address stands alone.
    [Fact]
    public void A_label_that_reads_like_another_address_is_not_shown_and_the_real_address_stands_alone()
    {
        var row = RowOf(LinkClip("a", "https://evil.example/steal", "https://bank.example/login"));

        Assert.Equal("https://evil.example/steal", row.Title);
        Assert.Equal("", row.Detail);
        Assert.Equal("https://evil.example/steal", _view.PreviewText);
    }

    [Theory]
    [MemberData(nameof(ClipLinksTests.LabelCases), MemberType = typeof(ClipLinksTests))]
    public void The_label_of_a_link_is_judged_in_the_row_and_in_the_preview_by_the_rule_capture_makes_it_by(string written, string? shown)
    {
        const string address = "https://example.org/walrus";

        var row = RowOf(LinkClip("a", address, written));

        if (shown is null)
        {
            Assert.Equal(address, row.Title);
            Assert.Equal("", row.Detail);
            Assert.Equal(address, _view.PreviewText);
        }
        else
        {
            Assert.Equal(shown, row.Title);
            Assert.Equal(address, row.Detail);
            Assert.Equal(shown + "\n" + address, _view.PreviewText);
        }
    }

    [Fact]
    public void A_label_that_looks_like_a_host_name_only_once_it_is_cut_for_the_row_is_left_out_of_the_row_and_not_of_the_preview()
    {
        // Each is judged as it is shown: the row cuts at the longest title, the preview at the longest label.
        string label = "bank.example/" + new string('x', 100) + " and more words";

        var row = RowOf(LinkClip("a", "https://example.org/walrus", label));

        Assert.Equal("https://example.org/walrus", row.Title);
        Assert.Equal("", row.Detail);
        Assert.Equal(label + "\nhttps://example.org/walrus", _view.PreviewText);
    }

    [Theory]
    [InlineData(0x3164)]
    [InlineData(0xFE0F)]
    [InlineData(0xE000)]
    [InlineData(0x2065)]
    [InlineData(0xF0000)]
    public void A_character_that_shows_nothing_but_is_not_a_control_or_format_character_is_left_out_of_what_a_row_shows(int code)
    {
        string character = char.ConvertFromUtf32(code);

        var text = RowOf(TextClip("a", "wal" + character + "rus") with { SourceApp = "ot" + character + "ter.exe" });

        Assert.Equal("walrus", text.Title);
        Assert.Equal("otter.exe", text.Source);
    }

    [Fact]
    public void Such_a_character_is_left_out_of_the_name_of_a_file_in_the_title_and_in_the_preview()
    {
        var entry = Clip("a") with { Files = [@"C:\harbour\tu" + char.ConvertFromUtf32(0x3164) + "sks" + char.ConvertFromUtf32(0xE000) + ".txt"] };

        Open(entry);

        Assert.Equal("tusks.txt", Assert.Single(_view.Rows).Title);
        Assert.Equal(@"C:\harbour\tusks.txt", _view.PreviewText);
    }

    [Fact]
    public void An_entry_that_is_only_a_picture_is_previewed_by_the_path_of_its_file()
    {
        var entry = Clip("a") with { Image = "walrus.png" };

        Open(entry);

        Assert.Equal(ClipPreviewKind.Image, _view.PreviewKind);
        Assert.Equal(entry.PathOf("walrus.png"), _view.PreviewImage);
        Assert.Equal("", _view.PreviewText);
        Assert.Equal(ClipboardViewModel.PictureNote, _view.PreviewNote);
    }

    [Fact]
    public void A_copied_picture_file_that_was_kept_is_previewed_by_the_path_of_the_kept_picture()
    {
        var entry = PictureFiles("a", 1);

        Open(entry);

        Assert.Equal(ClipPreviewKind.Image, _view.PreviewKind);
        Assert.Equal(entry.PathOf("kept-1.png"), _view.PreviewImage);
        Assert.Equal("", _view.PreviewText);
        Assert.Equal(ClipboardViewModel.PictureNote, _view.PreviewNote);
    }

    [Fact]
    public void Several_kept_pictures_are_previewed_by_the_first_and_the_note_says_how_many_there_are()
    {
        var entry = PictureFiles("a", 3);

        Open(entry);

        Assert.Equal(ClipPreviewKind.Image, _view.PreviewKind);
        Assert.Equal(entry.PathOf("kept-1.png"), _view.PreviewImage);
        Assert.StartsWith("The first of 3 pictures.", _view.PreviewNote);
        Assert.EndsWith(ClipboardViewModel.PictureNote, _view.PreviewNote);
    }

    [Fact]
    public void A_file_list_is_previewed_as_its_paths_one_to_a_line()
    {
        Open(Clip("a") with { Files = [@"C:\harbour\tusks.txt", @"C:\harbour\pond.txt", @"C:\den\holt.txt"] });

        Assert.Equal(ClipPreviewKind.Files, _view.PreviewKind);
        Assert.Equal("C:\\harbour\\tusks.txt\nC:\\harbour\\pond.txt\nC:\\den\\holt.txt", _view.PreviewText);
        Assert.Null(_view.PreviewImage);
        Assert.Equal("", _view.PreviewNote);
    }

    [Fact]
    public void A_file_list_longer_than_the_preview_shows_is_cut_and_the_note_gives_both_counts()
    {
        var files = Enumerable.Range(1, ClipboardViewModel.MaxPreviewFiles + 30)
            .Select(i => $@"C:\harbour\file-{i.ToString(CultureInfo.InvariantCulture)}.txt")
            .ToList();

        Open(Clip("a") with { Files = files });

        Assert.Equal(ClipPreviewKind.Files, _view.PreviewKind);
        Assert.Equal(string.Join("\n", files.Take(ClipboardViewModel.MaxPreviewFiles)), _view.PreviewText);
        Assert.Contains(ClipboardViewModel.MaxPreviewFiles.ToString(CultureInfo.InvariantCulture), _view.PreviewNote);
        Assert.Contains((ClipboardViewModel.MaxPreviewFiles + 30).ToString(CultureInfo.InvariantCulture), _view.PreviewNote);
    }

    [Fact]
    public void A_file_list_that_just_fits_the_preview_is_shown_whole_with_no_note()
    {
        var files = Enumerable.Range(1, ClipboardViewModel.MaxPreviewFiles)
            .Select(i => $@"C:\harbour\file-{i.ToString(CultureInfo.InvariantCulture)}.txt")
            .ToList();

        Open(Clip("a") with { Files = files });

        Assert.Equal(string.Join("\n", files), _view.PreviewText);
        Assert.Equal("", _view.PreviewNote);
    }

    [Fact]
    public void An_entry_that_is_only_formatted_text_is_previewed_as_no_text_and_a_note_that_says_why()
    {
        Open(Clip("a") with { Html = "walrus.html" });

        Assert.Equal(ClipPreviewKind.Text, _view.PreviewKind);
        Assert.Equal("", _view.PreviewText);
        Assert.Null(_view.PreviewImage);
        Assert.Equal(ClipboardViewModel.FormattedOnlyNote, _view.PreviewNote);
    }

    [Fact]
    public void Refresh_makes_the_rows_again_from_the_history_as_it_now_stands()
    {
        Open(TextClip("a", "walrus"), TextClip("b", "otter"));

        _history.RemoveAt(1);
        _history.Insert(0, TextClip("c", "seal"));
        Assert.Equal(["a", "b"], Ids());

        _view.Refresh();

        Assert.Equal(["c", "a"], Ids());
    }

    [Fact]
    public void Refresh_keeps_the_filter()
    {
        Open(TextClip("a", "walrus"), TextClip("b", "otter"));
        _view.Filter = "walrus";

        _history.Insert(0, TextClip("c", "otter pup"));
        _history.Insert(0, TextClip("d", "walrus calf"));
        _view.Refresh();

        Assert.Equal("walrus", _view.Filter);
        Assert.Equal(["d", "a"], Ids());
    }

    [Fact]
    public void Refresh_keeps_the_selected_row_selected_when_it_has_not_moved()
    {
        Open(TextClip("a", "walrus"), TextClip("b", "otter"), TextClip("c", "seal"));
        _view.MoveSelection(2);

        _history.Add(TextClip("d", "puffin"));
        _view.Refresh();

        Assert.Equal(2, _view.SelectedIndex);
        Assert.Equal("c", _view.Selected?.Id);
    }

    [Fact]
    public void Refresh_keeps_the_selected_row_selected_when_it_has_moved()
    {
        Open(TextClip("a", "walrus"), TextClip("b", "otter"), TextClip("c", "seal"));
        _view.MoveSelection(1);

        _history.Insert(0, TextClip("d", "puffin"));
        _history.Insert(0, TextClip("e", "narwhal"));
        _view.Refresh();

        Assert.Equal(["e", "d", "a", "b", "c"], Ids());
        Assert.Equal(3, _view.SelectedIndex);
        Assert.Equal("b", _view.Selected?.Id);
        Assert.Equal("otter", _view.PreviewText);
    }

    // When the selected entry goes for a reason the owner did not cause (it was deleted on
    // another machine, it expired, a scrub took it), the selection stays where it was. An Enter
    // pressed before the owner has seen the list change must not paste an entry he was not
    // looking at, and the newest entry is exactly that.
    [Fact]
    public void Refresh_keeps_the_selection_in_place_when_the_selected_one_is_gone()
    {
        Open(TextClip("a", "walrus"), TextClip("b", "otter"), TextClip("c", "seal"), TextClip("d", "puffin"));
        _view.MoveSelection(1);

        _history.RemoveAt(1);
        _view.Refresh();

        Assert.Equal(["a", "c", "d"], Ids());
        Assert.Equal(1, _view.SelectedIndex);
        Assert.Equal("c", _view.Selected?.Id);
        Assert.Equal("seal", _view.PreviewText);
    }

    // This is the test that used to be Refresh_selects_the_first_row_when_the_selected_one_is_gone:
    // the same history and selection, and now the last row that is left is selected, not the first.
    [Fact]
    public void Refresh_selects_the_last_row_when_the_selected_one_was_last_and_is_gone()
    {
        Open(TextClip("a", "walrus"), TextClip("b", "otter"), TextClip("c", "seal"));
        _view.MoveSelection(2);

        _history.RemoveAt(2);
        _view.Refresh();

        Assert.Equal(["a", "b"], Ids());
        Assert.Equal(1, _view.SelectedIndex);
        Assert.Equal("b", _view.Selected?.Id);
        Assert.Equal("otter", _view.PreviewText);
    }

    [Fact]
    public void Refresh_keeps_the_index_when_several_rows_are_gone_and_the_selected_one_among_them()
    {
        Open(TextClip("a", "walrus"), TextClip("b", "otter"), TextClip("c", "seal"), TextClip("d", "puffin"), TextClip("e", "narwhal"));
        _view.MoveSelection(3);

        _history.RemoveRange(2, 3);
        _view.Refresh();

        Assert.Equal(["a", "b"], Ids());
        Assert.Equal(1, _view.SelectedIndex);
        Assert.Equal("b", _view.Selected?.Id);
    }

    [Fact]
    public void Refresh_leaves_nothing_selected_when_no_row_is_left()
    {
        Open(TextClip("a", "walrus"), TextClip("b", "otter"));
        _view.MoveSelection(1);

        _history.Clear();
        _view.Refresh();

        Assert.Empty(_view.Rows);
        Assert.Equal(-1, _view.SelectedIndex);
        Assert.Null(_view.Selected);
        Assert.Equal(ClipPreviewKind.None, _view.PreviewKind);
        Assert.Equal(ClipboardViewModel.EmptyStatus, _view.Status);
    }

    [Fact]
    public void Refresh_selects_the_first_row_when_rows_appear_where_there_were_none()
    {
        _view.Open();
        Assert.Equal(-1, _view.SelectedIndex);

        _history.Add(TextClip("a", "walrus"));
        _history.Add(TextClip("b", "otter"));
        _view.Refresh();

        Assert.Equal(0, _view.SelectedIndex);
        Assert.Equal("a", _view.Selected?.Id);
    }

    [Fact]
    public void Refresh_keeps_the_selection_in_place_when_the_selected_one_is_gone_under_a_filter()
    {
        Open(TextClip("a", "walrus one"), TextClip("b", "otter"), TextClip("c", "walrus two"), TextClip("d", "walrus three"));
        _view.Filter = "walrus";
        _view.MoveSelection(1);

        _history.RemoveAt(2);
        _view.Refresh();

        Assert.Equal(["a", "d"], Ids());
        Assert.Equal(1, _view.SelectedIndex);
        Assert.Equal("d", _view.Selected?.Id);
    }

    // A bound list control drops its own selection when its items are replaced, so it has to be
    // told the selection again even when the index is the same as before.
    [Fact]
    public void The_selection_is_announced_every_time_the_rows_are_made_again()
    {
        Open(TextClip("a", "walrus"), TextClip("b", "otter"));
        _announced.Clear();

        _view.Refresh();
        _view.Filter = "o";

        Assert.Equal(2, _announced.Count(name => name == nameof(ClipboardViewModel.SelectedIndex)));
    }

    [Fact]
    public void Removing_returns_the_selected_entry_and_takes_its_row_out_while_the_history_still_holds_it()
    {
        var otter = TextClip("b", "otter");
        Open(TextClip("a", "walrus"), otter, TextClip("c", "seal"));
        _view.MoveSelection(1);

        var removed = _view.Remove();

        Assert.Same(otter, removed);
        Assert.Contains(otter, _history);
        Assert.Equal(["a", "c"], Ids());
    }

    [Fact]
    public void The_row_below_a_removed_row_takes_its_place()
    {
        Open(TextClip("a", "walrus"), TextClip("b", "otter"), TextClip("c", "seal"));
        _view.MoveSelection(1);
        _announced.Clear();

        _view.Remove();

        Assert.Equal(1, _view.SelectedIndex);
        Assert.Equal("c", _view.Selected?.Id);
        Assert.Equal("seal", _view.PreviewText);
        Assert.Contains(nameof(ClipboardViewModel.SelectedIndex), _announced);
    }

    // A bound list that is told the selection has gone to the top and then back may scroll to
    // the top and back. The rows are made again with the selection already where it is to be.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    public void Removing_a_row_announces_the_selection_and_the_preview_once_and_only_where_it_ends_up(int selected, int expectedIndex)
    {
        Open(TextClip("a", "walrus"), TextClip("b", "otter"), TextClip("c", "seal"), TextClip("d", "puffin"));
        _view.MoveSelection(selected);
        var indexes = new List<int>();
        _view.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(ClipboardViewModel.SelectedIndex))
            {
                indexes.Add(_view.SelectedIndex);
            }
        };
        _announced.Clear();

        _view.Remove();

        Assert.Equal([expectedIndex], indexes);
        Assert.Equal(1, _announced.Count(name => name == nameof(ClipboardViewModel.SelectedIndex)));
        Assert.Equal(1, _announced.Count(name => name == nameof(ClipboardViewModel.PreviewKind)));
        Assert.Equal(1, _announced.Count(name => name == nameof(ClipboardViewModel.PreviewText)));
        Assert.Equal(1, _announced.Count(name => name == nameof(ClipboardViewModel.PreviewImage)));
        Assert.Equal(1, _announced.Count(name => name == nameof(ClipboardViewModel.PreviewNote)));
    }

    [Fact]
    public void Removing_the_only_row_announces_the_selection_and_the_preview_once()
    {
        Open(TextClip("a", "walrus"));
        _announced.Clear();

        _view.Remove();

        Assert.Equal(1, _announced.Count(name => name == nameof(ClipboardViewModel.SelectedIndex)));
        Assert.Equal(1, _announced.Count(name => name == nameof(ClipboardViewModel.PreviewText)));
    }

    [Fact]
    public void Removing_the_last_row_selects_the_row_that_is_now_last()
    {
        Open(TextClip("a", "walrus"), TextClip("b", "otter"), TextClip("c", "seal"));
        _view.MoveSelection(2);

        _view.Remove();

        Assert.Equal(["a", "b"], Ids());
        Assert.Equal(1, _view.SelectedIndex);
        Assert.Equal("b", _view.Selected?.Id);
        Assert.Equal("otter", _view.PreviewText);
    }

    [Fact]
    public void Removing_the_only_row_leaves_nothing_selected_and_nothing_previewed()
    {
        Open(TextClip("a", "walrus"));

        Assert.NotNull(_view.Remove());

        Assert.Empty(_view.Rows);
        Assert.Equal(-1, _view.SelectedIndex);
        Assert.Null(_view.Selected);
        Assert.Equal(ClipPreviewKind.None, _view.PreviewKind);
        Assert.Equal("", _view.PreviewText);
        Assert.Equal(ClipboardViewModel.EmptyStatus, _view.Status);
    }

    [Fact]
    public void Removing_with_nothing_selected_returns_null_and_takes_no_row_out()
    {
        Open(TextClip("a", "walrus"), TextClip("b", "otter"));
        _view.SelectedIndex = -1;

        Assert.Null(_view.Remove());

        Assert.Equal(["a", "b"], Ids());
    }

    [Fact]
    public void Removing_keeps_the_filter()
    {
        Open(TextClip("a", "walrus one"), TextClip("b", "otter"), TextClip("c", "walrus two"), TextClip("d", "walrus three"));
        _view.Filter = "walrus";

        _view.Remove();

        Assert.Equal(["c", "d"], Ids());
        Assert.Equal("c", _view.Selected?.Id);
    }

    [Fact]
    public void A_removed_row_does_not_come_back_on_a_refresh_while_the_history_still_holds_its_entry()
    {
        Open(TextClip("a", "walrus"), TextClip("b", "otter"), TextClip("c", "seal"));
        _view.MoveSelection(1);
        _view.Remove();

        _view.Refresh();
        _view.Filter = "o";
        _view.Filter = "";

        Assert.Equal(["a", "c"], Ids());
    }

    [Fact]
    public void Two_rows_removed_one_after_the_other_both_stay_out()
    {
        Open(TextClip("a", "walrus"), TextClip("b", "otter"), TextClip("c", "seal"));

        _view.Remove();
        _view.Remove();
        _view.Refresh();

        Assert.Equal(["c"], Ids());
    }

    // The background task has done the deleting, and later an entry with the same id is in the
    // history again, as when the other machine's copy of it arrives. That one is not held back.
    [Fact]
    public void An_entry_with_the_id_of_a_removed_one_shows_once_the_history_has_let_go_of_the_removed_one()
    {
        var otter = TextClip("b", "otter");
        Open(TextClip("a", "walrus"), otter, TextClip("c", "seal"));
        _view.MoveSelection(1);
        _view.Remove();

        _history.Remove(otter);
        _view.Refresh();
        Assert.Equal(["a", "c"], Ids());

        _history.Insert(1, TextClip("b", "otter again"));
        _view.Refresh();

        Assert.Equal(["a", "b", "c"], Ids());
    }

    [Fact]
    public void Closing_returns_true_and_lets_go_of_the_rows_and_the_preview()
    {
        Open(Clip("a") with { Image = "walrus.png" }, TextClip("b", "otter"));
        Assert.NotNull(_view.PreviewImage);

        Assert.True(_view.Close());

        Assert.False(_view.IsOpen);
        Assert.Equal("", _view.Title);
        Assert.Equal("", _view.Prompt);
        Assert.Empty(_view.Rows);
        Assert.Equal(-1, _view.SelectedIndex);
        Assert.Null(_view.Selected);
        Assert.Equal(ClipPreviewKind.None, _view.PreviewKind);
        Assert.Equal("", _view.PreviewText);
        Assert.Null(_view.PreviewImage);
        Assert.Equal("", _view.PreviewNote);
    }

    [Fact]
    public void Closing_lets_go_of_the_status()
    {
        _view.Open();
        Assert.Equal(ClipboardViewModel.EmptyStatus, _view.Status);

        _view.Close();

        Assert.Equal("", _view.Status);
    }

    [Fact]
    public void Closing_announces_that_it_is_closed_and_everything_it_let_go_of()
    {
        Open(TextClip("a", "walrus"));
        _announced.Clear();

        _view.Close();

        Assert.Contains(nameof(ClipboardViewModel.IsOpen), _announced);
        Assert.Contains(nameof(ClipboardViewModel.Title), _announced);
        Assert.Contains(nameof(ClipboardViewModel.Prompt), _announced);
        Assert.Contains(nameof(ClipboardViewModel.SelectedIndex), _announced);
        Assert.Contains(nameof(ClipboardViewModel.Status), _announced);
        Assert.Contains(nameof(ClipboardViewModel.PreviewKind), _announced);
        Assert.Contains(nameof(ClipboardViewModel.PreviewText), _announced);
        Assert.Contains(nameof(ClipboardViewModel.PreviewImage), _announced);
        Assert.Contains(nameof(ClipboardViewModel.PreviewNote), _announced);
    }

    [Fact]
    public void A_view_that_was_closed_can_be_closed_no_further()
    {
        Open(TextClip("a", "walrus"));
        _view.Close();

        Assert.False(_view.Close());
    }

    [Fact]
    public void Opening_after_closing_starts_with_an_empty_filter()
    {
        Open(TextClip("a", "walrus"), TextClip("b", "otter"));
        _view.Filter = "otter";
        _view.Close();
        Assert.Equal("", _view.Filter);

        _view.Open();

        Assert.Equal("", _view.Filter);
        Assert.Equal(["a", "b"], Ids());
        Assert.Equal(0, _view.SelectedIndex);
    }

    // The history still holds the entry: whatever was to delete it has not done so. Once the
    // view has been closed it is no longer held back.
    [Fact]
    public void Opening_after_closing_shows_a_row_that_was_removed_when_the_history_still_holds_its_entry()
    {
        Open(TextClip("a", "walrus"), TextClip("b", "otter"));
        _view.Remove();
        Assert.Equal(["b"], Ids());
        _view.Close();

        _view.Open();

        Assert.Equal(["a", "b"], Ids());
    }

    [Fact]
    public void A_view_made_without_actions_gets_the_ones_that_do_nothing()
    {
        var entry = TextClip("a", "walrus");

        Assert.Same(ClipboardActions.None, _view.Actions);
        _view.Actions.Paste(entry);
        _view.Actions.Delete(entry.Id);
        _view.Actions.Save(entry);
        _view.Actions.OpenLink("https://example.org/walrus");
    }

    [Fact]
    public void A_view_holds_the_actions_it_was_given()
    {
        var actions = new ClipboardActions(_ => { }, _ => { }, _ => { }, _ => { });

        var view = new ClipboardViewModel(() => _history, _clock, actions, Ahead);

        Assert.Same(actions, view.Actions);
    }
}
