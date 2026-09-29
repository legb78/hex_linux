using HexLinux.Configuration;
using HexLinux.Output;
using HexLinux.Platform;
using Xunit;

namespace HexLinux.Tests.Output;

/// <summary>
/// Every desktop the planner decides for is a line here, where on real
/// machines each would need its own installation: X11, GNOME, KDE, Sway, a
/// bare console, with or without each tool and the uinput permission. A wrong
/// choice is a dictation that vanishes — keys sent by a tool the compositor
/// ignores, or a clipboard nothing reads — so each rule of the decision log
/// (P3 GNOME clipboard, P4 sender order, P5 wtype probe, clipboardFallback,
/// no typing through uinput) has its own case, and one exhaustive sweep checks
/// that no combination ever picks a tool that cannot run.
/// </summary>
public class InjectionPlannerTests
{
    private static readonly DesktopSession NoDisplay = new(DisplayServer.None, HasX11Display: false, "");
    private static readonly DesktopSession Xfce = new(DisplayServer.X11, HasX11Display: true, "XFCE");
    private static readonly DesktopSession Gnome = new(DisplayServer.Wayland, HasX11Display: true, "ubuntu:GNOME");
    private static readonly DesktopSession GnomeWithoutXwayland = new(DisplayServer.Wayland, HasX11Display: false, "ubuntu:GNOME");
    private static readonly DesktopSession Kde = new(DisplayServer.Wayland, HasX11Display: true, "KDE");
    private static readonly DesktopSession KdeWithoutXwayland = new(DisplayServer.Wayland, HasX11Display: false, "KDE");
    private static readonly DesktopSession Sway = new(DisplayServer.Wayland, HasX11Display: false, "sway");

    private static readonly string[] WlClipboard = [ToolLocator.WlCopy, ToolLocator.WlPaste];

    private static InjectionContext Context(DesktopSession session, string[] tools, bool uinput = false, bool? wtypeWorks = null) =>
        new(session, new HashSet<string>(tools, StringComparer.Ordinal), uinput, wtypeWorks);

    private static InjectionPlan Paste(InjectionContext context, KeySender sender = KeySender.Auto, bool fallback = false) =>
        InjectionPlanner.Plan(InsertionMode.Paste, sender, fallback, context);

    private static InjectionPlan Type(InjectionContext context, KeySender sender = KeySender.Auto, bool fallback = false) =>
        InjectionPlanner.Plan(InsertionMode.Type, sender, fallback, context);

    [Fact]
    public void A_missing_context_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => InjectionPlanner.Plan(InsertionMode.Paste, KeySender.Auto, false, null!));
    }

    // --- No graphical session -----------------------------------------------------

    [Theory]
    [InlineData(InsertionMode.Paste)]
    [InlineData(InsertionMode.Type)]
    public void Without_a_graphical_session_nothing_is_attempted(InsertionMode mode)
    {
        // Started from a bare systemd service or an SSH login: every tool is
        // installed and uinput is writable, yet no window can receive the text.
        InjectionContext context = Context(NoDisplay, [ToolLocator.Xclip, ToolLocator.Xdotool, .. WlClipboard], uinput: true);

        InjectionPlan plan = InjectionPlanner.Plan(mode, KeySender.Auto, clipboardFallback: true, context);

        Assert.Equal(InjectionOutcome.Impossible, plan.Outcome);
        Assert.False(plan.IsPossible);
        Assert.Equal(ClipboardTool.None, plan.Clipboard);
        Assert.Equal(KeyStroker.None, plan.Keys);
        Assert.Contains("no graphical session", plan.Problem, StringComparison.Ordinal);
        Assert.Contains("autostart", plan.Problem, StringComparison.Ordinal);
    }

    // --- Paste under X11 ----------------------------------------------------------

    [Fact]
    public void X11_pastes_through_xclip_and_xdotool()
    {
        // The plain X11 desktop (XFCE, MATE, Cinnamon): no permission needed.
        InjectionPlan plan = Paste(Context(Xfce, [ToolLocator.Xclip, ToolLocator.Xsel, ToolLocator.Xdotool]));

        Assert.Equal(InjectionOutcome.Insert, plan.Outcome);
        Assert.True(plan.IsPossible);
        Assert.Equal(ClipboardTool.Xclip, plan.Clipboard);
        Assert.Equal(KeyStroker.Xdotool, plan.Keys);
        Assert.Null(plan.Problem);
        Assert.Equal("clipboard xclip, keys xdotool", plan.Describe());
    }

    [Fact]
    public void X11_falls_back_on_xsel_when_xclip_is_missing()
    {
        InjectionPlan plan = Paste(Context(Xfce, [ToolLocator.Xsel, ToolLocator.Xdotool]));

        Assert.Equal(ClipboardTool.Xsel, plan.Clipboard);
        Assert.Equal("clipboard xsel, keys xdotool", plan.Describe());
    }

    [Fact]
    public void X11_prefers_xdotool_even_when_the_virtual_keyboard_is_available()
    {
        // P4: xdotool sends Ctrl+V by keysym, so it stays Ctrl+V on a Dvorak
        // or Bépo layout, where the uinput V key would type another letter.
        InjectionPlan plan = Paste(Context(Xfce, [ToolLocator.Xclip, ToolLocator.Xdotool], uinput: true));

        Assert.Equal(KeyStroker.Xdotool, plan.Keys);
    }

    [Fact]
    public void X11_without_xdotool_uses_the_virtual_keyboard()
    {
        // P4, second choice: the udev rule is installed but xdotool is not.
        InjectionPlan plan = Paste(Context(Xfce, [ToolLocator.Xclip], uinput: true));

        Assert.Equal(InjectionOutcome.Insert, plan.Outcome);
        Assert.Equal(KeyStroker.Uinput, plan.Keys);
        Assert.Equal("clipboard xclip, keys uinput", plan.Describe());
    }

    [Fact]
    public void X11_without_any_key_sender_asks_for_xdotool()
    {
        InjectionPlan plan = Paste(Context(Xfce, [ToolLocator.Xclip]));

        Assert.Equal(InjectionOutcome.Impossible, plan.Outcome);
        Assert.Contains("install xdotool", plan.Problem, StringComparison.Ordinal);
        Assert.Equal("impossible: " + plan.Problem, plan.Describe());
    }

    [Fact]
    public void X11_without_a_clipboard_tool_asks_for_xclip_or_typing()
    {
        // The minimal install: xdotool alone. Pasting cannot work, typing
        // could, and the message says both.
        InjectionPlan plan = Paste(Context(Xfce, [ToolLocator.Xdotool], uinput: true));

        Assert.Equal(InjectionOutcome.Impossible, plan.Outcome);
        Assert.Contains("install xclip", plan.Problem, StringComparison.Ordinal);
        Assert.Contains("\"Type\"", plan.Problem, StringComparison.Ordinal);
    }

    // --- Paste under Wayland ------------------------------------------------------

    [Fact]
    public void Kde_pastes_through_wl_clipboard_and_the_virtual_keyboard()
    {
        // KWin accepts no simulated keys from applications: the uinput
        // keyboard, below the compositor, is the way in.
        InjectionPlan plan = Paste(Context(Kde, [.. WlClipboard, ToolLocator.Xclip, ToolLocator.Xdotool], uinput: true));

        Assert.Equal(InjectionOutcome.Insert, plan.Outcome);
        Assert.Equal(ClipboardTool.WlClipboard, plan.Clipboard);
        Assert.Equal(KeyStroker.Uinput, plan.Keys);
        Assert.Equal("clipboard wl-clipboard, keys uinput", plan.Describe());
    }

    [Fact]
    public void Wayland_prefers_the_virtual_keyboard_over_a_working_wtype()
    {
        // P4 under Wayland: uinput first, the one sender every compositor
        // accepts. The feasibility study suggested wtype first on wlroots;
        // the decision log kept uinput.
        InjectionPlan plan = Paste(Context(Sway, [.. WlClipboard, ToolLocator.Wtype], uinput: true, wtypeWorks: true));

        Assert.Equal(KeyStroker.Uinput, plan.Keys);
    }

    [Fact]
    public void Sway_without_the_uinput_permission_uses_wtype_once_its_probe_succeeded()
    {
        // wlroots compositors offer the virtual-keyboard protocol wtype needs.
        InjectionPlan plan = Paste(Context(Sway, [.. WlClipboard, ToolLocator.Wtype], wtypeWorks: true));

        Assert.Equal(InjectionOutcome.Insert, plan.Outcome);
        Assert.Equal(ClipboardTool.WlClipboard, plan.Clipboard);
        Assert.Equal(KeyStroker.Wtype, plan.Keys);
        Assert.Equal("clipboard wl-clipboard, keys wtype", plan.Describe());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void Wtype_is_not_picked_automatically_unless_its_probe_succeeded(bool? wtypeWorks)
    {
        // P5: installed is not enough. Under Weston or KWin wtype fails with
        // "Compositor does not support the virtual keyboard protocol", and a
        // paste sent through it would silently go nowhere.
        InjectionPlan plan = Paste(Context(KdeWithoutXwayland, [.. WlClipboard, ToolLocator.Wtype], wtypeWorks: wtypeWorks));

        Assert.Equal(InjectionOutcome.Impossible, plan.Outcome);
        Assert.Equal(KeyStroker.None, plan.Keys);
    }

    [Fact]
    public void Wayland_without_any_key_sender_points_at_the_udev_rule_and_wtype()
    {
        InjectionPlan plan = Paste(Context(Kde, [.. WlClipboard]));

        Assert.Equal(InjectionOutcome.Impossible, plan.Outcome);
        Assert.Contains("install-udev-rules.sh --with-uinput", plan.Problem, StringComparison.Ordinal);
        Assert.Contains("wtype", plan.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Gnome_without_the_uinput_permission_says_only_the_virtual_keyboard_can_work()
    {
        // Mutter has no virtual-keyboard protocol: suggesting wtype there
        // would send the user after a tool that cannot work.
        InjectionPlan plan = Paste(Context(Gnome, [.. WlClipboard, ToolLocator.Xclip, ToolLocator.Wtype], wtypeWorks: false));

        Assert.Equal(InjectionOutcome.Impossible, plan.Outcome);
        Assert.Contains("GNOME accepts no simulated keys", plan.Problem, StringComparison.Ordinal);
        Assert.Contains("install-udev-rules.sh --with-uinput", plan.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain("install wtype", plan.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Gnome_uses_the_XWayland_clipboard_through_xclip()
    {
        // P3: Mutter offers wl-clipboard no data-control protocol, so wl-copy
        // would flash a window that steals the focus from the target; the
        // X11 clipboard of XWayland, which Mutter mirrors, does not.
        InjectionPlan plan = Paste(Context(Gnome, [.. WlClipboard, ToolLocator.Xclip], uinput: true));

        Assert.Equal(InjectionOutcome.Insert, plan.Outcome);
        Assert.Equal(ClipboardTool.Xclip, plan.Clipboard);
        Assert.Equal(KeyStroker.Uinput, plan.Keys);
    }

    [Fact]
    public void Gnome_uses_xsel_when_xclip_is_missing()
    {
        InjectionPlan plan = Paste(Context(Gnome, [.. WlClipboard, ToolLocator.Xsel], uinput: true));

        Assert.Equal(ClipboardTool.Xsel, plan.Clipboard);
    }

    [Fact]
    public void Gnome_without_the_X11_tools_still_pastes_through_wl_clipboard()
    {
        // The focus flash is a nuisance; no clipboard at all would be a
        // dictation lost.
        InjectionPlan plan = Paste(Context(Gnome, [.. WlClipboard], uinput: true));

        Assert.Equal(InjectionOutcome.Insert, plan.Outcome);
        Assert.Equal(ClipboardTool.WlClipboard, plan.Clipboard);
    }

    [Fact]
    public void Gnome_without_XWayland_ignores_an_installed_xclip()
    {
        // XWayland disabled or not started yet: DISPLAY is unset and xclip
        // has no server to talk to.
        InjectionPlan plan = Paste(Context(GnomeWithoutXwayland, [.. WlClipboard, ToolLocator.Xclip], uinput: true));

        Assert.Equal(ClipboardTool.WlClipboard, plan.Clipboard);
    }

    [Fact]
    public void Gnome_without_any_clipboard_tool_names_both_candidates()
    {
        InjectionPlan plan = Paste(Context(Gnome, [], uinput: true));

        Assert.Equal(InjectionOutcome.Impossible, plan.Outcome);
        Assert.Contains("install xclip (GNOME) or wl-clipboard", plan.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Kde_without_wl_clipboard_uses_the_XWayland_clipboard()
    {
        InjectionPlan plan = Paste(Context(Kde, [ToolLocator.Xclip], uinput: true));

        Assert.Equal(InjectionOutcome.Insert, plan.Outcome);
        Assert.Equal(ClipboardTool.Xclip, plan.Clipboard);
    }

    [Theory]
    [InlineData(ToolLocator.WlCopy)]
    [InlineData(ToolLocator.WlPaste)]
    public void Half_of_wl_clipboard_is_not_enough(string present)
    {
        // wl-paste saves the user's clipboard and wl-copy fills it: with one
        // of them missing the paste would either fail or lose what was copied.
        InjectionPlan plan = Paste(Context(Sway, [present], uinput: true));

        Assert.Equal(InjectionOutcome.Impossible, plan.Outcome);
        Assert.Contains("install wl-clipboard", plan.Problem, StringComparison.Ordinal);
    }

    // --- Forced senders -----------------------------------------------------------

    [Fact]
    public void A_forced_uinput_that_is_not_writable_says_how_to_allow_it()
    {
        InjectionPlan plan = Paste(Context(Xfce, [ToolLocator.Xclip, ToolLocator.Xdotool]), KeySender.Uinput);

        Assert.Equal(InjectionOutcome.Impossible, plan.Outcome);
        Assert.Contains("/dev/uinput is not writable", plan.Problem, StringComparison.Ordinal);
        Assert.Contains("install-udev-rules.sh --with-uinput", plan.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_forced_uinput_is_honoured_where_the_automatic_choice_would_be_xdotool()
    {
        // Forcing exists for the desktop the automatic rules get wrong.
        InjectionPlan plan = Paste(Context(Xfce, [ToolLocator.Xclip, ToolLocator.Xdotool], uinput: true), KeySender.Uinput);

        Assert.Equal(KeyStroker.Uinput, plan.Keys);
    }

    [Fact]
    public void A_forced_xdotool_works_under_Wayland_for_XWayland_windows()
    {
        InjectionPlan plan = Paste(Context(Kde, [.. WlClipboard, ToolLocator.Xdotool], uinput: true), KeySender.Xdotool);

        Assert.Equal(InjectionOutcome.Insert, plan.Outcome);
        Assert.Equal(KeyStroker.Xdotool, plan.Keys);
    }

    [Fact]
    public void A_forced_xdotool_that_is_not_installed_says_so()
    {
        InjectionPlan plan = Paste(Context(Xfce, [ToolLocator.Xclip], uinput: true), KeySender.Xdotool);

        Assert.Equal(InjectionOutcome.Impossible, plan.Outcome);
        Assert.Contains("xdotool is not installed", plan.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_forced_xdotool_without_an_X11_display_says_so()
    {
        InjectionPlan plan = Paste(Context(Sway, [.. WlClipboard, ToolLocator.Xdotool]), KeySender.Xdotool);

        Assert.Equal(InjectionOutcome.Impossible, plan.Outcome);
        Assert.Contains("no X11 display", plan.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_forced_wtype_that_is_not_installed_says_so()
    {
        InjectionPlan plan = Paste(Context(Sway, [.. WlClipboard]), KeySender.Wtype);

        Assert.Equal(InjectionOutcome.Impossible, plan.Outcome);
        Assert.Contains("wtype is not installed", plan.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_forced_wtype_outside_Wayland_says_so()
    {
        InjectionPlan plan = Paste(Context(Xfce, [ToolLocator.Xclip, ToolLocator.Wtype]), KeySender.Wtype);

        Assert.Equal(InjectionOutcome.Impossible, plan.Outcome);
        Assert.Contains("not a Wayland session", plan.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_forced_wtype_refused_by_the_compositor_says_so()
    {
        InjectionPlan plan = Paste(Context(Kde, [.. WlClipboard, ToolLocator.Wtype], wtypeWorks: false), KeySender.Wtype);

        Assert.Equal(InjectionOutcome.Impossible, plan.Outcome);
        Assert.Contains("refuses it", plan.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_forced_wtype_is_honoured_when_no_probe_said_otherwise()
    {
        // Unlike the automatic choice, forcing gives wtype the benefit of the
        // doubt: the user asked for it on a desktop they know.
        InjectionPlan plan = Paste(Context(Sway, [.. WlClipboard, ToolLocator.Wtype], wtypeWorks: null), KeySender.Wtype);

        Assert.Equal(InjectionOutcome.Insert, plan.Outcome);
        Assert.Equal(KeyStroker.Wtype, plan.Keys);
    }

    // --- Typing -------------------------------------------------------------------

    [Fact]
    public void X11_types_through_xdotool_without_touching_the_clipboard()
    {
        // Typing exists for applications that ignore the clipboard; it must
        // leave whatever the user had copied alone.
        InjectionPlan plan = Type(Context(Xfce, [ToolLocator.Xclip, ToolLocator.Xdotool], uinput: true));

        Assert.Equal(InjectionOutcome.Insert, plan.Outcome);
        Assert.Equal(InsertionMode.Type, plan.Mode);
        Assert.Equal(KeyStroker.Xdotool, plan.Keys);
        Assert.Equal(ClipboardTool.None, plan.Clipboard);
        Assert.Equal("typed by xdotool", plan.Describe());
    }

    [Fact]
    public void Typing_needs_no_clipboard_tool()
    {
        InjectionPlan plan = Type(Context(Xfce, [ToolLocator.Xdotool]));

        Assert.Equal(InjectionOutcome.Insert, plan.Outcome);
        Assert.Equal(KeyStroker.Xdotool, plan.Keys);
    }

    [Fact]
    public void X11_typing_without_xdotool_is_refused_even_with_the_virtual_keyboard()
    {
        InjectionPlan plan = Type(Context(Xfce, [ToolLocator.Xclip], uinput: true));

        Assert.Equal(InjectionOutcome.Impossible, plan.Outcome);
        Assert.Contains("typing needs xdotool under X11", plan.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Sway_types_through_wtype_once_its_probe_succeeded()
    {
        InjectionPlan plan = Type(Context(Sway, [.. WlClipboard, ToolLocator.Wtype], uinput: true, wtypeWorks: true));

        Assert.Equal(InjectionOutcome.Insert, plan.Outcome);
        Assert.Equal(KeyStroker.Wtype, plan.Keys);
        Assert.Equal(ClipboardTool.None, plan.Clipboard);
        Assert.Equal("typed by wtype", plan.Describe());
    }

    [Fact]
    public void Wayland_typing_with_a_refused_wtype_says_the_compositor_refused_it()
    {
        InjectionPlan plan = Type(Context(Gnome, [.. WlClipboard, ToolLocator.Wtype, ToolLocator.Xdotool], uinput: true, wtypeWorks: false));

        Assert.Equal(InjectionOutcome.Impossible, plan.Outcome);
        Assert.Contains("wtype was refused", plan.Problem, StringComparison.Ordinal);
        Assert.Contains("\"Paste\"", plan.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Wayland_typing_without_wtype_says_what_it_would_need()
    {
        InjectionPlan plan = Type(Context(Kde, [.. WlClipboard], uinput: true));

        Assert.Equal(InjectionOutcome.Impossible, plan.Outcome);
        Assert.Contains("wlroots compositor and wtype", plan.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void The_virtual_keyboard_cannot_type_and_the_problem_names_the_alternatives()
    {
        // Which character a physical key produces depends on the layout:
        // typing "a" with KEY_A gives "q" on AZERTY. The message must say what
        // to use instead, since the user asked for something that cannot work.
        InjectionPlan plan = Type(Context(Xfce, [ToolLocator.Xclip, ToolLocator.Xdotool], uinput: true), KeySender.Uinput);

        Assert.Equal(InjectionOutcome.Impossible, plan.Outcome);
        Assert.Equal(KeyStroker.None, plan.Keys);
        Assert.Contains("keyboard layout", plan.Problem, StringComparison.Ordinal);
        Assert.Contains("\"insertion\": \"Paste\"", plan.Problem, StringComparison.Ordinal);
        Assert.Contains("\"Xdotool\"", plan.Problem, StringComparison.Ordinal);
        Assert.Contains("\"Wtype\"", plan.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_forced_xdotool_types_into_XWayland_windows()
    {
        InjectionPlan plan = Type(Context(Kde, [ToolLocator.Xdotool]), KeySender.Xdotool);

        Assert.Equal(InjectionOutcome.Insert, plan.Outcome);
        Assert.Equal(KeyStroker.Xdotool, plan.Keys);
    }

    [Fact]
    public void A_forced_wtype_types_when_the_compositor_accepts_it()
    {
        InjectionPlan plan = Type(Context(Sway, [ToolLocator.Wtype], wtypeWorks: true), KeySender.Wtype);

        Assert.Equal(KeyStroker.Wtype, plan.Keys);
    }

    // --- clipboardFallback --------------------------------------------------------

    [Fact]
    public void With_the_fallback_on_and_no_key_sender_the_text_is_left_in_the_clipboard()
    {
        // Stock GNOME without the udev rule: nothing can press Ctrl+V. The
        // user accepted, through clipboardFallback, that the dictation stays
        // in the clipboard for a manual paste.
        InjectionPlan plan = Paste(Context(Gnome, [.. WlClipboard, ToolLocator.Xclip]), fallback: true);

        Assert.Equal(InjectionOutcome.ClipboardOnly, plan.Outcome);
        Assert.False(plan.IsPossible);
        Assert.Equal(ClipboardTool.Xclip, plan.Clipboard);
        Assert.Equal(KeyStroker.None, plan.Keys);
        Assert.Contains("nothing can send the paste shortcut", plan.Problem, StringComparison.Ordinal);
        Assert.Equal("left in the clipboard (xclip) for a manual paste, because " + plan.Problem, plan.Describe());
    }

    [Fact]
    public void With_the_fallback_off_no_key_sender_means_no_insertion()
    {
        // The default: the dictation is never left behind in the clipboard,
        // where any program could read it later.
        InjectionPlan plan = Paste(Context(Gnome, [.. WlClipboard, ToolLocator.Xclip]), fallback: false);

        Assert.Equal(InjectionOutcome.Impossible, plan.Outcome);
        Assert.Equal(ClipboardTool.None, plan.Clipboard);
    }

    [Fact]
    public void The_fallback_does_not_change_a_plan_that_works()
    {
        InjectionPlan plan = Paste(Context(Xfce, [ToolLocator.Xclip, ToolLocator.Xdotool]), fallback: true);

        Assert.Equal(InjectionOutcome.Insert, plan.Outcome);
        Assert.Equal(KeyStroker.Xdotool, plan.Keys);
    }

    [Fact]
    public void The_fallback_needs_a_clipboard_tool_and_then_reports_the_clipboard_problem()
    {
        InjectionPlan plan = Paste(Context(Sway, []), fallback: true);

        Assert.Equal(InjectionOutcome.Impossible, plan.Outcome);
        Assert.Contains("no clipboard tool was found", plan.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Typing_that_cannot_work_falls_back_on_the_clipboard_too()
    {
        // "insertion": "Type" on KDE: nothing can type, but the clipboard
        // works, and the user asked for the fallback.
        InjectionPlan plan = Type(Context(Kde, [.. WlClipboard]), fallback: true);

        Assert.Equal(InjectionOutcome.ClipboardOnly, plan.Outcome);
        Assert.Equal(InsertionMode.Type, plan.Mode);
        Assert.Equal(ClipboardTool.WlClipboard, plan.Clipboard);
        Assert.Contains("wlroots compositor and wtype", plan.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Typing_without_a_clipboard_reports_the_typing_problem_not_the_clipboard_one()
    {
        // The user asked to type: a missing clipboard tool is not what
        // stands in the way, and naming it would mislead.
        InjectionPlan plan = Type(Context(Kde, []), fallback: true);

        Assert.Equal(InjectionOutcome.Impossible, plan.Outcome);
        Assert.Contains("wlroots compositor and wtype", plan.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain("clipboard tool", plan.Problem, StringComparison.Ordinal);
    }

    // --- Describe -----------------------------------------------------------------

    [Fact]
    public void Describe_handles_every_plan_the_doctor_could_print()
    {
        // --doctor and the log print whatever plan comes out, including
        // shapes the planner does not produce today: none may throw.
        foreach (InsertionMode mode in Enum.GetValues<InsertionMode>())
        {
            foreach (InjectionOutcome outcome in Enum.GetValues<InjectionOutcome>())
            {
                foreach (ClipboardTool clipboard in Enum.GetValues<ClipboardTool>())
                {
                    foreach (KeyStroker keys in Enum.GetValues<KeyStroker>())
                    {
                        string described = new InjectionPlan(mode, outcome, clipboard, keys, "a reason").Describe();

                        Assert.False(string.IsNullOrWhiteSpace(described));
                    }
                }
            }
        }
    }

    [Fact]
    public void Impossible_builds_a_plan_that_uses_no_tool()
    {
        InjectionPlan plan = InjectionPlan.Impossible(InsertionMode.Type, "why");

        Assert.Equal(new InjectionPlan(InsertionMode.Type, InjectionOutcome.Impossible, ClipboardTool.None, KeyStroker.None, "why"), plan);
    }

    // --- Every combination --------------------------------------------------------

    [Fact]
    public void No_combination_ever_picks_a_tool_that_cannot_run()
    {
        // The sweep behind the cases above: every session, sender, mode,
        // fallback, permission, probe result and set of installed tools. A
        // plan naming a missing tool, or wtype where it was refused, is a
        // dictation that silently goes nowhere.
        DesktopSession[] sessions = [NoDisplay, Xfce, Gnome, GnomeWithoutXwayland, Kde, KdeWithoutXwayland, Sway];
        string[] tools = [ToolLocator.WlCopy, ToolLocator.WlPaste, ToolLocator.Xclip, ToolLocator.Xsel, ToolLocator.Xdotool, ToolLocator.Wtype];
        bool?[] probes = [null, false, true];
        int checkedPlans = 0;

        foreach (DesktopSession session in sessions)
        {
            for (int mask = 0; mask < 1 << tools.Length; mask++)
            {
                string[] installed = [.. tools.Where((_, index) => (mask & (1 << index)) != 0)];

                foreach (bool uinput in new[] { false, true })
                {
                    foreach (bool? probe in probes)
                    {
                        InjectionContext context = Context(session, installed, uinput, probe);

                        foreach (KeySender sender in Enum.GetValues<KeySender>())
                        {
                            foreach (InsertionMode mode in Enum.GetValues<InsertionMode>())
                            {
                                foreach (bool fallback in new[] { false, true })
                                {
                                    InjectionPlan plan = InjectionPlanner.Plan(mode, sender, fallback, context);
                                    string situation = $"{session} tools=[{string.Join(',', installed)}] uinput={uinput} probe={probe} sender={sender} mode={mode} fallback={fallback} -> {plan}";

                                    CheckPlan(plan, context, sender, mode, fallback, situation);
                                    checkedPlans++;
                                }
                            }
                        }
                    }
                }
            }
        }

        Assert.Equal(7 * 64 * 2 * 3 * 4 * 2 * 2, checkedPlans);
    }

    private static void CheckPlan(InjectionPlan plan, InjectionContext context, KeySender sender, InsertionMode mode, bool fallback, string situation)
    {
        Require(plan.Mode == mode, situation);

        switch (plan.Outcome)
        {
            case InjectionOutcome.Insert:
                Require(plan.Problem is null && plan.Keys != KeyStroker.None, situation);
                Require(mode == InsertionMode.Paste ? plan.Clipboard != ClipboardTool.None : plan.Clipboard == ClipboardTool.None, situation);
                Require(mode == InsertionMode.Paste || plan.Keys != KeyStroker.Uinput, situation);
                break;

            case InjectionOutcome.ClipboardOnly:
                Require(fallback && plan.Clipboard != ClipboardTool.None && plan.Keys == KeyStroker.None, situation);
                Require(!string.IsNullOrWhiteSpace(plan.Problem), situation);
                break;

            default:
                Require(plan.Clipboard == ClipboardTool.None && plan.Keys == KeyStroker.None, situation);
                Require(!string.IsNullOrWhiteSpace(plan.Problem), situation);
                break;
        }

        DesktopSession session = context.Session;
        Require(session.Server != DisplayServer.None || plan.Outcome == InjectionOutcome.Impossible, situation);

        switch (plan.Clipboard)
        {
            case ClipboardTool.WlClipboard:
                Require(session.Server == DisplayServer.Wayland && context.Has(ToolLocator.WlCopy) && context.Has(ToolLocator.WlPaste), situation);
                break;
            case ClipboardTool.Xclip:
                Require(session.HasX11Display && context.Has(ToolLocator.Xclip), situation);
                break;
            case ClipboardTool.Xsel:
                Require(session.HasX11Display && context.Has(ToolLocator.Xsel), situation);
                break;
        }

        switch (plan.Keys)
        {
            case KeyStroker.Uinput:
                Require(context.UinputWritable, situation);
                break;
            case KeyStroker.Xdotool:
                Require(session.HasX11Display && context.Has(ToolLocator.Xdotool), situation);
                Require(sender is KeySender.Auto or KeySender.Xdotool, situation);

                // Under Wayland xdotool reaches XWayland windows only: never
                // an automatic choice there.
                Require(sender == KeySender.Xdotool || session.Server == DisplayServer.X11, situation);
                break;
            case KeyStroker.Wtype:
                Require(session.Server == DisplayServer.Wayland && context.Has(ToolLocator.Wtype) && context.WtypeWorks != false, situation);
                Require(sender == KeySender.Wtype || context.WtypeWorks == true, situation);
                break;
        }

        // A forced sender is never swapped for another one.
        if (sender != KeySender.Auto && plan.Keys != KeyStroker.None)
        {
            Require(plan.Keys.ToString() == sender.ToString(), situation);
        }
    }

    private static void Require(bool condition, string situation)
    {
        if (!condition)
        {
            Assert.Fail(situation);
        }
    }
}
