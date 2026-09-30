using HexLinux.Ui;
using Xunit;

namespace HexLinux.Tests.Ui;

public class OpenCommandTests
{
    [Fact]
    public void An_absolute_path_can_be_opened()
    {
        Assert.True(OpenCommand.CanOpen("/home/ada/.config/hexlinux/settings.json"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("settings.json")]
    [InlineData("./settings.json")]
    [InlineData("-rf")]
    [InlineData("--manual")]
    [InlineData("/tmp/a\0b")]
    public void Anything_else_is_refused(string? path)
    {
        // A relative path depends on where the daemon runs; one that starts
        // with a dash would be read by xdg-open as an option; a NUL byte
        // cannot travel in an argument at all.
        Assert.False(OpenCommand.CanOpen(path));
    }

    [Fact]
    public void The_path_travels_as_one_untouched_argument()
    {
        // No shell reads it: spaces, quotes and dollars are not interpreted.
        const string path = "/home/ada/My \"notes\"/$HOME; rm -rf ~";

        Assert.Equal([path], OpenCommand.Arguments(path));
    }

    [Fact]
    public void Asking_for_the_arguments_of_a_refused_path_throws()
    {
        // The check cannot be skipped by a caller.
        Assert.Throws<ArgumentException>(() => OpenCommand.Arguments("settings.json"));
    }
}
