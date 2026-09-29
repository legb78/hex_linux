using System.Globalization;
using HexLinux.Configuration;
using HexLinux.Platform;

namespace HexLinux.Output;

/// <summary>One run of an external tool: its name on the PATH, and its arguments.</summary>
/// <param name="Tool">The tool's name, one of the <see cref="ToolLocator"/> constants.</param>
/// <param name="Arguments">The arguments, one per entry: no shell ever parses them.</param>
public sealed record ToolCommand(string Tool, IReadOnlyList<string> Arguments)
{
    /// <summary>For the diagnostics: the command as it would be typed.</summary>
    public override string ToString() => string.Join(' ', [Tool, .. Arguments]);
}

/// <summary>
/// The command lines of every tool HexLinux drives.
///
/// <para><b>No argument ever carries dictated text or clipboard content.</b>
/// A process's arguments are readable by every local user through
/// <c>/proc/&lt;pid&gt;/cmdline</c>; the text travels on the tool's standard
/// input instead, which is why no method here takes it. What does appear —
/// a MIME type, a session number — says nothing about what was
/// dictated.</para>
///
/// <para>Each syntax was checked against the tool's own <c>--help</c> or
/// manual page on the build machine: wl-clipboard 2.2.1, xclip 0.13, xsel
/// 1.2.1, xdotool 3.20160805, wtype 0.4, systemd 255's loginctl.</para>
/// </summary>
public static class ToolCommands
{
    /// <summary>The MIME type HexLinux puts dictated text under, on Wayland.</summary>
    public const string WaylandText = "text/plain;charset=utf-8";

    /// <summary>The X11 target for UTF-8 text.</summary>
    public const string X11Text = "UTF8_STRING";

    /// <summary>The format dictated text is written in, with <paramref name="tool"/>.</summary>
    public static string TextFormat(ClipboardTool tool) => tool == ClipboardTool.WlClipboard ? WaylandText : X11Text;

    // --- Clipboard ----------------------------------------------------------------

    /// <summary>
    /// Lists what the clipboard offers, or null for xsel, which only knows
    /// text. Always the CLIPBOARD selection: PRIMARY, the one a mere mouse
    /// selection fills, is never read nor written.
    /// </summary>
    public static ToolCommand? ListFormats(ClipboardTool tool) => tool switch
    {
        ClipboardTool.WlClipboard => new(ToolLocator.WlPaste, ["--list-types"]),
        ClipboardTool.Xclip => new(ToolLocator.Xclip, ["-selection", "clipboard", "-o", "-target", "TARGETS"]),
        _ => null,
    };

    /// <summary>
    /// Reads the clipboard in one format. <c>--no-newline</c> matters:
    /// wl-paste otherwise appends a newline to text, and restoring would add
    /// one to the user's clipboard at every dictation.
    /// </summary>
    public static ToolCommand Read(ClipboardTool tool, string format) => tool switch
    {
        ClipboardTool.WlClipboard => new(ToolLocator.WlPaste, ["--no-newline", "--type", format]),
        ClipboardTool.Xclip => new(ToolLocator.Xclip, ["-selection", "clipboard", "-o", "-target", format]),
        ClipboardTool.Xsel => new(ToolLocator.Xsel, ["--clipboard", "--output"]),
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "No clipboard tool."),
    };

    /// <summary>
    /// Fills the clipboard with what arrives on standard input. All three
    /// tools then fork to serve it and the parent returns — see
    /// <c>ProcessRunner</c> for how that is handled.
    /// </summary>
    public static ToolCommand Write(ClipboardTool tool, string format) => tool switch
    {
        ClipboardTool.WlClipboard => new(ToolLocator.WlCopy, ["--type", format]),
        ClipboardTool.Xclip => new(ToolLocator.Xclip, ["-selection", "clipboard", "-i", "-target", format]),
        ClipboardTool.Xsel => new(ToolLocator.Xsel, ["--clipboard", "--input"]),
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "No clipboard tool."),
    };

    /// <summary>
    /// Empties the clipboard. xclip has no such option: it is given empty
    /// input instead, which leaves an empty text in the clipboard — nothing
    /// left to paste either way.
    /// </summary>
    public static ToolCommand Clear(ClipboardTool tool) => tool switch
    {
        ClipboardTool.WlClipboard => new(ToolLocator.WlCopy, ["--clear"]),
        ClipboardTool.Xclip => new(ToolLocator.Xclip, ["-selection", "clipboard", "-i", "-target", X11Text]),
        ClipboardTool.Xsel => new(ToolLocator.Xsel, ["--clipboard", "--clear"]),
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "No clipboard tool."),
    };

    // --- Keystrokes ---------------------------------------------------------------

    /// <summary>
    /// Sends the paste shortcut through xdotool or wtype. The uinput keyboard
    /// takes key codes instead: see <see cref="KeySequences"/>.
    ///
    /// <para>xdotool always gets <c>--clearmodifiers</c>: it releases, for the
    /// length of the shortcut, the modifiers the X server believes held, then
    /// presses them again. Without it, a Super still held when the paste
    /// goes out turns Ctrl+V into Super+Ctrl+V — the HexWin incident where a
    /// system panel took the focus and the dictation vanished.</para>
    /// </summary>
    public static ToolCommand PasteKeys(KeyStroker keys, PasteShortcut shortcut) => keys switch
    {
        KeyStroker.Xdotool => new(ToolLocator.Xdotool, ["key", "--clearmodifiers", XdotoolChord(shortcut)]),
        KeyStroker.Wtype => new(ToolLocator.Wtype, WtypeChord(shortcut)),
        _ => throw new ArgumentOutOfRangeException(nameof(keys), keys, "Only xdotool and wtype take a command line."),
    };

    /// <summary>Types what arrives on standard input.</summary>
    public static ToolCommand TypeText(KeyStroker keys) => keys switch
    {
        KeyStroker.Xdotool => new(ToolLocator.Xdotool, ["type", "--clearmodifiers", "--file", "-"]),
        KeyStroker.Wtype => new(ToolLocator.Wtype, ["-"]),
        _ => throw new ArgumentOutOfRangeException(nameof(keys), keys, "Only xdotool and wtype can type text."),
    };

    /// <summary>
    /// Asks wtype to type nothing. It fails at once, and cleanly, when the
    /// compositor lacks the virtual-keyboard protocol (seen under Weston:
    /// "Compositor does not support the virtual keyboard protocol"), which
    /// makes it the cheapest test of whether wtype can work here.
    /// </summary>
    public static ToolCommand WtypeProbe() => new(ToolLocator.Wtype, ["-"]);

    private static string XdotoolChord(PasteShortcut shortcut) => shortcut switch
    {
        PasteShortcut.CtrlShiftV => "ctrl+shift+v",
        PasteShortcut.ShiftInsert => "shift+Insert",
        _ => "ctrl+v",
    };

    /// <summary>
    /// wtype presses the modifiers (<c>-M</c>), taps the key (<c>-k</c>, a
    /// keysym name) and releases the modifiers (<c>-m</c>) in reverse.
    /// </summary>
    private static string[] WtypeChord(PasteShortcut shortcut) => shortcut switch
    {
        PasteShortcut.CtrlShiftV => ["-M", "ctrl", "-M", "shift", "-k", "v", "-m", "shift", "-m", "ctrl"],
        PasteShortcut.ShiftInsert => ["-M", "shift", "-k", "Insert", "-m", "shift"],
        _ => ["-M", "ctrl", "-k", "v", "-m", "ctrl"],
    };

    // --- Session ------------------------------------------------------------------

    /// <summary>
    /// The properties the session guard decides on. Type, Seat and Remote
    /// tell a graphical session at a seat from an SSH login, which logind
    /// always reports as active; User tells whose session it is (printed as
    /// the numeric uid: <c>User=0</c>, verified with systemd 255).
    /// </summary>
    public static ToolCommand ShowSession(string sessionId) => new(
        ToolLocator.Loginctl,
        ["show-session", sessionId, "--property=Active", "--property=LockedHint", "--property=Type", "--property=Seat", "--property=Remote", "--property=User"]);

    /// <summary>The graphical session logind attributes to the user, as a bare id.</summary>
    public static ToolCommand ShowUserDisplay(uint uid) => new(
        ToolLocator.Loginctl,
        ["show-user", uid.ToString(CultureInfo.InvariantCulture), "--property=Display", "--value"]);

    /// <summary>
    /// The session in front of the main seat, as a bare id — empty when
    /// nobody sits at it (verified under WSL: <c>ActiveSession=</c>, exit
    /// code 0). Asked when the user's own session is attached to no seat and
    /// so says nothing about the screen.
    /// </summary>
    public static ToolCommand ShowSeatActiveSession() => new(
        ToolLocator.Loginctl,
        ["show-seat", "seat0", "--property=ActiveSession", "--value"]);
}
