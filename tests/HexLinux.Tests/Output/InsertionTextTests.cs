using HexLinux.Output;
using Xunit;

namespace HexLinux.Tests.Output;

/// <summary>
/// E13: the last pass before text reaches another application. The target
/// can be a terminal running a root shell: an escape character or a C1
/// control would reach it as the start of a command sequence, and a
/// bidirectional override would make the text read differently from what it
/// is ("Trojan Source"). Everything else — accents, French typography's
/// narrow no-break space, emoji built with joiners — must come through
/// untouched, since it is what the user said.
/// </summary>
public class InsertionTextTests
{
    [Fact]
    public void Ordinary_text_is_left_as_it_is()
    {
        const string sentence = "Bonjour Marie, à demain ! Ça coûte 12,50 € — « d'accord ».";

        Assert.Equal(sentence, InsertionText.Sanitize(sentence));
    }

    [Theory]
    [InlineData("naïve café, Größe, año")]
    [InlineData("東京で会いましょう")]
    [InlineData("مرحبا بالعالم")]
    [InlineData("👨‍👩‍👧 family, 👍🏽 ok")]
    public void Letters_of_every_script_and_joined_emoji_come_through(string text)
    {
        // U+200D, the zero-width joiner, is a format character, not a
        // control: removing it would split a family emoji into three people.
        Assert.Equal(text, InsertionText.Sanitize(text));
    }

    [Fact]
    public void The_narrow_no_break_space_next_to_the_override_range_is_kept()
    {
        // U+202F sits right after the removed U+202A-U+202E: French
        // typography puts it before "?" and "!".
        const string text = "On y va ? Oui !";

        Assert.Equal(text, InsertionText.Sanitize(text));
    }

    [Fact]
    public void An_escape_sequence_loses_its_escape_character()
    {
        // ESC [ ... m in a terminal changes colours; other sequences move the
        // cursor or rewrite the title. Without ESC it is harmless text.
        Assert.Equal("[31mred[0m", InsertionText.Sanitize("\u001b[31mred\u001b[0m"));
    }

    [Fact]
    public void A_terminal_clipboard_write_is_defused()
    {
        // OSC 52 asks the terminal to set the clipboard: ESC ] 52 ; ... BEL.
        string sanitized = InsertionText.Sanitize("\u001b]52;c;ZWNobyBoaQ==\u0007done");

        Assert.Equal("]52;c;ZWNobyBoaQ==done", sanitized);
    }

    [Theory]
    [InlineData('\u0000')] // NUL
    [InlineData('\u0003')] // ETX, Ctrl+C in a terminal
    [InlineData('\u0007')] // BEL
    [InlineData('\u0008')] // BS, could erase what precedes on screen
    [InlineData('\u001B')] // ESC
    [InlineData('\u007F')] // DEL
    [InlineData('\u0080')] // first of C1
    [InlineData('\u009B')] // CSI, the one-character C1 form of ESC [
    [InlineData('\u009F')] // APC, last of C1
    public void Control_characters_are_removed(char control)
    {
        string sanitized = InsertionText.Sanitize($"rm{control}file");

        Assert.Equal("rmfile", sanitized);
    }

    [Theory]
    [InlineData('\u0009')] // TAB, completes a command in a shell
    [InlineData('\u000A')] // LF, Enter in a terminal
    [InlineData('\u000B')] // VT
    [InlineData('\u000C')] // FF
    [InlineData('\u000D')] // CR, Enter as well
    [InlineData('\u0085')] // NEL, a C1 line break
    public void Whitespace_controls_do_not_reach_the_target_either(char control)
    {
        string sanitized = InsertionText.Sanitize($"ls{control}-la");

        Assert.DoesNotContain(new string(control, 1), sanitized, StringComparison.Ordinal);
        Assert.StartsWith("ls", sanitized, StringComparison.Ordinal);
        Assert.EndsWith("-la", sanitized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData('\u202A')] // LRE
    [InlineData('\u202B')] // RLE
    [InlineData('\u202C')] // PDF
    [InlineData('\u202D')] // LRO
    [InlineData('\u202E')] // RLO
    [InlineData('\u2066')] // LRI
    [InlineData('\u2067')] // RLI
    [InlineData('\u2068')] // FSI
    [InlineData('\u2069')] // PDI
    public void Bidirectional_embeddings_overrides_and_isolates_are_removed(char bidi)
    {
        Assert.Equal("abc", InsertionText.Sanitize($"a{bidi}b{bidi}c"));
    }

    [Fact]
    public void A_Trojan_Source_line_reads_as_it_really_is()
    {
        // The CVE-2021-42574 pattern: an override hides part of the text
        // inside what looks like a comment.
        string sanitized = InsertionText.Sanitize("access = \"user\u202E \u2066// admin\u2069 \u2066\"");

        Assert.Equal("access = \"user // admin \"", sanitized);
    }

    [Fact]
    public void No_line_break_ever_reaches_the_target()
    {
        // A line break typed into a terminal is Enter: the command line would
        // run before the user had read it.
        string sanitized = InsertionText.Sanitize("first line\r\nsecond line\nthird\u0085line");

        Assert.DoesNotContain("\n", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("\u0085", sanitized, StringComparison.Ordinal);
        Assert.StartsWith("first line", sanitized, StringComparison.Ordinal);
        Assert.EndsWith("line", sanitized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("first line\nsecond line", "first line second line")]
    [InlineData("first line\r\nsecond line", "first line second line")]
    [InlineData("end of line \n\n  start of the next", "end of line start of the next")]
    [InlineData("name\tvalue", "name value")]
    [InlineData("one\u0085two", "one two")]
    public void A_line_break_becomes_one_space_between_the_words(string text, string expected)
    {
        // `hexlinux --inject -` reads a text of several lines from standard
        // input: deleting the breaks glued "line" to "second". The engine's
        // own output never has any (the cleaner folds whitespace first).
        Assert.Equal(expected, InsertionText.Sanitize(text));
    }

    [Fact]
    public void Removal_happens_everywhere_in_a_long_text()
    {
        string text = string.Concat(Enumerable.Repeat("a\u001b\u202E", 1000));

        Assert.Equal(new string('a', 1000), InsertionText.Sanitize(text));
    }

    [Fact]
    public void Controls_next_to_emoji_do_not_break_the_surrogate_pairs()
    {
        Assert.Equal("🎹🎹", InsertionText.Sanitize("🎹\u0007🎹"));
    }

    [Fact]
    public void The_result_is_trimmed_once_the_controls_are_gone()
    {
        // An override or a line break at the edges would otherwise leave a
        // stray space at the start of the insertion.
        Assert.Equal("hello", InsertionText.Sanitize("\u202E  hello \n"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u001b\u0007\u202E\u2069\r\n")]
    public void Nothing_printable_gives_an_empty_string(string text)
    {
        Assert.Equal(string.Empty, InsertionText.Sanitize(text));
    }

    [Fact]
    public void Missing_text_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => InsertionText.Sanitize(null!));
        Assert.Throws<ArgumentNullException>(() => InsertionText.ForSegment(null!, 1));
    }

    // --- Segments -----------------------------------------------------------------

    [Fact]
    public void The_first_segment_is_inserted_as_it_is()
    {
        Assert.Equal("Hello there.", InsertionText.ForSegment("Hello there.", 1));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    public void A_later_segment_is_separated_from_the_previous_one_by_a_space(int ordinal)
    {
        // Sentence-by-sentence insertion: without the space the second
        // sentence would be glued to the first one's full stop.
        Assert.Equal(" How are you?", InsertionText.ForSegment("How are you?", ordinal));
    }

    [Fact]
    public void A_segment_is_sanitized_before_the_space_is_added()
    {
        Assert.Equal(" Next.", InsertionText.ForSegment("\u202E Next.\n", 2));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void A_segment_with_nothing_printable_inserts_nothing_not_even_a_space(int ordinal)
    {
        // A pause transcribed as noise must not leave a lone space behind.
        Assert.Equal(string.Empty, InsertionText.ForSegment("\u0007 \u202E", ordinal));
    }
}
