using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Beacon.Core.Sync;
using Xunit.Sdk;

namespace Beacon.Tests;

/// <summary>
/// The two sync windows cannot be shown by a test, and a binding to a property that does not
/// exist fails without a word when the window is shown. So the windows are read as text here:
/// every binding must name a property its view model really has, and the things about the
/// windows that keep a key off the clipboard and off the screen's pictures, and that keep the
/// owner from typing into a form that is being tested or saved, must be as they are.
/// </summary>
/// <remarks>
/// The markup is read as XML and the code without its comments, and a statement is looked for
/// inside the handler it belongs to: a sentence in a comment, or the right words in the wrong
/// place, must not pass. The last tests of the class are about these ways of looking themselves.
/// </remarks>
public class SyncWindowTests
{
    private const string CaptureSentence = "This window could not be kept out of screen captures. Do not share your screen while it is open.";

    // What the windows' own code must not hold. No file is read, no key is touched and nothing
    // is connected to from there, and nothing is put on the clipboard from there either: a key
    // is copied by the view model, through the writer that marks what it writes.
    private const string Forbidden = @"File\.|Directory\.|KeyStore|SyncConfig|SftpRemoteStore|IRemoteConnector|Task\.Run|\.Wait\(|\.Result\b|Clipboard";

    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    // The only forms of a binding these tests read: the name of a property, alone or with one
    // of the two options the windows use. A binding in any other form fails the test that meets it.
    private static readonly Regex BindingForm = new(
        @"^\{Binding (?<name>\w+)(?<options>(, (UpdateSourceTrigger=PropertyChanged|Converter=\{StaticResource VisibleWhenTrue\}))*)\}$");

    // One binding of a window: the element it is on, the attribute it sets, and the property it names.
    private sealed record Bound(XElement Element, string Attribute, string Property, string Options)
    {
        public string Kind => Element.Name.LocalName;
    }

    private static string AppFile(string name)
    {
        var folder = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "Beacon.slnx")))
        {
            folder = folder.Parent;
        }

        Assert.True(folder is not null, $"Beacon.slnx was not found above {AppContext.BaseDirectory}");
        return File.ReadAllText(Path.Combine(folder.FullName, "src", "Beacon.App", name)).ReplaceLineEndings("\n");
    }

    private static XDocument Markup(string window) => XDocument.Parse(AppFile(window + ".xaml"));

    private static string Code(string file) => WithoutComments(AppFile(file));

    // The code without its comments, so that a sentence in a comment cannot stand in for a
    // statement. What is inside a string is kept as it is: a "//" there is not a comment. The
    // files read here hold only plain strings; one that holds another kind fails the test.
    private static string WithoutComments(string code)
    {
        Assert.DoesNotContain("@\"", code);
        Assert.DoesNotContain("\"\"\"", code);
        Assert.DoesNotContain("'\"'", code);

        var kept = new StringBuilder(code.Length);
        int at = 0;
        while (at < code.Length)
        {
            if (code[at] == '"')
            {
                int end = EndOfString(code, at);
                kept.Append(code, at, end - at);
                at = end;
            }
            else if (string.CompareOrdinal(code, at, "//", 0, 2) == 0)
            {
                int end = code.IndexOf('\n', at);
                at = end < 0 ? code.Length : end;
            }
            else if (string.CompareOrdinal(code, at, "/*", 0, 2) == 0)
            {
                int end = code.IndexOf("*/", at + 2, StringComparison.Ordinal);
                at = end < 0 ? code.Length : end + 2;
            }
            else
            {
                kept.Append(code[at]);
                at++;
            }
        }

        return kept.ToString();
    }

    // The place after the string that opens at the given place.
    private static int EndOfString(string code, int open)
    {
        int at = open + 1;
        while (at < code.Length && code[at] != '"')
        {
            at += code[at] == '\\' ? 2 : 1;
        }

        return Math.Min(at + 1, code.Length);
    }

    // Where a piece of code is. It must be there exactly once.
    private static int Place(string code, string piece)
    {
        int first = code.IndexOf(piece, StringComparison.Ordinal);
        Assert.True(first >= 0, $"The code does not hold: {piece}");
        Assert.True(code.IndexOf(piece, first + piece.Length, StringComparison.Ordinal) < 0, $"The code holds this more than once: {piece}");
        return first;
    }

    // What is between the first brace at or after the given place and the brace that closes it.
    // The code must be without comments.
    private static string Block(string code, int from)
    {
        int open = code.IndexOf('{', from);
        Assert.True(open >= 0, "No block opens there");
        int depth = 0;
        int at = open;
        while (at < code.Length)
        {
            if (code[at] == '"')
            {
                at = EndOfString(code, at);
                continue;
            }

            if (code[at] == '{')
            {
                depth++;
            }
            else if (code[at] == '}' && --depth == 0)
            {
                return code[(open + 1)..at];
            }

            at++;
        }

        Assert.Fail("The block is not closed");
        return "";
    }

    // What the handler that the window's own code gives to one of its events does: the block of
    // the handler, or its one expression. Comments are not part of it.
    private static string HandlerBody(string code, string @event)
    {
        string plain = WithoutComments(code);
        var starts = Regex.Matches(plain, @"\b" + @event + @" \+= \(_, _\) =>\s*");
        Assert.True(starts.Count == 1, $"{@event} should be given one handler, and is given {starts.Count}");

        int after = starts[0].Index + starts[0].Length;
        if (plain[after] == '{')
        {
            return Block(plain, after);
        }

        int end = plain.IndexOf(';', after);
        Assert.True(end >= 0, $"The handler of {@event} does not end");
        return plain[after..(end + 1)];
    }

    // The statements must all be there, one after the other in this order.
    private static void AssertInOrder(string body, params string[] statements)
    {
        int from = 0;
        foreach (string statement in statements)
        {
            Assert.Contains(statement, body);
            int at = body.IndexOf(statement, from, StringComparison.Ordinal);
            Assert.True(at >= 0, $"{statement} comes too early: the order must be {string.Join(" ", statements)}");
            from = at + statement.Length;
        }
    }

    // Every binding of a window. A binding these tests cannot read fails here, rather than
    // being passed over without a word.
    private static List<Bound> Bindings(string xaml)
    {
        var found = new List<Bound>();
        foreach (var element in XDocument.Parse(xaml).Descendants())
        {
            Assert.False(
                element.Name.LocalName is "Binding" or "MultiBinding" or "PriorityBinding",
                $"A binding written as an element (<{element.Name.LocalName}>) is in a form these tests do not read");
            foreach (var attribute in element.Attributes().Where(attribute => Regex.IsMatch(attribute.Value, @"\{\s*(\w+:)?Binding\b")))
            {
                var match = BindingForm.Match(attribute.Value);
                Assert.True(match.Success, $"{attribute.Value} is a binding in a form these tests do not read");
                found.Add(new Bound(element, attribute.Name.LocalName, match.Groups["name"].Value, match.Groups["options"].Value));
            }
        }

        return found;
    }

    private static void AssertBindingsExist(string xaml, Type viewModel, int atLeast)
    {
        var bindings = Bindings(xaml);
        Assert.True(bindings.Count >= atLeast, $"only {bindings.Count} bindings were found");
        foreach (var bound in bindings)
        {
            var property = viewModel.GetProperty(bound.Property, BindingFlags.Instance | BindingFlags.Public);
            Assert.True(property is not null && property.GetMethod is { IsPublic: true }, $"{viewModel.Name} has no property {bound.Property} to bind to");

            // What the owner types or ticks must be able to reach the view model.
            bool edited = (bound.Kind, bound.Attribute) is ("TextBox", "Text") or ("RadioButton", "IsChecked") or ("CheckBox", "IsChecked");
            Assert.True(!edited || property.SetMethod is { IsPublic: true }, $"{viewModel.Name}.{bound.Property} is edited in the window and cannot be set");

            // A true-or-false shows or hides something only through the converter. Without it the
            // binding fails without a word, and the thing is shown always.
            bool converted = bound.Options.Contains("Converter=", StringComparison.Ordinal);
            Assert.True(converted == (bound.Attribute == "Visibility"), $"{bound.Kind}.{bound.Attribute} is bound to {bound.Property} with the converter where it should not be, or without it where it should be");
        }
    }

    // The one element of a window that binds this attribute to this property.
    private static XElement Control(XDocument window, string attribute, string property) =>
        Assert.Single(
            window.Descendants(),
            element => element.Attribute(attribute)?.Value is { } value && BindingForm.Match(value) is { Success: true } match && match.Groups["name"].Value == property);

    // The one button of the setup window that calls this handler.
    private static XElement Button(string handler) =>
        Assert.Single(Markup("SyncSetupWindow").Descendants(Wpf + "Button"), button => button.Attribute("Click")?.Value == handler);

    private static XElement TypedKeyBox() => Control(Markup("SyncSetupWindow"), "Text", "EnteredKey");

    // What an element's being enabled rests on: its own IsEnabled, and that of everything it
    // is inside, nearest first. An element is off when anything it is inside is off.
    private static List<string> EnabledBy(XElement element) =>
        [.. element.AncestorsAndSelf().Select(ancestor => ancestor.Attribute("IsEnabled")?.Value).OfType<string>()];

    private static void AssertProtectsForTheUserOnly(string code)
    {
        Assert.Contains("ProtectedData.Protect(secret, Purpose, DataProtectionScope.CurrentUser)", code);
        Assert.Contains("ProtectedData.Unprotect(protectedBytes, Purpose, DataProtectionScope.CurrentUser)", code);
        Assert.DoesNotContain("LocalMachine", code);
    }

    // The purpose is these bytes, UTF-8 and nothing else, and it is what both calls are given.
    private static void AssertKeepsItsPurpose(string code)
    {
        Assert.Contains("private static readonly byte[] Purpose = \"Beacon sync secret v1\"u8.ToArray();", code);
        Assert.Contains("ProtectedData.Protect(secret, Purpose, DataProtectionScope.CurrentUser)", code);
        Assert.Contains("ProtectedData.Unprotect(protectedBytes, Purpose, DataProtectionScope.CurrentUser)", code);
    }

    private static void AssertExcludesFromCapture(string code)
    {
        Assert.Contains("const uint ExcludeFromCapture = 0x00000011;", code);
        Assert.Contains("SetWindowDisplayAffinity(handle, ExcludeFromCapture)", code);
    }

    private static void AssertAsksToBeKeptOutOfCaptures(string code) =>
        Assert.Contains("ScreenCapture.Exclude(this)", HandlerBody(code, "SourceInitialized"));

    // ---- The bindings ----

    [Fact]
    public void Every_binding_of_the_setup_window_names_a_property_of_its_view_model()
    {
        AssertBindingsExist(AppFile("SyncSetupWindow.xaml"), typeof(SyncSetupViewModel), atLeast: 26);
    }

    [Fact]
    public void Every_binding_of_the_key_window_names_a_property_of_its_view_model()
    {
        AssertBindingsExist(AppFile("SyncKeyWindow.xaml"), typeof(SyncKeyViewModel), atLeast: 4);
    }

    [Fact]
    public void Every_field_the_owner_fills_in_is_bound_in_the_setup_window()
    {
        var bound = Bindings(AppFile("SyncSetupWindow.xaml")).Select(binding => binding.Property).ToHashSet();

        foreach (string field in new[]
        {
            "Host", "Port", "User", "KeyFile", "RemoteDir", "KeepKey", "GenerateKey", "EnterKey", "EnteredKey", "CanKeep",
            "NeedsPassphrase", "CanTest", "Fingerprint", "FingerprintKind", "FingerprintShown", "FingerprintConfirmed",
            "Message", "GeneratedKey", "HasGeneratedKey", "CanSave", "CanEdit", "IsBusy",
        })
        {
            Assert.Contains(field, bound);
        }
    }

    // ---- A key that Beacon shows ----

    [Fact]
    public void A_key_is_only_ever_shown_as_text_that_cannot_be_selected()
    {
        // A text box lets the owner select and copy with Ctrl+C, which writes to the clipboard
        // without the marks that keep a key out of clipboard histories. The one text box that
        // holds a key is the one a key is typed into: the tests after this one are about it.
        string setup = AppFile("SyncSetupWindow.xaml");
        string key = AppFile("SyncKeyWindow.xaml");

        Assert.Matches(@"<TextBlock[^>]*\{Binding GeneratedKey\}", setup);
        Assert.DoesNotMatch(@"<TextBox[^>]*\{Binding GeneratedKey", setup);
        Assert.Matches(@"<TextBlock[^>]*\{Binding KeyText\}", key);
        Assert.DoesNotContain("<TextBox", key);
        Assert.DoesNotMatch(@"IsReadOnly|SelectableText|RichTextBox", setup + key);
    }

    // ---- A key that is typed in: nothing leaves its box ----

    [Fact]
    public void The_typed_key_is_in_a_text_box_that_has_a_name()
    {
        var box = TypedKeyBox();

        Assert.Equal("TextBox", box.Name.LocalName);
        Assert.Equal("EnteredKeyBox", box.Attribute(Xaml + "Name")?.Value);
    }

    [Fact]
    public void The_typed_key_box_has_no_menu_that_offers_copy_or_cut()
    {
        Assert.Equal("{x:Null}", TypedKeyBox().Attribute("ContextMenu")?.Value);

        // And nothing else in the window gives it one.
        Assert.DoesNotContain(Markup("SyncSetupWindow").Descendants(), element => element.Name.LocalName.Contains("ContextMenu", StringComparison.Ordinal));
    }

    [Fact]
    public void The_typed_key_box_keeps_no_history_of_what_was_typed()
    {
        Assert.Equal("False", TypedKeyBox().Attribute("IsUndoEnabled")?.Value);
    }

    [Fact]
    public void Copy_cut_and_a_drag_out_of_the_typed_key_box_are_called_off()
    {
        string code = Code("SyncSetupWindow.xaml.cs");
        string constructor = Block(code, Place(code, "public SyncSetupWindow("));

        Assert.Matches(@"DataObject\.AddCopyingHandler\(EnteredKeyBox, \(_, (\w+)\) => \1\.CancelCommand\(\)\);", constructor);

        // It is done when the window is made, and for good: not in a handler that runs later or
        // never, and it is not taken off again.
        Assert.DoesNotContain("AddCopyingHandler", HandlerBody(code, "SourceInitialized"));
        Assert.DoesNotContain("AddCopyingHandler", HandlerBody(code, "Closed"));
        Assert.DoesNotContain("RemoveCopyingHandler", code);

        // Pasting into the box is left alone: a key may be pasted there.
        Assert.DoesNotContain("AddPastingHandler", code);
    }

    // ---- What is let go of when a window closes ----

    [Fact]
    public void The_setup_window_lets_go_of_what_it_shows_when_it_closes_the_view_model_first()
    {
        string closed = HandlerBody(AppFile("SyncSetupWindow.xaml.cs"), "Closed");

        AssertInOrder(closed, "viewModel.Clear();", "PassphraseBox.Clear();", "EnteredKeyBox.Clear();", "DataContext = null;");
    }

    [Fact]
    public void The_key_window_lets_go_of_what_it_shows_when_it_closes_the_view_model_first()
    {
        string closed = HandlerBody(AppFile("SyncKeyWindow.xaml.cs"), "Closed");

        AssertInOrder(closed, "viewModel.Clear();", "DataContext = null;");
    }

    [Fact]
    public void The_passphrase_is_typed_into_a_password_box_and_handed_over_as_it_changes()
    {
        string setup = AppFile("SyncSetupWindow.xaml");
        string code = Code("SyncSetupWindow.xaml.cs");

        Assert.Matches(@"<PasswordBox x:Name=""PassphraseBox""[^>]*PasswordChanged=""OnPassphraseChanged""", setup);
        Assert.DoesNotMatch(@"\{Binding Passphrase", setup);
        Assert.Contains("_viewModel.Passphrase = PassphraseBox.Password;", code);
        Assert.Contains("PassphraseBox.Clear();", code);
    }

    // ---- No input while a check or the saving is under way, or once it is saved ----

    [Theory]
    [InlineData("TextBox", "Text", "Host")]
    [InlineData("TextBox", "Text", "Port")]
    [InlineData("TextBox", "Text", "User")]
    [InlineData("TextBox", "Text", "KeyFile")]
    [InlineData("TextBox", "Text", "RemoteDir")]
    [InlineData("RadioButton", "IsChecked", "KeepKey")]
    [InlineData("RadioButton", "IsChecked", "GenerateKey")]
    [InlineData("RadioButton", "IsChecked", "EnterKey")]
    [InlineData("TextBox", "Text", "EnteredKey")]
    [InlineData("CheckBox", "IsChecked", "FingerprintConfirmed")]
    public void A_field_of_the_setup_window_takes_input_only_while_the_form_can_be_edited(string kind, string attribute, string property)
    {
        var control = Control(Markup("SyncSetupWindow"), attribute, property);

        Assert.Equal(kind, control.Name.LocalName);
        Assert.Contains("{Binding CanEdit}", EnabledBy(control));
    }

    [Fact]
    public void The_password_box_takes_input_only_while_the_form_can_be_edited()
    {
        // It cannot be bound back, so what is typed into it during a check would be dropped by
        // the view model and still be shown: only its being off prevents that.
        var box = Assert.Single(Markup("SyncSetupWindow").Descendants(Wpf + "PasswordBox"));

        Assert.Equal("PassphraseBox", box.Attribute(Xaml + "Name")?.Value);
        Assert.Contains("{Binding CanEdit}", EnabledBy(box));
    }

    [Fact]
    public void The_browse_button_can_be_pressed_only_while_the_form_can_be_edited()
    {
        // Its handler sets the key file, which the view model would drop without a word meanwhile.
        Assert.Contains("{Binding CanEdit}", EnabledBy(Button("OnBrowse")));
    }

    [Fact]
    public void The_typed_key_box_takes_input_only_for_the_enter_choice_and_only_while_the_form_can_be_edited()
    {
        var box = TypedKeyBox();

        // Both must hold: the box's own rule, and the rule of what the box is inside.
        Assert.Equal("{Binding EnterKey}", box.Attribute("IsEnabled")?.Value);
        Assert.Contains("{Binding CanEdit}", EnabledBy(box.Parent!));
    }

    [Fact]
    public void No_control_of_the_setup_window_that_takes_input_is_left_out_of_the_rule()
    {
        var inputs = Markup("SyncSetupWindow").Descendants()
            .Where(element => element.Name.LocalName is "TextBox" or "PasswordBox" or "RadioButton" or "CheckBox" or "ComboBox" or "ListBox" or "Slider" or "ToggleButton" or "RichTextBox" or "DatePicker")
            .ToList();

        Assert.NotEmpty(inputs);
        Assert.All(inputs, input => Assert.Contains("{Binding CanEdit}", EnabledBy(input)));
    }

    [Fact]
    public void The_buttons_keep_their_own_rules_and_Copy_and_Close_can_always_be_pressed()
    {
        Assert.Equal(new[] { "{Binding CanTest}" }, EnabledBy(Button("OnTest")));
        Assert.Equal(new[] { "{Binding CanSave}" }, EnabledBy(Button("OnSave")));

        // Copy must work after a save, when nothing can be edited any more. Close works always.
        Assert.Empty(EnabledBy(Button("OnCopyKey")));
        Assert.Empty(EnabledBy(Button("OnClose")));

        // With Browse, those are all the buttons there are.
        Assert.Equal(5, Markup("SyncSetupWindow").Descendants(Wpf + "Button").Count());
    }

    [Fact]
    public void The_setup_window_says_that_it_is_working_while_a_check_or_the_saving_is_under_way()
    {
        var working = Assert.Single(Markup("SyncSetupWindow").Descendants(Wpf + "TextBlock"), block => block.Attribute("Text")?.Value == "Working...");

        Assert.Equal("{Binding IsBusy, Converter={StaticResource VisibleWhenTrue}}", working.Attribute("Visibility")?.Value);

        // Nothing it is inside hides it for a reason of its own.
        Assert.DoesNotContain(working.Ancestors(), ancestor => ancestor.Attribute("Visibility") is not null);
        Assert.Single(Markup("SyncSetupWindow").Descendants(Wpf + "BooleanToVisibilityConverter"), converter => converter.Attribute(Xaml + "Key")?.Value == "VisibleWhenTrue");
    }

    // ---- Screen captures ----

    [Theory]
    [InlineData("SyncSetupWindow")]
    [InlineData("SyncKeyWindow")]
    public void Each_window_asks_to_be_kept_out_of_screen_captures_once_it_has_a_handle(string window)
    {
        AssertAsksToBeKeptOutOfCaptures(AppFile(window + ".xaml.cs"));
    }

    [Theory]
    [InlineData("SyncSetupWindow")]
    [InlineData("SyncKeyWindow")]
    public void A_window_that_could_not_be_kept_out_of_captures_says_so_in_the_window_and_in_the_log(string window)
    {
        var warning = Assert.Single(Markup(window).Descendants(Wpf + "TextBlock"), block => block.Attribute("Text")?.Value == CaptureSentence);
        string? name = warning.Attribute(Xaml + "Name")?.Value;

        // Not shown until Windows has refused, and then nothing it is inside hides it.
        Assert.False(string.IsNullOrEmpty(name), "the sentence has no name for the code to show it by");
        Assert.Equal("Collapsed", warning.Attribute("Visibility")?.Value);
        Assert.DoesNotContain(warning.Ancestors(), ancestor => ancestor.Attribute("Visibility") is not null);

        // It is shown where Windows refused, and nowhere else. The log is still told.
        string code = Code(window + ".xaml.cs");
        string handler = HandlerBody(code, "SourceInitialized");
        var refused = Regex.Match(handler, @"if \(!ScreenCapture\.Exclude\(this\)\)\s*\{(?<then>[^{}]*)\}");
        Assert.True(refused.Success, "the handler does not look at what the exclusion answered");
        Assert.Contains(name + ".Visibility = Visibility.Visible;", refused.Groups["then"].Value);
        Assert.Matches(@"report\(""The Sync (Setup|Key) window could not be kept out of screen captures"", null\);", refused.Groups["then"].Value);
        Assert.Single(Regex.Matches(code, name + @"\b"));
    }

    [Fact]
    public void The_capture_exclusion_passes_the_value_that_Windows_documents()
    {
        AssertExcludesFromCapture(Code("Interop/ScreenCapture.cs"));
    }

    // ---- The key file dialog ----

    [Fact]
    public void The_key_file_dialog_keeps_the_key_file_out_of_the_recent_files()
    {
        string code = Code("SyncSetupWindow.xaml.cs");
        string browse = Block(code, Place(code, "private void OnBrowse("));

        Assert.Matches(@"new OpenFileDialog\s*\{[^{}]*\bAddToRecent = false,[^{}]*\}", browse);
    }

    [Fact]
    public void A_key_file_dialog_that_fails_is_told_to_the_log_and_leaves_the_field_as_it_was()
    {
        string code = Code("SyncSetupWindow.xaml.cs");
        string browse = Block(code, Place(code, "private void OnBrowse("));

        var caught = Regex.Match(browse, @"try\s*\{[^{}]*dialog\.ShowDialog\(this\)[^{}]*\}\s*catch \(Exception (?<error>\w+)\)\s*\{(?<then>[^{}]*)\}");
        Assert.True(caught.Success, "ShowDialog is not inside a try that catches whatever it throws");

        // Fixed words and the exception, and then nothing more: no message, and the field is not set.
        Assert.Matches(@"^\s*_report\(""[^""]+"", " + caught.Groups["error"].Value + @"\);\s*return;\s*$", caught.Groups["then"].Value);
        Assert.DoesNotContain("MessageBox", code);
        Assert.Single(Regex.Matches(browse, @"\.KeyFile = "));
    }

    // ---- What the windows leave to others ----

    [Theory]
    [InlineData("SyncSetupWindow.xaml.cs")]
    [InlineData("SyncKeyWindow.xaml.cs")]
    [InlineData("SyncSetupWindow.xaml")]
    [InlineData("SyncKeyWindow.xaml")]
    public void The_windows_decide_nothing_themselves(string file)
    {
        // Comments and all: the word alone fails it.
        Assert.DoesNotMatch(Forbidden, AppFile(file));
    }

    [Fact]
    public void The_key_protector_protects_for_the_Windows_user_and_not_for_the_machine()
    {
        AssertProtectsForTheUserOnly(Code("Interop/DpapiKeyProtector.cs"));
    }

    [Fact]
    public void The_purpose_the_key_protector_names_is_the_one_every_key_and_passphrase_was_protected_with()
    {
        // What is protected with one purpose cannot be read back with another. Were this text
        // changed, no machine could read its sync key or its saved passphrase any more.
        AssertKeepsItsPurpose(Code("Interop/DpapiKeyProtector.cs"));
    }

    [Fact]
    public void The_pin_of_the_protectors_purpose_fails_on_a_protector_that_names_another()
    {
        // The purpose was right before it was pinned, so the pin has not been seen to fail on
        // the file itself. Here it is seen to fail on the file with the purpose put otherwise.
        string protector = Code("Interop/DpapiKeyProtector.cs");
        string otherVersion = Changed(protector, "\"Beacon sync secret v1\"u8", "\"Beacon sync secret v2\"u8");
        string otherCase = Changed(protector, "\"Beacon sync secret v1\"u8", "\"beacon sync secret v1\"u8");
        string otherEncoding = Changed(protector, "\"Beacon sync secret v1\"u8.ToArray()", "System.Text.Encoding.Unicode.GetBytes(\"Beacon sync secret v1\")");
        string none = Changed(protector, "ProtectedData.Unprotect(protectedBytes, Purpose, DataProtectionScope.CurrentUser)", "ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser)");
        string another = Changed(
            protector,
            "ProtectedData.Protect(secret, Purpose, DataProtectionScope.CurrentUser)",
            "ProtectedData.Protect(secret, Other, DataProtectionScope.CurrentUser)")
            + "\n// elsewhere\nstatic void Elsewhere() { _ = Purpose; }\n";
        Assert.Throws<ContainsException>(() => AssertKeepsItsPurpose(otherVersion));
        Assert.Throws<ContainsException>(() => AssertKeepsItsPurpose(otherCase));
        Assert.Throws<ContainsException>(() => AssertKeepsItsPurpose(otherEncoding));
        Assert.Throws<ContainsException>(() => AssertKeepsItsPurpose(none));
        Assert.Throws<ContainsException>(() => AssertKeepsItsPurpose(another));
    }

    // ---- The ways of looking themselves ----

    private static string Sample(string inner) =>
        $"""<StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">{inner}</StackPanel>""";

    // The code with one piece of it put otherwise. The piece must be there.
    private static string Changed(string code, string piece, string otherwise)
    {
        Assert.Contains(piece, code);
        return code.Replace(piece, otherwise, StringComparison.Ordinal);
    }

    [Fact]
    public void The_forbidden_words_catch_a_clipboard_call_and_let_the_copying_handler_be()
    {
        Assert.Matches(Forbidden, "Clipboard.SetText(key);");
        Assert.Matches(Forbidden, "System.Windows.Clipboard.SetDataObject(data, true);");
        Assert.Matches(Forbidden, "private readonly IClipboard _clipboard;");
        Assert.DoesNotMatch(Forbidden, "DataObject.AddCopyingHandler(EnteredKeyBox, (_, e) => e.CancelCommand());");
    }

    [Theory]
    [InlineData(@"<TextBox Text=""{Binding Path=Host}"" />")]
    [InlineData(@"<TextBox Text=""{Binding Path=Nothing.Here, Mode=TwoWay}"" />")]
    [InlineData(@"<TextBox Text=""{Binding Host.Length}"" />")]
    [InlineData(@"<TextBox Text=""{Binding}"" />")]
    [InlineData(@"<TextBox Text=""{ Binding Nothing }"" />")]
    [InlineData(@"<TextBox x:Name=""Other"" /><TextBox Text=""{Binding Host, ElementName=Other}"" />")]
    [InlineData(@"<TextBox Text=""{Binding Host, Mode=OneWay}"" />")]
    [InlineData(@"<TextBlock><TextBlock.Text><Binding Path=""Nothing"" /></TextBlock.Text></TextBlock>")]
    public void A_binding_written_in_a_form_these_tests_do_not_read_fails_them(string inner)
    {
        var failure = Assert.ThrowsAny<XunitException>(() => Bindings(Sample(inner)));

        Assert.Contains("in a form these tests do not read", failure.Message);
    }

    [Fact]
    public void A_text_box_that_edits_a_property_that_cannot_be_set_fails_even_when_a_text_block_shows_the_same_property()
    {
        string xaml = Sample(@"<TextBlock Text=""{Binding Message}"" /><TextBox Text=""{Binding Message}"" />");

        var failure = Assert.ThrowsAny<XunitException>(() => AssertBindingsExist(xaml, typeof(SyncSetupViewModel), atLeast: 2));

        Assert.Contains("SyncSetupViewModel.Message is edited in the window and cannot be set", failure.Message);
    }

    [Theory]
    [InlineData(@"<TextBlock Text=""{Binding Nothing}"" />", "SyncSetupViewModel has no property Nothing to bind to")]
    [InlineData(@"<TextBlock Text=""Working..."" Visibility=""{Binding IsBusy}"" />", "TextBlock.Visibility is bound to IsBusy with the converter where it should not be, or without it where it should be")]
    [InlineData(@"<Button IsEnabled=""{Binding CanTest, Converter={StaticResource VisibleWhenTrue}}"" />", "Button.IsEnabled is bound to CanTest with the converter where it should not be, or without it where it should be")]
    public void A_binding_that_would_fail_without_a_word_in_the_window_fails_the_check_of_the_bindings(string inner, string why)
    {
        var failure = Assert.ThrowsAny<XunitException>(() => AssertBindingsExist(Sample(inner), typeof(SyncSetupViewModel), atLeast: 1));

        Assert.Contains(why, failure.Message);
    }

    [Fact]
    public void Only_what_is_inside_a_handlers_body_counts_as_being_in_it()
    {
        const string code = """
            // Closed must call viewModel.Clear(); and then DataContext = null;
            public Sample()
            {
                SourceInitialized += (_, _) =>
                {
                    if (!ScreenCapture.Exclude(this))
                    {
                        report("a brace { in words, and // no comment", null);
                    }
                };

                Closed += (_, _) =>
                {
                    // viewModel.Clear();
                    /* DataContext = null; */
                    Box.Clear();
                };
                Loaded += (_, _) => Box.Focus();
            }

            private void Other() => DataContext = null;
            """;

        string closed = HandlerBody(code, "Closed");
        Assert.Contains("Box.Clear();", closed);
        Assert.DoesNotContain("viewModel.Clear();", closed);
        Assert.DoesNotContain("DataContext = null;", closed);
        Assert.DoesNotContain("ScreenCapture.Exclude(this)", closed);

        string initialized = HandlerBody(code, "SourceInitialized");
        Assert.Contains("ScreenCapture.Exclude(this)", initialized);
        Assert.Contains("report(\"a brace { in words, and // no comment\", null);", initialized);
        Assert.DoesNotContain("Box.Clear();", initialized);

        // A handler written as one expression is that expression.
        Assert.Equal("Box.Focus();", HandlerBody(code, "Loaded"));
    }

    [Fact]
    public void Statements_in_another_order_or_with_one_missing_do_not_pass_as_being_in_order()
    {
        AssertInOrder("a(); b(); c();", "a();", "b();", "c();");

        var disordered = Assert.Throws<TrueException>(() => AssertInOrder("b(); a(); c();", "a();", "b();", "c();"));
        Assert.Contains("b(); comes too early", disordered.Message);
        Assert.Throws<ContainsException>(() => AssertInOrder("a(); c();", "a();", "b();", "c();"));
    }

    [Fact]
    public void The_pins_of_what_this_round_did_not_change_fail_on_code_that_says_otherwise()
    {
        // These three rules held before they were pinned, so no test of them has been seen to
        // fail on the files themselves. Here each is seen to fail on the file with the rule broken.
        string protector = Code("Interop/DpapiKeyProtector.cs");
        string machine = Changed(protector, "DataProtectionScope.CurrentUser", "DataProtectionScope.LocalMachine");
        string readsForTheMachine = Changed(
            protector,
            "ProtectedData.Unprotect(protectedBytes, Purpose, DataProtectionScope.CurrentUser)",
            "ProtectedData.Unprotect(protectedBytes, Purpose, DataProtectionScope.LocalMachine)");
        Assert.Throws<ContainsException>(() => AssertProtectsForTheUserOnly(machine));
        Assert.Throws<ContainsException>(() => AssertProtectsForTheUserOnly(readsForTheMachine));

        string capture = Code("Interop/ScreenCapture.cs");
        string shownOnTheMonitorOnly = Changed(capture, "0x00000011", "0x00000001");
        string notPassed = Changed(capture, "SetWindowDisplayAffinity(handle, ExcludeFromCapture)", "SetWindowDisplayAffinity(handle, 0)");
        Assert.Throws<ContainsException>(() => AssertExcludesFromCapture(shownOnTheMonitorOnly));
        Assert.Throws<ContainsException>(() => AssertExcludesFromCapture(notPassed));

        foreach (string file in new[] { "SyncSetupWindow.xaml.cs", "SyncKeyWindow.xaml.cs" })
        {
            // The call gone from the handler, with a comment that names it left behind there
            // and the call itself made somewhere else.
            string movedOut = Changed(AppFile(file), "if (!ScreenCapture.Exclude(this))", "// ScreenCapture.Exclude(this)\n            if (false)")
                + "\n// elsewhere\nstatic void Elsewhere(Window window) { ScreenCapture.Exclude(this); }\n";
            Assert.Throws<ContainsException>(() => AssertAsksToBeKeptOutOfCaptures(movedOut));
        }
    }
}
