using HexLinux.Transcription;
using Xunit;

namespace HexLinux.Tests.Transcription;

/// <summary>
/// The search is tested without touching the disk: the existence predicate is
/// injected, which makes it possible to describe whole trees in one line and
/// to check the exact order in which locations are tried.
/// </summary>
public class ModelLocatorTests
{
    private const string BaseDirectory = "/home/ada/hex_linux/src/HexLinux/bin/Release/net10.0";
    private const string DataDirectory = "/home/ada/.local/share/hexlinux";

    private static Func<string, bool> ExistsOnly(params string[] paths) =>
        candidate => paths.Contains(candidate, StringComparer.Ordinal);

    [Fact]
    public void A_model_next_to_the_executable_is_found_immediately()
    {
        // A portable copy: models/ ships with the executable.
        const string expected = BaseDirectory + "/models/m";

        string? found = ModelLocator.Resolve("models/m", BaseDirectory, DataDirectory, ExistsOnly(expected));

        Assert.Equal(expected, found);
    }

    [Fact]
    public void A_model_at_the_repository_root_is_found_by_walking_up()
    {
        // Development: the executable is deep inside bin/, the model at the
        // root. Without walking up, 600 MB would have to be duplicated per
        // build configuration.
        const string expected = "/home/ada/hex_linux/models/m";

        string? found = ModelLocator.Resolve("models/m", BaseDirectory, DataDirectory, ExistsOnly(expected));

        Assert.Equal(expected, found);
    }

    [Fact]
    public void A_model_in_the_data_folder_is_found_when_nothing_is_nearer()
    {
        // Where get-model.sh puts it by default, so that replacing the
        // executable does not mean downloading the model again.
        const string expected = DataDirectory + "/models/m";

        string? found = ModelLocator.Resolve("models/m", BaseDirectory, DataDirectory, ExistsOnly(expected));

        Assert.Equal(expected, found);
    }

    [Fact]
    public void The_closest_to_the_executable_wins()
    {
        // A copy placed by hand beside the binary is the more specific wish.
        const string closest = BaseDirectory + "/models/m";
        const string farther = "/home/ada/hex_linux/models/m";
        const string data = DataDirectory + "/models/m";

        string? found = ModelLocator.Resolve("models/m", BaseDirectory, DataDirectory, ExistsOnly(data, farther, closest));

        Assert.Equal(closest, found);
    }

    [Fact]
    public void A_model_missing_everywhere_returns_null()
    {
        string? found = ModelLocator.Resolve("models/m", BaseDirectory, DataDirectory, _ => false);

        Assert.Null(found);
    }

    [Fact]
    public void Without_a_data_folder_only_the_tree_is_searched()
    {
        const string data = DataDirectory + "/models/m";

        string? found = ModelLocator.Resolve("models/m", BaseDirectory, dataDirectory: null, ExistsOnly(data));

        Assert.Null(found);
    }

    [Fact]
    public void Walking_up_stops_at_the_requested_limit()
    {
        // With no limit, a fruitless search would climb to the root of the
        // disk, asking the system at every level.
        const string tooFarUp = "/home/ada/hex_linux/models/m";

        string? found = ModelLocator.Resolve(
            "models/m", BaseDirectory, dataDirectory: null, ExistsOnly(tooFarUp), maxAscent: 1);

        Assert.Null(found);
    }

    [Fact]
    public void An_absolute_path_is_taken_at_its_word()
    {
        const string absolute = "/opt/models/m";

        string? found = ModelLocator.Resolve(absolute, BaseDirectory, DataDirectory, ExistsOnly(absolute));

        Assert.Equal(absolute, found);
    }

    [Fact]
    public void A_missing_absolute_path_triggers_no_search_elsewhere()
    {
        // An absolute path states an explicit intention: looking elsewhere
        // would be surprising.
        const string absolute = "/opt/models/m";
        const string elsewhere = DataDirectory + "/opt/models/m";

        string? found = ModelLocator.Resolve(absolute, BaseDirectory, DataDirectory, ExistsOnly(elsewhere));

        Assert.Null(found);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_path_is_refused(string configured)
    {
        Assert.Throws<ArgumentException>(
            () => ModelLocator.Resolve(configured, BaseDirectory, DataDirectory, _ => true));
    }
}
