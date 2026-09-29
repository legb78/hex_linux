namespace HexLinux.Daemon;

/// <summary>What the application is doing at a given moment.</summary>
public enum DictationState
{
    /// <summary>The model is loading. The shortcut does not respond yet.</summary>
    Loading,

    /// <summary>Ready, waiting for the shortcut.</summary>
    Idle,

    /// <summary>The microphone is running.</summary>
    Recording,

    /// <summary>The engine is working on the recording.</summary>
    Transcribing,

    /// <summary>The model could not be loaded: the application is unusable.</summary>
    Failed,
}

/// <summary>
/// Decides what is allowed given the current state.
///
/// Pure logic, pulled out from the rest for the same reason as
/// <see cref="Input.ChordDetector"/>: the situations to cover are races
/// between the user and the machine, awkward to provoke by hand but trivial to
/// describe in a test.
///
/// The case that matters: pressing the shortcut again while a transcription is
/// running. With no guard, two dictations would tread on each other and the
/// text would arrive out of order.
///
/// <para><b>Each dictation carries a generation number</b>, and that is what
/// makes a cancellation final. Cancelling returns to <see cref="DictationState.Idle"/>
/// at once, so a new dictation may start while segments of the cancelled one
/// are still being transcribed. Without the number, those segments would
/// still be inserted, and the late completion of the cancelled dictation would
/// end the new one halfway through, microphone left open — a latent race of
/// the Windows version, made likely here by a cancel command that can arrive
/// during a transcription.</para>
/// </summary>
public sealed class DictationCoordinator
{
    /// <summary>The generation of the last dictation that was cancelled, or 0.</summary>
    private int _cancelledGeneration;

    public DictationState State { get; private set; } = DictationState.Loading;

    /// <summary>
    /// Number of the current, or last, dictation. Taken by everything that
    /// belongs to it — its segments, its completion — so that they can be
    /// recognised later.
    /// </summary>
    public int Generation { get; private set; }

    /// <summary>Raised on every change, so the icon can follow.</summary>
    public event EventHandler<DictationState>? StateChanged;

    /// <summary>The model is loaded: the shortcut becomes live.</summary>
    public void MarkReady() => MoveTo(DictationState.Idle);

    /// <summary>The model could not be loaded.</summary>
    public void MarkFailed() => MoveTo(DictationState.Failed);

    /// <summary>
    /// Tries to start a recording. Returns false if the state does not allow
    /// it — model not loaded, or a transcription still running. A dictation
    /// that starts takes the next generation number.
    /// </summary>
    public bool TryStartRecording()
    {
        if (State != DictationState.Idle)
        {
            return false;
        }

        Generation++;
        MoveTo(DictationState.Recording);
        return true;
    }

    /// <summary>
    /// Whether text from dictation <paramref name="generation"/> may still be
    /// inserted: it must be the current dictation, and not a cancelled one.
    /// </summary>
    public bool MayInsert(int generation) => generation == Generation && generation != _cancelledGeneration;

    /// <summary>
    /// Tries to move on to transcription. Returns false if no recording was
    /// running: a release can arrive with no matching start, after a reset of
    /// the shortcut for instance.
    /// </summary>
    public bool TryStartTranscribing()
    {
        if (State != DictationState.Recording)
        {
            return false;
        }

        MoveTo(DictationState.Transcribing);
        return true;
    }

    /// <summary>The current dictation is over, successful or not: back to waiting.</summary>
    public void Complete() => Complete(Generation);

    /// <summary>
    /// Dictation <paramref name="generation"/> is over. Ignored when it is no
    /// longer the current one: a cancelled dictation finishing late must not
    /// end the dictation that started after it.
    /// </summary>
    public void Complete(int generation)
    {
        if (generation != Generation)
        {
            return;
        }

        if (State is DictationState.Recording or DictationState.Transcribing)
        {
            MoveTo(DictationState.Idle);
        }
    }

    /// <summary>
    /// Abandon: a foreign key, a locked session, a cancel command. Returns
    /// true if a dictation really was running and so must be interrupted.
    /// Nothing more of it is inserted from then on, even what is still being
    /// transcribed: that is what the user asked for.
    /// </summary>
    public bool Cancel()
    {
        bool wasBusy = State is DictationState.Recording or DictationState.Transcribing;

        if (wasBusy)
        {
            _cancelledGeneration = Generation;
            MoveTo(DictationState.Idle);
        }

        return wasBusy;
    }

    private void MoveTo(DictationState next)
    {
        if (State == next)
        {
            return;
        }

        State = next;
        StateChanged?.Invoke(this, next);
    }
}
