using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using HexLinux.Daemon;
using Tmds.DBus.Protocol;

namespace HexLinux.Ui;

/// <summary>
/// The tray icon and the desktop notifications, over the session bus: a
/// StatusNotifierItem with its dbusmenu menu, registered with the
/// StatusNotifierWatcher that the panel provides.
///
/// <para><b>Nothing here ever holds up the daemon.</b> The daemon calls
/// <see cref="Update"/> and <see cref="Notify"/> on its single loop, which
/// also runs every dictation. Both only store or queue, and return: the
/// connection is made in the background, the signals that tell the host
/// something changed are sent by a worker of their own, in order, and every
/// call to another process has a time limit. Neither ever throws.</para>
///
/// <para><b>The icon follows the panel, not the other way round.</b> At
/// login, an autostart entry often starts before the panel; a panel can
/// crash and restart; GNOME only has a tray with the AppIndicator extension.
/// The item is therefore exported as soon as the bus answers and stays there,
/// the owner of <c>org.kde.StatusNotifierWatcher</c> is watched, and the item
/// registers with every new owner — which is also what KDE's own items do.
/// While no watcher exists, the surface is notifications only, and says so in
/// the log once ("no tray host").</para>
///
/// <para><b>Threads.</b> The library dispatches incoming calls on its own
/// threads; they only read the current <see cref="TrayView"/>, replaced whole
/// by <see cref="Update"/>, so a host never reads half of one snapshot and
/// half of the next. A menu click is queued to the worker, which raises
/// <see cref="Requested"/> there: off the bus's thread, off the daemon's
/// loop, which the contract lets the daemon post onto.</para>
///
/// <para><b>Disposal is the one call that waits</b>, at most
/// <see cref="FlushLimit"/>, for the notifications already under way: the
/// daemon notifies "model not found" and exits straight after, and that
/// message must reach the screen rather than die with the process. It is
/// called once the daemon's loop has stopped, never while it runs.</para>
/// </summary>
[ExcludeFromCodeCoverage(Justification = "D-Bus shell: needs a session bus and a tray host; verified on a private bus by the front harness.")]
internal sealed class TraySurface : IStatusSurface
{
    /// <summary>
    /// Longest wait for an answer from the bus, the watcher or its host. A
    /// healthy peer answers in milliseconds; past this, the peer is treated as
    /// absent and the daemon carries on without it.
    /// </summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Wait before registering again with a watcher that refused or did not answer.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Longest wait, on disposal, for the notifications still under way. A
    /// healthy server answers in milliseconds, even with the connection still
    /// to be made; a silent one must not make quitting feel stuck.
    /// </summary>
    private static readonly TimeSpan FlushLimit = TimeSpan.FromSeconds(3);

    private readonly string _address;
    private readonly Action<string> _log;
    private readonly IReadOnlyDictionary<DictationState, IReadOnlyList<IconPixmap>> _icons;
    private readonly DesktopNotifier _notifier;
    private readonly FileOpener _opener;

    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource<DBusConnection?> _connected =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The worker's queue: signals to send and requests to raise, one after the other.</summary>
    private readonly Channel<Action> _work =
        Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Lock _gate = new();

    private TrayView _view = TrayPresentation.From(TrayPresentation.InitialSnapshot);

    /// <summary>The connection, once the objects are exported on it; null before and after.</summary>
    private DBusConnection? _exported;

    /// <summary>Every connection this surface opened, to be closed on disposal.</summary>
    private DBusConnection? _owned;

    /// <summary>The dbusmenu layout revision; moves only if entries appear or vanish.</summary>
    private uint _revision = 1;

    private volatile bool _registered;
    private volatile bool _hostPresent = true;
    private int _disposed;

    public TraySurface(string address, Action<string> log)
    {
        _address = address;
        _log = log;
        _icons = Enum.GetValues<DictationState>().ToDictionary(state => state, TrayIcon.RenderAll);
        _notifier = new DesktopNotifier(_connected.Task, log);
        _opener = new FileOpener(log, _notifier.Notify);

        _ = Task.Run(RunWorkerAsync);
        _ = Task.Run(StartAsync);
    }

    public bool IsVisible => _registered && _hostPresent && Volatile.Read(ref _disposed) == 0;

    public event EventHandler<SurfaceRequest>? Requested;

    public void Update(StatusSnapshot snapshot)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            TrayView next = TrayPresentation.From(snapshot);

            // Replacing the view and queueing its announcement under one lock
            // keeps the announcements in the order of the snapshots.
            lock (_gate)
            {
                TrayView previous = _view;
                Volatile.Write(ref _view, next);
                _work.Writer.TryWrite(() => Announce(previous, next));
            }
        }
        catch (Exception ex)
        {
            Log($"tray: the snapshot could not be shown ({ex.GetType().Name}: {ex.Message})");
        }
    }

    public void Notify(string title, string body)
    {
        try
        {
            _notifier.Notify(title, body);
        }
        catch (Exception ex)
        {
            Log($"notification not shown ({ex.GetType().Name}: {ex.Message}): {title}: {body}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            // First, while the connection is still there to carry them.
            if (!_notifier.Flush(FlushLimit))
            {
                Log($"tray: notifications still pending after {FlushLimit.TotalSeconds:0} s, left behind");
            }

            _stopping.Cancel();
            _work.Writer.TryComplete();

            DBusConnection? connection;

            lock (_gate)
            {
                connection = _owned;
                _owned = null;
            }

            Volatile.Write(ref _exported, null);
            _registered = false;

            // Closing the connection releases the item's name: the watcher
            // drops the icon at once, without waiting for the process to end.
            connection?.Dispose();
            _connected.TrySetResult(null);
        }
        catch (Exception ex)
        {
            Log($"tray: shutdown incomplete ({ex.GetType().Name}: {ex.Message})");
        }
    }

    // --- Start-up ----------------------------------------------------------------

    private async Task StartAsync()
    {
        try
        {
            var options = new DBusConnectionOptions(_address) { OnException = OnConnectionException };
            var connection = new DBusConnection(options);

            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) == 1)
                {
                    connection.Dispose();
                    return;
                }

                _owned = connection;
            }

            await connection.ConnectAsync().AsTask().WaitAsync(CallTimeout, _stopping.Token).ConfigureAwait(false);

            connection.AddMethodHandlers(
            [
                new StatusNotifierItemObject(CurrentView, _icons, Log),
                new DBusMenuObject(CurrentView, () => Volatile.Read(ref _revision), OnClicked, Log),
            ]);

            string service = await ClaimNameAsync(connection).ConfigureAwait(false);

            Volatile.Write(ref _exported, connection);
            _connected.TrySetResult(connection);
            _ = WatchDisconnectionAsync(connection);

            await KeepRegisteredAsync(connection, service).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Disposed while starting or waiting: nothing to report.
        }
        catch (DBusConnectionClosedException)
        {
            // Reported by WatchDisconnectionAsync, which saw it first.
        }
        catch (Exception ex)
        {
            Log(SurfaceChoice.Unreachable(Describe(ex)));
        }
        finally
        {
            // Notifications waiting for the connection go on without it.
            _connected.TrySetResult(null);
        }
    }

    /// <summary>
    /// Claims <c>org.kde.StatusNotifierItem-PID-1</c>, the name hosts expect;
    /// falls back to the connection's own unique name, which every watcher
    /// read for this work accepts as well (KDE's own items register that way).
    /// </summary>
    private async Task<string> ClaimNameAsync(DBusConnection connection)
    {
        string wellKnown = TrayNames.ItemService(Environment.ProcessId);

        try
        {
            if (await connection.TryRequestNameAsync(wellKnown, RequestNameOptions.None).WaitAsync(CallTimeout).ConfigureAwait(false))
            {
                return wellKnown;
            }

            Log($"tray: the name {wellKnown} is taken, the icon registers under the connection's own name");
        }
        catch (Exception ex) when (ex is DBusExceptionBase or TimeoutException)
        {
            Log($"tray: the name {wellKnown} could not be claimed ({Describe(ex)}), the icon registers under the connection's own name");
        }

        return connection.UniqueName ?? throw new InvalidOperationException("The bus gave this connection no name.");
    }

    // --- Registration with the watcher ---------------------------------------------

    /// <summary>
    /// Registers with every owner of the watcher's name, for as long as the
    /// surface lives: the first one, and each that replaces it after a panel
    /// restart. Returns only when the surface is disposed or the connection
    /// lost.
    /// </summary>
    private async Task KeepRegisteredAsync(DBusConnection connection, string service)
    {
        CancellationToken stopping = _stopping.Token;

        using NameOwnerWatcher watcher = await connection
            .WatchNameOwnerAsync(TrayNames.WatcherService)
            .WaitAsync(CallTimeout, stopping)
            .ConfigureAwait(false);

        using IDisposable hostSignals = await connection
            .AddMatchAsync(WatcherSignals, ReadMember, OnWatcherSignal, emitOnCapturedContext: false)
            .AsTask()
            .WaitAsync(CallTimeout, stopping)
            .ConfigureAwait(false);

        bool absenceLogged = false;

        while (!stopping.IsCancellationRequested)
        {
            string? owner = watcher.GetCurrentOwner();

            if (owner is null)
            {
                _registered = false;

                if (!absenceLogged)
                {
                    Log(SurfaceChoice.Describe(SurfaceKind.NotificationsOnly));
                    absenceLogged = true;
                }

                owner = await watcher.WaitForOwnerAsync(stopping).ConfigureAwait(false);
            }

            CancellationToken ownerChanged = watcher.GetOwnerChangedCancellationToken(owner);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stopping, ownerChanged);

            bool registered = await RegisterAsync(connection, owner, service).ConfigureAwait(false);

            if (registered)
            {
                absenceLogged = false;
                _registered = true;
                await RefreshHostAsync(connection).ConfigureAwait(false);
                Log($"{SurfaceChoice.Describe(SurfaceKind.Tray)} ({NameOwnerWatcher.GetOwnerBusName(owner)}, as {service})");
            }

            try
            {
                // Until the watcher changes hands; or, after a refusal, until
                // it is time to try again.
                await Task.Delay(registered ? Timeout.InfiniteTimeSpan : RetryDelay, wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
            {
                // The watcher changed hands: register with the new one.
            }

            if (registered && ownerChanged.IsCancellationRequested)
            {
                _registered = false;
                Log("tray host restarted or gone: the icon registers again with the next StatusNotifierWatcher");
            }
        }
    }

    private async Task<bool> RegisterAsync(DBusConnection connection, string owner, string service)
    {
        try
        {
            await connection.CallMethodAsync(CreateRegisterMessage(connection, owner, service)).WaitAsync(CallTimeout).ConfigureAwait(false);
            return true;
        }
        catch (DBusOwnerChangedException)
        {
            // The watcher was replaced during the call: the loop registers
            // with the new one.
            return false;
        }
        catch (Exception ex) when (ex is DBusMessageException or TimeoutException)
        {
            Log($"the StatusNotifierWatcher did not register the icon ({Describe(ex)}); next attempt in {RetryDelay.TotalSeconds:0} s");
            return false;
        }
    }

    private static MessageBuffer CreateRegisterMessage(DBusConnection connection, string owner, string service)
    {
        MessageWriter writer = connection.GetMessageWriter();

        try
        {
            // The owner identifier as destination binds the call to the
            // watcher instance being tracked: if it is replaced meanwhile, the
            // library reports it instead of registering with the wrong one.
            writer.WriteMethodCallHeader(
                destination: owner,
                path: TrayNames.WatcherPath,
                @interface: TrayNames.WatcherInterface,
                member: "RegisterStatusNotifierItem",
                signature: "s");
            writer.WriteString(service);

            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    // --- The host behind the watcher -------------------------------------------------

    /// <summary>The watcher's own signals, among them the arrival and departure of hosts.</summary>
    private static MatchRule WatcherSignals => new()
    {
        Type = MessageType.Signal,
        Sender = TrayNames.WatcherService,
        Path = TrayNames.WatcherPath,
        Interface = TrayNames.WatcherInterface,
    };

    private static string ReadMember(Message message, object? state) => message.MemberAsString ?? string.Empty;

    private void OnWatcherSignal(Notification<string> notification)
    {
        // A signal handler that throws makes the library close the
        // connection: nothing may escape.
        try
        {
            if (!notification.HasValue
                || notification.Value is not ("StatusNotifierHostRegistered" or "StatusNotifierHostUnregistered"))
            {
                return;
            }

            DBusConnection? connection = Volatile.Read(ref _exported);

            if (connection is not null)
            {
                _ = RefreshHostAsync(connection);
            }
        }
        catch (Exception ex)
        {
            Log($"tray: watcher signal ignored ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>
    /// Reads <c>IsStatusNotifierHostRegistered</c>: registered with a watcher
    /// is not yet shown, a host must be drawing the items.
    /// </summary>
    private async Task RefreshHostAsync(DBusConnection connection)
    {
        bool present;

        try
        {
            present = await connection
                .CallMethodAsync(CreateHostQuery(connection), static (message, _) => message.GetBodyReader().ReadVariantValue().GetBool())
                .WaitAsync(CallTimeout)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Deliberately broad: a watcher without the property, or with a
            // value of another type, still comes with its host in every panel
            // looked at; claiming the icon hidden would be the likelier lie.
            present = true;
        }

        if (present != _hostPresent)
        {
            _hostPresent = present;
            Log(present
                ? "tray host present: the icon is shown"
                : "the StatusNotifierWatcher reports no tray host: the icon is registered but nothing shows it");
        }
    }

    private static MessageBuffer CreateHostQuery(DBusConnection connection)
    {
        MessageWriter writer = connection.GetMessageWriter();

        try
        {
            writer.WriteMethodCallHeader(
                destination: TrayNames.WatcherService,
                path: TrayNames.WatcherPath,
                @interface: TrayNames.PropertiesInterface,
                member: "Get",
                signature: "ss");
            writer.WriteString(TrayNames.WatcherInterface);
            writer.WriteString("IsStatusNotifierHostRegistered");

            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private async Task WatchDisconnectionAsync(DBusConnection connection)
    {
        Exception? reason = await connection.DisconnectedAsync().ConfigureAwait(false);

        Volatile.Write(ref _exported, null);
        _registered = false;

        if (reason is not null && !_stopping.IsCancellationRequested)
        {
            Log($"session bus connection lost ({reason.Message}): no tray icon any more; "
                + "notifications fall back to notify-send, then to this log");
        }
    }

    // --- Menu clicks and announcements -----------------------------------------------

    private TrayView CurrentView() => Volatile.Read(ref _view);

    /// <summary>Called on the library's thread: queues the click, the worker carries it out.</summary>
    private void OnClicked(TrayMenuItem item, TrayView shown) =>
        _work.Writer.TryWrite(() => Execute(item, shown));

    private void Execute(TrayMenuItem item, TrayView shown)
    {
        // A click queued just before the daemon disposed the surface: the
        // daemon is shutting down and must not be asked for anything more.
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        switch (item.Command)
        {
            case MenuCommand.OpenSettingsFile:
                _opener.Open(shown.Snapshot.SettingsFile);
                return;

            case MenuCommand.OpenLogFolder:
                _opener.Open(shown.Snapshot.LogDirectory);
                return;
        }

        if (TrayMenu.RequestFor(item.Command, shown.Snapshot) is { } request)
        {
            Requested?.Invoke(this, request);
        }
    }

    /// <summary>
    /// Sends the host the signals for what changed between two views. Before
    /// the objects are exported there is nobody to tell: a host reads the
    /// current view when it first sees the item.
    /// </summary>
    private void Announce(TrayView previous, TrayView next)
    {
        DBusConnection? connection = Volatile.Read(ref _exported);

        if (connection is null)
        {
            return;
        }

        TrayChanges changes = TrayPresentation.Compare(previous, next);

        if (changes.HasFlag(TrayChanges.Icon))
        {
            DBusWire.TrySendSignal(connection, TrayNames.ItemPath, TrayNames.ItemInterface, "NewIcon");
        }

        if (changes.HasFlag(TrayChanges.ToolTip))
        {
            DBusWire.TrySendSignal(connection, TrayNames.ItemPath, TrayNames.ItemInterface, "NewToolTip");
        }

        if (changes.HasFlag(TrayChanges.Status))
        {
            DBusWire.TrySendSignal(connection, TrayNames.ItemPath, TrayNames.ItemInterface, "NewStatus", next.Status);
        }

        if (changes.HasFlag(TrayChanges.Menu))
        {
            AnnounceMenu(connection, DBusMenuLayout.Changes(previous.Menu, next.Menu));
        }
    }

    private void AnnounceMenu(DBusConnection connection, DBusMenuUpdate update)
    {
        MessageWriter writer = connection.GetMessageWriter();

        try
        {
            if (update.StructureChanged)
            {
                uint revision = Interlocked.Increment(ref _revision);
                writer.WriteSignalHeader(path: TrayNames.MenuPath, @interface: TrayNames.MenuInterface, member: "LayoutUpdated", signature: "ui");
                writer.WriteUInt32(revision);
                writer.WriteInt32(DBusMenuLayout.RootId);
            }
            else if (!update.IsEmpty)
            {
                writer.WriteSignalHeader(path: TrayNames.MenuPath, @interface: TrayNames.MenuInterface, member: "ItemsPropertiesUpdated", signature: "a(ia{sv})a(ias)");
                DBusWire.WriteItemProperties(ref writer, update.Updated);
                DBusWire.WriteRemovedProperties(ref writer, update.Removed);
            }
            else
            {
                return;
            }

            connection.TrySendMessage(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }

    // --- Worker and reporting -------------------------------------------------------

    private async Task RunWorkerAsync()
    {
        await foreach (Action work in _work.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                // Includes a Requested handler that threw: the next click and
                // the next snapshot must still be served.
                Log($"tray: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void OnConnectionException(DBusConnection.ExceptionContext context)
    {
        // Called synchronously by the library, which forbids it to throw.
        try
        {
            if (context.Source is DBusConnection.ExceptionSource.MethodHandler
                or DBusConnection.ExceptionSource.SignalHandler
                or DBusConnection.ExceptionSource.SignalReader)
            {
                // One bad message must not take the icon down with the
                // connection.
                context.DisconnectConnection = false;
            }

            if (context.Source != DBusConnection.ExceptionSource.ConnectionFailed)
            {
                Log($"session bus: {context.Source}: {context.Exception.GetType().Name}: {context.Exception.Message}");
            }
        }
        catch (Exception)
        {
            // Nothing can be done from here.
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        DBusErrorReplyException reply => $"{reply.ErrorName}: {reply.ErrorMessage}",
        TimeoutException => $"no answer within {CallTimeout.TotalSeconds:0} s",
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };

    private void Log(string line)
    {
        try
        {
            _log(line);
        }
        catch (Exception)
        {
            // The log is the last resort; a log that fails leaves nothing else.
        }
    }
}
