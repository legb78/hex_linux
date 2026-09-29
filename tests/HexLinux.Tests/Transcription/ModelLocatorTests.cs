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

    // --- The order of the search (P14) ------------------------------------------------

    private static List<string> Asked(string configured, string baseDirectory, string? data = DataDirectory, int maxAscent = 6)
    {
        List<string> asked = [];

        ModelLocator.Resolve(configured, baseDirectory, data, Home, candidate =>
        {
            asked.Add(candidate);
            return false;
        }, maxAscent);

        return asked;
    }

    [Fact]
    public void From_a_build_folder_the_data_folder_comes_first_then_each_parent_in_turn()
    {
        // The whole order, pinned: the folder only the user can write to,
        // then the executable's, then upward one level at a time, and no
        // further than the limit.
        Assert.Equal(
            [
                DataDirectory + "/models/m",
                BuildDirectory + "/models/m",
                "/home/ada/hex_linux/src/HexLinux/bin/Release/models/m",
                "/home/ada/hex_linux/src/HexLinux/bin/models/m",
                "/home/ada/hex_linux/src/HexLinux/models/m",
                "/home/ada/hex_linux/src/models/m",
                "/home/ada/hex_linux/models/m",
                "/home/ada/models/m",
            ],
            Asked("models/m", BuildDirectory));
    }

    [Fact]
    public void From_an_installed_folder_only_two_places_are_asked()
    {
        // The data folder, then beside the executable: nothing above /opt is
        // ever a candidate.
        Assert.Equal([DataDirectory + "/models/m", InstalledDirectory + "/models/m"], Asked("models/m", InstalledDirectory));
    }

    [Fact]
    public void The_data_folder_wins_over_the_repository_root_from_a_build_folder()
    {
        // P14: a developer's own downloaded model, in ~/.local/share, is the
        // one used, even when the repository also holds one.
        const string data = DataDirectory + "/models/m";
        const string repository = "/home/ada/hex_linux/models/m";

        Assert.Equal(data, Resolve("models/m", BuildDirectory, ExistsOnly(repository, data)));
    }

    [Fact]
    public void The_executable_folder_wins_over_its_parents()
    {
        const string nearby = BuildDirectory + "/models/m";
        const string repository = "/home/ada/hex_linux/models/m";

        Assert.Equal(nearby, Resolve("models/m", BuildDirectory, ExistsOnly(repository, nearby), data: null));
    }

    [Fact]
    public void A_limit_of_zero_keeps_the_search_beside_the_executable()
    {
        Assert.Equal([BuildDirectory + "/models/m"], Asked("models/m", BuildDirectory, data: null, maxAscent: 0));
    }

    [Fact]
    public void An_absolute_path_is_the_only_place_asked()
    {
        // No fallback that could pick a different model than the one named.
        Assert.Equal(["/opt/models/m"], Asked("/opt/models/m", BuildDirectory));
    }

    [Fact]
    public void A_path_starting_with_a_tilde_is_the_only_place_asked()
    {
        // "~/models/m" names one folder of the user's home: it is expanded
        // first, then treated as the absolute path it now is.
        Assert.Equal([Home + "/models/m"], Asked("~/models/m", InstalledDirectory));
    }

    [Fact]
    public void Spaces_around_the_configured_path_are_ignored()
    {
        // A stray space typed in settings.json must not make the model
        // unfindable.
        const string expected = DataDirectory + "/models/m";

        Assert.Equal(expected, Resolve("  models/m ", InstalledDirectory, ExistsOnly(expected)));
    }

    [Fact]
    public void A_trailing_slash_on_the_executable_folder_changes_nothing()
    {
        // AppContext.BaseDirectory ends with a separator.
        const string expected = "/home/ada/hex_linux/models/m";

        Assert.Equal(expected, Resolve("models/m", BuildDirectory + "/", ExistsOnly(expected)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_data_folder_is_skipped(string data)
    {
        Assert.Equal([InstalledDirectory + "/models/m"], Asked("models/m", InstalledDirectory, data));
    }

    [Theory]
    [InlineData("/r/bin/Release/net10.0", true)]
    [InlineData("/r/bin/Debug/net10.0/linux-x64", true)]
    [InlineData("/r/bin/Release", false)]
    [InlineData("/opt/hexlinux", false)]
    [InlineData("/usr/local/bin", false)]
    [InlineData("/home/ada/bin/apps/hexlinux", false)]
    [InlineData("/home/ada/bin/tools/hexlinux/x64", false)]
    public void Only_a_bin_configuration_framework_folder_counts_as_a_build_folder(string folder, bool expected)
    {
        // /usr/local/bin in particular: an installed binary must never climb
        // the tree. Nor must one unpacked under a personal ~/bin folder, two
        // or three levels down: only a framework folder (net…) marks a build.
        Assert.Equal(expected, ModelLocator.IsBuildFolder(new DirectoryInfo(folder)));
    }

    [Fact]
    public void The_arguments_are_checked()
    {
        Assert.Throws<ArgumentException>(() => ModelLocator.Resolve("models/m", " ", null, Home, _ => true));
        Assert.Throws<ArgumentNullException>(() => ModelLocator.Resolve("models/m", InstalledDirectory, null, Home, null!));
        Assert.Throws<ArgumentNullException>(() => ModelLocator.IsBuildFolder(null!));
        Assert.Throws<ArgumentNullException>(() => ModelLocator.ExpandHome(null!, Home));
    }

    [Fact]
    public void An_empty_home_folder_leaves_the_tilde_as_written()
    {
        Assert.Equal("~/m", ModelLocator.ExpandHome("~/m", ""));
        Assert.Equal("~", ModelLocator.ExpandHome("~", ""));
    }

    [Fact]
    public void The_real_file_system_variant_finds_a_folder_that_exists()
    {
        // The overload the daemon, --transcribe and --doctor call: the same
        // search, asking the disk. Only a temporary folder is involved.
        string root = Path.Combine(Path.GetTempPath(), $"hexlinux-models-{Guid.NewGuid():N}");
        string data = Path.Combine(root, "data");
        string model = Path.Combine(data, "models", "m");
        Directory.CreateDirectory(model);

        try
        {
            Assert.Equal(model, ModelLocator.Resolve("models/m", Path.Combine(root, "opt"), data));
            Assert.Null(ModelLocator.Resolve("models/other", Path.Combine(root, "opt"), data));
            Assert.Equal(model, ModelLocator.Resolve(model, "/nonexistent", null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
