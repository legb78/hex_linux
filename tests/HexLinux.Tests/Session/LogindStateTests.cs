using HexLinux.Session;
using Xunit;

namespace HexLinux.Tests.Session;

/// <summary>
/// The reading of <c>loginctl show-session</c>, on which the whole session
/// guard rests. The keyboard is read below the desktop, so without a correct
/// reading a shortcut pressed on the lock screen would dictate into the
/// password field. The outputs below are loginctl's real ones (systemd 255,
/// captured in WSL): one <c>Key=value</c> per line, in loginctl's order, not
/// the order the properties were asked in.
/// </summary>
public class LogindStateTests
{
    [Fact]
    public void The_real_output_of_a_text_session_is_read_whatever_the_order_of_the_lines()
    {
        // Exactly what WSL's session 1 answers: Seat first, empty.
        LogindState state = LogindState.Parse("Seat=\nRemote=no\nType=tty\nActive=yes\nLockedHint=no\n");

        Assert.Equal(new LogindState(true, false, "tty", "", false), state);
        Assert.False(state.IsLocalGraphical);
        Assert.True(state.AllowsInsertion);
    }

    [Fact]
    public void A_locked_desktop_session_is_read_as_locked()
    {
        // GNOME and KDE set LockedHint while their locker is up.
        LogindState state = LogindState.Parse("Seat=seat0\nRemote=no\nType=wayland\nActive=yes\nLockedHint=yes\n");

        Assert.True(state.Locked);
        Assert.True(state.IsLocalGraphical);
        Assert.False(state.AllowsInsertion);
    }

    [Fact]
    public void A_session_behind_another_users_one_is_read_as_inactive()
    {
        // After a switch of user, the first session stays open behind the
        // second: the keys typed by the second user still reach /dev/input.
        LogindState state = LogindState.Parse("Seat=seat0\nRemote=no\nType=x11\nActive=no\nLockedHint=no\n");

        Assert.False(state.Active);
        Assert.False(state.AllowsInsertion);
    }

    [Fact]
    public void An_empty_answer_is_unknown_and_does_not_by_itself_refuse()
    {
        // loginctl prints nothing, exit code 0, for a property it does not
        // know. The policy turns that into a refusal; the reading does not.
        LogindState state = LogindState.Parse(string.Empty);

        Assert.Equal(LogindState.Unknown, state);
        Assert.Null(state.Active);
        Assert.Null(state.Locked);
        Assert.Null(state.Remote);
        Assert.Equal(string.Empty, state.Type);
        Assert.Equal(string.Empty, state.Seat);
        Assert.True(state.AllowsInsertion);
    }

    [Theory]
    [InlineData("Active=maybe\nLockedHint=1")]
    [InlineData("Active=Yes\nLockedHint=NO")]
    [InlineData("Active=\nLockedHint=")]
    public void A_boolean_other_than_yes_or_no_is_unknown(string output)
    {
        // Only loginctl's own spelling is trusted: a "1" or a "Yes" from some
        // wrapper is not taken for an answer.
        LogindState state = LogindState.Parse(output);

        Assert.Null(state.Active);
        Assert.Null(state.Locked);
    }

    [Fact]
    public void The_owner_of_the_session_is_read_from_the_real_output()
    {
        // What WSL answered on 2026-09-29 for its terminal session, asked
        // with --property=User among the others: the uid comes first, as a
        // number, followed by the user's name.
        LogindState state = LogindState.Parse("User=0\nName=root\nSeat=\nRemote=no\nType=tty\nClass=user\nActive=yes\nLockedHint=no\n");

        Assert.Equal(new LogindState(true, false, "tty", "", false, 0), state);
    }

    [Fact]
    public void Lines_that_are_not_properties_and_unknown_keys_are_skipped()
    {
        // A warning printed on the same stream, or a property added by a
        // later systemd, must not shift or spoil the others.
        LogindState state = LogindState.Parse(
            "Warning: something\n=yes\nName=ana\nId=2\nActive=yes\nLockedHint=no\nType=wayland\nSeat=seat0\n");

        Assert.Equal(new LogindState(true, false, "wayland", "seat0"), state);
    }

    [Fact]
    public void Parsing_nothing_at_all_is_a_programming_error()
    {
        Assert.Throws<ArgumentNullException>(() => LogindState.Parse(null!));
    }

    // --- Insertion ----------------------------------------------------------------

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(true, null, true)]
    [InlineData(true, false, true)]
    [InlineData(null, false, true)]
    [InlineData(false, false, false)]
    [InlineData(false, null, false)]
    [InlineData(true, true, false)]
    [InlineData(null, true, false)]
    [InlineData(false, true, false)]
    public void Only_a_positive_inactive_or_locked_refuses_insertion(bool? active, bool? locked, bool allowed)
    {
        // Lockers that do not report LockedHint leave it unknown: that alone
        // must not make dictation impossible on those desktops.
        Assert.Equal(allowed, new LogindState(active, locked).AllowsInsertion);
    }

    // --- Which sessions say something about the screen -----------------------------

    [Theory]
    [InlineData("x11", "seat0", false, true)]
    [InlineData("wayland", "seat0", null, true)]
    [InlineData("wayland", "seat1", false, true)]
    [InlineData("tty", "seat0", false, false)]
    [InlineData("wayland", "", false, false)]
    [InlineData("wayland", "seat0", true, false)]
    [InlineData("Wayland", "seat0", false, false)]
    [InlineData("mir", "seat0", false, false)]
    [InlineData("", "", null, false)]
    public void Only_a_local_graphical_session_at_a_seat_speaks_for_the_screen(string type, string seat, bool? remote, bool expected)
    {
        // logind reports a seatless session — an SSH login — as always
        // active: a daemon started from there must look further.
        Assert.Equal(expected, new LogindState(true, false, type, seat, remote).IsLocalGraphical);
    }

    // --- Session ids passed to loginctl -------------------------------------------

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("c2")]
    [InlineData("C12")]
    public void Real_session_ids_are_valid(string id)
    {
        // Numbers for user sessions, "c" and a number for the ones the login
        // manager opens for its greeter.
        Assert.True(LogindState.IsValidSessionId(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1 2")]
    [InlineData("1;reboot")]
    [InlineData("../1")]
    [InlineData("1\n2")]
    [InlineData("-Hhost")]
    [InlineData("--host=elsewhere")]
    [InlineData("self-1")]
    [InlineData("self_1")]
    [InlineData("é1")]
    public void Anything_logind_would_not_accept_is_not_passed_to_loginctl(string? id)
    {
        // XDG_SESSION_ID comes from the environment. systemd accepts letters
        // and digits only (session_id_valid): an id starting with "-" would
        // reach loginctl's argv as an option, -H naming a host to connect to.
        Assert.False(LogindState.IsValidSessionId(id));
    }

    [Fact]
    public void An_id_of_up_to_64_characters_is_accepted_and_no_longer()
    {
        Assert.True(LogindState.IsValidSessionId(new string('7', 64)));
        Assert.False(LogindState.IsValidSessionId(new string('7', 65)));
    }

    // --- Description ----------------------------------------------------------------

    [Fact]
    public void The_description_of_an_ordinary_desktop_session_reads_plainly()
    {
        // What the log and --doctor print when dictation is allowed.
        Assert.Equal(
            "active, unlocked (wayland, seat0)",
            new LogindState(true, false, "wayland", "seat0", false).Describe());
    }

    [Fact]
    public void The_description_says_what_is_unknown_rather_than_guessing()
    {
        Assert.Equal(
            "activity unknown, lock state unreported (type unknown, no seat)",
            LogindState.Unknown.Describe());
    }

    [Fact]
    public void The_description_of_an_inactive_locked_remote_session_says_all_three()
    {
        Assert.Equal(
            "inactive, locked (tty, no seat, remote)",
            new LogindState(false, true, "tty", "", true).Describe());
    }

    // --- The answer of one loginctl call ---------------------------------------------

    [Fact]
    public void A_failed_loginctl_call_carries_no_output()
    {
        // A timeout or a non-zero exit: nothing it printed is to be read.
        Assert.False(LoginctlAnswer.Failed.Succeeded);
        Assert.Equal(string.Empty, LoginctlAnswer.Failed.Output);
    }
}
