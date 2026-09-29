using System.Diagnostics.CodeAnalysis;
using HexLinux.Diagnostics;
using HexLinux.Interop;

namespace HexLinux.Feedback;

/// <summary>
/// Plays the two short tones marking the boundaries of a recording.
///
/// <para>Rendered once, when the daemon starts, rather than on each dictation:
/// the start tone has to sound the moment the dictation is confirmed. Each
/// tone is played on a background task through its own libpulse-simple
/// playback stream, so that neither opening the stream nor waiting for it to
/// drain ever holds the daemon's loop.</para>
///
/// <para><b>No audio failure may cost a dictation.</b> A machine with no
/// output device, or no sound server, simply gets no tone: the failure is
/// logged once and dictation carries on.</para>
/// </summary>
[ExcludeFromCodeCoverage(Justification = "libpulse shell: needs a real output device, verified by --test-feedback. The waveform is ToneSynthesizer, which is tested.")]
public sealed class CueTones
{
    private readonly byte[] _start = ToneSynthesizer.Render(ToneSynthesizer.StartFrequency);
    private readonly byte[] _end = ToneSynthesizer.Render(ToneSynthesizer.EndFrequency);
    private readonly SessionLog _log;

    private int _failureLogged;

    public CueTones(SessionLog log)
    {
        ArgumentNullException.ThrowIfNull(log);

        _log = log;
    }

    /// <summary>Starts the tone and returns at once.</summary>
    public void Play(CueTone tone) => _ = PlayAsync(tone);

    /// <summary>
    /// Plays the tone and completes once it has been heard; never throws.
    /// The result is null when the tone played, else why it could not —
    /// which the daemon ignores and <c>--test-feedback</c> reports.
    /// </summary>
    public Task<string?> PlayAsync(CueTone tone)
    {
        byte[]? pcm = tone switch
        {
            CueTone.Start => _start,
            CueTone.End => _end,
            _ => null,
        };

        return pcm is null ? Task.FromResult<string?>(null) : Task.Run(() => PlayNow(pcm));
    }

    private unsafe string? PlayNow(byte[] pcm)
    {
        nint stream = 0;

        try
        {
            stream = PulseSimple.Open(PulseSimple.StreamPlayback, ToneSynthesizer.SampleRate, "Cue", 0, (uint)pcm.Length);

            int error;

            fixed (byte* data = pcm)
            {
                if (PulseSimple.Write(stream, data, (nuint)pcm.Length, out error) < 0)
                {
                    throw new InvalidOperationException($"playback failed: {PulseSimple.Describe(error)}");
                }
            }

            PulseSimple.Drain(stream, out _);
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            ReportOnce(ex);
            return ex.Message;
        }
        finally
        {
            if (stream != 0)
            {
                PulseSimple.Free(stream);
            }
        }
    }

    /// <summary>
    /// One line, not one per dictation: a machine with no sound card would
    /// otherwise fill the log with the same sentence.
    /// </summary>
    private void ReportOnce(Exception error)
    {
        if (Interlocked.Exchange(ref _failureLogged, 1) == 0)
        {
            _log.Write($"tones unavailable: {error.Message}");
        }
    }
}
