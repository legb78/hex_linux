namespace HexLinux.Audio;

/// <summary>
/// Lends a speech detector to each recording, and keeps one between them.
///
/// <para><b>Why not a single detector, as in HexWin.</b> A detector keeps
/// state from one block to the next, and Silero's is a native object that is
/// not safe to share between threads. On Linux each recording has a capture
/// thread of its own, and a recording being abandoned may still read its last
/// block while the next one has already started. Handing both the same
/// detector would mix two recordings in one state, and call the native code
/// from two threads at once. So each recording rents one: the kept detector,
/// or a fresh one in the rare case where the previous recording still holds
/// it.</para>
///
/// <para>Loading Silero takes a moment, so the detector a recording gives back
/// is reset and kept for the next; a second one, made during an overlap, is
/// disposed of. Thread-safe, pure: what a detector is comes from the factory.</para>
/// </summary>
public sealed class DetectorPool : IDisposable
{
    private readonly Func<ISpeechDetector> _create;
    private readonly Lock _sync = new();

    private ISpeechDetector? _idle;
    private bool _disposed;

    /// <param name="create">
    /// Makes a detector. Called on a capture thread: it must not throw, since
    /// nothing there could report it — fall back to a
    /// <see cref="LevelSpeechDetector"/> instead.
    /// </param>
    /// <param name="first">A detector already made, kept for the first recording.</param>
    public DetectorPool(Func<ISpeechDetector> create, ISpeechDetector? first = null)
    {
        ArgumentNullException.ThrowIfNull(create);

        _create = create;
        _idle = first;
    }

    /// <summary>The level threshold alone: what a recording uses when no model is installed.</summary>
    public static DetectorPool Level() => new(() => new LevelSpeechDetector());

    /// <summary>A detector for one recording, to be given back through <see cref="Return"/>.</summary>
    public ISpeechDetector Rent()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_idle is { } idle)
            {
                _idle = null;
                return idle;
            }
        }

        return _create();
    }

    /// <summary>
    /// A recording is over. Its detector is reset and kept, unless one is
    /// kept already or the pool is gone, in which case it is disposed of.
    /// </summary>
    public void Return(ISpeechDetector detector)
    {
        ArgumentNullException.ThrowIfNull(detector);

        lock (_sync)
        {
            if (!_disposed && _idle is null)
            {
                detector.Reset();
                _idle = detector;
                return;
            }
        }

        (detector as IDisposable)?.Dispose();
    }

    /// <summary>Disposes of the kept detector; one still rented goes when it is returned.</summary>
    public void Dispose()
    {
        ISpeechDetector? idle;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            idle = _idle;
            _idle = null;
        }

        (idle as IDisposable)?.Dispose();
    }
}
