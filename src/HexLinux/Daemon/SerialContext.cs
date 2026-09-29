using System.Collections.Concurrent;

namespace HexLinux.Daemon;

/// <summary>
/// The daemon's single thread: a queue of callbacks run one after the other,
/// exposed as a <see cref="SynchronizationContext"/>.
///
/// <para><b>Why one thread.</b> Every decision about a dictation — a key
/// pressed, a segment transcribed, a command from the socket, a click in the
/// tray — is taken here, in the order it arrived. That is what the Windows
/// version got from its message loop, and what lets the orchestration be a
/// close port of it: events from the reader threads are posted, and an
/// <c>await … .ConfigureAwait(true)</c> comes back to this thread.</para>
///
/// <para><b>It never throws where it cannot be caught.</b> A callback posted
/// after the loop has ended — a transcription finishing during shutdown — is
/// dropped: an exception there would surface on a pool thread and abort the
/// process. A callback that throws is reported and the loop carries on: one
/// bad event must not take dictation down until the next login.</para>
/// </summary>
public sealed class SerialContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly Action<Exception> _onError;
    private int _loopThread;

    /// <param name="onError">Told of every exception a callback lets escape; runs on the loop.</param>
    public SerialContext(Action<Exception> onError)
    {
        ArgumentNullException.ThrowIfNull(onError);

        _onError = onError;
    }

    /// <summary>True on the loop's own thread, while it runs.</summary>
    public bool IsOnLoop => Environment.CurrentManagedThreadId == Volatile.Read(ref _loopThread);

    /// <summary>Queues a callback. Silently dropped once the loop has completed.</summary>
    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);

        TryPost(d, state);
    }

    private bool TryPost(SendOrPostCallback d, object? state)
    {
        try
        {
            return _queue.TryAdd((d, state));
        }
        catch (InvalidOperationException)
        {
            // Adding is closed, or the queue is already disposed
            // (ObjectDisposedException derives from this one): the daemon is
            // stopping.
            return false;
        }
    }

    /// <summary>Queues an action, without the state argument.</summary>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        Post(static state => ((Action)state!)(), action);
    }

    /// <summary>
    /// Runs the callback and waits for it. On the loop itself it runs at once:
    /// waiting there for a callback queued behind the current one would wait
    /// forever.
    /// </summary>
    public override void Send(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);

        if (IsOnLoop)
        {
            d(state);
            return;
        }

        using var done = new ManualResetEventSlim();
        Exception? failure = null;

        bool queued = TryPost(
            _ =>
            {
                try
                {
                    d(state);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    done.Set();
                }
            },
            null);

        if (!queued)
        {
            throw new InvalidOperationException("The daemon loop has ended.");
        }

        done.Wait();

        if (failure is not null)
        {
            throw new InvalidOperationException("A callback sent to the daemon loop failed.", failure);
        }
    }

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>
    /// Runs the loop on the calling thread until <see cref="Complete"/>, then
    /// drains what was queued before it.
    /// </summary>
    public void Run()
    {
        SynchronizationContext? previous = Current;
        SetSynchronizationContext(this);
        Volatile.Write(ref _loopThread, Environment.CurrentManagedThreadId);

        try
        {
            foreach ((SendOrPostCallback callback, object? state) in _queue.GetConsumingEnumerable())
            {
                try
                {
                    callback(state);
                }
                catch (Exception ex)
                {
                    // Deliberately broad: whatever a callback lets escape is a
                    // bug to report, not a reason to stop dictating.
                    _onError(ex);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _loopThread, 0);
            SetSynchronizationContext(previous);
        }
    }

    /// <summary>Ends the loop once the callbacks already queued have run. Safe to call twice.</summary>
    public void Complete()
    {
        try
        {
            _queue.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
            // Already gone.
        }
    }

    public void Dispose() => _queue.Dispose();
}
