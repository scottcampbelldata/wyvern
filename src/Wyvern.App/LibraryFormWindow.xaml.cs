using System.Windows;
using System.Windows.Input;
using Wyvern.Core.Library;

namespace Wyvern.App;

/// <summary>
/// The form that makes or changes a snippet or a quicklink: a few boxes, Save and Cancel. What is typed
/// is the owner's and goes into the library file and nowhere else: nothing of it is logged.
/// Save hands the boxes to whoever opened the form, which writes the file and answers how it
/// went; what could not be saved is said at the foot and the form stays open.
/// </summary>
public partial class LibraryFormWindow : Window
{
    private readonly Func<LibraryFormWindow, LibrarySave> _save;

    private LibraryFormWindow(string heading, bool snippet, Func<LibraryFormWindow, LibrarySave> save)
    {
        InitializeComponent();
        LauncherLook.Apply(this, LauncherLook.IsLight());
        _save = save;
        Heading.Text = heading;

        // The rows of the other kind are taken away.
        var whenSnippet = snippet ? Visibility.Visible : Visibility.Collapsed;
        var whenQuicklink = snippet ? Visibility.Collapsed : Visibility.Visible;
        TextLabel.Visibility = whenSnippet;
        BodyBox.Visibility = whenSnippet;
        TextHint.Visibility = whenSnippet;
        LinkLabel.Visibility = whenQuicklink;
        LinkBox.Visibility = whenQuicklink;
        LinkHint.Visibility = whenQuicklink;
        if (snippet)
        {
            KeywordHint.Text = "Optional. One word that brings it to the top in the launcher, and that auto-expand replaces with the snippet when it is switched on.";
        }

        Loaded += (_, _) =>
        {
            // The first box that is still empty is where typing starts.
            (NameBox.Text.Length == 0 ? NameBox : snippet ? BodyBox : LinkBox).Focus();
        };
    }

    /// <summary>What is in the Name box.</summary>
    public string NameText => NameBox.Text;

    /// <summary>What is in the Keyword box.</summary>
    public string KeywordText => KeywordBox.Text;

    /// <summary>What is in the Text box of a snippet.</summary>
    public string BodyText => BodyBox.Text;

    /// <summary>What is in the Link box of a quicklink.</summary>
    public string LinkText => LinkBox.Text;

    /// <summary>The form for a snippet.</summary>
    /// <param name="text">What the Text box starts with: the text of a clipboard entry that is being saved as a snippet. Empty for a snippet from nothing.</param>
    /// <param name="save">Writes the snippet from what the boxes hold and answers how it went.</param>
    public static LibraryFormWindow ForSnippet(string text, Func<LibraryFormWindow, LibrarySave> save)
    {
        var form = new LibraryFormWindow("Create Snippet", snippet: true, save);
        form.BodyBox.Text = text;
        return form;
    }

    /// <summary>The form for a quicklink.</summary>
    /// <param name="save">Adds the quicklink from what the boxes hold and answers how it went.</param>
    public static LibraryFormWindow ForQuicklink(Func<LibraryFormWindow, LibrarySave> save) =>
        new("Create Quicklink", snippet: false, save);

    /// <summary>The form with a snippet that is there already in it, to be changed.</summary>
    /// <param name="save">Writes the snippet anew from what the boxes hold and answers how it went.</param>
    public static LibraryFormWindow ForSnippetEdit(Snippet snippet, Func<LibraryFormWindow, LibrarySave> save)
    {
        var form = new LibraryFormWindow("Edit Snippet", snippet: true, save);
        form.NameBox.Text = snippet.Name;
        form.KeywordBox.Text = snippet.Keyword ?? "";
        form.BodyBox.Text = snippet.Body;
        form.BodyBox.CaretIndex = form.BodyBox.Text.Length;
        return form;
    }

    /// <summary>The form with a quicklink that is there already in it, to be changed.</summary>
    /// <param name="save">Writes the quicklink anew from what the boxes hold and answers how it went.</param>
    public static LibraryFormWindow ForQuicklinkEdit(Quicklink quicklink, Func<LibraryFormWindow, LibrarySave> save)
    {
        var form = new LibraryFormWindow("Edit Quicklink", snippet: false, save);
        form.NameBox.Text = quicklink.Name;
        form.KeywordBox.Text = quicklink.Keyword ?? "";
        form.LinkBox.Text = quicklink.Link;
        form.LinkBox.CaretIndex = form.LinkBox.Text.Length;
        return form;
    }

    // Ctrl+Enter saves from any box. (Enter alone is a new line in the text of a snippet.)
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            Save();
        }
    }

    private void OnSave(object sender, RoutedEventArgs e) => Save();

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void Save()
    {
        LibrarySave saved;
        try
        {
            saved = _save(this);
        }
        catch (Exception)
        {
            // Whatever went wrong is not the form's to explain: nothing was saved.
            saved = LibrarySave.Refused(LibraryWriter.NotWritten);
        }

        if (saved.Saved)
        {
            Close();
        }
        else
        {
            Status.Text = saved.Note;
        }
    }
}
