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
    [InlineData("F1", 59)]
    [InlineData("F2", 60)]
    [InlineData("F5", 63)]
    [InlineData("F10", 68)]
    [InlineData("F11", 87)]
    [InlineData("F12", 88)]
    [InlineData("F13", 183)]
    [InlineData("F14", 184)]
    [InlineData("F20", 190)]
    [InlineData("F24", 194)]
    [InlineData("Pause", 119)]
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
    [InlineData("F0")]
    [InlineData("F01")]
    [InlineData("F25")]
    [InlineData("Fx13")]
    [InlineData("Escape")]
    [InlineData("Insert")]
    [InlineData("ScrollLock")]
    [InlineData("NumLock")]
    [InlineData("Numpad0")]
    [InlineData("MediaPlayPause")]
    [InlineData("VK_E8")]
    public void Any_other_name_is_refused(string name)
    {
        // Fn emits no code the kernel can see; a letter or a numpad digit
        // would be typed, Insert and the locks toggle a state, a media key is
        // acted on by the desktop — nothing is withheld on Linux. HexWin
        // accepts them all since its pull request #68.
        Assert.Throws<ArgumentException>(() => LinuxKeys.Resolve(name));
    }

    [Theory]
    [InlineData("f5", "F5")]
    [InlineData(" PAUSE ", "Pause")]
    [InlineData("rightsuper", "RightSuper")]
    [InlineData("ctrl", "Ctrl")]
    public void A_name_is_brought_back_to_its_written_form(string written, string canonical)
    {
        // settings.json and every message then spell the key the same way.
        Assert.Equal(canonical, LinuxKeys.Canonical(written));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Win")]
    [InlineData("A")]
    public void A_name_that_is_not_a_shortcut_key_has_no_written_form(string? written)
    {
        // "Win" is the settings' alias for Super, resolved before this.
        Assert.Null(LinuxKeys.Canonical(written));
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
    [InlineData(59, "F1")]
    [InlineData(64, "F6")]
    [InlineData(68, "F10")]
    [InlineData(87, "F11")]
    [InlineData(88, "F12")]
    [InlineData(119, "Pause")]
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
    [InlineData(58)] // KEY_CAPSLOCK, just before F1
    [InlineData(14)] // KEY_BACKSPACE
    [InlineData(69)] // KEY_NUMLOCK, between F10 and F11
    [InlineData(70)] // KEY_SCROLLLOCK
    [InlineData(82)] // KEY_KP0
    [InlineData(96)] // KEY_KPENTER
    [InlineData(110)] // KEY_INSERT
    [InlineData(113)] // KEY_MUTE
    [InlineData(164)] // KEY_PLAYPAUSE
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
    public void The_shortcut_codes_are_the_modifiers_the_function_keys_and_Pause()
    {
        HashSet<int> expected =
        [
            29, 97, 42, 54, 56, 100, 125, 126,
            .. Enumerable.Range(59, 10), 87, 88, .. Enumerable.Range(183, 12),
            119,
        ];

        Assert.Equal(expected, LinuxKeys.ShortcutCodes.ToHashSet());
    }

    [Fact]
    public void Every_shortcut_code_has_a_name_that_resolves_back_to_it()
    {
        // --watch-hotkey must name every key settings.json accepts, and the
        // name it prints must give that key back.
        foreach (int code in LinuxKeys.ShortcutCodes)
        {
            string? name = LinuxKeys.NameOf(code);

            Assert.NotNull(name);
            Assert.Equal([code], LinuxKeys.Resolve(name));
        }
    }

    [Fact]
    public void The_keys_HexLinux_presses_are_never_shortcut_keys()
    {
        // KEY_V and KEY_INSERT are sent by HexLinux itself to paste, and
        // KEY_BACKSPACE to carry out a spoken "efface ça"; taken for part of a
        // shortcut, they would start or cancel a dictation.
        Assert.Equal(47, LinuxKeys.V);
        Assert.Equal(110, LinuxKeys.Insert);
        Assert.Equal(14, LinuxKeys.Backspace);

        foreach (int code in new[] { LinuxKeys.V, LinuxKeys.Insert, LinuxKeys.Backspace })
        {
            Assert.DoesNotContain(code, LinuxKeys.ShortcutCodes);
            Assert.Null(LinuxKeys.NameOf(code));
        }
    }
}
