namespace HexLinux.Audio;

/// <summary>
/// How much the microphone still hands over once the key is released, so that
/// the last word of a dictation is not cut.
///
/// <para>Two losses to avoid, as in HexWin. People release the key as they
/// say the last syllable, not after it: the recording therefore goes on for
/// <see cref="Tail"/> past the release. And the audio heard just before the
/// release has not reached HexLinux yet: it sits in the sound server's buffer,
/// behind the read in progress. HexWin waits for the buffer the device was
/// filling; libpulse-simple says how far behind the reads are instead
/// (<c>pa_simple_get_latency</c>, the record latency), so that much is read
/// too, on top of the tail.</para>
///
/// <para>The backlog counted is capped at <see cref="MaximumBacklog"/>, HexWin's
/// flush timeout: a server reporting a long latency must not keep a stop
/// waiting that long. Such backlogs happen — under WSLg, the first recording
/// after an idle spell once came back with 5.7 s of audio for 3 s asked. Pure
/// logic.</para>
/// </summary>
public static class RecordingTail
{
    /// <summary>Listening kept up after the key is released: HexWin's value.</summary>
    public static readonly TimeSpan Tail = TimeSpan.FromMilliseconds(100);

    /// <summary>Most of the server's backlog that a stop waits for.</summary>
    public static readonly TimeSpan MaximumBacklog = TimeSpan.FromMilliseconds(500);

    /// <summary><c>PA_USEC_INVALID</c>, <c>((pa_usec_t) -1)</c> in <c>pulse/timeval.h</c>.</summary>
    public const ulong UnknownLatency = ulong.MaxValue;

    /// <summary>
    /// The bytes still to read once a stop is asked for, a whole number of
    /// samples.
    /// </summary>
    /// <param name="latencyMicroseconds">
    /// What the server reported, or <see cref="UnknownLatency"/> when it could
    /// not say: the tail alone is read then.
    /// </param>
    public static long BytesAfterStop(ulong latencyMicroseconds)
    {
        TimeSpan backlog = latencyMicroseconds == UnknownLatency
            ? TimeSpan.Zero
            : TimeSpan.FromMicroseconds(Math.Min(latencyMicroseconds, (ulong)MaximumBacklog.TotalMicroseconds));

        return RecordingFormat.BytesFor(Tail + backlog);
    }
}
