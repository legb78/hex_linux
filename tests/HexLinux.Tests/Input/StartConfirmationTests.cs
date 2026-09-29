using HexLinux.Input;
using Xunit;

namespace HexLinux.Tests.Input;

/// <summary>
/// Right Ctrl reaches the desktop, so Right Ctrl+C is still a copy. If the
/// press alone started the dictation, every copy made with the right Ctrl
/// would open the microphone and play two tones. The press only arms; the
/// dictation is confirmed once the shortcut has been held for the minimum
/// recording time. These cases pin down that boundary, and the one race that
/// matters: the deferred confirmation of a press already abandoned must never
/// confirm the next one early.
/// </summary>
public class StartConfirmationTests
{
    /// <summary>The default minRecordingMilliseconds.</summary>
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(250);

    private static TimeSpan At(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    [Fact]
    public void A_negative_delay_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StartConfirmation(TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public void The_delay_is_kept_as_given()
    {
        Assert.Equal(Delay, new StartConfirmation(Delay).Delay);
    }

    [Fact]
    public void Nothing_is_armed_before_the_first_press()
    {
        var confirmation = new StartConfirmation(Delay);

        Assert.False(confirmation.IsArmed);
        Assert.False(confirmation.Disarm());
    }

    [Fact]
    public void The_press_arms_without_confirming()
    {
        // The microphone is open from here, but no tone and no state yet.
        var confirmation = new StartConfirmation(Delay);

        int arming = confirmation.Arm(At(1_000));

        Assert.True(confirmation.IsArmed);
        Assert.False(confirmation.TryConfirm(arming, At(1_000)));
        Assert.False(confirmation.TryConfirm(arming, At(1_249)));
        Assert.True(confirmation.IsArmed);
    }

    [Fact]
    public void Holding_for_the_delay_confirms_the_dictation()
    {
        var confirmation = new StartConfirmation(Delay);
        int arming = confirmation.Arm(At(1_000));

        Assert.True(confirmation.TryConfirm(arming, At(1_250)));
        Assert.False(confirmation.IsArmed);
    }

    [Fact]
    public void A_dictation_is_confirmed_only_once()
    {
        // A second confirmation would play the start tone twice.
        var confirmation = new StartConfirmation(Delay);
        int arming = confirmation.Arm(At(0));
        confirmation.TryConfirm(arming, At(300));

        Assert.False(confirmation.TryConfirm(arming, At(400)));
    }

    [Fact]
    public void A_release_before_the_delay_disarms_in_silence()
    {
        // Right Ctrl tapped, or used for a copy: Disarm says so, and the
        // confirmation waking up afterwards finds nothing to confirm.
        var confirmation = new StartConfirmation(Delay);
        int arming = confirmation.Arm(At(0));

        Assert.True(confirmation.Disarm());
        Assert.False(confirmation.IsArmed);
        Assert.False(confirmation.TryConfirm(arming, At(250)));
    }

    [Fact]
    public void A_release_after_the_confirmation_is_the_end_of_a_real_dictation()
    {
        // Disarm returning false is how the stop tells a dictation, to be
        // transcribed, from a shortcut, to be dropped.
        var confirmation = new StartConfirmation(Delay);
        int arming = confirmation.Arm(At(0));
        confirmation.TryConfirm(arming, At(250));

        Assert.False(confirmation.Disarm());
    }

    [Fact]
    public void The_confirmation_of_an_abandoned_press_does_not_confirm_the_next_one()
    {
        // Right Ctrl+C at 0 ms, then Right Ctrl pressed again at 200 ms to
        // dictate. The first press's confirmation wakes at 250 ms: confirming
        // then would start the dictation after 50 ms instead of 250, and a
        // quick copy right after would play both tones.
        var confirmation = new StartConfirmation(Delay);
        int first = confirmation.Arm(At(0));
        confirmation.Disarm();
        int second = confirmation.Arm(At(200));

        Assert.NotEqual(first, second);
        Assert.False(confirmation.TryConfirm(first, At(250)));
        Assert.True(confirmation.IsArmed);
        Assert.False(confirmation.TryConfirm(second, At(250)));
        Assert.True(confirmation.TryConfirm(second, At(450)));
    }

    [Fact]
    public void Every_arming_gets_a_new_number()
    {
        var confirmation = new StartConfirmation(Delay);

        int first = confirmation.Arm(At(0));
        int second = confirmation.Arm(At(10));
        int third = confirmation.Arm(At(20));

        Assert.True(first < second && second < third);
    }

    [Fact]
    public void Remaining_counts_down_to_zero_and_never_below()
    {
        // What the daemon waits for before trying to confirm.
        var confirmation = new StartConfirmation(Delay);
        confirmation.Arm(At(1_000));

        Assert.Equal(At(250), confirmation.Remaining(At(1_000)));
        Assert.Equal(At(100), confirmation.Remaining(At(1_150)));
        Assert.Equal(TimeSpan.Zero, confirmation.Remaining(At(1_250)));
        Assert.Equal(TimeSpan.Zero, confirmation.Remaining(At(5_000)));
    }

    [Fact]
    public void A_zero_delay_confirms_at_once()
    {
        // minRecordingMilliseconds set to 0: the press is the dictation, as in
        // HexWin.
        var confirmation = new StartConfirmation(TimeSpan.Zero);
        int arming = confirmation.Arm(At(42));

        Assert.Equal(TimeSpan.Zero, confirmation.Remaining(At(42)));
        Assert.True(confirmation.TryConfirm(arming, At(42)));
    }

    [Fact]
    public void Right_Ctrl_C_never_becomes_a_dictation()
    {
        // The whole reason for arming, end to end with the detector: the copy
        // cancels before the delay, and the cancellation is a silent one.
        var detector = new ChordDetector(["RightCtrl"]);
        var confirmation = new StartConfirmation(Delay);

        Assert.Equal(ChordAction.Start, detector.OnKeyDown(LinuxKeys.RightCtrl));
        int arming = confirmation.Arm(At(0));

        // KEY_C, 80 ms later.
        Assert.Equal(ChordAction.Cancel, detector.OnKeyDown(46));
        Assert.True(confirmation.Disarm());
        Assert.False(confirmation.TryConfirm(arming, At(250)));
    }
}
