using HexLinux.Configuration;
using Xunit;

namespace HexLinux.Tests.Configuration;

/// <summary>
/// Where every file lands decides whether the daemon, <c>--doctor</c>,
/// <c>--toggle</c> and the autostart entry agree with each other and with
/// what the user finds in their home folder. The environment is passed in, so
/// each case of the XDG Base Directory specification is described in one line
/// without touching the variables of the test process.
/// </summary>
public class AppPathsTests
{
    private const string Home = "/home/ada";

    private static Func<string, string?> Variables(params (string Name, string Value)[] variables) =>
        name => variables.FirstOrDefault(variable => variable.Name == name).Value;

    private static AppPaths Resolve(params (string Name, string Value)[] variables) =>
        AppPaths.Resolve(Variables(variables), Home);

    [Fact]
    public void Without_any_XDG_variable_the_folders_of_the_specification_are_used()
    {
        // A session started without the variables (a bare tty, some display
        // managers): the defaults the specification prescribes.
        AppPaths paths = Resolve();

        Assert.Equal("/home/ada/.config/hexlinux", paths.ConfigDirectory);
        Assert.Equal("/home/ada/.local/share/hexlinux", paths.DataDirectory);
        Assert.Equal("/home/ada/.local/state/hexlinux", paths.StateDirectory);
        Assert.Equal("/home/ada/.config/autostart", paths.AutostartDirectory);
    }

    [Fact]
    public void Each_XDG_variable_moves_its_own_folder()
    {
        AppPaths paths = Resolve(
            ("XDG_CONFIG_HOME", "/cfg"),
            ("XDG_DATA_HOME", "/data"),
            ("XDG_STATE_HOME", "/state"),
            ("XDG_RUNTIME_DIR", "/run/user/1000"));

        Assert.Equal("/cfg/hexlinux", paths.ConfigDirectory);
        Assert.Equal("/data/hexlinux", paths.DataDirectory);
        Assert.Equal("/state/hexlinux", paths.StateDirectory);
        Assert.Equal("/run/user/1000/hexlinux", paths.RuntimeDirectory);
    }

    [Fact]
    public void The_autostart_folder_follows_the_configuration_home()
    {
        // The desktop looks for autostart entries in $XDG_CONFIG_HOME/autostart;
        // an entry written to ~/.config/autostart while the variable points
        // elsewhere would never start anything.
        Assert.Equal("/cfg/autostart", Resolve(("XDG_CONFIG_HOME", "/cfg")).AutostartDirectory);
    }

    [Fact]
    public void The_autostart_folder_is_shared_rather_than_inside_the_application_folder()
    {
        // Shared with every other application: an entry inside
        // ~/.config/hexlinux would be read by no desktop.
        AppPaths paths = Resolve();

        Assert.Equal(paths.AutostartDirectory, Path.GetDirectoryName(paths.AutostartFile));
        Assert.DoesNotContain(AppPaths.ApplicationFolder, paths.AutostartDirectory, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("XDG_CONFIG_HOME")]
    [InlineData("XDG_DATA_HOME")]
    [InlineData("XDG_STATE_HOME")]
    public void A_relative_value_is_ignored_as_the_specification_requires(string variable)
    {
        // Honoured, a relative value would put the files wherever the daemon
        // happened to be started from: a different settings.json from the
        // terminal and from the autostart entry.
        Assert.Equal(Resolve(), Resolve((variable, "relative/folder")));
    }

    [Theory]
    [InlineData("XDG_CONFIG_HOME")]
    [InlineData("XDG_DATA_HOME")]
    [InlineData("XDG_STATE_HOME")]
    public void A_value_starting_with_a_tilde_is_relative_and_ignored(string variable)
    {
        // "~/.config" written in environment.d or a unit file is not expanded
        // by anything: it is a relative path, and must not create a folder
        // named "~" in the working directory.
        Assert.Equal(Resolve(), Resolve((variable, "~/.config")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_value_counts_as_unset(string value)
    {
        // "export XDG_CONFIG_HOME=" in a shell profile: the specification
        // treats empty as unset.
        Assert.Equal(
            Resolve(),
            Resolve(("XDG_CONFIG_HOME", value), ("XDG_DATA_HOME", value), ("XDG_STATE_HOME", value), ("XDG_RUNTIME_DIR", value)));
    }

    [Fact]
    public void A_trailing_slash_does_not_double_the_separator()
    {
        // XDG_CONFIG_HOME=$HOME/.config/ is common: the paths shown by
        // --doctor must still be the canonical ones.
        Assert.Equal("/home/ada/.config/hexlinux/settings.json", Resolve(("XDG_CONFIG_HOME", "/home/ada/.config/")).SettingsFile);
    }

    [Fact]
    public void The_files_sit_in_their_folders_under_fixed_names()
    {
        // The names the documentation, the tray's "Open log folder" and the
        // user's own scripts rely on.
        AppPaths paths = Resolve(("XDG_RUNTIME_DIR", "/run/user/1000"));

        Assert.Equal("/home/ada/.config/hexlinux/settings.json", paths.SettingsFile);
        Assert.Equal("/home/ada/.local/state/hexlinux/hexlinux.log", paths.LogFile);
        Assert.Equal("/home/ada/.local/state/hexlinux/crash.log", paths.CrashFile);
        Assert.Equal("/run/user/1000/hexlinux/control.sock", paths.ControlSocket);
        Assert.Equal("/run/user/1000/hexlinux/daemon.lock", paths.LockFile);
        Assert.Equal("/home/ada/.config/autostart/hexlinux.desktop", paths.AutostartFile);
    }

    [Fact]
    public void Without_a_runtime_folder_the_socket_and_the_lock_go_to_the_state_folder()
    {
        // XDG_RUNTIME_DIR has no default in the specification: a session
        // without logind still gets a control socket, in a folder private to
        // the user, and the daemon and --toggle find the same one.
        AppPaths paths = Resolve();

        Assert.Equal(paths.StateDirectory, paths.RuntimeDirectory);
        Assert.Equal("/home/ada/.local/state/hexlinux/control.sock", paths.ControlSocket);
        Assert.Equal("/home/ada/.local/state/hexlinux/daemon.lock", paths.LockFile);
    }

    [Fact]
    public void A_relative_runtime_folder_is_ignored_like_the_others()
    {
        Assert.Equal(Resolve().RuntimeDirectory, Resolve(("XDG_RUNTIME_DIR", "run/user/1000")).RuntimeDirectory);
    }

    [Fact]
    public void A_moved_state_folder_takes_the_socket_fallback_with_it()
    {
        AppPaths paths = Resolve(("XDG_STATE_HOME", "/state"));

        Assert.Equal("/state/hexlinux/control.sock", paths.ControlSocket);
    }

    [Fact]
    public void The_environment_and_the_home_folder_are_required()
    {
        Assert.Throws<ArgumentNullException>(() => AppPaths.Resolve(null!, Home));
        Assert.ThrowsAny<ArgumentException>(() => AppPaths.Resolve(Variables(), ""));
        Assert.ThrowsAny<ArgumentException>(() => AppPaths.Resolve(Variables(), "  "));
    }

    [Fact]
    public void The_real_environment_is_read_with_the_home_folder_of_the_user()
    {
        // What the daemon, --doctor and --autostart actually use: the same
        // variables, with $HOME as the home folder — not .NET's ApplicationData,
        // which on Linux is already ~/.config and would give ~/.config/.config.
        string? home = Environment.GetEnvironmentVariable("HOME");
        Assert.False(string.IsNullOrEmpty(home), "HOME is not set in the test environment");

        Assert.Equal(AppPaths.Resolve(Environment.GetEnvironmentVariable, home), AppPaths.FromEnvironment());
    }
}
