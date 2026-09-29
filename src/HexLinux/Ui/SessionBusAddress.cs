using System.Text;

namespace HexLinux.Ui;

/// <summary>
/// Finds the address of the user's session bus, where the tray host and the
/// notification server live.
///
/// <para>The rules of the two libraries every desktop is built on, in their
/// order. <c>DBUS_SESSION_BUS_ADDRESS</c> first, when it is set; otherwise
/// <c>$XDG_RUNTIME_DIR/bus</c>, the socket of the per-user bus systemd
/// starts — the fallback of both libsystemd (<c>sd-bus.c</c>,
/// <c>bus_set_address_user</c>) and GLib (<c>gdbusaddress.c</c>,
/// <c>get_session_address_xdg</c>). That second rule matters to a daemon
/// started by a systemd user unit, whose environment may lack the
/// variable.</para>
///
/// <para>What this deliberately does not do: fall back, as the D-Bus library
/// would on its own, to asking the X server for an address. That path goes
/// through libX11, which a Wayland-only system may not even have, and it
/// cannot find a bus this rule misses on a systemd desktop.</para>
///
/// <para>Pure: the environment and the test for the socket are passed in.</para>
/// </summary>
public static class SessionBusAddress
{
    /// <summary>
    /// The session bus address, or null when this process has no session bus
    /// to reach — an SSH login, a service outside the user's session.
    /// </summary>
    /// <param name="environment">Reads an environment variable.</param>
    /// <param name="socketExists">
    /// Whether a file exists at that path. Existence is enough: a socket that
    /// belongs to another user refuses the connection at authentication,
    /// and the surface then falls back as for any other failure.
    /// </param>
    public static string? Resolve(Func<string, string?> environment, Func<string, bool> socketExists)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(socketExists);

        string? configured = environment("DBUS_SESSION_BUS_ADDRESS");

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        string? runtime = environment("XDG_RUNTIME_DIR");

        // A relative runtime directory is a misconfiguration: resolved against
        // whatever directory the daemon runs in, it could point anywhere.
        if (string.IsNullOrWhiteSpace(runtime) || !runtime.StartsWith('/'))
        {
            return null;
        }

        string socket = runtime.TrimEnd('/') + "/bus";

        // The D-Bus library decodes each %XX escape into one character, not one
        // byte, so a non-ASCII path would not survive the round trip. The
        // runtime directory is /run/user/<uid> wherever systemd sets it.
        if (!Ascii.IsValid(socket) || !socketExists(socket))
        {
            return null;
        }

        return "unix:path=" + Escape(socket);
    }

    /// <summary>
    /// Escapes a value for a D-Bus address: every byte is written as <c>%</c>
    /// and two hexadecimal digits, except letters, digits and <c>- _ / .</c>.
    /// A comma or a semicolon in a path would otherwise end the address early.
    ///
    /// <para>The specification lets a few more bytes through unescaped, but
    /// also allows any of them to be escaped, and the libraries disagree on
    /// which (GLib escapes <c>*</c>, keeps <c>\</c>). Escaping all but these
    /// is valid under every reading.</para>
    /// </summary>
    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var escaped = new StringBuilder(value.Length);

        foreach (byte octet in Encoding.UTF8.GetBytes(value))
        {
            char character = (char)octet;

            if (char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '/' or '.')
            {
                escaped.Append(character);
            }
            else
            {
                escaped.Append('%').Append(octet.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return escaped.ToString();
    }
}
