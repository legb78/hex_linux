using HexLinux.Input;

namespace HexLinux.Daemon;

/// <summary>What the daemon does with one action of the held shortcut.</summary>
public enum ChordEffect
{
    /// <summary>Nothing: the action concerns no dictation of the shortcut's.</summary>
    None,

    /// <summary>Open the microphone and wait for the confirmation delay.</summary>
    Arm,

    /// <summary>Drop the armed microphone in silence: the shortcut was a shortcut.</summary>
    Disarm,

    /// <summary>End the dictation the shortcut started, and transcribe it.</summary>
    Stop,

    /// <summary>Give up the dictation the shortcut started, without transcribing it.</summary>
    Cancel,
}

/// <summary>
/// What an action of the held shortcut may do, given where the dictation
/// stands — the keyboard's counterpart of <see cref="Session.ControlCommands"/>.
///
/// <para><b>The shortcut only ever acts on a dictation it started itself.</b>
/// Its keys are not withheld from the desktop, so Right Ctrl keeps its
/// ordinary uses while a dictation is being transcribed, or while one started
/// from <c>hexlinux --toggle</c> or the tray is recording. Right Ctrl+C
/// pressed then — a copy, right after dictating — reads to the detector as a
/// start ignored and a foreign key: a cancel. Acted on, it would throw away
/// the dictation just finished, in silence, or end one the shortcut never
/// began.</para>
///
/// <para><b>And a cancel from the keyboard never reaches a transcription.</b>
/// Once the key is released the user has said what they wanted; only the
/// explicit <c>cancel</c> command can still stop the text from arriving.</para>
///
/// <para>Pure: the daemon passes the state and whether the dictation in
/// progress is the shortcut's own.</para>
/// </summary>
public static class ChordCommands
{
    /// <param name="action">What the detector concluded from the last key event.</param>
    /// <param name="state">The coordinator's state.</param>
    /// <param name="armed">True while a press waits for its confirmation delay.</param>
    /// <param name="chordOwnsDictation">
    /// True when the dictation in progress was started by the shortcut, not
    /// by a command or the tray.
    /// </param>
    public static ChordEffect Resolve(ChordAction action, DictationState state, bool armed, bool chordOwnsDictation) => action switch
    {
        ChordAction.Start => state == DictationState.Idle ? ChordEffect.Arm : ChordEffect.None,
        ChordAction.Stop when armed => ChordEffect.Disarm,
        ChordAction.Stop => chordOwnsDictation && state == DictationState.Recording ? ChordEffect.Stop : ChordEffect.None,
        ChordAction.Cancel when armed => ChordEffect.Disarm,
        ChordAction.Cancel => chordOwnsDictation && state == DictationState.Recording ? ChordEffect.Cancel : ChordEffect.None,
        _ => ChordEffect.None,
    };
}
