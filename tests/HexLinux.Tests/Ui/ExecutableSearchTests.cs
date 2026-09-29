using HexLinux.Ui;
using Xunit;

namespace HexLinux.Tests.Ui;

public class ExecutableSearchTests
{
    private static Func<string, bool> Executables(params string[] paths) =>
        candidate => paths.Contains(candidate, StringComparer.Ordinal);

    [Fact]
    public void The_first_directory_of_the_path_that_has_the_command_wins()
    {
        // The order a shell would follow.
        string? found = ExecutableSearch.Find(
            "xdg-open",
            "/usr/local/bin:/usr/bin:/bin",
            Executables("/usr/bin/xdg-open", "/bin/xdg-open"));

        Assert.Equal("/usr/bin/xdg-open", found);
    }

    [Fact]
    public void Empty_and_relative_entries_are_skipped()
    {
        // POSIX reads an empty entry as the current directory: a daemon must
        // not run whatever xdg-open sits where it was started.
        bool askedOutsideAbsolute = false;

        string? found = ExecutableSearch.Find(
            "xdg-open",
            ":bin:.:/usr/bin",
            candidate =>
            {
                askedOutsideAbsolute |= !candidate.StartsWith('/');
                return candidate == "/usr/bin/xdg-open";
            });

        Assert.Equal("/usr/bin/xdg-open", found);
        Assert.False(askedOutsideAbsolute);
    }

    [Fact]
    public void A_missing_command_is_reported_as_null()
    {
        // WSL has no xdg-open: the surface then tells the user which package
        // to install.
        Assert.Null(ExecutableSearch.Find("xdg-open", "/usr/bin:/bin", Executables()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Without_a_path_nothing_is_found(string? path)
    {
        Assert.Null(ExecutableSearch.Find("notify-send", path, _ => true));
    }

    [Fact]
    public void A_path_instead_of_a_name_is_refused()
    {
        Assert.Throws<ArgumentException>(() => ExecutableSearch.Find("/usr/bin/xdg-open", "/usr/bin", _ => true));
    }
}
