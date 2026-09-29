namespace HexLinux.Input;

/// <summary>
/// Decides when a held shortcut becomes a dictation.
///
/// <para><b>Why a press is not enough on Linux.</b> The keys of the shortcut
/// are not withheld from the desktop, so Right Ctrl keeps its ordinary uses:
/// Right Ctrl+C is still a copy. Starting the dictation on the press would
/// open the microphone and play two tones every time the key serves a
/// shortcut — the start, then the end when the letter cancels it. HexWin never
/// had the problem, its shortcut being swallowed.</para>
///
/// <para>So the press only <i>arms</i>. The microphone opens at once — nothing
/// said in the first instant is lost — but the dictation is confirmed, with
/// its tone and its state, only once the shortcut has been held for the
/// minimum recording time without another key. A release or a foreign key
/// before that disarms in silence: the shortcut was a shortcut.</para>
///
/// <para>Pure: the clock is passed in, as a monotonic offset, so every timing
/// can be stated in a test without waiting.</para>
/// </summary>
public sealed class StartConfirmation
{
    private TimeSpan _armedAt;
    private int _arming;

    /// <param name="delay">
    /// How long the shortcut must be held; zero confirms at once.
    /// </param>
    public StartConfirmation(TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);

        Delay = delay;
    }

    public TimeSpan Delay { get; }

    /// <summary>True between a press and its confirmation or its abandonment.</summary>
    public bool IsArmed { get; private set; }

    /// <summary>
    /// The shortcut is complete. Returns the number of this arming, which the
    /// deferred confirmation must present: an older one, left over from a
    /// press already abandoned, is refused.
    /// </summary>
    public int Arm(TimeSpan now)
    {
        IsArmed = true;
        _armedAt = now;

        return ++_arming;
    }

    /// <summary>How long is left before <see cref="TryConfirm"/> can succeed.</summary>
    public TimeSpan Remaining(TimeSpan now)
    {
        TimeSpan left = Delay - (now - _armedAt);

        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>
    /// The shortcut was released or interrupted. Returns true when it was
    /// still armed — a shortcut that never became a dictation, to be dropped
    /// without a sound.
    /// </summary>
    public bool Disarm()
    {
        bool wasArmed = IsArmed;
        IsArmed = false;

        return wasArmed;
    }

    /// <summary>
    /// Confirms arming <paramref name="arming"/> when it is still the current
    /// one and has lasted long enough. Confirms only once.
    /// </summary>
    public bool TryConfirm(int arming, TimeSpan now)
    {
        if (!IsArmed || arming != _arming || now - _armedAt < Delay)
        {
            return false;
        }

        IsArmed = false;
        return true;
    }
}
