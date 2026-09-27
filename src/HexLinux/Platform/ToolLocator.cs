namespace HexLinux.Platform;

/// <summary>
/// Finds the command-line tools HexLinux can drive, on the <c>PATH</c>.
///
/// <para>HexLinux shells out rather than linking the libraries behind them:
/// wl-clipboard, xclip and xdotool are what every desktop already packages,
/// they know their protocol better than a reimplementation would, and a
/// missing one is a one-line fix the diagnostic can name.</para>
/// </summary>
public static class ToolLocator
{
    public const string WlCopy = "wl-copy";
    public const string WlPaste = "wl-paste";
    public const string Xclip = "xclip";
    public const string Xsel = "xsel";
    public const string Xdotool = "xdotool";
    public const string Wtype = "wtype";
    public const string Ydotool = "ydotool";
    public const string NotifySend = "notify-send";
    public const string Loginctl = "loginctl";

    /// <summary>Every tool worth looking for.</summary>
    public static readonly IReadOnlyList<string> Known =
        [WlCopy, WlPaste, Xclip, Xsel, Xdotool, Wtype, Ydotool, NotifySend, Loginctl];

    /// <summary>
    /// Full path of <paramref name="tool"/> on <paramref name="path"/>, or null.
    /// Empty entries are skipped: POSIX reads them as the current directory,
    /// and a daemon has no business running whatever sits there.
    /// </summary>
    public static string? Find(string tool, string? path, Func<string, bool> isExecutable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentNullException.ThrowIfNull(isExecutable);

        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (string directory in path.Split(':'))
        {
            if (directory.Length == 0 || !Path.IsPathRooted(directory))
            {
                continue;
            }

            string candidate = Path.Combine(directory, tool);

            if (isExecutable(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The known tools present on the real <c>PATH</c>.</summary>
    public static IReadOnlySet<string> Available()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");

        return Known.Where(tool => Find(tool, path, IsExecutableFile) is not null).ToHashSet(StringComparer.Ordinal);
    }

    private static bool IsExecutableFile(string candidate)
    {
        try
        {
            return File.Exists(candidate)
                && (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
