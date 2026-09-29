using HexLinux.Daemon;
using HexLinux.Ui;
using Xunit;

namespace HexLinux.Tests.Ui;

public class TrayTextTests
{
    [Fact]
    public void The_ready_tooltip_names_the_shortcut()
    {
        // The line the user reads when hovering the icon, worded as HexWin's:
        // it is how someone who forgot the shortcut finds it again.
        Assert.Equal("HexLinux — ready (Right Ctrl)", TrayText.ToolTip(DictationState.Idle, "Right Ctrl"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Without_a_shortcut_to_name_no_empty_parenthesis_is_shown(string? hotkey)
    {
        // Before the daemon's first snapshot, the description is empty.
        Assert.Equal("HexLinux — ready", TrayText.ToolTip(DictationState.Idle, hotkey));
    }

    [Fact]
    public void The_shortcut_is_shown_without_its_surrounding_spaces()
    {
        Assert.Equal("HexLinux — ready (Left Ctrl + Left Super)", TrayText.ToolTip(DictationState.Idle, " Left Ctrl + Left Super "));
    }

    [Theory]
    [InlineData(DictationState.Loading, "HexLinux — loading the model...")]
    [InlineData(DictationState.Recording, "HexLinux — recording")]
    [InlineData(DictationState.Transcribing, "HexLinux — transcribing...")]
    [InlineData(DictationState.Failed, "HexLinux — the model could not be loaded")]
    public void Every_state_has_its_tooltip(DictationState state, string expected)
    {
        // Colour alone is not enough: a colour-blind user reads the tooltip.
        Assert.Equal(expected, TrayText.ToolTip(state, "Right Ctrl"));
    }

    [Fact]
    public void The_shortcut_only_appears_when_it_is_usable()
    {
        // While recording or loading, naming the shortcut would suggest that
        // pressing it now does something it does not.
        Assert.DoesNotContain("Right Ctrl", TrayText.ToolTip(DictationState.Recording, "Right Ctrl"), StringComparison.Ordinal);
        Assert.DoesNotContain("Right Ctrl", TrayText.ToolTip(DictationState.Loading, "Right Ctrl"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_cannot_open_message_starts_with_the_path()
    {
        // In WSL there is no xdg-open: the path is what lets the user open the
        // file by hand, so it comes first.
        string body = TrayText.CannotOpenBody("/home/ada/.config/hexlinux/settings.json", TrayText.OpenerMissing);

        Assert.StartsWith("/home/ada/.config/hexlinux/settings.json\n", body, StringComparison.Ordinal);
        Assert.EndsWith(TrayText.OpenerMissing, body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_opener_is_reported_with_its_exit_code()
    {
        // xdg-open's exit codes tell a missing file from a missing application.
        Assert.Equal("xdg-open gave up (exit code 3).", TrayText.OpenerFailed(3));
    }

    [Fact]
    public void The_missing_opener_message_names_the_package()
    {
        Assert.Contains("xdg-utils", TrayText.OpenerMissing, StringComparison.Ordinal);
    }
}
