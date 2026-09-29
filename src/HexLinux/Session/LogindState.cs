using System.Globalization;

namespace HexLinux.Session;

/// <summary>
/// What systemd-logind says of one session: whether it is the one in front of
/// the screen, whether that screen is locked, and what kind of session it is.
///
/// <para><b>Why it matters more on Linux than on Windows.</b> The keyboard is
/// read from <c>/dev/input</c>, below the desktop. The kernel reports keys
/// there whatever the desktop is doing: on the lock screen, and while another
/// user's session is in front after a user switch. Windows stops delivering
/// keys to a hook in both cases; Linux does not. Without this check, the
/// shortcut pressed on a locked screen would open the microphone and dictate
/// into the password field, and the shortcut pressed by the next user would
/// dictate into their session.</para>
/// </summary>
/// <param name="Active">True when this session is the one in the foreground of its seat.</param>
/// <param name="Locked">True when its screen locker is up. Only lockers that report it set it (GNOME, KDE).</param>
/// <param name="Type">"x11", "wayland", "tty"…</param>
/// <param name="Seat">"seat0", or empty for a session attached to no seat.</param>
/// <param name="Remote">True for a session opened from the network.</param>
/// <param name="User">The uid the session belongs to, when reported.</param>
public readonly record struct LogindState(bool? Active, bool? Locked, string Type = "", string Seat = "", bool? Remote = null, uint? User = null)
{
    public static readonly LogindState Unknown = new(null, null);

    /// <summary>
    /// Whether dictated text may be inserted now: only a positive "inactive"
    /// or "locked" refuses.
    /// </summary>
    public bool AllowsInsertion => Active != false && Locked != true;

    /// <summary>
    /// A session that can be trusted to be the one in front of the screen:
    /// graphical, at a seat, local. logind reports every session attached to
    /// no seat — an SSH login among them — as always active, so such a session
    /// says nothing about the screen.
    /// </summary>
    public bool IsLocalGraphical =>
        (Type.Equals("x11", StringComparison.Ordinal) || Type.Equals("wayland", StringComparison.Ordinal))
        && Seat.Length > 0
        && Remote != true;

    /// <summary>
    /// Reads the output of <c>loginctl show-session ID --property=…</c>: one
    /// <c>Key=value</c> per line, booleans as <c>yes</c> or <c>no</c>.
    /// </summary>
    public static LogindState Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        bool? active = null;
        bool? locked = null;
        bool? remote = null;
        uint? user = null;
        string type = string.Empty;
        string seat = string.Empty;

        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int equals = line.IndexOf('=', StringComparison.Ordinal);

            if (equals <= 0)
            {
                continue;
            }

            string key = line[..equals];
            string value = line[(equals + 1)..].Trim();

            switch (key)
            {
                case "Active":
                    active = ReadBoolean(value);
                    break;
                case "LockedHint":
                    locked = ReadBoolean(value);
                    break;
                case "Remote":
                    remote = ReadBoolean(value);
                    break;
                case "Type":
                    type = value;
                    break;
                case "Seat":
                    seat = value;
                    break;
                case "User":
                    user = uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint uid) ? uid : null;
                    break;
                default:
                    break;
            }
        }

        return new LogindState(active, locked, type, seat, remote, user);
    }

    /// <summary>
    /// Whether <paramref name="id"/> can be a logind session id: ASCII letters
    /// and digits only, the rule logind itself applies (<c>session_id_valid</c>
    /// in systemd). Anything else is not passed to loginctl at all — an id
    /// starting with "-" would be read there as an option.
    /// </summary>
    public static bool IsValidSessionId(string? id) =>
        !string.IsNullOrEmpty(id)
        && id.Length <= 64
        && id.All(char.IsAsciiLetterOrDigit);

    /// <summary>For the log and the diagnostics.</summary>
    public string Describe()
    {
        string activity = Active switch { true => "active", false => "inactive", _ => "activity unknown" };
        string lockState = Locked switch { true => "locked", false => "unlocked", _ => "lock state unreported" };
        string kind = Type.Length > 0 ? Type : "type unknown";

        return $"{activity}, {lockState} ({kind}{(Seat.Length > 0 ? ", " + Seat : ", no seat")}{(Remote == true ? ", remote" : "")})";
    }

    private static bool? ReadBoolean(string value) => value switch
    {
        "yes" => true,
        "no" => false,
        _ => null,
    };
}

/// <summary>One answer from loginctl: whether it ran and replied, and what it printed.</summary>
public readonly record struct LoginctlAnswer(bool Succeeded, string Output)
{
    public static LoginctlAnswer Failed { get; } = new(false, string.Empty);
}

/// <summary>What the session guard concluded, and why.</summary>
/// <param name="Allowed">Whether the dictation may go on.</param>
/// <param name="Reason">A plain sentence for the log, the doctor and the notification.</param>
/// <param name="LogindKnown">False when there is no logind to ask at all.</param>
/// <param name="Inactive">
/// True when the session is positively not the one in front of the screen —
/// another user's session is — as opposed to merely locked or unanswered: the
/// case where the keyboards are closed until it comes back.
/// </param>
/// <param name="Locked">True when logind positively reports the screen locked.</param>
public readonly record struct GuardDecision(bool Allowed, string Reason, bool LogindKnown, bool Inactive = false, bool Locked = false)
{
    /// <summary>
    /// Whether a dictation may start — the microphone open. Stricter
    /// answers wait for the insertion: only a positive "locked" or
    /// "inactive" refuses a start, an unanswered question does not.
    /// </summary>
    public bool AllowsStart => !Inactive && !Locked;
}

/// <summary>
/// Decides whether the session allows a dictation, from what loginctl says.
///
/// <para><b>Fail closed, except where there is nothing to protect.</b> With
/// no logind at all — WSL without systemd, a container, a distribution
/// without it — there is no seat and no lock to respect: the dictation goes
/// on, and the fact is logged once. But when logind is there and does not
/// answer, or answers something unreadable, the one case the guard exists for
/// may be happening: refusing then costs a dictation, allowing could type into
/// a lock screen.</para>
///
/// <para>Which session to ask: <c>XDG_SESSION_ID</c> when it names a local
/// graphical session at a seat; otherwise — a daemon started from SSH or from
/// a service, whose own session is seatless and so always "active" — the
/// graphical session logind attributes to the user.</para>
///
/// <para><b>And when even that one sits at no seat</b> — the user has no
/// graphical session, only an SSH login or a terminal — it says nothing of the
/// screen either: logind elects any started session as the display, and a
/// seatless one is active for ever. The main seat is asked who is in front of
/// it. Nobody (WSL, a machine with no screen): nothing to protect, the
/// dictation goes on. One of this user's sessions: that one decides. Anybody
/// else's: this user is not in front — refused, and the keyboards
/// closed.</para>
///
/// <para>Pure: loginctl is reached through the functions passed in.</para>
/// </summary>
public static class SessionGuardPolicy
{
    /// <param name="logindPresent">logind runs on this machine.</param>
    /// <param name="loginctlFound">loginctl was found to ask it.</param>
    /// <param name="uid">The user the daemon runs as.</param>
    /// <param name="xdgSessionId">The session named by the environment, if any.</param>
    /// <param name="showSession"><c>loginctl show-session ID</c>, with the properties of <c>ToolCommands.ShowSession</c>.</param>
    /// <param name="showUserDisplay"><c>loginctl show-user UID --property=Display --value</c>.</param>
    /// <param name="showSeatActiveSession"><c>loginctl show-seat seat0 --property=ActiveSession --value</c>.</param>
    public static GuardDecision Decide(
        bool logindPresent,
        bool loginctlFound,
        uint uid,
        string? xdgSessionId,
        Func<string, LoginctlAnswer> showSession,
        Func<LoginctlAnswer> showUserDisplay,
        Func<LoginctlAnswer> showSeatActiveSession)
    {
        ArgumentNullException.ThrowIfNull(showSession);
        ArgumentNullException.ThrowIfNull(showUserDisplay);
        ArgumentNullException.ThrowIfNull(showSeatActiveSession);

        if (!logindPresent)
        {
            return new GuardDecision(true, "no logind on this system: there is no session to protect", false);
        }

        if (!loginctlFound)
        {
            // logind runs, so there is a seat and possibly a lock to respect:
            // not being able to ask is not the same as having nothing to ask.
            return new GuardDecision(false, "logind runs but loginctl cannot be found: refused to be safe", true);
        }

        string? id = xdgSessionId?.Trim();

        if (LogindState.IsValidSessionId(id))
        {
            LoginctlAnswer own = showSession(id!);

            if (!own.Succeeded)
            {
                return Unanswered($"session {id}");
            }

            LogindState state = LogindState.Parse(own.Output);

            if (state.IsLocalGraphical)
            {
                return FromState(id!, state);
            }
        }

        LoginctlAnswer display = showUserDisplay();

        if (!display.Succeeded)
        {
            return Unanswered("the user's display session");
        }

        string displayId = display.Output.Trim();

        if (!LogindState.IsValidSessionId(displayId))
        {
            return new GuardDecision(false, "logind knows no display session for this user: refused to be safe", true);
        }

        LoginctlAnswer answer = showSession(displayId);

        if (!answer.Succeeded)
        {
            return Unanswered($"session {displayId}");
        }

        LogindState displayState = LogindState.Parse(answer.Output);

        return displayState.IsLocalGraphical
            ? FromState(displayId, displayState)
            : FromSeat(uid, displayId, displayState, showSession, showSeatActiveSession);
    }

    /// <summary>
    /// The user's display session sits at no seat: who sits at the main one
    /// decides.
    /// </summary>
    private static GuardDecision FromSeat(
        uint uid,
        string displayId,
        LogindState display,
        Func<string, LoginctlAnswer> showSession,
        Func<LoginctlAnswer> showSeatActiveSession)
    {
        LoginctlAnswer seat = showSeatActiveSession();

        if (!seat.Succeeded)
        {
            return Unanswered("who is in front of the screen");
        }

        string activeId = seat.Output.Trim();

        if (activeId.Length == 0 || activeId == displayId)
        {
            // Nobody at the seat — WSL, a machine with no screen — or the
            // user's own session: the display session's state stands.
            return FromState(displayId, display);
        }

        if (!LogindState.IsValidSessionId(activeId))
        {
            return new GuardDecision(false, "logind's answer about the seat could not be read: refused to be safe", true);
        }

        LoginctlAnswer active = showSession(activeId);

        if (!active.Succeeded)
        {
            return Unanswered($"session {activeId}");
        }

        LogindState inFront = LogindState.Parse(active.Output);

        if (inFront.User is null)
        {
            return new GuardDecision(false, $"logind's answer for session {activeId} could not be read: refused to be safe", true);
        }

        if (inFront.User == uid)
        {
            return FromState(activeId, inFront);
        }

        return new GuardDecision(false, $"another user's session ({activeId}) is in front of the screen", true, Inactive: true);
    }

    private static GuardDecision FromState(string id, LogindState state)
    {
        if (state.Active is null)
        {
            return new GuardDecision(false, $"logind's answer for session {id} could not be read: refused to be safe", true);
        }

        if (state.Locked == true)
        {
            // Locked and, after a switch of user, not in front either: both
            // are said, since only the second closes the keyboards.
            return new GuardDecision(false, $"session {id} is locked", true, Inactive: state.Active == false, Locked: true);
        }

        if (state.Active == false)
        {
            return new GuardDecision(false, $"session {id} is not the one in front of the screen", true, Inactive: true);
        }

        return new GuardDecision(true, $"session {id}: {state.Describe()}", true);
    }

    private static GuardDecision Unanswered(string what) =>
        new(false, $"logind did not answer about {what}: refused to be safe", true);
}
