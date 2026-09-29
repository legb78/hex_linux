namespace HexLinux.Platform;

/// <summary>The display protocol of the graphical session.</summary>
public enum DisplayServer
{
    /// <summary>No graphical session reachable: a console, an SSH login, a bare systemd unit.</summary>
    None,

    X11,

    Wayland,
}

/// <summary>
/// The graphical session HexLinux runs in, read from the environment.
///
/// <para>It decides how text can be inserted. X11 lets any client send keys
/// to any other; Wayland lets none, on purpose, and each compositor offers a
/// different way around it or none at all. So the first question is always
/// which of the two this is — and the environment is the only place that says
/// so reliably.</para>
///
/// <para>Pure: the environment, and the one look at the disk it needs, are
/// passed in.</para>
/// </summary>
/// <param name="Server">The protocol clients talk.</param>
/// <param name="HasX11Display">
/// True when <c>DISPLAY</c> is set — under Wayland too, where it points at
/// XWayland and its clipboard, which the compositor keeps in step with its
/// own.
/// </param>
/// <param name="Desktop">
/// <c>XDG_CURRENT_DESKTOP</c>, a colon-separated list such as "ubuntu:GNOME".
/// </param>
public sealed record DesktopSession(DisplayServer Server, bool HasX11Display, string Desktop)
{
    /// <summary>
    /// True under GNOME. Mutter, its compositor, offers no protocol for a
    /// client to type into another window, which rules out wtype.
    /// </summary>
    public bool IsGnome => Desktop
        .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Contains("GNOME", StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads the session from the environment, trusting that the Wayland socket exists.</summary>
    public static DesktopSession Detect(Func<string, string?> environment) => Detect(environment, _ => true);

    /// <summary>
    /// Reads the session from the environment.
    ///
    /// <para><c>XDG_SESSION_TYPE</c> is trusted first, when the matching
    /// display variable backs it up. Otherwise <c>WAYLAND_DISPLAY</c> wins over
    /// <c>DISPLAY</c>: a Wayland session sets both, XWayland being there for
    /// older applications, while an X11 session never sets the first.</para>
    ///
    /// <para><b>A <c>WAYLAND_DISPLAY</c> whose socket does not exist does not
    /// count.</b> The variable outlives the compositor it names: WSL with
    /// systemd enabled is one such case, found while building this — logind
    /// mounts a fresh <c>/run/user/UID</c> over the folder that held WSLg's
    /// socket, the variable still says <c>wayland-0</c>, and every Wayland
    /// tool fails to connect while X11 works. Believing the variable would
    /// pick the tools that cannot work.</para>
    /// </summary>
    /// <param name="environment">Reads one environment variable.</param>
    /// <param name="socketExists">Whether a Wayland socket exists at a path.</param>
    public static DesktopSession Detect(Func<string, string?> environment, Func<string, bool> socketExists)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(socketExists);

        string type = environment("XDG_SESSION_TYPE")?.Trim() ?? string.Empty;
        bool wayland = WaylandSocketPath(environment) is { } socket && socketExists(socket);
        bool x11 = !string.IsNullOrWhiteSpace(environment("DISPLAY"));
        string desktop = environment("XDG_CURRENT_DESKTOP")?.Trim() ?? string.Empty;

        DisplayServer server = true switch
        {
            _ when type.Equals("x11", StringComparison.OrdinalIgnoreCase) && x11 => DisplayServer.X11,
            _ when type.Equals("wayland", StringComparison.OrdinalIgnoreCase) && wayland => DisplayServer.Wayland,
            _ when wayland => DisplayServer.Wayland,
            _ when x11 => DisplayServer.X11,
            _ => DisplayServer.None,
        };

        return new DesktopSession(server, x11, desktop);
    }

    /// <summary>
    /// Where the Wayland socket named by <c>WAYLAND_DISPLAY</c> lives, or null
    /// when the variable is unset or cannot name one.
    ///
    /// <para>The rule libwayland follows: an absolute value is the socket
    /// itself, a bare name sits in <c>XDG_RUNTIME_DIR</c>. Without a usable
    /// runtime folder a bare name leads nowhere.</para>
    /// </summary>
    public static string? WaylandSocketPath(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        string? display = environment("WAYLAND_DISPLAY")?.Trim();

        if (string.IsNullOrEmpty(display))
        {
            return null;
        }

        if (Path.IsPathRooted(display))
        {
            return display;
        }

        string? runtime = environment("XDG_RUNTIME_DIR")?.Trim();

        return string.IsNullOrEmpty(runtime) || !Path.IsPathRooted(runtime) ? null : Path.Combine(runtime, display);
    }

    /// <summary>Variant wired to the real environment of the process.</summary>
    public static DesktopSession FromEnvironment() => Detect(Environment.GetEnvironmentVariable, File.Exists);
}
