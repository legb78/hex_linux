using System.Reflection;
using HexLinux.Configuration;
using HexLinux.Output;
using HexLinux.Platform;
using Xunit;

namespace HexLinux.Tests.Output;

/// <summary>
/// The command lines HexLinux runs. Two things matter here beyond each
/// tool's syntax (checked against its own <c>--help</c> in WSL): the
/// dictated text never appears among the arguments, which every local user
/// can read in <c>/proc/&lt;pid&gt;/cmdline</c>, and only the CLIPBOARD
/// selection is ever touched, never PRIMARY, which a mere mouse selection
/// fills.
/// </summary>
public class ToolCommandsTests
{
    private static readonly ClipboardTool[] ClipboardTools = [ClipboardTool.WlClipboard, ClipboardTool.Xclip, ClipboardTool.Xsel];

    private static void AssertCommand(ToolCommand command, string tool, params string[] arguments)
    {
        Assert.Equal(tool, command.Tool);
        Assert.Equal(arguments, command.Arguments);
    }

    /// <summary>Every command line the class can produce, for the rules that hold for all of them.</summary>
    private static IEnumerable<ToolCommand> AllCommands()
    {
        foreach (ClipboardTool tool in ClipboardTools)
        {
            string format = ToolCommands.TextFormat(tool);

            if (ToolCommands.ListFormats(tool) is { } list)
            {
                yield return list;
            }

            yield return ToolCommands.Read(tool, format);
            yield return ToolCommands.Write(tool, format);
            yield return ToolCommands.Clear(tool);
        }

        foreach (KeyStroker keys in new[] { KeyStroker.Xdotool, KeyStroker.Wtype })
        {
            yield return ToolCommands.TypeText(keys);
            yield return ToolCommands.EraseKeys(keys, 3);

            foreach (PasteShortcut shortcut in Enum.GetValues<PasteShortcut>())
            {
                yield return ToolCommands.PasteKeys(keys, shortcut);
            }
        }

        yield return ToolCommands.WtypeProbe();
        yield return ToolCommands.ShowSession("2");
        yield return ToolCommands.ShowUserDisplay(1000);
        yield return ToolCommands.ShowSeatActiveSession();
    }

    // --- Privacy ------------------------------------------------------------------

    [Fact]
    public void No_command_builder_can_receive_the_dictated_text()
    {
        // Structural guard for the privacy rule: the only strings a command
        // line is built from are a clipboard format and a logind session id.
        // A future "text" parameter would put the dictation in argv.
        MethodInfo[] builders = typeof(ToolCommands).GetMethods(BindingFlags.Public | BindingFlags.Static);

        Assert.NotEmpty(builders);

        foreach (MethodInfo builder in builders)
        {
            foreach (ParameterInfo parameter in builder.GetParameters().Where(p => p.ParameterType == typeof(string)))
            {
                Assert.Contains(parameter.Name ?? string.Empty, new[] { "format", "sessionId" });
            }
        }
    }

    [Fact]
    public void The_text_to_insert_travels_on_standard_input_and_never_in_argv()
    {
        // What TextInjector runs for one dictation, whatever the route: the
        // text given to it must be nowhere in the arguments.
        const string dictation = "my bank password is hunter2";

        foreach (ToolCommand command in AllCommands())
        {
            Assert.DoesNotContain(command.Arguments, argument => argument.Contains(dictation, StringComparison.Ordinal));
            Assert.DoesNotContain(dictation, command.ToString(), StringComparison.Ordinal);
        }

        // Both typing tools are told to read standard input ("-").
        Assert.Equal("-", ToolCommands.TypeText(KeyStroker.Xdotool).Arguments[^1]);
        Assert.Equal("-", ToolCommands.TypeText(KeyStroker.Wtype).Arguments[^1]);
    }

    [Fact]
    public void The_PRIMARY_selection_is_never_read_nor_written()
    {
        // E12: PRIMARY holds whatever the mouse last selected, which the user
        // never chose to share nor to lose.
        foreach (ToolCommand command in AllCommands())
        {
            Assert.DoesNotContain(command.Arguments, argument => argument.Contains("primary", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("-p", command.Arguments);
        }
    }

    [Fact]
    public void Every_xclip_and_xsel_command_names_the_CLIPBOARD_selection()
    {
        // Both default to PRIMARY when no selection is named.
        foreach (ToolCommand command in AllCommands().Where(c => c.Tool == ToolLocator.Xclip))
        {
            int selection = command.Arguments.ToList().IndexOf("-selection");

            Assert.True(selection >= 0, command.ToString());
            Assert.Equal("clipboard", command.Arguments[selection + 1]);
        }

        foreach (ToolCommand command in AllCommands().Where(c => c.Tool == ToolLocator.Xsel))
        {
            Assert.Contains("--clipboard", command.Arguments);
        }
    }

    [Fact]
    public void Every_command_names_a_tool_the_locator_looks_for()
    {
        // A tool missing from ToolLocator.Known is never found, so the
        // planner would never offer it and the injector could never run it.
        foreach (ToolCommand command in AllCommands())
        {
            Assert.Contains(command.Tool, ToolLocator.Known);
        }
    }

    // --- Clipboard ----------------------------------------------------------------

    [Fact]
    public void Dictated_text_is_written_as_UTF_8_text_in_each_vocabulary()
    {
        Assert.Equal("text/plain;charset=utf-8", ToolCommands.TextFormat(ClipboardTool.WlClipboard));
        Assert.Equal("UTF8_STRING", ToolCommands.TextFormat(ClipboardTool.Xclip));
        Assert.Equal("UTF8_STRING", ToolCommands.TextFormat(ClipboardTool.Xsel));
    }

    [Theory]
    [InlineData(ClipboardTool.WlClipboard)]
    [InlineData(ClipboardTool.Xclip)]
    public void The_format_HexLinux_writes_is_the_one_it_would_save_first(ClipboardTool tool)
    {
        // A snapshot taken while a dictation of ours is still in the
        // clipboard must pick our text format, not a lesser one.
        string written = ToolCommands.TextFormat(tool);

        Assert.Equal(written, ClipboardFormats.Preferred(["TEXT", "STRING", "text/plain", written]));
    }

    [Fact]
    public void Listing_formats_uses_wl_paste_or_the_xclip_TARGETS()
    {
        AssertCommand(ToolCommands.ListFormats(ClipboardTool.WlClipboard)!, ToolLocator.WlPaste, "--list-types");
        AssertCommand(ToolCommands.ListFormats(ClipboardTool.Xclip)!, ToolLocator.Xclip, "-selection", "clipboard", "-o", "-target", "TARGETS");
    }

    [Theory]
    [InlineData(ClipboardTool.Xsel)]
    [InlineData(ClipboardTool.None)]
    public void Xsel_has_no_format_list_since_it_only_knows_text(ClipboardTool tool)
    {
        Assert.Null(ToolCommands.ListFormats(tool));
    }

    [Fact]
    public void Reading_wl_paste_asks_for_no_trailing_newline()
    {
        // Without --no-newline wl-paste appends one to text, and every
        // restore would add a newline to what the user had copied.
        AssertCommand(ToolCommands.Read(ClipboardTool.WlClipboard, "image/png"), ToolLocator.WlPaste, "--no-newline", "--type", "image/png");
    }

    [Fact]
    public void Reading_xclip_and_xsel_uses_the_clipboard_selection()
    {
        AssertCommand(ToolCommands.Read(ClipboardTool.Xclip, "image/png"), ToolLocator.Xclip, "-selection", "clipboard", "-o", "-target", "image/png");
        AssertCommand(ToolCommands.Read(ClipboardTool.Xsel, "UTF8_STRING"), ToolLocator.Xsel, "--clipboard", "--output");
    }

    [Fact]
    public void Writing_takes_the_format_and_reads_the_content_from_standard_input()
    {
        AssertCommand(ToolCommands.Write(ClipboardTool.WlClipboard, "text/uri-list"), ToolLocator.WlCopy, "--type", "text/uri-list");
        AssertCommand(ToolCommands.Write(ClipboardTool.Xclip, "image/png"), ToolLocator.Xclip, "-selection", "clipboard", "-i", "-target", "image/png");
        AssertCommand(ToolCommands.Write(ClipboardTool.Xsel, "UTF8_STRING"), ToolLocator.Xsel, "--clipboard", "--input");
    }

    [Fact]
    public void Clearing_empties_the_clipboard_with_each_tool()
    {
        // xclip has no clear option: it is fed empty input as UTF-8 text,
        // which leaves nothing to paste either.
        AssertCommand(ToolCommands.Clear(ClipboardTool.WlClipboard), ToolLocator.WlCopy, "--clear");
        AssertCommand(ToolCommands.Clear(ClipboardTool.Xclip), ToolLocator.Xclip, "-selection", "clipboard", "-i", "-target", "UTF8_STRING");
        AssertCommand(ToolCommands.Clear(ClipboardTool.Xsel), ToolLocator.Xsel, "--clipboard", "--clear");
    }

    [Fact]
    public void Without_a_clipboard_tool_nothing_can_be_read_written_or_cleared()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ToolCommands.Read(ClipboardTool.None, "UTF8_STRING"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ToolCommands.Write(ClipboardTool.None, "UTF8_STRING"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ToolCommands.Clear(ClipboardTool.None));
    }

    // --- Keystrokes ---------------------------------------------------------------

    [Theory]
    [InlineData(PasteShortcut.CtrlV, "ctrl+v")]
    [InlineData(PasteShortcut.CtrlShiftV, "ctrl+shift+v")]
    [InlineData(PasteShortcut.ShiftInsert, "shift+Insert")]
    public void Xdotool_sends_the_paste_shortcut_with_the_held_modifiers_cleared(PasteShortcut shortcut, string chord)
    {
        // --clearmodifiers: a Super still held would otherwise turn Ctrl+V
        // into Super+Ctrl+V — the HexWin incident where a system panel took
        // the focus and the dictation vanished.
        AssertCommand(ToolCommands.PasteKeys(KeyStroker.Xdotool, shortcut), ToolLocator.Xdotool, "key", "--clearmodifiers", chord);
    }

    [Fact]
    public void Xdotool_types_from_standard_input_with_the_held_modifiers_cleared()
    {
        AssertCommand(ToolCommands.TypeText(KeyStroker.Xdotool), ToolLocator.Xdotool, "type", "--clearmodifiers", "--file", "-");
    }

    [Theory]
    [InlineData(PasteShortcut.CtrlV, new[] { "-M", "ctrl", "-k", "v", "-m", "ctrl" })]
    [InlineData(PasteShortcut.CtrlShiftV, new[] { "-M", "ctrl", "-M", "shift", "-k", "v", "-m", "shift", "-m", "ctrl" })]
    [InlineData(PasteShortcut.ShiftInsert, new[] { "-M", "shift", "-k", "Insert", "-m", "shift" })]
    public void Wtype_presses_the_modifiers_taps_the_key_and_releases_them(PasteShortcut shortcut, string[] arguments)
    {
        AssertCommand(ToolCommands.PasteKeys(KeyStroker.Wtype, shortcut), ToolLocator.Wtype, arguments);
    }

    [Theory]
    [InlineData(PasteShortcut.CtrlV)]
    [InlineData(PasteShortcut.CtrlShiftV)]
    [InlineData(PasteShortcut.ShiftInsert)]
    public void Wtype_releases_every_modifier_it_pressed_in_reverse_order(PasteShortcut shortcut)
    {
        // A modifier left down on wtype's virtual keyboard would turn the
        // user's next keystrokes into shortcuts.
        IReadOnlyList<string> arguments = ToolCommands.PasteKeys(KeyStroker.Wtype, shortcut).Arguments;
        List<string> pressed = [];
        List<string> released = [];

        for (int i = 0; i < arguments.Count - 1; i += 2)
        {
            if (arguments[i] == "-M")
            {
                pressed.Add(arguments[i + 1]);
            }
            else if (arguments[i] == "-m")
            {
                released.Add(arguments[i + 1]);
            }
        }

        pressed.Reverse();
        Assert.NotEmpty(released);
        Assert.Equal(pressed, released);
    }

    [Fact]
    public void Wtype_types_from_standard_input()
    {
        AssertCommand(ToolCommands.TypeText(KeyStroker.Wtype), ToolLocator.Wtype, "-");
    }

    [Fact]
    public void The_wtype_probe_types_nothing_from_standard_input()
    {
        // Run with empty input: it fails at once where the compositor lacks
        // the virtual-keyboard protocol (Weston: "Compositor does not support
        // the virtual keyboard protocol"), and types nothing where it works.
        AssertCommand(ToolCommands.WtypeProbe(), ToolLocator.Wtype, "-");
    }

    [Theory]
    [InlineData(KeyStroker.Uinput)]
    [InlineData(KeyStroker.None)]
    public void Only_xdotool_and_wtype_take_a_command_line(KeyStroker keys)
    {
        // The uinput keyboard takes key codes (KeySequences), and cannot type.
        Assert.Throws<ArgumentOutOfRangeException>(() => ToolCommands.PasteKeys(keys, PasteShortcut.CtrlV));
        Assert.Throws<ArgumentOutOfRangeException>(() => ToolCommands.TypeText(keys));
        Assert.Throws<ArgumentOutOfRangeException>(() => ToolCommands.EraseKeys(keys, 1));
    }

    // --- Spoken erase ---------------------------------------------------------------

    [Fact]
    public void Xdotool_erases_with_repeated_backspaces_and_the_held_modifiers_cleared()
    {
        // "xdotool key --repeat TIMES" (xdotool 3.20160805 --help). Without
        // --clearmodifiers, a Ctrl still held would make each one erase a
        // word — text the dictation never typed.
        AssertCommand(ToolCommands.EraseKeys(KeyStroker.Xdotool, 16), ToolLocator.Xdotool, "key", "--clearmodifiers", "--repeat", "16", "BackSpace");
    }

    [Fact]
    public void Wtype_erases_with_one_tap_per_character()
    {
        // wtype 0.4 has no repeat option: -k presses and releases one key.
        AssertCommand(ToolCommands.EraseKeys(KeyStroker.Wtype, 3), ToolLocator.Wtype, "-k", "BackSpace", "-k", "BackSpace", "-k", "BackSpace");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Erasing_nothing_is_not_a_command(int characters)
    {
        // A command that erases nothing has no reason to run: the injector
        // stops before it, and a count it did not mean is refused here.
        Assert.Throws<ArgumentOutOfRangeException>(() => ToolCommands.EraseKeys(KeyStroker.Xdotool, characters));
    }

    // --- Session ------------------------------------------------------------------

    [Fact]
    public void The_session_guard_asks_logind_for_the_properties_it_decides_on()
    {
        // Type, Seat and Remote tell a graphical session at a seat from an
        // SSH login, which logind always reports as active (E4).
        AssertCommand(
            ToolCommands.ShowSession("c2"),
            ToolLocator.Loginctl,
            "show-session",
            "c2",
            "--property=Active",
            "--property=LockedHint",
            "--property=Type",
            "--property=Seat",
            "--property=Remote",
            "--property=User");
    }

    [Fact]
    public void The_session_in_front_of_the_seat_is_asked_for_as_a_bare_value()
    {
        // SEC-03: when the user's own session is seatless (SSH, a tty), who
        // sits at the screen decides; an empty answer means nobody does.
        AssertCommand(ToolCommands.ShowSeatActiveSession(), ToolLocator.Loginctl, "show-seat", "seat0", "--property=ActiveSession", "--value");
    }

    [Fact]
    public void The_user_display_session_is_asked_for_as_a_bare_value()
    {
        // The fallback when XDG_SESSION_ID is unset (autostart through
        // systemd --user): logind names the user's graphical session.
        AssertCommand(ToolCommands.ShowUserDisplay(1000), ToolLocator.Loginctl, "show-user", "1000", "--property=Display", "--value");
        AssertCommand(ToolCommands.ShowUserDisplay(uint.MaxValue), ToolLocator.Loginctl, "show-user", "4294967295", "--property=Display", "--value");
    }

    // --- Display ------------------------------------------------------------------

    [Fact]
    public void A_command_prints_as_it_would_be_typed()
    {
        // What --doctor shows, so that the user can try the same line by hand.
        Assert.Equal("xdotool key --clearmodifiers ctrl+v", ToolCommands.PasteKeys(KeyStroker.Xdotool, PasteShortcut.CtrlV).ToString());
        Assert.Equal("wl-paste --list-types", ToolCommands.ListFormats(ClipboardTool.WlClipboard)!.ToString());
    }
}
