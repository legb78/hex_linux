namespace HexLinux.Transcription;

/// <summary>
/// Finds the model folder from the path written in settings.json.
///
/// <para>Two places are searched, in this order.</para>
///
/// <para><b>Next to the executable, then up the tree.</b> A portable copy
/// keeps <c>models/</c> beside the binary and the search stops at once.
/// During development the executable lives deep inside
/// <c>bin/Release/net10.0/</c> while <c>models/</c> sits at the root of the
/// repository: without walking up, 600 MB would have to be duplicated for
/// every build configuration.</para>
///
/// <para><b>The XDG data folder</b>, <c>~/.local/share/hexlinux</c> by
/// default. That is where <c>scripts/get-model.sh</c> puts the model unless
/// told otherwise, because it is where a Linux user expects an application's
/// downloaded data to live, and it survives replacing the executable.</para>
///
/// <para>The lookup is passed in (<paramref name="exists"/>) so that the logic
/// stays testable without touching the disk.</para>
/// </summary>
public static class ModelLocator
{
    private const int DefaultMaxAscent = 6;

    /// <summary>
    /// Returns the absolute path of the model, or <c>null</c> if it stays out
    /// of reach.
    /// </summary>
    /// <param name="configuredPath">The <c>modelPath</c> setting.</param>
    /// <param name="baseDirectory">The executable's folder.</param>
    /// <param name="dataDirectory">The XDG data folder, or null to skip it.</param>
    /// <param name="exists">Whether a candidate folder exists.</param>
    /// <param name="maxAscent">How many parent folders to climb at most.</param>
    public static string? Resolve(
        string configuredPath,
        string baseDirectory,
        string? dataDirectory,
        Func<string, bool> exists,
        int maxAscent = DefaultMaxAscent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentNullException.ThrowIfNull(exists);

        // An absolute path is taken at its word: the user knows what they want.
        if (Path.IsPathRooted(configuredPath))
        {
            return exists(configuredPath) ? configuredPath : null;
        }

        DirectoryInfo? directory = new(baseDirectory);

        for (int level = 0; level <= maxAscent && directory is not null; level++)
        {
            string candidate = Path.GetFullPath(Path.Combine(directory.FullName, configuredPath));

            if (exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            string candidate = Path.GetFullPath(Path.Combine(dataDirectory, configuredPath));

            if (exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Variant wired to the real file system. The Parakeet model is a folder —
    /// encoder, decoder, joiner and vocabulary — hence Directory.Exists rather
    /// than File.Exists.
    /// </summary>
    public static string? Resolve(string configuredPath, string baseDirectory, string? dataDirectory) =>
        Resolve(configuredPath, baseDirectory, dataDirectory, Directory.Exists);
}
