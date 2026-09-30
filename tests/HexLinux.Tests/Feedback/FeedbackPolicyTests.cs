using HexLinux.Configuration;
using HexLinux.Daemon;
using HexLinux.Feedback;
using Xunit;

namespace HexLinux.Tests.Feedback;

/// <summary>
/// The rule checked here: the user hears exactly one beep when the microphone
/// opens and one when it closes, whatever route the state machine took to get
/// there — and nothing at all when the tones are off, including after the
/// tray's "Play tones" switch was flipped in the middle of a session.
/// </summary>
public class FeedbackPolicyTests
{
    private static FeedbackPolicy Ready(FeedbackMode mode = FeedbackMode.Sound)
    {
        var policy = new FeedbackPolicy(mode);

        // Every session goes through this: the model finishes loading and the
        // daemon becomes usable.
        policy.Next(DictationState.Idle);

        return policy;
    }

    [Fact]
    public void Becoming_ready_makes_no_sound()
    {
        // The model finishing its load is not the start of a dictation. A beep
        // here would fire on its own a few seconds after login, with nobody
        // having asked for anything.
        Assert.Equal(CueTone.None, new FeedbackPolicy(FeedbackMode.Sound).Next(DictationState.Idle));
    }

    [Fact]
    public void Starting_to_record_plays_the_start_tone()
    {
        Assert.Equal(CueTone.Start, Ready().Next(DictationState.Recording));
    }

    [Fact]
    public void Releasing_the_shortcut_plays_the_end_tone()
    {
        FeedbackPolicy policy = Ready();
        policy.Next(DictationState.Recording);

        Assert.Equal(CueTone.End, policy.Next(DictationState.Transcribing));
    }

    [Fact]
    public void An_abandoned_recording_still_plays_the_end_tone()
    {
        // The case that justifies the rule. A foreign key, a locked session,
        // a --cancel: the recording goes straight back to idle without ever
        // transcribing. Having heard the start tone, the user must hear the
        // end one — otherwise nothing says whether the microphone is still
        // open.
        FeedbackPolicy policy = Ready();
        policy.Next(DictationState.Recording);

        Assert.Equal(CueTone.End, policy.Next(DictationState.Idle));
    }

    [Theory]
    [InlineData(DictationState.Idle)]
    [InlineData(DictationState.Transcribing)]
    [InlineData(DictationState.Failed)]
    [InlineData(DictationState.Loading)]
    public void Every_exit_from_recording_plays_the_end_tone(DictationState next)
    {
        // The rule does not list the exits, it covers them all: a way out of
        // Recording added to the daemon later cannot forget to close the loop.
        FeedbackPolicy policy = Ready();
        policy.Next(DictationState.Recording);

        Assert.Equal(CueTone.End, policy.Next(next));
    }

    [Fact]
    public void Reaching_the_recording_state_again_does_not_replay_the_start_tone()
    {
        // The shortcut is held down, so keyboard auto-repeat fires it in
        // bursts. One beep per dictation, not one per keystroke.
        FeedbackPolicy policy = Ready();
        policy.Next(DictationState.Recording);

        Assert.Equal(CueTone.None, policy.Next(DictationState.Recording));
    }

    [Fact]
    public void The_end_of_a_transcription_is_silent()
    {
        // The recording already ended when the shortcut was released. Beeping
        // again when the text lands would mark the wrong moment.
        FeedbackPolicy policy = Ready();
        policy.Next(DictationState.Recording);
        policy.Next(DictationState.Transcribing);

        Assert.Equal(CueTone.None, policy.Next(DictationState.Idle));
    }

    [Fact]
    public void A_failed_model_makes_no_sound()
    {
        Assert.Equal(CueTone.None, Ready().Next(DictationState.Failed));
    }

    [Fact]
    public void Two_dictations_in_a_row_each_get_their_two_tones()
    {
        // The policy remembers only the last state: nothing from the first
        // dictation may leak into the second.
        FeedbackPolicy policy = Ready();

        CueTone[] heard =
        [
            policy.Next(DictationState.Recording),
            policy.Next(DictationState.Transcribing),
            policy.Next(DictationState.Idle),
            policy.Next(DictationState.Recording),
            policy.Next(DictationState.Idle),
        ];

        Assert.Equal([CueTone.Start, CueTone.End, CueTone.None, CueTone.Start, CueTone.End], heard);
    }

    [Theory]
    [InlineData(FeedbackMode.Sound, true)]
    [InlineData(FeedbackMode.None, false)]
    public void The_mode_is_readable_before_anything_is_built(FeedbackMode mode, bool tone)
    {
        // Read at construction, so that no waveform is rendered and no audio
        // stream opened for tones the user turned off.
        Assert.Equal(tone, new FeedbackPolicy(mode).PlaysTone);
    }

    [Fact]
    public void An_undefined_mode_plays_nothing()
    {
        // Only "Sound" asks for tones: anything else, even a value that
        // slipped past validation, stays silent rather than beep uninvited.
        Assert.False(new FeedbackPolicy((FeedbackMode)42).PlaysTone);
    }

    [Fact]
    public void With_the_tones_off_no_transition_makes_a_sound()
    {
        FeedbackPolicy policy = Ready(FeedbackMode.None);

        Assert.Equal(CueTone.None, policy.Next(DictationState.Recording));
        Assert.Equal(CueTone.None, policy.Next(DictationState.Transcribing));
        Assert.Equal(CueTone.None, policy.Next(DictationState.Idle));
    }

    [Fact]
    public void Turning_the_tones_off_mid_recording_silences_the_end_tone()
    {
        // Unticking "Play tones" takes effect at once, not at the next
        // dictation.
        FeedbackPolicy policy = Ready();
        policy.Next(DictationState.Recording);

        policy.PlaysTone = false;

        Assert.Equal(CueTone.None, policy.Next(DictationState.Transcribing));
    }

    [Fact]
    public void Turning_the_tones_on_mid_session_plays_the_next_start()
    {
        // Ticking the entry must take effect without restarting the daemon —
        // that is the whole point of the menu.
        FeedbackPolicy policy = Ready(FeedbackMode.None);

        policy.PlaysTone = true;

        Assert.Equal(CueTone.Start, policy.Next(DictationState.Recording));
    }

    [Fact]
    public void The_state_is_followed_while_silent()
    {
        // Tones turned back on while a recording is already under way: the
        // next auto-repeat must not be taken for a new start, because the
        // policy kept following the states while it was silent.
        FeedbackPolicy policy = Ready(FeedbackMode.None);
        policy.Next(DictationState.Recording);

        policy.PlaysTone = true;

        Assert.Equal(CueTone.None, policy.Next(DictationState.Recording));
    }
}
