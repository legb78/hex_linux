using HexLinux.Configuration;
using HexLinux.Input;

namespace HexLinux.Output;

/// <summary>
/// Decides whether the keys still held on the physical keyboard would change
/// what an insertion sends, and so whether it should wait for them.
///
/// <para><b>Why waiting is the only remedy with a virtual keyboard.</b>
/// HexWin released the stray modifiers itself before pasting. HexLinux's
/// uinput keyboard cannot: libinput tracks the state of each keyboard
/// separately, so a release written to the virtual keyboard does not release
/// a key held on the real one, and the compositor still combines the two into
/// one set of modifiers. A Super held when Ctrl+V goes out makes it
/// Super+Ctrl+V. xdotool can clear them (<c>--clearmodifiers</c>), wtype
/// cannot either; waiting a moment for the user's fingers works for
/// all.</para>
///
/// <para>Only the end of a dictation waits. Segments inserted while the
/// shortcut is held are sent with it held by design — the reason the default
/// shortcut is Right Ctrl, which leaves Ctrl+V unchanged.</para>
/// </summary>
public static class ModifierGuard
{
    /// <summary>The longest wait before inserting anyway.</summary>
    public static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(1);

    /// <summary>How often the held keys are looked at again while waiting.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// The held modifiers that would alter the insertion. Those the paste
    /// shortcut presses itself change nothing — Ctrl held with Ctrl+V is still
    /// Ctrl+V — whereas typed text is altered by any of them.
    /// </summary>
    public static IReadOnlySet<int> Blocking(IReadOnlySet<int> held, InsertionMode mode, PasteShortcut shortcut)
    {
        ArgumentNullException.ThrowIfNull(held);

        HashSet<int> harmless = mode == InsertionMode.Paste ? Harmless(shortcut) : [];

        return held.Where(code => LinuxKeys.Modifiers.Contains(code) && !harmless.Contains(code)).ToHashSet();
    }

    /// <summary>Whether to keep waiting, given what is held and how long it has lasted.</summary>
    public static bool ShouldWait(IReadOnlySet<int> blocking, TimeSpan waited)
    {
        ArgumentNullException.ThrowIfNull(blocking);

        return blocking.Count > 0 && waited < MaximumWait;
    }

    private static HashSet<int> Harmless(PasteShortcut shortcut) => shortcut switch
    {
        PasteShortcut.CtrlShiftV => [LinuxKeys.LeftCtrl, LinuxKeys.RightCtrl, LinuxKeys.LeftShift, LinuxKeys.RightShift],
        PasteShortcut.ShiftInsert => [LinuxKeys.LeftShift, LinuxKeys.RightShift],
        _ => [LinuxKeys.LeftCtrl, LinuxKeys.RightCtrl],
    };
}
