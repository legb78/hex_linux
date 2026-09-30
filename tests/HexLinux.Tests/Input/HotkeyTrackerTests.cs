using HexLinux.Input;
using Xunit;

namespace HexLinux.Tests.Input;

/// <summary>
/// The tracker is where raw evdev events from several keyboards meet. What it
/// protects are the releases that never come: a keyboard unplugged or gone to
/// sleep with a key down, and a release thrown away by the kernel when the
/// reader fell behind (<c>SYN_DROPPED</c>). Each must cost at most the
/// dictation held on that keyboard, never the shortcut for good, and never a
/// dictation held on another keyboard.
/// </summary>
public class HotkeyTrackerTests
{
    private const string Laptop = "/dev/input/event3";
    private const string Bluetooth = "/dev/input/event7";

    private const int RightCtrl = LinuxKeys.RightCtrl;

    // KEY_A of linux/input-event-codes.h.
    private const int KeyA = 30;

    /// <summary>EV_MSC / MSC_SCAN: the scan code many keyboards send before each key event.</summary>
    private static readonly InputEvent ScanCode = new(4, 4, 0x70028);

    private static readonly InputEvent Dropped = new(InputEvent.Synchronization, InputEvent.SynDropped, 0);

    private static InputEvent Press(int code) => new(InputEvent.Key, (ushort)code, 1);

    private static InputEvent Repeat(int code) => new(InputEvent.Key, (ushort)code, 2);

    private static InputEvent Release(int code) => new(InputEvent.Key, (ushort)code, 0);

    private static HotkeyTracker Tracker(params string[] keys) =>
        new(new ChordDetector(keys.Length == 0 ? ["RightCtrl"] : keys));

    // --- One keyboard -----------------------------------------------------------

    [Fact]
    public void A_press_and_a_release_start_and_stop_the_dictation()
    {
        HotkeyTracker tracker = Tracker();

        Assert.Equal(ChordAction.Start, tracker.OnEvent(Laptop, Press(RightCtrl)));
        Assert.Equal(ChordAction.None, tracker.OnEvent(Laptop, InputEvent.Report()));
        Assert.Equal(ChordAction.Stop, tracker.OnEvent(Laptop, Release(RightCtrl)));
    }

    [Fact]
    public void Auto_repeat_of_the_held_shortcut_does_not_restart_it()
    {
        // The kernel repeats a held key many times a second.
        HotkeyTracker tracker = Tracker();
        tracker.OnEvent(Laptop, Press(RightCtrl));

        Assert.Equal(ChordAction.None, tracker.OnEvent(Laptop, Repeat(RightCtrl)));
        Assert.Equal(ChordAction.None, tracker.OnEvent(Laptop, Repeat(RightCtrl)));
        Assert.True(tracker.Detector.IsActive);
    }

    [Fact]
    public void A_repeat_whose_press_was_never_seen_does_not_start_a_dictation()
    {
        // Right Ctrl was already down when the keyboard was opened (the daemon
        // starting, a keyboard plugged in): the user pressed nothing now.
        HotkeyTracker tracker = Tracker();

        Assert.Equal(ChordAction.None, tracker.OnEvent(Laptop, Repeat(RightCtrl)));
        Assert.False(tracker.Detector.IsActive);
        Assert.Equal(ChordAction.None, tracker.OnEvent(Laptop, Release(RightCtrl)));
    }

    [Fact]
    public void A_repeated_foreign_key_cancels_the_dictation()
    {
        // A letter held down while Right Ctrl is held types that letter again
        // and again into the application: not a dictation.
        HotkeyTracker tracker = Tracker();
        tracker.OnEvent(Laptop, Press(KeyA));
        tracker.OnEvent(Laptop, Press(RightCtrl));

        Assert.Equal(ChordAction.Cancel, tracker.OnEvent(Laptop, Repeat(KeyA)));
    }

    [Fact]
    public void Events_other_than_keys_are_ignored()
    {
        // Scan codes and reports come with every key event; a value outside
        // 0-2 is not a key transition anyone defined.
        HotkeyTracker tracker = Tracker();

        Assert.Equal(ChordAction.None, tracker.OnEvent(Laptop, ScanCode));
        Assert.Equal(ChordAction.None, tracker.OnEvent(Laptop, InputEvent.Report()));
        Assert.Equal(ChordAction.None, tracker.OnEvent(Laptop, new InputEvent(InputEvent.Key, RightCtrl, 7)));
        Assert.False(tracker.Detector.IsActive);
        Assert.Empty(tracker.HeldModifiers());
    }

    // --- Several keyboards --------------------------------------------------------

    [Fact]
    public void A_shortcut_can_be_held_across_two_keyboards()
    {
        // Ctrl on the laptop, Super on the external keyboard: the shortcut is
        // about keys, not about which device carries them.
        HotkeyTracker tracker = Tracker("Ctrl", "Super");

        Assert.Equal(ChordAction.None, tracker.OnEvent(Laptop, Press(LinuxKeys.LeftCtrl)));
        Assert.Equal(ChordAction.Start, tracker.OnEvent(Bluetooth, Press(LinuxKeys.LeftSuper)));
        Assert.Equal(ChordAction.Stop, tracker.OnEvent(Bluetooth, Release(LinuxKeys.LeftSuper)));
    }

    [Fact]
    public void A_keyboard_that_disappears_cancels_the_dictation_it_was_holding()
    {
        // Right Ctrl held on a Bluetooth keyboard whose battery dies: its
        // release will never be read.
        HotkeyTracker tracker = Tracker();
        tracker.OnEvent(Bluetooth, Press(RightCtrl));

        Assert.Equal(ChordAction.Cancel, tracker.OnDeviceRemoved(Bluetooth));
        Assert.False(tracker.Detector.IsActive);
    }

    [Fact]
    public void A_keyboard_that_disappears_leaves_a_dictation_held_on_another_alone()
    {
        // The Bluetooth keyboard falls asleep, a letter still believed down on
        // it, while the user dictates with Right Ctrl on the laptop.
        HotkeyTracker tracker = Tracker();
        tracker.OnEvent(Bluetooth, Press(KeyA));
        tracker.OnEvent(Laptop, Press(RightCtrl));

        Assert.Equal(ChordAction.None, tracker.OnDeviceRemoved(Bluetooth));
        Assert.True(tracker.Detector.IsActive);
        Assert.Equal(ChordAction.Stop, tracker.OnEvent(Laptop, Release(RightCtrl)));
    }

    [Fact]
    public void Removing_a_keyboard_that_held_nothing_reports_nothing()
    {
        HotkeyTracker tracker = Tracker();

        Assert.Equal(ChordAction.None, tracker.OnDeviceRemoved(Bluetooth));

        tracker.OnEvent(Bluetooth, Press(KeyA));
        tracker.OnEvent(Bluetooth, Release(KeyA));
        Assert.Equal(ChordAction.None, tracker.OnDeviceRemoved(Bluetooth));
    }

    [Fact]
    public void The_shortcut_works_again_after_its_keyboard_disappeared()
    {
        // Without forgetting the vanished key, the next press would be taken
        // for a key already held and nothing would ever start again.
        HotkeyTracker tracker = Tracker();
        tracker.OnEvent(Bluetooth, Press(RightCtrl));
        tracker.OnDeviceRemoved(Bluetooth);

        Assert.Equal(ChordAction.Start, tracker.OnEvent(Laptop, Press(RightCtrl)));
    }

    // --- SYN_DROPPED ------------------------------------------------------------

    [Fact]
    public void A_release_lost_in_a_drop_cancels_the_dictation_and_frees_the_shortcut()
    {
        // The reader fell behind and the kernel threw events away, the release
        // of Right Ctrl among them. Believed held, the next press read as
        // auto-repeat and the dictation ran until the recording limit.
        HotkeyTracker tracker = Tracker();
        tracker.OnEvent(Laptop, Press(RightCtrl));

        Assert.Equal(ChordAction.None, tracker.OnEvent(Laptop, Dropped));
        Assert.Equal(ChordAction.Cancel, tracker.OnEvent(Laptop, InputEvent.Report()));
        Assert.False(tracker.Detector.IsActive);

        Assert.Equal(ChordAction.Start, tracker.OnEvent(Laptop, Press(RightCtrl)));
    }

    [Fact]
    public void Events_after_a_drop_are_ignored_until_the_next_report()
    {
        // Up to that report the kernel's stream is unreliable: a press read
        // there may have no release to match it.
        HotkeyTracker tracker = Tracker();
        tracker.OnEvent(Laptop, Dropped);

        Assert.Equal(ChordAction.None, tracker.OnEvent(Laptop, Press(RightCtrl)));
        Assert.Equal(ChordAction.None, tracker.OnEvent(Laptop, ScanCode));
        Assert.False(tracker.Detector.IsActive);

        Assert.Equal(ChordAction.None, tracker.OnEvent(Laptop, InputEvent.Report()));
        Assert.Equal(ChordAction.Start, tracker.OnEvent(Laptop, Press(RightCtrl)));
    }

    [Fact]
    public void A_key_still_held_after_a_drop_needs_a_new_press()
    {
        // Right Ctrl really is still down after the drop: its repeats must not
        // bring back a dictation the drop cancelled.
        HotkeyTracker tracker = Tracker();
        tracker.OnEvent(Laptop, Press(RightCtrl));
        tracker.OnEvent(Laptop, Dropped);
        tracker.OnEvent(Laptop, InputEvent.Report());

        Assert.Equal(ChordAction.None, tracker.OnEvent(Laptop, Repeat(RightCtrl)));
        Assert.Equal(ChordAction.None, tracker.OnEvent(Laptop, Release(RightCtrl)));
        Assert.Equal(ChordAction.Start, tracker.OnEvent(Laptop, Press(RightCtrl)));
    }

    [Fact]
    public void A_drop_on_another_keyboard_leaves_the_dictation_alone()
    {
        HotkeyTracker tracker = Tracker();
        tracker.OnEvent(Bluetooth, Press(KeyA));
        tracker.OnEvent(Laptop, Press(RightCtrl));

        tracker.OnEvent(Bluetooth, Dropped);

        Assert.Equal(ChordAction.None, tracker.OnEvent(Bluetooth, InputEvent.Report()));
        Assert.True(tracker.Detector.IsActive);

        // Events of the laptop keyboard were never ignored meanwhile.
        Assert.Equal(ChordAction.Stop, tracker.OnEvent(Laptop, Release(RightCtrl)));
    }

    [Fact]
    public void A_keyboard_removed_during_a_drop_is_read_normally_when_it_comes_back()
    {
        // Plugged back in, the keyboard may get the same node again: it
        // must not stay ignored waiting for a report from the old one.
        HotkeyTracker tracker = Tracker();
        tracker.OnEvent(Bluetooth, Dropped);
        tracker.OnDeviceRemoved(Bluetooth);

        Assert.Equal(ChordAction.Start, tracker.OnEvent(Bluetooth, Press(RightCtrl)));
    }

    // --- Modifiers --------------------------------------------------------------

    [Fact]
    public void HeldModifiers_lists_the_modifiers_held_on_every_keyboard_and_nothing_else()
    {
        // What the paste waits for: a Ctrl+V sent while Shift or AltGr is held
        // becomes another shortcut. Letters are none of its business.
        HotkeyTracker tracker = Tracker();
        tracker.OnEvent(Laptop, Press(LinuxKeys.LeftShift));
        tracker.OnEvent(Laptop, Press(KeyA));
        tracker.OnEvent(Bluetooth, Press(LinuxKeys.RightAlt));

        Assert.Equal(
            new HashSet<int> { LinuxKeys.LeftShift, LinuxKeys.RightAlt },
            tracker.HeldModifiers().ToHashSet());
    }

    [Fact]
    public void HeldModifiers_drops_a_modifier_once_released_or_its_keyboard_gone()
    {
        HotkeyTracker tracker = Tracker();
        tracker.OnEvent(Laptop, Press(LinuxKeys.LeftShift));
        tracker.OnEvent(Bluetooth, Press(LinuxKeys.RightAlt));

        tracker.OnEvent(Laptop, Release(LinuxKeys.LeftShift));
        tracker.OnDeviceRemoved(Bluetooth);

        Assert.Empty(tracker.HeldModifiers());
    }

    [Fact]
    public void HeldModifiers_forgets_what_a_drop_may_have_released()
    {
        // A Shift whose release was lost would otherwise hold every paste back
        // until the wait gives up.
        HotkeyTracker tracker = Tracker();
        tracker.OnEvent(Laptop, Press(LinuxKeys.LeftShift));
        tracker.OnEvent(Laptop, Dropped);
        tracker.OnEvent(Laptop, InputEvent.Report());

        Assert.Empty(tracker.HeldModifiers());
    }

    // --- Arguments --------------------------------------------------------------

    [Fact]
    public void The_detector_given_is_the_one_used()
    {
        var detector = new ChordDetector(["RightCtrl"]);
        var tracker = new HotkeyTracker(detector);

        tracker.OnEvent(Laptop, Press(RightCtrl));

        Assert.Same(detector, tracker.Detector);
        Assert.True(detector.IsActive);
    }

    [Fact]
    public void Null_arguments_are_refused()
    {
        HotkeyTracker tracker = Tracker();

        Assert.Throws<ArgumentNullException>(() => new HotkeyTracker(null!));
        Assert.Throws<ArgumentNullException>(() => tracker.OnEvent(null!, Press(RightCtrl)));
        Assert.Throws<ArgumentNullException>(() => tracker.OnDeviceRemoved(null!));
    }
}
