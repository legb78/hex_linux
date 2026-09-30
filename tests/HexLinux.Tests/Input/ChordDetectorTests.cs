using HexLinux.Input;
using Xunit;

namespace HexLinux.Tests.Input;

/// <summary>
/// Every case covered here happens in real use and is awkward to reproduce by
/// hand: keyboard auto-repeat, keys released out of order, a two-key shortcut
/// let go one finger at a time, a letter that turns the shortcut into Ctrl+C,
/// a keyboard unplugged with a key still down. Making them testable is why the
/// deciding was separated from the evdev reader. Nothing is swallowed on
/// Linux, so HexWin's swallowing and Start menu cases have no counterpart.
/// </summary>
public class ChordDetectorTests
{
    private const int LeftCtrl = LinuxKeys.LeftCtrl;
    private const int RightCtrl = LinuxKeys.RightCtrl;
    private const int LeftSuper = LinuxKeys.LeftSuper;
    private const int RightSuper = LinuxKeys.RightSuper;

    // KEY_A, KEY_C, KEY_D of linux/input-event-codes.h.
    private const int KeyA = 30;
    private const int KeyC = 46;
    private const int KeyD = 32;

    private static ChordDetector CtrlSuper() => new(["Ctrl", "Super"]);

    private static ChordDetector DefaultShortcut() => new(["RightCtrl"]);

    private static ChordDetector Started(ChordDetector detector, params int[] codes)
    {
        foreach (int code in codes)
        {
            detector.OnKeyDown(code);
        }

        Assert.True(detector.IsActive);
        return detector;
    }

    // --- Nominal triggering ----------------------------------------------------

    [Fact]
    public void The_complete_shortcut_starts_the_dictation()
    {
        ChordDetector detector = CtrlSuper();

        Assert.Equal(ChordAction.None, detector.OnKeyDown(LeftCtrl));
        Assert.Equal(ChordAction.Start, detector.OnKeyDown(LeftSuper));
        Assert.True(detector.IsActive);
    }

    [Fact]
    public void Releasing_stops_the_dictation()
    {
        ChordDetector detector = Started(CtrlSuper(), LeftCtrl, LeftSuper);

        Assert.Equal(ChordAction.Stop, detector.OnKeyUp(LeftSuper));
        Assert.False(detector.IsActive);
    }

    [Fact]
    public void The_press_order_does_not_matter()
    {
        ChordDetector detector = CtrlSuper();

        Assert.Equal(ChordAction.None, detector.OnKeyDown(LeftSuper));
        Assert.Equal(ChordAction.Start, detector.OnKeyDown(LeftCtrl));
    }

    [Fact]
    public void Releasing_either_of_the_two_keys_stops_it()
    {
        // Users rarely release both keys at exactly the same instant: the
        // first key let go ends the dictation, whichever it is.
        ChordDetector detector = Started(CtrlSuper(), LeftCtrl, LeftSuper);

        Assert.Equal(ChordAction.Stop, detector.OnKeyUp(LeftCtrl));
    }

    [Fact]
    public void The_second_release_after_a_stop_reports_nothing()
    {
        // Keys released out of order: the dictation ended on the first one, the
        // second must not end it a second time while it is being transcribed.
        ChordDetector detector = Started(CtrlSuper(), LeftCtrl, LeftSuper);
        detector.OnKeyUp(LeftCtrl);

        Assert.Equal(ChordAction.None, detector.OnKeyUp(LeftSuper));
    }

    [Fact]
    public void The_left_and_right_keys_are_equivalent()
    {
        // "Ctrl" means both physical keys, and the kernel always reports one
        // side: the user should not have to wonder which one is under their
        // fingers.
        ChordDetector detector = CtrlSuper();

        Assert.Equal(ChordAction.None, detector.OnKeyDown(RightCtrl));
        Assert.Equal(ChordAction.Start, detector.OnKeyDown(RightSuper));
    }

    [Fact]
    public void The_default_shortcut_starts_on_the_press_and_stops_on_the_release()
    {
        // ["RightCtrl"], the shipped default: a single key is the whole chord.
        ChordDetector detector = DefaultShortcut();

        Assert.Equal(ChordAction.Start, detector.OnKeyDown(RightCtrl));
        Assert.Equal(ChordAction.Stop, detector.OnKeyUp(RightCtrl));
        Assert.False(detector.IsActive);
    }

    [Fact]
    public void A_sided_name_ignores_the_other_side()
    {
        // With Right Ctrl as the shortcut, the left Ctrl of Ctrl+C, Ctrl+V and
        // every other shortcut must never open the microphone.
        ChordDetector detector = DefaultShortcut();

        Assert.Equal(ChordAction.None, detector.OnKeyDown(LeftCtrl));
        Assert.Equal(ChordAction.None, detector.OnKeyUp(LeftCtrl));
        Assert.False(detector.IsActive);
    }

    [Fact]
    public void A_three_key_shortcut_requires_all_three()
    {
        var detector = new ChordDetector(["LeftCtrl", "LeftShift", "F13"]);

        Assert.Equal(ChordAction.None, detector.OnKeyDown(LeftCtrl));
        Assert.Equal(ChordAction.None, detector.OnKeyDown(LinuxKeys.LeftShift));
        Assert.Equal(ChordAction.Start, detector.OnKeyDown(LinuxKeys.F13));
    }

    [Fact]
    public void A_release_of_a_key_never_seen_pressed_reports_nothing()
    {
        // The daemon started, or the keyboard was opened, while the key was
        // already down: its release alone is not the end of a dictation.
        ChordDetector detector = DefaultShortcut();

        Assert.Equal(ChordAction.None, detector.OnKeyUp(RightCtrl));
        Assert.False(detector.IsActive);
    }

    // --- Auto-repeat ------------------------------------------------------------

    [Fact]
    public void Auto_repeat_does_not_restart_the_dictation()
    {
        // Holding a key sends key-downs in bursts (value 2 in evdev). Without
        // this guard, every repeat would restart the recording from zero and
        // the dictation would only ever hold the last fragment.
        ChordDetector detector = Started(CtrlSuper(), LeftCtrl, LeftSuper);

        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(ChordAction.None, detector.OnKeyDown(LeftCtrl));
            Assert.Equal(ChordAction.None, detector.OnKeyDown(LeftSuper));
        }

        Assert.True(detector.IsActive);
    }

    [Fact]
    public void Auto_repeat_of_the_first_key_does_not_complete_the_shortcut()
    {
        // Ctrl held long enough to repeat, Super still up: nothing starts
        // until the second key really comes down.
        ChordDetector detector = CtrlSuper();
        detector.OnKeyDown(LeftCtrl);

        Assert.Equal(ChordAction.None, detector.OnKeyDown(LeftCtrl));
        Assert.False(detector.IsActive);
        Assert.Equal(ChordAction.Start, detector.OnKeyDown(LeftSuper));
    }

    // --- A start per press ------------------------------------------------------

    [Fact]
    public void A_half_released_shortcut_does_not_start_a_second_dictation()
    {
        // The way a shortcut is really let go: the thumb leaves Super while the
        // little finger stays on Ctrl. Pressing Super back found the chord
        // complete again and opened a dictation over the one being
        // transcribed.
        ChordDetector detector = Started(CtrlSuper(), LeftCtrl, LeftSuper);
        detector.OnKeyUp(LeftSuper);

        Assert.Equal(ChordAction.None, detector.OnKeyDown(LeftSuper));
        Assert.False(detector.IsActive);
    }

    [Fact]
    public void Releasing_every_key_arms_the_shortcut_again()
    {
        // The counterpart of the test above: dictating twice in a row must keep
        // working, once the shortcut has really been let go.
        ChordDetector detector = Started(CtrlSuper(), LeftCtrl, LeftSuper);
        detector.OnKeyUp(LeftSuper);
        detector.OnKeyUp(LeftCtrl);

        detector.OnKeyDown(LeftCtrl);

        Assert.Equal(ChordAction.Start, detector.OnKeyDown(LeftSuper));
    }

    [Fact]
    public void IsAnyHeld_stays_true_until_the_last_key_of_the_shortcut_is_let_go()
    {
        // A two-key shortcut half released still holds a modifier: whatever
        // waits for the keyboard to be free must go on waiting.
        ChordDetector detector = Started(CtrlSuper(), LeftCtrl, LeftSuper);

        detector.OnKeyUp(LeftSuper);
        Assert.True(detector.IsAnyHeld);

        detector.OnKeyUp(LeftCtrl);
        Assert.False(detector.IsAnyHeld);
    }

    [Fact]
    public void IsAnyHeld_ignores_keys_foreign_to_the_shortcut()
    {
        ChordDetector detector = DefaultShortcut();

        detector.OnKeyDown(KeyA);

        Assert.False(detector.IsAnyHeld);
    }

    // --- Interruption -----------------------------------------------------------

    [Fact]
    public void A_foreign_key_during_the_dictation_cancels_it()
    {
        // Right Ctrl held, then C: that is a copy, and the desktop saw both
        // keys. The user was not asking for a transcription.
        ChordDetector detector = Started(DefaultShortcut(), RightCtrl);

        Assert.Equal(ChordAction.Cancel, detector.OnKeyDown(KeyC));
        Assert.False(detector.IsActive);
    }

    [Fact]
    public void A_foreign_key_before_the_shortcut_is_complete_changes_nothing()
    {
        // Ctrl+C with a Ctrl+Super shortcut: an ordinary copy, nothing to
        // start and nothing to cancel.
        ChordDetector detector = CtrlSuper();

        Assert.Equal(ChordAction.None, detector.OnKeyDown(LeftCtrl));
        Assert.Equal(ChordAction.None, detector.OnKeyDown(KeyC));
        Assert.Equal(ChordAction.None, detector.OnKeyUp(KeyC));
        Assert.Equal(ChordAction.None, detector.OnKeyUp(LeftCtrl));
        Assert.False(detector.IsActive);
    }

    [Fact]
    public void Only_the_first_foreign_key_cancels()
    {
        // Right Ctrl+C then Right Ctrl+V without letting go: one cancellation,
        // not a second one reaching a daemon that is already idle.
        ChordDetector detector = Started(DefaultShortcut(), RightCtrl);
        detector.OnKeyDown(KeyC);
        detector.OnKeyUp(KeyC);

        Assert.Equal(ChordAction.None, detector.OnKeyDown(LinuxKeys.V));
    }

    [Fact]
    public void A_foreign_key_released_during_the_dictation_is_ignored()
    {
        // A fast typist presses Right Ctrl before the last letter of the word
        // is up: that letter's release must neither stop nor cancel.
        ChordDetector detector = DefaultShortcut();
        detector.OnKeyDown(KeyA);
        detector.OnKeyDown(RightCtrl);

        Assert.Equal(ChordAction.None, detector.OnKeyUp(KeyA));
        Assert.True(detector.IsActive);
    }

    [Fact]
    public void After_a_cancellation_releasing_triggers_no_transcription()
    {
        ChordDetector detector = Started(CtrlSuper(), LeftCtrl, LeftSuper);
        detector.OnKeyDown(KeyD);

        Assert.Equal(ChordAction.None, detector.OnKeyUp(LeftSuper));
        Assert.Equal(ChordAction.None, detector.OnKeyUp(LeftCtrl));
    }

    [Fact]
    public void After_a_cancellation_the_held_key_does_not_start_again_until_released()
    {
        // Right Ctrl+C, C let go, Right Ctrl still down and repeating: the user
        // made a copy, not a request to dictate. A new dictation takes a new
        // press.
        ChordDetector detector = Started(DefaultShortcut(), RightCtrl);
        detector.OnKeyDown(KeyC);
        detector.OnKeyUp(KeyC);

        Assert.Equal(ChordAction.None, detector.OnKeyDown(RightCtrl));
        Assert.Equal(ChordAction.None, detector.OnKeyUp(RightCtrl));
        Assert.Equal(ChordAction.Start, detector.OnKeyDown(RightCtrl));
    }

    // --- Reset ------------------------------------------------------------------

    [Fact]
    public void A_reset_cancels_a_dictation_in_progress()
    {
        // Releases stop arriving, for instance from a keyboard that vanished:
        // the key would stay "held" forever.
        ChordDetector detector = Started(CtrlSuper(), LeftCtrl, LeftSuper);

        Assert.Equal(ChordAction.Cancel, detector.Reset());
        Assert.False(detector.IsActive);
        Assert.False(detector.IsAnyHeld);
    }

    [Fact]
    public void A_reset_while_idle_does_nothing()
    {
        Assert.Equal(ChordAction.None, CtrlSuper().Reset());
    }

    [Fact]
    public void The_shortcut_works_again_after_a_reset()
    {
        // Reset has to clear the start as well, or a shortcut half released
        // when it happened would stay silent until the application restarts.
        ChordDetector detector = Started(CtrlSuper(), LeftCtrl, LeftSuper);
        detector.OnKeyUp(LeftSuper);
        detector.Reset();

        detector.OnKeyDown(LeftCtrl);

        Assert.Equal(ChordAction.Start, detector.OnKeyDown(LeftSuper));
    }

    // --- Forget (one keyboard gone) ---------------------------------------------

    [Fact]
    public void Forgetting_a_key_of_the_shortcut_in_progress_cancels_the_dictation()
    {
        // The keyboard holding Right Ctrl was unplugged: nobody can tell whether
        // the key was released on purpose, so nothing is transcribed.
        ChordDetector detector = Started(DefaultShortcut(), RightCtrl);

        Assert.Equal(ChordAction.Cancel, detector.Forget([RightCtrl]));
        Assert.False(detector.IsActive);
        Assert.False(detector.IsAnyHeld);
    }

    [Fact]
    public void Forgetting_keys_foreign_to_the_shortcut_leaves_the_dictation_running()
    {
        // A Bluetooth keyboard falls asleep with a letter believed held, while
        // Right Ctrl is held on the laptop's own keyboard.
        ChordDetector detector = Started(DefaultShortcut(), RightCtrl);

        Assert.Equal(ChordAction.None, detector.Forget([KeyA, LinuxKeys.LeftShift]));
        Assert.True(detector.IsActive);
        Assert.Equal(ChordAction.Stop, detector.OnKeyUp(RightCtrl));
    }

    [Fact]
    public void Forgetting_nothing_changes_nothing()
    {
        ChordDetector detector = Started(DefaultShortcut(), RightCtrl);

        Assert.Equal(ChordAction.None, detector.Forget([]));
        Assert.True(detector.IsActive);
    }

    [Fact]
    public void Forgetting_the_key_left_held_after_a_stop_arms_the_shortcut_again()
    {
        // Super released (the dictation is being transcribed), then the
        // keyboard still holding Ctrl disappears. Its release will never come:
        // without clearing the start, Ctrl+Super would never start again.
        ChordDetector detector = Started(CtrlSuper(), LeftCtrl, LeftSuper);
        detector.OnKeyUp(LeftSuper);

        Assert.Equal(ChordAction.None, detector.Forget([LeftCtrl]));

        detector.OnKeyDown(LeftCtrl);
        Assert.Equal(ChordAction.Start, detector.OnKeyDown(LeftSuper));
    }

    [Fact]
    public void Forgetting_one_key_of_two_keeps_the_other_held()
    {
        // Ctrl on the laptop, Super on an external keyboard that goes away:
        // Ctrl is still down, so the shortcut is not armed again yet.
        ChordDetector detector = Started(CtrlSuper(), LeftCtrl, LeftSuper);

        Assert.Equal(ChordAction.Cancel, detector.Forget([LeftSuper]));
        Assert.True(detector.IsAnyHeld);
        Assert.Equal(ChordAction.None, detector.OnKeyDown(LeftSuper));
    }

    [Fact]
    public void The_shortcut_works_again_after_a_forget()
    {
        ChordDetector detector = Started(DefaultShortcut(), RightCtrl);
        detector.Forget([RightCtrl]);

        Assert.Equal(ChordAction.Start, detector.OnKeyDown(RightCtrl));
    }

    // --- Construction -----------------------------------------------------------

    [Fact]
    public void Codes_lists_every_physical_key_the_shortcut_accepts()
    {
        // What the device catalog listens for: both sides of a generic name.
        Assert.Equal(
            new HashSet<int> { LeftCtrl, RightCtrl, LeftSuper, RightSuper },
            CtrlSuper().Codes);
        Assert.Equal(new HashSet<int> { LinuxKeys.F13 }, new ChordDetector(["F13"]).Codes);
    }

    [Fact]
    public void An_empty_shortcut_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new ChordDetector([]));
    }

    [Theory]
    [InlineData("Fn")]
    [InlineData("Space")]
    [InlineData("CapsLock")]
    public void An_unknown_or_refused_key_is_refused(string name)
    {
        Assert.Throws<ArgumentException>(() => new ChordDetector(["RightCtrl", name]));
    }

    [Fact]
    public void Null_arguments_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => new ChordDetector(null!));
        Assert.Throws<ArgumentNullException>(() => DefaultShortcut().Forget(null!));
    }

    [Theory]
    [InlineData(LeftCtrl, RightCtrl)]
    [InlineData(RightCtrl, LeftCtrl)]
    public void Overlapping_key_names_start_whichever_key_comes_first(int first, int second)
    {
        // QA-07: ["Ctrl", "RightCtrl"] means both Control keys. Right Ctrl
        // pressed first used to take the generic "Ctrl" slot, and Left Ctrl
        // then fitted nowhere: the shortcut worked in one order only.
        var detector = new ChordDetector(["Ctrl", "RightCtrl"]);

        Assert.Equal(ChordAction.None, detector.OnKeyDown(first));
        Assert.Equal(ChordAction.Start, detector.OnKeyDown(second));
        Assert.Equal(ChordAction.Stop, detector.OnKeyUp(first));
    }

    [Fact]
    public void A_key_moved_to_another_slot_is_still_released_as_itself()
    {
        // Moving Right Ctrl to the slot that wants it exactly must not lose
        // track of it: releasing either key ends the dictation, and the next
        // press starts again.
        var detector = new ChordDetector(["Ctrl", "RightCtrl"]);

        detector.OnKeyDown(RightCtrl);
        detector.OnKeyDown(LeftCtrl);

        Assert.Equal(ChordAction.Stop, detector.OnKeyUp(RightCtrl));
        Assert.Equal(ChordAction.None, detector.OnKeyUp(LeftCtrl));
        Assert.False(detector.IsAnyHeld);

        detector.OnKeyDown(LeftCtrl);
        Assert.Equal(ChordAction.Start, detector.OnKeyDown(RightCtrl));
    }

    [Fact]
    public void A_key_no_slot_can_take_even_after_moving_is_still_foreign()
    {
        // Both slots full with the two Control keys: a letter is foreign and
        // cancels, as always.
        ChordDetector detector = Started(new ChordDetector(["Ctrl", "RightCtrl"]), RightCtrl, LeftCtrl);

        Assert.Equal(ChordAction.Cancel, detector.OnKeyDown(KeyC));
    }
}
