using System.ComponentModel;
using Beacon.Core.Platform;

namespace Beacon.Core.Sync;

/// <summary>
/// What the Show Sync Key window shows, free of UI types so it can be tested: the sync key as
/// text, a Copy button, and a sentence. The window only binds to it.
/// </summary>
/// <remarks>
/// The key is read from its file when the window opens (<see cref="Load"/>, which reads the
/// file and asks Windows to unprotect it, so it is called away from the UI thread) and is let
/// go of when the window closes (<see cref="Clear"/>). While the window is open the key is
/// text, and text cannot be wiped from memory: that is the price of showing it. The window
/// shows it as text that cannot be selected, so the Copy button is the only way it reaches the
/// clipboard, and Copy goes through the clipboard writer that marks what it writes: clipboard
/// managers, the Windows clipboard history and Beacon's own history leave it out.
/// </remarks>
public sealed class SyncKeyViewModel : INotifyPropertyChanged
{
    public const string ShownMessage = "Enter this key in Sync Setup on your other machine. Whoever has it and can reach your server can read what is synced, so keep it to yourself.";
    public const string NoKeyMessage = "This machine has no sync key yet. Run Sync Setup.";
    public const string UnreadableMessage = "The sync key on this machine cannot be read. Run Sync Setup and enter the key again.";
    public const string CopiedMessage = "Copied. The key stays on the clipboard until you copy something else. Clipboard history does not record it.";
    public const string CopyFailedMessage = "The key could not be copied. Press Copy again.";

    private readonly KeyStore _keys;
    private readonly IClipboard _clipboard;
    private readonly Action<string, Exception?> _report;
    private string _keyText = "";
    private string _message = "";

    /// <param name="clipboard">The clipboard writer that marks what it writes.</param>
    /// <param name="report">Receives fixed words and an exception for the log. Never the key.</param>
    public SyncKeyViewModel(KeyStore keys, IClipboard clipboard, Action<string, Exception?>? report = null)
    {
        _keys = keys;
        _clipboard = clipboard;
        _report = report ?? ((_, _) => { });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The key as base64, or empty when there is none to show.</summary>
    public string KeyText
    {
        get => _keyText;
        private set
        {
            _keyText = value;
            Raise(nameof(KeyText));
            Raise(nameof(HasKey));
        }
    }

    /// <summary>True when there is a key to show and to copy.</summary>
    public bool HasKey => _keyText.Length > 0;

    /// <summary>The sentence under the key.</summary>
    public string Message
    {
        get => _message;
        private set
        {
            _message = value;
            Raise(nameof(Message));
        }
    }

    /// <summary>Reads the key from its file. Call it away from the UI thread.</summary>
    public void Load()
    {
        var state = _keys.ReadMaster(out byte[] master, out var cause);
        try
        {
            if (state == SecretState.Read)
            {
                KeyText = SyncKeys.Format(master);
                Message = ShownMessage;
                return;
            }
        }
        finally
        {
            SyncKeys.Wipe(master);
        }

        if (state == SecretState.Unreadable)
        {
            _report("The sync key could not be read to be shown", cause);
        }

        KeyText = "";
        Message = state == SecretState.Missing ? NoKeyMessage : UnreadableMessage;
    }

    /// <summary>What the Copy button does. For the UI thread, where the clipboard writer lives.</summary>
    public void Copy()
    {
        if (!HasKey)
        {
            return;
        }

        try
        {
            _clipboard.SetText(_keyText);
            Message = CopiedMessage;
        }
        catch (Exception ex)
        {
            _report("The sync key could not be put on the clipboard", ex);
            Message = CopyFailedMessage;
        }
    }

    /// <summary>Call when the window closes: the key is shown and held no longer.</summary>
    public void Clear()
    {
        KeyText = "";
        Message = "";
    }

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
