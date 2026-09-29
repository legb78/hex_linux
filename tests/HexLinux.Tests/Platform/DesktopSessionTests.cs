using HexLinux.Platform;
using Xunit;

namespace HexLinux.Tests.Platform;

/// <summary>
/// Which display server the daemon believes it runs under decides every
/// insertion tool it will try, so each environment a real session produces
/// is replayed here: an X11 login, a Wayland login with XWayland, WSLg, WSL
/// with systemd (where WAYLAND_DISPLAY outlives its socket), an SSH login.
/// </summary>
public class DesktopSessionTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] variables)
    {
        Dictionary<string, string> values = variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);

        return name => values.TryGetValue(name, out string? value) ? value : null;
    }

    private static readonly Func<string, bool> AnySocket = _ => true;
    private static readonly Func<string, bool> NoSocket = _ => false;

    // --- Display server -------------------------------------------------------------

    [Fact]
    public void An_X11_login_is_X11()
    {
        DesktopSession session = DesktopSession.Detect(
            Env(("XDG_SESSION_TYPE", "x11"), ("DISPLAY", ":0"), ("XDG_CURRENT_DESKTOP", "XFCE")),
            NoSocket);

        Assert.Equal(new DesktopSession(DisplayServer.X11, HasX11Display: true, "XFCE"), session);
    }

    [Fact]
    public void A_Wayland_login_with_XWayland_is_Wayland_with_an_X11_display()
    {
        // GNOME on Ubuntu: both variables set, XWayland serving older
        // applications and the clipboard HexLinux uses there.
        DesktopSession session = DesktopSession.Detect(
            Env(
                ("XDG_SESSION_TYPE", "wayland"),
                ("WAYLAND_DISPLAY", "wayland-0"),
                ("XDG_RUNTIME_DIR", "/run/user/1000"),
                ("DISPLAY", ":0"),
                ("XDG_CURRENT_DESKTOP", "ubuntu:GNOME")),
            AnySocket);

        Assert.Equal(DisplayServer.Wayland, session.Server);
        Assert.True(session.HasX11Display);
        Assert.True(session.IsGnome);
    }

    [Fact]
    public void WSLg_without_a_session_type_is_Wayland_when_the_socket_is_there()
    {
        // WSLg sets WAYLAND_DISPLAY and DISPLAY but no XDG_SESSION_TYPE:
        // WAYLAND_DISPLAY wins, since an X11 session never sets it.
        List<string> probed = [];
        DesktopSession session = DesktopSession.Detect(
            Env(("WAYLAND_DISPLAY", "wayland-0"), ("XDG_RUNTIME_DIR", "/run/user/0/"), ("DISPLAY", ":0")),
            path =>
            {
                probed.Add(path);
                return true;
            });

        Assert.Equal(DisplayServer.Wayland, session.Server);
        Assert.True(session.HasX11Display);
        Assert.Equal(["/run/user/0/wayland-0"], probed);
    }

    [Fact]
    public void A_Wayland_variable_whose_socket_is_gone_does_not_count()
    {
        // WSL with systemd, found while building HexLinux: logind mounts a
        // fresh /run/user/0 over WSLg's socket, WAYLAND_DISPLAY still says
        // wayland-0, every Wayland tool fails while X11 works.
        DesktopSession session = DesktopSession.Detect(
            Env(("WAYLAND_DISPLAY", "wayland-0"), ("XDG_RUNTIME_DIR", "/run/user/0/"), ("DISPLAY", ":0")),
            NoSocket);

        Assert.Equal(DisplayServer.X11, session.Server);
    }

    [Fact]
    public void A_Wayland_session_type_whose_socket_is_gone_falls_back_on_X11()
    {
        DesktopSession session = DesktopSession.Detect(
            Env(("XDG_SESSION_TYPE", "wayland"), ("WAYLAND_DISPLAY", "wayland-0"), ("XDG_RUNTIME_DIR", "/run/user/1000"), ("DISPLAY", ":1")),
            NoSocket);

        Assert.Equal(DisplayServer.X11, session.Server);
    }

    [Fact]
    public void An_X11_session_type_is_trusted_over_a_stray_WAYLAND_DISPLAY()
    {
        // A nested Wayland compositor started from an X11 desktop, or a
        // variable left in a login script: the session type is the truth.
        DesktopSession session = DesktopSession.Detect(
            Env(("XDG_SESSION_TYPE", "x11"), ("WAYLAND_DISPLAY", "wayland-1"), ("XDG_RUNTIME_DIR", "/run/user/1000"), ("DISPLAY", ":0")),
            AnySocket);

        Assert.Equal(DisplayServer.X11, session.Server);
    }

    [Fact]
    public void An_X11_session_type_without_DISPLAY_is_not_believed()
    {
        // The type is trusted only when the matching display variable backs
        // it up.
        DesktopSession session = DesktopSession.Detect(
            Env(("XDG_SESSION_TYPE", "x11"), ("WAYLAND_DISPLAY", "wayland-0"), ("XDG_RUNTIME_DIR", "/run/user/1000")),
            AnySocket);

        Assert.Equal(DisplayServer.Wayland, session.Server);
        Assert.False(session.HasX11Display);
    }

    [Theory]
    [InlineData("X11")]
    [InlineData(" x11 ")]
    public void The_session_type_is_read_whatever_its_case_and_spacing(string type)
    {
        DesktopSession session = DesktopSession.Detect(
            Env(("XDG_SESSION_TYPE", type), ("WAYLAND_DISPLAY", "/tmp/wl"), ("DISPLAY", ":0")),
            AnySocket);

        Assert.Equal(DisplayServer.X11, session.Server);
    }

    [Fact]
    public void Wayland_is_recognised_whatever_the_case_of_its_session_type()
    {
        DesktopSession session = DesktopSession.Detect(
            Env(("XDG_SESSION_TYPE", "Wayland"), ("WAYLAND_DISPLAY", "/run/user/1000/wayland-0"), ("DISPLAY", ":0")),
            AnySocket);

        Assert.Equal(DisplayServer.Wayland, session.Server);
    }

    [Fact]
    public void An_SSH_login_has_no_graphical_session()
    {
        // logind reports "tty": no variable leads to a display, and the
        // planner then refuses to try anything.
        DesktopSession session = DesktopSession.Detect(Env(("XDG_SESSION_TYPE", "tty")), AnySocket);

        Assert.Equal(new DesktopSession(DisplayServer.None, HasX11Display: false, ""), session);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_DISPLAY_is_no_X11_display(string display)
    {
        DesktopSession session = DesktopSession.Detect(Env(("DISPLAY", display)), AnySocket);

        Assert.Equal(DisplayServer.None, session.Server);
        Assert.False(session.HasX11Display);
    }

    [Fact]
    public void Without_a_socket_check_the_Wayland_variable_is_trusted()
    {
        // The one-argument overload, for callers that have no disk to look at.
        DesktopSession session = DesktopSession.Detect(Env(("WAYLAND_DISPLAY", "wayland-0"), ("XDG_RUNTIME_DIR", "/run/user/1000")));

        Assert.Equal(DisplayServer.Wayland, session.Server);
    }

    [Fact]
    public void The_desktop_name_is_kept_trimmed()
    {
        DesktopSession session = DesktopSession.Detect(Env(("DISPLAY", ":0"), ("XDG_CURRENT_DESKTOP", "  KDE  ")), AnySocket);

        Assert.Equal("KDE", session.Desktop);
    }

    [Fact]
    public void Missing_readers_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => DesktopSession.Detect(null!, AnySocket));
        Assert.Throws<ArgumentNullException>(() => DesktopSession.Detect(Env(), null!));
        Assert.Throws<ArgumentNullException>(() => DesktopSession.WaylandSocketPath(null!));
    }

    [Fact]
    public void The_real_environment_is_read_the_same_way()
    {
        // FromEnvironment is Detect wired to the process: no rule of its own.
        Assert.Equal(DesktopSession.Detect(Environment.GetEnvironmentVariable, File.Exists), DesktopSession.FromEnvironment());
    }

    // --- GNOME ----------------------------------------------------------------------

    [Theory]
    [InlineData("GNOME")]
    [InlineData("ubuntu:GNOME")]
    [InlineData("pop:GNOME")]
    [InlineData("GNOME-Classic:GNOME")]
    [InlineData("gnome")]
    [InlineData("ubuntu: GNOME ")]
    public void Gnome_is_recognised_in_the_colon_separated_list(string desktop)
    {
        // Ubuntu, Pop!_OS and the classic session all name GNOME somewhere in
        // the list: Mutter, and so no wtype, no data-control clipboard.
        Assert.True(new DesktopSession(DisplayServer.Wayland, true, desktop).IsGnome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("KDE")]
    [InlineData("sway")]
    [InlineData("Hyprland")]
    [InlineData("X-Cinnamon")]
    [InlineData("GNOME-Flashback")]
    [InlineData("NotGNOME")]
    public void Other_desktops_are_not_taken_for_gnome(string desktop)
    {
        // A substring match would take "GNOME-Flashback" alone, or a name
        // merely containing GNOME, for Mutter.
        Assert.False(new DesktopSession(DisplayServer.Wayland, true, desktop).IsGnome);
    }

    // --- Wayland socket path ----------------------------------------------------------

    [Fact]
    public void A_bare_socket_name_lives_in_the_runtime_folder()
    {
        Assert.Equal(
            "/run/user/1000/wayland-0",
            DesktopSession.WaylandSocketPath(Env(("WAYLAND_DISPLAY", "wayland-0"), ("XDG_RUNTIME_DIR", "/run/user/1000"))));
    }

    [Fact]
    public void A_trailing_slash_on_the_runtime_folder_is_harmless()
    {
        // WSLg's XDG_RUNTIME_DIR is "/run/user/0/".
        Assert.Equal(
            "/run/user/0/wayland-0",
            DesktopSession.WaylandSocketPath(Env(("WAYLAND_DISPLAY", " wayland-0 "), ("XDG_RUNTIME_DIR", " /run/user/0/ "))));
    }

    [Fact]
    public void An_absolute_socket_path_is_taken_as_it_is()
    {
        // What the WSL-with-systemd workaround sets:
        // WAYLAND_DISPLAY=/mnt/wslg/runtime-dir/wayland-0.
        Assert.Equal(
            "/mnt/wslg/runtime-dir/wayland-0",
            DesktopSession.WaylandSocketPath(Env(("WAYLAND_DISPLAY", "/mnt/wslg/runtime-dir/wayland-0"))));
    }

    [Theory]
    [InlineData(null, "/run/user/1000")]
    [InlineData("", "/run/user/1000")]
    [InlineData("  ", "/run/user/1000")]
    [InlineData("wayland-0", null)]
    [InlineData("wayland-0", "")]
    [InlineData("wayland-0", "run/user/1000")]
    public void Without_a_usable_name_or_runtime_folder_there_is_no_socket(string? display, string? runtime)
    {
        // libwayland's rule: a bare name without an absolute XDG_RUNTIME_DIR
        // leads nowhere, and a relative folder would depend on the daemon's
        // working directory.
        List<(string, string)> variables = [];

        if (display is not null)
        {
            variables.Add(("WAYLAND_DISPLAY", display));
        }

        if (runtime is not null)
        {
            variables.Add(("XDG_RUNTIME_DIR", runtime));
        }

        Assert.Null(DesktopSession.WaylandSocketPath(Env([.. variables])));
    }

    [Fact]
    public void Without_a_socket_path_the_socket_is_not_even_looked_for()
    {
        bool looked = false;

        DesktopSession session = DesktopSession.Detect(
            Env(("WAYLAND_DISPLAY", "wayland-0"), ("DISPLAY", ":0")),
            _ =>
            {
                looked = true;
                return true;
            });

        Assert.False(looked);
        Assert.Equal(DisplayServer.X11, session.Server);
    }
}
