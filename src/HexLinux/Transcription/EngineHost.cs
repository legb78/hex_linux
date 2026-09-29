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
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Shell: loads a native library and drives a timer. The decision to release lives in IdlePolicy, which is tested.")]
public sealed class EngineHost : IDisposable
{
    private readonly string _modelPath;
    private readonly string _provider;
    private readonly int _threads;
    private readonly bool _frenchSpacing;
    private readonly IdlePolicy _policy;
    private readonly SessionLog _log;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer? _idleCheck;

    private ParakeetEngine? _engine;
    private DateTime _lastUse = DateTime.UtcNow;
    private volatile bool _busy;
    private bool _disposed;

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
        if (_engine is not null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await GetAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or DllNotFoundException or ObjectDisposedException)
            {
                // The transcription waiting for it reports the failure.
                _log.Write($"preload failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Returns the engine, loading it if needed. Several simultaneous calls
    /// cause only one load.
    /// </summary>
    public async Task<ParakeetEngine> GetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
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
        finally
        {
            _gate.Release();
        }
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

    private void ReleaseIfIdle()
    {
        if (_engine is null || !_policy.ShouldUnload(DateTime.UtcNow - _lastUse, _busy))
        {
            return;
        }

        if (!_gate.Wait(TimeSpan.Zero))
        {
            // A load or a transcription is under way: try again on the next
            // pass rather than wait and block.
            return;
        }

        try
        {
            // Checked again under the lock: the state may have changed in
            // between.
            if (_engine is not null && _policy.ShouldUnload(DateTime.UtcNow - _lastUse, _busy))
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _idleCheck?.Dispose();
        _engine?.Dispose();
        _engine = null;
        _gate.Dispose();
    }
}
