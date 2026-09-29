using HexLinux.Configuration;
using HexLinux.Output;
using HexLinux.Platform;
using HexLinux.Tests.Platform;
using Xunit;

namespace HexLinux.Tests.Output;

/// <summary>
/// The insertion shells — <see cref="Clipboard"/> and <see cref="TextInjector"/>
/// — driven end to end against a fake xclip and a fake xdotool that keep a
/// clipboard in a file and record what they are asked (RV-18).
///
/// <para>What is at stake is what the user finds afterwards: the text pasted,
/// their own clipboard given back, a password manager's secret never
/// republished without its mark (SEC-01), nothing pasted into a screen that
/// locked meanwhile (SEC-05), and the dictation in no process's
/// arguments.</para>
/// </summary>
[Trait("Category", "Shell")]
public sealed class TextInjectorTests : IDisposable
{
    private const string Dictated = "Dictée à insérer, 100 % locale.";

    private static readonly InjectionPlan PasteWithXclip =
        new(InsertionMode.Paste, InjectionOutcome.Insert, ClipboardTool.Xclip, KeyStroker.Xdotool, null);

    private static readonly InjectionPlan TypeWithXdotool =
        new(InsertionMode.Type, InjectionOutcome.Insert, ClipboardTool.None, KeyStroker.Xdotool, null);

    private readonly FakeToolFolder _fake = new();
    private readonly Dictionary<string, string> _tools = new(StringComparer.Ordinal);
    private readonly List<string> _log = [];

    public TextInjectorTests()
    {
        // xclip 0.13 as ToolCommands drives it: -o -target TARGETS lists,
        // -o -target T reads, -i -target T writes stdin and forks a child
        // that serves the selection.
        _tools[ToolLocator.Xclip] = _fake.Add(
            "xclip",
            """
            { for a in "$@"; do printf '%s\n' "$a"; done; echo ---; } >> "$DIR/xclip.args"
            for last; do :; done
            case " $* " in
              *" -o -target TARGETS "*) cat "$DIR/offered" ;;
              *" -o "*) cat "$DIR/content" ;;
              *" -i "*) cat > "$DIR/clipboard"; printf '%s' "$last" > "$DIR/clipboard.type"; sleep 1 & ;;
            esac
            exit 0
            """);

        // xdotool: the paste reads the clipboard into "pasted", typing reads
        // standard input into "typed".
        _tools[ToolLocator.Xdotool] = _fake.Add(
            "xdotool",
            """
            { for a in "$@"; do printf '%s\n' "$a"; done; echo ---; } >> "$DIR/xdotool.args"
            case "$1" in
              key) cat "$DIR/clipboard" > "$DIR/pasted" ;;
              type) cat > "$DIR/typed" ;;
            esac
            exit 0
            """);

        _fake.Write("offered", "TARGETS\nTIMESTAMP\nUTF8_STRING\n");
        _fake.Write("content", "what the user had copied");
        _fake.Write("clipboard", "what the user had copied");
    }

    public void Dispose() => _fake.Dispose();

    [Fact]
    public async Task A_paste_inserts_the_text_and_gives_the_user_s_clipboard_back()
    {
        InsertionResult result = await Injector().InsertAsync(Dictated, PasteWithXclip, PasteShortcut.CtrlV, _tools);

        Assert.Equal(InsertionStatus.Inserted, result.Status);
        Assert.Equal(Dictated, _fake.Read("pasted"));
        Assert.Equal("what the user had copied", _fake.Read("clipboard"));
        Assert.Equal("UTF8_STRING", _fake.Read("clipboard.type"));
        Assert.Contains("key\n--clearmodifiers\nctrl+v\n", _fake.Read("xdotool.args"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_dictation_appears_in_no_tool_s_arguments()
    {
        // /proc/<pid>/cmdline is readable by every local user.
        await Injector().InsertAsync(Dictated, PasteWithXclip, PasteShortcut.CtrlV, _tools);
        await Injector().InsertAsync(Dictated, TypeWithXdotool, PasteShortcut.CtrlV, _tools);

        Assert.DoesNotContain("Dictée", _fake.AllArguments(), StringComparison.Ordinal);
        Assert.DoesNotContain("insérer", _fake.AllArguments(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_password_manager_s_secret_is_cleared_after_the_paste_not_restored()
    {
        // SEC-01: KeePassXC offers the mark next to the password. Restored,
        // the password came back without it, and Klipper or CopyQ recorded it.
        _fake.Write("offered", "TARGETS\nUTF8_STRING\nx-kde-passwordManagerHint\n");
        _fake.Write("content", "S3cret-P4ss");
        _fake.Write("clipboard", "S3cret-P4ss");

        InsertionResult result = await Injector().InsertAsync(Dictated, PasteWithXclip, PasteShortcut.CtrlV, _tools);

        Assert.Equal(InsertionStatus.Inserted, result.Status);
        Assert.Equal(Dictated, _fake.Read("pasted"));
        Assert.Equal(string.Empty, _fake.Read("clipboard"));
        Assert.DoesNotContain("-target\nS3cret", _fake.Read("xclip.args"), StringComparison.Ordinal);
        Assert.Contains(_log, line => line.Contains("password manager", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_screen_locked_while_the_clipboard_was_prepared_receives_no_paste()
    {
        // SEC-05: the session was checked before the clipboard tools ran,
        // which can take seconds. Asked again before the keystroke, it says
        // "locked": no key goes out, and the clipboard is given back.
        InsertionResult result = await Injector().InsertAsync(
            Dictated, PasteWithXclip, PasteShortcut.CtrlV, _tools, () => "session 2 is locked");

        Assert.Equal(new InsertionResult(InsertionStatus.Refused, "session 2 is locked"), result);
        Assert.False(_fake.Has("pasted"));
        Assert.False(_fake.Has("xdotool.args"));
        Assert.Equal("what the user had copied", _fake.Read("clipboard"));
    }

    [Fact]
    public async Task Typing_sends_the_text_on_standard_input()
    {
        InsertionResult result = await Injector().InsertAsync(Dictated, TypeWithXdotool, PasteShortcut.CtrlV, _tools);

        Assert.Equal(InsertionStatus.Inserted, result.Status);
        Assert.Equal(Dictated, _fake.Read("typed"));
        Assert.Contains("type\n--clearmodifiers\n--file\n-\n", _fake.Read("xdotool.args"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Typing_into_a_screen_locked_meanwhile_types_nothing()
    {
        InsertionResult result = await Injector().InsertAsync(
            Dictated, TypeWithXdotool, PasteShortcut.CtrlV, _tools, () => "session 2 is locked");

        Assert.Equal(InsertionStatus.Refused, result.Status);
        Assert.False(_fake.Has("typed"));
    }

    [Fact]
    public async Task A_session_still_fine_changes_nothing()
    {
        // The re-check answering "go on" is the ordinary case.
        InsertionResult result = await Injector().InsertAsync(
            Dictated, PasteWithXclip, PasteShortcut.CtrlV, _tools, () => null);

        Assert.Equal(InsertionStatus.Inserted, result.Status);
        Assert.Equal(Dictated, _fake.Read("pasted"));
    }

    [Fact]
    public async Task The_clipboard_fallback_leaves_the_text_only_when_the_session_allows()
    {
        // clipboardFallback: nothing can send keys, the text is left for the
        // user to paste — but not into a locked session's clipboard.
        var copyOnly = new InjectionPlan(InsertionMode.Paste, InjectionOutcome.ClipboardOnly, ClipboardTool.Xclip, KeyStroker.None, "nothing can send the keys");

        InsertionResult refused = await Injector().InsertAsync(Dictated, copyOnly, PasteShortcut.CtrlV, _tools, () => "session 2 is locked");

        Assert.Equal(InsertionStatus.Refused, refused.Status);
        Assert.Equal("what the user had copied", _fake.Read("clipboard"));

        InsertionResult copied = await Injector().InsertAsync(Dictated, copyOnly, PasteShortcut.CtrlV, _tools);

        Assert.Equal(InsertionStatus.CopiedOnly, copied.Status);
        Assert.Equal(Dictated, _fake.Read("clipboard"));
    }

    [Fact]
    public void An_empty_clipboard_is_saved_as_nothing_and_cleared_after()
    {
        // HexWin's privacy rule: with nothing to put back, the clipboard is
        // emptied rather than left holding the dictation.
        _fake.Write("offered", "TARGETS\nTIMESTAMP\n");
        var clipboard = new Clipboard(_tools, _log.Add);

        ClipboardSnapshot saved = clipboard.Capture(ClipboardTool.Xclip);
        Assert.False(saved.HasContent);

        Assert.True(clipboard.SetText(ClipboardTool.Xclip, Dictated));
        clipboard.Restore(ClipboardTool.Xclip, saved);

        Assert.Equal(string.Empty, _fake.Read("clipboard"));
    }

    private TextInjector Injector() => new(uinput: null, _log.Add);
}
