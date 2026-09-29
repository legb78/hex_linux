using HexLinux.Daemon;
using HexLinux.Session;
using Xunit;

namespace HexLinux.Tests.Session;

/// <summary>
/// The one line the daemon sends back, and its reading by the client. The
/// grammar is printed by <c>--help</c> and read by scripts and by
/// <c>--doctor</c>; it is also a privacy boundary: a closed vocabulary that can
/// never carry dictated text back through the socket.
/// </summary>
public class ControlReplyTests
{
    [Theory]
    [InlineData(DictationState.Idle, "ok idle")]
    [InlineData(DictationState.Recording, "ok recording")]
    [InlineData(DictationState.Transcribing, "ok transcribing")]
    public void A_command_acted_on_answers_ok_and_the_state_reached(DictationState state, string line)
    {
        Assert.Equal(line, ControlReply.Done(state).Format());
    }

    [Fact]
    public void A_command_meaningless_now_answers_ignored_and_the_current_state()
    {
        // A toggle pressed during a transcription: the user learns why
        // nothing happened.
        Assert.Equal("ignored transcribing", ControlReply.Ignored(DictationState.Transcribing).Format());
    }

    [Fact]
    public void A_word_not_understood_answers_error_unknown_command()
    {
        Assert.Equal("error unknown-command", ControlReply.UnknownCommand.Format());
        Assert.Equal(ControlOutcome.Error, ControlReply.UnknownCommand.Outcome);
    }

    [Theory]
    [InlineData(ControlOutcome.Done, "loading")]
    [InlineData(ControlOutcome.Ignored, "recording")]
    [InlineData(ControlOutcome.Error, "unknown-command")]
    public void Every_reply_the_daemon_formats_is_read_back_by_the_client(ControlOutcome outcome, string detail)
    {
        // The daemon and hexlinux --status are one executable, but two
        // versions of it can meet after an update: the grammar must hold.
        var sent = new ControlReply(outcome, detail);

        Assert.True(ControlReply.TryParse(sent.Format(), out ControlReply received));
        Assert.Equal(sent, received);
    }

    [Fact]
    public void The_line_ending_of_the_reply_is_ignored()
    {
        // The client reads the reply with its newline.
        Assert.True(ControlReply.TryParse("ok idle\n", out ControlReply reply));
        Assert.Equal(ControlReply.Done(DictationState.Idle), reply);
    }

    [Fact]
    public void The_doctor_reads_the_state_of_a_running_daemon_from_the_detail()
    {
        Assert.True(ControlReply.TryParse("ok recording", out ControlReply reply));
        Assert.Equal(ControlOutcome.Done, reply.Outcome);
        Assert.Equal("recording", reply.Detail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ok")]
    [InlineData("ok idle now")]
    [InlineData("OK idle")]
    [InlineData("fine idle")]
    [InlineData("hexlinux 0.1.0")]
    public void A_line_off_the_grammar_is_rejected(string? line)
    {
        // Whatever else answers on that socket — an older daemon, another
        // program at the same path — is not taken for a HexLinux reply.
        Assert.False(ControlReply.TryParse(line, out ControlReply reply));
        Assert.Equal(default, reply);
    }
}
