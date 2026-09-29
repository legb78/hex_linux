using HexLinux.Session;
using Xunit;

namespace HexLinux.Tests.Session;

/// <summary>
/// How often loginctl runs. Too often, and an idle daemon spawns tens of
/// thousands of processes a day (QA-11); too rarely at the wrong moment, and a
/// screen that locks mid-dictation, or a session that comes back, is seen late.
/// </summary>
public class SessionPollPolicyTests
{
    private static readonly GuardDecision Allowed = new(true, "session 2: active, unlocked (wayland, seat0)", true);
    private static readonly GuardDecision Locked = new(false, "session 2 is locked", true, Locked: true);
    private static readonly GuardDecision Inactive = new(false, "session 2 is not the one in front of the screen", true, Inactive: true);
    private static readonly GuardDecision Unanswered = new(false, "logind did not answer about session 2: refused to be safe", true);

    [Fact]
    public void An_idle_daemon_in_an_open_session_asks_rarely()
    {
        // The daemon's normal state for most of the day: nobody dictates.
        Assert.Equal(SessionPollPolicy.Relaxed, SessionPollPolicy.NextCheck(Allowed, dictationBusy: false));
    }

    [Fact]
    public void A_dictation_under_way_is_watched_closely()
    {
        // A screen locked in the middle of a dictation must stop the
        // microphone within a couple of seconds, not a quarter of a minute.
        Assert.Equal(SessionPollPolicy.Watchful, SessionPollPolicy.NextCheck(Allowed, dictationBusy: true));
    }

    [Fact]
    public void A_refusing_session_is_watched_closely_so_its_return_is_seen()
    {
        // Starts are refused from the last answer: after an unlock, the
        // shortcut must work again within a couple of seconds.
        Assert.Equal(SessionPollPolicy.Watchful, SessionPollPolicy.NextCheck(Locked, dictationBusy: false));
        Assert.Equal(SessionPollPolicy.Watchful, SessionPollPolicy.NextCheck(Inactive, dictationBusy: false));
        Assert.Equal(SessionPollPolicy.Watchful, SessionPollPolicy.NextCheck(Unanswered, dictationBusy: false));
    }

    [Fact]
    public void Before_the_first_answer_the_question_comes_soon()
    {
        Assert.Equal(SessionPollPolicy.Watchful, SessionPollPolicy.NextCheck(null, dictationBusy: false));
    }

    [Fact]
    public void Without_logind_there_is_nothing_to_watch_closely()
    {
        // WSL without systemd, a container: the answer never changes.
        var noLogind = new GuardDecision(true, "no logind on this system: there is no session to protect", false);

        Assert.Equal(SessionPollPolicy.Relaxed, SessionPollPolicy.NextCheck(noLogind, dictationBusy: false));
    }

    [Fact]
    public void The_rare_period_stays_well_above_the_close_one()
    {
        // The point of the policy: a real reduction of the processes spawned.
        Assert.True(SessionPollPolicy.Relaxed >= 5 * SessionPollPolicy.Watchful);
    }
}
