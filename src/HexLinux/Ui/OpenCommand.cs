namespace HexLinux.Ui;

/// <summary>
/// What "Open settings file" and "Open log folder" hand to <c>xdg-open</c>.
///
/// <para>The path travels as one argument of the command line, never through
/// a shell: a path with a space, a quote or a <c>$</c> in it then reaches
/// <c>xdg-open</c> exactly as it is, and nothing in it can run as a command.
/// Only absolute paths are accepted. They begin with a slash, so none can be
/// mistaken for one of <c>xdg-open</c>'s options, and none is resolved against
/// whatever directory the daemon happens to run in.</para>
/// </summary>
public static class OpenCommand
{
    /// <summary>The freedesktop.org opener: the default application of the desktop in use.</summary>
    public const string Program = "xdg-open";

    /// <summary>
    /// True when <paramref name="path"/> can be handed to <see cref="Program"/>:
    /// absolute, and free of the NUL byte no command-line argument can carry.
    /// </summary>
    public static bool CanOpen(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && path.StartsWith('/')
        && !path.Contains('\0', StringComparison.Ordinal);

    /// <summary>
    /// The arguments of <see cref="Program"/> for <paramref name="path"/>: the
    /// path alone. Refuses what <see cref="CanOpen"/> refuses, so that a
    /// caller cannot skip the check.
    /// </summary>
    public static IReadOnlyList<string> Arguments(string path)
    {
        if (!CanOpen(path))
        {
            throw new ArgumentException("Only an absolute path can be opened.", nameof(path));
        }

        return [path];
    }
}
