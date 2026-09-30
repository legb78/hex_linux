using HexLinux.Daemon;
using HexLinux.Input;
using Xunit;

namespace HexLinux.Tests.Daemon;

/// <summary>
/// What the held shortcut may do to a dictation. Its keys keep their ordinary
/// uses — Right Ctrl+C is still a copy — so every row here is a moment where
/// an ordinary shortcut, typed at the wrong time, could otherwise throw a
/// dictation away (review finding RV-01).
/// </summary>
public class ChordCommandsTests
{
    [Fact]
    public void Right_Ctrl_C_during_a_transcription_cancels_nothing()
    {
        // RV-01: the user dictates, releases Right Ctrl, then copies with
        // Right Ctrl+C at once. The detector reads a start (ignored: the
        // daemon is transcribing) then a foreign key: a cancel. The text of
        // the dictation just finished used to be discarded in silence.
        Assert.Equal(ChordEffect.None, ChordCommands.Resolve(ChordAction.Start, DictationState.Transcribing, armed: false, chordOwnsDictation: false));
        Assert.Equal(ChordEffect.None, ChordCommands.Resolve(ChordAction.Cancel, DictationState.Transcribing, armed: false, chordOwnsDictation: true));
        Assert.Equal(ChordEffect.None, ChordCommands.Resolve(ChordAction.Cancel, DictationState.Transcribing, armed: false, chordOwnsDictation: false));
    }

    [Fact]
    public void Releasing_the_shortcut_during_a_transcription_changes_nothing()
    {
        // The press that came with Right Ctrl+C is released too: that stop
        // must not reach the transcription either.
        Assert.Equal(ChordEffect.None, ChordCommands.Resolve(ChordAction.Stop, DictationState.Transcribing, armed: false, chordOwnsDictation: true));
    }

    [Theory]
    [InlineData(ChordAction.Stop)]
    [InlineData(ChordAction.Cancel)]
    public void A_dictation_started_by_a_command_is_not_the_shortcut_s_to_end(ChordAction action)
    {
        // hexlinux --toggle bound to a desktop shortcut: the user dictates
        // hands free, and a Right Ctrl pressed and released, or Right Ctrl+V,
        // meanwhile must neither end nor cancel what the command started.
        Assert.Equal(ChordEffect.None, ChordCommands.Resolve(action, DictationState.Recording, armed: false, chordOwnsDictation: false));
    }

    [Fact]
    public void Releasing_the_shortcut_ends_its_own_dictation()
    {
        // Hold-to-talk itself: the release is what sends the text.
        Assert.Equal(ChordEffect.Stop, ChordCommands.Resolve(ChordAction.Stop, DictationState.Recording, armed: false, chordOwnsDictation: true));
    }

    [Fact]
    public void A_foreign_key_while_holding_cancels_the_shortcut_s_own_dictation()
    {
        // Right Ctrl held long enough to confirm, then C: the user was
        // copying, not dictating — HexWin's rule, kept.
        Assert.Equal(ChordEffect.Cancel, ChordCommands.Resolve(ChordAction.Cancel, DictationState.Recording, armed: false, chordOwnsDictation: true));
    }

    [Theory]
    [InlineData(ChordAction.Stop)]
    [InlineData(ChordAction.Cancel)]
    public void Before_the_confirmation_a_release_or_a_foreign_key_only_disarms(ChordAction action)
    {
        // Right Ctrl+C typed quickly: the microphone opened at the press is
        // dropped without a tone, whatever else is going on.
        Assert.Equal(ChordEffect.Disarm, ChordCommands.Resolve(action, DictationState.Idle, armed: true, chordOwnsDictation: false));
    }

    [Fact]
    public void A_press_arms_only_when_the_daemon_is_idle()
    {
        // A dictation cannot start over another one, nor before the model is
        // checked, nor when it is unusable.
        Assert.Equal(ChordEffect.Arm, ChordCommands.Resolve(ChordAction.Start, DictationState.Idle, armed: false, chordOwnsDictation: false));

        foreach (DictationState state in new[] { DictationState.Loading, DictationState.Recording, DictationState.Transcribing, DictationState.Failed })
        {
            Assert.Equal(ChordEffect.None, ChordCommands.Resolve(ChordAction.Start, state, armed: false, chordOwnsDictation: false));
        }
    }

    [Fact]
    public void Nothing_comes_of_no_action()
    {
        // Auto-repeat and keys outside any dictation yield None from the
        // detector: the daemon must do nothing with them.
        foreach (DictationState state in Enum.GetValues<DictationState>())
        {
            Assert.Equal(ChordEffect.None, ChordCommands.Resolve(ChordAction.None, state, armed: false, chordOwnsDictation: true));
            Assert.Equal(ChordEffect.None, ChordCommands.Resolve(ChordAction.None, state, armed: true, chordOwnsDictation: false));
        }
    }

    [Fact]
    public void Outside_a_recording_the_shortcut_never_stops_or_cancels_anything()
    {
        // The whole table at once: only a recording of the shortcut's own can
        // be ended or cancelled from the keyboard.
        foreach (DictationState state in Enum.GetValues<DictationState>())
        {
            foreach (bool owns in new[] { false, true })
            {
                foreach (ChordAction action in new[] { ChordAction.Stop, ChordAction.Cancel })
                {
                    ChordEffect effect = ChordCommands.Resolve(action, state, armed: false, owns);
                    bool mayAct = state == DictationState.Recording && owns;

                    Assert.Equal(mayAct, effect is ChordEffect.Stop or ChordEffect.Cancel);
                }
            }
        }
    }
}
