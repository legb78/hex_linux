using System.Text;
using System.Text.RegularExpressions;
using HexLinux.Transcription;
using Xunit;

namespace HexLinux.Tests.Qa;

/// <summary>
/// The cleaner run on text the engine could plausibly print, at random.
///
/// <para>Every dictation goes through it, so a text it mangles is a text the
/// user has to fix by hand, every time. The unit tests show each rule on the
/// sentence it was written for; these check what must hold for any sentence:
/// no stray spaces, no French space where <c>frenchSpacing</c> is off, the
/// French space everywhere it is on in text that reads as French — and none
/// in English, since HexWin's pull request #68 — and a second pass that
/// changes nothing.</para>
/// </summary>
public class TranscriptCleanerPropertyTests
{
    private const int Runs = 5000;

    /// <summary>Words as the engine spells them, glued marks aside.</summary>
    private static readonly string[] PlainWords =
    [
        "vendredi", "Are", "you", "ready", "ok", "14", "30", "https://exemple.fr/a", "d'accord", "é", "Ça", "3", "1", "x2",
    ];

    /// <summary>The same, plus what the cleaner removes: annotations, notes, invented credits.</summary>
    private static readonly string[] AnyWords =
    [
        .. PlainWords, "[BLANK_AUDIO]", "(musique)", "(rires)", "♪", "Merci", "Abonnez-vous", "«", "»",
    ];

    /// <summary>
    /// <see cref="AnyWords"/> without the English ones: whatever is drawn
    /// from it reads as French, or holds no clue, which counts as French.
    /// </summary>
    private static readonly string[] FrenchOrNeutralWords = [.. AnyWords.Except(["Are", "you", "ready"])];

    private static readonly string[] Marks = ["?", "!", ";", ":", "»", ".", ",", "?!", string.Empty, string.Empty, string.Empty];

    private static readonly string[] Gaps = [" ", " ", " ", "  ", "\n", "\t", "  "];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_output_never_has_stray_spaces_and_a_second_pass_changes_nothing(bool frenchSpacing)
    {
        // A dictation arriving with a double space or a leading blank is
        // pasted as such; a rule that fires again on its own output would
        // mean the text depends on how many times it went through.
        var random = new Random(20260927);

        for (int run = 0; run < Runs; run++)
        {
            string raw = RandomSentence(random, AnyWords, withGaps: true);

            string once = TranscriptCleaner.Clean(raw, frenchSpacing);

            Assert.Equal(once.Trim(), once);
            Assert.DoesNotContain("  ", once, StringComparison.Ordinal);
            Assert.Equal(once, TranscriptCleaner.Clean(once, frenchSpacing));
        }
    }

    [Fact]
    public void With_french_spacing_off_no_space_is_ever_added_before_a_mark()
    {
        // "Are you ready?" dictated in English must stay as the engine wrote
        // it once the user turned the switch off.
        var random = new Random(20260928);
        var spaced = new Regex(@"[\p{L}\p{N}] [?!;:»]");

        for (int run = 0; run < Runs; run++)
        {
            string raw = RandomSentence(random, PlainWords, withGaps: false);

            string cleaned = TranscriptCleaner.Clean(raw, frenchSpacing: false);

            Assert.False(spaced.IsMatch(cleaned), $"{raw} -> {cleaned}");
        }
    }

    [Fact]
    public void With_french_spacing_on_no_mark_is_left_glued_to_the_word_before_it()
    {
        // The switch's promise: "vendredi?" never reaches the document — in
        // text that reads as French, the only kind it now applies to.
        var random = new Random(20260929);
        var glued = new Regex(@"[\p{L}\p{N}][?!;:»](\s|$)");

        for (int run = 0; run < Runs; run++)
        {
            string raw = RandomSentence(random, FrenchOrNeutralWords, withGaps: true);

            string cleaned = TranscriptCleaner.Clean(raw, frenchSpacing: true);

            Assert.False(glued.IsMatch(cleaned), $"{raw} -> {cleaned}");
        }
    }

    [Fact]
    public void With_french_spacing_on_an_english_sentence_is_never_spaced()
    {
        // HexWin's pull request #68: "Are you ready?" stays as the engine
        // wrote it, switch on or not, once the sentence reads as English.
        var random = new Random(20260930);
        var spaced = new Regex(@"[\p{L}\p{N}] [?!;:»]");
        string[] english = ["Are", "you", "ready", "ok", "14", "30", "3", "1", "x2"];

        for (int run = 0; run < Runs; run++)
        {
            string raw = "Are you " + RandomSentence(random, english, withGaps: false);

            string cleaned = TranscriptCleaner.Clean(raw, frenchSpacing: true);

            Assert.False(spaced.IsMatch(cleaned), $"{raw} -> {cleaned}");
        }
    }

    [Theory]
    [InlineData("Abonnez-vous à la lettre d'information.")]
    [InlineData("Thanks for watching the kids yesterday.")]
    public void A_sentence_that_merely_contains_a_credit_phrase_is_kept_whole(string dictated)
    {
        // QA-12: the credits filter targets the closing line a subtitle-trained
        // model invents on silence, but it matches the words anywhere, so a
        // real sentence that starts with them loses them: "Thanks for
        // watching the kids yesterday." is inserted as "the kids yesterday."
        Assert.Equal(dictated, TranscriptCleaner.Clean(dictated, frenchSpacing: false));
    }

    /// <summary>
    /// Words with a punctuation mark glued after some of them, as the engine
    /// writes, joined by ordinary spaces or, <paramref name="withGaps"/>, by
    /// the odd whitespace a segment join can leave.
    /// </summary>
    private static string RandomSentence(Random random, string[] words, bool withGaps)
    {
        var builder = new StringBuilder();

        for (int i = random.Next(0, 12); i > 0; i--)
        {
            builder.Append(words[random.Next(words.Length)]);
            builder.Append(Marks[random.Next(Marks.Length)]);
            builder.Append(withGaps ? Gaps[random.Next(Gaps.Length)] : " ");
        }

        return builder.ToString();
    }
}
