using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Text;
using HexLinux.Configuration;
using HexLinux.Diagnostics;
using HexLinux.Interop;

namespace HexLinux.Session;

/// <summary>
/// The daemon's side of the control channel: the single-instance lock, and a
/// Unix socket answering one line per connection.
///
/// <para><b>Single instance through a lock, not through the socket.</b> Two
/// daemons would read the same keyboard and insert every dictation twice. A
/// socket file says nothing reliable — one left behind by a crash looks like
/// a running daemon, and "connect, else remove and bind" lets two daemons
/// started together each remove the other's. An exclusive <c>flock</c> on a
/// lock file, held for the daemon's lifetime and released by the kernel even
/// on a crash, settles it; only its holder removes and binds the
/// socket.</para>
///
/// <para><b>Private by construction.</b> The folder is made 0700 even when it
/// already exists — failing to change it means it belongs to someone else,
/// and no socket is made there — and the socket 0600. Each connection's
/// credentials are checked (<c>SO_PEERCRED</c>): another user is closed on
/// without an answer. The abstract namespace, which has no permissions at
/// all, is never used.</para>
///
/// <para><b>Bounded.</b> A client gets two seconds and 64 bytes to send its
/// line; a silent one cannot hold the others up. The line is parsed here, off
/// the loop: only the parsed <see cref="ControlCommand"/> reaches the daemon,
/// and only a state word comes back.</para>
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Unix socket and flock shell, verified by the daemon smoke tests (--status, --toggle, second instance, stale socket). Its grammar is ControlCommands and ControlReply, which are tested.")]
public sealed class ControlServer : IDisposable
{
    private const int SolSocket = 1;
    private const int SoPeerCredentials = 17;
    private const int CredentialsSize = 12;
    private const UnixFileMode PrivateFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    private readonly int _lock;
    private readonly Socket? _listener;
    private readonly string? _socketPath;
    private readonly Func<ControlCommand, Task<ControlReply>> _handle;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stopping = new();

    private ControlServer(int lockDescriptor, Socket? listener, string? socketPath, Func<ControlCommand, Task<ControlReply>> handle, Action<string> log)
    {
        _lock = lockDescriptor;
        _listener = listener;
        _socketPath = socketPath;
        _handle = handle;
        _log = log;
    }

    /// <summary>The socket's path, or null when the daemon runs without one.</summary>
    public string? SocketPath => _socketPath;

    /// <summary>
    /// Takes the single-instance lock and opens the socket. Returns null when
    /// another daemon holds the lock (<paramref name="alreadyRunning"/>), or
    /// when the lock cannot be taken at all.
    /// </summary>
    public static ControlServer? TryStart(
        AppPaths paths,
        Func<ControlCommand, Task<ControlReply>> handle,
        Action<string> log,
        out bool alreadyRunning)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(log);

        alreadyRunning = false;

        string? folder = ControlPaths.Folder(paths);

        if (folder is null || !MakePrivate(folder))
        {
            // No private folder for the lock: better one unguarded daemon
            // than none, with a line saying so.
            log($"no private folder for the control socket ({folder ?? "paths too long"}): running without it");
            return new ControlServer(-1, null, null, handle, log);
        }

        string lockPath = Path.Combine(folder, ControlPaths.LockName);
        int descriptor = Libc.Open(lockPath, Libc.ReadWrite | Libc.Create | Libc.CloseOnExec, (uint)PrivateFile);

        if (descriptor < 0)
        {
            log($"the lock file {lockPath} cannot be opened: {Libc.Describe(Libc.LastError)}");
            return null;
        }

        if (Libc.Flock(descriptor, Libc.LockExclusive | Libc.LockNonBlocking) < 0)
        {
            Libc.Close(descriptor);
            alreadyRunning = true;
            return null;
        }

        string socketPath = Path.Combine(folder, ControlPaths.SocketName);

        try
        {
            // Whoever holds the lock owns the socket's path: a file there now
            // was left by a daemon that died.
            if (File.Exists(socketPath))
            {
                File.Delete(socketPath);
            }

            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            File.SetUnixFileMode(socketPath, PrivateFile);
            listener.Listen(8);

            var server = new ControlServer(descriptor, listener, socketPath, handle, log);
            _ = server.AcceptLoopAsync();
            return server;
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException)
        {
            log($"the control socket cannot be opened ({ex.Message}): running without it");
            return new ControlServer(descriptor, null, null, handle, log);
        }
    }

    private static bool MakePrivate(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder, PrivateFolder);

            // CreateDirectory leaves an existing folder's mode as it was.
            File.SetUnixFileMode(folder, PrivateFolder);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            Socket client;

            try
            {
                client = await _listener!.AcceptAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            // One connection at a time is plenty, each bounded by its timeout.
            await ServeAsync(client).ConfigureAwait(false);
        }
    }

    private async Task ServeAsync(Socket client)
    {
        using (client)
        {
            try
            {
                if (!FromSameUser(client))
                {
                    _log("a control connection from another user was refused");
                    return;
                }

                string? line = await ReadLineAsync(client).ConfigureAwait(false);
                ControlCommand command = ControlCommands.Parse(line);

                ControlReply reply = command == ControlCommand.Unknown
                    ? ControlReply.UnknownCommand
                    : await _handle(command).ConfigureAwait(false);

                await client.SendAsync(Encoding.ASCII.GetBytes(reply.Format() + "\n"), SocketFlags.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The client went away; nothing to answer.
            }
        }
    }

    private static bool FromSameUser(Socket client)
    {
        byte[] credentials = new byte[CredentialsSize];
        int length = client.GetRawSocketOption(SolSocket, SoPeerCredentials, credentials);

        // struct ucred { pid_t pid; uid_t uid; gid_t gid; }: the uid at 4.
        return length == CredentialsSize
            && BinaryPrimitives.ReadUInt32LittleEndian(credentials.AsSpan(4)) == Libc.GetEffectiveUid();
    }

    private static async Task<string?> ReadLineAsync(Socket client)
    {
        using var timeout = new CancellationTokenSource(ReadTimeout);
        byte[] buffer = new byte[ControlCommands.MaxLineLength];
        int length = 0;

        while (length < buffer.Length)
        {
            int read = await client.ReceiveAsync(buffer.AsMemory(length), SocketFlags.None, timeout.Token).ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            int newline = Array.IndexOf(buffer, (byte)'\n', length, read);
            length += read;

            if (newline >= 0)
            {
                return Encoding.ASCII.GetString(buffer, 0, newline);
            }
        }

        // Too long, or closed without a newline: whatever arrived, if it is a word.
        return length < buffer.Length ? Encoding.ASCII.GetString(buffer, 0, length) : null;
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener?.Dispose();

        if (_socketPath is not null)
        {
            try
            {
                File.Delete(_socketPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Removed at the next start, under the lock.
            }
        }

        if (_lock >= 0)
        {
            // Closing releases the lock. The file stays: removing it could let
            // two daemons lock two different files.
            Libc.Close(_lock);
        }

        _stopping.Dispose();
    }
}

/// <summary>The command line's side: one request, one reply, then gone.</summary>
[ExcludeFromCodeCoverage(Justification = "Unix socket shell, verified by the smoke tests.")]
public static class ControlClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Sends <paramref name="command"/> to the running daemon. Returns null
    /// when no daemon answers.
    /// </summary>
    public static string? Send(AppPaths paths, ControlCommand command)
    {
        ArgumentNullException.ThrowIfNull(paths);

        string? folder = ControlPaths.Folder(paths);

        if (folder is null)
        {
            return null;
        }

        string socketPath = Path.Combine(folder, ControlPaths.SocketName);

        if (!File.Exists(socketPath))
        {
            return null;
        }

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            using var timeout = new CancellationTokenSource(Timeout);

            socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeout.Token).AsTask().GetAwaiter().GetResult();
            socket.Send(Encoding.ASCII.GetBytes(ControlCommands.Name(command) + "\n"));

            byte[] buffer = new byte[256];
            int length = 0;

            while (length < buffer.Length)
            {
                int read = socket.ReceiveAsync(buffer.AsMemory(length), SocketFlags.None, timeout.Token).AsTask().GetAwaiter().GetResult();

                if (read == 0)
                {
                    break;
                }

                length += read;

                if (Array.IndexOf(buffer, (byte)'\n', 0, length) >= 0)
                {
                    break;
                }
            }

            return Encoding.ASCII.GetString(buffer, 0, length).Trim();
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            return null;
        }
    }
}
