using HexLinux.Daemon;
using Xunit;

namespace HexLinux.Tests.Daemon;

/// <summary>
/// The autostart entry. Its faults only show at the next login — HexLinux
/// silently not starting — so the Exec quoting is checked against the Desktop
/// Entry specification's own rules (reserved characters force double quotes;
/// inside them <c>"</c>, <c>`</c>, <c>$</c> and <c>\</c> take a backslash; the
/// string escape then doubles each backslash; a literal <c>%</c> is
/// <c>%%</c>), and the refusal of binaries another user could replace, which
/// would run as this user at every login.
/// </summary>
public class DesktopEntryTests
{
    private const UnixFileMode Executable755 =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private const UnixFileMode Folder755 = Executable755;

    // --- The entry ------------------------------------------------------------------

    [Fact]
    public void The_entry_starts_the_executable_hidden_from_the_menus()
    {
        // NoDisplay: an autostart entry, not a launcher. The session's display
        // variables are inherited from the desktop that starts it.
        Assert.Equal(
            "[Desktop Entry]\n"
            + "Type=Application\n"
            + "Name=HexLinux\n"
            + "Comment=Local hold-to-talk dictation\n"
            + "Exec=/home/ana/.local/bin/hexlinux\n"
            + "Icon=audio-input-microphone\n"
            + "Terminal=false\n"
            + "NoDisplay=true\n"
            + "X-GNOME-Autostart-enabled=true\n",
            DesktopEntry.Build("/home/ana/.local/bin/hexlinux"));
    }

    [Fact]
    public void The_entry_is_the_same_for_the_same_executable_and_differs_once_it_moved()
    {
        // The daemon rewrites the entry at start when it differs from the
        // one it would write: the same binary must give the same text, byte
        // for byte, or the file would be rewritten at every start.
        string here = DesktopEntry.Build("/home/ana/.local/bin/hexlinux");

        Assert.Equal(here, DesktopEntry.Build("/home/ana/.local/bin/hexlinux"));
        Assert.NotEqual(here, DesktopEntry.Build("/opt/hexlinux/hexlinux"));
    }

    [Fact]
    public void A_path_with_a_space_is_quoted_in_the_exec_line()
    {
        // An archive unpacked in "~/My Programs": unquoted, the desktop would
        // try to run "/home/ana/My" with an argument.
        Assert.Contains("\nExec=\"/home/ana/My Programs/hexlinux\"\n", DesktopEntry.Build("/home/ana/My Programs/hexlinux"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("")]
    public void An_empty_path_is_refused(string path)
    {
        Assert.Throws<ArgumentException>(() => DesktopEntry.Build(path));
    }

    // --- Exec quoting ------------------------------------------------------------------

    [Theory]
    [InlineData("/usr/bin/hexlinux", "/usr/bin/hexlinux")]
    [InlineData("/home/ana/bin/hex-linux_2.0", "/home/ana/bin/hex-linux_2.0")]
    [InlineData("/home/amélie/bin/hexlinux", "/home/amélie/bin/hexlinux")]
    public void A_plain_path_is_written_as_it_is(string argument, string expected)
    {
        Assert.Equal(expected, DesktopEntry.QuoteExec(argument));
    }

    [Theory]
    [InlineData("/a b/hexlinux", "\"/a b/hexlinux\"")]
    [InlineData("/home/o'brien/hexlinux", "\"/home/o'brien/hexlinux\"")]
    [InlineData("~/bin/hexlinux", "\"~/bin/hexlinux\"")]
    [InlineData("/opt/a&b/hexlinux", "\"/opt/a&b/hexlinux\"")]
    [InlineData("/opt/a;b/hexlinux", "\"/opt/a;b/hexlinux\"")]
    [InlineData("/opt/(x86)/hexlinux", "\"/opt/(x86)/hexlinux\"")]
    [InlineData("/opt/a|b<c>d/hexlinux", "\"/opt/a|b<c>d/hexlinux\"")]
    [InlineData("/opt/a*b?c#d/hexlinux", "\"/opt/a*b?c#d/hexlinux\"")]
    public void A_reserved_character_puts_the_argument_in_double_quotes(string argument, string expected)
    {
        // The specification's reserved characters. Inside double quotes a
        // tilde is a plain character, never the home folder.
        Assert.Equal(expected, DesktopEntry.QuoteExec(argument));
    }

    [Theory]
    [InlineData("/opt/say \"hi\"/hexlinux", "\"/opt/say \\\\\"hi\\\\\"/hexlinux\"")]
    [InlineData("/opt/`x`/hexlinux", "\"/opt/\\\\`x\\\\`/hexlinux\"")]
    [InlineData("/opt/$HOME/hexlinux", "\"/opt/\\\\$HOME/hexlinux\"")]
    [InlineData("/opt/a\\b/hexlinux", "\"/opt/a\\\\\\\\b/hexlinux\"")]
    public void Characters_special_inside_quotes_take_a_backslash_itself_escaped_once_more(string argument, string expected)
    {
        // The specification spells out the result: a literal "$" is written
        // \\$ and a literal backslash four backslashes, because the string
        // escape of the key file is applied before the quoting.
        Assert.Equal(expected, DesktopEntry.QuoteExec(argument));
    }

    [Theory]
    [InlineData("/opt/100%/hexlinux", "/opt/100%%/hexlinux")]
    [InlineData("/opt/%f/hexlinux", "/opt/%%f/hexlinux")]
    [InlineData("/opt/50% off/hexlinux", "\"/opt/50%% off/hexlinux\"")]
    public void A_percent_sign_is_doubled_so_it_is_not_taken_for_a_field_code(string argument, string expected)
    {
        // %f would be replaced by a file name — here, by nothing.
        Assert.Equal(expected, DesktopEntry.QuoteExec(argument));
    }

    [Theory]
    [InlineData("/opt/a\nb/hexlinux")]
    [InlineData("/opt/a\rb/hexlinux")]
    public void A_line_break_cannot_be_written_and_is_refused(string argument)
    {
        // It would end the Exec line and start another key with the rest.
        Assert.Throws<ArgumentException>(() => DesktopEntry.QuoteExec(argument));
    }

    [Theory]
    [InlineData("/opt/a\tb/hexlinux")]
    [InlineData("/opt/a\u001bb/hexlinux")]
    [InlineData("/opt/a\u007fb/hexlinux")]
    public void Another_control_character_is_refused_too(string argument)
    {
        // The specification allows no control character in a value: written
        // raw, a desktop may reject the whole entry and HexLinux would
        // silently not start at the next login.
        Assert.Throws<ArgumentException>(() => DesktopEntry.QuoteExec(argument));
    }

    [Fact]
    public void Quoting_nothing_is_a_programming_error()
    {
        Assert.Throws<ArgumentNullException>(() => DesktopEntry.QuoteExec(null!));
    }

    // --- Pointing an existing entry at a moved executable ------------------------------

    [Fact]
    public void The_exec_value_of_an_entry_is_read_as_written()
    {
        Assert.Equal("\"/a b/hexlinux\"", DesktopEntry.ExecOf(DesktopEntry.Build("/a b/hexlinux")));
    }

    [Fact]
    public void Only_the_main_group_exec_counts()
    {
        // A [Desktop Action] group has its own Exec, which is not what the
        // session starts.
        const string entry = "[Desktop Action other]\nExec=/usr/bin/other\n[Desktop Entry]\nType=Application\nExec = /opt/hexlinux\n";

        Assert.Equal("/opt/hexlinux", DesktopEntry.ExecOf(entry));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[Desktop Entry]\nType=Application\n")]
    [InlineData("[Desktop Entry]\nExecutable=/opt/hexlinux\n")]
    public void An_entry_without_exec_has_none(string entry)
    {
        Assert.Null(DesktopEntry.ExecOf(entry));
        Assert.Null(DesktopEntry.WithExec(entry, "/opt/hexlinux"));
    }

    [Fact]
    public void Moving_the_executable_changes_the_exec_line_and_nothing_else()
    {
        // The user switched "start at login" off in the desktop's settings,
        // which wrote Hidden=true; HexLinux was then moved. Pointing the entry
        // at the new place must not switch autostart back on behind them.
        string entry = DesktopEntry.Build("/home/ana/old/hexlinux").Replace(
            "X-GNOME-Autostart-enabled=true", "X-GNOME-Autostart-enabled=false\nHidden=true", StringComparison.Ordinal);

        string? moved = DesktopEntry.WithExec(entry, "/home/ana/.local/bin/hexlinux");

        Assert.NotNull(moved);
        Assert.Equal("/home/ana/.local/bin/hexlinux", DesktopEntry.ExecOf(moved));
        Assert.Contains("\nHidden=true\n", moved, StringComparison.Ordinal);
        Assert.Contains("\nX-GNOME-Autostart-enabled=false\n", moved, StringComparison.Ordinal);
        Assert.Equal(
            entry.Replace("Exec=/home/ana/old/hexlinux", "Exec=/home/ana/.local/bin/hexlinux", StringComparison.Ordinal),
            moved);
    }

    [Fact]
    public void The_new_exec_is_quoted_like_a_fresh_entry()
    {
        string? moved = DesktopEntry.WithExec(DesktopEntry.Build("/opt/hexlinux"), "/opt/100% sure/hexlinux");

        Assert.Equal(DesktopEntry.Build("/opt/100% sure/hexlinux"), moved);
    }

    // --- What may be started at every login ----------------------------------------------

    [Fact]
    public void An_executable_only_its_owner_can_change_is_accepted_without_a_word()
    {
        (string? refusal, string? warning) = Check("/home/ana/.local/bin/hexlinux", Executable755, Folder755);

        Assert.Null(refusal);
        Assert.Null(warning);
    }

    [Theory]
    [InlineData("/usr/share/dotnet/dotnet")]
    [InlineData("/usr/lib/dotnet/dotnet")]
    [InlineData("/home/ana/.dotnet/dotnet")]
    public void The_dotnet_host_is_refused(string host)
    {
        // A development run through "dotnet hexlinux.dll" would give an entry
        // that starts dotnet with no program at every login.
        (string? refusal, string? warning) = Check(host, Executable755, Folder755);

        Assert.Contains("dotnet host", refusal, StringComparison.Ordinal);
        Assert.Null(warning);
    }

    [Fact]
    public void An_executable_in_a_dotnet_folder_is_not_taken_for_the_host()
    {
        // "dotnet tool install" puts its commands in ~/.dotnet/tools: only
        // the file name says whether it is the host.
        (string? refusal, _) = Check("/home/ana/.dotnet/tools/hexlinux", Executable755, Folder755);

        Assert.Null(refusal);
    }

    [Fact]
    public void A_binary_any_user_can_modify_is_refused()
    {
        // Anyone could replace it, and it would run as this user at login.
        (string? refusal, string? warning) = Check(
            "/home/ana/hexlinux", Executable755 | UnixFileMode.OtherWrite, Folder755);

        Assert.Equal("/home/ana/hexlinux can be modified by any user: fix its permissions (chmod o-w) first.", refusal);
        Assert.Null(warning);
    }

    [Fact]
    public void A_binary_in_a_folder_any_user_can_write_is_refused()
    {
        // An archive unpacked in /tmp (mode 1777): anyone may replace the
        // file there, whatever its own mode.
        UnixFileMode tmp = UnixFileMode.StickyBit
            | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

        (string? refusal, string? warning) = Check("/tmp/hexlinux/hexlinux", Executable755, tmp);

        Assert.Equal(
            "the folder of /tmp/hexlinux/hexlinux can be modified by any user: move HexLinux somewhere only you can write to.",
            refusal);
        Assert.Null(warning);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Group_write_access_is_only_a_warning(bool fileGroupWritable, bool folderGroupWritable)
    {
        // Ubuntu gives each user a group of their own and a umask that makes
        // new files group-writable: refusing would turn the ordinary case away.
        UnixFileMode file = Executable755 | (fileGroupWritable ? UnixFileMode.GroupWrite : 0);
        UnixFileMode folder = Folder755 | (folderGroupWritable ? UnixFileMode.GroupWrite : 0);

        (string? refusal, string? warning) = Check("/home/ana/bin/hexlinux", file, folder);

        Assert.Null(refusal);
        Assert.Equal("/home/ana/bin/hexlinux or its folder is writable by its group: harmless if the group is yours alone.", warning);
    }

    [Fact]
    public void A_refusal_takes_precedence_over_a_warning()
    {
        UnixFileMode groupWritable = Executable755 | UnixFileMode.GroupWrite;

        (string? refusal, string? warning) = Check(
            "/home/ana/bin/hexlinux", groupWritable | UnixFileMode.OtherWrite, groupWritable);

        Assert.NotNull(refusal);
        Assert.Null(warning);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Checking_no_path_is_refused(string path)
    {
        Assert.Throws<ArgumentException>(() => Check(path, Executable755, Folder755));
    }

    [Fact]
    public void A_build_that_needs_the_dotnet_runtime_of_an_SDK_is_refused()
    {
        // QA-04: the output of "dotnet build", enabled from a VM following
        // docs/testing.md, exited at login with "You must install .NET"
        // (exit code 131) while --autostart on had reported success.
        (string? refusal, string? warning) = DesktopEntry.CheckAutostart(
            "/home/ana/hex_linux/src/HexLinux/bin/Release/net10.0/hexlinux", Executable755, Folder755, Ana, needsDotnetRuntime: true);

        Assert.Contains("needs the .NET runtime of an SDK", refusal, StringComparison.Ordinal);
        Assert.Contains("scripts/publish.sh", refusal, StringComparison.Ordinal);
        Assert.Null(warning);
    }

    [Fact]
    public void A_path_holding_a_percent_sign_is_refused()
    {
        // QA-06: written "%%" as the specification wants, but GLib 2.80 looks
        // "Hex 100%%" up before undoing it, and GNOME never starts the entry.
        (string? refusal, _) = Check("/home/ana/Hex 100%/hexlinux", Executable755, Folder755);

        Assert.Equal(
            "the path of /home/ana/Hex 100%/hexlinux holds a '%', which GNOME and the other GLib desktops cannot start: move HexLinux to a folder without one first.",
            refusal);
    }

    [Fact]
    public void A_binary_owned_by_another_user_is_refused()
    {
        // SEC-07: a 0755 binary in someone else's home passes the mode checks,
        // yet its owner can replace it, and it would run as this user at login.
        (string? refusal, _) = DesktopEntry.CheckAutostart(
            "/home/bob/hexlinux/hexlinux", Executable755, Folder755, new FileOwners(1001, 1000, 1000), needsDotnetRuntime: false);

        Assert.Equal(
            "/home/bob/hexlinux/hexlinux belongs to another user (uid 1001), who could replace it: install HexLinux in a folder of your own first.",
            refusal);
    }

    [Fact]
    public void A_folder_owned_by_another_user_is_refused()
    {
        // Its owner can rename the binary away and put another in its place.
        (string? refusal, _) = DesktopEntry.CheckAutostart(
            "/home/bob/hexlinux/hexlinux", Executable755, Folder755, new FileOwners(1000, 1001, 1000), needsDotnetRuntime: false);

        Assert.StartsWith("the folder of /home/bob/hexlinux/hexlinux belongs to another user (uid 1001)", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_binary_installed_by_root_is_accepted()
    {
        // /opt or /usr/local, owned by root: nobody but root can replace it.
        (string? refusal, string? warning) = DesktopEntry.CheckAutostart(
            "/opt/hexlinux/hexlinux", Executable755, Folder755, new FileOwners(0, 0, 1000), needsDotnetRuntime: false);

        Assert.Null(refusal);
        Assert.Null(warning);
    }

    [Fact]
    public void Owners_that_cannot_be_read_only_warn()
    {
        // A file system that does not report owners: autostart must still be
        // possible, and the user told what could not be checked.
        (string? refusal, string? warning) = DesktopEntry.CheckAutostart(
            "/home/ana/hexlinux", Executable755, Folder755, new FileOwners(null, 1000, 1000), needsDotnetRuntime: false);

        Assert.Null(refusal);
        Assert.Equal("the owner of /home/ana/hexlinux or of its folder could not be read.", warning);
    }

    // --- Pointing an existing entry at the running copy ---------------------------------

    [Fact]
    public void An_entry_starting_another_copy_that_still_works_is_left_alone()
    {
        // QA-05: running a development build once, or a release unpacked in
        // Downloads to try it, used to take the autostart entry over.
        string entry = DesktopEntry.Build("/home/ana/.local/bin/hexlinux");

        Assert.False(DesktopEntry.ShouldRepoint(entry, "/home/ana/Downloads/hexlinux/hexlinux", _ => true));
    }

    [Fact]
    public void An_entry_starting_a_copy_that_is_gone_is_pointed_at_the_running_one()
    {
        // HexLinux moved: the entry follows it, as before.
        string entry = DesktopEntry.Build("/home/ana/old place/hexlinux");
        List<string> asked = [];

        Assert.True(DesktopEntry.ShouldRepoint(entry, "/home/ana/.local/bin/hexlinux", path =>
        {
            asked.Add(path);
            return false;
        }));
        Assert.Equal(["/home/ana/old place/hexlinux"], asked);
    }

    [Fact]
    public void An_entry_that_already_starts_the_running_copy_needs_nothing()
    {
        string entry = DesktopEntry.Build("/home/ana/.local/bin/hexlinux");

        Assert.False(DesktopEntry.ShouldRepoint(entry, "/home/ana/.local/bin/hexlinux", _ => throw new InvalidOperationException("nothing to look at")));
    }

    [Theory]
    [InlineData("[Desktop Entry]\nExec=/opt/hexlinux --verbose\n")]
    [InlineData("[Desktop Entry]\nExec=/opt/hexlinux %f\n")]
    [InlineData("[Desktop Entry]\nExec=\"/opt/hex\"linux\n")]
    [InlineData("[Desktop Entry]\nType=Application\n")]
    public void An_entry_whose_exec_is_not_one_plain_path_is_left_alone(string entry)
    {
        // Edited by hand, or not written by HexLinux: nothing to repoint.
        Assert.False(DesktopEntry.ShouldRepoint(entry, "/home/ana/.local/bin/hexlinux", _ => false));
    }

    [Theory]
    [InlineData("/opt/hexlinux")]
    [InlineData("/home/ana/My Apps/hexlinux")]
    [InlineData("/home/ana/100% sure/hexlinux")]
    [InlineData("/home/ana/a\"b`c$d\\e/hexlinux")]
    [InlineData("/home/ana/(x) & 'y' ~z/hexlinux")]
    public void Every_exec_value_written_is_read_back_as_its_path(string path)
    {
        // The reading undoes the three layers of the writing, whatever the
        // path holds.
        Assert.Equal(path, DesktopEntry.UnquoteExec(DesktopEntry.QuoteExec(path)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"\"")]
    [InlineData("/opt/hex linux")]
    [InlineData("\"/opt/hex\"linux\"")]
    [InlineData("/opt/hexlinux\\q")]
    [InlineData("/opt/%f")]
    public void An_exec_value_that_is_no_single_path_reads_as_none(string exec)
    {
        Assert.Null(DesktopEntry.UnquoteExec(exec));
    }

    private static readonly FileOwners Ana = new(1000, 1000, 1000);

    /// <summary>The mode checks, for an executable and a folder of the user's own, in a self-contained build.</summary>
    private static (string? Refusal, string? Warning) Check(string path, UnixFileMode file, UnixFileMode folder) =>
        DesktopEntry.CheckAutostart(path, file, folder, Ana, needsDotnetRuntime: false);
}
