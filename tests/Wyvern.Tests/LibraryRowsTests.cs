using Wyvern.Core.Library;
using Wyvern.Core.Search;

namespace Wyvern.Tests;

public class LibraryRowsTests
{
    private int _made;

    private LibraryRows<string> NewRows() => new(name =>
    {
        _made++;
        return new SearchItem("test:" + name, name, "", ItemKind.Command, () => { });
    });

    [Fact]
    public void There_is_one_row_for_each_item_in_the_same_order()
    {
        var rows = NewRows().For(["one", "two", "three"]);

        Assert.Equal(["one", "two", "three"], rows.Select(row => row.Title));
    }

    [Fact]
    public void Rows_are_made_once_for_a_list_however_often_it_is_asked_for()
    {
        var rows = NewRows();
        IReadOnlyList<string> items = ["one", "two"];

        var first = rows.For(items);
        var second = rows.For(items);

        Assert.Same(first, second);
        Assert.Same(first[0], second[0]);
        Assert.Equal(2, _made);
    }

    [Fact]
    public void A_different_list_gets_rows_of_its_own_even_when_it_holds_the_same_items()
    {
        var rows = NewRows();
        IReadOnlyList<string> before = ["one", "two"];
        IReadOnlyList<string> after = ["one", "two"];

        var first = rows.For(before);
        var second = rows.For(after);

        Assert.NotSame(first, second);
        Assert.NotSame(first[0], second[0]);
        Assert.Equal(4, _made);
    }

    [Fact]
    public void Going_back_to_an_earlier_list_makes_its_rows_again()
    {
        var rows = NewRows();
        IReadOnlyList<string> before = ["one"];
        IReadOnlyList<string> after = ["two"];

        rows.For(before);
        rows.For(after);

        Assert.Equal(["one"], rows.For(before).Select(row => row.Title));
        Assert.Equal(3, _made);
    }

    [Fact]
    public void An_empty_list_has_no_rows()
    {
        Assert.Empty(NewRows().For([]));
        Assert.Empty(NewRows().For(new List<string>()));
        Assert.Equal(0, _made);
    }
}
