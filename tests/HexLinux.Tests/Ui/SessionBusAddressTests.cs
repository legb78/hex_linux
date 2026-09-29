using HexLinux.Ui;
using Xunit;

namespace HexLinux.Tests.Ui;

/// <summary>
/// The environment is described in a dictionary and the socket test is a
/// predicate: every desktop, SSH login and service case fits in one line.
/// </summary>
public class SessionBusAddressTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] variables) =>
        name => variables.FirstOrDefault(variable => variable.Name == name).Value;

    private static Func<string, bool> Exists(params string[] paths) =>
        path => paths.Contains(path, StringComparer.Ordinal);

    [Fact]
    public void The_explicit_address_wins()
    {
        // Set by the desktop session, or by dbus-run-session in the harness.
        string? address = SessionBusAddress.Resolve(
            Env(("DBUS_SESSION_BUS_ADDRESS", "unix:path=/tmp/private-bus"), ("XDG_RUNTIME_DIR", "/run/user/1000")),
            Exists("/run/user/1000/bus"));

        Assert.Equal("unix:path=/tmp/private-bus", address);
    }

    [Fact]
    public void Without_the_variable_the_per_user_bus_is_found()
    {
        // A systemd user unit whose environment lacks DBUS_SESSION_BUS_ADDRESS:
        // sd-bus and GLib both fall back to $XDG_RUNTIME_DIR/bus.
        string? address = SessionBusAddress.Resolve(
            Env(("XDG_RUNTIME_DIR", "/run/user/1000")),
            Exists("/run/user/1000/bus"));

        Assert.Equal("unix:path=/run/user/1000/bus", address);
    }

    [Fact]
    public void A_trailing_slash_on_the_runtime_directory_does_not_matter()
    {
        // WSL sets XDG_RUNTIME_DIR=/run/user/0/ with the slash.
        string? address = SessionBusAddress.Resolve(Env(("XDG_RUNTIME_DIR", "/run/user/0/")), Exists("/run/user/0/bus"));

        Assert.Equal("unix:path=/run/user/0/bus", address);
    }

    [Fact]
    public void A_blank_variable_counts_as_unset()
    {
        string? address = SessionBusAddress.Resolve(
            Env(("DBUS_SESSION_BUS_ADDRESS", "  "), ("XDG_RUNTIME_DIR", "/run/user/1000")),
            Exists("/run/user/1000/bus"));

        Assert.Equal("unix:path=/run/user/1000/bus", address);
    }

    [Fact]
    public void No_socket_means_no_session_bus()
    {
        // An SSH login: XDG_RUNTIME_DIR may exist without a bus in it.
        Assert.Null(SessionBusAddress.Resolve(Env(("XDG_RUNTIME_DIR", "/run/user/1000")), Exists()));
    }

    [Fact]
    public void Nothing_set_means_no_session_bus()
    {
        Assert.Null(SessionBusAddress.Resolve(Env(), Exists("/bus")));
    }

    [Fact]
    public void A_relative_runtime_directory_is_ignored_without_looking()
    {
        // Resolved against the daemon's working directory, it could name any
        // socket; the predicate is not even consulted.
        bool asked = false;

        string? address = SessionBusAddress.Resolve(Env(("XDG_RUNTIME_DIR", "run/user/1000")), _ => asked = true);

        Assert.Null(address);
        Assert.False(asked);
    }

    [Fact]
    public void A_non_ascii_runtime_directory_is_not_used()
    {
        // The D-Bus library decodes %XX into one character per byte, so the
        // escaped path would name another file.
        Assert.Null(SessionBusAddress.Resolve(Env(("XDG_RUNTIME_DIR", "/run/usér")), _ => true));
    }

    [Theory]
    [InlineData("/run/user/1000/bus", "/run/user/1000/bus")]
    [InlineData("/tmp/a b", "/tmp/a%20b")]
    [InlineData("/tmp/a,b;c", "/tmp/a%2cb%3bc")]
    [InlineData("/tmp/100%", "/tmp/100%25")]
    [InlineData("/tmp/a=b", "/tmp/a%3db")]
    [InlineData("/tmp/x*y\\z", "/tmp/x%2ay%5cz")]
    public void Address_values_are_escaped(string value, string expected)
    {
        // A comma ends a key-value pair and a semicolon ends an address: left
        // as they are, they would cut the path short.
        Assert.Equal(expected, SessionBusAddress.Escape(value));
    }

    [Fact]
    public void A_runtime_directory_needing_escapes_is_escaped_in_the_address()
    {
        string? address = SessionBusAddress.Resolve(Env(("XDG_RUNTIME_DIR", "/tmp/my runtime")), Exists("/tmp/my runtime/bus"));

        Assert.Equal("unix:path=/tmp/my%20runtime/bus", address);
    }
}
