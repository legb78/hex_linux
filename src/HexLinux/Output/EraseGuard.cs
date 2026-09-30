using HexLinux.Input;

namespace HexLinux.Output;

/// <summary>What to do about a spoken erase, given the keys held right now.</summary>
public enum EraseDecision
{
    /// <summary>No modifier is held: the Backspaces may go.</summary>
    Erase,

    /// <summary>Look again in a moment.</summary>
    Wait,

    /// <summary>A modifier stayed held too long after the dictation: leave the text as it is.</summary>
    GiveUp,
}

/// <summary>
/// Decides when the Backspaces of a spoken "efface ça" may be sent.
///
/// <para><b>Never with a modifier held.</b> Ctrl+Backspace erases a whole
/// word in most applications, Alt+Backspace does in terminals (readline's
/// backward-kill-word), and Super+Backspace may be a desktop shortcut: the
/// count of characters would then reach text the dictation never typed.
/// HexWin releases Ctrl before erasing; HexLinux's virtual keyboard cannot
/// release a key held on the real one (see <see cref="ModifierGuard"/>), so it
/// waits.</para>
///
/// <para><b>How long.</b> With segmentation, the erase usually comes while
/// the shortcut is still held — Right Ctrl by default — for the dictation
/// goes on: it waits as long as the recording lasts, the segments after it
/// queueing behind. Once the shortcut is released, a modifier still held after
/// <see cref="ModifierGuard.MaximumWait"/> makes it give up: the sentence
/// stays, which is safe, where erasing blindly is not. Pure.</para>
/// </summary>
public static class EraseGuard
{
    /// <param name="held">The keys held on the physical keyboards.</param>
    /// <param name="recording">The dictation is still recording: its shortcut is held on purpose.</param>
    /// <param name="sinceRecordingEnded">How long it has not been recording.</param>
    public static EraseDecision Decide(IReadOnlySet<int> held, bool recording, TimeSpan sinceRecordingEnded)
    {
        ArgumentNullException.ThrowIfNull(held);

        if (!held.Any(LinuxKeys.Modifiers.Contains))
        {
            return EraseDecision.Erase;
        }

        return recording || sinceRecordingEnded < ModifierGuard.MaximumWait ? EraseDecision.Wait : EraseDecision.GiveUp;
    }
}
