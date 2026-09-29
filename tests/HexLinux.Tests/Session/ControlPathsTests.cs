using HexLinux.Configuration;
using HexLinux.Session;
using Xunit;

namespace HexLinux.Tests.Session;

/// <summary>
/// Where the control socket goes. A Unix socket's path must fit in
/// <c>sun_path</c> — 108 bytes on Linux, verified with gcc in WSL, its
/// terminating zero included — and a path that does not fit makes the bind
/// fail. The daemon and <c>hexlinux --toggle</c> apply the same rule, so they
/// always meet at the same path.
/// </summary>
public class ControlPathsTests
{
    private const string Home = "/home/ana";

    [Fact]
    public void The_runtime_folder_is_used_when_the_session_provides_one()
    {
        // Every logind session sets XDG_RUNTIME_DIR: private, in memory,
        // emptied at logout.
        AppPaths paths = Resolve("/run/user/1000", stateHome: null);

        Assert.Equal("/run/user/1000/hexlinux", ControlPaths.Folder(paths));
    }

    [Fact]
    public void Without_a_runtime_folder_the_state_folder_is_used()
    {
        // A container, or a shell opened outside any logind session, has no
        // XDG_RUNTIME_DIR at all.
        AppPaths paths = Resolve(runtime: null, stateHome: null);

        Assert.Equal("/home/ana/.local/state/hexlinux", ControlPaths.Folder(paths));
    }

    [Fact]
    public void A_runtime_folder_too_deep_for_the_socket_falls_back_to_the_state_folder()
    {
        // XDG_RUNTIME_DIR pointed deep into a sandbox or a test harness's
        // temporary tree: binding there would fail, leaving no socket.
        string deep = "/tmp/" + new string('r', 100);
        AppPaths paths = Resolve(deep, stateHome: null);

        Assert.Equal("/home/ana/.local/state/hexlinux", ControlPaths.Folder(paths));
    }

    [Fact]
    public void When_neither_folder_leaves_room_the_daemon_runs_without_a_socket()
    {
        // The shortcut still works; only --toggle and --status do not.
        string deep = "/tmp/" + new string('r', 100);
        AppPaths paths = Resolve(deep, stateHome: "/srv/" + new string('s', 100));

        Assert.Null(ControlPaths.Folder(paths));
    }

    [Fact]
    public void The_socket_found_there_is_the_one_the_paths_name()
    {
        // AppPaths.ControlSocket and ControlPaths describe one file: two
        // spellings that drifted apart would make the client knock at an
        // empty path.
        AppPaths paths = Resolve("/run/user/1000", stateHome: null);

        Assert.Equal(paths.ControlSocket, Path.Combine(ControlPaths.Folder(paths)!, ControlPaths.SocketName));
        Assert.Equal(paths.LockFile, Path.Combine(ControlPaths.Folder(paths)!, ControlPaths.LockName));
    }

    [Fact]
    public void A_path_of_107_bytes_fits_and_one_of_108_does_not()
    {
        // sun_path is 108 bytes, the terminating zero included.
        Assert.True(ControlPaths.Fits("/" + new string('a', 106)));
        Assert.False(ControlPaths.Fits("/" + new string('a', 107)));
    }

    [Fact]
    public void The_limit_counts_bytes_not_characters()
    {
        // A home folder named after its owner — /home/amélie — takes two
        // bytes for the é: 107 characters can be 108 bytes.
        string path = "/home/amélie/" + new string('a', 107 - "/home/amélie/".Length);

        Assert.Equal(107, path.Length);
        Assert.False(ControlPaths.Fits(path));
    }

    [Fact]
    public void Asking_without_paths_is_a_programming_error()
    {
        Assert.Throws<ArgumentNullException>(() => ControlPaths.Folder(null!));
        Assert.Throws<ArgumentNullException>(() => ControlPaths.Fits(null!));
    }

    private static AppPaths Resolve(string? runtime, string? stateHome) =>
        AppPaths.Resolve(
            variable => variable switch
            {
                "XDG_RUNTIME_DIR" => runtime,
                "XDG_STATE_HOME" => stateHome,
                _ => null,
            },
            Home);
}
