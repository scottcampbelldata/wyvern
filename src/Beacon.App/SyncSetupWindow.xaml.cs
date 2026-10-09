using System.Windows;
using Beacon.App.Interop;
using Beacon.Core.Sync;
using Microsoft.Win32;

namespace Beacon.App;

/// <summary>
/// The Sync Setup window. It only shows <see cref="SyncSetupViewModel"/> and hands clicks to
/// it: nothing is decided here, and nothing here reads a file, unprotects a key or connects.
/// </summary>
public partial class SyncSetupWindow : Window
{
    private readonly SyncSetupViewModel _viewModel;
    private readonly Action<string, Exception?> _report;

    /// <param name="report">Receives fixed words for the log.</param>
    public SyncSetupWindow(SyncSetupViewModel viewModel, Action<string, Exception?> report)
    {
        InitializeComponent();

        // Light or dark as Windows is set, in the colours the launcher has.
        LauncherLook.Apply(this, LauncherLook.IsLight());
        _viewModel = viewModel;
        _report = report;
        DataContext = viewModel;

        // A key that is typed in is in a text box, so that it can be read back by eye. The
        // box's own copy and cut, and a drag of what is selected out of it, would hand the key
        // over without the marks that keep it out of every history of copied things. WPF asks
        // here before each of the three, and each is called off. Pasting into the box does not
        // come this way and still works.
        DataObject.AddCopyingHandler(EnteredKeyBox, (_, e) => e.CancelCommand());

        // A passphrase is typed here and a new key is shown here, so the window is kept out of
        // pictures of the screen. When Windows will not do that, the window says so, and so
        // does the log.
        SourceInitialized += (_, _) =>
        {
            if (!ScreenCapture.Exclude(this))
            {
                CaptureWarning.Visibility = Visibility.Visible;
                report("The Sync Setup window could not be kept out of screen captures", null);
            }
        };

        // What was typed and what was shown is let go of with the window. The view model
        // first: it tells the controls that are bound to it, and they empty themselves. Then
        // the two boxes that hold a secret are emptied by name, whatever they were told. Last,
        // the window lets go of the view model, so that nothing in it shows anything again.
        Closed += (_, _) =>
        {
            viewModel.Clear();
            PassphraseBox.Clear();
            EnteredKeyBox.Clear();
            DataContext = null;
        };
    }

    private void OnTest(object sender, RoutedEventArgs e) => _viewModel.TestConnection();

    private void OnSave(object sender, RoutedEventArgs e) => _viewModel.Save();

    private void OnCopyKey(object sender, RoutedEventArgs e) => _viewModel.CopyGeneratedKey();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // A password box cannot be bound, so what is typed is handed over as it changes.
    private void OnPassphraseChanged(object sender, RoutedEventArgs e) => _viewModel.Passphrase = PassphraseBox.Password;

    // The owner picks the key file. Only its path is taken; the file is read later, by the
    // check that Test Connection makes away from this thread. The dialog opens wherever
    // Windows last left it: nothing is asked about a folder here. What is picked is a private
    // key, so the dialog is told not to put it into the list of recent files that Windows keeps.
    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose your SSH private key file",
            CheckFileExists = true,
            AddToRecent = false,
            Filter = "All files (*.*)|*.*",
        };

        // A dialog that Windows could not show is told to the log, in fixed words. Nothing is
        // shown, and the field stays as it was: the path can still be typed.
        bool? chosen;
        try
        {
            chosen = dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            _report("The dialog for choosing the key file of Sync Setup could not be shown", ex);
            return;
        }

        if (chosen == true)
        {
            _viewModel.KeyFile = dialog.FileName;
        }
    }
}
