using System.Text.RegularExpressions;
using HexLinux.Cli;
using HexLinux.Configuration;
using HexLinux.Daemon;
using HexLinux.Session;
using Xunit;

namespace HexLinux.Tests.Cli;

/// <summary>
/// The command line is how every diagnostic is reached, and how a desktop
/// shortcut drives the daemon. Two rules matter above the rest: a mistyped
/// option must say so instead of starting a second daemon, and a number that
/// cannot be read must not stop a diagnostic, exactly as in HexWin.
/// </summary>
public class CommandLineTests
{
    private static CliRequest Parse(params string[] args) => CommandLine.Parse(args);

    private static void AssertUsageError(CliRequest request, string expectedFragment)
    {
        Assert.Equal(CliMode.Invalid, request.Mode);
        Assert.NotNull(request.Error);
        Assert.Contains(expectedFragment, request.Error, StringComparison.Ordinal);
    }

    // --- Modes ------------------------------------------------------------------

    [Fact]
    public void No_argument_runs_the_daemon()
    {
        // What the autostart entry and the systemd unit run.
        CliRequest request = Parse();

        Assert.Equal(CliMode.Daemon, request.Mode);
        Assert.Null(request.Error);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("--doctor", "--help")]
    [InlineData("--record", "out.wav", "-h")]
    [InlineData("--toogle", "--help")]
    public void Help_wins_wherever_it_appears(params string[] args)
    {
        // Someone unsure of the syntax appends --help to what they typed:
        // they get the help, not an error about the rest of the line.
        Assert.Equal(CliMode.Help, CommandLine.Parse(args).Mode);
    }

    [Theory]
    [InlineData("--watch-hotkey", CliMode.WatchHotkey)]
    [InlineData("--test-feedback", CliMode.TestFeedback)]
    [InlineData("--doctor", CliMode.Doctor)]
    public void A_diagnostic_flag_selects_its_mode(string flag, CliMode expected)
    {
        Assert.Equal(expected, Parse(flag).Mode);
    }

    [Theory]
    [InlineData("--toggle", ControlCommand.Toggle)]
    [InlineData("--start", ControlCommand.Start)]
    [InlineData("--stop", ControlCommand.Stop)]
    [InlineData("--cancel", ControlCommand.Cancel)]
    [InlineData("--status", ControlCommand.Status)]
    public void A_control_flag_names_the_command_sent_to_the_daemon(string flag, ControlCommand expected)
    {
        // "hexlinux --toggle" bound to a desktop shortcut is how one dictates
        // without any keyboard permission: it must reach the socket as Toggle.
        CliRequest request = Parse(flag);

        Assert.Equal(CliMode.Control, request.Mode);
        Assert.Equal(expected, request.Command);
    }

    // --- --record -------------------------------------------------------------------

    [Fact]
    public void Record_takes_the_output_path_and_five_seconds_by_default()
    {
        CliRequest request = Parse("--record", "out.wav");

        Assert.Equal(CliMode.Record, request.Mode);
        Assert.Equal("out.wav", request.Path);
        Assert.Equal(5, request.Seconds);
    }

    [Fact]
    public void Record_reads_the_seconds_option()
    {
        Assert.Equal(12, Parse("--record", "out.wav", "--seconds", "12").Seconds);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("2.5")]
    [InlineData("99999999999")]
    [InlineData("")]
    public void An_unreadable_or_non_positive_number_falls_back_to_its_default(string value)
    {
        // Lenient as in HexWin: a diagnostic run with "--seconds 2.5" still
        // records, for the default time, rather than refusing to start.
        Assert.Equal(CommandLine.DefaultSeconds, Parse("--record", "out.wav", "--seconds", value).Seconds);
        Assert.Equal(CommandLine.DefaultDelay, Parse("--inject", "hello", "--delay", value).Delay);
    }

    [Fact]
    public void A_number_option_left_without_its_value_at_the_end_is_as_good_as_absent()
    {
        CliRequest request = Parse("--record", "out.wav", "--seconds");

        Assert.Equal(CliMode.Record, request.Mode);
        Assert.Equal(CommandLine.DefaultSeconds, request.Seconds);
    }

    [Fact]
    public void Record_with_its_path_forgotten_does_not_record_into_a_file_named_after_an_option()
    {
        // "hexlinux --record --seconds 5": the path was forgotten. Taking
        // "--seconds" as the file name would leave the 5 unexplained, so the
        // line is refused rather than guessed at.
        AssertUsageError(Parse("--record", "--seconds", "5"), "Unknown option: 5");
    }

    [Fact]
    public void Options_may_come_before_the_mode()
    {
        CliRequest request = Parse("--seconds", "3", "--record", "out.wav");

        Assert.Equal(CliMode.Record, request.Mode);
        Assert.Equal(3, request.Seconds);
    }

    [Fact]
    public void A_repeated_option_keeps_its_last_value()
    {
        // A shell alias that sets a delay, completed by the user with another.
        Assert.Equal(7, Parse("--inject", "hello", "--delay", "2", "--delay", "7").Delay);
    }

    // --- --transcribe -----------------------------------------------------------------

    [Fact]
    public void Transcribe_takes_the_file_and_the_optional_model_and_provider()
    {
        // --model is how two model folders are compared without editing
        // settings.json in between.
        CliRequest request = Parse("--transcribe", "voice.wav", "--model", "/tmp/other-model", "--provider", "cpu");

        Assert.Equal(CliMode.Transcribe, request.Mode);
        Assert.Equal("voice.wav", request.Path);
        Assert.Equal("/tmp/other-model", request.Model);
        Assert.Equal("cpu", request.Provider);
    }

    [Fact]
    public void Transcribe_without_options_leaves_the_model_and_provider_to_the_settings()
    {
        CliRequest request = Parse("--transcribe", "voice.wav");

        Assert.Null(request.Model);
        Assert.Null(request.Provider);
    }

    // --- --inject ---------------------------------------------------------------------

    [Fact]
    public void Inject_defaults_to_paste_four_seconds_and_the_sender_of_the_settings()
    {
        CliRequest request = Parse("--inject", "some text");

        Assert.Equal(CliMode.Inject, request.Mode);
        Assert.Equal("some text", request.Text);
        Assert.False(request.TextFromStandardInput);
        Assert.Equal(InsertionMode.Paste, request.InjectMode);
        Assert.Equal(4, request.Delay);

        // Null, not Auto: Program then follows settings.json's keySender.
        Assert.Null(request.Sender);
    }

    [Fact]
    public void Inject_dash_reads_the_text_from_standard_input()
    {
        // E9: a text given in argv is visible to every local user through
        // /proc/<pid>/cmdline; "--inject -" keeps it out of there.
        CliRequest request = Parse("--inject", "-", "--mode", "Type");

        Assert.True(request.TextFromStandardInput);
        Assert.Equal(InsertionMode.Type, request.InjectMode);
    }

    [Fact]
    public void Only_a_lone_dash_means_standard_input()
    {
        // A list item dictated for a test, "- milk", is text like any other.
        Assert.False(Parse("--inject", "- milk").TextFromStandardInput);
    }

    [Theory]
    [InlineData("Type", InsertionMode.Type)]
    [InlineData("type", InsertionMode.Type)]
    [InlineData("TYPE", InsertionMode.Type)]
    [InlineData("Paste", InsertionMode.Paste)]
    [InlineData("paste", InsertionMode.Paste)]
    public void Inject_mode_is_read_regardless_of_case(string value, InsertionMode expected)
    {
        Assert.Equal(expected, Parse("--inject", "hello", "--mode", value).InjectMode);
    }

    [Theory]
    [InlineData("Typo")]
    [InlineData("Types")]
    [InlineData("")]
    public void An_unknown_inject_mode_is_refused_rather_than_read_as_paste(string value)
    {
        // QA-10: Type is what a user chooses to keep the text out of the
        // clipboard; HexWin's fallback to Paste sent "--mode Typo" through
        // the clipboard anyway. Refused like an unknown sender.
        AssertUsageError(Parse("--inject", "hello", "--mode", value), $"Unknown mode: {value}");
    }

    [Theory]
    [InlineData("auto", KeySender.Auto)]
    [InlineData("uinput", KeySender.Uinput)]
    [InlineData("xdotool", KeySender.Xdotool)]
    [InlineData("wtype", KeySender.Wtype)]
    [InlineData("XDOTOOL", KeySender.Xdotool)]
    public void Inject_sender_forces_a_sender(string value, KeySender expected)
    {
        // Forcing is how the automatic choice is checked against each
        // alternative on a desktop where it picks the wrong one.
        Assert.Equal(expected, Parse("--inject", "hello", "--sender", value).Sender);
    }

    [Theory]
    [InlineData("ydotool")]
    [InlineData("xdotol")]
    public void An_unknown_sender_is_a_usage_error(string value)
    {
        // ydotool was removed on purpose: silently using another sender would
        // make the test say something about a tool it never ran.
        AssertUsageError(Parse("--inject", "hello", "--sender", value), $"Unknown sender: {value}");
    }

    [Fact]
    public void Inject_reads_its_delay()
    {
        Assert.Equal(10, Parse("--inject", "hello", "--delay", "10").Delay);
    }

    [Fact]
    public void Inject_with_its_text_forgotten_is_refused()
    {
        // "hexlinux --inject --mode Type": the text was forgotten; "--mode"
        // is taken as the text, and the stray "Type" makes the line an error
        // instead of pasting the word "--mode".
        AssertUsageError(Parse("--inject", "--mode", "Type"), "Unknown option: Type");
    }

    // --- --autostart ----------------------------------------------------------------

    [Theory]
    [InlineData("on", true)]
    [InlineData("off", false)]
    [InlineData("ON", true)]
    [InlineData("Off", false)]
    public void Autostart_takes_on_or_off(string value, bool expected)
    {
        CliRequest request = Parse("--autostart", value);

        Assert.Equal(CliMode.AutoStart, request.Mode);
        Assert.Equal(expected, request.AutoStartOn);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("")]
    public void Autostart_refuses_any_other_word(string value)
    {
        // "--autostart yes" must not be read as off: the user would believe
        // the entry was written.
        AssertUsageError(Parse("--autostart", value), "--autostart takes on or off");
    }

    // --- Usage errors (decision 23) --------------------------------------------------

    [Theory]
    [InlineData("--record")]
    [InlineData("--transcribe")]
    [InlineData("--inject")]
    [InlineData("--autostart")]
    public void A_mode_missing_its_value_is_a_usage_error(string flag)
    {
        AssertUsageError(Parse(flag), $"{flag} needs a value");
    }

    [Theory]
    [InlineData("--toogle")]
    [InlineData("--Doctor")]
    [InlineData("doctor")]
    [InlineData("-d")]
    [InlineData("--record=out.wav")]
    public void An_unknown_option_is_a_usage_error_not_a_second_daemon(string arg)
    {
        // With a daemon already running, a mistyped --toogle bound to a
        // desktop shortcut must say so, not try to start another instance.
        AssertUsageError(Parse(arg), $"Unknown option: {arg}");
    }

    [Fact]
    public void An_unknown_word_after_a_valid_mode_is_still_refused()
    {
        AssertUsageError(Parse("--doctor", "--verbose"), "Unknown option: --verbose");
    }

    [Theory]
    [InlineData("--toggle", "--status")]
    [InlineData("--doctor", "--record", "out.wav")]
    [InlineData("--doctor", "--doctor")]
    public void Two_modes_at_once_are_refused(params string[] args)
    {
        AssertUsageError(CommandLine.Parse(args), "Only one mode at a time");
    }

    [Theory]
    [InlineData("--seconds", "--doctor", "--seconds", "3")]
    [InlineData("--delay", "--record", "out.wav", "--delay", "2")]
    [InlineData("--model", "--inject", "hello", "--model", "m")]
    [InlineData("--sender", "--transcribe", "a.wav", "--sender", "wtype")]
    [InlineData("--mode", "--toggle", "--mode", "Type")]
    public void An_option_of_another_mode_is_refused(string option, params string[] args)
    {
        // "--toggle --mode Type" suggests the toggle could type: it cannot,
        // and saying so beats ignoring the option.
        AssertUsageError(CommandLine.Parse(args), $"{option} does not apply to");
    }

    [Fact]
    public void An_option_without_a_mode_is_refused()
    {
        AssertUsageError(Parse("--seconds", "3"), "No mode was given: --seconds");
    }

    [Fact]
    public void A_null_argument_list_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => CommandLine.Parse(null!));
    }

    // --- Usage text -----------------------------------------------------------------

    [Fact]
    public void Usage_warns_against_testing_inject_with_secrets()
    {
        // E9: the help is where someone learns --inject; it must tell them
        // that its argument is public on the machine, and how to avoid it.
        Assert.Contains("--inject -", CommandLine.Usage, StringComparison.Ordinal);
        Assert.Contains("standard input", CommandLine.Usage, StringComparison.Ordinal);
        Assert.Contains("Do not test with secrets", CommandLine.Usage, StringComparison.Ordinal);
        Assert.Contains("visible to every user of the machine", CommandLine.Usage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "success")]
    [InlineData(1, "generic failure")]
    [InlineData(2, "model is missing")]
    [InlineData(3, "failure")]
    [InlineData(4, "silent")]
    [InlineData(5, "no keyboard can be read")]
    public void Usage_documents_each_exit_code_as_HexWin_numbers_them(int code, string meaning)
    {
        // Scripts and the documentation rely on these numbers, which are
        // HexWin's: 2 is always the missing model, 4 the silent recording.
        Match line = Regex.Match(CommandLine.Usage, $@"^\s+{code}\s+(.+)$", RegexOptions.Multiline);

        Assert.True(line.Success, $"exit code {code} is not documented");
        Assert.Contains(meaning, line.Groups[1].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_option_the_usage_names_is_one_the_parser_knows()
    {
        // The help and the parser are two lists of the same options; an
        // option documented but refused as unknown is a help that lies.
        string[] documented = [.. Regex.Matches(CommandLine.Usage, "--[a-z][a-z-]*").Select(match => match.Value).Distinct()];

        Assert.NotEmpty(documented);

        foreach (string option in documented)
        {
            CliRequest request = Parse(option);

            Assert.False(
                request.Error?.StartsWith("Unknown option", StringComparison.Ordinal) == true,
                $"{option} is documented but unknown to the parser");
        }
    }

    [Theory]
    [InlineData("--record")]
    [InlineData("--seconds")]
    [InlineData("--transcribe")]
    [InlineData("--model")]
    [InlineData("--provider")]
    [InlineData("--watch-hotkey")]
    [InlineData("--inject")]
    [InlineData("--mode")]
    [InlineData("--delay")]
    [InlineData("--sender")]
    [InlineData("--test-feedback")]
    [InlineData("--doctor")]
    [InlineData("--autostart")]
    [InlineData("--toggle")]
    [InlineData("--start")]
    [InlineData("--stop")]
    [InlineData("--cancel")]
    [InlineData("--status")]
    [InlineData("--help")]
    public void Every_option_the_parser_knows_is_in_the_usage(string option)
    {
        Assert.Contains(option, CommandLine.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void Usage_names_every_state_the_status_reply_can_carry()
    {
        // A script parsing "hexlinux --status" learns the vocabulary from the
        // help: a state added to the daemon must be added there too.
        foreach (DictationState state in Enum.GetValues<DictationState>())
        {
            Assert.Contains(ControlCommands.StateName(state), CommandLine.Usage, StringComparison.Ordinal);
        }
    }
}
