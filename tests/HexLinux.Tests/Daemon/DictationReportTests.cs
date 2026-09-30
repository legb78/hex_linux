using System.Globalization;
using HexLinux.Audio;
using HexLinux.Daemon;
using Xunit;

namespace HexLinux.Tests.Daemon;

/// <summary>
/// The log line of each dictation. It is what a bug report shows, so it
/// carries the numbers that tell faults apart — a deaf microphone from an
/// engine that recognises nothing — and never the text itself: only its
/// length reaches this code.
/// </summary>
public class DictationReportTests
{
    [Fact]
    public void A_dictation_is_described_by_its_numbers()
    {
        Assert.Equal(
            "3.2 s dictated, level 41.5 %, transcribed in 0.84 s, 57 characters",
            DictationReport.Describe(TimeSpan.FromSeconds(3.2), 1, 0.415, TimeSpan.FromMilliseconds(840), 57));
    }

    [Fact]
    public void A_later_segment_says_which_one_it_is()
    {
        // Sentence-by-sentence dictation logs one line per segment: the
        // number tells a slow segment from a slow dictation.
        Assert.Equal(
            "1.5 s dictated (segment 3), level 12.0 %, transcribed in 0.30 s, 20 characters",
            DictationReport.Describe(TimeSpan.FromSeconds(1.5), 3, 0.12, TimeSpan.FromMilliseconds(300), 20));
    }

    [Fact]
    public void The_line_does_not_change_shape_with_the_locale()
    {
        // A French locale writes 3,2: scripts reading the log, and whoever
        // compares two logs, would be thrown by it.
        CultureInfo original = CultureInfo.CurrentCulture;
        var comma = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        comma.NumberFormat.NumberDecimalSeparator = ",";

        try
        {
            CultureInfo.CurrentCulture = comma;

            Assert.Equal(
                "3.2 s dictated, level 41.5 %, transcribed in 0.84 s, 57 characters",
                DictationReport.Describe(TimeSpan.FromSeconds(3.2), 1, 0.415, TimeSpan.FromMilliseconds(840), 57));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void A_dictation_that_gave_text_needs_no_explanation()
    {
        Assert.Null(DictationReport.WhyNothing(12, 0.0));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.005)]
    public void No_text_from_a_silent_microphone_blames_the_microphone(double peak)
    {
        // A muted microphone, or the wrong input source picked in the sound
        // settings: nothing to do with the model.
        Assert.Equal("  -> nothing inserted: the microphone captured no sound", DictationReport.WhyNothing(0, peak));
    }

    [Theory]
    [InlineData(AudioLevel.SilenceThreshold)]
    [InlineData(0.3)]
    public void No_text_despite_sound_says_no_speech_was_recognised(double peak)
    {
        // A cough, a key press, or speech in a language the model does not
        // know: the microphone works, so it is not the thing to check.
        Assert.Equal(
            "  -> nothing inserted: sound was captured but no speech was recognised",
            DictationReport.WhyNothing(0, peak));
    }
}
