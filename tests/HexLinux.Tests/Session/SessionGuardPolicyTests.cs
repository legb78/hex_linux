using HexLinux.Session;
using Xunit;

namespace HexLinux.Tests.Session;

/// <summary>
/// The session guard's decision, the one barrier between a shortcut pressed on
/// the lock screen — or by the next user after a switch — and a dictation typed
/// into their screen. Evdev delivers keys in both cases; Windows did not.
///
/// <para>Fail closed where it matters: no logind at all lets the dictation go
/// on (WSL without systemd, containers), but a logind that does not answer, or
/// answers something unreadable, refuses. The loginctl outputs are the real
/// ones of systemd 255; an unknown session makes loginctl exit 1, which the
/// fake answers as a failure too.</para>
/// </summary>
public class SessionGuardPolicyTests
{
    private const string Desktop = "Seat=seat0\nRemote=no\nType=wayland\nActive=yes\nLockedHint=no\n";
    private const string DesktopLocked = "Seat=seat0\nRemote=no\nType=wayland\nActive=yes\nLockedHint=yes\n";
    private const string DesktopBehind = "Seat=seat0\nRemote=no\nType=x11\nActive=no\nLockedHint=no\n";
    private const string DesktopLockedBehind = "Seat=seat0\nRemote=no\nType=wayland\nActive=no\nLockedHint=yes\n";
    private const string Ssh = "Seat=\nRemote=yes\nType=tty\nActive=yes\nLockedHint=no\n";
    private const string WslTerminal = "Seat=\nRemote=no\nType=tty\nActive=yes\nLockedHint=no\n";

    [Fact]
    public void Without_logind_the_dictation_goes_on_and_loginctl_is_never_asked()
    {
        // WSL without systemd, a container: no seat, no lock to respect.
        GuardDecision decision = SessionGuardPolicy.Decide(
            logindPresent: false,
            "2",
            _ => throw new InvalidOperationException("loginctl must not run"),
            () => throw new InvalidOperationException("loginctl must not run"));

        Assert.True(decision.Allowed);
        Assert.True(decision.AllowsStart);
        Assert.False(decision.LogindKnown);
        Assert.Contains("no logind", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_own_desktop_session_in_front_and_unlocked_allows_the_dictation()
    {
        var logind = new FakeLogind().Session("2", Desktop);

        GuardDecision decision = logind.Decide("2");

        Assert.True(decision.Allowed);
        Assert.True(decision.LogindKnown);
        Assert.Equal("session 2: active, unlocked (wayland, seat0)", decision.Reason);
        Assert.Equal(0, logind.DisplayQuestions);
    }

    [Fact]
    public void A_locked_screen_refuses_the_start_and_the_insertion()
    {
        // The shortcut pressed on the lock screen: dictating would type into
        // the password field.
        GuardDecision decision = new FakeLogind().Session("2", DesktopLocked).Decide("2");

        Assert.False(decision.Allowed);
        Assert.False(decision.AllowsStart);
        Assert.True(decision.Locked);
        Assert.False(decision.Inactive);
        Assert.Equal("session 2 is locked", decision.Reason);
    }

    [Fact]
    public void Another_users_session_in_front_refuses_and_says_it_is_not_in_front()
    {
        // The daemon closes the keyboards on Inactive: the next user's
        // keystrokes are none of this session's business.
        GuardDecision decision = new FakeLogind().Session("2", DesktopBehind).Decide("2");

        Assert.False(decision.Allowed);
        Assert.False(decision.AllowsStart);
        Assert.True(decision.Inactive);
        Assert.False(decision.Locked);
    }

    [Fact]
    public void A_session_locked_then_left_behind_another_user_is_reported_as_both()
    {
        // Switching user from a locked screen leaves the first session locked
        // AND inactive. Reporting only "locked" kept the keyboards open while
        // the other user typed.
        GuardDecision decision = new FakeLogind().Session("2", DesktopLockedBehind).Decide("2");

        Assert.False(decision.Allowed);
        Assert.True(decision.Locked);
        Assert.True(decision.Inactive);
    }

    [Fact]
    public void An_unanswered_question_refuses_the_insertion_but_not_the_start()
    {
        // loginctl timed out: the microphone may open — the check is repeated
        // before inserting — but nothing is typed on a guess.
        var logind = new FakeLogind();

        GuardDecision decision = logind.Decide("2");

        Assert.False(decision.Allowed);
        Assert.True(decision.AllowsStart);
        Assert.True(decision.LogindKnown);
        Assert.Equal("logind did not answer about session 2: refused to be safe", decision.Reason);
        Assert.Equal(0, logind.DisplayQuestions);
    }

    [Fact]
    public void A_daemon_started_from_ssh_follows_the_users_desktop_session()
    {
        // The SSH session is seatless, hence always "active": it says nothing
        // of the screen, where the desktop session is locked.
        var logind = new FakeLogind().Session("5", Ssh).Session("2", DesktopLocked).DisplayIs("2\n");

        GuardDecision decision = logind.Decide("5");

        Assert.False(decision.Allowed);
        Assert.True(decision.Locked);
        Assert.Equal(["5", "2"], logind.Asked);
    }

    [Fact]
    public void Without_XDG_SESSION_ID_the_users_display_session_is_asked()
    {
        // Desktops that start autostart entries through systemd's generator
        // leave XDG_SESSION_ID out of the environment.
        var logind = new FakeLogind().Session("2", Desktop).DisplayIs("2\n");

        GuardDecision decision = logind.Decide(null);

        Assert.True(decision.Allowed);
        Assert.Equal(["2"], logind.Asked);
        Assert.Equal(1, logind.DisplayQuestions);
    }

    [Theory]
    [InlineData("-Hhost")]
    [InlineData("2 --property=Name")]
    [InlineData("")]
    [InlineData("   ")]
    public void An_XDG_SESSION_ID_that_is_no_session_id_is_never_passed_to_loginctl(string xdgSessionId)
    {
        // The variable comes from the environment; loginctl gets it in argv.
        var logind = new FakeLogind().Session("2", Desktop).DisplayIs("2\n");

        GuardDecision decision = logind.Decide(xdgSessionId);

        Assert.True(decision.Allowed);
        Assert.Equal(["2"], logind.Asked);
    }

    [Fact]
    public void An_XDG_SESSION_ID_with_spaces_around_it_is_still_used()
    {
        var logind = new FakeLogind().Session("2", Desktop);

        Assert.True(logind.Decide(" 2 ").Allowed);
        Assert.Equal(["2"], logind.Asked);
    }

    [Fact]
    public void The_terminal_session_of_wsl_is_its_own_display_session()
    {
        // What WSL with systemd really answers: session 1 is a seatless tty,
        // and it is also the one logind names as the user's display.
        var logind = new FakeLogind().Session("1", WslTerminal).DisplayIs("1\n");

        GuardDecision decision = logind.Decide("1");

        Assert.True(decision.Allowed);
        Assert.Equal("session 1: active, unlocked (tty, no seat)", decision.Reason);
        Assert.Equal(["1", "1"], logind.Asked);
    }

    [Fact]
    public void When_logind_does_not_say_which_session_is_the_users_display_the_dictation_is_refused()
    {
        GuardDecision decision = new FakeLogind().Decide(null);

        Assert.False(decision.Allowed);
        Assert.True(decision.AllowsStart);
        Assert.Equal("logind did not answer about the user's display session: refused to be safe", decision.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    public void A_user_with_no_display_session_is_refused(string display)
    {
        // A daemon started by a user service before anyone logged in: there
        // is no screen whose state could be checked.
        GuardDecision decision = new FakeLogind().DisplayIs(display).Decide(null);

        Assert.False(decision.Allowed);
        Assert.True(decision.LogindKnown);
        Assert.Equal("logind knows no display session for this user: refused to be safe", decision.Reason);
    }

    [Fact]
    public void The_display_session_not_answering_is_refused()
    {
        GuardDecision decision = new FakeLogind().DisplayIs("3").Decide(null);

        Assert.False(decision.Allowed);
        Assert.Equal("logind did not answer about session 3: refused to be safe", decision.Reason);
    }

    [Fact]
    public void An_answer_without_the_activity_is_refused()
    {
        // Verified: loginctl prints nothing and exits 0 for properties it does
        // not know. An empty answer is no evidence that the screen is free.
        GuardDecision decision = new FakeLogind().Session("3", string.Empty).DisplayIs("3").Decide(null);

        Assert.False(decision.Allowed);
        Assert.True(decision.AllowsStart);
        Assert.Equal("logind's answer for session 3 could not be read: refused to be safe", decision.Reason);
    }

    [Fact]
    public void Deciding_without_a_way_to_ask_is_a_programming_error()
    {
        Assert.Throws<ArgumentNullException>(() => SessionGuardPolicy.Decide(true, "2", null!, () => LoginctlAnswer.Failed));
        Assert.Throws<ArgumentNullException>(() => SessionGuardPolicy.Decide(true, "2", _ => LoginctlAnswer.Failed, null!));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void Only_a_positive_locked_or_inactive_refuses_a_start(bool inactive, bool locked, bool allowsStart)
    {
        Assert.Equal(allowsStart, new GuardDecision(false, "reason", true, inactive, locked).AllowsStart);
    }

    /// <summary>loginctl as the policy sees it: one answer per session id, and the user's display.</summary>
    private sealed class FakeLogind
    {
        private readonly Dictionary<string, LoginctlAnswer> _sessions = new(StringComparer.Ordinal);
        private LoginctlAnswer _display = LoginctlAnswer.Failed;

        /// <summary>The session ids passed to show-session, in order.</summary>
        public List<string> Asked { get; } = [];

        public int DisplayQuestions { get; private set; }

        public FakeLogind Session(string id, string output)
        {
            _sessions[id] = new LoginctlAnswer(true, output);
            return this;
        }

        public FakeLogind DisplayIs(string output)
        {
            _display = new LoginctlAnswer(true, output);
            return this;
        }

        public GuardDecision Decide(string? xdgSessionId) =>
            SessionGuardPolicy.Decide(true, xdgSessionId, ShowSession, ShowUserDisplay);

        private LoginctlAnswer ShowSession(string id)
        {
            Asked.Add(id);
            return _sessions.TryGetValue(id, out LoginctlAnswer answer) ? answer : LoginctlAnswer.Failed;
        }

        private LoginctlAnswer ShowUserDisplay()
        {
            DisplayQuestions++;
            return _display;
        }
    }
}
