using HexLinux.Configuration;
using HexLinux.Input;

namespace HexLinux.Output;

/// <summary>
/// The events HexLinux's virtual keyboard writes to press a paste shortcut.
///
/// <para>Keys go down in order and come up in reverse, each followed by a
/// <c>SYN_REPORT</c> so that it takes effect on its own: a compositor that saw
/// V go down in the same report as Ctrl could read it before the modifier.
/// The caller leaves a few milliseconds between groups, for the same
/// reason.</para>
///
/// <para>Physical keys, not characters: <c>KEY_V</c> is the key at the V
/// position of a QWERTY keyboard. That is V on AZERTY, QWERTZ and Colemak
/// too, but another letter on Dvorak or Bépo — hence the doctor's warning for
/// those layouts, and Shift+Insert as the way around it.</para>
/// </summary>
public static class KeySequences
{
    /// <summary>The keys of <paramref name="shortcut"/>, in the order they are pressed.</summary>
    public static IReadOnlyList<int> KeysOf(PasteShortcut shortcut) => shortcut switch
    {
        PasteShortcut.CtrlShiftV => [LinuxKeys.LeftCtrl, LinuxKeys.LeftShift, LinuxKeys.V],
        PasteShortcut.ShiftInsert => [LinuxKeys.LeftShift, LinuxKeys.Insert],
        _ => [LinuxKeys.LeftCtrl, LinuxKeys.V],
    };

    /// <summary>Groups of events: one key change and its report each.</summary>
    public static IReadOnlyList<InputEvent[]> Paste(PasteShortcut shortcut)
    {
        IReadOnlyList<int> keys = KeysOf(shortcut);
        List<InputEvent[]> groups = [];

        foreach (int key in keys)
        {
            groups.Add([InputEvent.KeyEvent(key, pressed: true), InputEvent.Report()]);
        }

        foreach (int key in Enumerable.Reverse(keys))
        {
            groups.Add([InputEvent.KeyEvent(key, pressed: false), InputEvent.Report()]);
        }

        return groups;
    }
}
