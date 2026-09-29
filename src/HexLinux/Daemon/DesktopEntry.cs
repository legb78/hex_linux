using System.Text;

namespace HexLinux.Daemon;

/// <summary>
/// Writes the <c>.desktop</c> file that starts HexLinux with the session.
///
/// <para>An XDG autostart entry rather than a systemd user unit: every
/// desktop honours <c>~/.config/autostart</c>, and an entry started there
/// inherits the session's display — <c>WAYLAND_DISPLAY</c>, <c>DISPLAY</c> —
/// which a user unit only gets when the desktop remembers to export it.
/// Without them HexLinux cannot insert anything. <c>XDG_SESSION_ID</c> is not
/// guaranteed: desktops that start their autostart entries through systemd's
/// generator leave it out, which is why the session guard falls back to
/// asking logind for the user's display session.</para>
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

    /// <summary>
    /// Whether the executable at <paramref name="executablePath"/> may be
    /// started at every login: a refusal, a warning, or neither.
    ///
    /// <para>Refused: the .NET host itself — a development run through
    /// <c>dotnet hexlinux.dll</c> — which would give an entry that starts
    /// <c>dotnet</c> with no program; and a binary any other user can replace,
    /// directly or through its folder (an archive unpacked in <c>/tmp</c>),
    /// which would be run as this user at each login with whatever was put
    /// there.</para>
    ///
    /// <para>Only a warning when the group can write: Ubuntu gives every user a
    /// group of their own and a umask that makes new files group-writable, so
    /// refusing would turn away the ordinary case to guard against a rare
    /// one.</para>
    /// </summary>
    public static (string? Refusal, string? Warning) CheckAutostart(string executablePath, UnixFileMode fileMode, UnixFileMode folderMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        if (string.Equals(Path.GetFileNameWithoutExtension(executablePath), "dotnet", StringComparison.Ordinal))
        {
            return ("HexLinux is running through the dotnet host: start the hexlinux executable itself to enable autostart.", null);
        }

        if ((fileMode & UnixFileMode.OtherWrite) != 0)
        {
            return ($"{executablePath} can be modified by any user: fix its permissions (chmod o-w) first.", null);
        }

        if ((folderMode & UnixFileMode.OtherWrite) != 0)
        {
            return ($"the folder of {executablePath} can be modified by any user: move HexLinux somewhere only you can write to.", null);
        }

        if (((fileMode | folderMode) & UnixFileMode.GroupWrite) != 0)
        {
            return (null, $"{executablePath} or its folder is writable by its group: harmless if the group is yours alone.");
        }

        return (null, null);
    }

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
    /// The value of the <c>Exec</c> key of the <c>[Desktop Entry]</c> group,
    /// as written (still quoted), or null when there is none.
    /// </summary>
    public static string? ExecOf(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        string[] lines = content.Split('\n');
        int index = ExecLine(lines);

        if (index < 0)
        {
            return null;
        }

        string line = lines[index].TrimEnd('\r');
        return line[(line.IndexOf('=', StringComparison.Ordinal) + 1)..].Trim();
    }

    /// <summary>
    /// The entry with its <c>Exec</c> key pointed at
    /// <paramref name="executablePath"/>, every other line kept as the user
    /// left it — <c>Hidden=true</c> or <c>X-GNOME-Autostart-enabled=false</c>
    /// above all: a desktop's "don't start at login" switch must survive
    /// HexLinux being moved. Null when the entry has no <c>Exec</c> key to
    /// point anywhere: it is then not one this program wrote.
    /// </summary>
    public static string? WithExec(string content, string executablePath)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        string[] lines = content.Split('\n');
        int index = ExecLine(lines);

        if (index < 0)
        {
            return null;
        }

        lines[index] = $"Exec={QuoteExec(executablePath)}";
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Where the <c>Exec</c> key of the <c>[Desktop Entry]</c> group is, or
    /// -1. Keys of other groups (<c>[Desktop Action …]</c>) have an
    /// <c>Exec</c> of their own, which is not the one started at login.
    /// </summary>
    private static int ExecLine(string[] lines)
    {
        bool inMainGroup = false;

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].TrimEnd('\r');

            if (line.StartsWith('['))
            {
                inMainGroup = line.Trim() == "[Desktop Entry]";
            }
            else if (inMainGroup && line.StartsWith("Exec", StringComparison.Ordinal) && line.AsSpan(4).TrimStart().StartsWith("="))
            {
                return index;
            }
        }

        return -1;
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
    /// <para>A line break cannot be represented in an argument at all, and the
    /// specification allows no other control character in a value either (a
    /// tab included): both are refused rather than written as something
    /// else.</para>
    /// </summary>
    public static string QuoteExec(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);

        if (argument.AsSpan().IndexOfAny('\n', '\r') >= 0)
        {
            throw new ArgumentException("An Exec argument cannot contain a line break.", nameof(argument));
        }

        if (argument.Any(char.IsControl))
        {
            throw new ArgumentException("An Exec argument cannot contain a control character.", nameof(argument));
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
