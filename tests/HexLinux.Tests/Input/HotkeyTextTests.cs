using HexLinux.Input;
using Xunit;

namespace HexLinux.Tests.Input;

/// <summary>
/// What the tray tooltip, <c>--doctor</c> and <c>--watch-hotkey</c> say about
/// the shortcut. The side must be spelled out — telling the left Ctrl from the
/// right one is the whole point of a one-key shortcut — and every shortcut
/// that costs something on Linux, where the desktop still sees its keys, must
/// say so before the user finds out by losing dictations. The default must
/// cost nothing worth saying.
/// </summary>
public class HotkeyTextTests
{
    [Theory]
    [InlineData("RightCtrl", "Right Ctrl")]
    [InlineData("LeftCtrl", "Left Ctrl")]
    [InlineData("Ctrl", "Ctrl")]
    [InlineData("LeftAlt", "Left Alt")]
    [InlineData("RightAlt", "Right Alt")]
    [InlineData("Alt", "Alt")]
    [InlineData("LeftShift", "Left Shift")]
    [InlineData("RightShift", "Right Shift")]
    [InlineData("Shift", "Shift")]
    [InlineData("LeftSuper", "Left Super")]
    [InlineData("RightSuper", "Right Super")]
    [InlineData("Super", "Super")]
    public void A_key_is_shown_as_printed_on_the_keyboard_with_its_side(string key, string expected)
    {
        Assert.Equal(expected, HotkeyText.Describe(key));
    }

    [Theory]
    [InlineData("rightctrl", "Right Ctrl")]
    [InlineData("  RightCtrl  ", "Right Ctrl")]
    [InlineData("F13", "F13")]
    [InlineData(" F24 ", "F24")]
    [InlineData("SomethingElse", "SomethingElse")]
    public void Other_spellings_and_names_are_shown_as_written(string key, string expected)
    {
        // Function keys need no translation; a name not known is shown rather
        // than hidden, so that a diagnostic still says what was configured.
        Assert.Equal(expected, HotkeyText.Describe(key));
    }

    [Fact]
    public void A_chord_is_joined_the_way_it_is_written()
    {
        Assert.Equal("Left Ctrl + Left Super", HotkeyText.Describe(["LeftCtrl", "LeftSuper"]));
        Assert.Equal("Right Ctrl", HotkeyText.Describe(["RightCtrl"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_default_shortcut_costs_nothing_worth_saying(bool segmentation)
    {
        // Right Ctrl: types nothing, has no use of its own on most desktops,
        // and Ctrl+V stays Ctrl+V while it is held.
        Assert.Empty(HotkeyText.Caveats(["RightCtrl"], segmentation));
    }

    [Theory]
    [InlineData("F13")]
    [InlineData("F24")]
    public void A_function_key_beyond_F12_costs_nothing(string key)
    {
        // The macro pad bought precisely to dictate with F13.
        Assert.Empty(HotkeyText.Caveats([key], segmentation: true));
    }

    [Theory]
    [InlineData("Shift", "Shift is also used for capitals")]
    [InlineData("LeftShift", "Left Shift is also used for capitals")]
    [InlineData("RightShift", "Right Shift is also used for capitals")]
    [InlineData("Ctrl", "Ctrl is also used for shortcuts such as Ctrl+C")]
    [InlineData("LeftCtrl", "Left Ctrl is also used for shortcuts such as Ctrl+C")]
    [InlineData("Alt", "Alt still reaches the desktop")]
    [InlineData("LeftAlt", "Left Alt still reaches the desktop")]
    [InlineData("Super", "Super still reaches the desktop")]
    [InlineData("LeftSuper", "Left Super still reaches the desktop")]
    [InlineData("RightSuper", "Right Super still reaches the desktop")]
    public void A_lone_modifier_with_a_use_of_its_own_says_what_it_costs(string key, string start)
    {
        // Each of these is pressed alone or before a letter many times a day:
        // the user must learn from the diagnostic, not from lost dictations.
        IReadOnlyList<string> caveats = HotkeyText.Caveats([key], segmentation: false);

        string caveat = Assert.Single(caveats);
        Assert.StartsWith(start, caveat, StringComparison.Ordinal);
    }

    [Fact]
    public void Right_Alt_is_flagged_as_AltGr()
    {
        // On a French or German layout, @ and the euro sign are typed with
        // Right Alt: each one would cancel the dictation in progress.
        string caveat = Assert.Single(HotkeyText.Caveats(["RightAlt"], segmentation: false));

        Assert.Contains("AltGr", caveat, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lone_modifier_is_recognised_whatever_its_spelling()
    {
        string caveat = Assert.Single(HotkeyText.Caveats([" leftshift "], segmentation: false));

        Assert.StartsWith("Left Shift is also used for capitals", caveat, StringComparison.Ordinal);
    }

    [Fact]
    public void A_chord_of_modifiers_has_no_lone_key_caveat()
    {
        // Ctrl+Super is not pressed by accident before a letter.
        Assert.Empty(HotkeyText.Caveats(["LeftCtrl", "LeftSuper"], segmentation: false));
    }

    [Theory]
    [InlineData("LeftShift")]
    [InlineData("Alt")]
    [InlineData("RightSuper")]
    public void Segmentation_with_Shift_Alt_or_Super_in_the_shortcut_warns_about_the_paste(string key)
    {
        // Segments are pasted while the shortcut is still held: Ctrl+V sent
        // under Shift is Ctrl+Shift+V, another shortcut altogether.
        IReadOnlyList<string> caveats = HotkeyText.Caveats(["Ctrl", key], segmentation: true);

        string caveat = Assert.Single(caveats);
        Assert.Contains("segmentation", caveat, StringComparison.Ordinal);
        Assert.Contains("Ctrl+V", caveat, StringComparison.Ordinal);
    }

    [Fact]
    public void Segmentation_off_makes_the_paste_warning_moot()
    {
        // The paste then waits for the shortcut to be let go.
        Assert.Empty(HotkeyText.Caveats(["Ctrl", "Shift"], segmentation: false));
    }

    [Fact]
    public void Ctrl_alone_is_no_paste_hazard()
    {
        // Ctrl held with Ctrl+V is still Ctrl+V: only its lone-key caveat
        // applies.
        string caveat = Assert.Single(HotkeyText.Caveats(["LeftCtrl"], segmentation: true));

        Assert.DoesNotContain("segmentation", caveat, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lone_Shift_with_segmentation_gets_both_caveats()
    {
        IReadOnlyList<string> caveats = HotkeyText.Caveats(["RightShift"], segmentation: true);

        Assert.Equal(2, caveats.Count);
        Assert.StartsWith("Right Shift is also used for capitals", caveats[0], StringComparison.Ordinal);
        Assert.Contains("segmentation", caveats[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Null_arguments_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => HotkeyText.Describe((string)null!));
        Assert.Throws<ArgumentNullException>(() => HotkeyText.Describe((IEnumerable<string>)null!));
        Assert.Throws<ArgumentNullException>(() => HotkeyText.Caveats(null!, segmentation: false));
    }
}
