using System.Text;
using HexLinux.Configuration;

namespace HexLinux.Session;

/// <summary>
/// Where the control socket and the single-instance lock go.
///
/// <para>The runtime folder (<c>$XDG_RUNTIME_DIR/hexlinux</c>) first: private
/// to the user, in memory, emptied at logout. But a Unix socket's path has to
/// fit in 107 bytes (<c>sun_path</c> is 108, its terminating zero included):
/// when it does not, the state folder is tried, and when neither fits the
/// daemon runs without a socket — the shortcut still works, only
/// <c>--toggle</c> does not. The daemon and its clients apply the same rule,
/// so they always meet at the same path.</para>
///
/// <para>Pure: which folders exist and who owns them is the server's
/// business.</para>
/// </summary>
public static class ControlPaths
{
    public const int MaxSocketPathBytes = 107;

    public const string SocketName = "control.sock";
    public const string LockName = "daemon.lock";

    /// <summary>The folder to use, or null when no candidate leaves room for the socket's path.</summary>
    public static string? Folder(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        foreach (string folder in (string[])[paths.RuntimeDirectory, paths.StateDirectory])
        {
            if (Fits(Path.Combine(folder, SocketName)))
            {
                return folder;
            }
        }

        return null;
    }

    public static bool Fits(string socketPath)
    {
        ArgumentNullException.ThrowIfNull(socketPath);

        return Encoding.UTF8.GetByteCount(socketPath) <= MaxSocketPathBytes;
    }
}
