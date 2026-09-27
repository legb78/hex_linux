using System.Text;

namespace HexLinux.Daemon;

/// <summary>
/// Writes the <c>.desktop</c> file that starts HexLinux with the session.
///
/// <para>An XDG autostart entry rather than a systemd user unit: every
/// desktop honours <c>~/.config/autostart</c>, and an entry started there
/// inherits the session's environment — <c>WAYLAND_DISPLAY</c>,
/// <c>DISPLAY</c>, <c>XDG_SESSION_ID</c> — which a user unit only gets when
/// the desktop remembers to export it. Without those variables HexLinux cannot
/// tell which session it serves, nor insert anything into it.</para>
///
/// <para>Pure, because the quoting is where it goes wrong: a path with a space
/// or a <c>%</c> in it breaks the <c>Exec</c> line in ways that only show at
/// the next login.</para>
/// </summary>
public static class DesktopEntry
{
    public const string FileName = "hexlinux.desktop";

    /// <summary>Characters the specification reserves in an Exec argument.</summary>
    private const string Reserved = " \t\"'\\><~|&;$*?#()`";

    public static string Build(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        return string.Join(
            '\n',
            "[Desktop Entry]",
            "Type=Application",
            "Name=HexLinux",
            "Comment=Local hold-to-talk dictation",
            $"Exec={QuoteExec(executablePath)}",
            "Icon=audio-input-microphone",
            "Terminal=false",
            "NoDisplay=true",
            "X-GNOME-Autostart-enabled=true",
            string.Empty);
    }

    /// <summary>
    /// Quotes one argument of an Exec line, per the Desktop Entry
    /// specification.
    ///
    /// <para>Three layers, in this order. A reserved character forces double
    /// quotes, inside which <c>"</c>, <c>`</c>, <c>$</c> and <c>\</c> take a
    /// backslash. The value is then a string of the key file format, where
    /// each backslash is written twice. And <c>%</c> introduces a field code
    /// such as <c>%f</c>, so a literal one is written <c>%%</c>.</para>
    ///
    /// <para>A line break cannot be represented in an argument at all, and is
    /// refused rather than written as something else.</para>
    /// </summary>
    public static string QuoteExec(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);

        if (argument.AsSpan().IndexOfAny('\n', '\r') >= 0)
        {
            throw new ArgumentException("An Exec argument cannot contain a line break.", nameof(argument));
        }

        string quoted = argument;

        if (argument.AsSpan().IndexOfAny(Reserved) >= 0)
        {
            var builder = new StringBuilder("\"");

            foreach (char character in argument)
            {
                if (character is '"' or '`' or '$' or '\\')
                {
                    builder.Append('\\');
                }

                builder.Append(character);
            }

            quoted = builder.Append('"').ToString();
        }

        return quoted
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "%%", StringComparison.Ordinal);
    }
}
