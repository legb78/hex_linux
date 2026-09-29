using HexLinux.Input;
using Xunit;

namespace HexLinux.Tests.Input;

/// <summary>
/// HexWin accepts any key as the shortcut since its pull request #68; on
/// Linux the key still reaches the desktop, so most are refused. The reason
/// is what --doctor shows a user whose settings.json came from Windows.
/// </summary>
public class RefusedKeysTests
{
    [Theory]
    [InlineData("RightCtrl")]
    [InlineData("F5")]
    [InlineData("Pause")]
    [InlineData("F24")]
    public void A_key_Linux_accepts_is_not_refused(string key)
    {
        Assert.Null(RefusedKeys.Reason(key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Fn")]
    [InlineData("Banana")]
    public void A_name_that_is_no_key_at_all_has_no_reason_of_its_own(string? key)
    {
        // The settings then say "not a key a shortcut can use".
        Assert.Null(RefusedKeys.Reason(key));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("Z")]
    [InlineData("0")]
    [InlineData("Numpad9")]
    [InlineData("NumpadAdd")]
    [InlineData("Oem102")]
    [InlineData("Tab")]
    [InlineData("Delete")]
    [InlineData("Clear")]
    public void A_key_that_types_or_edits_is_refused_for_that_reason(string key)
    {
        // Held for a whole dictation, a letter types itself then repeats.
        Assert.StartsWith("it would type or delete text while held", RefusedKeys.Reason(key), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Left")]
    [InlineData("Right")]
    [InlineData("Down")]
    [InlineData("Home")]
    [InlineData("End")]
    [InlineData("PageUp")]
    public void A_key_that_moves_the_cursor_is_refused_for_that_reason(string key)
    {
        // The text would land where the repeated key left the caret.
        Assert.StartsWith("it would move the cursor", RefusedKeys.Reason(key), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MediaNext")]
    [InlineData("VolumeMute")]
    [InlineData("BrowserBack")]
    [InlineData("LaunchMail")]
    public void A_key_the_desktop_acts_on_is_refused_for_that_reason(string key)
    {
        Assert.StartsWith("the desktop acts on it itself", RefusedKeys.Reason(key), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("VK_41")]
    [InlineData("vk_e8")]
    [InlineData("VK_8")]
    public void A_windows_virtual_key_code_is_named_as_such(string key)
    {
        Assert.StartsWith("a Windows virtual-key code", RefusedKeys.Reason(key), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Sleep", "the desktop puts the computer to sleep")]
    [InlineData("Apps", "it would open the context menu at every press")]
    [InlineData(" capslock ", "it would toggle capitals at every press")]
    public void The_named_refusals_say_what_the_key_would_do(string key, string start)
    {
        Assert.StartsWith(start, RefusedKeys.Reason(key), StringComparison.Ordinal);
    }
}
