using System.Text;

namespace HexLinux.Daemon;

/// <summary>Who owns the executable an autostart entry would start, and its folder.</summary>
/// <param name="File">The executable's owner, or null when it could not be read.</param>
/// <param name="Folder">Its folder's owner, or null.</param>
/// <param name="CurrentUser">The user HexLinux runs as.</param>
public readonly record struct FileOwners(uint? File, uint? Folder, uint CurrentUser);

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
    /// <para>Refused, because the entry would not start at all:</para>
    /// <list type="bullet">
    /// <item>the .NET host itself — a development run through
    /// <c>dotnet hexlinux.dll</c> — which would give an entry that starts
    /// <c>dotnet</c> with no program;</item>
    /// <item>a build that needs the .NET runtime of an SDK, what
    /// <c>dotnet build</c> produces: at login it finds no runtime and exits
    /// ("You must install .NET", exit code 131, verified), while the
    /// self-contained release starts anywhere (QA-04);</item>
    /// <item>a path holding <c>%</c>: written <c>%%</c> as the specification
    /// wants, but GLib looks the program up before undoing it, so GNOME never
    /// starts it (verified with GLib 2.80, QA-06).</item>
    /// </list>
    ///
    /// <para>Refused, because it would run as this user at each login with
    /// whatever someone else put there: a binary another user can replace —
    /// writable by everyone, directly or through its folder (an archive
    /// unpacked in <c>/tmp</c>), or owned by another user than this one or
    /// root (SEC-07).</para>
    ///
    /// <para>Only a warning when the group can write: Ubuntu gives every user a
    /// group of their own and a umask that makes new files group-writable, so
    /// refusing would turn away the ordinary case to guard against a rare
    /// one. A warning too when the owners could not be read.</para>
    /// </summary>
    /// <param name="executablePath">The running executable.</param>
    /// <param name="fileMode">Its mode.</param>
    /// <param name="folderMode">Its folder's mode.</param>
    /// <param name="owners">Who owns them.</param>
    /// <param name="needsDotnetRuntime">A <c>.runtimeconfig.json</c> sits next to it: a framework-dependent build.</param>
    public static (string? Refusal, string? Warning) CheckAutostart(
        string executablePath,
        UnixFileMode fileMode,
        UnixFileMode folderMode,
        FileOwners owners,
        bool needsDotnetRuntime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        if (string.Equals(Path.GetFileNameWithoutExtension(executablePath), "dotnet", StringComparison.Ordinal))
        {
            return ("HexLinux is running through the dotnet host: start the hexlinux executable itself to enable autostart.", null);
        }

        if (needsDotnetRuntime)
        {
            return ($"{executablePath} is a build that needs the .NET runtime of an SDK, which a login session does not find: "
                + "enable autostart from the release archive, or from what scripts/publish.sh builds.", null);
        }

        if (executablePath.Contains('%', StringComparison.Ordinal))
        {
            return ($"the path of {executablePath} holds a '%', which GNOME and the other GLib desktops cannot start: move HexLinux to a folder without one first.", null);
        }

        if ((fileMode & UnixFileMode.OtherWrite) != 0)
        {
            return ($"{executablePath} can be modified by any user: fix its permissions (chmod o-w) first.", null);
        }

        if ((folderMode & UnixFileMode.OtherWrite) != 0)
        {
            return ($"the folder of {executablePath} can be modified by any user: move HexLinux somewhere only you can write to.", null);
        }

        if (owners.File is { } fileOwner && fileOwner != owners.CurrentUser && fileOwner != 0)
        {
            return ($"{executablePath} belongs to another user (uid {fileOwner}), who could replace it: install HexLinux in a folder of your own first.", null);
        }

        if (owners.Folder is { } folderOwner && folderOwner != owners.CurrentUser && folderOwner != 0)
        {
            return ($"the folder of {executablePath} belongs to another user (uid {folderOwner}), who could replace it: install HexLinux in a folder of your own first.", null);
        }

        if (((fileMode | folderMode) & UnixFileMode.GroupWrite) != 0)
        {
            return (null, $"{executablePath} or its folder is writable by its group: harmless if the group is yours alone.");
        }

        if (owners.File is null || owners.Folder is null)
        {
            return (null, $"the owner of {executablePath} or of its folder could not be read.");
        }

        return (null, null);
    }

    /// <summary>
    /// Whether an existing entry should be pointed at
    /// <paramref name="executablePath"/>, the copy running now: only when it
    /// starts another executable that no longer works — moved, deleted, no
    /// longer executable.
    ///
    /// <para>An entry that starts another copy that still works is left
    /// alone: running a development build, or a release unpacked elsewhere to
    /// try it, used to take autostart over, and the login then started that
    /// copy, or nothing once it was deleted (QA-05). An entry whose Exec is
    /// not a single path — edited by hand, or not written by HexLinux — is
    /// left alone too.</para>
    /// </summary>
    /// <param name="content">The entry as it is on disk.</param>
    /// <param name="executablePath">The running executable.</param>
    /// <param name="isUsable">Whether a path names an executable that exists.</param>
    public static bool ShouldRepoint(string content, string executablePath, Func<string, bool> isUsable)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(isUsable);

        if (ExecOf(content) is not { } exec || exec == QuoteExec(executablePath))
        {
            return false;
        }

        return UnquoteExec(exec) is { } target && target != executablePath && !isUsable(target);
    }

    /// <summary>
    /// The one path an Exec value names, undoing <see cref="QuoteExec"/>'s
    /// three layers — or null when the value is not a single plain path: more
    /// than one argument, a field code such as <c>%f</c>, a quote left open.
    /// </summary>
    public static string? UnquoteExec(string exec)
    {
        ArgumentNullException.ThrowIfNull(exec);

        // The key file's string escapes, then the literal percent signs.
        var text = new StringBuilder();

        for (int i = 0; i < exec.Length; i++)
        {
            char character = exec[i];

            if (character == '\\')
            {
                // The key file format defines \s \n \t \r and \\; anything
                // else is no entry HexLinux wrote.
                char? unescaped = i + 1 < exec.Length
                    ? exec[i + 1] switch
                    {
                        's' => ' ',
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        '\\' => '\\',
                        _ => null,
                    }
                    : null;

                if (unescaped is null)
                {
                    return null;
                }

                text.Append(unescaped.Value);
                i++;
            }
            else if (character == '%')
            {
                if (i + 1 >= exec.Length || exec[i + 1] != '%')
                {
                    return null;
                }

                text.Append('%');
                i++;
            }
            else
            {
                text.Append(character);
            }
        }

        string value = text.ToString();

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            var path = new StringBuilder();

            for (int i = 1; i < value.Length - 1; i++)
            {
                char character = value[i];

                if (character == '\\' && i + 1 < value.Length - 1 && value[i + 1] is '"' or '`' or '$' or '\\')
                {
                    path.Append(value[++i]);
                }
                else if (character is '"' or '`' or '$' or '\\')
                {
                    // Unescaped inside double quotes: not a value QuoteExec
                    // writes, and not a single argument either.
                    return null;
                }
                else
                {
                    path.Append(character);
                }
            }

            return path.Length > 0 ? path.ToString() : null;
        }

        // Unquoted: a reserved character would have forced quotes.
        return value.Length == 0 || value.AsSpan().IndexOfAny(Reserved) >= 0 ? null : value;
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
