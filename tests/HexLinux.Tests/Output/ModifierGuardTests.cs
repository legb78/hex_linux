using HexLinux.Configuration;
using HexLinux.Input;
using HexLinux.Output;
using Xunit;

namespace HexLinux.Tests.Output;

/// <summary>
/// P7: before the end-of-dictation insertion, the daemon waits for the user's
/// fingers to leave the modifiers that would change it. The uinput keyboard
/// cannot release a key held on the real one (libinput keeps each device's
/// state apart), so a Super still held turns Ctrl+V into Super+Ctrl+V — the
/// HexWin incident where a system panel took the focus and the dictation
/// vanished. Waiting too much is a fault too: the wait is capped.
/// </summary>
public class ModifierGuardTests
{
    private static HashSet<int> Held(params int[] codes) => [.. codes];

    [Fact]
    public void Nothing_held_blocks_nothing()
    {
        IReadOnlySet<int> blocking = ModifierGuard.Blocking(Held(), InsertionMode.Paste, PasteShortcut.CtrlV);

        Assert.Empty(blocking);
        Assert.False(ModifierGuard.ShouldWait(blocking, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(LinuxKeys.LeftSuper)]
    [InlineData(LinuxKeys.RightSuper)]
    [InlineData(LinuxKeys.LeftAlt)]
    [InlineData(LinuxKeys.RightAlt)]
    [InlineData(LinuxKeys.LeftShift)]
    [InlineData(LinuxKeys.RightShift)]
    public void A_modifier_foreign_to_ctrl_v_blocks_the_paste(int code)
    {
        // Super+Ctrl+V opens a panel on some desktops, Alt+Ctrl+V and
        // Shift+Ctrl+V mean other things again.
        IReadOnlySet<int> blocking = ModifierGuard.Blocking(Held(code), InsertionMode.Paste, PasteShortcut.CtrlV);

        Assert.Equal([code], blocking);
        Assert.True(ModifierGuard.ShouldWait(blocking, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(LinuxKeys.LeftCtrl)]
    [InlineData(LinuxKeys.RightCtrl)]
    public void Ctrl_held_changes_nothing_to_ctrl_v(int code)
    {
        // The default shortcut is Right Ctrl, precisely because Ctrl+V
        // stays Ctrl+V while it is held: no reason to wait for it.
        Assert.Empty(ModifierGuard.Blocking(Held(code), InsertionMode.Paste, PasteShortcut.CtrlV));
    }

    [Fact]
    public void Ctrl_and_shift_held_change_nothing_to_ctrl_shift_v_but_alt_does()
    {
        IReadOnlySet<int> blocking = ModifierGuard.Blocking(
            Held(LinuxKeys.LeftCtrl, LinuxKeys.RightCtrl, LinuxKeys.LeftShift, LinuxKeys.RightShift, LinuxKeys.LeftAlt),
            InsertionMode.Paste,
            PasteShortcut.CtrlShiftV);

        Assert.Equal([LinuxKeys.LeftAlt], blocking);
    }

    [Fact]
    public void Shift_held_changes_nothing_to_shift_insert_but_ctrl_does()
    {
        // Ctrl+Shift+Insert is not a paste; Ctrl+Insert is even a copy.
        IReadOnlySet<int> blocking = ModifierGuard.Blocking(
            Held(LinuxKeys.LeftShift, LinuxKeys.RightShift, LinuxKeys.RightCtrl),
            InsertionMode.Paste,
            PasteShortcut.ShiftInsert);

        Assert.Equal([LinuxKeys.RightCtrl], blocking);
    }

    [Theory]
    [InlineData(PasteShortcut.CtrlV)]
    [InlineData(PasteShortcut.CtrlShiftV)]
    [InlineData(PasteShortcut.ShiftInsert)]
    public void Typing_is_altered_by_every_modifier_ctrl_included(PasteShortcut shortcut)
    {
        // Typed text goes out key by key: with Ctrl held, "a" selects
        // everything; the paste shortcut setting plays no part.
        IReadOnlySet<int> blocking = ModifierGuard.Blocking(LinuxKeys.Modifiers, InsertionMode.Type, shortcut);

        Assert.Equal(LinuxKeys.Modifiers.Order(), blocking.Order());
    }

    [Fact]
    public void Keys_that_are_not_modifiers_never_block()
    {
        // The dictation shortcut may be F13, or a letter can still be down
        // from what the user typed: neither changes what Ctrl+V means.
        IReadOnlySet<int> blocking = ModifierGuard.Blocking(Held(LinuxKeys.F13, LinuxKeys.V, 30), InsertionMode.Type, PasteShortcut.CtrlV);

        Assert.Empty(blocking);
    }

    [Fact]
    public void Only_the_foreign_modifiers_are_reported()
    {
        IReadOnlySet<int> blocking = ModifierGuard.Blocking(
            Held(LinuxKeys.RightCtrl, LinuxKeys.LeftSuper, LinuxKeys.LeftAlt, LinuxKeys.F13),
            InsertionMode.Paste,
            PasteShortcut.CtrlV);

        Assert.Equal([LinuxKeys.LeftAlt, LinuxKeys.LeftSuper], blocking.Order());
    }

    [Fact]
    public void The_wait_lasts_while_a_foreign_modifier_is_held_up_to_one_second()
    {
        HashSet<int> blocking = Held(LinuxKeys.LeftSuper);

        Assert.True(ModifierGuard.ShouldWait(blocking, TimeSpan.Zero));
        Assert.True(ModifierGuard.ShouldWait(blocking, TimeSpan.FromMilliseconds(999)));
    }

    [Fact]
    public void After_one_second_the_text_is_inserted_anyway()
    {
        // A user resting a thumb on Alt must still get the dictation, even
        // at the risk of a modified shortcut: losing it for sure is worse.
        HashSet<int> blocking = Held(LinuxKeys.LeftSuper);

        Assert.Equal(TimeSpan.FromSeconds(1), ModifierGuard.MaximumWait);
        Assert.False(ModifierGuard.ShouldWait(blocking, ModifierGuard.MaximumWait));
        Assert.False(ModifierGuard.ShouldWait(blocking, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void The_held_keys_are_looked_at_several_times_within_the_wait()
    {
        // A poll slower than the cap would make the guard a plain delay.
        Assert.True(ModifierGuard.PollInterval > TimeSpan.Zero);
        Assert.True(ModifierGuard.PollInterval * 10 <= ModifierGuard.MaximumWait);
    }

    [Fact]
    public void Missing_sets_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => ModifierGuard.Blocking(null!, InsertionMode.Paste, PasteShortcut.CtrlV));
        Assert.Throws<ArgumentNullException>(() => ModifierGuard.ShouldWait(null!, TimeSpan.Zero));
    }
}
