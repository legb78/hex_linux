namespace HexLinux.Session;

/// <summary>
/// How long the daemon waits before asking logind about the session again.
///
/// <para><b>Why not a fixed short period.</b> Every question costs two to
/// four loginctl processes, each waking logind. Asked every two seconds for
/// the daemon's whole life, that came to some 86,000 processes a day on a
/// machine where nobody dictates (measured by QA: 43 of each loginctl call in
/// 70 s).</para>
///
/// <para><b>So: often only while it matters.</b> During a dictation, a
/// session that locks or goes to the background must stop it quickly; and
/// while the session is locked, not in front, or unanswered, a start is
/// refused from the last answer, so the return has to be seen within a
/// couple of seconds. Otherwise the question is rare. What the rare period
/// could miss is covered elsewhere: a press of the shortcut, or a start
/// command, asks at once — well within the confirmation delay, so a lock
/// that happened since is seen before any tone — and every insertion asks
/// afresh anyway.</para>
///
/// <para>Pure: the daemon passes what it knows.</para>
/// </summary>
public static class SessionPollPolicy
{
    /// <summary>While a dictation runs, or the session refuses one.</summary>
    public static readonly TimeSpan Watchful = TimeSpan.FromSeconds(2);

    /// <summary>At rest, in a session that allows dictating.</summary>
    public static readonly TimeSpan Relaxed = TimeSpan.FromSeconds(15);

    /// <param name="last">The last answer, or null before the first one.</param>
    /// <param name="dictationBusy">A press is armed, or a dictation records or is transcribed.</param>
    public static TimeSpan NextCheck(GuardDecision? last, bool dictationBusy) =>
        dictationBusy || last is not { Allowed: true } ? Watchful : Relaxed;
}
