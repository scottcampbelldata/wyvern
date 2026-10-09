using System.ComponentModel;

namespace Wyvern.Core.Launcher;

/// <summary>
/// The launcher's output view: one run of a script, with its title, a status line that says
/// "Running" until the script ends, and then everything it printed. Kept free of UI types so it
/// can be unit tested; the window only shows it.
/// </summary>
/// <remarks>
/// The view shows one run at a time, the one opened last. Closing it does not stop the script:
/// the script runs on, and when it ends <see cref="Complete"/> answers false, so that whoever
/// started it can tell the owner another way. What is shown is held in memory only and is let
/// go of when the view closes. Use from one thread only, the UI thread.
/// </remarks>
public sealed class OutputViewModel : INotifyPropertyChanged
{
    /// <summary>The status while the script is running.</summary>
    public const string Running = "Running";

    private object? _ticket;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>True while the view is on show in place of the result list.</summary>
    public bool IsOpen => _ticket is not null;

    /// <summary>The title of the item whose output is shown. Empty when the view is closed.</summary>
    public string Title { get; private set; } = "";

    /// <summary><see cref="Running"/>, then a few words on how the run ended. Empty when the view is closed.</summary>
    public string Status { get; private set; } = "";

    /// <summary>What the script printed. Empty while it runs and when the view is closed.</summary>
    public string Text { get; private set; } = "";

    /// <summary>Opens the view for a run that is starting now, in place of whatever it showed.</summary>
    /// <returns>A ticket to give to <see cref="Complete"/> when that run ends.</returns>
    public object Open(string title)
    {
        // A local, not the field read back afterwards: whoever is told that the view has opened
        // may close it, or open it for another run, before this returns. The ticket given back
        // is the one made for this run.
        var ticket = new object();
        _ticket = ticket;
        Title = title;
        Status = Running;
        Text = "";
        Announce();
        return ticket;
    }

    /// <summary>Shows how the run ended and what it printed, if the view is still showing that run.</summary>
    /// <param name="ticket">What <see cref="Open"/> returned for the run.</param>
    /// <returns>
    /// False when the view was closed, or opened for another run, before this one ended.
    /// Nothing is shown then.
    /// </returns>
    public bool Complete(object ticket, string status, string text)
    {
        // A closed view holds no ticket, and a ticket that is missing is no ticket either: both
        // are null, and must not be taken to match.
        if (_ticket is null || !ReferenceEquals(ticket, _ticket))
        {
            return false;
        }

        Status = status;
        Text = text;
        Announce();
        return true;
    }

    /// <summary>Closes the view and lets go of what it showed. The script it was showing is not stopped.</summary>
    /// <returns>False when the view was not open.</returns>
    public bool Close()
    {
        if (_ticket is null)
        {
            return false;
        }

        _ticket = null;
        Title = "";
        Status = "";
        Text = "";
        Announce();
        return true;
    }

    private void Announce()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOpen)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
    }
}
