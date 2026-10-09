using System.Globalization;
using Wyvern.Core.Settings;

namespace Wyvern.Core.Update;

/// <summary>What the owner wants done about updates. "updates" in settings.json.</summary>
public enum UpdateMode
{
    /// <summary>Look for updates, say when there is one, and install it when the owner asks. The default.</summary>
    Ask,

    /// <summary>Look for updates and install one as soon as it is found, without asking.</summary>
    Auto,

    /// <summary>Never look: no connection is made for updates.</summary>
    Off,
}

/// <summary>The update settings, worked out from settings.json.</summary>
public static class UpdateOptions
{
    /// <summary>How long after Wyvern has started it first looks for an update.</summary>
    public static readonly TimeSpan FirstLook = TimeSpan.FromSeconds(45);

    /// <summary>How long it waits between two looks after that.</summary>
    public static readonly TimeSpan Between = TimeSpan.FromHours(4);

    /// <summary>
    /// "updates" is "ask", "auto" or "off", in any case, or true (ask) or false (off). Left
    /// out, or anything else, it is "ask".
    /// </summary>
    public static UpdateMode From(SettingsDocument? settings)
    {
        if (settings is null)
        {
            return UpdateMode.Ask;
        }

        return settings.Text("updates").Trim().ToLowerInvariant() switch
        {
            "auto" => UpdateMode.Auto,
            "off" => UpdateMode.Off,
            "ask" => UpdateMode.Ask,
            _ => settings.Flag("updates", true) ? UpdateMode.Ask : UpdateMode.Off,
        };
    }
}

/// <summary>
/// Fetches files from the update server. In the app it is the curl that is part of Windows,
/// started with the arguments <see cref="UpdateDownload.CurlArguments"/> gives: it can only
/// fetch. Called on a background thread.
/// </summary>
public interface IUpdateSource
{
    /// <summary>The bytes at an address, or null when they could not be had or are more than <paramref name="maxBytes"/>.</summary>
    byte[]? Fetch(string url, int maxBytes);

    /// <summary>Writes what is at an address into a file. False when it could not be had whole, or is more than <paramref name="maxBytes"/>.</summary>
    bool Download(string url, string toFile, long maxBytes);
}

/// <summary>The one way Wyvern fetches a file from the update server: the arguments it starts Windows' own curl with.</summary>
public static class UpdateDownload
{
    /// <summary>How long a connection may take to be made, in seconds.</summary>
    public const int ConnectSeconds = 8;

    /// <summary>How long the small files may take, in seconds.</summary>
    public const int SmallFileSeconds = 20;

    /// <summary>How long the setup program may take, in seconds.</summary>
    public const int InstallerSeconds = 900;

    /// <summary>
    /// What the setup program of an update is started with: install without showing or
    /// asking anything, never restart Windows, and the mark that tells the setup it was
    /// started by Wyvern itself, so that it waits for Wyvern to have quit and starts the
    /// new one when it is done.
    /// </summary>
    public static IReadOnlyList<string> SetupArguments { get; } = ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/WYVERNUPDATE=1"];

    /// <summary>
    /// The arguments for that curl: fetch this one address into this one file and do nothing
    /// else. No settings file of curl's is read, no proxy is used, nothing but plain http is
    /// spoken, no redirect is followed, and a file larger than allowed is not taken. There is
    /// nothing here that sends anything: every argument is fixed but the address, the file
    /// and the two limits.
    /// </summary>
    /// <param name="url">An address from <see cref="UpdateChannel.UrlOf"/>.</param>
    public static IReadOnlyList<string> CurlArguments(string url, string toFile, long maxBytes, int maxSeconds) =>
    [
        "-q",
        "--silent",
        "--fail",
        "--ipv4",
        "--noproxy", "*",
        "--proto", "=http",
        "--max-redirs", "0",
        "--connect-timeout", ConnectSeconds.ToString(CultureInfo.InvariantCulture),
        "--max-time", maxSeconds.ToString(CultureInfo.InvariantCulture),
        "--max-filesize", maxBytes.ToString(CultureInfo.InvariantCulture),
        "--user-agent", "Wyvern",
        "--output", toFile,
        "--url", url,
    ];
}

/// <summary>
/// Looks for a newer Wyvern on the owner's update server, and fetches its setup program.
/// Nothing here installs anything, and nothing is kept that is not the file a signed
/// manifest names. Both calls wait for the server: use them on a background thread.
/// </summary>
public sealed class UpdateService
{
    private readonly UpdateChannel _channel;
    private readonly Version _running;
    private readonly IUpdateSource _source;
    private readonly string _folder;
    private readonly Action<string, Exception?> _report;

    /// <param name="running">The version of this Wyvern.</param>
    /// <param name="folder">Where a fetched setup program is kept until it is run: a folder of Wyvern's own.</param>
    /// <param name="report">Told in fixed words and version numbers what was found and what went wrong.</param>
    public UpdateService(UpdateChannel channel, Version running, IUpdateSource source, string folder, Action<string, Exception?>? report = null)
    {
        _channel = channel;
        _running = UpdateManifest.ThreeNumbers(running);
        _source = source;
        _folder = folder;
        _report = report ?? ((_, _) => { });
    }

    /// <summary>The version of this Wyvern, as three numbers.</summary>
    public Version Running => _running;

    /// <summary>Asks the server what the newest version is. Never throws.</summary>
    public UpdateCheck Check()
    {
        try
        {
            var check = Look();

            // When an update is published its two small files are put in place one right after
            // the other. A look that fell between the two finds a signature that does not fit
            // the manifest: it looks once more before it says so.
            return check.Verdict == UpdateVerdict.NotSigned ? Look() : check;
        }
        catch (Exception ex)
        {
            _report("Looking for an update failed", ex);
            return UpdateCheck.Unreachable;
        }
    }

    private UpdateCheck Look()
    {
        if (_source.Fetch(_channel.UrlOf(UpdateManifest.FileName), UpdateManifest.MaxBytes) is not { } manifest
            || _source.Fetch(_channel.UrlOf(UpdateManifest.SignatureFileName), UpdateManifest.MaxSignatureBytes) is not { } signature)
        {
            return UpdateCheck.Unreachable;
        }

        return UpdateManifest.Read(manifest, signature, _channel.PublicKey, _running);
    }

    /// <summary>
    /// Fetches the setup program of an offer into Wyvern's updates folder and checks it
    /// against the offer. A file that is not byte for byte the one the signed manifest names
    /// is deleted. Never throws.
    /// </summary>
    /// <returns>The setup program's path, or null when it could not be fetched or was refused.</returns>
    public string? Download(UpdateOffer offer)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            Tidy(keep: offer.FileName);
            string file = Path.Combine(_folder, offer.FileName);

            // Fetched before, by a download whose install did not follow.
            if (UpdateManifest.FileMatches(file, offer))
            {
                return file;
            }

            string unfinished = file + ".part";
            File.Delete(unfinished);
            File.Delete(file);
            if (!_source.Download(_channel.UrlOf(offer.FileName), unfinished, offer.Size))
            {
                File.Delete(unfinished);
                return null;
            }

            if (!UpdateManifest.FileMatches(unfinished, offer))
            {
                File.Delete(unfinished);
                _report(FormattableString.Invariant($"The setup program of version {offer.VersionText} was refused: it is not the file that was signed"), null);
                return null;
            }

            File.Move(unfinished, file, overwrite: true);
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _report("An update could not be fetched", ex);
            return null;
        }
    }

    /// <summary>
    /// Removes what earlier updates left in the updates folder: setup programs and unfinished
    /// downloads, all but the one named. Call it when Wyvern starts, with nothing to keep.
    /// Only files Wyvern put there by name are touched. Never throws.
    /// </summary>
    public void Tidy(string? keep = null)
    {
        try
        {
            if (!Directory.Exists(_folder))
            {
                return;
            }

            foreach (string path in Directory.EnumerateFiles(_folder))
            {
                string name = Path.GetFileName(path);
                bool ours = name.StartsWith(UpdateManifest.InstallerPrefix, StringComparison.Ordinal)
                    && (name.EndsWith(".exe", StringComparison.Ordinal) || name.EndsWith(".exe.part", StringComparison.Ordinal))
                    || name.EndsWith(".fetch", StringComparison.Ordinal);
                if (ours && name != keep)
                {
                    File.Delete(path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // What cannot be removed now is removed the next time.
        }
    }
}
