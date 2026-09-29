using HexLinux.Configuration;
using HexLinux.Platform;

namespace HexLinux.Output;

/// <summary>The pair of tools that moves text through the clipboard.</summary>
public enum ClipboardTool
{
    None,

    /// <summary>wl-copy and wl-paste, the Wayland clipboard.</summary>
    WlClipboard,

    /// <summary>xclip, the X11 clipboard — XWayland's too, which the compositor keeps in step.</summary>
    Xclip,

    /// <summary>xsel: X11, plain text only. The fallback when xclip is missing.</summary>
    Xsel,
}

/// <summary>What delivers the keystrokes.</summary>
public enum KeyStroker
{
    None,

    /// <summary>HexLinux's own virtual keyboard on /dev/uinput.</summary>
    Uinput,

    Xdotool,

    Wtype,
}

/// <summary>What an insertion will amount to.</summary>
public enum InjectionOutcome
{
    /// <summary>The text reaches the focused application.</summary>
    Insert,

    /// <summary>
    /// Nothing can send keystrokes, and <c>clipboardFallback</c> is on: the
    /// text is left in the clipboard for the user to paste, and not taken
    /// back out.
    /// </summary>
    ClipboardOnly,

    /// <summary>Nothing can be done; <see cref="InjectionPlan.Problem"/> says why.</summary>
    Impossible,
}

/// <summary>What the planner needs to know about the machine, collected by the caller.</summary>
/// <param name="Session">The graphical session, from the environment.</param>
/// <param name="Tools">Names of the tools found on the PATH (see <see cref="ToolLocator"/>).</param>
/// <param name="UinputWritable">True when HexLinux's virtual keyboard could be created.</param>
/// <param name="WtypeWorks">
/// What the probe said: <c>wtype -</c> with nothing to type succeeds only
/// where the compositor offers the virtual-keyboard protocol. Null when it was
/// not run — wtype missing, or no Wayland session.
/// </param>
public sealed record InjectionContext(
    DesktopSession Session,
    IReadOnlySet<string> Tools,
    bool UinputWritable,
    bool? WtypeWorks = null)
{
    public bool Has(string tool) => Tools.Contains(tool);
}

/// <summary>
/// How one insertion will be carried out, or, in <see cref="Problem"/>, why it
/// cannot be — in words the user can act on.
/// </summary>
/// <param name="Mode">The insertion mode asked for.</param>
/// <param name="Outcome">Insert, leave in the clipboard, or nothing.</param>
/// <param name="Clipboard">The clipboard tools, when the text goes through the clipboard.</param>
/// <param name="Keys">What sends the paste shortcut, or types the text.</param>
/// <param name="Problem">Why the text cannot be inserted; null when it can.</param>
public sealed record InjectionPlan(
    InsertionMode Mode,
    InjectionOutcome Outcome,
    ClipboardTool Clipboard,
    KeyStroker Keys,
    string? Problem)
{
    public bool IsPossible => Outcome == InjectionOutcome.Insert;

    public static InjectionPlan Impossible(InsertionMode mode, string problem) =>
        new(mode, InjectionOutcome.Impossible, ClipboardTool.None, KeyStroker.None, problem);

    /// <summary>One line for the diagnostics and the log: tool names, never text.</summary>
    public string Describe() => Outcome switch
    {
        InjectionOutcome.Insert when Mode == InsertionMode.Type => $"typed by {Name(Keys)}",
        InjectionOutcome.Insert => $"clipboard {Name(Clipboard)}, keys {Name(Keys)}",
        InjectionOutcome.ClipboardOnly => $"left in the clipboard ({Name(Clipboard)}) for a manual paste, because {Problem}",
        _ => "impossible: " + Problem,
    };

    private static string Name(ClipboardTool tool) => tool switch
    {
        ClipboardTool.WlClipboard => "wl-clipboard",
        ClipboardTool.Xclip => ToolLocator.Xclip,
        ClipboardTool.Xsel => ToolLocator.Xsel,
        _ => "none",
    };

    private static string Name(KeyStroker keys) => keys switch
    {
        KeyStroker.Uinput => "uinput",
        KeyStroker.Xdotool => ToolLocator.Xdotool,
        KeyStroker.Wtype => ToolLocator.Wtype,
        _ => "none",
    };
}

/// <summary>
/// Decides how dictated text reaches the focused application.
///
/// <para><b>Why this is the hard part on Linux.</b> Windows has one way in,
/// SendInput, that reaches every application. Linux has as many as it has
/// desktops: X11 lets any client send keys to any other (xdotool); Wayland
/// lets none on purpose, and only some compositors make an exception —
/// wlroots ones accept wtype's virtual keyboard, while GNOME's Mutter, KDE's
/// KWin and Weston do not. Below all of them, a virtual keyboard made through
/// /dev/uinput works everywhere, but needs a permission and sends physical
/// keys: it can press a shortcut, not type text, since which character a key
/// produces depends on the layout — and even Ctrl+V is Ctrl+<i>whatever the V
/// position types</i> on a Dvorak or Bépo layout.</para>
///
/// <para>Hence the order. Under X11, xdotool first: it works by keysym, so on
/// any layout, with no permission. Under Wayland, the virtual keyboard first,
/// since it is the one sender every compositor accepts; wtype only where the
/// probe proved it works. The clipboard follows the same logic: wl-clipboard
/// under Wayland, except under GNOME, where Mutter offers wl-clipboard no
/// data-control protocol and it has to flash a window that steals the focus —
/// there the X11 clipboard of XWayland is used, which Mutter mirrors (to be
/// confirmed on a real GNOME session: it cannot be checked under WSL).</para>
///
/// <para>Pure: the session, the tools found, the uinput permission and the
/// wtype probe are collected by the caller. Every combination is therefore a
/// line in a test, where on a real machine each would need a different
/// desktop.</para>
/// </summary>
public static class InjectionPlanner
{
    private const string UdevHint = "run scripts/install-udev-rules.sh --with-uinput (see hexlinux --doctor)";

    public static InjectionPlan Plan(InsertionMode mode, KeySender sender, bool clipboardFallback, InjectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Session.Server == DisplayServer.None)
        {
            return InjectionPlan.Impossible(
                mode,
                "there is no graphical session: neither WAYLAND_DISPLAY nor DISPLAY leads to a display. "
                + "Start HexLinux from the desktop session (autostart entry or terminal), not from a bare service.");
        }

        (ClipboardTool clipboard, string? clipboardProblem) = PickClipboard(context);

        (KeyStroker keys, string? keysProblem) = mode == InsertionMode.Type
            ? PickTyping(sender, context)
            : PickPasteKeys(sender, context);

        if (mode == InsertionMode.Paste && clipboardProblem is not null)
        {
            return InjectionPlan.Impossible(mode, clipboardProblem);
        }

        if (keysProblem is null)
        {
            ClipboardTool used = mode == InsertionMode.Paste ? clipboard : ClipboardTool.None;
            return new InjectionPlan(mode, InjectionOutcome.Insert, used, keys, null);
        }

        // Nothing can deliver the keys. The text can still be handed over
        // through the clipboard, when the user accepted that trade-off.
        if (clipboardFallback && clipboardProblem is null)
        {
            return new InjectionPlan(mode, InjectionOutcome.ClipboardOnly, clipboard, KeyStroker.None, keysProblem);
        }

        return InjectionPlan.Impossible(mode, keysProblem);
    }

    // --- Clipboard --------------------------------------------------------------

    private static (ClipboardTool, string?) PickClipboard(InjectionContext context)
    {
        DesktopSession session = context.Session;
        bool wlClipboard = context.Has(ToolLocator.WlCopy) && context.Has(ToolLocator.WlPaste);

        if (session.Server == DisplayServer.Wayland && !session.IsGnome && wlClipboard)
        {
            return (ClipboardTool.WlClipboard, null);
        }

        if (session.HasX11Display)
        {
            if (context.Has(ToolLocator.Xclip))
            {
                return (ClipboardTool.Xclip, null);
            }

            if (context.Has(ToolLocator.Xsel))
            {
                return (ClipboardTool.Xsel, null);
            }
        }

        // GNOME without the X11 tools: wl-clipboard still works, at the cost
        // of the focus it briefly takes — better than nothing.
        if (session.Server == DisplayServer.Wayland && wlClipboard)
        {
            return (ClipboardTool.WlClipboard, null);
        }

        string install = session.Server != DisplayServer.Wayland
            ? "xclip"
            : session.IsGnome ? "xclip (GNOME) or wl-clipboard" : "wl-clipboard";

        return (ClipboardTool.None, $"no clipboard tool was found: install {install}, or set \"insertion\" to \"Type\".");
    }

    // --- Paste shortcut -----------------------------------------------------------

    private static (KeyStroker, string?) PickPasteKeys(KeySender sender, InjectionContext context)
    {
        DesktopSession session = context.Session;

        switch (sender)
        {
            case KeySender.Uinput:
                return context.UinputWritable
                    ? (KeyStroker.Uinput, null)
                    : (KeyStroker.None, "\"keySender\" is Uinput but /dev/uinput is not writable: " + UdevHint + ".");

            case KeySender.Xdotool:
                return RequireXdotool(context);

            case KeySender.Wtype:
                return RequireWtype(context);
        }

        if (session.Server == DisplayServer.X11)
        {
            if (context.Has(ToolLocator.Xdotool))
            {
                return (KeyStroker.Xdotool, null);
            }

            return context.UinputWritable
                ? (KeyStroker.Uinput, null)
                : (KeyStroker.None, "nothing can send the paste shortcut: install xdotool.");
        }

        if (context.UinputWritable)
        {
            return (KeyStroker.Uinput, null);
        }

        if (WtypeUsable(context))
        {
            return (KeyStroker.Wtype, null);
        }

        return (KeyStroker.None, session.IsGnome
            ? "nothing can send the paste shortcut: GNOME accepts no simulated keys from applications, "
              + "so HexLinux needs its own virtual keyboard: " + UdevHint + "."
            : "nothing can send the paste shortcut: " + UdevHint
              + ", or, on a wlroots compositor (Sway, Hyprland…), install wtype.");
    }

    // --- Typing -------------------------------------------------------------------

    private static (KeyStroker, string?) PickTyping(KeySender sender, InjectionContext context)
    {
        DesktopSession session = context.Session;

        switch (sender)
        {
            case KeySender.Uinput:
                return (KeyStroker.None,
                    "the virtual keyboard cannot type text: it sends physical keys, and which character each one "
                    + "produces depends on the keyboard layout. Use \"insertion\": \"Paste\", or \"keySender\": "
                    + "\"Xdotool\" (xdotool, X11) or \"Wtype\" (wtype, wlroots compositors).");

            case KeySender.Xdotool:
                return RequireXdotool(context);

            case KeySender.Wtype:
                return RequireWtype(context);
        }

        if (session.Server == DisplayServer.X11)
        {
            return context.Has(ToolLocator.Xdotool)
                ? (KeyStroker.Xdotool, null)
                : (KeyStroker.None, "typing needs xdotool under X11: install it, or use \"insertion\": \"Paste\".");
        }

        if (WtypeUsable(context))
        {
            return (KeyStroker.Wtype, null);
        }

        return (KeyStroker.None, context.Has(ToolLocator.Wtype)
            ? "this compositor accepts no simulated typing (wtype was refused): use \"insertion\": \"Paste\"."
            : "typing under Wayland needs a wlroots compositor and wtype: use \"insertion\": \"Paste\".");
    }

    // --- Forced senders -----------------------------------------------------------

    /// <summary>
    /// A forced choice is honoured whenever it can run at all, even where the
    /// automatic one would not pick it: forcing exists precisely for the
    /// desktop the automatic rules get wrong.
    /// </summary>
    private static (KeyStroker, string?) RequireXdotool(InjectionContext context)
    {
        if (!context.Has(ToolLocator.Xdotool))
        {
            return (KeyStroker.None, "\"keySender\" is Xdotool but xdotool is not installed.");
        }

        return context.Session.HasX11Display
            ? (KeyStroker.Xdotool, null)
            : (KeyStroker.None, "\"keySender\" is Xdotool but there is no X11 display (DISPLAY is unset).");
    }

    private static (KeyStroker, string?) RequireWtype(InjectionContext context)
    {
        if (!context.Has(ToolLocator.Wtype))
        {
            return (KeyStroker.None, "\"keySender\" is Wtype but wtype is not installed.");
        }

        if (context.Session.Server != DisplayServer.Wayland)
        {
            return (KeyStroker.None, "\"keySender\" is Wtype but this is not a Wayland session.");
        }

        return context.WtypeWorks == false
            ? (KeyStroker.None, "\"keySender\" is Wtype but this compositor refuses it (no virtual-keyboard protocol).")
            : (KeyStroker.Wtype, null);
    }

    /// <summary>Only a probe that succeeded counts for the automatic choice.</summary>
    private static bool WtypeUsable(InjectionContext context) =>
        context.Session.Server == DisplayServer.Wayland
        && context.Has(ToolLocator.Wtype)
        && context.WtypeWorks == true;
}
