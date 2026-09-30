using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using HexLinux.Diagnostics;

namespace HexLinux.Transcription;

/// <summary>
/// Holds the engine, loads it on demand and releases it after a period of
/// inactivity.
///
/// <para><b>The reload is triggered when the dictation starts</b>, not on the
/// release: it therefore runs while the user is still speaking. On a
/// two-second sentence, the seconds of loading are almost entirely hidden,
/// leaving a short remainder instead of a full wait.</para>
///
/// <para>A lock serialises loads and releases: without it, a release
/// triggered by the timer could land between the moment a caller obtains the
/// engine and the moment it uses it.</para>
///
/// <para><b>Native work is never cut short.</b> A decode runs on a pool
/// thread inside ONNX Runtime; freeing the recognizer under it, or letting
/// the process exit while a load or a decode is still in the native code,
/// crashed the daemon with a segmentation fault (verified: SIGTERM or "Quit"
/// during a transcription, or just after a dictation while the model was
/// still loading). So each decode holds a lease on the engine, a load holds
/// the lock, and <see cref="Dispose"/> waits for both — a few seconds at
/// most — before freeing anything.</para>
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Shell: loads a native library and drives a timer. The decision to release lives in IdlePolicy, which is tested; the shutdown is checked by the SIGTERM-while-transcribing smoke test.")]
public sealed class EngineHost : IDisposable
{
    /// <summary>
    /// How long <see cref="Dispose"/> waits for a load or a decode under way.
    /// A dictation at the two-minute ceiling decodes in a couple of seconds on
    /// a laptop; past this bound the engine is left to the process exit.
    /// </summary>
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(8);

    private readonly string _modelPath;
    private readonly string _provider;
    private readonly int _threads;
    private readonly bool _frenchSpacing;
    private readonly IdlePolicy _policy;
    private readonly SessionLog _log;

    /// <summary>
    /// Serialises loads, releases and the handing out of leases. Never
    /// disposed: a timer tick or a late caller may still reach it after
    /// <see cref="Dispose"/>, and a disposed semaphore would throw there.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer? _idleCheck;

    private ParakeetEngine? _engine;
    private DateTime _lastUse = DateTime.UtcNow;
    private volatile bool _busy;
    private volatile bool _disposed;

    /// <summary>Decodes running now, each on the engine it was handed.</summary>
    private int _leases;

    public EngineHost(string modelPath, string provider, int threads, bool frenchSpacing, IdlePolicy policy, SessionLog log)
    {
        _modelPath = modelPath;
        _provider = provider;
        _threads = threads;
        _frenchSpacing = frenchSpacing;
        _policy = policy;
        _log = log;

        if (_policy.IsEnabled)
        {
            // Checked four times per deadline: fine enough to release without
            // dragging, rare enough to cost nothing.
            TimeSpan tick = TimeSpan.FromMilliseconds(Math.Max(_policy.Timeout.TotalMilliseconds / 4, 5_000));
            _idleCheck = new Timer(_ => ReleaseIfIdle(), null, tick, tick);
        }
    }

    public bool IsLoaded => _engine is not null;

    /// <summary>
    /// Starts loading without waiting. Called when a dictation starts, so the
    /// work happens while the user is speaking.
    /// </summary>
    public void BeginLoad()
    {
        if (_engine is not null || _disposed)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await GetAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Deliberately broad: nobody awaits this task. The
                // transcription waiting for the engine reports the failure.
                _log.Write($"preload failed: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Returns the engine, loading it if needed. Several simultaneous calls
    /// cause only one load. For checking the model and keeping it resident:
    /// a transcription goes through <see cref="TranscribeAsync"/>, which
    /// holds a lease while it decodes.
    /// </summary>
    public async Task<ParakeetEngine> GetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await LoadedEngineAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Transcribes <paramref name="wav"/>, loading the engine first if needed.
    /// The engine cannot be released or freed while the decode runs.
    /// </summary>
    public async Task<TranscriptionResult> TranscribeAsync(Stream wav, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wav);

        ParakeetEngine engine;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            engine = await LoadedEngineAsync(cancellationToken).ConfigureAwait(false);

            // Taken under the lock: a release or a disposal waiting for it
            // sees the lease before it looks at the engine.
            Interlocked.Increment(ref _leases);
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            return await engine.TranscribeAsync(wav, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Released on whichever thread the decode ends: after a shutdown
            // the daemon's loop no longer runs anything.
            Interlocked.Decrement(ref _leases);
            _lastUse = DateTime.UtcNow;
        }
    }

    /// <summary>Called with the lock held.</summary>
    private async Task<ParakeetEngine> LoadedEngineAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_engine is null)
        {
            var chrono = Stopwatch.StartNew();

            ParakeetEngine engine = await Task
                .Run(() => ParakeetEngine.Load(_modelPath, _provider, _threads, _frenchSpacing), cancellationToken)
                .ConfigureAwait(false);

            await engine.WarmUpAsync(cancellationToken).ConfigureAwait(false);

            _engine = engine;
            _log.Write($"model loaded in {chrono.Elapsed.TotalSeconds:F1} s");
        }

        _lastUse = DateTime.UtcNow;
        return _engine;
    }

    /// <summary>
    /// Signals that a dictation is starting or finishing. While one is running
    /// the model cannot be released, even if the idle deadline falls due.
    /// </summary>
    public void SetBusy(bool busy)
    {
        _busy = busy;

        if (!busy)
        {
            _lastUse = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// On a timer thread, where an exception would end the process: nothing
    /// may escape.
    /// </summary>
    private void ReleaseIfIdle()
    {
        try
        {
            if (_disposed || _engine is null || !_policy.ShouldUnload(DateTime.UtcNow - _lastUse, _busy))
            {
                return;
            }

            if (!_gate.Wait(TimeSpan.Zero))
            {
                // A load or a transcription is under way: try again on the
                // next pass rather than wait and block.
                return;
            }

            try
            {
                // Checked again under the lock: the state may have changed in
                // between, and a decode may hold the engine.
                if (!_disposed
                    && _engine is not null
                    && Volatile.Read(ref _leases) == 0
                    && _policy.ShouldUnload(DateTime.UtcNow - _lastUse, _busy))
                {
                    _engine.Dispose();
                    _engine = null;
                    _log.Write($"model released after {_policy.Timeout.TotalMinutes:F0} min without use");
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            _log.Write($"idle release failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Frees the engine once no load and no decode is using it, waiting a few
    /// seconds at most. Past that, the engine is left as it is: the process
    /// is ending, and freeing it under a running decode is what crashes.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Set first: no new load and no new lease from now on.
        _disposed = true;
        _idleCheck?.Dispose();

        var waited = Stopwatch.StartNew();

        if (!_gate.Wait(ShutdownWait))
        {
            _log.Write("the model was still loading at shutdown: left to the process exit");
            return;
        }

        try
        {
            TimeSpan left = ShutdownWait - waited.Elapsed;

            if (!SpinWait.SpinUntil(() => Volatile.Read(ref _leases) == 0, left > TimeSpan.Zero ? left : TimeSpan.Zero))
            {
                _log.Write("a transcription was still running at shutdown: the model is left to the process exit");
                return;
            }

            _engine?.Dispose();
            _engine = null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
