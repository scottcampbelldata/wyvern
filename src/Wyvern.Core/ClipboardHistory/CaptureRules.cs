using Wyvern.Core.Platform;

namespace Wyvern.Core.ClipboardHistory;

/// <summary>Whether what is on the clipboard may go into the history, and if not, why not.</summary>
public enum CaptureVerdict
{
    /// <summary>It may be kept.</summary>
    Keep,

    /// <summary>Wyvern put it there itself, when it pasted something.</summary>
    OwnWrite,

    /// <summary>The program that copied it marked it as not for clipboard histories.</summary>
    MarkedPrivate,

    /// <summary>It was copied from a program the owner has listed in "clipboardExcludedApps".</summary>
    ExcludedProgram,
}

/// <summary>
/// The rules for what is never kept. They are looked at before any content is read, so that
/// what must not be kept is not even copied into Wyvern's memory.
/// </summary>
public static class CaptureRules
{
    /// <summary>
    /// The first rule that applies, in this order: Wyvern's own writes; content that carries
    /// the format which tells monitors to leave it alone (or the older format that says the same), or either of the two formats that
    /// hold a number unless that number is exactly 1 (see <see cref="ClipboardBytes.SaysNo"/>:
    /// 0 says do not, and any other number than 1 is in doubt, and in doubt the copy is left
    /// alone); a program the owner has excluded. Password managers mark what they copy in the
    /// second way. "Do not send to another device" counts as "do not keep", because the
    /// history is synced to the owner's other machine.
    /// </summary>
    /// <param name="keptOutOfMonitors">True when the clipboard has <see cref="ClipboardFormat.KeepOutOfMonitors"/> or the older <see cref="ClipboardFormat.ViewerIgnore"/>: either says, by its presence alone, that the content is to be left alone. The caller folds the two into this one answer.</param>
    /// <param name="canIncludeInHistory">
    /// The block of <see cref="ClipboardFormat.CanIncludeInHistory"/>, or null when the
    /// clipboard does not have it. A marker that is there but could not be read is an empty
    /// block, not null: it says no.
    /// </param>
    /// <param name="canUploadToCloud">
    /// The block of <see cref="ClipboardFormat.CanUploadToCloud"/>, or null when the clipboard
    /// does not have it. A marker that is there but could not be read is an empty block, not
    /// null: it says no.
    /// </param>
    /// <param name="excludes">Says whether a program is one the owner has excluded. It is not asked when an earlier rule has decided.</param>
    public static CaptureVerdict Judge(
        ClipboardOwner owner,
        bool keptOutOfMonitors,
        byte[]? canIncludeInHistory,
        byte[]? canUploadToCloud,
        Func<string?, bool> excludes)
    {
        if (owner.IsWyvern)
        {
            return CaptureVerdict.OwnWrite;
        }

        if (keptOutOfMonitors || ClipboardBytes.SaysNo(canIncludeInHistory) || ClipboardBytes.SaysNo(canUploadToCloud))
        {
            return CaptureVerdict.MarkedPrivate;
        }

        return excludes(owner.Program) ? CaptureVerdict.ExcludedProgram : CaptureVerdict.Keep;
    }
}
