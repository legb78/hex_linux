using HexLinux.Input;
using HexLinux.Output;
using Xunit;

namespace HexLinux.Tests.Output;

/// <summary>
/// A spoken "efface ça" becomes Backspaces, which must never go out with a
/// modifier held: with Ctrl each one erases a word, and the count would reach
/// text the dictation never typed.
/// </summary>
public class EraseGuardTests
{
    private static readonly TimeSpan Zero = TimeSpan.Zero;

    [Fact]
    public void Nothing_held_erases_at_once()
    {
        Assert.Equal(EraseDecision.Erase, EraseGuard.Decide(new HashSet<int>(), recording: true, Zero));
    }

    [Fact]
    public void A_held_letter_does_not_hold_the_erase_back()
    {
        // Only a modifier changes what Backspace does.
        Assert.Equal(EraseDecision.Erase, EraseGuard.Decide(new HashSet<int> { 30 }, recording: false, Zero));
    }

    [Fact]
    public void The_shortcut_held_during_the_dictation_makes_it_wait_as_long_as_it_lasts()
    {
        // Segmentation on, Right Ctrl held: the erase comes mid-dictation, and
        // Ctrl+Backspace would erase words. It waits for the release, however
        // long the user keeps talking.
        var held = new HashSet<int> { LinuxKeys.RightCtrl };

        Assert.Equal(EraseDecision.Wait, EraseGuard.Decide(held, recording: true, Zero));
        Assert.Equal(EraseDecision.Wait, EraseGuard.Decide(held, recording: true, TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void After_the_dictation_a_modifier_gets_a_second_to_be_let_go()
    {
        // A two-key shortcut released one key at a time.
        var held = new HashSet<int> { LinuxKeys.LeftSuper };

        Assert.Equal(EraseDecision.Wait, EraseGuard.Decide(held, recording: false, TimeSpan.FromMilliseconds(900)));
        Assert.Equal(EraseDecision.GiveUp, EraseGuard.Decide(held, recording: false, TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(LinuxKeys.LeftCtrl)]
    [InlineData(LinuxKeys.RightAlt)]
    [InlineData(LinuxKeys.LeftShift)]
    [InlineData(LinuxKeys.RightSuper)]
    public void Every_modifier_holds_the_erase_back(int modifier)
    {
        // Alt+Backspace erases a word in terminals (readline), and Super may
        // be a desktop shortcut.
        Assert.Equal(EraseDecision.GiveUp, EraseGuard.Decide(new HashSet<int> { modifier }, recording: false, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void The_held_keys_are_required()
    {
        Assert.Throws<ArgumentNullException>(() => EraseGuard.Decide(null!, recording: false, Zero));
    }
}
