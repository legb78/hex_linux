namespace HexLinux.Transcription;

/// <summary>
/// Finds the model folder from the path written in settings.json.
///
/// <para>Places are searched in this order, and the order is a security
/// decision as much as a convenience.</para>
///
/// <para><b>The XDG data folder first</b>, <c>~/.local/share/hexlinux</c> by
/// default. That is where <c>scripts/get-model.sh</c> puts the model, where a
/// Linux user expects an application's downloaded data to live, and — unlike
/// the folders around the executable — a place only the user can write to.
/// The model decides what text gets typed into the user's applications: it
/// must not be found first somewhere another local user can plant one, such as
/// <c>/tmp/models</c> next to an archive unpacked in <c>/tmp</c>.</para>
///
/// <para><b>Next to the executable</b>, for a portable copy that keeps
/// <c>models/</c> beside the binary. <b>Up the tree only from a build
/// folder</b> (<c>bin/&lt;configuration&gt;/&lt;framework&gt;</c>): during
/// development the model sits at the root of the repository, and without
/// walking up, 600 MB would have to be duplicated per build configuration.
/// Anywhere else, climbing the tree would only widen the search to folders
/// nobody chose.</para>
///
/// <para>The lookup is passed in (<c>exists</c>) so that the logic stays
/// testable without touching the disk.</para>
/// </summary>
public static class ModelLocator
{
    private const int DefaultMaxAscent = 6;

    /// <summary>
    /// Returns the absolute path of the model, or <c>null</c> if it stays out
    /// of reach.
    /// </summary>
    /// <param name="configuredPath">The <c>modelPath</c> setting; a leading <c>~/</c> means the home folder.</param>
    /// <param name="baseDirectory">The executable's folder.</param>
    /// <param name="dataDirectory">The XDG data folder, or null to skip it.</param>
    /// <param name="home">The home folder, for <c>~/</c>; null leaves the path as written.</param>
    /// <param name="exists">Whether a candidate folder exists.</param>
    /// <param name="maxAscent">How many parent folders to climb at most, from a build folder.</param>
    public static string? Resolve(
        string configuredPath,
        string baseDirectory,
        string? dataDirectory,
        string? home,
        Func<string, bool> exists,
        int maxAscent = DefaultMaxAscent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentNullException.ThrowIfNull(exists);

        string path = ExpandHome(configuredPath.Trim(), home);

        // An absolute path is taken at its word: the user knows what they want.
        if (Path.IsPathRooted(path))
        {
            return exists(path) ? path : null;
        }

        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            string candidate = Path.GetFullPath(Path.Combine(dataDirectory, path));

            if (exists(candidate))
            {
                return candidate;
            }
        }

        DirectoryInfo? directory = new(Path.TrimEndingDirectorySeparator(baseDirectory));
        int levels = IsBuildFolder(directory) ? maxAscent : 0;

        for (int level = 0; level <= levels && directory is not null; level++)
        {
            string candidate = Path.GetFullPath(Path.Combine(directory.FullName, path));

            if (exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>Variant wired to the real file system and the real home folder.</summary>
    public static string? Resolve(string configuredPath, string baseDirectory, string? dataDirectory) =>
        Resolve(
            configuredPath,
            baseDirectory,
            dataDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Directory.Exists);

    /// <summary>
    /// <c>~</c> and <c>~/…</c> stand for the home folder, as in a shell — the
    /// way a user naturally writes a path in settings.json, which no shell
    /// ever expands. <c>~other</c> is left alone.
    /// </summary>
    public static string ExpandHome(string path, string? home)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (string.IsNullOrEmpty(home))
        {
            return path;
        }

        if (path == "~")
        {
            return home;
        }

        return path.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, path[2..]) : path;
    }

    /// <summary>
    /// True for <c>…/bin/&lt;configuration&gt;/&lt;framework&gt;</c>, with or
    /// without a runtime folder below it: where <c>dotnet build</c> and
    /// <c>dotnet test</c> put the executable.
    /// </summary>
    public static bool IsBuildFolder(DirectoryInfo directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        return directory.Parent?.Parent?.Name == "bin" || directory.Parent?.Parent?.Parent?.Name == "bin";
    }
}
