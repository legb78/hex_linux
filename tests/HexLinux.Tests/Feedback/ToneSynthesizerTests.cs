using System.Buffers.Binary;
using HexLinux.Feedback;
using Xunit;

namespace HexLinux.Tests.Feedback;

/// <summary>
/// The tones are the only thing HexLinux says aloud, and they sound during
/// dictation, next to the user's own voice: short, quiet, no click, and
/// higher going in than coming out — HexWin's cue, sample for sample. The
/// player needs a sound server; the waveform does not, so its shape is
/// checked here.
/// </summary>
public class ToneSynthesizerTests
{
    private static short[] Samples(byte[] pcm)
    {
        short[] samples = new short[pcm.Length / sizeof(short)];

        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * sizeof(short)));
        }

        return samples;
    }

    /// <summary>The frequency the sign changes of the signal give.</summary>
    private static double MeasuredFrequency(short[] samples)
    {
        int crossings = 0;
        short previous = 0;

        foreach (short sample in samples)
        {
            if (sample == 0)
            {
                continue;
            }

            if (previous != 0 && (sample > 0) != (previous > 0))
            {
                crossings++;
            }

            previous = sample;
        }

        double seconds = samples.Length / (double)ToneSynthesizer.SampleRate;
        return crossings / (2 * seconds);
    }

    [Fact]
    public void The_tones_are_HexWins()
    {
        // Brief, section 6.4: same synthesis as HexWin's CueTones, so that
        // somebody using both machines hears the same cue.
        Assert.Equal(44_100, ToneSynthesizer.SampleRate);
        Assert.Equal(70, ToneSynthesizer.ToneMilliseconds);
        Assert.Equal(5, ToneSynthesizer.FadeMilliseconds);
        Assert.Equal(0.22, ToneSynthesizer.Amplitude);
        Assert.Equal(880, ToneSynthesizer.StartFrequency);
        Assert.Equal(587, ToneSynthesizer.EndFrequency);
    }

    [Theory]
    [InlineData(ToneSynthesizer.StartFrequency)]
    [InlineData(ToneSynthesizer.EndFrequency)]
    public void A_tone_lasts_seventy_milliseconds_of_sixteen_bit_mono(double frequency)
    {
        // Long enough to be heard, short enough not to cover the first word:
        // 3087 samples of two bytes at 44.1 kHz.
        byte[] pcm = ToneSynthesizer.Render(frequency);

        Assert.Equal(ToneSynthesizer.SampleCount * sizeof(short), pcm.Length);
        Assert.Equal(3_087, ToneSynthesizer.SampleCount);
        Assert.Equal(70.0, pcm.Length / (double)sizeof(short) / ToneSynthesizer.SampleRate * 1000, 0);
    }

    [Theory]
    [InlineData(ToneSynthesizer.StartFrequency)]
    [InlineData(ToneSynthesizer.EndFrequency)]
    public void A_tone_starts_and_ends_on_silence(double frequency)
    {
        // A sine cut off squarely ends on a step in the waveform, heard as a
        // click — on a tone this short, louder than the tone itself.
        short[] samples = Samples(ToneSynthesizer.Render(frequency));

        Assert.Equal(0, samples[0]);
        Assert.Equal(0, samples[^1]);
    }

    [Theory]
    [InlineData(ToneSynthesizer.StartFrequency)]
    [InlineData(ToneSynthesizer.EndFrequency)]
    public void A_tone_rises_and_falls_within_the_fade(double frequency)
    {
        // Over the first and last 5 ms, no sample may exceed the envelope:
        // that ramp is what removes the click.
        short[] samples = Samples(ToneSynthesizer.Render(frequency));
        const int fade = ToneSynthesizer.SampleRate * ToneSynthesizer.FadeMilliseconds / 1000;
        double peak = ToneSynthesizer.Amplitude * short.MaxValue;

        for (int i = 0; i < fade; i++)
        {
            double ceiling = peak * i / fade;

            Assert.True(Math.Abs(samples[i]) <= ceiling, $"sample {i} = {samples[i]} is above the fade-in ({ceiling:F1})");
            Assert.True(Math.Abs(samples[^(i + 1)]) <= ceiling, $"sample -{i + 1} = {samples[^(i + 1)]} is above the fade-out ({ceiling:F1})");
        }
    }

    [Theory]
    [InlineData(ToneSynthesizer.StartFrequency)]
    [InlineData(ToneSynthesizer.EndFrequency)]
    public void A_tone_stays_well_below_full_scale(double frequency)
    {
        // A cue, not an alarm: about a fifth of full scale, reached in the
        // middle of the tone and never exceeded.
        short[] samples = Samples(ToneSynthesizer.Render(frequency));
        int loudest = samples.Max(sample => Math.Abs((int)sample));

        Assert.InRange(loudest, 7_100, (int)(ToneSynthesizer.Amplitude * short.MaxValue));
    }

    [Theory]
    [InlineData(ToneSynthesizer.StartFrequency)]
    [InlineData(ToneSynthesizer.EndFrequency)]
    public void A_tone_is_a_sine_at_its_frequency(double frequency)
    {
        // Measured from the signal itself, not from the constant: a mistake in
        // the phase formula would change the pitch the user hears.
        short[] samples = Samples(ToneSynthesizer.Render(frequency));

        Assert.InRange(MeasuredFrequency(samples), frequency - 15, frequency + 15);
    }

    [Fact]
    public void The_start_tone_is_higher_than_the_end_tone()
    {
        // Higher going in, lower coming out: the direction is heard without
        // having to be learnt, when the two tones are a second apart.
        double start = MeasuredFrequency(Samples(ToneSynthesizer.For(CueTone.Start)!));
        double end = MeasuredFrequency(Samples(ToneSynthesizer.For(CueTone.End)!));

        Assert.True(start > end * 1.4, $"start {start:F0} Hz, end {end:F0} Hz");
    }

    [Theory]
    [InlineData(ToneSynthesizer.StartFrequency)]
    [InlineData(ToneSynthesizer.EndFrequency)]
    public void The_samples_are_little_endian(double frequency)
    {
        // libpulse is opened for S16LE. Read with that byte order the signal
        // must be smooth — a sine moves by at most its angular step per
        // sample, plus what the fade adds; bytes in the wrong order would
        // read as noise, with steps in the thousands.
        short[] samples = Samples(ToneSynthesizer.Render(frequency));
        const int fade = ToneSynthesizer.SampleRate * ToneSynthesizer.FadeMilliseconds / 1000;
        double largestStep = ((2 * Math.PI * frequency / ToneSynthesizer.SampleRate) + (1.0 / fade)) * ToneSynthesizer.Amplitude * short.MaxValue;

        for (int i = 1; i < samples.Length; i++)
        {
            Assert.True(Math.Abs(samples[i] - samples[i - 1]) <= largestStep + 1, $"step of {samples[i] - samples[i - 1]} at sample {i}");
        }

        Assert.Contains(samples, sample => sample < 0);
    }

    [Fact]
    public void Each_cue_has_its_own_tone_and_none_has_none()
    {
        // CueTones renders both once at start-up and plays None as nothing.
        Assert.Equal(ToneSynthesizer.Render(ToneSynthesizer.StartFrequency), ToneSynthesizer.For(CueTone.Start));
        Assert.Equal(ToneSynthesizer.Render(ToneSynthesizer.EndFrequency), ToneSynthesizer.For(CueTone.End));
        Assert.Null(ToneSynthesizer.For(CueTone.None));
        Assert.Null(ToneSynthesizer.For((CueTone)42));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-440.0)]
    public void A_frequency_that_is_not_positive_is_refused(double frequency)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ToneSynthesizer.Render(frequency));
    }
}
