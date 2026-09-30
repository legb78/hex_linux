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
/// line. Lines are read from several clients at once, so silent ones cannot
/// hold the others up (verified before the change: nine idle connections
/// made <c>--status</c> fail as "not running"); the commands they carry are
/// then handed to the daemon one at a time, in the order they came, and each
/// gets an answer within a few seconds — <c>error busy</c> when the daemon
/// could not act in time. The line is parsed here, off the loop: only the
/// parsed <see cref="ControlCommand"/> reaches the daemon, and only a state
/// word comes back.</para>
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Unix socket and flock shell, verified by the daemon smoke tests (--status, --toggle, second instance, stale socket). Its grammar is ControlCommands and ControlReply, which are tested.")]
public sealed class ControlServer : IDisposable
{
    private const int SolSocket = 1;
    private const int SoPeerCredentials = 17;
    private const int CredentialsSize = 12;
    private const UnixFileMode PrivateFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Connections read at the same time; past it, a new one is closed unanswered.</summary>
    private const int MaxClients = 16;

    /// <summary>Long enough for a line typed by hand through socat.</summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a command may take on the daemon's loop before the client is
    /// told <c>error busy</c>: above the five seconds the microphone may take
    /// to open, below the client's own ten.
    /// </summary>
    private static readonly TimeSpan HandleTimeout = TimeSpan.FromSeconds(8);

    private readonly int _lock;
    private readonly Socket? _listener;
    private readonly string? _socketPath;
    private readonly Func<ControlCommand, Task<ControlReply>> _handle;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stopping = new();

    // Never disposed: a connection still being served may release them after
    // the server is gone.
    private readonly SemaphoreSlim _clients = new(MaxClients, MaxClients);
    private readonly SemaphoreSlim _commands = new(1, 1);

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
    /// when the lock cannot be taken at all — no daemon then, rather than one
    /// that could run twice. Without a socket, the daemon runs: the shortcut
    /// works, only <c>--toggle</c> and the like do not.
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

        string? lockFolder = ControlPaths.LockFolders(paths).FirstOrDefault(MakePrivate);

        if (lockFolder is null)
        {
            log($"no private folder can hold the single-instance lock ({string.Join(", ", ControlPaths.LockFolders(paths))}): not starting, since a second daemon could not be detected");
            return null;
        }

        string lockPath = Path.Combine(lockFolder, ControlPaths.LockName);
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

        string? folder = ControlPaths.Folder(paths);

        if (folder is null || (folder != lockFolder && !MakePrivate(folder)))
        {
            log($"no private folder for the control socket ({folder ?? "paths too long"}): running without it, the shortcut still works");
            return new ControlServer(descriptor, null, null, handle, log);
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

            if (!_clients.Wait(TimeSpan.Zero))
            {
                // Every slot taken by clients that say nothing: this one is
                // closed at once rather than queued behind them.
                client.Dispose();
                continue;
            }

            _ = ServeThenReleaseAsync(client);
        }
    }

    private async Task ServeThenReleaseAsync(Socket client)
    {
        try
        {
            await ServeAsync(client).ConfigureAwait(false);
        }
        finally
        {
            _clients.Release();
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
                    : await HandleInTurnAsync(command).ConfigureAwait(false);

                await client.SendAsync(Encoding.ASCII.GetBytes(reply.Format() + "\n"), SocketFlags.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The client went away, or the daemon is stopping; nothing to answer.
            }
        }
    }

    /// <summary>
    /// One command at a time, as when connections were served one by one: two
    /// toggles sent together still read as a start then a stop.
    /// </summary>
    private async Task<ControlReply> HandleInTurnAsync(ControlCommand command)
    {
        await _commands.WaitAsync(_stopping.Token).ConfigureAwait(false);

        try
        {
            return await _handle(command).WaitAsync(HandleTimeout, _stopping.Token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log($"control command {ControlCommands.Name(command)} not answered within {HandleTimeout.TotalSeconds:F0} s");
            return ControlReply.Busy;
        }
        finally
        {
            _commands.Release();
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

/// <summary>Whether a control request reached a daemon, and what it answered.</summary>
public enum ControlDelivery
{
    /// <summary>No daemon listens: no socket, or one left behind by a daemon that died.</summary>
    NotRunning,

    /// <summary>A daemon is there but gave no answer in time — busy, or stuck.</summary>
    NoAnswer,

    /// <summary>The daemon answered one line.</summary>
    Answered,
}

/// <summary>The outcome of one control request.</summary>
/// <param name="Delivery">Whether a daemon was reached.</param>
/// <param name="Line">Its reply, when it answered.</param>
public readonly record struct ControlResponse(ControlDelivery Delivery, string? Line = null);

/// <summary>
/// The command line's side: one request, one reply, then gone.
///
/// <para>"Nobody listens" and "nobody answered in time" are told apart: a
/// <c>--start</c> waiting on a slow microphone used to print "HexLinux is not
/// running" while the daemon ran, and the dictation had perhaps
/// started.</para>
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Unix socket shell, verified by the smoke tests.")]
public static class ControlClient
{
    /// <summary>Above the daemon's own bound on a command (eight seconds): its "error busy" arrives first.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Sends <paramref name="command"/> to the running daemon.</summary>
    public static ControlResponse Send(AppPaths paths, ControlCommand command)
    {
        ArgumentNullException.ThrowIfNull(paths);

        string? folder = ControlPaths.Folder(paths);

        if (folder is null)
        {
            return new ControlResponse(ControlDelivery.NotRunning);
        }

        string socketPath = Path.Combine(folder, ControlPaths.SocketName);

        if (!File.Exists(socketPath))
        {
            return new ControlResponse(ControlDelivery.NotRunning);
        }

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var timeout = new CancellationTokenSource(Timeout);

        try
        {
            socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeout.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionRefused or SocketError.AddressNotAvailable)
        {
            // A socket file nobody listens on: left behind by a daemon that died.
            return new ControlResponse(ControlDelivery.NotRunning);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            // Listening but not accepting: a full queue, a stuck daemon.
            return new ControlResponse(ControlDelivery.NoAnswer);
        }

        try
        {
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

            string line = Encoding.ASCII.GetString(buffer, 0, length).Trim();

            // Closed without a word: the daemon went away while answering.
            return line.Length > 0 ? new ControlResponse(ControlDelivery.Answered, line) : new ControlResponse(ControlDelivery.NoAnswer);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            return new ControlResponse(ControlDelivery.NoAnswer);
        }
    }
}
