using HexLinux.Configuration;
using HexLinux.Diagnostics;
using HexLinux.Output;
using HexLinux.Platform;
using HexLinux.Session;
using Xunit;

namespace HexLinux.Tests.Diagnostics;

/// <summary>
/// <c>hexlinux --doctor</c> is the first thing the README, the release notes
/// and the bug report ask for, and the only place that says what a given
/// desktop allows: each Linux desktop needs a different mix of tools and
/// permissions, and none of them can be tried from one machine. These cases
/// build the facts of the real desktops — WSLg, Ubuntu GNOME with and without
/// the udev rule, KDE, Sway, a plain X11 session — and check two things: the
/// line a user reads tells them what to do, and the exit code is not zero
/// only when dictation cannot work at all (a keyboard that cannot be read is
/// not such a case: <c>hexlinux --toggle</c> still dictates).
/// </summary>
public class DoctorTests
{
    private const string SettingsPath = "/home/ada/.config/hexlinux/settings.json";
    private const string ModelFolder = "/home/ada/.local/share/hexlinux/models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8";
    private const string Laptop = "AT Translated Set 2 keyboard";
    private const string External = "Logitech USB Keyboard";
    private const string UdevHint = "run scripts/install-udev-rules.sh, or bind \"hexlinux --toggle\" to a desktop shortcut";

    // --- The machines ------------------------------------------------------------

    /// <summary>A working X11 desktop, the base the other machines vary.</summary>
    private static DoctorFacts X11Desktop(AppSettings? settings = null) => new()
    {
        Session = new DesktopSession(DisplayServer.X11, true, "XFCE"),
        SettingsFile = SettingsPath,
        SettingsFileExists = true,
        Settings = settings ?? new AppSettings(),
        ModelDirectory = ModelFolder,
        Keyboards = [Keyboard(Laptop, 3, DeviceAccess.Accessible)],
        Uinput = DeviceAccess.PermissionDenied,
        Tools = ToolSet(ToolLocator.Xclip, ToolLocator.Xdotool, ToolLocator.NotifySend, ToolLocator.Loginctl),
        Audio = AudioStatus.Works,
        Guard = Logind("Active=yes\nLockedHint=no\nType=x11\nSeat=seat0\nRemote=no\n"),
    };

    /// <summary>
    /// WSL with systemd, as found while building HexLinux: logind mounts a
    /// fresh /run/user/0 over the folder that held WSLg's socket, so
    /// WAYLAND_DISPLAY=wayland-0 leads nowhere while DISPLAY=:0 works. There
    /// is no /dev/input and no /dev/uinput at all.
    /// </summary>
    private static DoctorFacts WslgDefault() => X11Desktop() with
    {
        Session = new DesktopSession(DisplayServer.X11, true, string.Empty),
        WaylandSocket = "/run/user/0/wayland-0",
        WaylandSocketExists = false,
        Keyboards = [],
        Uinput = DeviceAccess.Missing,
        Tools = ToolSet(ToolLocator.WlCopy, ToolLocator.WlPaste, ToolLocator.Xclip, ToolLocator.Xdotool, ToolLocator.Wtype, ToolLocator.Loginctl),
        WtypeWorks = null,
    };

    /// <summary>
    /// WSLg with WAYLAND_DISPLAY pointed at /mnt/wslg/runtime-dir/wayland-0:
    /// Weston answers, and refuses wtype (no virtual-keyboard protocol,
    /// verified while building).
    /// </summary>
    private static DoctorFacts WslgWayland() => WslgDefault() with
    {
        Session = new DesktopSession(DisplayServer.Wayland, true, string.Empty),
        WaylandSocket = "/mnt/wslg/runtime-dir/wayland-0",
        WaylandSocketExists = true,
        WtypeWorks = false,
    };

    /// <summary>
    /// Ubuntu's GNOME on Wayland. Without the udev rule neither the keyboards
    /// nor /dev/uinput can be opened; with it (installed with --with-uinput)
    /// both can.
    /// </summary>
    private static DoctorFacts GnomeWayland(bool udevRule) => X11Desktop() with
    {
        Session = new DesktopSession(DisplayServer.Wayland, true, "ubuntu:GNOME"),
        WaylandSocket = "/run/user/1000/wayland-0",
        WaylandSocketExists = true,
        Keyboards = TwoKeyboards(udevRule ? DeviceAccess.Accessible : DeviceAccess.PermissionDenied),
        Uinput = udevRule ? DeviceAccess.Accessible : DeviceAccess.PermissionDenied,
        Tools = ToolSet(
            ToolLocator.WlCopy,
            ToolLocator.WlPaste,
            ToolLocator.Xclip,
            ToolLocator.NotifySend,
            ToolLocator.Loginctl,
            ToolLocator.XdgOpen,
            ToolLocator.Localectl,
            ToolLocator.Gsettings),
        Guard = Logind("Active=yes\nLockedHint=no\nType=wayland\nSeat=seat0\nRemote=no\n"),
        Layouts = ["fr"],
    };

    /// <summary>KDE Plasma on Wayland: KWin refuses wtype, the udev rule is installed.</summary>
    private static DoctorFacts KdeWayland(AppSettings? settings = null) => GnomeWayland(udevRule: true) with
    {
        Session = new DesktopSession(DisplayServer.Wayland, true, "KDE"),
        Settings = settings ?? new AppSettings(),
        Tools = ToolSet(ToolLocator.WlCopy, ToolLocator.WlPaste, ToolLocator.Wtype, ToolLocator.NotifySend, ToolLocator.Loginctl),
        WtypeWorks = false,
    };

    /// <summary>Sway: wtype works, and no udev rule was installed.</summary>
    private static DoctorFacts Sway() => GnomeWayland(udevRule: false) with
    {
        Session = new DesktopSession(DisplayServer.Wayland, true, "sway"),
        Tools = ToolSet(ToolLocator.WlCopy, ToolLocator.WlPaste, ToolLocator.Wtype, ToolLocator.Loginctl),
        WtypeWorks = true,
    };

    private static DoctorFacts Machine(string name) => name switch
    {
        "x11" => X11Desktop(),
        "wslg" => WslgDefault(),
        "wslg-wayland" => WslgWayland(),
        "gnome" => GnomeWayland(udevRule: false),
        "gnome-udev" => GnomeWayland(udevRule: true),
        "kde-type" => KdeWayland(new AppSettings { Insertion = InsertionMode.Type }),
        "sway" => Sway(),
        "no-clipboard" => X11Desktop() with { Tools = ToolSet(ToolLocator.Xdotool) },
        "no-model" => GnomeWayland(udevRule: true) with { ModelDirectory = null },
        "no-microphone" => X11Desktop() with { Audio = AudioStatus.LibraryMissing },
        "no-session" => X11Desktop() with { Session = new DesktopSession(DisplayServer.None, false, string.Empty), Tools = ToolSet() },
        "logind-unanswered" => X11Desktop() with { Guard = LogindUnanswered() },
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown machine"),
    };

    // --- Helpers ---------------------------------------------------------------------

    private static KeyboardFact Keyboard(string name, int eventNumber, DeviceAccess access) =>
        new(name, $"/dev/input/event{eventNumber}", access);

    private static KeyboardFact[] TwoKeyboards(DeviceAccess access) =>
        [Keyboard(Laptop, 3, access), Keyboard(External, 5, access)];

    private static Dictionary<string, string> ToolSet(params string[] names) =>
        names.ToDictionary(name => name, name => "/usr/bin/" + name, StringComparer.Ordinal);

    /// <summary>What the session guard concludes when loginctl answers <paramref name="showSession"/> for session 2.</summary>
    private static GuardDecision Logind(string showSession) =>
        SessionGuardPolicy.Decide(true, "2", _ => new LoginctlAnswer(true, showSession), () => LoginctlAnswer.Failed);

    private static GuardDecision LogindAbsent() =>
        SessionGuardPolicy.Decide(false, null, _ => LoginctlAnswer.Failed, () => LoginctlAnswer.Failed);

    private static GuardDecision LogindUnanswered() =>
        SessionGuardPolicy.Decide(true, "2", _ => LoginctlAnswer.Failed, () => LoginctlAnswer.Failed);

    private static DoctorReport Evaluate(DoctorFacts facts) => DoctorEvaluation.Evaluate(facts);

    private static DoctorCheck Only(DoctorReport report, string area) =>
        Assert.Single(report.Checks, check => check.Area == area);

    private static DoctorCheck Line(DoctorReport report, string area, string fragment) =>
        Assert.Single(report.Checks, check => check.Area == area && check.Summary.Contains(fragment, StringComparison.Ordinal));

    private static void AssertLine(DoctorReport report, string area, CheckStatus status, string fragment)
    {
        DoctorCheck check = Line(report, area, fragment);
        Assert.Equal(status, check.Status);
    }

    // --- Real machines, end to end -------------------------------------------------

    [Theory]
    [InlineData("x11", 0)]
    [InlineData("wslg", 0)]
    [InlineData("wslg-wayland", 3)]
    [InlineData("gnome", 3)]
    [InlineData("gnome-udev", 0)]
    [InlineData("kde-type", 3)]
    [InlineData("sway", 0)]
    [InlineData("no-clipboard", 3)]
    [InlineData("no-model", 2)]
    [InlineData("no-microphone", 3)]
    [InlineData("no-session", 3)]
    [InlineData("logind-unanswered", 3)]
    public void The_exit_code_is_not_zero_exactly_when_a_line_says_FAIL(string machine, int exitCode)
    {
        // Scripts and the bug report read the exit code; people read the
        // lines. A FAIL with exit code 0, or an exit code with no FAIL to say
        // why, would send each of them a different way.
        DoctorReport report = Evaluate(Machine(machine));

        Assert.Equal(exitCode, report.ExitCode);
        Assert.Equal(report.ExitCode != 0, report.Checks.Any(check => check.Status == CheckStatus.Error));
    }

    [Fact]
    public void Wslg_with_a_stale_wayland_socket_says_x11_is_used_and_inserts_through_xclip_and_xdotool()
    {
        // The default WSL setup this was built on: the variable names a socket
        // logind's tmpfs has hidden. The doctor must say so and point at the
        // socket that works, instead of letting wl-clipboard fail silently.
        DoctorReport report = Evaluate(WslgDefault());

        AssertLine(report, "session", CheckStatus.Ok, "X11 (desktop unknown)");
        DoctorCheck socket = Line(report, "session", "WAYLAND_DISPLAY names /run/user/0/wayland-0");
        Assert.Equal(CheckStatus.Warning, socket.Status);
        Assert.Contains("which does not exist: Wayland tools cannot connect, so X11 is used instead", socket.Summary, StringComparison.Ordinal);
        Assert.Contains("WAYLAND_DISPLAY=/mnt/wslg/runtime-dir/wayland-0", socket.Hint, StringComparison.Ordinal);

        // No /dev/input under WSL: reported, not fatal.
        AssertLine(report, "keyboards", CheckStatus.Warning, "no keyboard able to send Right Ctrl was found: the shortcut is unavailable");
        Assert.Contains("hexlinux --toggle", Only(report, "keyboards").Hint, StringComparison.Ordinal);

        // X11 needs no virtual keyboard: its absence is information only.
        AssertLine(report, "uinput", CheckStatus.Info, "/dev/uinput does not exist");

        Assert.Equal("clipboard xclip, keys xdotool (configured)", Only(report, "paste").Summary);
        Assert.Equal(CheckStatus.Ok, Only(report, "paste").Status);
        Assert.Equal("typed by xdotool", Only(report, "type").Summary);
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public void Wslg_with_its_wayland_socket_reached_has_no_key_sender_because_weston_refuses_wtype()
    {
        // WAYLAND_DISPLAY=/mnt/wslg/runtime-dir/wayland-0 makes Wayland win;
        // then Weston refuses wtype and there is no /dev/uinput: paste cannot
        // work, and that is essential.
        DoctorReport report = Evaluate(WslgWayland());

        AssertLine(report, "session", CheckStatus.Ok, "Wayland (desktop unknown), with an X11 display for XWayland");
        Assert.DoesNotContain(report.Checks, check => check.Summary.StartsWith("WAYLAND_DISPLAY names", StringComparison.Ordinal));
        AssertLine(report, "tools", CheckStatus.Info, "wtype is installed but this compositor refuses it");

        // Under Wayland the missing virtual keyboard matters.
        AssertLine(report, "uinput", CheckStatus.Warning, "/dev/uinput does not exist");

        AssertLine(report, "paste", CheckStatus.Error, "impossible: nothing can send the paste shortcut");
        Assert.EndsWith("(configured)", Only(report, "paste").Summary, StringComparison.Ordinal);
        AssertLine(report, "type", CheckStatus.Warning, "wtype was refused");
        Assert.Equal(3, report.ExitCode);
    }

    [Fact]
    public void Forcing_xdotool_under_wslg_wayland_inserts_through_xwayland()
    {
        // The documented way out when the automatic choice is wrong for a
        // desktop: "keySender": "Xdotool" reaches XWayland windows.
        DoctorReport report = Evaluate(WslgWayland() with { Settings = new AppSettings { KeySender = KeySender.Xdotool } });

        Assert.Equal("clipboard wl-clipboard, keys xdotool (configured)", Only(report, "paste").Summary);
        Assert.Equal("typed by xdotool", Only(report, "type").Summary);
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public void Gnome_wayland_without_the_udev_rule_names_both_permissions_and_fails_on_paste()
    {
        // A fresh Ubuntu: the keyboards are root-only and GNOME accepts no
        // simulated keys, so HexLinux's own virtual keyboard is the only way
        // to paste. The user must learn both, with the script that fixes them.
        DoctorReport report = Evaluate(GnomeWayland(udevRule: false));

        AssertLine(report, "session", CheckStatus.Ok, "Wayland (ubuntu:GNOME)");

        DoctorCheck keyboards = Only(report, "keyboards");
        Assert.Equal(CheckStatus.Warning, keyboards.Status);
        Assert.Equal("2 keyboard(s) found, none readable (permission denied): the shortcut is unavailable", keyboards.Summary);
        Assert.Equal(UdevHint, keyboards.Hint);

        DoctorCheck uinput = Only(report, "uinput");
        Assert.Equal(CheckStatus.Warning, uinput.Status);
        Assert.Equal("/dev/uinput is not writable: needed only to paste under GNOME or KDE Wayland", uinput.Summary);
        Assert.Equal("scripts/install-udev-rules.sh --with-uinput", uinput.Hint);

        AssertLine(report, "paste", CheckStatus.Error, "GNOME accepts no simulated keys from applications");
        AssertLine(report, "paste", CheckStatus.Error, "install-udev-rules.sh --with-uinput");
        Assert.Equal(3, report.ExitCode);
    }

    [Fact]
    public void Gnome_wayland_with_the_udev_rule_pastes_through_xclip_and_the_virtual_keyboard()
    {
        // After scripts/install-udev-rules.sh --with-uinput and a new login:
        // every keyboard is named, the paste goes through XWayland's clipboard
        // (Mutter offers wl-clipboard no data-control protocol) and uinput.
        DoctorReport report = Evaluate(GnomeWayland(udevRule: true));

        Assert.Equal(
            "2 readable: AT Translated Set 2 keyboard (/dev/input/event3), Logitech USB Keyboard (/dev/input/event5)",
            Only(report, "keyboards").Summary);
        Assert.Equal(CheckStatus.Ok, Only(report, "keyboards").Status);
        AssertLine(report, "uinput", CheckStatus.Ok, "writable");

        Assert.Equal("clipboard xclip, keys uinput (configured)", Only(report, "paste").Summary);
        Assert.Equal(ClipboardTool.Xclip, report.PastePlan.Clipboard);
        Assert.Equal(KeyStroker.Uinput, report.PastePlan.Keys);

        // Typing is not configured: a warning that says why, not a failure.
        AssertLine(report, "type", CheckStatus.Warning, "typing under Wayland needs a wlroots compositor and wtype");
        Assert.Equal(InjectionOutcome.Impossible, report.TypePlan.Outcome);

        AssertLine(report, "logind", CheckStatus.Ok, "session 2: active, unlocked (wayland, seat0)");
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public void A_udev_rule_installed_without_uinput_gives_the_shortcut_but_still_no_paste_under_gnome()
    {
        // The keyboard rule alone: the shortcut works, the text cannot land.
        DoctorReport report = Evaluate(GnomeWayland(udevRule: true) with { Uinput = DeviceAccess.PermissionDenied });

        Assert.Equal(CheckStatus.Ok, Only(report, "keyboards").Status);
        Assert.Equal(CheckStatus.Warning, Only(report, "uinput").Status);
        AssertLine(report, "paste", CheckStatus.Error, "GNOME accepts no simulated keys");
        Assert.Equal(3, report.ExitCode);
    }

    [Fact]
    public void Kde_wayland_pastes_through_uinput_but_cannot_type_because_kwin_refuses_wtype()
    {
        // KWin has no virtual-keyboard protocol: with "insertion": "Type"
        // nothing can be inserted, although Paste would work.
        DoctorReport report = Evaluate(KdeWayland(new AppSettings { Insertion = InsertionMode.Type }));

        DoctorCheck paste = Only(report, "paste");
        Assert.Equal(CheckStatus.Ok, paste.Status);
        Assert.Equal("clipboard wl-clipboard, keys uinput", paste.Summary);

        DoctorCheck type = Only(report, "type");
        Assert.Equal(CheckStatus.Error, type.Status);
        Assert.StartsWith("impossible: this compositor accepts no simulated typing (wtype was refused)", type.Summary, StringComparison.Ordinal);
        Assert.EndsWith("(configured)", type.Summary, StringComparison.Ordinal);
        Assert.Equal(3, report.ExitCode);
    }

    [Fact]
    public void Kde_wayland_with_paste_configured_has_nothing_essential_missing()
    {
        // The same desktop with the default mode: the Type line stays a warning.
        DoctorReport report = Evaluate(KdeWayland());

        Assert.Equal(CheckStatus.Warning, Only(report, "type").Status);
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public void Sway_pastes_and_types_through_wtype_without_any_permission()
    {
        // wlroots accepts wtype's virtual keyboard: no udev rule is needed to
        // insert, only to read the keyboards.
        DoctorReport report = Evaluate(Sway());

        Assert.Equal("clipboard wl-clipboard, keys wtype (configured)", Only(report, "paste").Summary);
        Assert.Equal("typed by wtype", Only(report, "type").Summary);
        Assert.Equal(CheckStatus.Ok, Only(report, "type").Status);
        Assert.DoesNotContain(report.Checks, check => check.Summary.Contains("refuses it", StringComparison.Ordinal));
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public void An_x11_session_with_xdotool_needs_no_permission_to_insert()
    {
        // X11 lets any client send keys: /dev/uinput closed is information,
        // not a warning.
        DoctorReport report = Evaluate(X11Desktop());

        AssertLine(report, "session", CheckStatus.Ok, "X11 (XFCE)");
        Assert.Single(report.Checks, check => check.Area == "session");
        AssertLine(report, "uinput", CheckStatus.Info, "/dev/uinput is not writable");
        Assert.Equal("clipboard xclip, keys xdotool (configured)", Only(report, "paste").Summary);
        Assert.Equal(KeyStroker.Xdotool, report.PastePlan.Keys);
        Assert.Equal(KeyStroker.Xdotool, report.TypePlan.Keys);
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public void Unreadable_keyboards_are_a_warning_never_an_essential()
    {
        // Permission denied on /dev/input is the normal state before the udev
        // rule; "hexlinux --toggle" bound to a desktop shortcut still dictates.
        DoctorReport report = Evaluate(X11Desktop() with { Keyboards = TwoKeyboards(DeviceAccess.PermissionDenied) });

        Assert.Equal(CheckStatus.Warning, Only(report, "keyboards").Status);
        Assert.Equal(0, report.ExitCode);
        Assert.Contains("Nothing essential is missing.", report.Render(), StringComparison.Ordinal);
    }

    [Fact]
    public void Without_any_session_both_plans_fail_and_the_session_line_says_where_to_start()
    {
        // A bare systemd service or an SSH login: no display to insert into.
        DoctorReport report = Evaluate(Machine("no-session"));

        DoctorCheck session = Only(report, "session");
        Assert.Equal(CheckStatus.Warning, session.Status);
        Assert.Contains("no graphical session", session.Summary, StringComparison.Ordinal);
        Assert.Equal("start HexLinux from the desktop session", session.Hint);
        AssertLine(report, "paste", CheckStatus.Error, "there is no graphical session");
        AssertLine(report, "type", CheckStatus.Warning, "there is no graphical session");
        Assert.Equal(3, report.ExitCode);
    }

    // --- Session ---------------------------------------------------------------------

    [Fact]
    public void A_wayland_session_without_x11_does_not_mention_xwayland()
    {
        // A Sway set up without XWayland: DISPLAY is unset.
        DoctorReport report = Evaluate(Sway() with { Session = new DesktopSession(DisplayServer.Wayland, false, "sway") });

        Assert.Equal("Wayland (sway)", Only(report, "session").Summary);
    }

    [Fact]
    public void A_missing_wayland_socket_without_x11_does_not_promise_an_x11_fallback()
    {
        // Same stale variable, but no DISPLAY either: nothing to fall back on.
        DoctorReport report = Evaluate(WslgDefault() with { Session = new DesktopSession(DisplayServer.None, false, string.Empty) });

        DoctorCheck socket = Line(report, "session", "WAYLAND_DISPLAY names");
        Assert.EndsWith("Wayland tools cannot connect", socket.Summary, StringComparison.Ordinal);
    }

    // --- Settings ------------------------------------------------------------------

    [Fact]
    public void The_settings_line_shows_the_file_in_use()
    {
        // The user edits that file: its full path is what they need.
        DoctorCheck settings = Only(Evaluate(X11Desktop()), "settings");

        Assert.Equal(CheckStatus.Ok, settings.Status);
        Assert.Equal(SettingsPath, settings.Summary);
    }

    [Fact]
    public void A_settings_file_not_created_yet_is_information_with_the_defaults_in_use()
    {
        DoctorReport report = Evaluate(X11Desktop() with { SettingsFileExists = false });

        DoctorCheck settings = Only(report, "settings");
        Assert.Equal(CheckStatus.Info, settings.Status);
        Assert.Equal(SettingsPath + " does not exist yet: defaults in use", settings.Summary);
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public void Every_rejected_setting_is_listed_as_a_warning_without_failing_the_doctor()
    {
        // A settings.json carried over from HexWin: a HexWin-only key, a
        // shortcut Linux refuses, a GPU provider. Each fell back to its
        // default; a setting that silently does nothing is the hardest fault
        // to find, so each is listed (Q8), and none stops dictation.
        AppSettings settings = AppSettings.Parse(
            """{ "feedbackColor": "#FF0000", "hotkey": ["Space"], "provider": "cuda" }""",
            out IReadOnlyList<string> notes);

        DoctorReport report = Evaluate(X11Desktop(settings) with { SettingsNotes = notes });

        DoctorCheck[] warnings = [.. report.Checks.Where(check => check.Area == "settings" && check.Status == CheckStatus.Warning)];
        Assert.Equal(notes, warnings.Select(check => check.Summary));
        Assert.Equal(3, warnings.Length);
        Assert.Contains(warnings, check => check.Summary.Contains("\"feedbackColor\"", StringComparison.Ordinal));
        Assert.Equal(0, report.ExitCode);
    }

    // --- Model -----------------------------------------------------------------------

    [Fact]
    public void A_missing_model_is_exit_code_2_with_the_download_script_named()
    {
        // The first run after extracting the release, before get-model.sh.
        DoctorReport report = Evaluate(Machine("no-model"));

        DoctorCheck model = Only(report, "model");
        Assert.Equal(CheckStatus.Error, model.Status);
        Assert.Equal(
            "\"models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8\" was found neither in the data folder nor next to the executable",
            model.Summary);
        Assert.Equal("download it with scripts/get-model.sh", model.Hint);
        Assert.Equal(2, report.ExitCode);
        Assert.EndsWith("Something essential is missing (exit code 2).\n", report.Render(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_incomplete_model_lists_every_problem_and_asks_for_a_forced_download()
    {
        // An interrupted download left a truncated encoder and no vocabulary.
        string[] problems =
        [
            ModelFolder + "/encoder.int8.onnx is only 1048576 bytes, too small to be a model file",
            ModelFolder + "/tokens.txt is missing",
        ];

        DoctorReport report = Evaluate(X11Desktop() with { ModelProblems = problems });

        DoctorCheck model = Only(report, "model");
        Assert.Equal(CheckStatus.Error, model.Status);
        Assert.Equal("incomplete: " + problems[0] + "; " + problems[1], model.Summary);
        Assert.Equal("download it again with scripts/get-model.sh --force", model.Hint);
        Assert.Equal(2, report.ExitCode);
    }

    [Fact]
    public void A_found_model_shows_its_folder()
    {
        DoctorCheck model = Only(Evaluate(X11Desktop()), "model");

        Assert.Equal(CheckStatus.Ok, model.Status);
        Assert.Equal(ModelFolder, model.Summary);
    }

    [Fact]
    public void The_missing_model_wins_the_exit_code_over_every_other_failure()
    {
        // Exit code 2 is the one HexWin's users know: "get the model first".
        DoctorReport report = Evaluate(Machine("no-session") with
        {
            ModelDirectory = null,
            Audio = AudioStatus.Failed,
            Guard = LogindUnanswered(),
        });

        Assert.Equal(2, report.ExitCode);
        Assert.Equal(CheckStatus.Error, Only(report, "audio").Status);
    }

    // --- Keyboards and shortcut ------------------------------------------------------

    [Fact]
    public void Keyboards_that_vanished_or_failed_are_not_blamed_on_permissions()
    {
        // A device unplugged between the listing and the opening, or an I/O
        // error: saying "permission denied" would send the user to udev.
        DoctorReport report = Evaluate(X11Desktop() with
        {
            Keyboards = [Keyboard(Laptop, 3, DeviceAccess.Missing), Keyboard(External, 5, DeviceAccess.Failed)],
        });

        DoctorCheck keyboards = Only(report, "keyboards");
        Assert.Equal(CheckStatus.Warning, keyboards.Status);
        Assert.Equal("2 keyboard(s) found, none readable: the shortcut is unavailable", keyboards.Summary);
    }

    [Fact]
    public void Some_readable_keyboards_are_named_and_the_others_counted()
    {
        // The shortcut works from the laptop keyboard only: the user must know
        // which one, and that the other needs the rule too.
        DoctorReport report = Evaluate(X11Desktop() with
        {
            Keyboards = [Keyboard(Laptop, 3, DeviceAccess.Accessible), Keyboard(External, 5, DeviceAccess.PermissionDenied)],
        });

        DoctorCheck keyboards = Only(report, "keyboards");
        Assert.Equal(CheckStatus.Warning, keyboards.Status);
        Assert.Equal("1 readable (AT Translated Set 2 keyboard (/dev/input/event3)), 1 not readable", keyboards.Summary);
        Assert.Equal(UdevHint, keyboards.Hint);
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public void The_shortcut_is_named_as_printed_on_the_keyboard_and_the_default_has_no_caveat()
    {
        // Right Ctrl was chosen because it costs nothing, even with
        // segmentation on (F38).
        DoctorReport report = Evaluate(X11Desktop(new AppSettings { Segmentation = true }));

        DoctorCheck shortcut = Only(report, "shortcut");
        Assert.Equal(CheckStatus.Info, shortcut.Status);
        Assert.Equal("Right Ctrl", shortcut.Summary);
    }

    [Fact]
    public void Right_alt_with_segmentation_gets_both_of_its_caveats_as_warnings()
    {
        // AltGr on French and German layouts cancels the dictation at each @
        // or euro sign, and Alt held during a segment's paste turns Ctrl+V into
        // another shortcut (F15, F26).
        DoctorReport report = Evaluate(X11Desktop(new AppSettings { Hotkey = ["RightAlt"], Segmentation = true }));

        DoctorCheck[] shortcut = [.. report.Checks.Where(check => check.Area == "shortcut")];
        Assert.Equal(3, shortcut.Length);
        Assert.Equal("Right Alt", shortcut[0].Summary);
        Assert.Contains(shortcut, check => check.Status == CheckStatus.Warning && check.Summary.Contains("AltGr", StringComparison.Ordinal));
        Assert.Contains(shortcut, check => check.Status == CheckStatus.Warning && check.Summary.Contains("\"segmentation\"", StringComparison.Ordinal));
        Assert.Equal(0, report.ExitCode);
    }

    // --- uinput --------------------------------------------------------------------

    [Theory]
    [InlineData(DeviceAccess.PermissionDenied, "/dev/uinput is not writable")]
    [InlineData(DeviceAccess.Missing, "/dev/uinput does not exist (the uinput module is not loaded)")]
    [InlineData(DeviceAccess.Failed, "/dev/uinput could not be opened")]
    public void An_unusable_uinput_is_a_warning_under_wayland_only(DeviceAccess access, string fragment)
    {
        // Under Wayland the virtual keyboard may be the only sender; under X11
        // xdotool makes it unnecessary.
        AssertLine(Evaluate(X11Desktop() with { Uinput = access }), "uinput", CheckStatus.Info, fragment);
        AssertLine(Evaluate(Sway() with { Uinput = access }), "uinput", CheckStatus.Warning, fragment);
    }

    // --- Tools -----------------------------------------------------------------------

    [Fact]
    public void The_tools_line_lists_found_and_missing_tools_in_a_fixed_order()
    {
        // The bug report pastes this line: the same order on every machine
        // makes two reports comparable at a glance.
        DoctorCheck tools = Only(Evaluate(X11Desktop()), "tools");

        Assert.Equal(CheckStatus.Info, tools.Status);
        Assert.Equal(
            "found: xclip xdotool notify-send loginctl; missing: wl-copy wl-paste xsel wtype xdg-open localectl gsettings",
            tools.Summary);
    }

    [Fact]
    public void The_tools_line_says_none_rather_than_leaving_a_blank()
    {
        DoctorCheck nothing = Only(Evaluate(Machine("no-session")), "tools");
        DoctorCheck everything = Only(Evaluate(X11Desktop() with { Tools = ToolSet([.. ToolLocator.Known]) }), "tools");

        Assert.StartsWith("found: none; missing: wl-copy ", nothing.Summary, StringComparison.Ordinal);
        Assert.EndsWith("; missing: none", everything.Summary, StringComparison.Ordinal);
    }

    // --- Audio -----------------------------------------------------------------------

    [Fact]
    public void A_working_microphone_is_ok()
    {
        AssertLine(Evaluate(X11Desktop()), "audio", CheckStatus.Ok, "the microphone opens");
    }

    [Fact]
    public void A_missing_libpulse_is_essential_and_names_the_package()
    {
        // A minimal install without libpulse0: PipeWire alone is not enough,
        // pipewire-pulse is reached through that library.
        DoctorReport report = Evaluate(Machine("no-microphone"));

        DoctorCheck audio = Only(report, "audio");
        Assert.Equal(CheckStatus.Error, audio.Status);
        Assert.Equal("libpulse-simple.so.0 is missing", audio.Summary);
        Assert.Contains("install libpulse0", audio.Hint, StringComparison.Ordinal);
        Assert.Equal(3, report.ExitCode);
    }

    [Fact]
    public void A_microphone_that_cannot_open_shows_the_sound_server_answer()
    {
        // The probe's own words ("the sound server did not answer within 5 s",
        // pa_strerror's message) are what tells a stopped server from a
        // missing source.
        DoctorReport report = Evaluate(X11Desktop() with
        {
            Audio = AudioStatus.Failed,
            AudioDetail = "the sound server did not answer within 5 s",
        });

        DoctorCheck audio = Only(report, "audio");
        Assert.Equal(CheckStatus.Error, audio.Status);
        Assert.Equal("the microphone cannot be opened: the sound server did not answer within 5 s", audio.Summary);
        Assert.Contains("pactl get-default-source", audio.Hint, StringComparison.Ordinal);
        Assert.Equal(3, report.ExitCode);
    }

    [Fact]
    public void A_microphone_failure_without_detail_still_says_something()
    {
        DoctorCheck audio = Only(Evaluate(X11Desktop() with { Audio = AudioStatus.Failed, AudioDetail = null }), "audio");

        Assert.Equal("the microphone cannot be opened: unknown error", audio.Summary);
    }

    // --- Clipboard tools -----------------------------------------------------------

    [Theory]
    [InlineData("x11", "install xclip, or set \"insertion\" to \"Type\"")]
    [InlineData("gnome", "install xclip (GNOME) or wl-clipboard")]
    [InlineData("sway", "install wl-clipboard,")]
    public void Without_any_clipboard_tool_paste_fails_and_names_what_to_install_on_that_desktop(string machine, string fragment)
    {
        // Neither wl-clipboard nor xclip ships with every desktop: the line
        // names the package that works there.
        DoctorFacts facts = Machine(machine);
        string[] kept = [.. facts.Tools.Keys.Where(tool => tool is not (ToolLocator.WlCopy or ToolLocator.WlPaste or ToolLocator.Xclip or ToolLocator.Xsel))];
        DoctorReport report = Evaluate(facts with { Tools = ToolSet(kept), Uinput = DeviceAccess.Accessible });

        AssertLine(report, "paste", CheckStatus.Error, "no clipboard tool was found: " + fragment);
        Assert.Equal(3, report.ExitCode);
    }

    [Fact]
    public void Typing_rescues_a_machine_without_clipboard_tool_when_it_is_the_configured_mode()
    {
        // The hint says "or set insertion to Type": once done, the missing
        // clipboard tool is no longer essential.
        DoctorReport report = Evaluate(Machine("no-clipboard") with { Settings = new AppSettings { Insertion = InsertionMode.Type } });

        AssertLine(report, "paste", CheckStatus.Warning, "no clipboard tool was found");
        Assert.Equal("typed by xdotool (configured)", Only(report, "type").Summary);
        Assert.Equal(0, report.ExitCode);
    }

    // --- clipboardFallback, Shift+Insert, layouts ------------------------------------

    [Fact]
    public void Clipboard_fallback_turns_an_impossible_paste_into_a_warning_and_recalls_the_trade_off()
    {
        // GNOME without the uinput rule, with "clipboardFallback": true: the
        // text is left for a manual Ctrl+V — dictation works, and the user is
        // reminded that a clipboard history may keep what was dictated.
        DoctorReport report = Evaluate(GnomeWayland(udevRule: false) with
        {
            Settings = new AppSettings { ClipboardFallback = true },
        });

        DoctorCheck paste = Only(report, "paste");
        Assert.Equal(CheckStatus.Warning, paste.Status);
        Assert.StartsWith("left in the clipboard (xclip) for a manual paste, because nothing can send the paste shortcut", paste.Summary, StringComparison.Ordinal);
        Assert.Equal(InjectionOutcome.ClipboardOnly, report.PastePlan.Outcome);

        AssertLine(report, "insertion", CheckStatus.Info, "clipboardFallback is on");
        AssertLine(report, "insertion", CheckStatus.Info, "a clipboard history may keep it");
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public void Clipboard_fallback_cannot_help_without_a_clipboard_tool()
    {
        DoctorReport report = Evaluate(Machine("no-clipboard") with
        {
            Settings = new AppSettings { ClipboardFallback = true },
            Tools = ToolSet(),
        });

        AssertLine(report, "paste", CheckStatus.Error, "no clipboard tool was found");
        Assert.Equal(3, report.ExitCode);
    }

    [Fact]
    public void Without_clipboard_fallback_there_is_no_insertion_note()
    {
        Assert.DoesNotContain(Evaluate(X11Desktop()).Checks, check => check.Area == "insertion");
    }

    [Fact]
    public void Shift_insert_is_recalled_to_paste_the_primary_selection_in_xterm()
    {
        // E11: in xterm, Shift+Insert pastes the last selection, not the
        // dictation — possibly something secret, into a terminal. A choice the
        // user made, so recalled as information, never a failure.
        DoctorReport report = Evaluate(X11Desktop(new AppSettings { PasteShortcut = PasteShortcut.ShiftInsert }));

        DoctorCheck note = Line(report, "insertion", "PRIMARY");
        Assert.Equal(CheckStatus.Info, note.Status);
        Assert.Equal("Shift+Insert pastes the PRIMARY selection, not the clipboard, in xterm and similar terminals", note.Summary);
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public void A_layout_that_moves_v_is_warned_about_when_the_virtual_keyboard_pastes()
    {
        // P4: the virtual keyboard presses the QWERTY V position; on Bépo that
        // key types another letter, and the dictation is lost to another
        // shortcut. The layouts come from GNOME's input sources, as the probe
        // reads them.
        IReadOnlyList<string> layouts = KeyboardLayouts.FromGnomeSources("[('xkb', 'fr+bepo'), ('xkb', 'us')]");
        DoctorReport report = Evaluate(GnomeWayland(udevRule: true) with { Layouts = layouts });

        DoctorCheck layout = Only(report, "layout");
        Assert.Equal(CheckStatus.Warning, layout.Status);
        Assert.Equal(
            "the layout fr+bepo puts another letter where QWERTY has V: the virtual keyboard's paste shortcut would press that letter instead",
            layout.Summary);
        Assert.Contains("\"pasteShortcut\" to \"ShiftInsert\"", layout.Hint, StringComparison.Ordinal);
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public void Ctrl_shift_v_on_a_moving_layout_is_warned_about_too()
    {
        // Ctrl+Shift+V also presses the V position.
        DoctorReport report = Evaluate(GnomeWayland(udevRule: true) with
        {
            Settings = new AppSettings { PasteShortcut = PasteShortcut.CtrlShiftV },
            Layouts = ["us+dvorak"],
        });

        Assert.Contains("us+dvorak", Only(report, "layout").Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void No_layout_warning_where_v_stays_in_place_or_the_sender_works_by_keysym()
    {
        // AZERTY keeps V where QWERTY has it; Shift+Insert involves no letter;
        // xdotool and wtype send keysyms, whatever the layout.
        DoctorFacts gnome = GnomeWayland(udevRule: true);

        Assert.DoesNotContain(Evaluate(gnome with { Layouts = ["fr", "de"] }).Checks, check => check.Area == "layout");
        Assert.DoesNotContain(
            Evaluate(gnome with { Layouts = ["us+dvorak"], Settings = new AppSettings { PasteShortcut = PasteShortcut.ShiftInsert } }).Checks,
            check => check.Area == "layout");
        Assert.DoesNotContain(Evaluate(X11Desktop() with { Layouts = ["us+dvorak"] }).Checks, check => check.Area == "layout");
        Assert.DoesNotContain(Evaluate(Sway() with { Layouts = ["us+dvorak"] }).Checks, check => check.Area == "layout");
    }

    // --- logind ----------------------------------------------------------------------

    [Fact]
    public void Without_logind_the_guard_is_a_warning_since_locks_cannot_be_seen()
    {
        // WSL without systemd, a container: dictation goes on (E4), but the
        // user should know the lock screen does not stop it.
        GuardDecision guard = LogindAbsent();
        DoctorReport report = Evaluate(X11Desktop() with { Guard = guard });

        DoctorCheck logind = Only(report, "logind");
        Assert.Equal(CheckStatus.Warning, logind.Status);
        Assert.Equal(guard.Reason, logind.Summary);
        Assert.Contains("no logind", logind.Summary, StringComparison.Ordinal);
        Assert.Equal("lock screens and user switches cannot be detected here", logind.Hint);
        Assert.Equal(0, report.ExitCode);
    }

    [Theory]
    [InlineData("Active=yes\nLockedHint=yes\nType=wayland\nSeat=seat0\n", "is locked")]
    [InlineData("Active=no\nLockedHint=no\nType=wayland\nSeat=seat0\n", "is not the one in front of the screen")]
    public void A_locked_or_background_session_is_a_passing_warning(string showSession, string fragment)
    {
        // The doctor run over SSH while the screen is locked, or from a
        // console while another user's session is in front: dictation is
        // refused for now, as it should be, and will work once back.
        GuardDecision guard = Logind(showSession);
        DoctorReport report = Evaluate(X11Desktop() with { Guard = guard });

        DoctorCheck logind = Only(report, "logind");
        Assert.Equal(CheckStatus.Warning, logind.Status);
        Assert.Equal("dictation would be refused right now: " + guard.Reason, logind.Summary);
        Assert.Contains(fragment, logind.Summary, StringComparison.Ordinal);
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public void A_logind_that_does_not_answer_is_essential_since_every_insertion_is_refused()
    {
        // E4: logind is there but loginctl fails — the guard then fails closed
        // on every insertion. Saying "nothing essential is missing" would have
        // the user dictate, see nothing inserted, and not know why.
        GuardDecision guard = LogindUnanswered();
        DoctorReport report = Evaluate(X11Desktop() with { Guard = guard });

        DoctorCheck logind = Only(report, "logind");
        Assert.Equal(CheckStatus.Error, logind.Status);
        Assert.Equal("every insertion would be refused: " + guard.Reason, logind.Summary);
        Assert.Contains("did not answer", logind.Summary, StringComparison.Ordinal);
        Assert.Contains("loginctl show-session", logind.Hint, StringComparison.Ordinal);
        Assert.Equal(3, report.ExitCode);
    }

    [Fact]
    public void A_logind_answer_that_cannot_be_read_is_essential_too()
    {
        // No Active= line at all: the guard refuses to be safe, every time.
        GuardDecision guard = Logind("LockedHint=no\nType=wayland\nSeat=seat0\n");
        DoctorReport report = Evaluate(X11Desktop() with { Guard = guard });

        Assert.Equal(CheckStatus.Error, Only(report, "logind").Status);
        Assert.Equal(3, report.ExitCode);
    }

    [Fact]
    public void A_user_without_a_display_session_is_essential_too()
    {
        // The doctor run from SSH with nobody logged in graphically: logind
        // knows no display session, and the guard would refuse every
        // insertion from here.
        GuardDecision guard = SessionGuardPolicy.Decide(true, null, _ => LoginctlAnswer.Failed, () => new LoginctlAnswer(true, "\n"));
        DoctorReport report = Evaluate(X11Desktop() with { Guard = guard });

        Assert.Equal(CheckStatus.Error, Only(report, "logind").Status);
        Assert.Equal(3, report.ExitCode);
    }

    // --- Daemon and autostart --------------------------------------------------------

    [Fact]
    public void The_daemon_line_says_whether_one_answers_and_in_which_state()
    {
        // "Is it running?" is the first question when the shortcut does nothing.
        Assert.Equal("not running", Only(Evaluate(X11Desktop()), "daemon").Summary);
        Assert.Equal("running (idle)", Only(Evaluate(X11Desktop() with { DaemonState = "idle" }), "daemon").Summary);
    }

    [Fact]
    public void The_autostart_line_says_how_to_turn_it_on()
    {
        Assert.Equal("off (hexlinux --autostart on)", Only(Evaluate(X11Desktop()), "autostart").Summary);
        Assert.Equal(
            "on: HexLinux starts with the session",
            Only(Evaluate(X11Desktop() with { AutostartEnabled = true }), "autostart").Summary);
    }

    // --- Rendering -------------------------------------------------------------------

    [Fact]
    public void The_report_renders_one_aligned_line_per_check_and_its_hint_below()
    {
        // What a user pastes into a bug report: statuses in a column, areas
        // aligned, each hint under its line, the verdict last.
        InjectionPlan plan = InjectionPlan.Impossible(InsertionMode.Paste, "no display");
        DoctorReport report = new(
            [
                new DoctorCheck("session", CheckStatus.Ok, "X11 (XFCE)"),
                new DoctorCheck("daemon", CheckStatus.Info, "not running"),
                new DoctorCheck("keyboards", CheckStatus.Warning, "1 keyboard(s) found, none readable", "run scripts/install-udev-rules.sh"),
                new DoctorCheck("model", CheckStatus.Error, "\"models/x\" was found nowhere", "download it with scripts/get-model.sh"),
            ],
            plan,
            plan,
            2);

        string indent = new(' ', 19);
        string expected =
            "HexLinux doctor\n\n"
            + "[  ok] session     X11 (XFCE)\n"
            + "[info] daemon      not running\n"
            + "[warn] keyboards   1 keyboard(s) found, none readable\n"
            + indent + "-> run scripts/install-udev-rules.sh\n"
            + "[FAIL] model       \"models/x\" was found nowhere\n"
            + indent + "-> download it with scripts/get-model.sh\n"
            + "\n"
            + "Something essential is missing (exit code 2).\n";

        Assert.Equal(expected, report.Render());
    }

    [Fact]
    public void A_healthy_report_ends_by_saying_nothing_essential_is_missing()
    {
        string text = Evaluate(GnomeWayland(udevRule: true)).Render();

        Assert.StartsWith("HexLinux doctor\n\n[  ok] session     Wayland (ubuntu:GNOME), with an X11 display for XWayland\n", text, StringComparison.Ordinal);
        Assert.Contains("[  ok] paste       clipboard xclip, keys uinput (configured)\n", text, StringComparison.Ordinal);
        Assert.EndsWith("\nNothing essential is missing.\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failing_report_shows_the_fail_tag_and_the_hint_to_follow()
    {
        string text = Evaluate(GnomeWayland(udevRule: false)).Render();

        Assert.Contains("[FAIL] paste       impossible: nothing can send the paste shortcut: GNOME", text, StringComparison.Ordinal);
        Assert.Contains("[warn] keyboards   2 keyboard(s) found, none readable (permission denied)", text, StringComparison.Ordinal);
        Assert.Contains(new string(' ', 19) + "-> " + UdevHint + "\n", text, StringComparison.Ordinal);
        Assert.EndsWith("Something essential is missing (exit code 3).\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_refuses_null_facts()
    {
        Assert.Throws<ArgumentNullException>(() => DoctorEvaluation.Evaluate(null!));
    }
}
