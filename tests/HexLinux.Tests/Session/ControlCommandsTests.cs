using HexLinux.Daemon;
using HexLinux.Input;
using HexLinux.Session;
using Xunit;

namespace HexLinux.Tests.Session;

/// <summary>
/// The control socket's vocabulary. It is how a user who will not grant
/// <c>/dev/input</c> dictates at all — <c>hexlinux --toggle</c> bound to a
/// desktop shortcut — so a word misread, or a toggle that queues a recording
/// behind a transcription, is the whole feature failing for them.
/// </summary>
public class ControlCommandsTests
{
    [Theory]
    [InlineData("toggle", ControlCommand.Toggle)]
    [InlineData("start", ControlCommand.Start)]
    [InlineData("stop", ControlCommand.Stop)]
    [InlineData("cancel", ControlCommand.Cancel)]
    [InlineData("status", ControlCommand.Status)]
    public void Each_word_of_the_protocol_is_understood(string line, ControlCommand expected)
    {
        Assert.Equal(expected, ControlCommands.Parse(line));
    }

    [Theory]
    [InlineData("TOGGLE")]
    [InlineData("Toggle")]
    [InlineData("  toggle  ")]
    [InlineData("toggle\r")]
    [InlineData("\ttoggle\n")]
    public void Case_and_surrounding_whitespace_do_not_matter(string line)
    {
        // A request typed by hand through socat or nc arrives with its line
        // ending, sometimes a CRLF.
        Assert.Equal(ControlCommand.Toggle, ControlCommands.Parse(line));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("--toggle")]
    [InlineData("toggle now")]
    [InlineData("tog gle")]
    [InlineData("quit")]
    [InlineData("unknown")]
    public void Anything_else_is_unknown(string? line)
    {
        // The daemon answers "error unknown-command" instead of guessing.
        Assert.Equal(ControlCommand.Unknown, ControlCommands.Parse(line));
    }

    [Theory]
    [InlineData(ControlCommand.Toggle, "toggle")]
    [InlineData(ControlCommand.Start, "start")]
    [InlineData(ControlCommand.Stop, "stop")]
    [InlineData(ControlCommand.Cancel, "cancel")]
    [InlineData(ControlCommand.Status, "status")]
    public void The_client_sends_the_word_the_daemon_reads(ControlCommand command, string word)
    {
        // hexlinux --toggle writes Name(Toggle); the daemon parses it back.
        Assert.Equal(word, ControlCommands.Name(command));
        Assert.Equal(command, ControlCommands.Parse(ControlCommands.Name(command)));
    }

    [Fact]
    public void Every_word_fits_in_the_line_the_daemon_reads()
    {
        // The server cuts a request off at this length: a longer word added
        // to the protocol would never be understood.
        foreach (ControlCommand command in Enum.GetValues<ControlCommand>())
        {
            Assert.True(ControlCommands.Name(command).Length < ControlCommands.MaxLineLength, command.ToString());
        }
    }

    // --- What a command amounts to --------------------------------------------------

    [Theory]
    [InlineData(DictationState.Idle, ChordAction.Start)]
    [InlineData(DictationState.Recording, ChordAction.Stop)]
    [InlineData(DictationState.Transcribing, ChordAction.None)]
    [InlineData(DictationState.Loading, ChordAction.None)]
    [InlineData(DictationState.Failed, ChordAction.None)]
    public void A_toggle_starts_when_idle_and_finishes_when_recording(DictationState state, ChordAction expected)
    {
        // Press-to-start, press-to-stop from a desktop shortcut. During a
        // transcription it does nothing, as the held shortcut does: a queued
        // start would open the microphone when nobody expects it.
        Assert.Equal(expected, ControlCommands.Resolve(ControlCommand.Toggle, state));
    }

    [Theory]
    [InlineData(DictationState.Idle, ChordAction.Start)]
    [InlineData(DictationState.Recording, ChordAction.None)]
    [InlineData(DictationState.Transcribing, ChordAction.None)]
    [InlineData(DictationState.Loading, ChordAction.None)]
    [InlineData(DictationState.Failed, ChordAction.None)]
    public void Start_only_starts_from_idle(DictationState state, ChordAction expected)
    {
        // Two shortcuts, one for start and one for stop: pressing start twice
        // must not end the recording.
        Assert.Equal(expected, ControlCommands.Resolve(ControlCommand.Start, state));
    }

    [Theory]
    [InlineData(DictationState.Recording, ChordAction.Stop)]
    [InlineData(DictationState.Idle, ChordAction.None)]
    [InlineData(DictationState.Transcribing, ChordAction.None)]
    [InlineData(DictationState.Loading, ChordAction.None)]
    [InlineData(DictationState.Failed, ChordAction.None)]
    public void Stop_only_finishes_a_recording(DictationState state, ChordAction expected)
    {
        // A stop pressed twice must not start a new dictation.
        Assert.Equal(expected, ControlCommands.Resolve(ControlCommand.Stop, state));
    }

    [Theory]
    [InlineData(DictationState.Recording, ChordAction.Cancel)]
    [InlineData(DictationState.Transcribing, ChordAction.Cancel)]
    [InlineData(DictationState.Idle, ChordAction.None)]
    [InlineData(DictationState.Loading, ChordAction.None)]
    [InlineData(DictationState.Failed, ChordAction.None)]
    public void Cancel_abandons_a_dictation_even_while_it_is_transcribed(DictationState state, ChordAction expected)
    {
        // The one command that can still stop text from arriving once the
        // key is released: the user realised the wrong window has the focus.
        Assert.Equal(expected, ControlCommands.Resolve(ControlCommand.Cancel, state));
    }

    [Theory]
    [InlineData(ControlCommand.Status)]
    [InlineData(ControlCommand.Unknown)]
    public void Status_and_unknown_words_change_nothing_whatever_the_state(ControlCommand command)
    {
        // --doctor asks for the status of a running daemon: asking must never
        // start or end a dictation.
        foreach (DictationState state in Enum.GetValues<DictationState>())
        {
            Assert.Equal(ChordAction.None, ControlCommands.Resolve(command, state));
        }
    }

    [Theory]
    [InlineData(DictationState.Loading, "loading")]
    [InlineData(DictationState.Idle, "idle")]
    [InlineData(DictationState.Recording, "recording")]
    [InlineData(DictationState.Transcribing, "transcribing")]
    [InlineData(DictationState.Failed, "failed")]
    public void States_go_by_the_words_documented_in_help(DictationState state, string word)
    {
        // Scripts that read "hexlinux --status" depend on these words.
        Assert.Equal(word, ControlCommands.StateName(state));
    }
}
