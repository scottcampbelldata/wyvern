using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Beacon.Core.ClipboardHistory;
using Beacon.Core.Launcher;
using Beacon.Core.Search;
using Screen = System.Windows.Forms.Screen;

namespace Beacon.App;

public partial class LauncherWindow : Window
{
    // The place a picture has in the clipboard preview, in the units the window is laid out
    // in. Used until the preview has been laid out: the history can be opened by its hotkey
    // before the window has ever been drawn.
    private const double PictureBoxWidth = 432;
    private const double PictureBoxHeight = 178;

    // How many rows Page Up and Page Down move by when the list cannot tell how many it shows.
    private const int DefaultPageRows = 8;

    // How long a row has to stay selected before its picture is read. A key that is held down
    // passes many rows a second, and a full read for each of them would be work for pictures
    // nobody sees; a row that is stopped at shows its picture this much later.
    private static readonly TimeSpan PictureWait = TimeSpan.FromMilliseconds(60);

    private readonly LauncherViewModel _viewModel;
    private readonly ClipPictureLoader _pictures;

    // True when the window stands on blurred glass that Windows draws. Decided once, before
    // the window is first shown.
    private bool _glass;

    // The program whose window was in front as the launcher opened, by its own name: where a
    // paste goes. Empty when it is not known.
    private string _pasteTo = "";

    // The picture in the clipboard preview. The path and the flag are used on the UI thread
    // only. The ticket is written on the UI thread only, and read once more by the background
    // work before it starts reading, which is why it is volatile.
    // The path of the picture that is shown or being read, or null when there is none.
    private string? _picturePath;

    // Goes up each time another picture is wanted, or none. A picture that comes back from
    // being read is shown only if the ticket it was read for is still this one.
    private volatile int _pictureTicket;

    // True from when the view model first announces its picture until the window has looked.
    private bool _pictureLookWaits;

    /// <param name="pictures">Reads the pictures of the clipboard history for the preview.</param>
    public LauncherWindow(LauncherViewModel viewModel, ClipPictureLoader pictures)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _pictures = pictures;

        // The rows show an item's alias after its name, asked of the view model.
        ItemAliasConverter.AliasOf = viewModel.AliasOf;
        DataContext = viewModel;
        LauncherLook.Apply(this, LauncherLook.IsLight(), _glass);

        // The small pictures in front of the clipboard history's rows are read with the same
        // reader as the preview. The rows of both lists stand under headings: the launcher's
        // under the ones the view model gives them, the history's under the day of each entry.
        Thumb.Use(pictures);
        TypeFilter.ItemsSource = LauncherLook.TypeChoices;
        ToneFilter.ItemsSource = LauncherLook.ToneChoices;
        CollectionViewSource.GetDefaultView(viewModel.Results).GroupDescriptions.Add(new SectionGrouping(viewModel));
        CollectionViewSource.GetDefaultView(viewModel.Clipboard.Rows).GroupDescriptions.Add(new PropertyGroupDescription(nameof(ClipRow.Group)));

        // What stands round the lists (the mark or the way back in front of the input, the
        // words at the foot) follows whatever changes: the mode, the selection, the results.
        viewModel.PropertyChanged += (_, _) => UpdateChrome();
        viewModel.Output.PropertyChanged += (_, _) => UpdateChrome();
        viewModel.Clipboard.PropertyChanged += (_, _) => UpdateChrome();
        viewModel.Results.CollectionChanged += (_, _) => UpdateChrome();
        viewModel.Emoji.PropertyChanged += (_, changed) =>
        {
            UpdateChrome();
            if (changed.PropertyName == nameof(EmojiViewModel.Selected))
            {
                ShowSelectedEmoji();
            }
        };
        viewModel.Clipboard.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(ClipboardViewModel.Info))
            {
                ShowInfo();
            }
        };
        UpdateChrome();

        // The view model raises Dismissed just before it runs an action, so the launched
        // app receives focus instead of fighting this window for it. It is not raised for an
        // item that keeps the launcher open, such as a script in output mode: the window stays.
        viewModel.Dismissed += (_, _) => Hide();
        Deactivated += (_, _) => HideLauncher();

        // The clipboard view names the picture of the selected entry by its path, and the
        // window reads the file. The view is one object for as long as the window lives.
        viewModel.Clipboard.PropertyChanged += OnClipboardChanged;
    }

    /// <summary>
    /// Raised when the launcher is asked to show, before it takes the focus. Whatever window
    /// is in front at that moment is the one the owner was working in.
    /// </summary>
    public event EventHandler? Opening;

    public void Toggle()
    {
        if (IsVisible)
        {
            HideLauncher();
        }
        else
        {
            ShowLauncher();
        }
    }

    /// <summary>
    /// What the clipboard hotkey does: shows the launcher with the clipboard history open, or
    /// hides it when that is what it is showing already. A launcher that is open on something
    /// else turns to the history and stays open.
    /// </summary>
    public void ToggleClipboard()
    {
        if (IsVisible && _viewModel.Clipboard.IsOpen)
        {
            HideLauncher();
            return;
        }

        if (!IsVisible)
        {
            // Raises Opening first, so the window to paste into is remembered as usual.
            ShowLauncher();
        }

        _viewModel.OpenClipboard();
        QueryBox.Focus();
    }

    public void ShowLauncher()
    {
        Opening?.Invoke(this, EventArgs.Empty);
        NameFront();
        Prepare();
        MoveToActiveMonitor();
        Show();
        Activate();
        QueryBox.Focus();
    }

    /// <summary>
    /// Makes the window ready to be shown: light or dark as Windows is set now, and the list
    /// made afresh, so that the launcher opens on the suggestions, the commands and the apps
    /// with the first row selected.
    /// </summary>
    public void Prepare()
    {
        bool light = LauncherLook.IsLight();
        LauncherLook.Apply(this, light, _glass);
        ShowGlassAs(light);
        _viewModel.Refresh();
        if (!_viewModel.InArgumentMode && !_viewModel.Output.IsOpen && !_viewModel.Clipboard.IsOpen && _viewModel.Results.Count > 0)
        {
            _viewModel.SelectedIndex = 0;
            ScrollToTop(ResultList);
        }
    }

    /// <summary>
    /// Puts the launcher on blurred glass: Windows draws the blur behind it, rounds its corners
    /// and gives it its shadow, and the window's own background becomes a thin tint. Only on
    /// Windows 11, version 22H2 or later; on anything older nothing changes. Call it once,
    /// before the window is first shown: what kind of window it is cannot change afterwards.
    /// </summary>
    public void UseGlass(bool glass)
    {
        if (!glass || _glass || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) || new WindowInteropHelper(this).Handle != IntPtr.Zero)
        {
            return;
        }

        _glass = true;

        // An ordinary window whose frame Windows draws, with no title bar: the content takes
        // the whole of it. A window that draws itself through (AllowsTransparency) gets no glass.
        AllowsTransparency = false;
        WindowStyle = WindowStyle.SingleBorderWindow;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        Width = 750;
        Outer.Margin = new Thickness(0);
        Shade.Visibility = Visibility.Collapsed;
        Body.CornerRadius = new CornerRadius(0);
        Body.BorderThickness = new Thickness(0);
        LauncherLook.Apply(this, LauncherLook.IsLight(), glass: true);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (!_glass)
        {
            return;
        }

        // The window exists now. Windows is asked for the blur, the round corners and no
        // opening animation. If it will not give the blur, the window gets its solid
        // background back; it is then an ordinary window with square corners.
        bool given = false;
        try
        {
            IntPtr window = new WindowInteropHelper(this).Handle;
            if (HwndSource.FromHwnd(window) is { CompositionTarget: { } target })
            {
                target.BackgroundColor = Colors.Transparent;
            }

            // Windows draws a close button on the frame of a window that has a system menu.
            // The launcher has neither: it is closed by Escape, or by going somewhere else.
            int style = GetWindowLong(window, StyleIndex);
            if (style != 0)
            {
                _ = SetWindowLong(window, StyleIndex, style & ~SystemMenu);
                _ = SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, NoMove | NoSize | NoZOrder | NoActivate | FrameChanged);
            }

            int round = RoundCorners;
            _ = DwmSetWindowAttribute(window, CornerPreference, ref round, sizeof(int));
            int still = 1;
            _ = DwmSetWindowAttribute(window, TransitionsOff, ref still, sizeof(int));
            int acrylic = AcrylicBackdrop;
            given = DwmSetWindowAttribute(window, BackdropType, ref acrylic, sizeof(int)) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            given = false;
        }

        if (!given)
        {
            _glass = false;
            LauncherLook.Apply(this, LauncherLook.IsLight());
        }
        else
        {
            ShowGlassAs(LauncherLook.IsLight());
        }
    }

    // The glass is light or dark as the window's colours are.
    private void ShowGlassAs(bool light)
    {
        IntPtr window = new WindowInteropHelper(this).Handle;
        if (!_glass || window == IntPtr.Zero)
        {
            return;
        }

        try
        {
            int dark = light ? 0 : 1;
            _ = DwmSetWindowAttribute(window, DarkMode, ref dark, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // The glass stays as it was.
        }
    }

    // The program whose window is in front as the launcher opens is where a paste goes. Its
    // own name is found away from this thread and shown in the foot when it has come: "Paste
    // to" and the name. Until then, and when it cannot be found, the foot says "Paste".
    private async void NameFront()
    {
        try
        {
            _pasteTo = "";
            IntPtr front = GetForegroundWindow();
            if (front == IntPtr.Zero || GetWindowThreadProcessId(front, out uint id) == 0 || id == (uint)Environment.ProcessId)
            {
                return;
            }

            string process;
            using (var found = System.Diagnostics.Process.GetProcessById((int)id))
            {
                process = found.ProcessName;
            }

            if (await SourcePrograms.Find(process) is { } program)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _pasteTo = program.Name;
                    UpdateChrome();
                });
            }
        }
        catch (Exception)
        {
            // The foot says "Paste".
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 must hide the launcher, not destroy it: this one window is reused for as long
        // as the app runs. Quitting still works, because application shutdown ignores the cancel.
        e.Cancel = true;
        HideLauncher();
        base.OnClosing(e);
    }

    private void HideLauncher()
    {
        ActionsPopup.IsOpen = false;
        if (IsVisible)
        {
            Hide();

            // Whatever was half done is given up: the launcher always opens in search mode, empty.
            // An output view that was open is closed too; the script it showed runs on. So is
            // the clipboard history, and what it showed is let go of.
            _viewModel.Reset();
        }
    }

    private void MoveToActiveMonitor()
    {
        // WorkingArea is in physical pixels; WPF positions windows in device-independent units.
        var area = Screen.FromHandle(GetForegroundWindow()).WorkingArea;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Left = area.Left / scale + (area.Width / scale - Width) / 2;
        Top = area.Top / scale + area.Height / scale * 0.2;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ActionsPopup.IsOpen)
        {
            OnKeyDownInActions(e);
            return;
        }

        if (TypeFilter.IsDropDownOpen)
        {
            OnKeyDownInChoice(TypeFilter, e);
            return;
        }

        if (ToneFilter.IsDropDownOpen)
        {
            OnKeyDownInChoice(ToneFilter, e);
            return;
        }

        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control && !_viewModel.Output.IsOpen)
        {
            // Everything that can be done with what is selected.
            OpenActions();
            e.Handled = true;
            return;
        }

        if (_viewModel.Output.IsOpen)
        {
            OnKeyDownInOutputMode(e);
            return;
        }

        if (_viewModel.Clipboard.IsOpen)
        {
            OnKeyDownInClipboardMode(e);
            return;
        }

        if (_viewModel.Emoji.IsOpen)
        {
            OnKeyDownInEmojiMode(e);
            return;
        }

        switch (e.Key)
        {
            case Key.Down when Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                // A favorite, one place down its list.
                MoveFavorite(1);
                break;
            case Key.Up when Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                MoveFavorite(-1);
                break;
            case Key.F when Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                if (!e.IsRepeat)
                {
                    // A favorite, or one no longer. The window stays open.
                    ToggleFavorite();
                }

                break;
            case Key.A when Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                if (!e.IsRepeat)
                {
                    _viewModel.AskAliasForSelected();
                }

                break;
            case Key.Down:
                MoveSelection(1);
                break;
            case Key.Up:
                MoveSelection(-1);
                break;
            case Key.Enter:
                if (e.IsRepeat)
                {
                    // Enter is being held down. Swallowed, so that on a quicklink that asks for
                    // text the first press enters argument mode and the repeat does not run it
                    // at once with nothing typed: it takes a fresh press to run.
                    break;
                }

                _viewModel.RunSelected(secondary: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                break;
            case Key.Tab:
                // Always taken, even when the selected item has no argument, so that Tab can
                // never move the focus out of the input line.
                _viewModel.EnterArgumentMode();
                break;
            case Key.Back:
                if (e.IsRepeat && _viewModel.InArgumentMode && _viewModel.Query.Length == 0)
                {
                    // Backspace is being held down and the argument has run out. Swallowed, so
                    // that holding the key to clear the argument stops there: it takes a fresh
                    // press to leave argument mode, and the search text put back is not eaten.
                    break;
                }

                if (!_viewModel.Backspace())
                {
                    // An ordinary Backspace: the input line deletes a character.
                    return;
                }

                MoveCaretToEnd();
                break;
            case Key.Escape:
                if (e.IsRepeat)
                {
                    // Esc is being held down. Swallowed, so that the press which closed the
                    // output view or gave up an argument does not go on to hide the launcher
                    // as well: each step back takes a fresh press.
                    break;
                }

                if (_viewModel.LeaveArgumentMode())
                {
                    MoveCaretToEnd();
                }
                else if (!_viewModel.CloseScope())
                {
                    // Nothing to step back from: a list of its own, such as the open
                    // windows, would have been closed first.
                    HideLauncher();
                }

                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // While the emoji picker is shown, typing filters it. The four arrows move through the
    // grid, Enter pastes the selected emoji, Ctrl+Shift+C copies it without pasting, and Esc
    // goes back to the search.
    private void OnKeyDownInEmojiMode(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Right:
                _viewModel.Emoji.Move(1, 0);
                break;
            case Key.Left:
                _viewModel.Emoji.Move(-1, 0);
                break;
            case Key.Down:
                _viewModel.Emoji.Move(0, 1);
                break;
            case Key.Up:
                _viewModel.Emoji.Move(0, -1);
                break;
            case Key.PageDown:
                _viewModel.Emoji.Move(0, EmojiPageLines);
                break;
            case Key.PageUp:
                _viewModel.Emoji.Move(0, -EmojiPageLines);
                break;
            case Key.Enter:
                if (!e.IsRepeat)
                {
                    // A held Enter pastes once, as in the clipboard history.
                    _viewModel.RunSelected();
                }

                break;
            case Key.C when Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                if (!e.IsRepeat)
                {
                    _viewModel.CopySelectedEmoji();
                }

                break;
            case Key.P when Keyboard.Modifiers == ModifierKeys.Control:
                // The choice of skin tone.
                ToneFilter.IsDropDownOpen = true;
                break;
            case Key.Tab:
                // Taken and ignored, so that Tab can never move the focus out of the input line.
                break;
            case Key.Escape:
                if (!e.IsRepeat)
                {
                    _viewModel.CloseEmoji();
                    QueryBox.Focus();
                }

                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // How many lines of the emoji grid Page Up and Page Down move by.
    private const int EmojiPageLines = 4;

    // The line the selected emoji stands in is brought into view, and with the first line
    // under a heading the heading as well.
    private void ShowSelectedEmoji()
    {
        var rows = _viewModel.Emoji.Rows;
        int row = _viewModel.Emoji.SelectedRow;
        if (row < 0 || row >= rows.Count)
        {
            return;
        }

        if (row > 0 && rows[row - 1].Heading is not null)
        {
            EmojiList.ScrollIntoView(rows[row - 1]);
        }

        EmojiList.ScrollIntoView(rows[row]);
    }

    // The emoji under the pointer becomes the selected one as soon as the pointer moves, as a
    // row of the lists does, and a click pastes it.
    private void OnEmojiMouseMove(object sender, MouseEventArgs e)
    {
        Point at = e.GetPosition(this);
        if (at == _pointerAt)
        {
            return;
        }

        bool first = double.IsNaN(_pointerAt.X);
        _pointerAt = at;
        if (!first && sender is FrameworkElement { DataContext: EmojiCell cell })
        {
            _viewModel.Emoji.Select(cell);
        }
    }

    private void OnEmojiClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: EmojiCell cell })
        {
            _viewModel.Emoji.Select(cell);
            _viewModel.RunSelected();
        }
    }

    // While a script's output is shown there is nothing to select, run or type. Esc goes back
    // to the search, and the keys that would move through the results move through the output
    // instead. Every other key is left alone, so that Ctrl+C still copies what was selected in
    // the output with the mouse.
    private void OnKeyDownInOutputMode(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (e.IsRepeat)
                {
                    // Esc is being held down. Swallowed, as in search mode, so that a held key does
                    // one step: the press that closes the output view is the only one that does.
                    break;
                }

                _viewModel.CloseOutput();

                // The focus may be in the output, if it was clicked. Typing belongs to the input.
                QueryBox.Focus();
                break;
            case Key.Down:
                OutputBox.LineDown();
                break;
            case Key.Up:
                OutputBox.LineUp();
                break;
            case Key.PageDown:
                OutputBox.PageDown();
                break;
            case Key.PageUp:
                OutputBox.PageUp();
                break;
            case Key.Home:
                OutputBox.ScrollToHome();
                break;
            case Key.End:
                OutputBox.ScrollToEnd();
                break;
            case Key.Enter:
            case Key.Tab:
            case Key.Back:
                // Taken and ignored: Enter must not run anything, Tab must not move the focus,
                // and there is nothing to delete.
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // While the clipboard history is shown, typing filters it, so every key that types is left
    // alone. The keys that would move through the results move through the history, Enter
    // pastes the selected entry (Ctrl+Enter opens it when it is a link), Ctrl+Delete deletes it,
    // Ctrl+S saves a copy of its pictures, and Esc goes back to the search.
    private void OnKeyDownInClipboardMode(KeyEventArgs e)
    {
        bool control = Keyboard.Modifiers == ModifierKeys.Control;
        switch (e.Key)
        {
            case Key.Down:
                _viewModel.MoveSelection(1);
                break;
            case Key.Up:
                _viewModel.MoveSelection(-1);
                break;
            case Key.PageDown:
                _viewModel.MoveSelection(ClipPageRows());
                break;
            case Key.PageUp:
                _viewModel.MoveSelection(-ClipPageRows());
                break;
            case Key.Enter:
                if (!e.IsRepeat)
                {
                    // A held Enter pastes once. The repeats are swallowed here; once the window
                    // has hidden they go to whatever was pasted into. With Ctrl and Shift it
                    // is the text alone that is pasted.
                    if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                    {
                        _viewModel.PasteSelectedClipAsText();
                    }
                    else
                    {
                        _viewModel.RunSelected(secondary: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                    }
                }

                break;
            case Key.Delete when control:
                if (!e.IsRepeat)
                {
                    // One entry for each press: holding the keys down must not empty the history.
                    _viewModel.DeleteSelectedClip();
                }

                break;
            case Key.C when Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                if (!e.IsRepeat)
                {
                    // On the clipboard again, without being pasted anywhere.
                    _viewModel.CopySelectedClip();
                }

                break;
            case Key.T when Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                if (!e.IsRepeat)
                {
                    // The text that was read in the picture, on the clipboard.
                    _viewModel.CopySelectedClipPictureText();
                }

                break;
            case Key.P when Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                if (!e.IsRepeat)
                {
                    // Pinned, or unpinned again. The window stays open.
                    _viewModel.TogglePinSelectedClip();
                }

                break;
            case Key.P when control:
                // The choice of which kinds of entries are listed.
                TypeFilter.IsDropDownOpen = true;
                break;
            case Key.S when Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                if (!e.IsRepeat)
                {
                    _viewModel.SaveSelectedClipAsSnippet();
                }

                break;
            case Key.S when control:
                if (!e.IsRepeat)
                {
                    _viewModel.SaveSelectedClip();
                }

                break;
            case Key.Tab:
                // Taken and ignored, so that Tab can never move the focus out of the input line.
                break;
            case Key.Escape:
                if (!e.IsRepeat)
                {
                    // Each step back takes a fresh press, as in the other modes.
                    _viewModel.CloseClipboard();
                    QueryBox.Focus();
                }

                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // How many rows a page is: as many as the list shows at once, which is its height over the
    // height of a row. Rows are not all one height (a link that has a label takes a line more),
    // so the tallest row the list has made decides: a page must not pass over a row that was
    // never in view. The list makes rows only for what is on show and a little to either side.
    private int ClipPageRows()
    {
        double tallest = 0;
        for (int i = 0; i < ClipList.Items.Count; i++)
        {
            if (ClipList.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement row && row.ActualHeight > tallest)
            {
                tallest = row.ActualHeight;
            }
        }

        double shown = ClipList.ActualHeight;
        if (!(tallest > 0 && shown > 0))
        {
            // Nothing has been laid out to measure.
            return DefaultPageRows;
        }

        double rows = Math.Floor(shown / tallest);
        return rows >= 1 ? (int)Math.Min(rows, ClipList.Items.Count) : 1;
    }

    // The selected row is kept in view however the selection came to change: a key, a click,
    // the filter being typed, a row deleted, the history refreshed or opened again.
    private void OnClipSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ClipList.SelectedItem is { } selected)
        {
            ClipList.ScrollIntoView(selected);
        }
    }

    // The picture of the selected entry is read here and not by a binding, so that it is read
    // away from the UI thread and at the size it is shown. A large screenshot takes long enough
    // to unpack that the window would be slow to appear and slow to follow the keys.
    //
    // The view model announces its picture again and again: with every key typed in the filter
    // and every move of the selection, whether or not the path has changed. And when it makes
    // its rows again the list drops its selection for a moment, the binding of the selection
    // writes that back, and so a picture of null is announced in between, followed by the same
    // path as before. So nothing is done as an announcement arrives. The first one asks the
    // dispatcher to come back here when what is going on has finished, which is before the
    // window is next drawn, and what counts is the path the view model holds then.
    //
    // A path that is already shown or being read is left alone. Another path, or none, takes a
    // new ticket and clears the picture; closing the view is a path of null. A picture that was
    // still being read is dropped when it comes back, because its ticket is no longer the newest.
    //
    // The file is not read at once. Holding Down or Up moves the selection once for every
    // repeat of the key, and each row passed would otherwise start a full read of a picture
    // that is gone from view before it has come. So the read waits a moment (PictureWait) and
    // is given up if another picture, or none, has been asked for meanwhile; and the background
    // work asks the same once more before it opens the file, because it can be a while before
    // a pool thread takes it up.
    //
    // The view model announces on the window's own thread. Should an announcement ever come on
    // another thread, nothing is done, because everything here belongs to the window's thread,
    // and it is said once in the log.
    //
    // An async void method has nobody above it, and an exception that left it would end Beacon.
    // So everything is inside the one try.
    private async void OnClipboardChanged(object? sender, PropertyChangedEventArgs e)
    {
        try
        {
            // DispatcherObject.CheckAccess: true when the calling thread is the one this window
            // belongs to. Any thread may ask it. It comes before anything else is read here.
            if (!CheckAccess())
            {
                _pictures.ReportWrongThread();
                return;
            }

            if (e.PropertyName != nameof(ClipboardViewModel.PreviewImage) || _pictureLookWaits)
            {
                return;
            }

            _pictureLookWaits = true;
            await Dispatcher.Yield(DispatcherPriority.Normal);
            _pictureLookWaits = false;

            string? path = _viewModel.Clipboard.PreviewImage;
            if (path == _picturePath)
            {
                return;
            }

            _picturePath = path;
            int ticket = ++_pictureTicket;
            ClipPreviewPicture.Source = null;
            if (path is null)
            {
                return;
            }

            // Comes back on this thread. A ticket that has moved on means another picture, or
            // none, was asked for while this one waited: its file is never opened.
            await Task.Delay(PictureWait);
            if (ticket != _pictureTicket)
            {
                return;
            }

            var (width, height) = PictureBoxInPixels();
            ClipPictureLoader pictures = _pictures;

            // On a pool thread. The ticket is asked about once more there, before the file is
            // opened: null is what a picture that is no longer wanted comes back as, and it is
            // dropped below like any other that comes back late.
            BitmapSource? picture = await Task.Run(() => ticket == _pictureTicket ? pictures.Load(path, width, height) : null);

            // Back on the UI thread. Null when there is no picture to show: the preview stays
            // empty, and the path is not read again until another has been selected.
            if (ticket == _pictureTicket)
            {
                ClipPreviewPicture.Source = picture;
            }
        }
        catch (Exception ex)
        {
            _pictureLookWaits = false;
            try
            {
                _pictures.ReportFailure(ex);
            }
            catch (Exception)
            {
                // The report failed as well. It must not leave this method either.
            }
        }
    }

    // How much room a picture has in the preview, in pixels of the screen the window is on. The
    // reader makes the picture with no more pixels than this: more could not be shown. The room
    // is the preview's place less the picture's margin. Until the preview has been laid out
    // there is nothing to measure, and the room is taken to be what the layout gives it with
    // one line of note underneath. The note can take a line more or less from one entry to the
    // next; the picture's own element makes up the difference, downwards only.
    private (int Width, int Height) PictureBoxInPixels()
    {
        Thickness margin = ClipPreviewPicture.Margin;
        double width = ClipPreviewBox.ActualWidth - margin.Left - margin.Right;
        double height = ClipPreviewBox.ActualHeight - margin.Top - margin.Bottom;
        if (!(width >= 1 && height >= 1))
        {
            width = PictureBoxWidth;
            height = PictureBoxHeight;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        return ((int)Math.Ceiling(width * dpi.DpiScaleX), (int)Math.Ceiling(height * dpi.DpiScaleY));
    }

    // A click selects the row, so that its preview shows. It does not paste: that takes Enter.
    private void OnClipClicked(object sender, MouseButtonEventArgs e)
    {
        if (RowOf(e.OriginalSource) is { } row)
        {
            ClipList.SelectedItem = row.DataContext;
        }
    }

    // Where the pointer was when a list last followed it.
    private Point _pointerAt = new(double.NaN, double.NaN);

    // The row under the pointer becomes the selected one, as soon as the pointer moves. A
    // list that scrolls under a pointer that is lying still raises the same event, and must
    // not take the selection away from the keys, so the pointer has to have moved.
    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        Point at = e.GetPosition(this);
        if (at == _pointerAt)
        {
            return;
        }

        bool first = double.IsNaN(_pointerAt.X);
        _pointerAt = at;
        if (!first && sender is ListBox list && RowOf(e.OriginalSource) is { } row && !ReferenceEquals(list.SelectedItem, row.DataContext))
        {
            list.SelectedItem = row.DataContext;
        }
    }

    // The row a click landed in, or null when it landed on a heading or between rows. The rows
    // stand inside groups, so the list is asked by walking up from what was clicked.
    private static ListBoxItem? RowOf(object? clicked)
    {
        var at = clicked as DependencyObject;
        while (at is not null and not ListBoxItem)
        {
            at = at is Visual ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at);
        }

        return at as ListBoxItem;
    }

    private static void ScrollToTop(DependencyObject list)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(list); i++)
        {
            var child = VisualTreeHelper.GetChild(list, i);
            if (child is ScrollViewer scroller)
            {
                scroller.ScrollToTop();
                return;
            }

            ScrollToTop(child);
        }
    }

    // One step back, as Esc: out of the output, out of the clipboard history, or out of an
    // argument. Typing belongs to the input line again afterwards.
    private void OnBackClicked(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Output.IsOpen)
        {
            _viewModel.CloseOutput();
        }
        else if (_viewModel.Clipboard.IsOpen)
        {
            _viewModel.CloseClipboard();
        }
        else if (_viewModel.Emoji.IsOpen)
        {
            _viewModel.CloseEmoji();
        }
        else if (_viewModel.LeaveArgumentMode())
        {
            MoveCaretToEnd();
        }
        else
        {
            _viewModel.CloseScope();
        }

        QueryBox.Focus();
    }

    // The choice of kinds was closed, by a click or a key: typing filters the list again.
    private void OnTypeFilterClosed(object? sender, EventArgs e) => QueryBox.Focus();

    // While the choice of kinds is open the arrows move through it, and Enter or Esc closes it.
    private static void OnKeyDownInChoice(ComboBox choice, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                choice.SelectedIndex = Math.Min(choice.SelectedIndex + 1, choice.Items.Count - 1);
                break;
            case Key.Up:
                choice.SelectedIndex = Math.Max(choice.SelectedIndex - 1, 0);
                break;
            case Key.Enter:
            case Key.Escape:
            case Key.Tab:
                choice.IsDropDownOpen = false;
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // What stands round the lists, made again from how things are: in front of the input the
    // mark while searching and the way back otherwise, the words that say what to type, the
    // line said when nothing was found, and at the foot what is open and what Enter does.
    private void UpdateChrome()
    {
        bool clipboard = _viewModel.Clipboard.IsOpen;
        bool output = _viewModel.Output.IsOpen;
        bool emoji = _viewModel.Emoji.IsOpen;
        bool scope = _viewModel.InScope;
        bool search = !clipboard && !output && !emoji && !scope && !_viewModel.InArgumentMode;

        SearchMark.Visibility = search ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = search ? Visibility.Collapsed : Visibility.Visible;
        SearchPrompt.Visibility = search && _viewModel.Query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultList.Visibility = clipboard || output || emoji ? Visibility.Collapsed : Visibility.Visible;
        NoResults.Visibility = (search || scope) && _viewModel.Query.Trim().Length > 0 && _viewModel.Results.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Under the preview: only what the list of actions does not say already. (Saving a
        // picture and opening a link are there, with their keys.)
        string note = _viewModel.Clipboard.PreviewNote;
        if (note == ClipboardViewModel.LinkNote)
        {
            note = "";
        }
        else if (note.EndsWith(ClipboardViewModel.PictureNote, StringComparison.Ordinal))
        {
            note = note[..^ClipboardViewModel.PictureNote.Length].Trim();
        }

        ClipNote.Text = note;
        ClipNote.Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        FootMark.Visibility = clipboard ? Visibility.Collapsed : Visibility.Visible;
        FootTile.Visibility = clipboard ? Visibility.Visible : Visibility.Collapsed;

        // At the foot: the name of the view that is open, and in the emoji picker the name
        // of the emoji that is selected.
        FootName.Text = clipboard ? ClipboardViewModel.Heading
            : emoji ? _viewModel.Emoji.Selected?.Name ?? EmojiViewModel.Heading
            : scope ? _viewModel.ScopeTitle
            : "";

        string action = "";
        if (clipboard)
        {
            if (_viewModel.Clipboard.Selected is not null)
            {
                action = PasteWords();
            }
        }
        else if (emoji)
        {
            if (_viewModel.Emoji.Selected is not null)
            {
                action = PasteWords();
            }
        }
        else if (_viewModel.InArgumentMode)
        {
            action = "Run";
        }
        else if (!output && _viewModel.SelectedIndex >= 0 && _viewModel.SelectedIndex < _viewModel.Results.Count)
        {
            SearchItem item = _viewModel.Results[_viewModel.SelectedIndex];
            action = LauncherLook.Action(item);
        }

        ActionText.Text = action;
        FootAction.Visibility = action.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Beside it, the way to everything else that can be done: Ctrl+K.
        SecondAction.Visibility = action.Length > 0 && !_viewModel.InArgumentMode ? Visibility.Visible : Visibility.Collapsed;
    }

    // What is said about the selected entry of the clipboard history: what the view model
    // says, and for a picture its size in pixels and on disk, read away from this thread and
    // put in after the kind when it has come, if the same entry is still the selected one.
    private async void ShowInfo()
    {
        try
        {
            var info = _viewModel.Clipboard.Info.Select(line => new InfoLine(line.Label, line.Value, null)).ToList();
            InfoTable.ItemsSource = info;
            _ = NameSource(info, _viewModel.Clipboard.Selected);
            // The picture of the selected row. (The view model announces its information
            // before it names the picture of the preview, so the row is asked.)
            int at = _viewModel.Clipboard.SelectedIndex;
            if (at < 0
                || at >= _viewModel.Clipboard.Rows.Count
                || _viewModel.Clipboard.Rows[at].Picture is not { } path
                || _viewModel.Clipboard.Selected is not { } entry)
            {
                return;
            }

            if (await Thumb.Measure(path) is not { } size)
            {
                return;
            }

            // The reading comes back on whichever thread it likes when this was not started
            // from inside the window's own work, so the table is always filled on its thread.
            await Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_viewModel.Clipboard.Selected, entry))
                {
                    var whole = (InfoTable.ItemsSource as IEnumerable<InfoLine> ?? info).ToList();
                    int after = whole.FindIndex(line => line.Label == "Type") + 1;
                    whole.Insert(after, new InfoLine("Dimensions", Thumb.Dimensions(size.Width, size.Height), null));
                    whole.Insert(after + 1, new InfoLine("Size", Thumb.FileSize(size.Bytes), null));
                    InfoTable.ItemsSource = whole;
                }
            });
        }
        catch (Exception ex)
        {
            try
            {
                _pictures.ReportFailure(ex);
            }
            catch (Exception)
            {
                // The report failed as well. It must not leave this method either.
            }
        }
    }

    // The line that says where an entry was copied from gets the program's own name and
    // icon, as Windows has them, when the program can be found among those that are running.
    // Otherwise it keeps the name made from the file's name.
    private async Task NameSource(List<InfoLine> info, ClipEntry? entry)
    {
        try
        {
            if (entry?.SourceApp is not { Length: > 0 } program || await SourcePrograms.Find(program) is not { } found)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_viewModel.Clipboard.Selected, entry) && InfoTable.ItemsSource is IEnumerable<InfoLine> shown)
                {
                    InfoTable.ItemsSource = shown
                        .Select(line => line.Label == "Source" ? new InfoLine(line.Label, found.Name, found.Icon) : line)
                        .ToList();
                }
            });
        }
        catch (Exception)
        {
            // The line keeps the name it had.
        }
    }

    // What pasting is called: with the name of the program it goes to, when that is known.
    private string PasteWords() => _pasteTo.Length > 0 ? "Paste to " + _pasteTo : "Paste";

    // Everything that can be done with what is selected, with the keys that do each thing
    // without the list.
    private List<LauncherAction> Actions()
    {
        var actions = new List<LauncherAction>();
        if (_viewModel.Clipboard.IsOpen)
        {
            if (_viewModel.Clipboard.Selected is { } entry)
            {
                actions.Add(new LauncherAction(PasteWords(), ["\uE751"], () => _viewModel.RunSelected()));
                if (entry.Link is not null)
                {
                    actions.Add(new LauncherAction("Open Link in Browser", ["Ctrl", "\uE751"], () => _viewModel.RunSelected(secondary: true)));
                }

                if (entry.Text is not null)
                {
                    actions.Add(new LauncherAction("Paste as Plain Text", ["Ctrl", "Shift", "\uE751"], _viewModel.PasteSelectedClipAsText));
                }

                actions.Add(new LauncherAction("Copy to Clipboard", ["Ctrl", "Shift", "C"], _viewModel.CopySelectedClip));
                if (_viewModel.Clipboard.PictureText(entry) is { Length: > 0 })
                {
                    actions.Add(new LauncherAction("Copy Text from Picture", ["Ctrl", "Shift", "T"], _viewModel.CopySelectedClipPictureText));
                }

                if (entry.Text is not null)
                {
                    actions.Add(new LauncherAction("Save as Snippet", ["Ctrl", "Shift", "S"], _viewModel.SaveSelectedClipAsSnippet));
                }

                if (_viewModel.Clipboard.CanPin)
                {
                    actions.Add(new LauncherAction(_viewModel.Clipboard.SelectedIsPinned ? "Unpin Entry" : "Pin Entry", ["Ctrl", "Shift", "P"], _viewModel.TogglePinSelectedClip));
                }

                if (entry.HasPicture)
                {
                    actions.Add(new LauncherAction("Save Picture to Pictures Folder", ["Ctrl", "S"], _viewModel.SaveSelectedClip));
                }

                actions.Add(new LauncherAction("Delete Entry", ["Ctrl", "Del"], _viewModel.DeleteSelectedClip));
            }
        }
        else if (_viewModel.Emoji.IsOpen)
        {
            if (_viewModel.Emoji.Selected is not null)
            {
                actions.Add(new LauncherAction(PasteWords(), ["\uE751"], () => _viewModel.RunSelected()));
                actions.Add(new LauncherAction("Copy to Clipboard", ["Ctrl", "Shift", "C"], _viewModel.CopySelectedEmoji));
            }
        }
        else if (!_viewModel.InArgumentMode
            && !_viewModel.Output.IsOpen
            && _viewModel.SelectedIndex >= 0
            && _viewModel.SelectedIndex < _viewModel.Results.Count)
        {
            SearchItem item = _viewModel.Results[_viewModel.SelectedIndex];
            actions.Add(new LauncherAction(LauncherLook.Action(item), ["\uE751"], () => _viewModel.RunSelected()));
            if (LauncherLook.SecondAction(item) is { Length: > 0 } second)
            {
                actions.Add(new LauncherAction(second, ["Ctrl", "\uE751"], () => _viewModel.RunSelected(secondary: true)));
            }

            if (item.Argument is not null)
            {
                actions.Add(new LauncherAction("Type Something for It First", ["Tab"], () => _viewModel.EnterArgumentMode()));
            }

            // What else can be done with it: for an app, say, starting it as administrator.
            foreach (var further in item.Actions)
            {
                actions.Add(new LauncherAction(further.Name, [], () => _viewModel.RunAction(further)));
            }

            // A place among the favorites, which are listed first, and a short word of its own.
            if (_viewModel.CanMarkSelected)
            {
                bool favorite = _viewModel.SelectedIsFavorite;
                actions.Add(new LauncherAction(favorite ? LauncherViewModel.RemoveFromFavorites : LauncherViewModel.AddToFavorites, ["Ctrl", "Shift", "F"], ToggleFavorite));
                if (favorite)
                {
                    actions.Add(new LauncherAction(LauncherViewModel.MoveUpInFavorites, ["Ctrl", "Shift", "\uE74A"], () => MoveFavorite(-1)));
                    actions.Add(new LauncherAction(LauncherViewModel.MoveDownInFavorites, ["Ctrl", "Shift", "\uE74B"], () => MoveFavorite(1)));
                }

                actions.Add(new LauncherAction(LauncherViewModel.SetAlias, ["Ctrl", "Shift", "A"], _viewModel.AskAliasForSelected));
            }
        }

        return actions;
    }

    private void OpenActions()
    {
        var actions = Actions();
        if (actions.Count == 0)
        {
            return;
        }

        ActionsList.ItemsSource = actions;
        ActionsList.SelectedIndex = 0;

        // Its right edge stands at the right edge of the window.
        ActionsPopup.HorizontalOffset = Foot.ActualWidth - 330 - 14 - 8;
        ActionsPopup.IsOpen = true;
    }

    private void RunAction()
    {
        var action = ActionsList.SelectedItem as LauncherAction;
        ActionsPopup.IsOpen = false;
        action?.Run();
        QueryBox.Focus();
    }

    private void OnActionClicked(object sender, MouseButtonEventArgs e)
    {
        if (RowOf(e.OriginalSource) is { } row)
        {
            ActionsList.SelectedItem = row.DataContext;
            RunAction();
        }
    }

    // While the list of actions is open the arrows move through it, Enter does the selected
    // one, and Esc or Ctrl+K closes it. Nothing is typed meanwhile.
    private void OnKeyDownInActions(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                ActionsList.SelectedIndex = Math.Min(ActionsList.SelectedIndex + 1, ActionsList.Items.Count - 1);
                break;
            case Key.Up:
                ActionsList.SelectedIndex = Math.Max(ActionsList.SelectedIndex - 1, 0);
                break;
            case Key.Enter:
                if (!e.IsRepeat)
                {
                    RunAction();
                }

                break;
            case Key.Escape:
                ActionsPopup.IsOpen = false;
                break;
            case Key.K when Keyboard.Modifiers == ModifierKeys.Control:
                ActionsPopup.IsOpen = false;
                break;
        }

        e.Handled = true;
    }

    // The selected item among the favorites, or out of them again. It stays selected, wherever
    // in the list it stands now, and is scrolled to.
    private void ToggleFavorite()
    {
        _viewModel.ToggleFavoriteSelected();
        ShowSelected();
    }

    private void MoveFavorite(int by)
    {
        _viewModel.MoveSelectedFavorite(by);
        ShowSelected();
    }

    private void ShowSelected()
    {
        if (ResultList.SelectedItem is { } selected)
        {
            ResultList.ScrollIntoView(selected);
        }
    }

    // The search text has just been put back; typing should carry on after it, not in front of it.
    private void MoveCaretToEnd() => QueryBox.CaretIndex = QueryBox.Text.Length;

    private void MoveSelection(int delta)
    {
        _viewModel.MoveSelection(delta);
        if (ResultList.SelectedItem is { } selected)
        {
            ResultList.ScrollIntoView(selected);
        }
    }

    private void OnResultClicked(object sender, MouseButtonEventArgs e)
    {
        if (RowOf(e.OriginalSource) is { } row)
        {
            ResultList.SelectedItem = row.DataContext;
            _viewModel.RunSelected();
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    // DWORD GetWindowThreadProcessId(HWND, LPDWORD): the thread that made the window, zero for
    // a handle that is no longer a window, and through the second argument its process.
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    // DwmSetWindowAttribute(hwnd, dwAttribute, pvAttribute, cbAttribute), in Dwmapi.dll. It
    // answers S_OK (zero) or an error code. The attributes used, from the DWMWINDOWATTRIBUTE
    // list: DWMWA_TRANSITIONS_FORCEDISABLED (3), DWMWA_USE_IMMERSIVE_DARK_MODE (20),
    // DWMWA_WINDOW_CORNER_PREFERENCE (33) with DWMWCP_ROUND (2), and DWMWA_SYSTEMBACKDROP_TYPE
    // (38) with DWMSBT_TRANSIENTWINDOW (3), which is the blurred glass of menus and flyouts.
    private const int TransitionsOff = 3;
    private const int DarkMode = 20;
    private const int CornerPreference = 33;
    private const int RoundCorners = 2;
    private const int BackdropType = 38;
    private const int AcrylicBackdrop = 3;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    // GetWindowLong(hwnd, GWL_STYLE) gives the window's styles, zero when it fails, and
    // SetWindowLong puts them. GWL_STYLE is -16 and WS_SYSMENU is 0x00080000. A change of
    // style shows after SetWindowPos with SWP_FRAMECHANGED (0x0020), here with SWP_NOSIZE
    // (0x0001), SWP_NOMOVE (0x0002), SWP_NOZORDER (0x0004) and SWP_NOACTIVATE (0x0010), so
    // that nothing else about the window changes.
    private const int StyleIndex = -16;
    private const int SystemMenu = 0x00080000;
    private const uint NoSize = 0x0001;
    private const uint NoMove = 0x0002;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;
    private const uint FrameChanged = 0x0020;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
