using System.Globalization;
using Beacon.Core.Platform;

namespace Beacon.Core.ClipboardHistory;

/// <summary>
/// Gets a picture back out of the clipboard history: saves a copy of an entry's picture, and of
/// the pictures that were kept from files it listed, into a folder inside the owner's Pictures
/// folder, and shows it in Explorer. This is what makes a photo that was copied and later
/// deleted recoverable.
/// </summary>
/// <remarks>
/// Nothing is ever overwritten: a name that is taken gets "-2", "-3" and so on. The name a kept
/// picture is saved under comes from a record, which another machine may have written, so it is
/// made a plain file name first, of at most <see cref="MaxNameLength"/> characters, and always
/// ends in the extension of the kind of picture the file really is. The folder and the names
/// are the owner's: none of them is logged.
/// </remarks>
public sealed class ClipRecovery
{
    /// <summary>The folder inside the Pictures folder that copies are saved into. It is made when it is missing.</summary>
    public const string FolderName = "Beacon recovered";

    /// <summary>Shown when an entry's pictures are not on this machine, so that there was nothing to save.</summary>
    public const string NothingSavedNote = "The picture is not on this machine, so no copy could be saved.";

    /// <summary>
    /// The words of the <see cref="IOException"/> that refuses a Pictures folder that is blank or
    /// is not a full path, which is what the folder is when Windows could not say where it is.
    /// They name no folder. Nothing has been written when it is thrown.
    /// </summary>
    public const string NoPicturesFolderMessage = "The Pictures folder is not known, so no copy of the picture was saved.";

    /// <summary>The longest name a copy is given, without its extension. A longer name is cut.</summary>
    public const int MaxNameLength = 120;

    // Far more copies of one name than anyone makes. It only stops the search for a free name.
    private const int MaxCopies = 1000;

    private readonly Func<string> _picturesFolder;
    private readonly IProcessLauncher _launcher;
    private readonly Action<string> _notify;
    private readonly TimeZoneInfo? _zone;

    /// <param name="picturesFolder">Gives the owner's Pictures folder. The app asks Windows; a test passes a temporary folder.</param>
    /// <param name="launcher">Shows the saved copy in Explorer.</param>
    /// <param name="notify">Shows one of this class's fixed sentences to the owner.</param>
    /// <param name="zone">The time zone a clipboard picture's name is made in. Left out everywhere except in tests: the machine's own.</param>
    public ClipRecovery(Func<string> picturesFolder, IProcessLauncher launcher, Action<string> notify, TimeZoneInfo? zone = null)
    {
        _picturesFolder = picturesFolder;
        _launcher = launcher;
        _notify = notify;
        _zone = zone;
    }

    /// <summary>
    /// Saves a copy of every picture the entry holds and shows the first in Explorer. A picture
    /// that was on the clipboard is named for the entry's date and time; a picture kept from a
    /// file gets the name that file had. An entry without pictures is left alone.
    /// </summary>
    /// <returns>
    /// The full paths of the copies, in the order they were saved. When Explorer could not be
    /// started to show the first, that is not told: the copies are there.
    /// </returns>
    /// <exception cref="IOException">
    /// The folder or a copy could not be written, or the Pictures folder is blank or is not a
    /// full path (<see cref="NoPicturesFolderMessage"/>, thrown before anything is written).
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The folder or a copy could not be written.</exception>
    public IReadOnlyList<string> Save(ClipEntry entry)
    {
        var saved = new List<string>();
        if (!entry.HasPicture)
        {
            return saved;
        }

        // Windows can be without a Pictures folder, or the function that asks for it can give
        // blank text or a path that is not a full one. Combined with a folder name and made, that
        // would put "Beacon recovered" wherever Beacon happened to be started from. It is
        // refused before anything is written, in words that name no folder.
        string pictures = _picturesFolder();
        if (string.IsNullOrWhiteSpace(pictures) || !Path.IsPathFullyQualified(pictures))
        {
            throw new IOException(NoPicturesFolderMessage);
        }

        string folder = Path.Combine(pictures, FolderName);

        if (entry.Image is { } image && ClipPaths.IsBlobName(image, ClipPaths.ImageExtension))
        {
            var local = TimeZoneInfo.ConvertTime(entry.Time, _zone ?? TimeZoneInfo.Local);
            string name = "Clipboard " + local.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture);
            Copy(entry.PathOf(image), folder, name, ClipPaths.ImageExtension, saved);
        }

        foreach (var picture in entry.Pictures)
        {
            if (ClipPaths.IsPictureName(picture.File))
            {
                // The extension is the stored file's own, whatever the record says the name was.
                Copy(entry.PathOf(picture.File), folder, Stem(picture.Name), Path.GetExtension(picture.File), saved);
            }
        }

        if (saved.Count == 0)
        {
            _notify(NothingSavedNote);
        }
        else
        {
            RevealFirst(saved[0]);
        }

        return saved;
    }

    // Shows the first copy in Explorer. By now every copy is on disk, so a failure to start
    // Explorer is no failure to save: it must not come out of Save, where the owner would be told
    // that nothing was saved while the copies are there. This class has no log, so the failure
    // is not said anywhere; what could be said (a launcher's words) is not Beacon's to quote.
    private void RevealFirst(string copy)
    {
        try
        {
            _launcher.RevealInFolder(copy);
        }
        catch (Exception)
        {
            // The copies are in the folder for copies, and that is what was asked for.
        }
    }

    // Copies one file under the first free name. A source that has gone is skipped: the entry
    // may be another machine's, whose files have not arrived.
    private static void Copy(string source, string folder, string stem, string extension, List<string> saved)
    {
        if (!File.Exists(source))
        {
            return;
        }

        // Made only now, so that an entry with nothing to save leaves no empty folder behind.
        Directory.CreateDirectory(folder);
        for (int number = 1; number <= MaxCopies; number++)
        {
            string target = Path.Combine(folder, (number == 1 ? stem : $"{stem}-{number.ToString(CultureInfo.InvariantCulture)}") + extension);
            FileStream output;
            try
            {
                // CreateNew refuses a name that exists, so nothing is ever overwritten. Only
                // this step says "the name is taken": a failure to write afterwards is a real
                // failure, even though the file is there by then.
                output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && (File.Exists(target) || Directory.Exists(target)))
            {
                // A folder with the name takes it just as a file does.
                continue;
            }

            try
            {
                using (output)
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    input.CopyTo(output);
                }
            }
            catch
            {
                // Half a picture must not stay behind looking like a recovered one.
                try
                {
                    File.Delete(target);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The failure that matters is the one being passed on.
                }

                throw;
            }

            saved.Add(target);
            return;
        }

        throw new IOException("Every name for the recovered picture is already taken.");
    }

    // The name a picture had, without its folder and its extension, made safe as part of a file
    // name. The rules are the ones that make a machine's name a safe folder name: no characters
    // Windows refuses, no name Windows reserves, nothing that ends in a dot or a space. A name
    // of which nothing is left, such as one that was only an extension, becomes "picture".
    private static string Stem(string name)
    {
        int separator = name.LastIndexOfAny(['\\', '/']);
        string file = separator < 0 ? name : name[(separator + 1)..];
        int dot = file.LastIndexOf('.');
        string stem = ClipboardOptions.SafeFileName(NoteText.WithoutInvisibleCharacters(dot >= 0 ? file[..dot] : file), MaxNameLength);
        return stem.Length == 0 ? "picture" : stem;
    }
}
