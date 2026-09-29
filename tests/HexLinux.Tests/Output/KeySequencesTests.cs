using HexLinux.Configuration;
using HexLinux.Input;
using HexLinux.Output;
using Xunit;

namespace HexLinux.Tests.Output;

/// <summary>
/// What the uinput keyboard writes to paste. A key left down on the virtual
/// keyboard stays down for the whole desktop — every later keystroke of the
/// user would become a shortcut — and a key the device did not declare is
/// silently dropped by the kernel. Both are checked for every shortcut.
/// </summary>
public class KeySequencesTests
{
    public static TheoryData<PasteShortcut> Shortcuts => [PasteShortcut.CtrlV, PasteShortcut.CtrlShiftV, PasteShortcut.ShiftInsert];

    [Fact]
    public void Ctrl_V_is_left_ctrl_then_V()
    {
        Assert.Equal([LinuxKeys.LeftCtrl, LinuxKeys.V], KeySequences.KeysOf(PasteShortcut.CtrlV));
    }

    [Fact]
    public void Ctrl_Shift_V_is_left_ctrl_left_shift_then_V()
    {
        // The terminals' paste shortcut.
        Assert.Equal([LinuxKeys.LeftCtrl, LinuxKeys.LeftShift, LinuxKeys.V], KeySequences.KeysOf(PasteShortcut.CtrlShiftV));
    }

    [Fact]
    public void Shift_Insert_is_left_shift_then_insert()
    {
        // The way around a Dvorak or Bépo layout, where the V position types
        // another letter: Insert is Insert on every layout.
        Assert.Equal([LinuxKeys.LeftShift, LinuxKeys.Insert], KeySequences.KeysOf(PasteShortcut.ShiftInsert));
    }

    [Fact]
    public void The_codes_are_the_kernel_ones()
    {
        // linux/input-event-codes.h, checked by compiling against it in WSL.
        Assert.Equal(29, LinuxKeys.LeftCtrl);
        Assert.Equal(42, LinuxKeys.LeftShift);
        Assert.Equal(47, LinuxKeys.V);
        Assert.Equal(110, LinuxKeys.Insert);
    }

    [Fact]
    public void Ctrl_V_presses_in_order_and_releases_in_reverse_each_with_its_own_report()
    {
        // One SYN_REPORT per key change: a compositor that saw V go down in
        // the same report as Ctrl could apply it before the modifier.
        IReadOnlyList<InputEvent[]> groups = KeySequences.Paste(PasteShortcut.CtrlV);

        Assert.Equal(4, groups.Count);
        Assert.Equal([InputEvent.KeyEvent(LinuxKeys.LeftCtrl, pressed: true), InputEvent.Report()], groups[0]);
        Assert.Equal([InputEvent.KeyEvent(LinuxKeys.V, pressed: true), InputEvent.Report()], groups[1]);
        Assert.Equal([InputEvent.KeyEvent(LinuxKeys.V, pressed: false), InputEvent.Report()], groups[2]);
        Assert.Equal([InputEvent.KeyEvent(LinuxKeys.LeftCtrl, pressed: false), InputEvent.Report()], groups[3]);
    }

    [Fact]
    public void Ctrl_Shift_V_releases_V_then_shift_then_ctrl()
    {
        IReadOnlyList<InputEvent[]> groups = KeySequences.Paste(PasteShortcut.CtrlShiftV);

        Assert.Equal(
            [
                (LinuxKeys.LeftCtrl, 1),
                (LinuxKeys.LeftShift, 1),
                (LinuxKeys.V, 1),
                (LinuxKeys.V, 0),
                (LinuxKeys.LeftShift, 0),
                (LinuxKeys.LeftCtrl, 0),
            ],
            groups.Select(group => ((int)group[0].Code, group[0].Value)));
    }

    [Fact]
    public void Shift_Insert_releases_insert_before_shift()
    {
        IReadOnlyList<InputEvent[]> groups = KeySequences.Paste(PasteShortcut.ShiftInsert);

        Assert.Equal(
            [(LinuxKeys.LeftShift, 1), (LinuxKeys.Insert, 1), (LinuxKeys.Insert, 0), (LinuxKeys.LeftShift, 0)],
            groups.Select(group => ((int)group[0].Code, group[0].Value)));
    }

    [Theory]
    [MemberData(nameof(Shortcuts))]
    public void Every_group_is_one_key_event_followed_by_a_report(PasteShortcut shortcut)
    {
        foreach (InputEvent[] group in KeySequences.Paste(shortcut))
        {
            Assert.Equal(2, group.Length);
            Assert.True(group[0].IsKey);
            Assert.True(group[0].Value is 0 or 1, "a virtual keyboard never sends auto-repeat");
            Assert.True(group[1].IsSyncReport);
        }
    }

    [Theory]
    [MemberData(nameof(Shortcuts))]
    public void Every_key_pressed_is_released(PasteShortcut shortcut)
    {
        // A Ctrl left down on the virtual keyboard would make every letter
        // the user types next a shortcut, until the daemon restarts.
        IReadOnlyList<InputEvent[]> groups = KeySequences.Paste(shortcut);
        HashSet<int> down = [];

        foreach (InputEvent key in groups.Select(group => group[0]))
        {
            if (key.Value == 1)
            {
                Assert.True(down.Add(key.Code), $"key {key.Code} pressed twice");
            }
            else
            {
                Assert.True(down.Remove(key.Code), $"key {key.Code} released without a press");
            }
        }

        Assert.Empty(down);
        Assert.Equal(2 * KeySequences.KeysOf(shortcut).Count, groups.Count);
    }

    [Theory]
    [MemberData(nameof(Shortcuts))]
    public void Every_key_is_one_the_virtual_keyboard_declares(PasteShortcut shortcut)
    {
        // The kernel drops events for keys the device did not declare
        // (E10): the paste would then silently not happen.
        foreach (int key in KeySequences.KeysOf(shortcut))
        {
            Assert.Contains(key, UinputAbi.DeclaredKeys);
        }
    }
}
