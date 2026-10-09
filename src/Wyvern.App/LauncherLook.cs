using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wyvern.Core.ClipboardHistory;
using Wyvern.Core.Commands;
using Wyvern.Core.Launcher;
using Wyvern.Core.Search;
using Wyvern.Core.Settings;
using Microsoft.Win32;

namespace Wyvern.App;

/// <summary>One choice of which kinds of entries the clipboard history lists, with the words shown for it.</summary>
public sealed record TypeChoice(string Name, ClipTypeFilter Value);

/// <summary>One of the skin tones the emoji picker can be set to, with the words it is shown by.</summary>
public sealed record ToneChoice(string Name, SkinTone Value);

/// <summary>
/// How the launcher looks: its colours, light or dark as Windows is set, and the sign and
/// colour each kind of row has. The layout itself is in LauncherWindow.xaml.
/// </summary>
public static class LauncherLook
{
    public static IReadOnlyList<TypeChoice> TypeChoices { get; } =
    [
        new("All Types", ClipTypeFilter.All),
        new("Text Only", ClipTypeFilter.Text),
        new("Images Only", ClipTypeFilter.Images),
        new("Files Only", ClipTypeFilter.Files),
        new("Links Only", ClipTypeFilter.Links),
    ];

    public static IReadOnlyList<ToneChoice> ToneChoices { get; } =
    [
        new("Default Skin Tone", SkinTone.Default),
        new("Light", SkinTone.Light),
        new("Medium-Light", SkinTone.MediumLight),
        new("Medium", SkinTone.Medium),
        new("Medium-Dark", SkinTone.MediumDark),
        new("Dark", SkinTone.Dark),
    ];

    private static readonly (string Key, string Light, string Dark)[] Colours =
    [
        ("Bg", "#FFFBFBFB", "#FF1F1F21"),
        ("Stroke", "#FFD0D0D0", "#FF3D3D40"),
        ("Line", "#FFE5E5E5", "#FF303033"),
        ("Ink", "#FF242424", "#FFF2F2F3"),
        ("Dim", "#FF8A8A8A", "#FF98989D"),
        ("Faint", "#FFA9A9A9", "#FF6E6E73"),
        ("Pick", "#FFEAEAEB", "#FF323236"),
        ("Chip", "#FFE8E8E9", "#FF38383C"),
    ];

    /// <summary>
    /// True when Windows shows apps in light colours, which is what it says under
    /// Personalisation. Read from the owner's own part of the registry; when it cannot be
    /// read, light is assumed, as Windows itself does.
    /// </summary>
    /// <summary>What Wyvern's windows are to look like: as Windows is set, or light or dark whatever Windows is set to. Set from settings.json when it is read.</summary>
    public static Appearance Appearance { get; set; } = Appearance.System;

    /// <summary>True when Wyvern's windows are to be light now: by its own setting, or by Windows when the setting leaves it to Windows.</summary>
    public static bool IsLight() => Appearance switch
    {
        Appearance.Light => true,
        Appearance.Dark => false,
        _ => WindowsIsLight(),
    };

    public static bool WindowsIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int light || light != 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    // Over blurred glass: the window's own tint, thin enough for the blur to show, and the
    // greys that lie on it, which are a little black or a little white and not a solid grey.
    private static readonly (string Key, string Light, string Dark)[] OnGlass =
    [
        ("Panel", "#A6FBFBFB", "#8C1C1C1E"),
        ("Pick", "#14000000", "#26FFFFFF"),
        ("Line", "#1C000000", "#1FFFFFFF"),
        ("Chip", "#14000000", "#26FFFFFF"),
    ];

    /// <summary>
    /// Gives a window the light or the dark colours. Everything in it that asks for one by
    /// name follows. With <paramref name="glass"/> the window stands on blurred glass that
    /// Windows draws: its own background is then a thin tint.
    /// </summary>
    public static void Apply(FrameworkElement window, bool light, bool glass = false)
    {
        foreach (var (key, lightColour, darkColour) in Colours)
        {
            Set(window, key, light ? lightColour : darkColour);
            if (key == "Bg")
            {
                // The window's own background: the same, unless it stands on glass.
                Set(window, "Panel", light ? lightColour : darkColour);
            }
        }

        if (glass)
        {
            foreach (var (key, lightColour, darkColour) in OnGlass)
            {
                Set(window, key, light ? lightColour : darkColour);
            }
        }

        if (window is Window top)
        {
            TitleBar(top, dark: !light);
        }
    }

    // A window with a title bar of Windows' own gets a dark one when it is dark, so that the
    // settings window is not dark under a white bar. Asked of Windows when the window exists,
    // which for a window that is still being made is a moment later. Windows 10 before
    // version 2004 does not know the request and leaves the bar as it is.
    private static void TitleBar(Window window, bool dark)
    {
        IntPtr handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            window.SourceInitialized += (_, _) => TitleBar(window, dark);
            return;
        }

        try
        {
            int on = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, ImmersiveDarkMode, ref on, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // The bar stays as it is.
        }
    }

    // DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE (20), BOOL*, sizeof(BOOL)), in Dwmapi.dll.
    private const int ImmersiveDarkMode = 20;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    private static void Set(FrameworkElement window, string key, string colour)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colour));
        brush.Freeze();
        window.Resources[key] = brush;
    }

    /// <summary>The sign and the colour of the tile shown for an item that has no icon of its own.</summary>
    public static (string Glyph, string Colour) Tile(SearchItem item) => item.Id switch
    {
        "command:clipboard-history" => ("\uE77F", "#FFF5A623"),
        "command:pause-clipboard" => ("\uE769", "#FFF5A623"),
        "command:resume-clipboard" => ("\uE768", "#FFF5A623"),
        "command:clear-clipboard" => ("\uE74D", "#FFF5A623"),
        "command:new-snippet" => ("\uE70F", "#FFA66BFF"),
        "command:edit-quicklinks" => ("\uE71B", "#FF3B82F6"),
        "command:new-quicklink" => ("\uE710", "#FF3B82F6"),
        "command:open-snippets" => ("\uE8B7", "#FFA66BFF"),
        "command:open-library" => ("\uE8B7", "#FF3B82F6"),
        "command:lock-screen" => ("\uE72E", "#FF636366"),
        "command:sleep" => ("\uE708", "#FF5E5CE6"),
        "command:hibernate" => ("\uE823", "#FF5E5CE6"),
        "command:restart" => ("\uE777", "#FFFF9F0A"),
        "command:shut-down" => ("\uE7E8", "#FFFF6363"),
        "command:sign-out" => ("\uF3B1", "#FF636366"),
        "command:empty-recycle-bin" => ("\uE74D", "#FFFF6363"),
        "command:open-recycle-bin" => ("\uE74D", "#FF636366"),
        "command:toggle-mute" => ("\uE74F", "#FF3B82F6"),
        "command:volume-up" => ("\uE995", "#FF3B82F6"),
        "command:volume-down" => ("\uE993", "#FF3B82F6"),
        "command:set-volume" => ("\uE767", "#FF3B82F6"),
        "command:toggle-appearance" => ("\uE793", "#FF636366"),
        "command:show-desktop" => ("\uE7F4", "#FF636366"),
        "command:media-play-pause" => ("\uE768", "#FFFF375F"),
        "command:media-next" => ("\uE893", "#FFFF375F"),
        "command:media-previous" => ("\uE892", "#FFFF375F"),
        "command:settings" => ("\uE713", "#FF8E8E93"),
        "command:open-settings" => ("\uE713", "#FF8E8E93"),
        "command:reload" => ("\uE72C", "#FF8E8E93"),
        "command:quit" => ("\uE7E8", "#FFFF6363"),
        "command:check-updates" => ("\uE895", "#FF34C759"),
        "command:update-wyvern" => ("\uE896", "#FF34C759"),
        "command:confetti" => ("\uE735", "#FFFF9F0A"),
        "command:appearance-dark" => ("\uE708", "#FF636366"),
        "command:appearance-light" => ("\uE706", "#FFFF9F0A"),
        "command:appearance-system" => ("\uE771", "#FF8E8E93"),
        "command:search-emoji" => ("\uE76E", "#FFFFB02E"),
        "command:switch-windows" => ("\uE7C4", WindowColour),
        "command:sync-now" => ("\uE895", "#FF14B8A6"),
        "command:sync-setup" => ("\uE774", "#FF14B8A6"),
        "command:show-sync-key" => ("\uE8D7", "#FF14B8A6"),
        "command:create-sync-key" => ("\uE8D7", "#FF14B8A6"),
        "command:restore-missing-files" => ("\uE896", "#FF14B8A6"),
        "command:confirm-sync-deletions" => ("\uE73E", "#FF14B8A6"),
        "command:window-next-display" => ("\uE72A", WindowColour),
        "command:window-previous-display" => ("\uE72B", WindowColour),
        "command:window-restore" => ("\uE7A7", WindowColour),

        // The other window commands have a small picture of where the window goes, in place of a sign: see Shape.
        _ when IsWindowCommand(item) => ("", WindowColour),
        _ => item.Kind switch
        {
            ItemKind.App => ("\uE71D", "#FF5E5CE6"),
            ItemKind.Quicklink => ("\uE71B", "#FF3B82F6"),
            ItemKind.Snippet => ("\uE70F", "#FFA66BFF"),
            ItemKind.Script => ("\uE756", "#FF22C55E"),
            ItemKind.Clipboard => ("\uE77F", "#FFF5A623"),
            ItemKind.File => ("\uE7C3", "#FF8E8E93"),
            ItemKind.Calculation => ("\uE8EF", "#FFFF9F0A"),
            ItemKind.Window => ("\uE7C4", "#FF8E8E93"),
            _ => ("\uE945", "#FFFF6363"),
        },
    };

    private const string WindowColour = "#FFEC4899";

    private static bool IsWindowCommand(SearchItem item) =>
        item.Kind == ItemKind.Command && item.Id.StartsWith("command:" + WindowCommands.IdPrefix, StringComparison.Ordinal);

    private static readonly Dictionary<WindowLayout, Geometry?> Shapes = [];

    /// <summary>
    /// The small picture on the tile of a command that puts the window somewhere: a screen,
    /// with the part of it the window takes filled in. It is 14 wide and 11 high. Null for
    /// any other item, and for the window commands that have a sign of their own.
    /// </summary>
    public static Geometry? Shape(SearchItem item)
    {
        if (!IsWindowCommand(item) || WindowCommands.LayoutOf(item.Id["command:".Length..]) is not { } layout)
        {
            return null;
        }

        if (Shapes.TryGetValue(layout, out var made))
        {
            return made;
        }

        // A screen of 1000 by 700 with a window in the middle of it, drawn at a hundredth.
        var screen = new ScreenRect(0, 0, 1000, 700);
        var window = new ScreenRect(300, 210, 400, 280);
        ScreenRect? place = layout switch
        {
            WindowLayout.Maximize => screen,
            WindowLayout.Restore or WindowLayout.NextDisplay or WindowLayout.PreviousDisplay => null,
            _ => WindowCommands.Place(layout, screen, window),
        };
        Geometry? shape = null;
        if (place is { } part)
        {
            // Filled where a point is inside an odd number of these: the rim of the screen,
            // not its inside, and then the part the window takes.
            var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
            group.Children.Add(new RectangleGeometry(new Rect(0, 0, 14, 11), 2.5, 2.5));
            group.Children.Add(new RectangleGeometry(new Rect(1, 1, 12, 9), 1.5, 1.5));
            group.Children.Add(new RectangleGeometry(new Rect(2 + (part.Left / 100.0), 2 + (part.Top / 100.0), part.Width / 100.0, part.Height / 100.0), 0.5, 0.5));
            group.Freeze();
            shape = group;
        }

        Shapes[layout] = shape;
        return shape;
    }

    /// <summary>The part of Wyvern a command belongs to, shown in grey after its name.</summary>
    public static string Part(SearchItem command) => command.Id switch
    {
        "command:pause-clipboard" or "command:resume-clipboard" or "command:clear-clipboard" => "Clipboard History",
        "command:lock-screen" or "command:sleep" or "command:hibernate" or "command:restart" or "command:shut-down" or "command:sign-out"
            or "command:empty-recycle-bin" or "command:open-recycle-bin" or "command:toggle-mute" or "command:volume-up" or "command:volume-down"
            or "command:set-volume" or "command:toggle-appearance" or "command:show-desktop" => "System",
        "command:media-play-pause" or "command:media-next" or "command:media-previous" => "Media",
        "command:check-updates" or "command:update-wyvern" => "Updates",
        "command:new-snippet" or "command:open-snippets" => "Snippets",
        "command:new-quicklink" => "Quicklinks",
        "command:edit-quicklinks" => "Quicklinks",
        "command:open-library" => "Library",
        "command:sync-now" or "command:sync-setup" or "command:show-sync-key" or "command:restore-missing-files" or "command:confirm-sync-deletions" or "command:create-sync-key" => "Sync",
        "command:switch-windows" => "Window Management",
        _ when IsWindowCommand(command) => "Window Management",
        _ => "Wyvern",
    };

    /// <summary>The part of Wyvern the command of this short name belongs to.</summary>
    public static string PartOfCommand(string id) => Part(new SearchItem("command:" + id, "", "", ItemKind.Command, null));

    /// <summary>What Enter does with an item, in a few words for the foot of the window.</summary>
    public static string Action(SearchItem item) => item.Kind switch
    {
        ItemKind.App => "Open Application",
        ItemKind.Quicklink => "Open Quicklink",
        ItemKind.Snippet => "Paste Snippet",
        ItemKind.Script => "Run Script",
        ItemKind.Clipboard => "Paste",
        ItemKind.File => "Open File",
        ItemKind.Calculation => "Copy Answer",
        ItemKind.Window => "Switch to Window",
        _ => "Open Command",
    };

    /// <summary>What Ctrl+Enter does with an item, or empty when it does nothing.</summary>
    public static string SecondAction(SearchItem item) => item.RunSecondary is null ? "" : item.Kind switch
    {
        ItemKind.File => "Show in Explorer",
        ItemKind.Snippet => "Open File in Editor",
        ItemKind.Script => "Edit Script",
        _ => "More",
    };
}

/// <summary>One line of what is said about an entry of the clipboard history, with a small picture for its value when there is one.</summary>
public sealed record InfoLine(string Label, string Value, ImageSource? Icon)
{
    /// <summary>The type's name only: the value can be the program the owner copied from.</summary>
    public override string ToString() => nameof(InfoLine);
}

/// <summary>
/// The programs things were copied from, by the name Windows has for them and with their
/// icons. An entry remembers its program by the name of its process; when a program of that
/// name is running, its file says what it is called and carries its icon.
/// </summary>
public static class SourcePrograms
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Name, ImageSource? Icon)> Known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The program's name and icon, or null when no program of that name is running or its file cannot be asked. Never throws.</summary>
    public static async Task<(string Name, ImageSource? Icon)?> Find(string process)
    {
        string name = process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? process[..^4] : process;
        if (Known.TryGetValue(name, out var known))
        {
            return known;
        }

        var found = await Task.Run(() => Ask(name));
        if (found is { } program)
        {
            Known[name] = program;
        }

        return found;
    }

    // On a pool thread. A process that belongs to another user, or runs with more rights, does
    // not say where its file is: the next of the same name is asked.
    private static (string Name, ImageSource? Icon)? Ask(string name)
    {
        try
        {
            foreach (var running in System.Diagnostics.Process.GetProcessesByName(name))
            {
                using (running)
                {
                    try
                    {
                        if (running.MainModule?.FileName is not { Length: > 0 } file)
                        {
                            continue;
                        }

                        string? called = System.Diagnostics.FileVersionInfo.GetVersionInfo(file).FileDescription?.Trim();
                        if (string.IsNullOrEmpty(called))
                        {
                            continue;
                        }

                        ImageSource? icon = null;
                        using (var packed = System.Drawing.Icon.ExtractAssociatedIcon(file))
                        {
                            if (packed is not null)
                            {
                                icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(packed.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                                icon.Freeze();
                            }
                        }

                        return (called, icon);
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or NotSupportedException or ArgumentException)
                    {
                        // Not this one.
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The list of processes could not be had.
        }

        return null;
    }
}

/// <summary>One thing that can be done with what is selected: the words for it, the keys that do it, and the doing.</summary>
public sealed record LauncherAction(string Text, string[] Keys, Action Run);

/// <summary>Groups the launcher's rows under the headings the view model gives them.</summary>
public sealed class SectionGrouping(LauncherViewModel viewModel) : GroupDescription
{
    public override object GroupNameFromItem(object item, int level, CultureInfo culture) =>
        item is SearchItem row ? viewModel.SectionOf(row) : "";
}

/// <summary>The word shown at the right of a row for what kind of thing it is.</summary>
public sealed class KindLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ItemKind.App => "Application",
        ItemKind.Command => "Command",
        ItemKind.Quicklink => "Quicklink",
        ItemKind.Snippet => "Snippet",
        ItemKind.Script => "Script",
        ItemKind.Clipboard => "Clipboard",
        ItemKind.File => "File",
        ItemKind.Calculation => "Calculator",
        ItemKind.Window => "Window",
        _ => "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The sign on the tile of an item that has no icon of its own.</summary>
public sealed class ItemGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SearchItem item ? LauncherLook.Tile(item).Glyph : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Lets XAML turn an emoji of the picker into its picture in colour.</summary>
public sealed class EmojiPictureConverter(Interop.EmojiFont font) : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is EmojiItem emoji ? font.Picture(emoji.Text) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The alias the owner has given an item, shown in a small frame after its name, or empty.</summary>
public sealed class ItemAliasConverter : IValueConverter
{
    /// <summary>Gives an item's alias. The launcher window sets it to its view model's; until then no item has one.</summary>
    public static Func<SearchItem, string> AliasOf { get; set; } = _ => "";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SearchItem item ? AliasOf(item) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The small picture on the tile of a command that puts the window somewhere, or nothing.</summary>
public sealed class ItemShapeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SearchItem item ? LauncherLook.Shape(item) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The colour of the tile of an item that has no icon of its own.</summary>
public sealed class ItemTileConverter : IValueConverter
{
    private static readonly Dictionary<string, Brush> Made = [];

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SearchItem item)
        {
            return null;
        }

        string colour = LauncherLook.Tile(item).Colour;
        if (!Made.TryGetValue(colour, out var brush))
        {
            brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colour));
            brush.Freeze();
            Made[colour] = brush;
        }

        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// The grey words after a row's title. An app has none: the word at the right says what it is.
/// A command says which part of Wyvern it belongs to. Anything else says what it was made with.
/// </summary>
public sealed class ItemSubtitleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        SearchItem { Kind: ItemKind.App } => "",
        SearchItem { Kind: ItemKind.Command } command => LauncherLook.Part(command),
        SearchItem item => item.Subtitle,
        _ => "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Shows the coloured tile for an item with no icon of its own, and for one whose icon has not
/// been read yet, so that no row stands without a picture in front of it.
/// </summary>
public sealed class TileVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string key
            && Application.Current?.TryFindResource("IconKeyConverter") is IconKeyConverter icons
            && icons.Convert(key, typeof(ImageSource), null, culture) is not null)
        {
            return Visibility.Collapsed;
        }

        return Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The sign in front of a row of the clipboard history, for what kind of entry it is.</summary>
public sealed class ClipGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ClipPreviewKind.Link => "\uE71B",
        ClipPreviewKind.Image => "\uEB9F",
        ClipPreviewKind.Files => "\uE8B7",
        _ => "\uE7C3",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Hides an element whose value is null.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// The small copy of a picture in front of its row in the clipboard history. Set
/// <c>Thumb.Path</c> on an Image and the picture's file is read away from the UI thread, at
/// the small size it is shown, and put there when it has come. A row that has scrolled away
/// meanwhile, and now stands for another entry, does not get it.
/// </summary>
public static class Thumb
{
    // The side of the place a small picture has, in pixels, for screens scaled up to double.
    private const int Side = 96;

    // How many small pictures are kept. They are a few kilobytes each.
    private const int MaxKept = 400;

    public static readonly DependencyProperty PathProperty = DependencyProperty.RegisterAttached(
        "Path", typeof(string), typeof(Thumb), new PropertyMetadata(null, OnPathChanged));

    public static readonly DependencyProperty SizeOfProperty = DependencyProperty.RegisterAttached(
        "SizeOf", typeof(ClipRow), typeof(Thumb), new PropertyMetadata(null, OnSizeOfChanged));

    private static readonly Dictionary<string, BitmapSource> Kept = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Width, int Height, long Bytes)?> Sizes = new(StringComparer.OrdinalIgnoreCase);
    private static ClipPictureLoader? reader;

    /// <summary>Says which reader the small pictures are read with: the one the preview uses, with its limits and its care.</summary>
    public static void Use(ClipPictureLoader pictures) => reader = pictures;

    public static string? GetPath(DependencyObject element) => (string?)element.GetValue(PathProperty);

    public static void SetPath(DependencyObject element, string? value) => element.SetValue(PathProperty, value);

    public static ClipRow? GetSizeOf(DependencyObject element) => (ClipRow?)element.GetValue(SizeOfProperty);

    public static void SetSizeOf(DependencyObject element, ClipRow? value) => element.SetValue(SizeOfProperty, value);

    /// <summary>
    /// How large a picture is, read away from the UI thread the first time and kept. Null when
    /// it cannot be told or no reader has been named.
    /// </summary>
    public static async Task<(int Width, int Height, long Bytes)?> Measure(string path)
    {
        if (Sizes.TryGetValue(path, out var known))
        {
            return known;
        }

        if (reader is not { } pictures)
        {
            return null;
        }

        var measured = await Task.Run(() => pictures.Measure(path));
        if (Sizes.Count >= MaxKept)
        {
            Sizes.Clear();
        }

        Sizes[path] = measured;
        return measured;
    }

    /// <summary>A picture's width and height as they are written: 1500\u00D7950.</summary>
    public static string Dimensions(int width, int height) =>
        width.ToString(CultureInfo.InvariantCulture) + "\u00D7" + height.ToString(CultureInfo.InvariantCulture);

    /// <summary>A file's size in the largest unit that keeps it above one: 540 KB, 1.2 MB.</summary>
    public static string FileSize(long bytes) =>
        bytes >= 1024 * 1024 ? (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " MB"
        : bytes >= 1024 ? (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB"
        : bytes.ToString(CultureInfo.InvariantCulture) + " bytes";

    // What stands after the title of a row of the clipboard history. A picture that has no
    // name of its own is called "Image"; when its size has been read, the size is said after it.
    private static async void OnSizeOfChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        try
        {
            if (element is not TextBlock text)
            {
                return;
            }

            text.Text = "";
            if (e.NewValue is not ClipRow { Unnamed: true, Picture: { } path } row)
            {
                return;
            }

            if (await Measure(path) is { } size)
            {
                await text.Dispatcher.InvokeAsync(() =>
                {
                    if (ReferenceEquals(GetSizeOf(text), row))
                    {
                        text.Text = " (" + Dimensions(size.Width, size.Height) + ")";
                    }
                });
            }
        }
        catch (Exception)
        {
            // The row stays as it was.
        }
    }

    // An async void method has nobody above it, and an exception that left it would end Wyvern.
    // So everything is inside the one try.
    private static async void OnPathChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        try
        {
            if (element is not Image image)
            {
                return;
            }

            image.Source = null;
            if (e.NewValue is not string path || reader is not { } pictures)
            {
                return;
            }

            if (Kept.TryGetValue(path, out var kept))
            {
                image.Source = kept;
                return;
            }

            BitmapSource? read = await Task.Run(() => pictures.Load(path, Side, Side));
            if (read is null)
            {
                return;
            }

            // On the picture's own thread, whichever thread the reading came back on. The row
            // may stand for another entry by now.
            await image.Dispatcher.InvokeAsync(() =>
            {
                if (Kept.Count >= MaxKept)
                {
                    Kept.Clear();
                }

                Kept[path] = read;
                if (GetPath(image) == path)
                {
                    image.Source = read;
                }
            });
        }
        catch (Exception)
        {
            // A small picture that cannot be read leaves the sign for its kind showing.
        }
    }
}
