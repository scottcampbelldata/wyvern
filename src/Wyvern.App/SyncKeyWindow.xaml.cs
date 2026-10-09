using System.Windows;
using Wyvern.App.Interop;
using Wyvern.Core.Sync;

namespace Wyvern.App;

/// <summary>
/// The window that shows the sync key. It only shows <see cref="SyncKeyViewModel"/>, which has
/// read the key before the window opens, and lets go of the key when it closes.
/// </summary>
public partial class SyncKeyWindow : Window
{
    private readonly SyncKeyViewModel _viewModel;

    /// <param name="report">Receives fixed words for the log.</param>
    public SyncKeyWindow(SyncKeyViewModel viewModel, Action<string, Exception?> report)
    {
        InitializeComponent();

        // Light or dark as Windows is set, in the colours the launcher has.
        LauncherLook.Apply(this, LauncherLook.IsLight());
        _viewModel = viewModel;
        DataContext = viewModel;

        // The key is on screen here, so the window is kept out of pictures of the screen. When
        // Windows will not do that, the window says so, and so does the log.
        SourceInitialized += (_, _) =>
        {
            if (!ScreenCapture.Exclude(this))
            {
                CaptureWarning.Visibility = Visibility.Visible;
                report("The Sync Key window could not be kept out of screen captures", null);
            }
        };

        // The view model lets go of the key and tells the text that shows it, which empties.
        // Then the window lets go of the view model, so that nothing in it shows anything again.
        Closed += (_, _) =>
        {
            viewModel.Clear();
            DataContext = null;
        };
    }

    private void OnCopy(object sender, RoutedEventArgs e) => _viewModel.Copy();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
