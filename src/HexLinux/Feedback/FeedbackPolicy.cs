using HexLinux.Configuration;
using HexLinux.Daemon;

namespace HexLinux.Feedback;

/// <summary>Tone marking one boundary of a recording.</summary>
public enum CueTone
{
    /// <summary>Nothing to play.</summary>
    None,

    /// <summary>The microphone has just opened.</summary>
    Start,

    /// <summary>The microphone has just closed.</summary>
    End,
}

/// <summary>
/// Turns state changes into tones.
///
/// Pure logic, pulled out for the same reason as
/// <see cref="DictationCoordinator"/>: what matters are the boundaries between
/// states, and the ways of getting one wrong are tedious to provoke by hand —
/// a press too short to count, a shortcut released out of order — and trivial
/// to state as a test.
///
/// The rule is deliberately single: <b>every exit from
/// <see cref="DictationState.Recording"/> closes the loop with the end tone</b>,
/// cancellations included. A start heard with no end would leave the user
/// wondering whether the microphone is still open.
/// </summary>
public sealed class FeedbackPolicy
{
    private DictationState _previous = DictationState.Loading;

    public FeedbackPolicy(FeedbackMode mode) => PlaysTone = mode == FeedbackMode.Sound;

    /// <summary>True when the configuration asks for the tones.</summary>
    public bool PlaysTone { get; set; }

    /// <summary>
    /// Tone for the state just reached.
    ///
    /// Reaching the same state twice produces no tone. The shortcut is held
    /// down, so keyboard auto-repeat fires it in bursts: the user must hear one
    /// beep per dictation, not one per keystroke.
    /// </summary>
    public CueTone Next(DictationState state)
    {
        bool wasRecording = _previous == DictationState.Recording;
        bool isRecording = state == DictationState.Recording;

        _previous = state;

        CueTone tone = (wasRecording, isRecording) switch
        {
            (false, true) => CueTone.Start,
            (true, false) => CueTone.End,
            _ => CueTone.None,
        };

        return PlaysTone ? tone : CueTone.None;
    }
}
