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
/// <para>Pure: the environment is passed in.</para>
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

    /// <summary>
    /// Reads the session from the environment.
    ///
    /// <para><c>XDG_SESSION_TYPE</c> is trusted first, when the matching
    /// display variable backs it up. Otherwise <c>WAYLAND_DISPLAY</c> wins over
    /// <c>DISPLAY</c>: a Wayland session sets both, XWayland being there for
    /// older applications, while an X11 session never sets the first.</para>
    /// </summary>
    public static DesktopSession Detect(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        string type = environment("XDG_SESSION_TYPE")?.Trim() ?? string.Empty;
        bool wayland = !string.IsNullOrWhiteSpace(environment("WAYLAND_DISPLAY"));
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

    /// <summary>Variant wired to the real environment of the process.</summary>
    public static DesktopSession FromEnvironment() => Detect(Environment.GetEnvironmentVariable);
}
