using Wyvern.Core.Launcher;

namespace Wyvern.Tests;

public class OutputViewModelTests
{
    private readonly OutputViewModel _output = new();
    private readonly List<string?> _announced = [];

    public OutputViewModelTests()
    {
        _output.PropertyChanged += (_, e) => _announced.Add(e.PropertyName);
    }

    [Fact]
    public void It_starts_closed_and_empty()
    {
        Assert.False(_output.IsOpen);
        Assert.Equal("", _output.Title);
        Assert.Equal("", _output.Status);
        Assert.Equal("", _output.Text);
    }

    [Fact]
    public void Opening_shows_the_title_and_running_and_no_text_yet()
    {
        _output.Open("Restart Docker");

        Assert.True(_output.IsOpen);
        Assert.Equal("Restart Docker", _output.Title);
        Assert.Equal("Running", _output.Status);
        Assert.Equal("", _output.Text);
        Assert.Equal(["IsOpen", "Title", "Status", "Text"], _announced);
    }

    [Fact]
    public void Completing_the_run_on_show_replaces_running_with_how_it_ended_and_shows_what_it_printed()
    {
        object ticket = _output.Open("Restart Docker");
        _announced.Clear();

        bool shown = _output.Complete(ticket, "Exit code 3", "stopping\r\nfailed");

        Assert.True(shown);
        Assert.True(_output.IsOpen);
        Assert.Equal("Restart Docker", _output.Title);
        Assert.Equal("Exit code 3", _output.Status);
        Assert.Equal("stopping\r\nfailed", _output.Text);
        Assert.Contains("Status", _announced);
        Assert.Contains("Text", _announced);
    }

    [Fact]
    public void Closing_lets_go_of_everything_that_was_shown()
    {
        object ticket = _output.Open("Restart Docker");
        _output.Complete(ticket, "Finished", "private output");
        _announced.Clear();

        Assert.True(_output.Close());

        Assert.False(_output.IsOpen);
        Assert.Equal("", _output.Title);
        Assert.Equal("", _output.Status);
        Assert.Equal("", _output.Text);
        Assert.Equal(["IsOpen", "Title", "Status", "Text"], _announced);
    }

    [Fact]
    public void Closing_a_view_that_is_not_open_does_nothing()
    {
        Assert.False(_output.Close());

        Assert.Empty(_announced);
    }

    [Fact]
    public void A_run_that_ends_after_the_view_was_closed_is_not_shown()
    {
        object ticket = _output.Open("Restart Docker");
        _output.Close();
        _announced.Clear();

        bool shown = _output.Complete(ticket, "Finished", "too late");

        Assert.False(shown);
        Assert.False(_output.IsOpen);
        Assert.Equal("", _output.Text);
        Assert.Empty(_announced);
    }

    [Fact]
    public void A_run_that_ends_after_the_view_was_opened_for_another_does_not_overwrite_the_other()
    {
        object first = _output.Open("First");
        object second = _output.Open("Second");

        Assert.False(_output.Complete(first, "Finished", "from the first"));

        Assert.Equal("Second", _output.Title);
        Assert.Equal("Running", _output.Status);
        Assert.Equal("", _output.Text);

        Assert.True(_output.Complete(second, "Finished", "from the second"));
        Assert.Equal("from the second", _output.Text);
    }

    [Fact]
    public void Opening_for_another_run_lets_go_of_what_the_run_before_printed()
    {
        object first = _output.Open("First");
        _output.Complete(first, "Exit code 2", "from the first");

        _output.Open("Second");

        Assert.Equal("Second", _output.Title);
        Assert.Equal("Running", _output.Status);
        Assert.Equal("", _output.Text);
    }

    [Fact]
    public void A_run_that_ends_after_the_view_was_closed_and_opened_again_for_another_is_not_shown_either()
    {
        object first = _output.Open("First");
        _output.Close();
        _output.Open("Second");

        Assert.False(_output.Complete(first, "Finished", "from the first"));

        Assert.Equal("Running", _output.Status);
    }

    [Fact]
    public void Something_that_is_not_a_ticket_completes_nothing()
    {
        _output.Open("First");

        Assert.False(_output.Complete(new object(), "Finished", "from nowhere"));

        Assert.Equal("Running", _output.Status);
    }

    // A closed view holds no ticket, and a missing ticket is no ticket: the two are both null,
    // and must not be taken to match.
    [Fact]
    public void Nothing_completes_a_view_that_is_closed_not_even_a_missing_ticket()
    {
        Assert.False(_output.Complete(null!, "Finished", "from nowhere"));
        Assert.False(_output.Complete(new object(), "Finished", "from nowhere"));

        Assert.False(_output.IsOpen);
        Assert.Equal("", _output.Status);
        Assert.Equal("", _output.Text);
        Assert.Empty(_announced);
    }

    [Fact]
    public void A_missing_ticket_does_not_complete_a_view_that_is_open_either()
    {
        _output.Open("First");

        Assert.False(_output.Complete(null!, "Finished", "from nowhere"));

        Assert.Equal("Running", _output.Status);
    }

    // Open tells whoever is listening that the view is open, and a listener may close it, or open
    // it for something else, before Open has returned. The ticket is the one made for this run.
    [Fact]
    public void Open_returns_the_ticket_it_made_even_when_a_listener_closes_the_view_before_it_returns()
    {
        bool closed = false;
        _output.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "IsOpen" && _output.IsOpen && !closed)
            {
                closed = true;
                _output.Close();
            }
        };

        object ticket = _output.Open("First");

        Assert.NotNull(ticket);
        Assert.False(_output.IsOpen);
        Assert.False(_output.Complete(ticket, "Finished", "too late"));
    }

    [Fact]
    public void Open_returns_the_ticket_it_made_even_when_a_listener_opens_the_view_for_another_run_before_it_returns()
    {
        object? second = null;
        bool openedAnother = false;
        _output.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "IsOpen" && !openedAnother)
            {
                openedAnother = true;
                second = _output.Open("Second");
            }
        };

        object first = _output.Open("First");

        Assert.NotSame(first, second);
        Assert.False(_output.Complete(first, "Finished", "from the first"));
        Assert.Equal("Running", _output.Status);
        Assert.True(_output.Complete(second!, "Finished", "from the second"));
        Assert.Equal("from the second", _output.Text);
    }
}
