using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Beacon.App.Interop;

/// <summary>Loads the icons Windows shows for installed apps and for kinds of file.</summary>
public static class ShellIcon
{
    private const int IconOnly = 0x4; // SIIGBF_ICONONLY: never a thumbnail of file contents

    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeNormal = 0x80;
    private const uint GetIcon = 0x100;           // SHGFI_ICON (large icon)
    private const uint UseFileAttributes = 0x10;  // SHGFI_USEFILEATTRIBUTES: go by the name alone, never touch a file

    /// <summary>The icon of an entry in shell:AppsFolder.</summary>
    /// <returns>A frozen image usable from any thread, or null when Windows has no icon for the id.</returns>
    public static BitmapSource? Load(string appId, int pixelSize)
    {
        Guid factoryId = typeof(IShellItemImageFactory).GUID;
        int result = SHCreateItemFromParsingName(@"shell:AppsFolder\" + appId, IntPtr.Zero, ref factoryId, out var factory);
        if (result != 0 || factory is null)
        {
            return null;
        }

        try
        {
            if (factory.GetImage(new NativeSize { Width = pixelSize, Height = pixelSize }, IconOnly, out IntPtr bitmap) != 0)
            {
                return null;
            }

            try
            {
                var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                return image;
            }
            finally
            {
                DeleteObject(bitmap);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>
    /// The icon Windows uses for a kind of file. It is worked out from the extension alone: no
    /// file is opened, so asking about ".pdf" never touches any particular PDF.
    /// </summary>
    /// <param name="fileType">"folder", or an extension with its dot such as ".pdf", or "" for a file without one.</param>
    /// <returns>A frozen image usable from any thread, or null when Windows gives none.</returns>
    public static BitmapSource? LoadForFileType(string fileType)
    {
        bool isFolder = fileType == "folder";
        var info = default(ShellFileInfo);
        IntPtr found = SHGetFileInfo(
            isFolder ? "folder" : "file" + fileType,
            isFolder ? FileAttributeDirectory : FileAttributeNormal,
            ref info,
            (uint)Marshal.SizeOf<ShellFileInfo>(),
            GetIcon | UseFileAttributes);
        if (found == IntPtr.Zero || info.Icon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally
        {
            DestroyIcon(info.Icon);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string path,
        IntPtr bindContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? item);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint fileAttributes, ref ShellFileInfo info, uint infoSize, uint flags);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    // SHFILEINFOW
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }
}
