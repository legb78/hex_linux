using System.Diagnostics.CodeAnalysis;
using System.Text;
using HexLinux.Configuration;

namespace HexLinux.Diagnostics;

/// <summary>
/// Operational log, in <c>$XDG_STATE_HOME/hexlinux/hexlinux.log</c>
/// (<c>~/.local/state/hexlinux</c> by default), and the crash report beside
/// it.
///
/// <para>With no trace, a dictation that inserts nothing cannot be diagnosed:
/// there is no telling whether the microphone caught nothing, the engine
/// recognised nothing, or the insertion failed. One line per dictation is
/// enough to settle it — durations, level, a character count, never the
/// text.</para>
///
/// <para>Private: the folder is made 0700 and the files are created 0600,
/// whatever the umask — <c>File.AppendAllText</c> would create them readable
/// by everyone on a machine with Ubuntu's default umask. Writing must never
/// fail an otherwise successful dictation: every I/O error is absorbed.</para>
///
/// <para>Also echoed on the console when there is one, so that a daemon
/// started from a terminal shows what it does.</para>
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Writes into the user's state folder; its line format is LogLine, which is tested.")]
public sealed class SessionLog
{
    /// <summary>Past this size the log starts over, keeping one previous file.</summary>
    private const long RotateAtBytes = 1024 * 1024;

    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode PrivateFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly string? _path;
    private readonly bool _echo;
    private readonly Lock _sync = new();

    private SessionLog(string? path, bool echo)
    {
        _path = path;
        _echo = echo;
    }

    /// <summary>A log that writes nothing: the diagnostic modes that only print.</summary>
    public static SessionLog Silent { get; } = new(null, echo: false);

    public static SessionLog Create(bool enabled, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        bool echo = !Console.IsErrorRedirected;

        if (!enabled)
        {
            return new SessionLog(null, echo: false);
        }

        if (!EnsurePrivateFolder(paths.StateDirectory))
        {
            return new SessionLog(null, echo);
        }

        Rotate(paths.LogFile);
        return new SessionLog(paths.LogFile, echo);
    }

    public void Write(string message)
    {
        string line = LogLine.Format(DateTime.Now, message);

        if (_echo)
        {
            Console.Error.Write(line);
        }

        if (_path is null)
        {
            return;
        }

        lock (_sync)
        {
            AppendPrivately(_path, line);
        }
    }

    /// <summary>
    /// Writes what brought the daemon down to crash.log, even when the log is
    /// off: a daemon that vanishes in silence cannot be diagnosed otherwise.
    /// Returns the path written, or null.
    /// </summary>
    public static string? WriteCrash(AppPaths paths, Exception error)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(error);

        if (!EnsurePrivateFolder(paths.StateDirectory))
        {
            return null;
        }

        return AppendPrivately(paths.CrashFile, LogLine.Format(DateTime.Now, error.ToString()) + "\n") ? paths.CrashFile : null;
    }

    /// <summary>
    /// Creates the folder 0700 and brings an existing one back to 0700: a
    /// folder created earlier, by hand or by another program, keeps its mode
    /// otherwise.
    /// </summary>
    public static bool EnsurePrivateFolder(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory, PrivateFolder);
            File.SetUnixFileMode(directory, PrivateFolder);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Appends to a file created 0600 when it does not exist yet.</summary>
    public static bool AppendPrivately(string path, string text)
    {
        try
        {
            using var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Append,
                Access = FileAccess.Write,
                Share = FileShare.ReadWrite,
                UnixCreateMode = PrivateFile,
            });

            byte[] bytes = Encoding.UTF8.GetBytes(text);
            stream.Write(bytes);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Disk full or file locked: the dictation takes priority.
            return false;
        }
    }

    private static void Rotate(string path)
    {
        try
        {
            if (new FileInfo(path) is { Exists: true, Length: > RotateAtBytes })
            {
                File.Move(path, path + ".1", overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep appending to the old one.
        }
    }
}
