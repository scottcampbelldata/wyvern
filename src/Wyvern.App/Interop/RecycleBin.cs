using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace Wyvern.App.Interop;

/// <summary>Puts a file into the Windows Recycle Bin, from where the owner can have it back.</summary>
public static class RecycleBin
{
    /// <summary>
    /// Moves one file to the Recycle Bin without asking. Windows asks only where it cannot keep
    /// the file, on a drive without a bin: saying no there leaves the file where it is.
    /// </summary>
    /// <exception cref="IOException">The file is not there, or Windows would not move it.</exception>
    /// <exception cref="OperationCanceledException">The owner said no when Windows asked.</exception>
    public static void Send(string file)
    {
        if (!File.Exists(file))
        {
            throw new FileNotFoundException("The file to put into the Recycle Bin is not there.");
        }

        FileSystem.DeleteFile(file, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
    }
}
