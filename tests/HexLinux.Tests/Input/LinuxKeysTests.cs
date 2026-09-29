using HexLinux.Input;
using Xunit;

namespace HexLinux.Tests.Input;

/// <summary>
/// <see cref="LinuxKeys"/> is where a name in settings.json becomes the number
/// the kernel reports. The numbers are written out literally below, taken
/// from <c>linux/input-event-codes.h</c>: a constant mistyped in the class
/// would otherwise be checked against itself. The class also decides which
/// keys can be a shortcut at all, and which ones <c>--watch-hotkey</c> may
/// name.
/// </summary>
public class LinuxKeysTests
{
    [Theory]
    [InlineData("LeftCtrl", 29)]
    [InlineData("RightCtrl", 97)]
    [InlineData("LeftShift", 42)]
    [InlineData("RightShift", 54)]
    [InlineData("LeftAlt", 56)]
    [InlineData("RightAlt", 100)]
    [InlineData("LeftSuper", 125)]
    [InlineData("RightSuper", 126)]
    [InlineData("F13", 183)]
    [InlineData("F14", 184)]
    [InlineData("F20", 190)]
    [InlineData("F24", 194)]
    public void A_sided_name_resolves_to_the_kernel_code_of_that_key(string name, int code)
    {
        Assert.Equal([code], LinuxKeys.Resolve(name));
    }

    [Theory]
    [InlineData("Ctrl", 29, 97)]
    [InlineData("Shift", 42, 54)]
    [InlineData("Alt", 56, 100)]
    [InlineData("Super", 125, 126)]
    public void A_generic_name_accepts_both_sides(string name, int left, int right)
    {
        // The kernel never reports a generic Ctrl: it always sends the left or
        // the right key.
        Assert.Equal([left, right], LinuxKeys.Resolve(name));
    }

    [Theory]
    [InlineData("rightctrl")]
    [InlineData("RIGHTCTRL")]
    [InlineData("  RightCtrl ")]
    public void Names_ignore_case_and_surrounding_spaces(string name)
    {
        // A hand-edited settings.json is not always typed with care.
        Assert.Equal([97], LinuxKeys.Resolve(name));
    }

    [Fact]
    public void A_lower_case_function_key_is_accepted()
    {
        Assert.Equal([183], LinuxKeys.Resolve("f13"));
    }

    [Theory]
    [InlineData("Space")]
    [InlineData("CapsLock")]
    public void Space_and_CapsLock_are_not_shortcut_keys(string name)
    {
        // Accepted by HexWin, which withholds the shortcut's keys. Nothing is
        // withheld on Linux: Space would type spaces for the whole dictation,
        // CapsLock would toggle capitals at every press.
        Assert.Throws<ArgumentException>(() => LinuxKeys.Resolve(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Fn")]
    [InlineData("A")]
    [InlineData("F")]
    [InlineData("F1")]
    [InlineData("F12")]
    [InlineData("F25")]
    [InlineData("Fx13")]
    [InlineData("Escape")]
    public void Any_other_name_is_refused(string name)
    {
        // F1 to F12 are on every keyboard and bound everywhere; Fn emits no
        // code the kernel can see; a letter would be typed.
        Assert.Throws<ArgumentException>(() => LinuxKeys.Resolve(name));
    }

    [Fact]
    public void A_null_name_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => LinuxKeys.Resolve(null!));
    }

    [Theory]
    [InlineData(29, "LeftCtrl")]
    [InlineData(97, "RightCtrl")]
    [InlineData(42, "LeftShift")]
    [InlineData(54, "RightShift")]
    [InlineData(56, "LeftAlt")]
    [InlineData(100, "RightAlt")]
    [InlineData(125, "LeftSuper")]
    [InlineData(126, "RightSuper")]
    [InlineData(183, "F13")]
    [InlineData(189, "F19")]
    [InlineData(194, "F24")]
    public void A_shortcut_key_is_named_with_its_side(int code, string expected)
    {
        // What --watch-hotkey prints, to be copied into settings.json as is.
        Assert.Equal(expected, LinuxKeys.NameOf(code));
    }

    [Theory]
    [InlineData(30)] // KEY_A
    [InlineData(3)] // KEY_2
    [InlineData(1)] // KEY_ESC
    [InlineData(28)] // KEY_ENTER
    [InlineData(57)] // KEY_SPACE
    [InlineData(58)] // KEY_CAPSLOCK
    [InlineData(88)] // KEY_F12
    [InlineData(116)] // KEY_POWER
    [InlineData(182)] // just before F13
    [InlineData(195)] // just past F24
    [InlineData(0)]
    public void A_key_that_cannot_be_a_shortcut_has_no_name(int code)
    {
        // --watch-hotkey must never be a way to watch what is being typed:
        // letters, digits and every other key show nothing.
        Assert.Null(LinuxKeys.NameOf(code));
    }

    [Theory]
    [InlineData(29)]
    [InlineData(100)]
    [InlineData(126)]
    [InlineData(185)]
    public void The_name_resolves_back_to_that_key_alone(int code)
    {
        Assert.Equal([code], LinuxKeys.Resolve(LinuxKeys.NameOf(code)!));
    }

    [Fact]
    public void The_modifiers_are_the_eight_sided_keys()
    {
        // Any of them held during a paste changes what Ctrl+V means.
        Assert.Equal(
            new HashSet<int> { 29, 97, 42, 54, 56, 100, 125, 126 },
            LinuxKeys.Modifiers.ToHashSet());
    }

    [Fact]
    public void The_shortcut_codes_are_the_modifiers_and_F13_to_F24()
    {
        HashSet<int> expected = [29, 97, 42, 54, 56, 100, 125, 126, .. Enumerable.Range(183, 12)];

        Assert.Equal(expected, LinuxKeys.ShortcutCodes.ToHashSet());
    }

    [Fact]
    public void The_paste_keys_are_never_shortcut_keys()
    {
        // KEY_V and KEY_INSERT are sent by HexLinux itself to paste; taken for
        // part of a shortcut, the paste would start or cancel a dictation.
        Assert.Equal(47, LinuxKeys.V);
        Assert.Equal(110, LinuxKeys.Insert);
        Assert.DoesNotContain(LinuxKeys.V, LinuxKeys.ShortcutCodes);
        Assert.DoesNotContain(LinuxKeys.Insert, LinuxKeys.ShortcutCodes);
        Assert.Null(LinuxKeys.NameOf(LinuxKeys.V));
        Assert.Null(LinuxKeys.NameOf(LinuxKeys.Insert));
    }
}
