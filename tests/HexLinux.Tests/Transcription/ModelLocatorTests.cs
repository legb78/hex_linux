using HexLinux.Transcription;
using Xunit;

namespace HexLinux.Tests.Transcription;

/// <summary>
/// The search is tested without touching the disk: the existence predicate is
/// injected, which makes it possible to describe whole trees in one line and
/// to check the exact order in which locations are tried — an order that is a
/// security decision, since the model decides what text gets typed.
/// </summary>
public class ModelLocatorTests
{
    private const string BuildDirectory = "/home/ada/hex_linux/src/HexLinux/bin/Release/net10.0";
    private const string InstalledDirectory = "/opt/hexlinux";
    private const string DataDirectory = "/home/ada/.local/share/hexlinux";
    private const string Home = "/home/ada";

    private static Func<string, bool> ExistsOnly(params string[] paths) =>
        candidate => paths.Contains(candidate, StringComparer.Ordinal);

    private static string? Resolve(string configured, string baseDirectory, Func<string, bool> exists, string? data = DataDirectory) =>
        ModelLocator.Resolve(configured, baseDirectory, data, Home, exists);

    [Fact]
    public void The_data_folder_is_searched_first()
    {
        // Where get-model.sh puts the model, and a folder only the user can
        // write to: it must win over anything near the executable.
        const string data = DataDirectory + "/models/m";
        const string nearby = InstalledDirectory + "/models/m";

        Assert.Equal(data, Resolve("models/m", InstalledDirectory, ExistsOnly(nearby, data)));
    }

    [Fact]
    public void A_model_next_to_the_executable_is_found_when_the_data_folder_has_none()
    {
        // A portable copy: models/ ships with the executable.
        const string expected = InstalledDirectory + "/models/m";

        Assert.Equal(expected, Resolve("models/m", InstalledDirectory, ExistsOnly(expected)));
    }

    [Fact]
    public void A_model_at_the_repository_root_is_found_from_a_build_folder()
    {
        // Development: the executable is deep inside bin/, the model at the
        // root. Without walking up, 600 MB would have to be duplicated per
        // build configuration.
        const string expected = "/home/ada/hex_linux/models/m";

        Assert.Equal(expected, Resolve("models/m", BuildDirectory, ExistsOnly(expected)));
    }

    [Fact]
    public void The_tree_is_not_climbed_from_an_ordinary_folder()
    {
        // An archive unpacked in /tmp would otherwise make /tmp/models a
        // candidate, and /tmp is writable by every local user: a model planted
        // there would decide what gets typed into the victim's applications.
        const string planted = "/tmp/models/m";

        Assert.Null(Resolve("models/m", "/tmp/hexlinux-linux-x64", ExistsOnly(planted)));
    }

    [Fact]
    public void A_build_folder_with_a_runtime_identifier_is_recognised()
    {
        // dotnet build -r linux-x64 adds one more level below the framework.
        Assert.True(ModelLocator.IsBuildFolder(new DirectoryInfo("/r/src/HexLinux/bin/Debug/net10.0/linux-x64")));
        Assert.True(ModelLocator.IsBuildFolder(new DirectoryInfo(BuildDirectory)));
        Assert.False(ModelLocator.IsBuildFolder(new DirectoryInfo(InstalledDirectory)));
    }

    [Fact]
    public void A_model_missing_everywhere_returns_null()
    {
        Assert.Null(Resolve("models/m", BuildDirectory, _ => false));
    }

    [Fact]
    public void Without_a_data_folder_only_the_executable_side_is_searched()
    {
        const string data = DataDirectory + "/models/m";

        Assert.Null(Resolve("models/m", InstalledDirectory, ExistsOnly(data), data: null));
    }

    [Fact]
    public void Walking_up_stops_at_the_requested_limit()
    {
        // With no limit, a fruitless search would climb to the root of the
        // disk, asking the system at every level.
        const string tooFarUp = "/home/ada/hex_linux/models/m";

        string? found = ModelLocator.Resolve("models/m", BuildDirectory, null, Home, ExistsOnly(tooFarUp), maxAscent: 1);

        Assert.Null(found);
    }

    [Fact]
    public void An_absolute_path_is_taken_at_its_word()
    {
        const string absolute = "/opt/models/m";

        Assert.Equal(absolute, Resolve(absolute, BuildDirectory, ExistsOnly(absolute)));
    }

    [Fact]
    public void A_missing_absolute_path_triggers_no_search_elsewhere()
    {
        // An absolute path states an explicit intention: looking elsewhere
        // would be surprising.
        const string absolute = "/opt/models/m";
        const string elsewhere = DataDirectory + "/opt/models/m";

        Assert.Null(Resolve(absolute, BuildDirectory, ExistsOnly(elsewhere)));
    }

    [Fact]
    public void A_path_starting_with_a_tilde_means_the_home_folder()
    {
        // The way anyone writes a path by hand; no shell ever expands it in
        // a JSON file.
        const string expected = Home + "/models/m";

        Assert.Equal(expected, Resolve("~/models/m", BuildDirectory, ExistsOnly(expected)));
    }

    [Theory]
    [InlineData("~", Home)]
    [InlineData("~/a/b", Home + "/a/b")]
    [InlineData("~other/a", "~other/a")]
    [InlineData("models/~/a", "models/~/a")]
    public void Only_a_leading_tilde_alone_or_before_a_slash_is_expanded(string written, string expected)
    {
        // ~other is another user's home in a shell: not something to guess.
        Assert.Equal(expected, ModelLocator.ExpandHome(written, Home));
    }

    [Fact]
    public void Without_a_home_folder_the_tilde_is_left_as_written()
    {
        Assert.Equal("~/m", ModelLocator.ExpandHome("~/m", null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_path_is_refused(string configured)
    {
        Assert.Throws<ArgumentException>(() => Resolve(configured, BuildDirectory, _ => true));
    }
}
