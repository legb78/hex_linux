using HexLinux.Platform;
using Xunit;

namespace HexLinux.Tests.Platform;

/// <summary>
/// The PATH lookup behind every tool HexLinux runs. The tools are started by
/// the full path found here, so this is where a daemon could be tricked into
/// running the wrong program: an empty or relative PATH entry means "the
/// current directory" to a shell, and a daemon started from a downloads
/// folder has no business running whatever "xclip" sits there.
/// </summary>
public class ToolLocatorTests
{
    private static Func<string, bool> Only(params string[] executables)
    {
        HashSet<string> present = new(executables, StringComparer.Ordinal);

        return present.Contains;
    }

    [Fact]
    public void The_first_directory_of_the_PATH_that_holds_the_tool_wins()
    {
        // The shell's own rule: a tool installed in /usr/local/bin shadows
        // the distribution's.
        string? found = ToolLocator.Find(
            ToolLocator.Xclip,
            "/usr/local/bin:/usr/bin:/bin",
            Only("/usr/bin/xclip", "/usr/local/bin/xclip"));

        Assert.Equal("/usr/local/bin/xclip", found);
    }

    [Fact]
    public void A_tool_further_down_the_PATH_is_found()
    {
        Assert.Equal("/bin/loginctl", ToolLocator.Find(ToolLocator.Loginctl, "/usr/local/bin:/usr/bin:/bin", Only("/bin/loginctl")));
    }

    [Fact]
    public void A_trailing_slash_on_a_directory_is_harmless()
    {
        Assert.Equal("/usr/bin/xdotool", ToolLocator.Find(ToolLocator.Xdotool, "/usr/bin/", Only("/usr/bin/xdotool")));
    }

    [Fact]
    public void A_tool_absent_from_every_directory_is_not_found()
    {
        Assert.Null(ToolLocator.Find(ToolLocator.Wtype, "/usr/local/bin:/usr/bin", Only("/usr/bin/xclip")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Without_a_PATH_nothing_is_found(string? path)
    {
        // A systemd unit started with a cleared environment.
        Assert.Null(ToolLocator.Find(ToolLocator.Xclip, path, _ => true));
    }

    [Theory]
    [InlineData(":/usr/bin")]
    [InlineData("/usr/bin:")]
    [InlineData("/usr/bin::/bin")]
    [InlineData(".:/usr/bin")]
    [InlineData("bin:/usr/bin")]
    [InlineData("./bin:/usr/bin")]
    public void Empty_and_relative_entries_are_never_looked_into(string path)
    {
        // POSIX reads an empty entry as the current directory; a relative one
        // depends on it too. Only absolute directories are ever probed.
        List<string> probed = [];

        string? found = ToolLocator.Find(
            ToolLocator.Xclip,
            path,
            candidate =>
            {
                probed.Add(candidate);
                return candidate == "/usr/bin/xclip";
            });

        Assert.Equal("/usr/bin/xclip", found);
        Assert.All(probed, candidate => Assert.StartsWith("/", candidate, StringComparison.Ordinal));
    }

    [Fact]
    public void A_PATH_of_only_relative_entries_finds_nothing_even_if_the_tool_is_there()
    {
        List<string> probed = [];

        string? found = ToolLocator.Find(
            ToolLocator.Xclip,
            ".:bin::tools",
            candidate =>
            {
                probed.Add(candidate);
                return true;
            });

        Assert.Null(found);
        Assert.Empty(probed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void A_blank_tool_name_is_refused(string tool)
    {
        Assert.Throws<ArgumentException>(() => ToolLocator.Find(tool, "/usr/bin", _ => true));
    }

    [Fact]
    public void Missing_arguments_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => ToolLocator.Find(null!, "/usr/bin", _ => true));
        Assert.Throws<ArgumentNullException>(() => ToolLocator.Find(ToolLocator.Xclip, "/usr/bin", null!));
    }

    // --- The known tools ------------------------------------------------------------

    [Fact]
    public void The_known_tools_are_listed_once_each()
    {
        Assert.Equal(ToolLocator.Known.Count, ToolLocator.Known.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_tool_the_planner_and_the_doctor_rely_on_is_looked_for()
    {
        // A tool missing from this list is never located: the planner would
        // report it missing on a machine where it is installed.
        string[] needed =
        [
            ToolLocator.WlCopy, ToolLocator.WlPaste, ToolLocator.Xclip, ToolLocator.Xsel, ToolLocator.Xdotool, ToolLocator.Wtype,
            ToolLocator.NotifySend, ToolLocator.Loginctl, ToolLocator.XdgOpen, ToolLocator.Localectl, ToolLocator.Gsettings,
        ];

        Assert.All(needed, tool => Assert.Contains(tool, ToolLocator.Known));
    }

    [Fact]
    public void Ydotool_is_not_looked_for()
    {
        // Decision of the brief: ydotool is not supported in V1 (Ubuntu ships
        // 0.1.8, whose syntax differs from 1.x); the uinput sender covers it.
        Assert.DoesNotContain("ydotool", ToolLocator.Known);
    }

    [Fact]
    public void The_tools_found_on_this_machine_are_known_ones_given_by_absolute_path()
    {
        // Locate reads the real PATH and only looks at files: whatever it
        // returns must be a known tool, found where the lookup says, so that
        // the program started later is the one that was checked.
        IReadOnlyDictionary<string, string> found = ToolLocator.Locate();

        foreach ((string tool, string location) in found)
        {
            Assert.Contains(tool, ToolLocator.Known);
            Assert.True(Path.IsPathRooted(location), location);
            Assert.Equal(tool, Path.GetFileName(location));
            Assert.True(File.Exists(location), location);
            Assert.NotEqual(UnixFileMode.None, File.GetUnixFileMode(location) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute));
        }
    }
}
