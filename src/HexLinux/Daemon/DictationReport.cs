using System.Globalization;
using HexLinux.Audio;

namespace HexLinux.Daemon;

/// <summary>
/// The line the log keeps for each dictation, and what it says when nothing
/// was inserted.
///
/// <para><b>It never receives the text.</b> Only its length: the
/// transcription is the user's, and a log is the kind of file that ends up
/// attached to a bug report. Taking a count rather than the string makes that
/// a property of the signature, not of the caller's care.</para>
///
/// <para>The captured level is logged with every dictation, not only in the
/// diagnostic mode. Without it "zero characters" cannot be diagnosed: there is
/// no telling a microphone that hears nothing from an engine that recognises
/// nothing — two very different faults with the same symptom.</para>
/// </summary>
public static class DictationReport
{
    /// <param name="duration">Length of the audio of this segment.</param>
    /// <param name="ordinal">1 for the first segment of the dictation.</param>
    /// <param name="peak">Captured level, 0 to 1.</param>
    /// <param name="transcription">Time the engine took.</param>
    /// <param name="characters">Length of the text obtained.</param>
    public static string Describe(TimeSpan duration, int ordinal, double peak, TimeSpan transcription, int characters)
    {
        string label = ordinal == 1 ? "dictated" : $"dictated (segment {ordinal})";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{duration.TotalSeconds:F1} s {label}, level {peak * 100:F1} %, "
            + $"transcribed in {transcription.TotalSeconds:F2} s, {characters} characters");
    }

    /// <summary>
    /// Why nothing was inserted, told apart by the level: a silent microphone,
    /// or sound without recognisable speech. Null when there was text.
    /// </summary>
    public static string? WhyNothing(int characters, double peak)
    {
        if (characters > 0)
        {
            return null;
        }

        return peak < AudioLevel.SilenceThreshold
            ? "  -> nothing inserted: the microphone captured no sound"
            : "  -> nothing inserted: sound was captured but no speech was recognised";
    }
}
