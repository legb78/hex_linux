namespace HexLinux.Feedback;

/// <summary>
/// The two short tones marking the boundaries of a recording, as samples.
///
/// <para>The same waveform as HexWin's: a sine wave of 70 ms, well below full
/// scale — a cue, not an alarm — higher going in (880 Hz) than coming out
/// (587 Hz), so the direction is heard without having to be learnt.</para>
///
/// <para>Each tone fades in and out over a few milliseconds. A sine cut off
/// squarely ends on a step in the waveform, heard as a click — on a tone this
/// short, louder than the tone itself.</para>
///
/// <para>Pure, pulled out of the player so that the shape can be tested: the
/// player itself needs a sound server.</para>
/// </summary>
public static class ToneSynthesizer
{
    public const int SampleRate = 44_100;
    public const int ToneMilliseconds = 70;
    public const int FadeMilliseconds = 5;

    /// <summary>Well below full scale: a cue, not an alarm.</summary>
    public const double Amplitude = 0.22;

    public const double StartFrequency = 880;
    public const double EndFrequency = 587;

    /// <summary>Samples in one tone.</summary>
    public const int SampleCount = SampleRate * ToneMilliseconds / 1000;

    /// <summary>The tone for <paramref name="tone"/>, or null when there is none to play.</summary>
    public static byte[]? For(CueTone tone) => tone switch
    {
        CueTone.Start => Render(StartFrequency),
        CueTone.End => Render(EndFrequency),
        _ => null,
    };

    /// <summary>One tone as signed 16-bit little-endian mono samples.</summary>
    public static byte[] Render(double frequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);

        const int fade = SampleRate * FadeMilliseconds / 1000;

        byte[] pcm = new byte[SampleCount * sizeof(short)];

        for (int index = 0; index < SampleCount; index++)
        {
            double envelope = Math.Min(1.0, Math.Min(index, SampleCount - 1 - index) / (double)fade);
            double value = Amplitude * envelope * Math.Sin(2 * Math.PI * frequency * index / SampleRate);

            short sample = (short)(value * short.MaxValue);

            pcm[index * 2] = (byte)(sample & 0xFF);
            pcm[(index * 2) + 1] = (byte)((sample >> 8) & 0xFF);
        }

        return pcm;
    }
}
