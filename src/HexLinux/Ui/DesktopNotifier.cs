using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Tmds.DBus.Protocol;

namespace HexLinux.Ui;

/// <summary>
/// Shows the daemon's failures as desktop notifications: the
/// <c>org.freedesktop.Notifications</c> interface over the surface's own
/// connection, and the log when no server can show them.
///
/// <para><b>Never blocks, never throws.</b> <see cref="Notify"/> starts the
/// work and returns at once; every step has a time limit. A notification that
/// nothing could show ends up in the log, whole, so that the message is never
/// lost — which is what happens in WSL, where the session bus has no
/// notification server.</para>
///
/// <para><b>When notify-send, and when not.</b> <c>notify-send</c> talks to the
/// same service over the same bus: when the bus answered that no server is
/// there, or the server did not answer in time, it would fail the same way
/// (the feasibility study saw exactly that, <c>ServiceUnknown</c>, in WSL), so
/// the message goes straight to the log. It is only tried when the surface's
/// own connection is what failed — never established, or lost — since
/// libnotify then makes a connection of its own. It is run by absolute path,
/// its arguments one by one, never through a shell; what it receives is
/// readable by every local user in <c>/proc</c>, which is why notifications
/// only ever carry failure messages (see <see cref="NotificationRules"/>).</para>
///
/// <para><b>What is under way is known.</b> The daemon may notify and quit at
/// once — the model is missing at start-up, and HexLinux exits with code 2
/// right after saying so, as HexWin does after its message box. A
/// notification still in flight would die with the process, so the surface
/// asks <see cref="Flush"/> to let the pending ones finish, within a bound,
/// before it lets go of the bus.</para>
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Shell over the session bus and a child process; verified by the front harness.")]
internal sealed class DesktopNotifier
{
    /// <summary>How an attempt at the server ended.</summary>
    private enum Outcome
    {
        Shown,

        /// <summary>The bus answered, but no server did: nothing else can show it.</summary>
        NoServer,

        /// <summary>This connection is unusable: another connection might still succeed.</summary>
        ConnectionUnusable,
    }

    /// <summary>
    /// Longest wait for the notification server. A healthy one answers in
    /// milliseconds; one that has to be started by the bus takes longer, and
    /// beyond this the message is better off in the log than in limbo.
    /// </summary>
    private static readonly TimeSpan ServerTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Longest wait for notify-send, which exits once the server has answered.</summary>
    private static readonly TimeSpan NotifySendTimeout = TimeSpan.FromSeconds(5);

    private const string NotifySend = "notify-send";

    private readonly Task<DBusConnection?> _connection;
    private readonly Action<string> _log;
    private readonly NotificationLedger _ledger = new();

    /// <summary>The notifications started and not finished yet, for <see cref="Flush"/>.</summary>
    private readonly HashSet<Task> _pending = [];
    private readonly Lock _pendingGate = new();

    /// <summary>
    /// Whether the server reads markup; null until a server has answered
    /// GetCapabilities, and again after a server failed, so that the one
    /// that replaces it is asked afresh.
    /// </summary>
    private bool? _serverReadsMarkup;

    /// <param name="connection">
    /// The surface's connection, once established; null when it could not
    /// be. Awaited, so that a notification raised at start-up waits for the
    /// connection instead of skipping it.
    /// </param>
    /// <param name="log">The daemon's log.</param>
    public DesktopNotifier(Task<DBusConnection?> connection, Action<string> log)
    {
        _connection = connection;
        _log = log;
    }

    /// <summary>Starts showing the notification and returns at once.</summary>
    public void Notify(string title, string body)
    {
        Task showing = ShowAsync(title ?? string.Empty, body ?? string.Empty);

        lock (_pendingGate)
        {
            _pending.Add(showing);
        }

        // Removed once done, on whatever thread finishes it. A notification
        // that finished before this line is simply removed at once.
        _ = showing.ContinueWith(
            done =>
            {
                lock (_pendingGate)
                {
                    _pending.Remove(done);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Waits, at most <paramref name="limit"/>, for the notifications already
    /// started to be shown or logged. Called on disposal only, after the
    /// daemon's loop has stopped: it is the one wait of the interface layer,
    /// and it is bounded, so quitting can never hang on a silent server.
    /// </summary>
    /// <returns>True when nothing was left pending.</returns>
    public bool Flush(TimeSpan limit)
    {
        Task[] pending;

        lock (_pendingGate)
        {
            pending = [.. _pending];
        }

        if (pending.Length == 0)
        {
            return true;
        }

        try
        {
            // ShowAsync catches everything, so none of these can fault.
            return Task.WhenAll(pending).Wait(limit);
        }
        catch (AggregateException)
        {
            return false;
        }
    }

    private async Task ShowAsync(string title, string body)
    {
        try
        {
            DBusConnection? connection = await _connection.ConfigureAwait(false);

            if (connection is not null)
            {
                (Outcome outcome, string reason) = await TryServerAsync(connection, title, body).ConfigureAwait(false);

                if (outcome == Outcome.Shown)
                {
                    return;
                }

                if (outcome == Outcome.NoServer)
                {
                    _serverReadsMarkup = null;
                    _log($"notification not shown (no notification server: {reason}): {title}: {body}");
                    return;
                }
            }

            if (await TryNotifySendAsync(title, body).ConfigureAwait(false))
            {
                return;
            }

            _log($"notification not shown (no session bus connection, {NotifySend} failed): {title}: {body}");
        }
        catch (Exception ex)
        {
            // Last resort: the message must still reach the log.
            _log($"notification not shown ({ex.GetType().Name}: {ex.Message}): {title}: {body}");
        }
    }

    private async Task<(Outcome Outcome, string Reason)> TryServerAsync(DBusConnection connection, string title, string body)
    {
        try
        {
            bool markup = _serverReadsMarkup ??= await ReadsMarkupAsync(connection).ConfigureAwait(false);

            uint replaces = _ledger.ReplacesIdFor(title);
            uint id = await connection
                .CallMethodAsync(CreateNotify(connection, replaces, title, NotificationRules.Body(body, markup)), static (message, _) => message.GetBodyReader().ReadUInt32())
                .WaitAsync(ServerTimeout)
                .ConfigureAwait(false);

            _ledger.Remember(title, id);
            return (Outcome.Shown, string.Empty);
        }
        catch (Exception ex) when (ex is DBusConnectionException or ObjectDisposedException or InvalidOperationException)
        {
            // Closed, never connected, or disposed: this connection is the
            // problem, not the server.
            return (Outcome.ConnectionUnusable, Describe(ex));
        }
        catch (Exception ex)
        {
            // Deliberately broad: an error reply (no server), no answer in
            // time, a reply of the wrong shape — the bus is there, the server
            // is not usable, and the message goes to the log instead of being
            // lost.
            return (Outcome.NoServer, Describe(ex));
        }
    }

    private static async Task<bool> ReadsMarkupAsync(DBusConnection connection)
    {
        string[] capabilities = await connection
            .CallMethodAsync(CreateGetCapabilities(connection), static (message, _) => message.GetBodyReader().ReadArrayOfString())
            .WaitAsync(ServerTimeout)
            .ConfigureAwait(false);

        return capabilities.Contains("body-markup", StringComparer.Ordinal);
    }

    private static MessageBuffer CreateGetCapabilities(DBusConnection connection)
    {
        MessageWriter writer = connection.GetMessageWriter();

        try
        {
            writer.WriteMethodCallHeader(
                destination: TrayNames.NotificationsService,
                path: TrayNames.NotificationsPath,
                @interface: TrayNames.NotificationsInterface,
                member: "GetCapabilities");

            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>
    /// <c>Notify(app_name, replaces_id, app_icon, summary, body, actions,
    /// hints, expire_timeout)</c>: no action, no hint, and -1 for the
    /// server's own expiry, which follows the user's settings.
    /// </summary>
    private static MessageBuffer CreateNotify(DBusConnection connection, uint replaces, string title, string body)
    {
        MessageWriter writer = connection.GetMessageWriter();

        try
        {
            writer.WriteMethodCallHeader(
                destination: TrayNames.NotificationsService,
                path: TrayNames.NotificationsPath,
                @interface: TrayNames.NotificationsInterface,
                member: "Notify",
                signature: "susssasa{sv}i");

            writer.WriteString(NotificationRules.AppName);
            writer.WriteUInt32(replaces);
            writer.WriteString(NotificationRules.Icon);
            writer.WriteString(title);
            writer.WriteString(body);
            writer.WriteArray(Array.Empty<string>());

            ArrayStart hints = writer.WriteDictionaryStart();
            writer.WriteDictionaryEnd(hints);

            writer.WriteInt32(-1);

            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private async Task<bool> TryNotifySendAsync(string title, string body)
    {
        string? program = ExecutableSearch.Find(NotifySend, Environment.GetEnvironmentVariable("PATH"), IsExecutableFile);

        if (program is null)
        {
            _log($"{NotifySend} not found (package libnotify-bin on Debian and Ubuntu)");
            return false;
        }

        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,

            // notify-send does not fork: its output can be read to the end
            // without waiting on a child that outlives it.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string argument in NotificationRules.NotifySendArguments(title, body))
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using Process? process = Process.Start(start);

            if (process is null)
            {
                return false;
            }

            process.StandardInput.Close();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            Task<string> output = process.StandardOutput.ReadToEndAsync();

            using var timeout = new CancellationTokenSource(NotifySendTimeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                _log($"{NotifySend} did not finish within {NotifySendTimeout.TotalSeconds:0} s");
                return false;
            }

            string error = (await errors.ConfigureAwait(false)).Trim();
            _ = await output.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                _log($"{NotifySend} failed (exit code {process.ExitCode}){(error.Length > 0 ? ": " + error : string.Empty)}");
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            _log($"{NotifySend} could not run: {ex.Message}");
            return false;
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        DBusErrorReplyException reply => reply.ErrorName,
        TimeoutException => $"no answer within {ServerTimeout.TotalSeconds:0} s",
        _ => ex.GetType().Name,
    };

    internal static bool IsExecutableFile(string candidate)
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
